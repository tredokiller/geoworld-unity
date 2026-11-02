using System.Collections.Generic;
using GeoWorld;
using Sirenix.OdinInspector;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Фактура земли вблизи камеры поверх спутника тайлов (рисует шейдер террейна, GroundDetail.hlsl). Отдаёт шейдерам:
    ///  - массивы текстур набора (GroundDetailSet из Resources/Terrain): собираются при старте копией на GPU из текстур
    ///    набора — так в проекте и билде лежат только сами текстуры, без второй копии в массиве; после копии они выгружаются;
    ///  - параметры слоёв (повтор, контраст, нормали, AO, средний цвет) и слои каждого класса земли;
    ///  - окно классов земли GeoWorld вокруг камеры (_GroundDetailClasses: клетки карты земель, ~10 м, 128 × 128 — ~1.3 км;
    ///    фактура видна до ~280 м, окно переезжает, когда камера отходит от его центра на 320 м). Строится по строкам за
    ///    несколько кадров во втором массиве и подменяет готовое целиком.
    /// Один на сцену (на объекте WeatherManager).
    /// </summary>
    public sealed class GroundDetail : MonoBehaviour
    {
        private const string SetPath = "Terrain/GroundDetailSet";
        private const int MaxLayers = 32;
        private const int Cells = 128;
        private const int RowsPerFrame = 16;
        private const float RecenterDistance = 320f;

        private static readonly int AlbedoId = Shader.PropertyToID("_GroundDetailAlbedo");
        private static readonly int NormalId = Shader.PropertyToID("_GroundDetailNormal");
        private static readonly int MaskId = Shader.PropertyToID("_GroundDetailMask");
        private static readonly int ClassesId = Shader.PropertyToID("_GroundDetailClasses");
        private static readonly int RectId = Shader.PropertyToID("_GroundDetailRect");
        private static readonly int FadeId = Shader.PropertyToID("_GroundDetailFade");
        private static readonly int LayerId = Shader.PropertyToID("_GroundDetailLayer");
        private static readonly int AverageId = Shader.PropertyToID("_GroundDetailAverage");
        private static readonly int ClassId = Shader.PropertyToID("_GroundDetailClass");

        private readonly List<Texture2DArray> _arrays = new();
        private byte[] _ready = new byte[Cells * Cells];
        private byte[] _next = new byte[Cells * Cells];
        private Texture2D _classes;
        private GeoLandCover _cover;
        private Vector2 _origin;
        private Vector2 _buildingOrigin;
        private float _cell;
        private float _buildingCell;
        private int _buildingRow = -1;
        private bool _has;
        private bool _initialized;

        [ShowInInspector, LabelText("Фактура земли")]
        public bool Visible
        {
            get => _visible;
            set
            {
                _visible = value;
                PublishRect();
            }
        }

        private bool _visible = true;

        public string Stats => $"layers={_arrays.Count > 0} window={(_has ? _origin.ToString("F0") : "none")} cell={_cell:F1} building={_buildingRow}";

        private void Awake()
        {
            Shader.SetGlobalVector(RectId, Vector4.zero);
            GroundDetailSet set = Resources.Load<GroundDetailSet>(SetPath);

            if (set == null || set.layers.Count == 0)
            {
                Debug.LogWarning($"[GroundDetail] Ground detail set is missing: Resources/{SetPath}", this);
                return;
            }

            if (!BuildArrays(set))
                return;

            PublishSettings(set);
            _classes = new Texture2D(Cells, Cells, TextureFormat.R8, false, true)
            {
                name = "Ground detail classes",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            Shader.SetGlobalTexture(ClassesId, _classes);
            _initialized = true;
        }

        private void OnDestroy()
        {
            Shader.SetGlobalVector(RectId, Vector4.zero);

            foreach (Texture2DArray array in _arrays)
            {
                if (array != null)
                    Destroy(array);
            }

            if (_classes != null)
                Destroy(_classes);
        }

        private void Update()
        {
            if (!_initialized)
                return;

            Camera camera = Camera.main;

            if (camera != null)
                Build(camera.transform.position);
        }

        // Альбедо, нормали и маски слоёв — в три массива. Все текстуры набора одного размера и формата (иначе GPU их не
        // скопирует); слой, что не сходится, берёт текстуру первого.
        private bool BuildArrays(GroundDetailSet set)
        {
            Texture2DArray albedo = BuildArray(set, layer => layer.albedo, "Ground detail albedo");
            Texture2DArray normal = BuildArray(set, layer => layer.normal, "Ground detail normal");
            Texture2DArray mask = BuildArray(set, layer => layer.mask, "Ground detail mask");

            if (albedo == null || normal == null || mask == null)
                return false;

            Shader.SetGlobalTexture(AlbedoId, albedo);
            Shader.SetGlobalTexture(NormalId, normal);
            Shader.SetGlobalTexture(MaskId, mask);

            // текстуры набора больше не нужны: всё уже в массивах
            foreach (GroundDetailSet.Layer layer in set.layers)
            {
                Unload(layer.albedo);
                Unload(layer.normal);
                Unload(layer.mask);
            }

            return true;
        }

        private Texture2DArray BuildArray(GroundDetailSet set, System.Func<GroundDetailSet.Layer, Texture2D> pick, string arrayName)
        {
            int count = Mathf.Min(set.layers.Count, MaxLayers);
            Texture2D first = pick(set.layers[0]);

            if (first == null)
            {
                Debug.LogWarning($"[GroundDetail] {arrayName}: the first layer has no texture.", this);
                return null;
            }

            Texture2DArray array = new(first.width, first.height, count, first.graphicsFormat,
                TextureCreationFlags.MipChain | TextureCreationFlags.DontInitializePixels, first.mipmapCount)
            {
                name = arrayName,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8
            };
            // память под копию на CPU не нужна: массив заполняет копия на GPU
            array.Apply(false, true);

            for (int i = 0; i < count; i++)
            {
                Texture2D source = pick(set.layers[i]);
                bool fits = source != null && source.width == first.width && source.height == first.height &&
                            source.graphicsFormat == first.graphicsFormat && source.mipmapCount == first.mipmapCount;

                if (!fits)
                    Debug.LogWarning($"[GroundDetail] {arrayName}: layer {set.layers[i].name} differs from the first in size or format.", this);

                Graphics.CopyTexture(fits ? source : first, 0, array, i);
            }

            _arrays.Add(array);
            return array;
        }

        private static void Unload(Texture2D texture)
        {
            if (texture != null)
                Resources.UnloadAsset(texture);
        }

        private static void PublishSettings(GroundDetailSet set)
        {
            Vector4[] layers = new Vector4[MaxLayers];
            Vector4[] averages = new Vector4[MaxLayers];

            for (int i = 0; i < MaxLayers; i++)
            {
                GroundDetailSet.Layer layer = set.layers[Mathf.Min(i, set.layers.Count - 1)];
                layers[i] = new Vector4(1f / Mathf.Max(layer.tileMetres, 0.1f), layer.contrast, layer.normalStrength, layer.occlusion);
                averages[i] = new Vector4(layer.average.r, layer.average.g, layer.average.b, 1f);
            }

            Vector4[] classes = new Vector4[GeoLandCover.ClassCount];
            int last = Mathf.Min(set.layers.Count, MaxLayers) - 1;

            for (int i = 0; i < classes.Length; i++)
            {
                GroundDetailSet.ClassLayers entry = set.classes != null && i < set.classes.Length ? set.classes[i] : null;
                classes[i] = entry == null
                    ? Vector4.zero
                    : new Vector4(Mathf.Clamp(entry.green, 0, last), Mathf.Clamp(entry.bareDark, 0, last), Mathf.Clamp(entry.bareLight, 0, last), 0f);
            }

            Shader.SetGlobalVectorArray(LayerId, layers);
            Shader.SetGlobalVectorArray(AverageId, averages);
            Shader.SetGlobalVectorArray(ClassId, classes);
            float fadeEnd = Mathf.Max(set.fadeEnd, set.fadeStart + 1f);
            Shader.SetGlobalVector(FadeId, new Vector4(set.fadeStart, 1f / (fadeEnd - set.fadeStart), fadeEnd, 0f));
        }

        // Окно классов по клеткам карты земель того мира, над которым камера; новое считается по строкам и подменяет старое.
        private void Build(Vector3 eye)
        {
            if (_buildingRow < 0)
            {
                Vector2 centre = _origin + Vector2.one * (Cells * _cell * 0.5f);

                if (_has && Mathf.Abs(eye.x - centre.x) < RecenterDistance && Mathf.Abs(eye.z - centre.y) < RecenterDistance)
                    return;

                GeoLandCover cover = GeoLandCover.At(eye.x, eye.z);

                if (cover == null)
                    return;

                _cover = cover;
                _buildingCell = cover.cellSize > 0f ? cover.cellSize : 10f;
                float half = Cells * _buildingCell * 0.5f;
                // по сетке карты земель: тексель окна — ровно её клетка
                _buildingOrigin = new Vector2(
                    cover.origin.x + Mathf.Round((eye.x - half - cover.origin.x) / _buildingCell) * _buildingCell,
                    cover.origin.y + Mathf.Round((eye.z - half - cover.origin.y) / _buildingCell) * _buildingCell);
                _buildingRow = 0;
            }

            if (_cover == null)
            {
                _buildingRow = -1;
                return;
            }

            int end = Mathf.Min(_buildingRow + RowsPerFrame, Cells);

            for (int z = _buildingRow; z < end; z++)
            {
                float pointZ = _buildingOrigin.y + (z + 0.5f) * _buildingCell;

                for (int x = 0; x < Cells; x++)
                {
                    float pointX = _buildingOrigin.x + (x + 0.5f) * _buildingCell;
                    _next[z * Cells + x] = _cover.Sample(pointX, pointZ, out GeoLandClass land) ? (byte)land : (byte)GeoLandClass.None;
                }
            }

            _buildingRow = end;

            if (_buildingRow < Cells)
                return;

            _buildingRow = -1;
            (_ready, _next) = (_next, _ready);
            _origin = _buildingOrigin;
            _cell = _buildingCell;
            _has = true;
            _classes.SetPixelData(_ready, 0);
            _classes.Apply(false);
            PublishRect();
        }

        private void PublishRect()
        {
            Shader.SetGlobalVector(RectId, _has && _visible
                ? new Vector4(_origin.x, _origin.y, 1f / _cell, Cells)
                : Vector4.zero);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState() => Shader.SetGlobalVector(RectId, Vector4.zero);
    }
}
