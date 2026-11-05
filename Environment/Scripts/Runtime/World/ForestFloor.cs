using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using _Game.Scripts.Enums;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif
using GeoWorld;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Лесная подстилка (ForestFloor.hlsl в шейдере террейна). Снимок тайла над лесом — это кроны, и земля под деревьями
    /// была их цвета: летом зелёной, осенью оливковой (SeasonGround желтит зелень), в тени — синевато-серой. Здесь по
    /// деревьям загруженных тайлов строится карта вокруг камеры (_ForestFloorMap, windowSize м с шагом texel м): R —
    /// подстилка (кроны с разлётом листвы за них), G — доля хвои в ней, B — сомкнутость крон (тень, меньше неба), A — доля
    /// листвы от деревьев, голых осенью (BareInAutumn: там свежий лист гуще и пестрее). Крона —
    /// по ящику LOD0 прототипа (у хвойных листва с _Evergreen) и ширине экземпляра. По карте шейдер кладёт свою почву по
    /// поре года (SeasonLook: слои набора фактуры земли и их тон): осенью свежий медно-бурый и пёстрый лист, летом тёмная
    /// прошлогодняя подстилка с пятнами мха, весной выцветшая с молодой зеленью, зимой мокрая бурая; под соснами — рыжая
    /// хвоя. Карта считается в фоновом потоке и подменяется целиком, когда камера отходит от её середины на
    /// recentreDistance или меняются деревья (тайл загрузился, выгрузился, виды расставлены — TileTreeSpecies.Ready).
    /// Деревья-объекты (точка взлёта ставит свои деревья префабами) сообщают о себе через SetObjectTrees. Один на сцену
    /// (добавляет WeatherManager).
    /// </summary>
    public sealed class ForestFloor : MonoBehaviour
    {
        private const string DetailSetResource = "Terrain/GroundDetailSet";
        private const float CheckInterval = 0.5f;

        private static readonly int MapId = Shader.PropertyToID("_ForestFloorMap");
        private static readonly int RectId = Shader.PropertyToID("_ForestFloorRect");
        private static readonly int LayersId = Shader.PropertyToID("_ForestFloorLayers");
        private static readonly int ToneId = Shader.PropertyToID("_ForestFloorTone");
        private static readonly int MixId = Shader.PropertyToID("_ForestFloorMix");
        private static readonly int FreshId = Shader.PropertyToID("_ForestFloorFresh");

        // Вид подстилки в одну пору: четыре слоя набора фактуры земли (GroundDetailSet, по имени) — основной, пятна, мох,
        // хвоя; тон слоя — его средний цвет (recolour: рисунок текстуры на этот цвет) или множитель её цвета; доли пятен и
        // мха; где по карте (R) подстилка начинается и где сплошная.
        [Serializable]
        public sealed class SeasonLook
        {
            public string baseLayer;
            public Color baseTone = Color.white;
            public bool baseRecolour;
            public string patchLayer;
            public Color patchTone = Color.white;
            public bool patchRecolour;
            [Range(0f, 1f)] public float patchShare = 0.3f;
            public string mossLayer;
            public Color mossTone = Color.white;
            [Range(0f, 1f)] public float mossShare = 0.2f;
            public string needleLayer = "pine_floor";
            public Color needleTone = Color.white;
            [Tooltip("Карта R: с какой подстилка начинается и с какой она сплошная (у одинокого дерева в середине ~0.7, в лесу ~0.95).")]
            public Vector2 amount = new(0.2f, 0.7f);
            [Tooltip("Под голыми деревьями (карта A): доля пятен больше на столько, подстилка начинается раньше на столько.")]
            [Range(0f, 1f)] public float freshPatch;
            [Range(0f, 0.5f)] public float freshAmount;
        }

        [Tooltip("Сторона окна карты вокруг камеры, м.")]
        [SerializeField] private float windowSize = 2048f;
        [Tooltip("Шаг карты, м.")]
        [SerializeField] private float texel = 2f;
        [Tooltip("На сколько камера отходит от середины окна, прежде чем карту пересчитать, м.")]
        [SerializeField] private float recentreDistance = 256f;
        [Tooltip("Крона от ящика LOD0 (он по кончикам веток — шире настоящей кроны).")]
        [SerializeField] private float crownScale = 0.85f;
        [Tooltip("Листва ложится дальше кроны: радиус × это + litterReach.")]
        [SerializeField] private float litterSpread = 1.5f;
        [SerializeField] private float litterReach = 1f;

        // Тона — средний цвет слоя (линейный), подобраны в игре против земли со снимка вокруг: родные цвета фото-текстур
        // (медный лист — 0.17, 0.08, 0.035) вдвое ярче и насыщеннее её, и подстилка читалась красной глиной.
        // Осень: свежий опавший лист — плотный буро-медный ковёр (бук, дуб) с охристыми пятнами пёстрого (клён, тополь,
        // липа); мох почти скрыт. Подстилка и вокруг одиноких деревьев.
        [SerializeField] private SeasonLook autumn = new()
        {
            baseLayer = "leaves_autumn", baseTone = new Color(0.085f, 0.056f, 0.03f), baseRecolour = true,
            patchLayer = "leaves_mixed", patchTone = new Color(0.115f, 0.088f, 0.032f), patchRecolour = true, patchShare = 0.4f,
            mossLayer = "forest_moss", mossTone = new Color(0.07f, 0.075f, 0.03f), mossShare = 0.06f,
            needleTone = new Color(0.1f, 0.06f, 0.032f), amount = new Vector2(0.2f, 0.7f),
            freshPatch = 0.45f, freshAmount = 0.15f
        };

        // Лето: прошлогодняя подстилка — тёмная, бурая, полуперегнившая, пятнами темнее; мох и травы в просветах крон. Только
        // в сомкнутом лесу: под одиноким деревом летом трава.
        [SerializeField] private SeasonLook summer = new()
        {
            baseLayer = "forest_litter", baseTone = new Color(0.08f, 0.062f, 0.04f), baseRecolour = true,
            patchLayer = "leaves_autumn", patchTone = new Color(0.062f, 0.044f, 0.03f), patchRecolour = true, patchShare = 0.3f,
            mossLayer = "forest_moss", mossTone = new Color(0.058f, 0.082f, 0.026f), mossShare = 0.35f,
            needleTone = new Color(0.1f, 0.066f, 0.038f), amount = new Vector2(0.72f, 0.95f)
        };

        // Весна: прошлогодний лист выцвел до серо-бурого, между ним — молодая зелень (сныть, ветреница) пятнами.
        [SerializeField] private SeasonLook spring = new()
        {
            baseLayer = "forest_litter", baseTone = new Color(0.11f, 0.088f, 0.058f), baseRecolour = true,
            patchLayer = "leaves_autumn", patchTone = new Color(0.085f, 0.062f, 0.04f), patchRecolour = true, patchShare = 0.3f,
            mossLayer = "forest_moss", mossTone = new Color(0.07f, 0.1f, 0.028f), mossShare = 0.38f,
            needleTone = new Color(0.11f, 0.072f, 0.04f), amount = new Vector2(0.6f, 0.92f)
        };

        // Зима (где снег не лёг): мокрый тёмно-бурый лист.
        [SerializeField] private SeasonLook winter = new()
        {
            baseLayer = "leaves_autumn", baseTone = new Color(0.065f, 0.045f, 0.028f), baseRecolour = true,
            patchLayer = "forest_litter", patchTone = new Color(0.058f, 0.042f, 0.027f), patchRecolour = true, patchShare = 0.3f,
            mossLayer = "forest_moss", mossTone = new Color(0.045f, 0.055f, 0.028f), mossShare = 0.05f,
            needleTone = new Color(0.08f, 0.055f, 0.034f), amount = new Vector2(0.3f, 0.8f)
        };

        private sealed class Crowns
        {
            public TerrainData Data;
            public int Count;
            public bool Ready;
            public Rect Bounds;
            // x, z — мир, z — радиус кроны, w — 0 лиственное, 1 хвойное, 2 лиственное, голое осенью
            public Vector4[] Trees;
        }

        // деревья-объекты по владельцу: x, z — мир, z — радиус кроны (ещё без crownScale), w — 1 у хвойного
        private static readonly Dictionary<object, Vector4[]> ObjectTrees = new();
        private static readonly Dictionary<GameObject, Vector2> Prototypes = new();
        private static int _objectTreesVersion;

        private readonly Dictionary<Terrain, Crowns> _crowns = new();
        private readonly List<Terrain> _gone = new();
        private readonly List<Vector4> _gather = new();
        private GroundDetailSet _set;
        private Texture2D _map;
        private Task<byte[]> _job;
        private Vector2 _jobOrigin;
        private Vector2 _origin;
        private bool _has;
        private int _signature;
        private int _jobSignature;
        private float _nextCheck;
        private int _resolution;
        private int _treesInMap;
        private bool _visible = true;

#if ODIN_INSPECTOR
        [ShowInInspector, LabelText("Лесная подстилка")]
#endif
        public bool Visible
        {
            get => _visible;
            set
            {
                _visible = value;
                PublishRect();
            }
        }

        /// <summary>Деревья-объекты владельца (null или пусто — убрать): x, z — корень в мире, z — радиус кроны, w — 1 у хвойного.</summary>
        public static void SetObjectTrees(object owner, Vector4[] trees)
        {
            if (trees == null || trees.Length == 0)
                ObjectTrees.Remove(owner);
            else
                ObjectTrees[owner] = trees;

            _objectTreesVersion++;
        }

        public string Stats => $"window={(_has ? _origin.ToString("F0") : "none")} trees={_treesInMap} terrains={_crowns.Count} building={_job != null}";

        private void OnEnable()
        {
            Shader.SetGlobalVector(RectId, Vector4.zero);
            _set = Resources.Load<GroundDetailSet>(DetailSetResource);
            _resolution = Mathf.Max(16, Mathf.RoundToInt(windowSize / Mathf.Max(texel, 0.25f)));
            _map = new Texture2D(_resolution, _resolution, TextureFormat.RGBA32, false, true)
            {
                name = "Forest floor",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            Shader.SetGlobalTexture(MapId, _map);
            Seasons.Changed += PublishLook;
            PublishLook();
        }

        private void OnDisable()
        {
            Seasons.Changed -= PublishLook;
            Shader.SetGlobalVector(RectId, Vector4.zero);

            if (_map != null)
                Destroy(_map);

            _map = null;
            _job = null;
            _has = false;
            _crowns.Clear();
        }

        private void Update()
        {
            if (_map == null)
                return;

            if (_job != null && _job.IsCompleted)
                Finish();

            if (_job != null || Time.unscaledTime < _nextCheck)
                return;

            _nextCheck = Time.unscaledTime + CheckInterval;
            Camera camera = GeoWorldCamera.Current;

            if (camera == null)
                return;

            Vector3 eye = camera.transform.position;
            Vector2 centre = _origin + Vector2.one * (windowSize * 0.5f);
            bool recentre = !_has || Mathf.Abs(eye.x - centre.x) > recentreDistance || Mathf.Abs(eye.z - centre.y) > recentreDistance;
            Vector2 origin = recentre
                ? new Vector2(Mathf.Floor((eye.x - windowSize * 0.5f) / texel) * texel, Mathf.Floor((eye.z - windowSize * 0.5f) / texel) * texel)
                : _origin;
            int signature = Refresh(new Rect(origin, Vector2.one * windowSize));

            if (recentre || signature != _signature)
                Begin(origin, signature);
        }

        // Кроны террейнов окна (новые и изменившиеся — читаются заново); подпись — что в окне сейчас.
        private int Refresh(Rect window)
        {
            int signature = 17 + _objectTreesVersion * 7919;
            _gone.Clear();

            foreach (Terrain terrain in _crowns.Keys)
            {
                if (terrain == null || !terrain.isActiveAndEnabled)
                    _gone.Add(terrain);
            }

            foreach (Terrain terrain in _gone)
                _crowns.Remove(terrain);

            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                TerrainData data = terrain != null ? terrain.terrainData : null;

                if (data == null)
                    continue;

                Vector3 position = terrain.transform.position;
                Rect bounds = new(position.x, position.z, data.size.x, data.size.z);

                if (!bounds.Overlaps(window))
                    continue;

                bool ready = TileTreeSpecies.Ready(terrain);

                if (!_crowns.TryGetValue(terrain, out Crowns crowns) || crowns.Data != data || crowns.Count != data.treeInstanceCount ||
                    crowns.Ready != ready || crowns.Bounds != bounds)
                    _crowns[terrain] = crowns = Read(terrain, data, bounds, ready);

                unchecked
                {
                    signature = signature * 31 + terrain.GetInstanceID();
                    signature = signature * 31 + data.GetInstanceID();
                    signature = signature * 31 + crowns.Count;
                    signature = signature * 31 + (ready ? 1 : 0);
                }
            }

            return signature;
        }

        // Кроны террейна: пока виды не расставлены — без деревьев (прототипы ещё пакета, их перепишут).
        private Crowns Read(Terrain terrain, TerrainData data, Rect bounds, bool ready)
        {
            var crowns = new Crowns { Data = data, Count = data.treeInstanceCount, Ready = ready, Bounds = bounds, Trees = Array.Empty<Vector4>() };

            if (!ready || crowns.Count == 0)
                return crowns;

            TreePrototype[] prototypes = data.treePrototypes;
            Vector2[] sizes = new Vector2[prototypes.Length];

            for (int i = 0; i < prototypes.Length; i++)
                sizes[i] = Crown(prototypes[i].prefab);

            TreeInstance[] instances = data.treeInstances;
            Vector3 size = data.size;
            Vector4[] trees = new Vector4[instances.Length];

            for (int i = 0; i < instances.Length; i++)
            {
                TreeInstance tree = instances[i];
                Vector2 prototype = tree.prototypeIndex >= 0 && tree.prototypeIndex < sizes.Length ? sizes[tree.prototypeIndex] : new Vector2(3f, 0f);
                float x = bounds.x + tree.position.x * size.x;
                float z = bounds.y + tree.position.z * size.z;
                trees[i] = new Vector4(x, z, prototype.x * Mathf.Max(tree.widthScale, 0.1f) * crownScale, Kind(x, z, prototype.y > 0.5f));
            }

            crowns.Trees = trees;
            return crowns;
        }

        /// <summary>Радиус кроны префаба дерева (по ящику LOD0 вокруг его корня) и 1, если оно хвойное (листва с _Evergreen).</summary>
        public static Vector2 Crown(GameObject prefab)
        {
            if (prefab == null)
                return new Vector2(3f, 0f);

            if (Prototypes.TryGetValue(prefab, out Vector2 known))
                return known;

            LODGroup lod = prefab.GetComponentInChildren<LODGroup>();
            Renderer[] renderers = lod != null && lod.lodCount > 0 ? lod.GetLODs()[0].renderers : prefab.GetComponentsInChildren<Renderer>();
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            float extentX = 0f, extentZ = 0f;
            bool evergreen = false;

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                    continue;

                MeshFilter filter = renderer.GetComponent<MeshFilter>();

                if (filter != null && filter.sharedMesh != null)
                {
                    Matrix4x4 matrix = toRoot * renderer.transform.localToWorldMatrix;
                    Bounds b = filter.sharedMesh.bounds;

                    for (int c = 0; c < 8; c++)
                    {
                        Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1));
                        Vector3 point = matrix.MultiplyPoint3x4(corner);
                        extentX = Mathf.Max(extentX, Mathf.Abs(point.x));
                        extentZ = Mathf.Max(extentZ, Mathf.Abs(point.z));
                    }
                }

                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null && material.HasProperty("_Evergreen") && material.GetFloat("_Evergreen") > 0.5f)
                        evergreen = true;
                }
            }

            float radius = (extentX + extentZ) * 0.5f * Mathf.Abs(prefab.transform.localScale.x);
            Vector2 result = new(radius > 0.1f ? radius : 3f, evergreen ? 1f : 0f);
            Prototypes[prefab] = result;
            return result;
        }

        private void Begin(Vector2 origin, int signature)
        {
            Rect grown = new(origin - Vector2.one * 16f, Vector2.one * (windowSize + 32f));
            _gather.Clear();

            foreach (Crowns crowns in _crowns.Values)
            {
                if (!crowns.Bounds.Overlaps(grown))
                    continue;

                foreach (Vector4 tree in crowns.Trees)
                {
                    if (grown.Contains(new Vector2(tree.x, tree.y)))
                        _gather.Add(tree);
                }
            }

            foreach (Vector4[] trees in ObjectTrees.Values)
            {
                foreach (Vector4 tree in trees)
                {
                    if (grown.Contains(new Vector2(tree.x, tree.y)))
                        _gather.Add(new Vector4(tree.x, tree.y, tree.z * crownScale, Kind(tree.x, tree.y, tree.w > 0.5f)));
                }
            }

            Vector4[] gathered = _gather.ToArray();
            int resolution = _resolution;
            float step = texel;
            float spread = litterSpread;
            float reach = litterReach;
            _jobOrigin = origin;
            _jobSignature = signature;
            _treesInMap = gathered.Length;
            _job = Task.Run(() => Stamp(gathered, origin, resolution, step, spread, reach));
        }

        private void Finish()
        {
            Task<byte[]> job = _job;
            _job = null;

            if (job.IsFaulted || job.Result == null)
            {
                Debug.LogWarning("[ForestFloor] Map build failed: " + job.Exception?.GetBaseException().Message, this);
                return;
            }

            _map.SetPixelData(job.Result, 0);
            _map.Apply(false, false);
            _origin = _jobOrigin;
            _signature = _jobSignature;
            _has = true;
            PublishRect();
        }

        // Вид дерева для карты: 1 — хвойное, 2 — лиственное, голое осенью, 0 — лиственное.
        private static float Kind(float x, float z, bool conifer) => conifer ? 1f : BareInAutumn(x, z) ? 2f : 0f;

        /// <summary>
        /// Голое ли осенью лиственное дерево с корнем в (x, z) — как решает шейдер деревьев (SeasonTrees.hlsl:
        /// SeasonTreeRandom(41u) &lt; 0.3, Jenkins-хеш Random.hlsl по корню с точностью 10 см), бит в бит.
        /// </summary>
        public static bool BareInAutumn(float x, float z)
        {
            uint ix = unchecked((uint)(int)Mathf.Floor(x * 10f));
            uint iz = unchecked((uint)(int)Mathf.Floor(z * 10f));
            uint hash = Jenkins(ix ^ Jenkins(iz ^ Jenkins(41u)));
            return BitConverter.Int32BitsToSingle((int)((hash & 0x007FFFFFu) | 0x3F800000u)) - 1f < 0.3f;
        }

        private static uint Jenkins(uint x)
        {
            unchecked
            {
                x += x << 10;
                x ^= x >> 6;
                x += x << 3;
                x ^= x >> 11;
                x += x << 15;
                return x;
            }
        }

        // Кроны на карту (фоновый поток): каждой кроне — мягкий круг сомкнутости и шире — круг листвы; суммы насыщаются
        // (1 − e^−k·сумма), сглаживаются на тексель и пишутся в RGBA (R подстилка, G доля хвои, B сомкнутость, A доля листвы
        // голых осенью деревьев).
        private static byte[] Stamp(Vector4[] trees, Vector2 origin, int n, float step, float spread, float reach)
        {
            float[] litter = new float[n * n];
            float[] crown = new float[n * n];
            float[] needles = new float[n * n];
            float[] fresh = new float[n * n];

            foreach (Vector4 tree in trees)
            {
                float radius = tree.z;
                float far = radius * spread + reach;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((tree.x - far - origin.x) / step));
                int x1 = Mathf.Min(n - 1, Mathf.FloorToInt((tree.x + far - origin.x) / step));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((tree.y - far - origin.y) / step));
                int z1 = Mathf.Min(n - 1, Mathf.FloorToInt((tree.y + far - origin.y) / step));
                bool conifer = tree.w > 0.5f && tree.w < 1.5f;
                bool bare = tree.w > 1.5f;

                for (int z = z0; z <= z1; z++)
                {
                    float dz = origin.y + (z + 0.5f) * step - tree.y;

                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = origin.x + (x + 0.5f) * step - tree.x;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);

                        if (d >= far)
                            continue;

                        int i = z * n + x;
                        float leaves = 1f - Smooth(radius * 0.8f, far, d);
                        litter[i] += leaves;
                        crown[i] += 1f - Smooth(radius * 0.6f, radius * 1.05f, d);

                        if (conifer)
                            needles[i] += leaves;
                        else if (bare)
                            fresh[i] += leaves;
                    }
                }
            }

            Blur(litter, n);
            Blur(crown, n);
            Blur(needles, n);
            Blur(fresh, n);
            byte[] pixels = new byte[n * n * 4];

            for (int i = 0; i < n * n; i++)
            {
                pixels[i * 4] = ToByte(1f - Mathf.Exp(-1.3f * litter[i]));
                pixels[i * 4 + 1] = ToByte(litter[i] > 1e-3f ? needles[i] / litter[i] : 0f);
                pixels[i * 4 + 2] = ToByte(1f - Mathf.Exp(-1.2f * crown[i]));
                pixels[i * 4 + 3] = ToByte(litter[i] > 1e-3f ? fresh[i] / litter[i] : 0f);
            }

            return pixels;
        }

        // Сглаживание [1 2 1]/4 по обеим осям.
        private static void Blur(float[] values, int n)
        {
            float[] row = new float[n];

            for (int z = 0; z < n; z++)
            {
                int start = z * n;

                for (int x = 0; x < n; x++)
                    row[x] = (values[start + Mathf.Max(x - 1, 0)] + 2f * values[start + x] + values[start + Mathf.Min(x + 1, n - 1)]) * 0.25f;

                Array.Copy(row, 0, values, start, n);
            }

            for (int x = 0; x < n; x++)
            {
                for (int z = 0; z < n; z++)
                    row[z] = (values[Mathf.Max(z - 1, 0) * n + x] + 2f * values[z * n + x] + values[Mathf.Min(z + 1, n - 1) * n + x]) * 0.25f;

                for (int z = 0; z < n; z++)
                    values[z * n + x] = row[z];
            }
        }

        private static float Smooth(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(edge1 - edge0, 1e-4f));
            return t * t * (3f - 2f * t);
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);

        // Вид подстилки текущей поры: номера слоёв в массивах фактуры земли (GroundDetail строит их из набора в том же
        // порядке), тона и доли.
        private void PublishLook()
        {
            Season season = Seasons.Instance != null ? Seasons.Instance.Current : Season.Summer;
            SeasonLook look = season switch
            {
                Season.Spring => spring,
                Season.Autumn => autumn,
                Season.Winter => winter,
                _ => summer
            };

            int needles = Layer(look.needleLayer);
            int baseLayer = Layer(look.baseLayer);

            if (baseLayer < 0 || needles < 0)
            {
                Debug.LogWarning($"[ForestFloor] Ground detail layers '{look.baseLayer}' or '{look.needleLayer}' are missing in Resources/{DetailSetResource}", this);
                Shader.SetGlobalVector(MixId, Vector4.zero);
                return;
            }

            int patch = Layer(look.patchLayer);
            int moss = Layer(look.mossLayer);
            Shader.SetGlobalVector(LayersId, new Vector4(baseLayer, patch >= 0 ? patch : baseLayer, moss >= 0 ? moss : baseLayer, needles));
            Shader.SetGlobalVectorArray(ToneId, new[]
            {
                Tone(look.baseTone, look.baseRecolour),
                Tone(look.patchTone, look.patchRecolour),
                Tone(look.mossTone, true),
                Tone(look.needleTone, true)
            });
            Shader.SetGlobalVector(MixId, new Vector4(patch >= 0 ? look.patchShare : 0f, moss >= 0 ? look.mossShare : 0f,
                look.amount.x, Mathf.Max(look.amount.y, look.amount.x + 0.01f)));
            Shader.SetGlobalVector(FreshId, new Vector4(look.freshPatch, look.freshAmount, 0f, 0f));
        }

        private static Vector4 Tone(Color tone, bool recolour) => new(tone.r, tone.g, tone.b, recolour ? 1f : 0f);

        private int Layer(string layerName)
        {
            if (_set == null || string.IsNullOrEmpty(layerName))
                return -1;

            return _set.layers.FindIndex(layer => layer.name == layerName);
        }

        private void PublishRect()
        {
            Shader.SetGlobalVector(RectId, _has && _visible
                ? new Vector4(_origin.x, _origin.y, 1f / texel, _resolution)
                : Vector4.zero);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            Shader.SetGlobalVector(RectId, Vector4.zero);
            ObjectTrees.Clear();
            _objectTreesVersion = 0;
        }
    }
}
