using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Покрытие дорог на земле. На снимке тайла дорогу видно не везде (в лесу, в тени, где её размыло сжатие), а тайлы
    /// kharkiv_east своих дорог не рисуют (меши дорог GeoWorld выключены, слоёв дорог у спутникового террейна нет). Здесь
    /// оси дорог (RoadAreas: вид покрытия, разметка, износ, бордюр) рисуются сверху в окно windowSize вокруг камеры
    /// (Hidden/Game/Road Map: в каждом пикселе у дороги — её отрезок, полуширина, метры вдоль, вид и концы дороги — _RoadMap,
    /// _RoadMapInfo, _RoadMapRect; где полотна накрывают друг друга, побеждает главная — RoadMapCommon.hlsl; перекрёстки —
    /// _RoadJunction), и террейн по ним рисует полотно (GroundRoads.hlsl): асфальт с обочиной и разметкой, гравий и грунт с
    /// колеями, тропинки, плитку, насыпь рельсов. Фактуры — слои набора фактуры земли (GroundDetailSet, по имени —
    /// _RoadLayers): своих сэмплеров у прохода террейна не осталось. Карта перерисовывается, когда камера уходит от середины
    /// окна на recentreDistance. Один на сцену (добавляет WeatherManager).
    /// Окна два: ближнее (windowSize, 1 м на тексель — точные кромки, разметка, колеи у дрона) и дальнее (farWindowSize, до
    /// дальности загрузки тайлов, грубее — _RoadMapFar, _RoadMapInfoFar, _RoadJunctionFar, _RoadMapRectFar). С одним окном в
    /// 512 м дороги обрывались в ~250 м от дрона. В дальнем те же отрезки, и террейн мерит до них так же точно, просто в
    /// тексель попадает меньше соседних дорог; полоса каждой дороги там шире на тексель, чтобы узкие не проваливались между
    /// текселями. Террейн берёт ближнее окно внутри него, дальнее — за ним.
    /// </summary>
    public class RoadMap : MonoBehaviour
    {
        private const string MaterialResource = "Materials/RoadMap";
        private const string DetailSetResource = "Terrain/GroundDetailSet";
        private static readonly int LayersId = Shader.PropertyToID("_RoadLayers");
        private static readonly int Layers2Id = Shader.PropertyToID("_RoadLayers2");
        private static readonly int AsphaltAlbedoId = Shader.PropertyToID("_RoadAsphaltAlbedo");
        private static readonly int AsphaltNormalId = Shader.PropertyToID("_RoadAsphaltNormal");
        private static readonly int AsphaltId = Shader.PropertyToID("_RoadAsphalt");
        private static readonly int DrawId = Shader.PropertyToID("_RoadDraw");
        private static readonly int ReachId = Shader.PropertyToID("_RoadReach");

        // слои GroundDetailSet (Poly Haven, CC0): асфальт, гравий, грязь колеи, плитка; укатанный грунт, сухая трава
        private static readonly string[] LayerNames = { "asphalt", "gravel", "mud", "paving", "field_dry", "meadow_dry" };

        [Tooltip("Сторона окна карты дорог вокруг камеры, м.")]
        [SerializeField] private float windowSize = 512f;
        [Tooltip("Текселей по стороне окна (1 м на тексель при 512 и 512 м): расстояния террейн считает по отрезкам точно, " +
                 "тексель решает только, какие дороги рядом.")]
        [SerializeField] private int resolution = 512;
        [Tooltip("Запас за краем полотна, м: обочина и мягкий край.")]
        [SerializeField] private float reach = 2f;
        [Tooltip("На сколько камера уходит от середины окна, прежде чем карту перерисовать, м.")]
        [SerializeField] private float recentreDistance = 64f;

        [Header("Far window")]
        [Tooltip("Сторона дальнего окна, м (тайлы грузятся до ~2 км от камеры). Не больше ближнего — дальнего окна нет.")]
        [SerializeField] private float farWindowSize = 4096f;
        [Tooltip("Текселей по стороне дальнего окна (4 м на тексель при 1024 и 4096 м; три текстуры ~46 МБ).")]
        [SerializeField] private int farResolution = 1024;
        [Tooltip("На сколько камера уходит от середины дальнего окна, прежде чем его перерисовать, м.")]
        [SerializeField] private float farRecentreDistance = 512f;

        // Окно карты: три текстуры, свои отрезки и где оно лежит.
        private sealed class Window
        {
            public readonly string Name;
            public readonly int MapId;
            public readonly int InfoId;
            public readonly int JunctionId;
            public readonly int RectId;
            // отрезки (концы A, B) и их данные (полуширина, вдоль у A, вид и номер дороги)
            public RenderTexture Map;
            public RenderTexture Info;
            // перекрёстки: R — меньший номер дороги, накрывающей точку, A — больший (Hidden/Game/Road Map, проход 1)
            public RenderTexture Junction;
            public Mesh Mesh;
            public Vector2 Origin;
            public bool Drawn;

            public Window(string name, string suffix)
            {
                Name = name;
                MapId = Shader.PropertyToID("_RoadMap" + suffix);
                InfoId = Shader.PropertyToID("_RoadMapInfo" + suffix);
                JunctionId = Shader.PropertyToID("_RoadJunction" + suffix);
                RectId = Shader.PropertyToID("_RoadMapRect" + suffix);
            }
        }

        private readonly List<Vector3> _positions = new();
        private readonly List<Vector2> _a = new();
        private readonly List<Vector2> _b = new();
        private readonly List<Vector4> _road = new();
        private readonly List<Vector2> _id = new();
        private readonly List<int> _indices = new();
        private readonly RenderTargetIdentifier[] _targets = new RenderTargetIdentifier[2];
        private readonly Window _near = new("Road map", "");
        private readonly Window _far = new("Road map far", "Far");
        private Material _material;
        private CommandBuffer _commands;
        private bool _layersFound;
        private Vector4 _layers;
        private Vector4 _layers2;
        // полосы асфальта GeoWorld в массивах (RoadSurfaceSet); x — длина вдоль дороги, y — вариантов, z — есть ли
        private readonly List<Texture2DArray> _arrays = new();
        private Vector4 _asphalt;
        private bool _surfacesTried;

        private void OnDisable()
        {
            ReleaseWindow(_near);
            ReleaseWindow(_far);

            foreach (Texture2DArray array in _arrays)
            {
                if (array != null)
                    Destroy(array);
            }

            _arrays.Clear();
            _surfacesTried = false;

            if (_material != null)
                Destroy(_material);

            _commands?.Release();
            _commands = null;
            _material = null;
        }

        private static void ReleaseWindow(Window window)
        {
            Shader.SetGlobalVector(window.RectId, Vector4.zero);
            Release(ref window.Map);
            Release(ref window.Info);
            Release(ref window.Junction);

            if (window.Mesh != null)
                Destroy(window.Mesh);

            window.Mesh = null;
            window.Drawn = false;
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
                Destroy(texture);
            }

            texture = null;
        }

        private void LateUpdate()
        {
            Camera camera = Camera.main;

            if (camera == null || RoadAreas.Instance == null || !EnsureResources())
                return;

            Vector2 eye = new(camera.transform.position.x, camera.transform.position.z);
            Shader.SetGlobalVector(LayersId, _layers);
            Shader.SetGlobalVector(Layers2Id, _layers2);
            Shader.SetGlobalVector(AsphaltId, _asphalt);
            UpdateWindow(_near, eye, windowSize, resolution, recentreDistance, 0f);

            if (HasFarWindow)
                UpdateWindow(_far, eye, farWindowSize, farResolution, farRecentreDistance, farWindowSize / farResolution);
        }

        private bool HasFarWindow => farWindowSize > windowSize && farResolution > 1;

        private void UpdateWindow(Window window, Vector2 eye, float size, int texels, float recentre, float margin)
        {
            Vector2 centre = window.Origin + Vector2.one * (size * 0.5f);

            if (window.Drawn && window.Map.IsCreated() && (eye - centre).sqrMagnitude < recentre * recentre)
                return;

            Draw(window, eye, size, texels, margin);
        }

        // margin — запас на тексель окна сверх reach: полоса дороги не уже текселя, узкая не проваливается между ними.
        private void Draw(Window window, Vector2 eye, float size, int texels, float margin)
        {
            float texel = size / texels;
            // начало окна — по сетке текселей: когда окно переезжает, дорога не дрожит
            window.Origin = new Vector2(Mathf.Floor((eye.x - size * 0.5f) / texel) * texel, Mathf.Floor((eye.y - size * 0.5f) / texel) * texel);
            window.Drawn = true;
            BuildMesh(window.Mesh, RoadAreas.Instance.SegmentsIn(new Rect(window.Origin, Vector2.one * size)), reach + margin);

            _commands.Clear();
            _targets[0] = window.Map;
            _targets[1] = window.Info;
            _commands.SetRenderTarget(_targets, window.Map.depthBuffer);
            // полуширина 0 — дороги рядом нет
            _commands.ClearRenderTarget(true, true, Color.clear, 1f);
            _commands.SetGlobalVector(DrawId, new Vector4(window.Origin.x, window.Origin.y, 1f / size, SystemInfo.graphicsUVStartsAtTop ? -1f : 1f));
            _commands.SetGlobalVector(ReachId, new Vector4(reach + margin, margin, 0f, 0f));

            if (window.Mesh.vertexCount > 0)
                _commands.DrawMesh(window.Mesh, Matrix4x4.identity, _material, 0, 0);

            _commands.SetRenderTarget(window.Junction);
            _commands.ClearRenderTarget(false, true, new Color(60000f, 0f, 0f, -1f));

            if (window.Mesh.vertexCount > 0)
                _commands.DrawMesh(window.Mesh, Matrix4x4.identity, _material, 0, 1);

            Graphics.ExecuteCommandBuffer(_commands);
            Shader.SetGlobalTexture(window.MapId, window.Map);
            Shader.SetGlobalTexture(window.InfoId, window.Info);
            Shader.SetGlobalTexture(window.JunctionId, window.Junction);
            Shader.SetGlobalVector(window.RectId, new Vector4(window.Origin.x, window.Origin.y, 1f / size, texels));
        }

        // Прямоугольник по полотну отрезка с запасом grow за кромкой — четыре вершины; в каждой — концы отрезка, полуширина,
        // вдоль у A, до конца дороги и вид, фрагмент сам считает до оси.
        private void BuildMesh(Mesh mesh, RoadAreas.Segment[] segments, float margin)
        {
            _positions.Clear();
            _a.Clear();
            _b.Clear();
            _road.Clear();
            _id.Clear();
            _indices.Clear();

            foreach (RoadAreas.Segment segment in segments)
            {
                Vector2 ab = segment.B - segment.A;
                float length = ab.magnitude;

                if (length < 0.01f)
                    continue;

                Vector2 along = ab / length;
                Vector2 across = new(-along.y, along.x);
                float grow = segment.HalfWidth + margin;
                Vector2 start = segment.A - along * grow;
                Vector2 end = segment.B + along * grow;
                // вид, разметка, износ, бордюр и концы дороги (RoadMapCommon.hlsl) — целым в float
                float packed = (float)segment.Kind + (segment.Marked ? 8f : 0f) + Mathf.Floor(Mathf.Clamp01(segment.Wear) * 7.99f) * 16f +
                               (segment.Curb ? 128f : 0f) + (float)segment.Start * 256f + (float)segment.End * 1024f;
                // как в карте: полуширина, вдоль у A, вид, сколько от A до конца дороги
                var data = new Vector4(segment.HalfWidth, segment.AlongA, packed, segment.Length - segment.AlongA);
                // номер дороги — в половинной точности целым (до 2048): у соседних дорог одного перекрёстка не совпадёт
                var id = new Vector2(segment.Road % 2000 + 1, 0f);
                int first = _positions.Count;

                AddVertex(start - across * grow, segment, data, id);
                AddVertex(start + across * grow, segment, data, id);
                AddVertex(end + across * grow, segment, data, id);
                AddVertex(end - across * grow, segment, data, id);
                _indices.Add(first);
                _indices.Add(first + 1);
                _indices.Add(first + 2);
                _indices.Add(first);
                _indices.Add(first + 2);
                _indices.Add(first + 3);
            }

            mesh.Clear();
            mesh.indexFormat = _positions.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_positions);
            mesh.SetUVs(0, _a);
            mesh.SetUVs(1, _b);
            mesh.SetUVs(2, _road);
            mesh.SetUVs(3, _id);
            mesh.SetTriangles(_indices, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        private void AddVertex(Vector2 corner, RoadAreas.Segment segment, Vector4 data, Vector2 id)
        {
            _positions.Add(new Vector3(corner.x, 0f, corner.y));
            _a.Add(segment.A);
            _b.Add(segment.B);
            _road.Add(data);
            _id.Add(id);
        }

        private bool EnsureResources()
        {
            if (!_layersFound && !FindLayers())
                return false;

            if (!_surfacesTried)
            {
                _surfacesTried = true;
                BuildSurfaces();
            }

            if (_material == null)
            {
                Material source = Resources.Load<Material>(MaterialResource);
                Shader shader = source != null ? source.shader : Shader.Find("Hidden/Game/Road Map");

                if (shader == null || !shader.isSupported)
                    return false;

                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            _commands ??= new CommandBuffer { name = "Road map" };
            EnsureWindow(_near, resolution);

            if (HasFarWindow)
                EnsureWindow(_far, farResolution);
            else if (_far.Map != null)
                ReleaseWindow(_far);

            return true;
        }

        private static void EnsureWindow(Window window, int texels)
        {
            if (window.Mesh == null)
                window.Mesh = new Mesh { name = window.Name + " segments", hideFlags = HideFlags.HideAndDontSave };

            window.Drawn &= EnsureTexture(ref window.Map, texels, 24, RenderTextureFormat.ARGBFloat, window.Name);
            window.Drawn &= EnsureTexture(ref window.Info, texels, 0, RenderTextureFormat.ARGBFloat, window.Name + " info");
            window.Drawn &= EnsureTexture(ref window.Junction, texels, 0, RenderTextureFormat.ARGBHalf, window.Name + " junctions");
        }

        // false — текстуру пришлось создать заново (окно надо перерисовать).
        private static bool EnsureTexture(ref RenderTexture texture, int texels, int depth, RenderTextureFormat format, string textureName)
        {
            if (texture != null && texture.width == texels && texture.IsCreated())
                return true;

            Release(ref texture);
            texture = new RenderTexture(texels, texels, depth, format, RenderTextureReadWrite.Linear)
            {
                name = textureName,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false
            };
            texture.Create();
            return false;
        }

        // Полосы асфальта GeoWorld — в два массива (альбедо, нормали): все одного размера и формата, копия на GPU. Без набора
        // асфальт берёт фактуру асфальта из набора фактуры земли.
        private void BuildSurfaces()
        {
            _asphalt = Vector4.zero;
            RoadSurfaceSet set = Resources.Load<RoadSurfaceSet>(RoadSurfaceSet.ResourcePath);

            if (set == null || set.asphaltAlbedo.Length == 0 || set.asphaltAlbedo.Length != set.asphaltNormal.Length)
                return;

            Texture2DArray albedo = BuildArray(set.asphaltAlbedo, "Road asphalt albedo");
            Texture2DArray normal = BuildArray(set.asphaltNormal, "Road asphalt normal");

            if (albedo == null || normal == null)
                return;

            Shader.SetGlobalTexture(AsphaltAlbedoId, albedo);
            Shader.SetGlobalTexture(AsphaltNormalId, normal);
            _asphalt = new Vector4(set.stripLengthMetres, set.asphaltAlbedo.Length, 1f, 0f);
        }

        private Texture2DArray BuildArray(Texture2D[] sources, string arrayName)
        {
            Texture2D first = sources[0];

            if (first == null)
                return null;

            Texture2DArray array = new(first.width, first.height, sources.Length, first.graphicsFormat,
                UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain | UnityEngine.Experimental.Rendering.TextureCreationFlags.DontInitializePixels,
                first.mipmapCount)
            {
                name = arrayName,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8
            };
            // память под копию на CPU не нужна: массив заполняет копия на GPU
            array.Apply(false, true);

            for (int i = 0; i < sources.Length; i++)
            {
                Texture2D source = sources[i];
                bool fits = source != null && source.width == first.width && source.height == first.height &&
                            source.graphicsFormat == first.graphicsFormat && source.mipmapCount == first.mipmapCount;

                if (!fits)
                    Debug.LogWarning($"[RoadMap] {arrayName}: {(source != null ? source.name : "slot " + i)} differs from the first in size or format.", this);

                Graphics.CopyTexture(fits ? source : first, 0, array, i);
            }

            _arrays.Add(array);
            return array;
        }

        // Номера слоёв дорог в массивах фактуры земли (GroundDetail строит их из набора в том же порядке).
        private bool FindLayers()
        {
            GroundDetailSet set = Resources.Load<GroundDetailSet>(DetailSetResource);

            if (set == null || set.layers.Count == 0)
                return false;

            float[] found = new float[LayerNames.Length];

            for (int i = 0; i < LayerNames.Length; i++)
            {
                int index = set.layers.FindIndex(layer => layer.name == LayerNames[i]);

                if (index < 0)
                {
                    Debug.LogWarning($"[RoadMap] Ground detail layer '{LayerNames[i]}' is missing in Resources/{DetailSetResource}", this);
                    enabled = false;
                    return false;
                }

                found[i] = index;
            }

            _layers = new Vector4(found[0], found[1], found[2], found[3]);
            _layers2 = new Vector4(found[4], found[5], 0f, 0f);
            _layersFound = true;
            return true;
        }
    }
}
