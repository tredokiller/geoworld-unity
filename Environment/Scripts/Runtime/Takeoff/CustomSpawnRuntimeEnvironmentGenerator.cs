using System.Collections;
using System.Collections.Generic;
using _Game.Scripts.Enums;
using _Game.Scripts.Locations.FlyPoints;
using _Game.Scripts.World;
using GeoWorld;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace _Game.Scripts.Locations
{
    public sealed class CustomSpawnRuntimeEnvironmentGenerator : MonoBehaviour
    {
        private const float SquareMetersPerHectare = 10000f;
        private const float OccupancyCellSize = 4f;
        private const float CenterClearance = 0.5f;
        private const int CenterAttemptsPerCluster = 30;
        private const int MemberAttempts = 10;
        private const int DetailPlacementAttemptsMultiplier = 4;
        private const int DetailNoiseChannel = 8;
        private const int DetailClumpChannel = 16;
        private const float DetailPatchBlend = 0.12f;
        // как у Nature Renderer (OptimizePatchSize): с 32 он при инициализации перечитывал и переписывал каждый слой 1024²
        private const int DetailPatchResolution = 64;
        private const int MaxDetailPerCell = 255;
        private const int BoundaryChannel = 0;
        private const int GravelChannel = 1;
        private const int SoilChannel = 2;
        private const int TrampleChannel = 5;
        private const int PathChannel = 6;
        private const int SatelliteChannel = 7;
        private const int TrampleDetailChannel = 24;
        private const int TrampleFineChannel = 25;
        private const float BoundaryNoiseScale = 60f;
        private const float GravelNoiseScale = 3f;
        private const float SoilBlendWidth = 0.15f;
        private const float SatelliteRampEnd = 0.65f;
        private const float NearSatelliteShare = 0.35f;
        private const float SatellitePatchScale = 30f;
        private const float PadObjectClearance = 1.5f;
        private const float TileTreeClearance = 1.2f;
        private const float TreeLitterInner = 1.2f;
        private const float TreeLitterOuter = 5.5f;
        // пни и брёвна (KeepApart) видят дороги и на столько метров за краем тайла
        private const float ApartRoadReach = 10f;
        private const float WornGroundThreshold = 0.35f;
        // середина вытоптанной площадки и тропинки — голая: на ней ни травы, ни цветов, ни листвы
        private const float WornCore = 0.8f;
        // анализ места (AnalyzeSite): деревья тайла в этом радиусе и пороги плотности на гектар
        private const float SiteTreeRadius = 120f;
        private const float SiteOpenTreesPerHectare = 1f;
        private const float SiteForestTreesPerHectare = 30f;
        // спутник в этом радиусе; пороги красного над зелёным для зелени и голой земли
        private const float SiteGroundRadius = 60f;
        private const int SiteGroundRings = 6;
        private const float SiteGreenRedOverGreen = -0.03f;
        private const float SiteBareRedOverGreen = 0.03f;
        // карта земель GeoWorld (GeoLandCover): доля леса и сада в круге SiteTreeRadius, ниже которой место открытое, и выше — лес
        private const float SiteOpenTreeShare = 0.03f;
        private const float SiteForestTreeShare = 0.5f;
        // кусты считаются за полдерева
        private const float SiteScrubTreeWeight = 0.5f;
        // застройка в том же круге: от этой доли деревьев меньше, к этой — нет совсем
        private const float SiteBuiltUpLow = 0.15f;
        private const float SiteBuiltUpHigh = 0.6f;
        // пашня, которую спутник видит зелёной (всходы), — наполовину голая земля
        private const float SiteGreenCropSoil = 0.5f;
        private static readonly float[] LandShares = new float[GeoLandCover.ClassCount];
        // карта земель в зоне точки: шаг чтения (м) и доля воды, дорог и зданий, с которой в ячейке нет травы
        private const float HardGroundStep = 10f;
        private const float HardGroundShare = 0.35f;
        private const int SatelliteCopySize = 512;
        private const float BareSatelliteInfluence = 0.8f;
        private const int RuntimeAlphamapResolution = 1024;
        // ~1 м на ячейку для тайла 1 км: у тайла 128 (ячейка ~8 м), и трава с пятнами ложилась квадратами
        private const int RuntimeDetailResolution = 1024;
        private const float TreeLoaderTimeout = 10f;
        private const int ReattachCheckFrames = 15;
        private const float RuntimeDataDestroyDelay = 2f;
        private const string TerrainMaterialResource = "Terrain/TakeoffTerrainLit";
        // листва кустов (ива, клён) — на шейдере NatureManufacture без поры года; ей — его вариант с порой года
        private const string NatureManufactureFoliageShader = "NatureManufacture/URP/Foliage/Foliage";
        private const string SeasonalFoliageShader = "NatureManufacture/URP/Foliage/Foliage (Nature Renderer)";
        // вариант считает материал растением у земли (не деревом), если _PuddleSink > 0.001; оседание в лужу на 2 мм не видно
        private const float SeasonalBushPuddleSink = 0.002f;
        // глянец листвы, как у деревьев (у листвы NatureManufacture 1: в тени она отражала голубое небо — кусты и кроны синели)
        private const float LeafSmoothness = 0.3f;
        private static readonly int PuddleSinkId = Shader.PropertyToID("_PuddleSink");
        private static readonly int SmoothnessRemapMaxId = Shader.PropertyToID("_SmoothnessRemapMax");

        [SerializeField, Min(25f)] private float radius = 200f;
        [SerializeField] private bool paintTerrainTexture = true;
        [SerializeField] private bool placeDetailObjects;
        [SerializeField, Min(0f)] private float detailBudgetMultiplier = 1f;
        [SerializeField, Min(0)] private int maxDetailInstances = 1500;
        [SerializeField, Min(0)] private int maxPropInstances = 400;
        [SerializeField] private LayerMask groundLayer = ~0;
        [SerializeField] private LayerMask obstacleLayer = ~0;
        [SerializeField] private float raycastStartHeight = 150f;
        [SerializeField] private float raycastDistance = 300f;
        [Tooltip("Высота объекта на экране (доля высоты кадра), ниже которой пропадает его последний LOD. ~0.004 — около 4 пикселей при 1080p.")]
        [SerializeField, Range(0.0005f, 0.05f)] private float minCullScreenHeight = 0.004f;

        private readonly Dictionary<Vector2Int, List<OccupancyCircle>> _occupancy = new();
        private readonly List<OccupancyCircle> _occupancyCircles = new();
        private readonly Dictionary<GameObject, PrefabFootprint> _footprints = new();
        private readonly List<Vector2> _treeGroupCenters = new();
        private readonly List<Vector2> _treePositions = new();
        // кроны деревьев-префабов для лесной подстилки (ForestFloor): x, z, радиус, 1 у хвойного
        private readonly List<Vector4> _treeCrowns = new();
        private readonly List<RuntimeTerrainDataBackup> _runtimeTerrainBackups = new();
        private readonly List<(Terrain Terrain, Material Original, Material Runtime)> _runtimeTerrainMaterials = new();
        private readonly HashSet<TerrainData> _pendingReattach = new();
        private readonly Collider[] _overlaps = new Collider[32];
        // копии материалов листвы кустов на шейдере с порой года — по исходному материалу, общие на точку
        private readonly Dictionary<Material, Material> _seasonalMaterials = new();

        private Transform _root;
        private Coroutine _pendingPopulate;
        private FlyPointEnvironmentProfile _profile;
        private Vector3 _center;
        private System.Random _random;
        private RuntimeNoise _noise;
        private TakeoffSite _site;
        private SiteContext _context = SiteContext.Default;
        private int _seed;
        private int _propCount;
        private int _detailCount;
        private float _treeGroupRadius;

        // Копии TerrainData тайлов, которые сейчас держит генератор: трава мира (GroundDressing) такие тайлы не трогает.
        private static readonly HashSet<TerrainData> RuntimeCopies = new();

        public static bool IsRuntimeCopy(TerrainData data) => data != null && RuntimeCopies.Contains(data);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeCopies() => RuntimeCopies.Clear();

        private void Awake()
        {
            SceneManager.sceneLoaded += ReattachRuntimeTerrainData;
            Seasons.Changed += RegenerateForSeason;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= ReattachRuntimeTerrainData;
            Seasons.Changed -= RegenerateForSeason;

            foreach (Material material in _seasonalMaterials.Values)
                Destroy(material);

            _seasonalMaterials.Clear();
        }

        // Сменилась пора года (в редакторе пресета): точка — заново с тем же зерном, раскладка та же, трава и листва — по поре.
        private void RegenerateForSeason()
        {
            if (_profile != null && _root != null)
                Generate(_center, _profile, _seed);
        }

        // Тайл GeoWorld включает свой террейн не сразу к sceneLoaded: подхватываем его, как только он появится.
        private void Update()
        {
            if (Time.frameCount % ReattachCheckFrames != 0)
                return;

            if (_runtimeTerrainBackups.Exists(backup => backup.Terrain == null))
                ReattachRuntimeTerrainData(default, LoadSceneMode.Additive);

            PaintLateTerrains();
        }

        public void Generate(Vector3 center, FlyPointEnvironmentProfile profile, int seed)
        {
            Clear();

            if (profile == null)
            {
                Debug.LogWarning("[CustomSpawnRuntimeEnvironmentGenerator] Environment profile is not assigned.");
                return;
            }

            _profile = profile;
            _seed = seed;
            _random = new System.Random(seed);
            _noise = new RuntimeNoise(seed);
            _propCount = 0;
            _detailCount = 0;
            _treeGroupRadius = 0f;
            _treeGroupCenters.Clear();
            _treePositions.Clear();
            _treeCrowns.Clear();
            _occupancy.Clear();
            _occupancyCircles.Clear();
            // кейс на площадке; ближе к точке, чем RadiusRange категорий, объекты и так не ставятся
            AddOccupancy(XZ(center), PadObjectClearance);

            _root = new GameObject("CustomSpawnRuntimeEnvironment").transform;
            _center = center;
            _site = new TakeoffSite(_noise, _random, profile.GravelRadius, radius + profile.EdgeBand);

            if (Application.isPlaying && TerrainsAround(center).Exists(terrain => !TreesReady(terrain)))
            {
                _pendingPopulate = StartCoroutine(PopulateWhenTreesLoaded(profile, center));
                return;
            }

            Populate(TerrainsAround(center), profile, center);
        }

        /// <summary>
        /// Точка взлёта в только что подгруженном тайле: GeoTerrainTreeLoader ещё не положил деревья. Подмена
        /// TerrainData раньше него запоминала оригиналом ассет без деревьев, загрузчик клонировал уже нашу копию,
        /// и при пересоздании точки на том же месте возвращался ассет без деревьев — деревья тайла пропадали.
        /// Объекты тоже ждут: они обходят деревья тайла. Ждём все тайлы, на которые заходит зона точки.
        /// </summary>
        private IEnumerator PopulateWhenTreesLoaded(FlyPointEnvironmentProfile profile, Vector3 center)
        {
            float deadline = Time.unscaledTime + TreeLoaderTimeout;

            while (TerrainsAround(center).Exists(terrain => !TreesReady(terrain)) && Time.unscaledTime < deadline)
                yield return null;

            _pendingPopulate = null;

            if (_root != null)
                Populate(TerrainsAround(center), profile, center);
        }

        // Сначала объекты, потом земля и трава: трава не растёт сквозь камни и под кейсом.
        private void Populate(List<Terrain> terrains, FlyPointEnvironmentProfile profile, Vector3 center)
        {
            foreach (Terrain terrain in terrains)
                AddTileTreeOccupancy(terrain, center);

            _context = AnalyzeSite(terrains, profile, center);
            FlyPointScatterCategory[] categories = profile.Categories ?? System.Array.Empty<FlyPointScatterCategory>();

            foreach (FlyPointScatterCategory category in categories)
            {
                if (category != null && category.AsTerrainTrees)
                    PlaceCategory(profile, category, center);
            }

            foreach (FlyPointScatterCategory category in categories)
            {
                if (category != null && !category.AsTerrainTrees)
                    PlaceCategory(profile, category, center);
            }

            // Зона точки (radius + EdgeBand) у края тайла заходит на соседний: красился только тайл центра,
            // и трава с землёй обрывались прямой линией по стыку. Смешивание считается в мировых координатах,
            // поэтому на соседних тайлах оно продолжается без шва.
            if (paintTerrainTexture)
            {
                foreach (Terrain terrain in terrains)
                    PaintTerrainTexture(terrain, profile, center);
            }

            if (placeDetailObjects)
                PlaceDetails(profile, center);

            ForestFloor.SetObjectTrees(this, _treeCrowns.ToArray());
        }

        /// <summary>
        /// Соседний тайл, на который заходит зона точки, мог загрузиться позже генерации: красим его, как только
        /// он появится и загрузчик положит деревья. Тайл, который выгружался и вернулся, подхватывает
        /// ReattachRuntimeTerrainData (у него уже есть копия TerrainData).
        /// </summary>
        private void PaintLateTerrains()
        {
            if (!Application.isPlaying || !paintTerrainTexture || _root == null || _profile == null || _pendingPopulate != null)
                return;

            foreach (Terrain terrain in TerrainsAround(_center))
            {
                Vector2 anchor = AnchorOf(terrain);

                if (!TreesReady(terrain) ||
                    _runtimeTerrainBackups.Exists(backup => backup.Terrain == terrain || (backup.Anchor - anchor).sqrMagnitude < 1f))
                {
                    continue;
                }

                AddTileTreeOccupancy(terrain, _center);
                PaintTerrainTexture(terrain, _profile, _center);
            }
        }

        // Тайлы, на которые заходит зона точки вместе с краевой полосой.
        private List<Terrain> TerrainsAround(Vector3 center)
        {
            List<Terrain> terrains = new();
            float extent = radius + (_profile != null ? _profile.EdgeBand : 0f);
            Vector2 point = XZ(center);

            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null)
                    continue;

                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                float dx = Mathf.Max(origin.x - point.x, 0f, point.x - (origin.x + size.x));
                float dz = Mathf.Max(origin.z - point.y, 0f, point.y - (origin.z + size.z));

                if (dx * dx + dz * dz <= extent * extent)
                    terrains.Add(terrain);
            }

            return terrains;
        }

        private static bool TreesReady(Terrain terrain)
        {
            GeoTerrainTreeLoader trees = terrain != null ? terrain.GetComponent<GeoTerrainTreeLoader>() : null;
            return (trees == null || trees.Ready) && _Game.Scripts.World.TileTreeSpecies.Ready(terrain);
        }

        // Середина тайла: по ней вернувшийся после выгрузки тайл находит свою копию TerrainData.
        private static Vector2 AnchorOf(Terrain terrain)
        {
            Vector3 size = terrain.terrainData.size;
            return XZ(terrain.transform.position) + new Vector2(size.x, size.z) * 0.5f;
        }

        public bool HasEnvironmentAt(Vector3 center) =>
            _root != null && (XZ(center) - XZ(_center)).sqrMagnitude < 1f;

        public void Clear()
        {
            if (_pendingPopulate != null)
            {
                StopCoroutine(_pendingPopulate);
                _pendingPopulate = null;
            }

            RestoreRuntimeTerrainData();

            if (_root != null)
            {
                // Destroy срабатывает только в конце кадра, а Generate ставит новые деревья сразу: коллайдеры старых
                // (тот же сид — те же места) считались препятствием, и при пересоздании точки деревьев не оставалось.
                _root.gameObject.SetActive(false);
                Destroy(_root.gameObject);
                _root = null;
            }

            _occupancy.Clear();
            _occupancyCircles.Clear();
            _treeGroupCenters.Clear();
            _treePositions.Clear();
            _treeCrowns.Clear();
            ForestFloor.SetObjectTrees(this, null);
        }

        /// <summary>
        /// Что вокруг точки. Если у мира запечена карта земель GeoWorld (GeoLandCover: ESA WorldCover + OSM, 10 м) — по
        /// ней (TryReadLandCover). Иначе лес или открытое место — по деревьям тайла рядом: в лесу и саду их ~100 на гектар,
        /// у лесополосы десятки, в поле единицы. Зелень или голая земля (пахота, стерня) — по спутнику: у голой
        /// земли красный выше зелёного (пахота ~+0.03 в круге 60 м), у луга и леса ниже (~-0.10 и -0.11). По этому профиль
        /// решает, сколько ставить деревьев, пней, кустов, травы и цветов и сколько оставить голой земли.
        /// </summary>
        private SiteContext AnalyzeSite(List<Terrain> terrains, FlyPointEnvironmentProfile profile, Vector3 center)
        {
            Vector2 point = XZ(center);
            int trees = 0;

            foreach (Vector2 tree in _treePositions)
            {
                if ((tree - point).sqrMagnitude < SiteTreeRadius * SiteTreeRadius)
                    trees++;
            }

            float treesPerHectare = trees / (Mathf.PI * SiteTreeRadius * SiteTreeRadius / SquareMetersPerHectare);
            float treeCover = RuntimeNoise.SmoothStep(SiteOpenTreesPerHectare, SiteForestTreesPerHectare, treesPerHectare);
            float redOverGreen = 0f;
            float weight = 0f;
            Dictionary<Texture, Texture2D> satellite = new();

            for (int ring = 0; ring <= SiteGroundRings; ring++)
            {
                float distance = SiteGroundRadius * ring / SiteGroundRings;
                int samples = ring == 0 ? 1 : ring * 8;
                // середина весит больше: там площадка и ближняя трава
                float ringWeight = 1f - 0.5f * ring / SiteGroundRings;

                for (int i = 0; i < samples; i++)
                {
                    float angle = Mathf.PI * 2f * i / samples;
                    Vector2 sample = point + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;

                    if (!TrySampleSatellite(terrains, profile, satellite, sample, out Color colour))
                        continue;

                    redOverGreen += (colour.r - colour.g) * ringWeight;
                    weight += ringWeight;
                }
            }

            foreach (Texture2D copy in satellite.Values)
                Destroy(copy);

            float bareGround = weight > 0f
                ? RuntimeNoise.SmoothStep(SiteGreenRedOverGreen, SiteBareRedOverGreen, redOverGreen / weight)
                : 0f;
            float meanRedOverGreen = weight > 0f ? redOverGreen / weight : 0f;

            return TryReadLandCover(center, bareGround, out float landTreeCover, out float landBareGround, out string landCover)
                ? new SiteContext(landTreeCover, landBareGround, treesPerHectare, meanRedOverGreen, landCover)
                : new SiteContext(treeCover, bareGround, treesPerHectare, meanRedOverGreen, null);
        }

        /// <summary>
        /// Что вокруг точки по карте земель GeoWorld: лес и сад дают деревья, застройка их убирает; пашня и голая земля —
        /// грунт, причём на пашне спутник решает, вспахана она или во всходах (satelliteBare: 0 зелень, 1 голая земля).
        /// false — у мира нет карты земель (не запечена или старый мир): тогда работает оценка по деревьям тайла и спутнику.
        /// </summary>
        private static bool TryReadLandCover(Vector3 center, float satelliteBare, out float treeCover, out float bareGround, out string summary)
        {
            treeCover = 0f;
            bareGround = 0f;
            summary = null;

            if (!GeoLandCover.TryGetFractions(center, SiteTreeRadius, LandShares))
                return false;

            float trees = GeoLandCover.Share(LandShares, GeoLandClass.Forest, GeoLandClass.Orchard) +
                          SiteScrubTreeWeight * LandShares[(int)GeoLandClass.Scrub];
            float builtUp = GeoLandCover.Share(LandShares, GeoLandClass.Urban, GeoLandClass.Building, GeoLandClass.Asphalt, GeoLandClass.Paving);
            treeCover = RuntimeNoise.SmoothStep(SiteOpenTreeShare, SiteForestTreeShare, trees) *
                        (1f - RuntimeNoise.SmoothStep(SiteBuiltUpLow, SiteBuiltUpHigh, builtUp));
            string around = DescribeLand(LandShares);

            if (!GeoLandCover.TryGetFractions(center, SiteGroundRadius, LandShares))
                return false;

            float crop = LandShares[(int)GeoLandClass.Cropland];
            bareGround = Mathf.Clamp01(LandShares[(int)GeoLandClass.Bare] + crop * Mathf.Lerp(SiteGreenCropSoil, 1f, satelliteBare));
            summary = $"{SiteTreeRadius:0} m: {around}; {SiteGroundRadius:0} m: {DescribeLand(LandShares)}";
            return true;
        }

        // «Cropland 100%, Orchard 3%»: классы от 1%
        private static string DescribeLand(float[] shares)
        {
            System.Text.StringBuilder text = new();

            for (int i = 1; i < shares.Length; i++)
            {
                if (shares[i] < 0.01f)
                    continue;

                if (text.Length > 0)
                    text.Append(", ");

                text.Append((GeoLandClass)i).Append(' ').Append(Mathf.RoundToInt(shares[i] * 100f)).Append('%');
            }

            return text.ToString();
        }

        // Цвет спутника тайла в точке: слой тайла — первый не из профиля.
        private static bool TrySampleSatellite(
            List<Terrain> terrains,
            FlyPointEnvironmentProfile profile,
            Dictionary<Texture, Texture2D> copies,
            Vector2 world,
            out Color colour)
        {
            foreach (Terrain terrain in terrains)
            {
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;

                if (world.x < origin.x || world.x > origin.x + size.x || world.y < origin.z || world.y > origin.z + size.z)
                    continue;

                foreach (TerrainLayer layer in terrain.terrainData.terrainLayers)
                {
                    if (layer == null || layer.diffuseTexture == null ||
                        layer == profile.GrassLayer || layer == profile.SoilLayer || layer == profile.GravelLayer)
                    {
                        continue;
                    }

                    if (!copies.TryGetValue(layer.diffuseTexture, out Texture2D copy))
                    {
                        copy = SmallReadableCopy(layer.diffuseTexture);
                        copies[layer.diffuseTexture] = copy;
                    }

                    float u = (world.x - origin.x + layer.tileOffset.x) / Mathf.Max(1f, layer.tileSize.x);
                    float v = (world.y - origin.z + layer.tileOffset.y) / Mathf.Max(1f, layer.tileSize.y);
                    colour = copy.GetPixelBilinear(u, v);
                    return true;
                }
            }

            colour = default;
            return false;
        }

        /// <summary>
        /// Спутник тайла сжат DXT1 Crunched: GetPixel с такой текстуры отдаёт белый даже при Read/Write. Уменьшенная
        /// копия через GPU, 512 px на тайл (~2 м на пиксель): межи и полосы полей не размываются.
        /// </summary>
        private static Texture2D SmallReadableCopy(Texture texture)
        {
            RenderTexture target = RenderTexture.GetTemporary(
                SatelliteCopySize, SatelliteCopySize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(texture, target);
            RenderTexture.active = target;

            Texture2D copy = new(SatelliteCopySize, SatelliteCopySize, TextureFormat.RGBA32, false, false);
            copy.ReadPixels(new Rect(0, 0, SatelliteCopySize, SatelliteCopySize), 0, 0);
            copy.Apply(false);

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            return copy;
        }

        /// <summary>
        /// Деревья тайла лежат в TerrainCollider, который проверка препятствий пропускает: объекты не должны
        /// вставать в стволы, а листва под деревьями ложится вокруг них.
        /// </summary>
        private void AddTileTreeOccupancy(Terrain terrain, Vector3 center)
        {
            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            float reach = radius + TreeLitterOuter;

            foreach (TreeInstance tree in data.treeInstances)
            {
                Vector2 position = XZ(Vector3.Scale(tree.position, size) + origin);

                if ((position - XZ(center)).sqrMagnitude > reach * reach)
                    continue;

                AddOccupancy(position, TileTreeClearance * Mathf.Max(0.5f, tree.widthScale));
                _treePositions.Add(position);
            }
        }

        private void PlaceCategory(FlyPointEnvironmentProfile profile, FlyPointScatterCategory category, Vector3 center)
        {
            List<FlyPointPrefabEntry> entries = ValidEntries(category);

            if (entries.Count == 0)
                return;

            float minRadius = category.RadiusRange.x;
            float maxRadius = Mathf.Min(category.RadiusRange.y, radius - profile.EdgeBand * 0.5f);

            if (maxRadius <= minRadius)
                return;

            // вдоль тропинки — по её длине, а не по площади кольца
            float hectares = category.AlongPath
                ? (maxRadius - minRadius) * _site.PathHalfWidth * 2f / SquareMetersPerHectare
                : Mathf.PI * (maxRadius * maxRadius - minRadius * minRadius) / SquareMetersPerHectare;
            int clusterCount = StochasticRound(hectares * category.ClustersPerHectare *
                                               _context.Scale(category.TreeCoverScale, category.BareGroundScale));
            Transform group = CreateGroup(category.Name);
            List<Vector2> centers = new(clusterCount);
            float centerSpacing = category.ClusterRadius * 2f;

            for (int attempt = 0; attempt < clusterCount * CenterAttemptsPerCluster && centers.Count < clusterCount; attempt++)
            {
                Vector2 candidate = SampleClusterCenter(category, center, minRadius, maxRadius);

                if (!IsInRing(candidate, center, minRadius, maxRadius) ||
                    !IsFarFromAll(centers, candidate, centerSpacing) ||
                    !IsFree(candidate, CenterClearance) ||
                    !SuitsGround(category, candidate - XZ(center)) ||
                    OnRoad(category, candidate))
                {
                    continue;
                }

                centers.Add(candidate);
                PlaceCluster(category, entries, center, candidate, minRadius, maxRadius, group);
            }

            if (category.UnderSnow)
                BuryUnderSnow(group);

            if (!category.AsTerrainTrees)
                return;

            _treeGroupCenters.AddRange(centers);
            _treeGroupRadius = Mathf.Max(_treeGroupRadius, category.ClusterRadius);
        }

        // Зимой мелочь под снегом: скрываем ту, что лежит под ним по узору снега шейдера террейна (SnowCover), на тонком
        // краю пятна — часть. Раскладка (и ГСЧ точки) от поры года не меняется, камни просто прячутся.
        private void BuryUnderSnow(Transform group)
        {
            float snow = Seasons.Instance != null ? Seasons.Instance.SnowCover : 0f;

            if (snow <= 0f)
                return;

            foreach (Transform child in group)
            {
                Vector3 position = child.position;
                TryGetSurface(XZ(position), out _, out Vector3 normal);
                float cover = SnowCover.At(position.x, position.z, normal, SnowCover.NeutralGround, snow, 0.5f);
                float hash = Mathf.Repeat(Mathf.Sin(position.x * 12.9898f + position.z * 78.233f) * 43758.547f, 1f);

                if (hash < cover)
                    child.gameObject.SetActive(false);
            }
        }

        // Мелкие камни и камешки — только на вытоптанной земле, у тропинки — только у тропинки.
        private bool SuitsGround(FlyPointScatterCategory category, Vector2 local) =>
            !category.OnWornGround || _site.Worn(local) >= WornGroundThreshold;

        // Кусты, деревья и камни — не на дороге и не на тропинке: по осям дорог (RoadAreas), где они запечены, иначе по карте
        // земель GeoWorld (её клетка ~10 м); камешки вытоптанной земли (OnWornGround) лежат и на тропинке точки взлёта.
        // footprint — сколько объект занимает вокруг точки: бревно в 4 м с серединой у обочины лежало поперёк дороги.
        private static bool OnRoad(FlyPointScatterCategory category, Vector2 position, float footprint = 0f) =>
            !category.OnWornGround && IsRoadAt(position, footprint);

        private static bool IsRoadAt(Vector2 position, float footprint = 0f)
        {
            RoadAreas roads = RoadAreas.Instance;

            if (roads != null && roads.Covers(position.x, position.y))
            {
                float reach = footprint + DetailSpacing.RoadGap;
                return roads.CoverAt(position.x, position.y) > 0.5f ||
                       footprint > 0f && roads.EdgeDistanceAt(position.x, position.y, reach) < reach;
            }

            return GeoLandCover.TrySample(new Vector3(position.x, 0f, position.y), out GeoLandClass land) && LandGroups.IsRoad(land);
        }

        private void PaintTerrainTexture(Terrain terrain, FlyPointEnvironmentProfile profile, Vector3 center)
        {
            if (profile.GrassLayer == null || profile.SoilLayer == null || profile.GravelLayer == null)
                return;

            if (!Application.isPlaying)
                return;

            TerrainData data = EnsureRuntimeTerrainData(terrain);
            EnableLayerMaps(terrain);
            int grassLayer = EnsureTerrainLayer(data, profile.GrassLayer);
            int soilLayer = EnsureTerrainLayer(data, profile.SoilLayer);
            int gravelLayer = EnsureTerrainLayer(data, profile.GravelLayer);

            if (grassLayer < 0 || soilLayer < 0 || gravelLayer < 0)
                return;

            foreach (TerrainLayer layer in data.terrainLayers)
                UseTrilinearFiltering(layer);

            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            float extent = radius + profile.EdgeBand;
            int mapWidth = data.alphamapWidth;
            int mapHeight = data.alphamapHeight;
            int xMin = Mathf.Clamp(Mathf.FloorToInt((center.x - extent - origin.x) / size.x * mapWidth), 0, mapWidth - 1);
            int zMin = Mathf.Clamp(Mathf.FloorToInt((center.z - extent - origin.z) / size.z * mapHeight), 0, mapHeight - 1);
            int xMax = Mathf.Clamp(Mathf.CeilToInt((center.x + extent - origin.x) / size.x * mapWidth), xMin + 1, mapWidth);
            int zMax = Mathf.Clamp(Mathf.CeilToInt((center.z + extent - origin.z) / size.z * mapHeight), zMin + 1, mapHeight);
            int width = xMax - xMin;
            int height = zMax - zMin;
            float[,,] maps = data.GetAlphamaps(xMin, zMin, width, height);

            ApplyTextureBlend(
                maps,
                xMin,
                zMin,
                mapWidth,
                mapHeight,
                terrain,
                center,
                profile,
                grassLayer,
                soilLayer,
                gravelLayer);

            data.SetAlphamaps(xMin, zMin, maps);
            AlphaRegion alpha = new(maps, xMin, zMin, mapWidth, mapHeight, grassLayer, soilLayer);
            PaintTerrainDetails(profile, terrain, center, alpha);
            NatureRendererTerrain.Refresh(terrain);
            terrain.Flush();
        }

        private TerrainData EnsureRuntimeTerrainData(Terrain terrain)
        {
            foreach (RuntimeTerrainDataBackup backup in _runtimeTerrainBackups)
            {
                if (backup.Terrain == terrain)
                    return terrain.terrainData;
            }

            TerrainData original = terrain.terrainData;
            TerrainData runtime = Instantiate(original);
            // GeoTerrainTreeLoader resolves its GeoWorld tree data by TerrainData name.
            runtime.name = original.name;
            RaiseAlphamapResolution(runtime);
            TerrainCollider collider = terrain.GetComponent<TerrainCollider>();

            _runtimeTerrainBackups.Add(new RuntimeTerrainDataBackup(terrain, collider, original, runtime, AnchorOf(terrain)));
            RuntimeCopies.Add(runtime);
            BindTerrainData(terrain, collider, runtime);
            return runtime;
        }

        /// <summary>
        /// Тайл GeoWorld со спутником несёт карту смешивания 32×32 на 1 км (~31 м на тексель): трава, грунт
        /// и гравий вокруг точки взлёта ложились 30-метровыми кляксами и выглядели как низкое качество.
        /// В копии поднимаем разрешение до ~1 м на тексель и переносим прежние веса (билинейно).
        /// </summary>
        private static void RaiseAlphamapResolution(TerrainData data)
        {
            int previousSize = data.alphamapResolution;

            if (previousSize >= RuntimeAlphamapResolution)
                return;

            float[,,] previous = data.GetAlphamaps(0, 0, previousSize, previousSize);
            int layers = data.alphamapLayers;
            data.alphamapResolution = RuntimeAlphamapResolution;
            int size = data.alphamapResolution;
            float[,,] maps = new float[size, size, layers];
            float scale = (previousSize - 1) / (float)(size - 1);

            for (int z = 0; z < size; z++)
            {
                float fz = z * scale;
                int z0 = Mathf.Min((int)fz, previousSize - 2);
                float tz = fz - z0;

                for (int x = 0; x < size; x++)
                {
                    float fx = x * scale;
                    int x0 = Mathf.Min((int)fx, previousSize - 2);
                    float tx = fx - x0;

                    for (int layer = 0; layer < layers; layer++)
                    {
                        float bottom = Mathf.Lerp(previous[z0, x0, layer], previous[z0, x0 + 1, layer], tx);
                        float top = Mathf.Lerp(previous[z0 + 1, x0, layer], previous[z0 + 1, x0 + 1, layer], tx);
                        maps[z, x, layer] = Mathf.Lerp(bottom, top, tz);
                    }
                }
            }

            data.SetAlphamaps(0, 0, maps);
        }

        private void ReattachRuntimeTerrainData(Scene scene, LoadSceneMode mode)
        {
            for (int i = 0; i < _runtimeTerrainBackups.Count; i++)
            {
                RuntimeTerrainDataBackup backup = _runtimeTerrainBackups[i];

                if (backup.Terrain != null)
                    continue;

                // точка может лежать на нескольких тайлах: каждая копия возвращается на свой тайл, а не на тайл центра
                Terrain terrain = TerrainAt(backup.Anchor);

                if (terrain == null || _pendingReattach.Contains(backup.RuntimeData))
                    continue;

                StartCoroutine(ReattachWhenTreesLoaded(terrain, backup.RuntimeData));
            }
        }

        /// <summary>
        /// GeoTerrainTreeLoader после загрузки тайла асинхронно кладёт деревья в свой клон TerrainData. Подмена
        /// раньше отдавала ему нашу копию, а оригиналом запоминался ассет без деревьев — после снятия точки
        /// взлёта деревья тайла пропадали. Ждём загрузчик и только потом подменяем.
        /// </summary>
        private IEnumerator ReattachWhenTreesLoaded(Terrain terrain, TerrainData runtime)
        {
            _pendingReattach.Add(runtime);
            float deadline = Time.unscaledTime + TreeLoaderTimeout;

            // и виды деревьев (TileTreeSpecies): иначе они ложились в нашу копию, а в клоне загрузчика оставались
            // бук и тополь пакета — и вернулись бы с ним при следующей очистке точки
            while (terrain != null && !TreesReady(terrain) && Time.unscaledTime < deadline)
                yield return null;

            _pendingReattach.Remove(runtime);

            if (terrain == null || runtime == null)
                yield break;

            int index = _runtimeTerrainBackups.FindIndex(backup => backup.RuntimeData == runtime);

            if (index < 0 || _runtimeTerrainBackups[index].Terrain != null)
                yield break;

            TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
            RebindTileAssets(runtime, terrain.terrainData);
            _runtimeTerrainBackups[index] = new RuntimeTerrainDataBackup(terrain, collider, terrain.terrainData, runtime, _runtimeTerrainBackups[index].Anchor);
            BindTerrainData(terrain, collider, runtime);
            EnableLayerMaps(terrain);
            // заново загруженный тайл без деревьев пришёл без Nature Renderer, а у тайла с деревьями он мог начать с
            // прежних данных: траву точки рисует он, а не встроенный рендер Unity
            NatureRendererTerrain.Refresh(terrain);
        }

        /// <summary>
        /// Материал и шейдер террейна тайла приходят из бандла Addressables, а сборка оставляет в шейдере только
        /// варианты для материалов из бандлов: один спутниковый слой, без _NORMALMAP и _MASKMAP. Трава, грунт и
        /// гравий точки взлёта в билде рисовались мылом (в редакторе бандлов нет — там было нормально).
        /// Террейн получает копию игрового материала Resources/Terrain/TakeoffTerrainLit (URP Terrain Lit с этими
        /// ключевыми словами и instancing): его варианты попадают в билд. RestoreRuntimeTerrainData возвращает прежний.
        /// </summary>
        private void EnableLayerMaps(Terrain terrain)
        {
            Material original = terrain.materialTemplate;
            Material template = Resources.Load<Material>(TerrainMaterialResource);

            if (original == null || template == null)
                return;

            Material runtime = new(template) { name = original.name + " (takeoff)" };
            terrain.materialTemplate = runtime;
            _runtimeTerrainMaterials.Add((terrain, original, runtime));
        }

        /// <summary>
        /// Копия TerrainData пережила выгрузку тайла, но её слои и прототипы — ассеты из бандла тайла
        /// (спутниковый слой, трава, деревья), и при выгрузке бандла они уничтожены: без этого вернувшийся
        /// тайл рисовался серым. Берём их из только что загруженного TerrainData; добавленные генератором
        /// слои и прототипы травы стоят после тайловых и остаются как есть.
        /// </summary>
        private static void RebindTileAssets(TerrainData runtime, TerrainData fresh)
        {
            TerrainLayer[] layers = runtime.terrainLayers;
            TerrainLayer[] freshLayers = fresh.terrainLayers;

            for (int i = 0; i < freshLayers.Length && i < layers.Length; i++)
                layers[i] = freshLayers[i];

            runtime.terrainLayers = layers;

            DetailPrototype[] details = runtime.detailPrototypes;
            DetailPrototype[] freshDetails = fresh.detailPrototypes;

            for (int i = 0; i < freshDetails.Length && i < details.Length; i++)
                details[i] = freshDetails[i];

            runtime.detailPrototypes = details;

            // прототипы тайла — по индексам (после них TileTreeSpecies дописывает виды: их префабы из проекта, не из бандла)
            TreePrototype[] trees = runtime.treePrototypes;
            TreePrototype[] freshTrees = fresh.treePrototypes;

            for (int i = 0; i < freshTrees.Length && i < trees.Length; i++)
                trees[i] = freshTrees[i];

            runtime.treePrototypes = trees;
        }

        private void RestoreRuntimeTerrainData()
        {
            for (int i = _runtimeTerrainBackups.Count - 1; i >= 0; i--)
            {
                RuntimeTerrainDataBackup backup = _runtimeTerrainBackups[i];

                if (backup.Terrain != null && backup.OriginalData != null)
                {
                    BindTerrainData(backup.Terrain, backup.Collider, backup.OriginalData);
                    backup.Terrain.Flush();
                    NatureRendererTerrain.Restart(backup.Terrain);
                }

                // Стример травы Nature Renderer подгружает ячейки асинхронно и ещё несколько кадров читает прежнюю
                // копию: уничтоженная сразу, она сыпала MissingReferenceException из TerrainGrassSpawner.
                RuntimeCopies.Remove(backup.RuntimeData);
                Destroy(backup.RuntimeData, RuntimeDataDestroyDelay);
            }

            _runtimeTerrainBackups.Clear();

            foreach ((Terrain terrain, Material original, Material runtime) in _runtimeTerrainMaterials)
            {
                if (terrain != null && terrain.materialTemplate == runtime)
                    terrain.materialTemplate = original;

                Destroy(runtime);
            }

            _runtimeTerrainMaterials.Clear();
        }

        private static void BindTerrainData(Terrain terrain, TerrainCollider collider, TerrainData data)
        {
            terrain.terrainData = data;

            if (collider != null)
                collider.terrainData = data;

            NatureRendererTerrain.AssignTerrainData(terrain, data);
        }

        private static void UseTrilinearFiltering(TerrainLayer layer)
        {
            if (layer == null)
                return;

            foreach (Texture texture in new Texture[] { layer.diffuseTexture, layer.normalMapTexture, layer.maskMapTexture })
            {
                if (texture != null)
                    texture.filterMode = FilterMode.Trilinear;
            }
        }

        private static int EnsureTerrainLayer(TerrainData data, TerrainLayer layer)
        {
            TerrainLayer[] layers = data.terrainLayers;

            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] == layer)
                    return i;
            }

            TerrainLayer[] expanded = new TerrainLayer[layers.Length + 1];
            System.Array.Copy(layers, expanded, layers.Length);
            expanded[^1] = layer;
            data.terrainLayers = expanded;
            return expanded.Length - 1;
        }

        private void ApplyTextureBlend(
            float[,,] maps,
            int xOffset,
            int zOffset,
            int mapWidth,
            int mapHeight,
            Terrain terrain,
            Vector3 center,
            FlyPointEnvironmentProfile profile,
            int grassLayer,
            int soilLayer,
            int gravelLayer)
        {
            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            int layers = data.alphamapLayers;
            // На голой земле (пахота, стерня) грунта и вокруг площадки больше, а спутник с настоящим цветом поля
            // подходит ближе к точке: слой травы пятнами давал вокруг площадки зелёный «газон» посреди пашни.
            float soilThreshold = 1f - Mathf.Lerp(profile.SoilCoverage, profile.BareSoilCoverage, _context.BareGround);
            float satelliteInfluence = Mathf.Lerp(profile.SatelliteInfluence, BareSatelliteInfluence, _context.BareGround);
            Vector2 centerXZ = XZ(center);

            for (int z = 0; z < maps.GetLength(0); z++)
            {
                int terrainZ = zOffset + z;
                float worldZ = origin.z + (terrainZ + 0.5f) / mapHeight * size.z;

                for (int x = 0; x < maps.GetLength(1); x++)
                {
                    int terrainX = xOffset + x;
                    float worldX = origin.x + (terrainX + 0.5f) / mapWidth * size.x;
                    Vector2 local = new Vector2(worldX, worldZ) - centerXZ;
                    float distance = local.magnitude;

                    float boundary = radius + (_noise.Perlin(local.x, local.y, BoundaryNoiseScale, BoundaryChannel) - 0.5f) * 2f * profile.BoundaryNoise;
                    float detail = 1f - RuntimeNoise.SmoothStep(boundary - profile.BoundaryBlendWidth, boundary, distance);

                    // Площадка — неровное вытоптанное пятно, вытянутое к тропинке: в середине земля с россыпями
                    // камешков, к краю трава; от площадки уходит тропинка или колея.
                    float trample = _site.Trample(local);
                    float path = _site.Path(local);
                    float worn = Mathf.Max(trample, path);
                    float pebbles = RuntimeNoise.SmoothStep(0.4f, 0.75f, _noise.Perlin(local.x, local.y, GravelNoiseScale, GravelChannel));
                    float gravel = trample * trample * Mathf.Lerp(0.25f, 0.9f, pebbles);
                    float soilNoise = _noise.SignedFbm(local.x, local.y, profile.SoilNoiseScale, SoilChannel) * 0.5f + 0.5f;
                    float naturalSoil = RuntimeNoise.SmoothStep(soilThreshold - SoilBlendWidth, soilThreshold + SoilBlendWidth, soilNoise);
                    float soil = Mathf.Max(naturalSoil, Mathf.Max(trample * 0.85f, path * 0.8f)) * (1f - gravel);
                    float grass = Mathf.Max(0f, 1f - soil - gravel);
                    // немного спутника и у самой площадки, пятнами: крупные переходы цвета вместо ровного газона
                    float nearbySatellite = satelliteInfluence *
                                            Mathf.Lerp(NearSatelliteShare, 1f, RuntimeNoise.SmoothStep(profile.InnerRadius, radius * SatelliteRampEnd, distance)) *
                                            Mathf.Lerp(0.55f, 1.45f, _noise.Perlin(local.x, local.y, SatellitePatchScale, SatelliteChannel)) *
                                            (1f - worn);
                    float satellite = 1f - detail + detail * Mathf.Clamp01(nearbySatellite) * (1f - gravel);
                    float groundShare = 1f - satellite;
                    float sourceShare = 0f;
                    int fallbackSourceLayer = -1;

                    for (int layer = 0; layer < layers; layer++)
                    {
                        if (layer == grassLayer || layer == soilLayer || layer == gravelLayer)
                            continue;

                        fallbackSourceLayer = layer;
                        sourceShare += maps[z, x, layer];
                    }

                    if (sourceShare > 0.0001f)
                    {
                        for (int layer = 0; layer < layers; layer++)
                        {
                            if (layer == grassLayer || layer == soilLayer || layer == gravelLayer)
                                continue;

                            maps[z, x, layer] = maps[z, x, layer] / sourceShare * satellite;
                        }
                    }
                    else if (fallbackSourceLayer >= 0)
                    {
                        maps[z, x, fallbackSourceLayer] = satellite;
                    }

                    maps[z, x, grassLayer] = groundShare * grass;
                    maps[z, x, soilLayer] = groundShare * soil;
                    maps[z, x, gravelLayer] = groundShare * gravel;
                }
            }
        }

        private void PaintTerrainDetails(
            FlyPointEnvironmentProfile profile,
            Terrain terrain,
            Vector3 center,
            AlphaRegion alpha)
        {
            FlyPointDetailEntry[] profileDetails = profile.Details ?? System.Array.Empty<FlyPointDetailEntry>();
            List<FlyPointDetailEntry> entries = new();

            foreach (FlyPointDetailEntry entry in profileDetails)
            {
                if (entry != null && entry.Prototype != null)
                    entries.Add(entry);
            }

            if (entries.Count == 0)
                return;

            TerrainData data = terrain.terrainData;
            // трава мира на этом тайле (GroundDressing) — с его данных до генератора; за краем зоны кладётся она
            TerrainData source = OriginalDataOf(terrain);
            GameObject[] worldPrototypes = null;
            int[][,] worldCounts = null;
            bool world = GroundDressing.Instance != null && source != null &&
                         GroundDressing.Instance.TryGetWorldDetail(terrain, source, out worldPrototypes, out worldCounts);

            if (data.detailResolution < RuntimeDetailResolution)
                data.SetDetailResolution(RuntimeDetailResolution, DetailPatchResolution);

            data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);

            DetailPrototype[] previousPrototypes = data.detailPrototypes;
            List<DetailPrototype> prototypes = new(previousPrototypes);
            int[] detailLayers = new int[entries.Count];

            for (int i = 0; i < entries.Count; i++)
            {
                detailLayers[i] = EnsureDetailPrototype(prototypes, entries[i]);
            }

            data.detailPrototypes = prototypes.ToArray();

            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            int resolution = data.detailResolution;
            float extent = radius + profile.EdgeBand;
            int xMin = Mathf.Clamp(Mathf.FloorToInt((center.x - extent - origin.x) / size.x * resolution), 0, resolution - 1);
            int zMin = Mathf.Clamp(Mathf.FloorToInt((center.z - extent - origin.z) / size.z * resolution), 0, resolution - 1);
            int xMax = Mathf.Clamp(Mathf.CeilToInt((center.x + extent - origin.x) / size.x * resolution), xMin + 1, resolution);
            int zMax = Mathf.Clamp(Mathf.CeilToInt((center.z + extent - origin.z) / size.z * resolution), zMin + 1, resolution);
            int width = xMax - xMin;
            int height = zMax - zMin;
            DetailGrid grid = BuildDetailGrid(terrain, center, profile, alpha, xMin, zMin, width, height, resolution);
            int[,] full = world ? new int[resolution, resolution] : null;
            // пни, брёвна, штабеля — после всех слоёв: разводятся вместе (KeepApartLayers)
            List<ApartLayer> apart = new();

            for (int i = 0; i < entries.Count; i++)
            {
                int[,] counts = new int[height, width];
                ApplyDetailLayer(counts, grid, entries[i], i);
                int worldLayer = world ? System.Array.IndexOf(worldPrototypes, entries[i].Prototype) : -1;

                if (worldLayer < 0)
                {
                    if (entries[i].KeepApart)
                        apart.Add(new ApartLayer(i, counts, xMin, zMin));
                    else
                        data.SetDetailLayer(xMin, zMin, detailLayers[i], counts);

                    continue;
                }

                // весь тайл — трава мира в сетке 1 м, в зоне — своя, к краю зоны всё больше травы мира
                int[,] target = entries[i].KeepApart ? new int[resolution, resolution] : full;
                int channel = _seed + i * 31;
                FillWorldDetail(target, worldCounts[worldLayer], channel);
                BlendZoneDetail(target, counts, grid, channel + 1);

                if (entries[i].KeepApart)
                    apart.Add(new ApartLayer(i, target, 0, 0));
                else
                    data.SetDetailLayer(0, 0, detailLayers[i], target);
            }

            KeepApartLayers(terrain, apart, entries, resolution);

            foreach (ApartLayer layer in apart)
                data.SetDetailLayer(layer.X, layer.Z, detailLayers[layer.Entry], layer.Counts);

            terrain.drawTreesAndFoliage = true;
            terrain.detailObjectDensity = Mathf.Max(terrain.detailObjectDensity, 1f);
            terrain.detailObjectDistance = Mathf.Max(terrain.detailObjectDistance, radius);
        }

        // Пни, брёвна, штабеля (KeepApart) — по одному в ячейке, не у стволов деревьев, камней и кустов, не друг у друга и не
        // у дорог (DetailSpacing), и в зоне, и в траве мира за её краем (её 2-метровая ячейка делится на четыре по 1 м).
        private void KeepApartLayers(Terrain terrain, List<ApartLayer> layers, List<FlyPointDetailEntry> entries, int resolution)
        {
            if (layers.Count == 0)
                return;

            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            float cellX = size.x / resolution;
            float cellZ = size.z / resolution;
            List<DetailSpacing.Candidate> candidates = new();

            for (int l = 0; l < layers.Count; l++)
            {
                ApartLayer layer = layers[l];
                FlyPointDetailEntry entry = entries[layer.Entry];
                float radius = DetailSpacing.Radius(entry.Prototype) * entry.WidthRange.y;
                int rows = layer.Counts.GetLength(0);
                int columns = layer.Counts.GetLength(1);

                for (int z = 0; z < rows; z++)
                {
                    for (int x = 0; x < columns; x++)
                    {
                        if (layer.Counts[z, x] == 0)
                            continue;

                        layer.Counts[z, x] = 0;
                        int terrainX = layer.X + x;
                        int terrainZ = layer.Z + z;
                        candidates.Add(new DetailSpacing.Candidate
                        {
                            Position = new Vector2(origin.x + (terrainX + 0.5f) * cellX, origin.z + (terrainZ + 0.5f) * cellZ),
                            Radius = radius,
                            Priority = RuntimeNoise.Hash01(terrainX, terrainZ, _seed + layer.Entry * 104729),
                            Layer = l,
                            Cell = z * columns + x
                        });
                    }
                }
            }

            List<Vector3> obstacles = new();

            foreach (TreeInstance tree in data.treeInstances)
            {
                Vector3 position = Vector3.Scale(tree.position, size) + origin;
                obstacles.Add(new Vector3(position.x, position.z, DetailSpacing.TrunkRadius));
            }

            foreach (Vector2 tree in _treePositions)
                obstacles.Add(new Vector3(tree.x, tree.y, DetailSpacing.TrunkRadius));

            foreach (OccupancyCircle circle in _occupancyCircles)
                obstacles.Add(new Vector3(circle.Center.x, circle.Center.y, circle.Radius));

            RoadAreas roads = RoadAreas.Instance;
            RoadAreas.Segment[] segments = roads != null
                ? roads.SegmentsIn(new Rect(origin.x - ApartRoadReach, origin.z - ApartRoadReach, size.x + ApartRoadReach * 2f, size.z + ApartRoadReach * 2f))
                : null;
            float slop = 0.5f * Mathf.Sqrt(cellX * cellX + cellZ * cellZ);

            foreach (DetailSpacing.Candidate kept in DetailSpacing.Resolve(candidates, slop, obstacles, segments))
            {
                int[,] counts = layers[kept.Layer].Counts;
                int columns = counts.GetLength(1);
                counts[kept.Cell / columns, kept.Cell % columns] = 1;
            }
        }

        private readonly struct ApartLayer
        {
            public ApartLayer(int entry, int[,] counts, int x, int z)
            {
                Entry = entry;
                Counts = counts;
                X = x;
                Z = z;
            }

            public int Entry { get; }
            public int[,] Counts { get; }
            // где слой лежит в сетке деталей террейна
            public int X { get; }
            public int Z { get; }
        }

        private TerrainData OriginalDataOf(Terrain terrain)
        {
            foreach (RuntimeTerrainDataBackup backup in _runtimeTerrainBackups)
            {
                if (backup.Terrain == terrain)
                    return backup.OriginalData;
            }

            return null;
        }

        // Трава мира (сетка ~2 м) в сетку генератора (~1 м): каждая ячейка получает свою долю родительской.
        private static void FillWorldDetail(int[,] target, int[,] world, int channel)
        {
            int size = target.GetLength(0);
            int worldSize = world.GetLength(0);
            float share = (float)worldSize * worldSize / ((float)size * size);

            for (int z = 0; z < size; z++)
            {
                int worldZ = Mathf.Min(z * worldSize / size, worldSize - 1);

                for (int x = 0; x < size; x++)
                {
                    float expected = world[worldZ, Mathf.Min(x * worldSize / size, worldSize - 1)] * share;
                    int whole = (int)expected;
                    target[z, x] = whole + (RuntimeNoise.Hash01(x, z, channel) < expected - whole ? 1 : 0);
                }
            }
        }

        // В зоне точки: своя трава плюс трава мира, которой тем меньше, чем глубже в зону (EdgeFade), и нет под камнями.
        private static void BlendZoneDetail(int[,] target, int[,] zone, DetailGrid grid, int channel)
        {
            for (int z = 0; z < grid.Height; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int targetZ = grid.ZMin + z;
                    int targetX = grid.XMin + x;
                    float world = grid.Blocked[z, x] > 0f ? 0f : target[targetZ, targetX] * (1f - grid.EdgeFade[z, x]);
                    int whole = (int)world;
                    int extra = RuntimeNoise.Hash01(targetX, targetZ, channel) < world - whole ? 1 : 0;
                    target[targetZ, targetX] = Mathf.Min(zone[z, x] + whole + extra, MaxDetailPerCell);
                }
            }
        }

        private int EnsureDetailPrototype(List<DetailPrototype> prototypes, FlyPointDetailEntry entry)
        {
            for (int i = 0; i < prototypes.Count; i++)
            {
                DetailPrototype prototype = prototypes[i];

                // префаб с LODGroup Nature Renderer подменяет его дочерним LOD0 (ReplaceUnsupportedDetails), а рисует по корню
                if (prototype.prototype != null && prototype.prototype.transform.root.gameObject == entry.Prototype)
                    return i;
            }

            DetailPrototype newPrototype = new()
            {
                prototype = entry.Prototype,
                usePrototypeMesh = true,
                renderMode = DetailRenderMode.VertexLit,
                useInstancing = true,
                minWidth = entry.WidthRange.x,
                maxWidth = entry.WidthRange.y,
                minHeight = entry.HeightRange.x,
                maxHeight = entry.HeightRange.y,
                healthyColor = entry.HealthyColor,
                dryColor = entry.DryColor,
                noiseSpread = entry.NoiseSpread,
                noiseSeed = _seed,
                alignToGround = entry.PrototypeAlignToGround
            };

            prototypes.Add(newPrototype);
            return prototypes.Count - 1;
        }

        /// <summary>
        /// Общее для всех видов травы на ячейку: веса травы и земли из только что покрашенной карты, затухание к краю
        /// зоны, вытоптанность, занятость камнями и деревьями, близость деревьев. Считается один раз — при сетке ~1 м
        /// это ~200 тыс. ячеек, а GetAlphamaps на ячейку и поиск по занятости на каждый вид были бы в разы дороже.
        /// </summary>
        private DetailGrid BuildDetailGrid(
            Terrain terrain,
            Vector3 center,
            FlyPointEnvironmentProfile profile,
            AlphaRegion alpha,
            int xMin,
            int zMin,
            int width,
            int height,
            int resolution)
        {
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            DetailGrid grid = new(xMin, zMin, width, height, size.x / resolution, size.z / resolution, origin, XZ(center));
            int alphaWidth = alpha.Maps.GetLength(1);
            int alphaHeight = alpha.Maps.GetLength(0);

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    Vector2 local = grid.Local(x, z);
                    Vector2 world = local + grid.Center;
                    int ax = Mathf.FloorToInt((world.x - origin.x) / size.x * alpha.MapWidth) - alpha.XMin;
                    int az = Mathf.FloorToInt((world.y - origin.z) / size.z * alpha.MapHeight) - alpha.ZMin;

                    if (ax >= 0 && ax < alphaWidth && az >= 0 && az < alphaHeight)
                    {
                        grid.Grass[z, x] = alpha.Maps[az, ax, alpha.GrassLayer];
                        grid.Soil[z, x] = alpha.Maps[az, ax, alpha.SoilLayer];
                    }

                    grid.EdgeFade[z, x] = RuntimeEdgeFade(local, profile);
                    // вытоптано неровно: проплешины и примятые островки, а не ровный градиент
                    float worn = _site.Worn(local);
                    grid.Worn[z, x] = worn > 0f
                        ? Mathf.Clamp01(worn * Mathf.Lerp(0.7f, 1.2f, _noise.Perlin(local.x, local.y, 1.6f, TrampleDetailChannel)))
                        : 0f;
                }
            }

            foreach (OccupancyCircle circle in _occupancyCircles)
                grid.Rasterize(circle.Center, circle.Radius, (cellDistance, circleRadius) => cellDistance < circleRadius ? 1f : 0f, grid.Blocked);

            foreach (Vector2 tree in _treePositions)
                grid.Rasterize(tree, TreeLitterOuter, (cellDistance, _) => 1f - RuntimeNoise.SmoothStep(TreeLitterInner, TreeLitterOuter, cellDistance), grid.TreeNear);

            ReadLand(grid, width, height);
            ReadRoads(grid, width, height);
            ReadCraters(grid, width, height);
            ReadSatelliteStates(grid, terrain, profile);
            ReadSnow(grid, terrain);

            if (profile.Details != null && System.Array.Exists(profile.Details, entry => entry != null && entry.AlignToGround > 0f))
                ReadSteepness(grid, terrain);

            return grid;
        }

        // Крутизна в ячейках — для лежащего по склону (опад, ветки, камешки не кладутся круче
        // FlyPointDetailEntry.MaxGroundSlope); через 4 клетки (под экраном загрузки), между ними — ближайшая.
        private static void ReadSteepness(DetailGrid grid, Terrain terrain)
        {
            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            grid.Steepness = new float[grid.Height, grid.Width];
            float steep = 0f;

            for (int z = 0; z < grid.Height; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    if ((x & 3) == 0)
                    {
                        Vector2 world = grid.Local(x, z) + grid.Center;
                        steep = data.GetSteepness(Mathf.Clamp01((world.x - origin.x) / size.x), Mathf.Clamp01((world.y - origin.z) / size.z));
                    }

                    grid.Steepness[z, x] = steep;
                }
            }
        }

        // Зимой — сколько земли в ячейке снег оставил голой, по узору снега шейдера террейна (SnowCover), как у травы мира.
        private static void ReadSnow(DetailGrid grid, Terrain terrain)
        {
            float snow = Seasons.Instance != null ? Seasons.Instance.SnowCover : 0f;

            if (snow <= 0f)
                return;

            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            grid.SnowFree = new float[grid.Height, grid.Width];

            for (int z = 0; z < grid.Height; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    Vector2 world = grid.Local(x, z) + grid.Center;
                    Vector3 normal = data.GetInterpolatedNormal((world.x - origin.x) / size.x, (world.y - origin.z) / size.z);
                    grid.SnowFree[z, x] = 1f - SnowCover.At(world.x, world.y, normal, SnowCover.NeutralGround, snow, grid.CellX);
                }
            }
        }

        // Что на земле по спутнику тайла (SatelliteStates) — как у травы мира (GroundDressing): та же копия 512² и те же
        // доли, в тех же u, v по тайлу, поэтому на краю зоны виды травы не меняются. Каждый тайл красится один раз за
        // генерацию, поэтому доли не кэшируются (кэш ни разу не попадал и держал ~5 МБ на тайл всю тренировку).
        private void ReadSatelliteStates(DetailGrid grid, Terrain terrain, FlyPointEnvironmentProfile profile)
        {
            TerrainLayer layer = SatelliteLayer(terrain.terrainData, profile);

            if (layer == null)
                return;

            Texture2D copy = SmallReadableCopy(layer.diffuseTexture);
            Vector4[] states = SatelliteStates.Classify(copy.GetPixels32(), SatelliteCopySize, out float[] arable);
            Destroy(copy);

            if (states == null)
                return;

            TerrainData data = terrain.terrainData;
            Vector3 size = data.size;
            // поле, которое карта зовёт лугом или голой землёй, и край поля без зелени растут как поле (как у травы мира)
            float[] maybeField = grid.HasLand ? LandGroups.MaybeFieldShare(grid.Land) : null;
            float[] edge = grid.HasLand ? LandGroups.FieldEdgeShare(grid.Land, grid.LandCountX, grid.LandCountZ) : null;
            float steep = 0f;

            for (int z = 0; z < grid.Height; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    Vector2 world = grid.Local(x, z) + grid.Center;
                    float u = (world.x - grid.Origin.x) / size.x;
                    float v = (world.y - grid.Origin.z) / size.z;
                    grid.Satellite[z, x] = SatelliteStates.Sample(states, SatelliteCopySize, u, v);

                    if (maybeField == null)
                        continue;

                    // крутизна — через 4 клетки (под экраном загрузки), между ними — ближайшая
                    if ((x & 3) == 0)
                        steep = data.GetSteepness(Mathf.Clamp01(u), Mathf.Clamp01(v));

                    grid.LandNode(x, z, out float gx, out float gz);
                    float field = SatelliteStates.Sample(arable, SatelliteCopySize, u, v) *
                                  (1f - RuntimeNoise.SmoothStep(4.5f, 8f, steep));
                    grid.ToField[z, x] = Mathf.Clamp01(
                        LandGroups.Sample(maybeField, grid.LandCountX, grid.LandCountZ, gx, gz) * field +
                        LandGroups.Sample(edge, grid.LandCountX, grid.LandCountZ, gx, gz) * (1f - grid.Satellite[z, x].x));

                    // брошенное поле — уже не поле
                    if (grid.Abandoned != null)
                        grid.ToField[z, x] *= 1f - LandGroups.Sample(grid.Abandoned, grid.LandCountX, grid.LandCountZ, gx, gz);
                }
            }

            grid.HasSatellite = true;
        }

        private static TerrainLayer SatelliteLayer(TerrainData data, FlyPointEnvironmentProfile profile)
        {
            foreach (TerrainLayer layer in data.terrainLayers)
            {
                if (layer != null && layer.diffuseTexture != null &&
                    layer != profile.GrassLayer && layer != profile.SoilLayer && layer != profile.GravelLayer)
                {
                    return layer;
                }
            }

            return null;
        }

        // Земля зоны по карте земель GeoWorld: классы в узлах через HardGroundStep (её ячейка ~10 м), от шага до края
        // зоны и на шаг за ним. Вода, дороги, здания и рельсы — без травы (у точки возле реки трава росла прямо в
        // воде), доля леса — для листвы, сами классы — для видов травы по группам земли (ApplyDetailLayer).
        private static void ReadLand(DetailGrid grid, int width, int height)
        {
            Vector2 first = grid.Local(0, 0) + grid.Center;
            Vector2 last = grid.Local(width - 1, height - 1) + grid.Center;

            if (GeoLandCover.At(first.x, first.y) == null && GeoLandCover.At(last.x, last.y) == null)
                return;

            Vector2 start = first - Vector2.one * HardGroundStep;
            int countX = Mathf.CeilToInt((last.x - start.x) / HardGroundStep) + 2;
            int countZ = Mathf.CeilToInt((last.y - start.y) / HardGroundStep) + 2;
            byte[] land = new byte[countX * countZ];

            for (int j = 0; j < countZ; j++)
            {
                for (int i = 0; i < countX; i++)
                {
                    Vector3 point = new(start.x + i * HardGroundStep, 0f, start.y + j * HardGroundStep);

                    if (GeoLandCover.TrySample(point, out GeoLandClass value))
                    {
                        // где шли бои (воронки, CraterField), поле брошено: пашня — залежь, растёт как луг, без стерни — как
                        // у травы тайлов (GroundDressing)
                        if (CraterField.AbandonedAt(point.x, point.z))
                        {
                            grid.Abandoned ??= new float[countX * countZ];
                            grid.Abandoned[j * countX + i] = 1f;

                            if (value == GeoLandClass.Cropland)
                                value = GeoLandClass.Grass;
                        }

                        land[j * countX + i] = (byte)value;
                    }
                }
            }

            grid.SetLand(land, start, countX, countZ, HardGroundStep);
            // дороги, известные осями (RoadAreas), вырезает ReadRoads точно: их клетки по 10 м здесь не в счёт
            RoadAreas roads = RoadAreas.Instance;
            float[] hard = LandGroups.Hard(land, roads == null || !roads.Covers(grid.Center.x, grid.Center.y));
            float[] forest = LandGroups.Share(land, FlyPointLandGroup.Forest);

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    grid.LandNode(x, z, out float gx, out float gz);

                    if (LandGroups.Sample(hard, countX, countZ, gx, gz) >= HardGroundShare)
                        grid.Blocked[z, x] = 1f;

                    grid.ForestLand[z, x] = LandGroups.Sample(forest, countX, countZ, gx, gz);
                }
            }
        }

        // Воронки и окопы в ячейках зоны (CraterField), как у травы тайлов (GroundDressing): в чаше свежей воронки, в прудке
        // и во рву свежего окопа травы нет; старые заросли — трава в них растёт.
        private static readonly List<int> FoundCraters = new();

        private static void ReadCraters(DetailGrid grid, int width, int height)
        {
            CraterField field = CraterField.Instance;

            if (field == null)
                return;

            Vector2 first = grid.Local(0, 0) + grid.Center;
            Vector2 last = grid.Local(width - 1, height - 1) + grid.Center;
            Rect zone = Rect.MinMaxRect(first.x, first.y, last.x, last.y);
            field.Query(zone, FoundCraters);

            foreach (int i in FoundCraters)
            {
                CraterField.Crater c = field.craters[i];

                if (!c.water && c.age >= 0.35f)
                    continue;

                grid.Rasterize(new Vector2(c.x, c.z), c.diameter * 0.5f, (cellDistance, radius) => cellDistance < radius ? 1f : 0f, grid.Blocked);
            }

            field.QueryTrenches(zone, FoundCraters);

            foreach (int i in FoundCraters)
            {
                CraterField.Trench t = field.trenches[i];

                if (t.points == null)
                    continue;

                // the trench is a mesh out to the end of its lip, the terrain under it cut lower: nothing grows or stands there
                float half = new GeoWorld.Editor.GeoTrenchSection(t.width, t.depth, t.age, GeoWorld.Editor.GeoTrenchSection.LipWidth(1f)).CutTo(1f);

                for (int k = 0; k + 3 < t.points.Length; k += 2)
                {
                    Vector2 a = new(t.points[k], t.points[k + 1]), b = new(t.points[k + 2], t.points[k + 3]);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / Mathf.Max(0.25f, half)));

                    for (int s = 0; s <= steps; s++)
                        grid.Rasterize(Vector2.Lerp(a, b, s / (float)steps), half, (cellDistance, radius) => cellDistance < radius ? 1f : 0f, grid.Blocked);
                }
            }
        }

        // Полотно дорог по осям (RoadAreas) в ячейках зоны: голо на нём и ClearMargin за краем, к траве — за Fade.
        private static void ReadRoads(DetailGrid grid, int width, int height)
        {
            RoadAreas roads = RoadAreas.Instance;

            if (roads == null)
                return;

            Vector2 first = grid.Local(0, 0) + grid.Center;
            Vector2 last = grid.Local(width - 1, height - 1) + grid.Center;
            RoadAreas.Segment[] segments = roads.SegmentsIn(Rect.MinMaxRect(first.x, first.y, last.x, last.y));

            if (segments.Length == 0)
                return;

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float cover = RoadAreas.Cover(segments, grid.Local(x, z) + grid.Center);
                    grid.Road[z, x] = cover;

                    if (cover >= 0.99f)
                        grid.Blocked[z, x] = 1f;
                }
            }
        }

        private void ApplyDetailLayer(int[,] counts, DetailGrid grid, FlyPointDetailEntry entry, int entryIndex)
        {
            // обломки у руин площадка сама не кладёт: только мир за её краем (GroundDressing, у стен руин)
            if (entry.NearRuins)
                return;

            // в поле, в лесу и на голой земле — разная трава и цветы (профиль: TreeCoverScale, BareGroundScale); по поре года
            // и снегу — как у травы тайлов (Seasons; под снегом — по его узору, SnowFree)
            Seasons seasons = Seasons.Instance;
            Season current = seasons != null ? seasons.Current : Season.Summer;
            float season = entry.SeasonScale(current);
            float[,] snowFree = entry.HiddenBySnow(current) ? grid.SnowFree : null;
            bool useSatellite = entry.UsesSatelliteScales;
            FlyPointSatelliteScales satelliteScales = entry.SatelliteScales;
            // старая запись — «голое» на всю точку (AnalyzeSite), новая — по спутнику в каждой ячейке, как у травы мира
            float siteScale = useSatellite ? _context.TreeScale(entry.TreeCoverScale) : _context.Scale(entry.TreeCoverScale, entry.BareGroundScale);
            float cellArea = grid.CellX * grid.CellZ * siteScale * season;
            float minRadiusSqr = entry.MinRadius * entry.MinRadius;
            float fadeWidth = Mathf.Max(8f, entry.MaxRadius * 0.3f);
            float worn = 1f - entry.TrampleTolerance;
            int hashChannel = _seed + entryIndex * 7919;
            // и поклеточно по карте земель: луг, поле и его край, лес, болото и берег, посёлок, дороги, залежь (LandScales)
            FlyPointLandScales landScales = entry.EffectiveLandScales;
            float[] landLattice = grid.HasLand ? LandGroups.EntryScales(grid.Land, grid.LandCountX, grid.LandCountZ, landScales, grid.Abandoned) : null;

            for (int z = 0; z < grid.Height; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    if (grid.Blocked[z, x] > 0f || grid.Worn[z, x] >= WornCore)
                        continue;

                    Vector2 local = grid.Local(x, z);
                    float distanceSqr = local.sqrMagnitude;

                    if (distanceSqr < minRadiusSqr)
                        continue;

                    float land = landScales.Meadow;

                    if (landLattice != null)
                    {
                        grid.LandNode(x, z, out float gx, out float gz);
                        land = LandGroups.Sample(landLattice, grid.LandCountX, grid.LandCountZ, gx, gz);
                    }

                    if (useSatellite)
                        land = Mathf.Lerp(land, landScales.Field, grid.ToField[z, x]);

                    if (land <= 0.0001f)
                        continue;

                    if (useSatellite)
                    {
                        land *= satelliteScales.Of(grid.SatelliteAt(x, z));

                        if (land <= 0.0001f)
                            continue;
                    }

                    // листва — под деревьями на любой земле, в лесу по карте почти везде; трава — по весам травы и земли
                    float cover = entry.UnderTrees
                        ? Mathf.Max(grid.TreeNear[z, x], grid.ForestLand[z, x] * LandGroups.ForestLitterCover)
                        : grid.Grass[z, x] + grid.Soil[z, x] * entry.SoilDensity;
                    float expected = entry.DensityPerSquareMeter * cellArea * cover * grid.EdgeFade[z, x] *
                                     (1f - grid.Worn[z, x] * worn) * land * (1f - grid.Road[z, x]);

                    if (snowFree != null)
                        expected *= snowFree[z, x];

                    if (entry.AlignToGround > 0f && grid.Steepness != null)
                        expected *= FlyPointDetailEntry.GroundSlopeScale(grid.Steepness[z, x]);

                    if (entry.MaxRadius > 0f)
                        expected *= 1f - RuntimeNoise.SmoothStep(entry.MaxRadius - fadeWidth, entry.MaxRadius, Mathf.Sqrt(distanceSqr));

                    if (expected <= 0.0001f)
                        continue;

                    expected *= DetailPatchMask(entry, local, entryIndex) * DetailClumpMask(entry, local, entryIndex);

                    if (expected <= 0f)
                        continue;

                    int terrainX = grid.XMin + x;
                    int terrainZ = grid.ZMin + z;
                    int whole = Mathf.FloorToInt(expected);
                    int extra = RuntimeNoise.Hash01(terrainX, terrainZ, hashChannel) < expected - whole ? 1 : 0;
                    counts[z, x] = Mathf.Min(whole + extra, MaxDetailPerCell);
                }
            }
        }

        private float DetailPatchMask(FlyPointDetailEntry entry, Vector2 local, int layer)
        {
            if (entry.PatchThreshold <= 0f)
                return 1f;

            float value = _noise.Contrasted(local.x, local.y, entry.PatchScale, DetailNoiseChannel + layer);
            return RuntimeNoise.SmoothStep(entry.PatchThreshold, entry.PatchThreshold + DetailPatchBlend, value);
        }

        // Кочки: плотность собирается в куртины в пару метров, средняя почти не меняется.
        private float DetailClumpMask(FlyPointDetailEntry entry, Vector2 local, int layer)
        {
            if (entry.ClumpStrength <= 0f)
                return 1f;

            float value = _noise.Contrasted(local.x, local.y, entry.ClumpScale, DetailClumpChannel + layer);
            return Mathf.Lerp(1f, RuntimeNoise.SmoothStep(0.3f, 0.8f, value) * 2f, entry.ClumpStrength);
        }

        private float RuntimeEdgeFade(Vector2 local, FlyPointEnvironmentProfile profile)
        {
            float distance = local.magnitude;
            float boundary = radius + (_noise.Perlin(local.x, local.y, BoundaryNoiseScale, BoundaryChannel) - 0.5f) * 2f * profile.BoundaryNoise;
            return 1f - RuntimeNoise.SmoothStep(boundary - profile.BoundaryBlendWidth, boundary, distance);
        }

        private void PlaceCluster(
            FlyPointScatterCategory category,
            List<FlyPointPrefabEntry> entries,
            Vector3 center,
            Vector2 clusterCenter,
            float minRadius,
            float maxRadius,
            Transform group)
        {
            int count = _random.Next(category.ClusterSize.x, category.ClusterSize.y + 1);

            for (int member = 0; member < count && _propCount < maxPropInstances; member++)
            {
                for (int attempt = 0; attempt < MemberAttempts; attempt++)
                {
                    Vector2 position = member == 0 && attempt == 0
                        ? clusterCenter
                        : clusterCenter + Gaussian() * category.ClusterRadius * 0.5f;

                    if (!IsInRing(position, center, minRadius, maxRadius) || !SuitsGround(category, position - XZ(center)) ||
                        OnRoad(category, position))
                        continue;

                    FlyPointPrefabEntry entry = PickWeighted(entries);
                    float scale = PickScale(entry, member == 0);
                    PrefabFootprint footprint = FootprintOf(entry.Prefab);
                    float footprintRadius = footprint.Radius * scale * category.Spacing;

                    // кроны деревьев над дорогой — как в натуре; пень, бревно, камень, куст на ней — нет
                    if (!IsFree(position, footprintRadius) ||
                        OnRoad(category, position, category.AsTerrainTrees ? 0f : footprint.Radius * scale) ||
                        !TryGetSurface(position, out Vector3 surface, out Vector3 normal) ||
                        HitsSceneObstacle(position, surface.y, footprintRadius, footprint.Height * scale))
                    {
                        continue;
                    }

                    Spawn(entry, footprint, surface, normal, scale, group, category.StripColliders);
                    AddOccupancy(position, footprintRadius);

                    if (category.AsTerrainTrees)
                    {
                        _treePositions.Add(position);
                        Vector2 crown = ForestFloor.Crown(entry.Prefab);
                        _treeCrowns.Add(new Vector4(position.x, position.y, crown.x * scale, crown.y));
                    }

                    break;
                }
            }
        }

        private void PlaceDetails(FlyPointEnvironmentProfile profile, Vector3 center)
        {
            FlyPointDetailEntry[] details = profile.Details ?? System.Array.Empty<FlyPointDetailEntry>();

            foreach (FlyPointDetailEntry entry in details)
            {
                if (entry == null || entry.Prototype == null || entry.NearRuins || _detailCount >= maxDetailInstances)
                    continue;

                float area = Mathf.PI * Mathf.Max(0f, radius * radius - entry.MinRadius * entry.MinRadius);
                int targetCount = Mathf.Min(
                    maxDetailInstances - _detailCount,
                    Mathf.Max(0, StochasticRound(area * entry.DensityPerSquareMeter * detailBudgetMultiplier)));
                int attempts = targetCount * DetailPlacementAttemptsMultiplier;
                Transform group = CreateGroup(entry.Prototype.name);

                for (int i = 0; i < attempts && _detailCount < maxDetailInstances && targetCount > 0; i++)
                {
                    float distance = Mathf.Sqrt(Mathf.Lerp(entry.MinRadius * entry.MinRadius, radius * radius, NextFloat()));
                    Vector2 position = XZ(center) + RandomDirection() * distance;
                    Vector2 local = position - XZ(center);

                    if (!DetailPatchAllows(entry, local) || IsRoadAt(position) || _site.Worn(local) >= WornCore)
                        continue;

                    if (!IsFree(position, 0f) || !TryGetSurface(position, out Vector3 surface, out Vector3 normal))
                        continue;

                    float scaleX = Mathf.Lerp(entry.WidthRange.x, entry.WidthRange.y, NextFloat());
                    float scaleY = Mathf.Lerp(entry.HeightRange.x, entry.HeightRange.y, NextFloat());
                    GameObject instance = Instantiate(entry.Prototype, surface, Quaternion.FromToRotation(Vector3.up, normal), group);
                    instance.transform.Rotate(0f, NextFloat() * 360f, 0f, Space.Self);
                    instance.transform.localScale = new Vector3(scaleX, scaleY, scaleX);
                    ExtendLodCulling(instance);
                    _detailCount++;
                }
            }
        }

        private void Spawn(FlyPointPrefabEntry entry, PrefabFootprint footprint, Vector3 surface, Vector3 normal, float scale, Transform group, bool stripColliders)
        {
            Quaternion alignment = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, normal), entry.AlignToNormal);
            Quaternion variation = Quaternion.Euler(
                RandomRange(-entry.RandomTilt, entry.RandomTilt),
                NextFloat() * 360f,
                RandomRange(-entry.RandomTilt, entry.RandomTilt));
            float sink = footprint.Height * scale * entry.SinkFraction;

            GameObject instance = Instantiate(entry.Prefab, surface - Vector3.up * sink, alignment * variation, group);
            instance.transform.localScale = entry.Prefab.transform.localScale * scale;
            ExtendLodCulling(instance);
            UseSeasonalFoliage(instance);
            _propCount++;

            // камешкам коллайдеры ни к чему: дрон о них не бьётся, а физике лишняя работа
            if (!stripColliders)
                return;

            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
                Destroy(collider);
        }

        // Кусты точки взлёта (ива, клён) были на шейдере листвы NatureManufacture без поры года и зимой зеленели на снегу.
        // Их листве — копия материала на его варианте с порой года, как у травы: осенью буреет, зимой — сухая, весной свежая.
        // Голым, как дерево, куст не делаем: листва куста — весь куст, он пропадал бы целиком, а коллайдер оставался.
        private void UseSeasonalFoliage(GameObject instance)
        {
            Shader seasonal = null;

            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    Material source = materials[i];

                    if (source == null || source.shader == null || source.shader.name != NatureManufactureFoliageShader)
                        continue;

                    if (!_seasonalMaterials.TryGetValue(source, out Material copy))
                    {
                        seasonal ??= Shader.Find(SeasonalFoliageShader);

                        if (seasonal == null)
                            return;

                        copy = new Material(source) { name = source.name + " (seasons)", shader = seasonal };
                        copy.SetFloat(PuddleSinkId, SeasonalBushPuddleSink);
                        copy.SetFloat(SmoothnessRemapMaxId, Mathf.Min(copy.GetFloat(SmoothnessRemapMaxId), LeafSmoothness));
                        _seasonalMaterials.Add(source, copy);
                    }

                    materials[i] = copy;
                    changed = true;
                }

                if (changed)
                    renderer.sharedMaterials = materials;
            }
        }

        /// <summary>
        /// Порог последнего LOD в ассетах посчитан под полный размер модели, а точка взлёта ставит их мельче
        /// (камни 0.1–0.6, камешки 0.02–0.09). Порог LODGroup — доля высоты кадра от размера самого объекта, поэтому
        /// уменьшенный камень или куст пропадал в десятке метров, прямо перед камерой. На копии порог опускается до
        /// minCullScreenHeight: объект исчезает, только когда на экране от него остаются пиксели.
        /// У части деревьев в ассетах пороги больше 1 (2.5, 1.0, ...), а SetLODs молча не принимает набор, где порог
        /// выше 1 или не убывает строго, — их сначала прижимаем к 1 (LOD0 и так виден только вплотную).
        /// </summary>
        private void ExtendLodCulling(GameObject instance)
        {
            foreach (LODGroup lodGroup in instance.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] lods = lodGroup.GetLODs();

                if (lods.Length == 0 || lods[^1].screenRelativeTransitionHeight <= minCullScreenHeight)
                    continue;

                float limit = 1f;

                for (int i = 0; i < lods.Length; i++)
                {
                    lods[i].screenRelativeTransitionHeight = Mathf.Min(lods[i].screenRelativeTransitionHeight, limit);
                    limit = lods[i].screenRelativeTransitionHeight * 0.999f;
                }

                lods[^1].screenRelativeTransitionHeight = Mathf.Min(lods[^1].screenRelativeTransitionHeight, minCullScreenHeight);
                lodGroup.SetLODs(lods);
            }
        }

        private bool TryGetSurface(Vector2 position, out Vector3 point, out Vector3 normal)
        {
            Terrain terrain = TerrainAt(position);
            if (terrain != null)
            {
                Vector3 terrainPosition = terrain.transform.position;
                Vector3 terrainSize = terrain.terrainData.size;
                float normalizedX = Mathf.Clamp01((position.x - terrainPosition.x) / terrainSize.x);
                float normalizedZ = Mathf.Clamp01((position.y - terrainPosition.z) / terrainSize.z);

                point = new Vector3(position.x, terrain.SampleHeight(new Vector3(position.x, 0f, position.y)) + terrainPosition.y, position.y);
                normal = terrain.terrainData.GetInterpolatedNormal(normalizedX, normalizedZ);
                return true;
            }

            Vector3 origin = new Vector3(position.x, transform.position.y + raycastStartHeight, position.y);

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, raycastDistance, groundLayer, QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                normal = hit.normal;
                return true;
            }

            point = default;
            normal = Vector3.up;
            return false;
        }

        private Terrain TerrainAt(Vector2 position)
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null)
                    continue;

                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;

                if (position.x >= origin.x && position.x <= origin.x + size.x &&
                    position.y >= origin.z && position.y <= origin.z + size.z)
                {
                    return terrain;
                }
            }

            return null;
        }

        private bool HitsSceneObstacle(Vector2 position, float groundY, float radius, float height)
        {
            Vector3 bottom = new(position.x, groundY + radius, position.y);
            Vector3 top = new(position.x, groundY + Mathf.Max(height, radius * 2f), position.y);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, _overlaps, obstacleLayer, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                Collider overlap = _overlaps[i];

                // капсула стоит на земле и всегда задевает террейн: с ним отбраковывались почти все места
                if (overlap == null || overlap is TerrainCollider || overlap.transform.IsChildOf(_root))
                    continue;

                return true;
            }

            return false;
        }

        private bool DetailPatchAllows(FlyPointDetailEntry entry, Vector2 local)
        {
            if (entry.PatchThreshold <= 0f)
                return true;

            float value = _noise.Contrasted(local.x, local.y, entry.PatchScale, DetailNoiseChannel);
            return RuntimeNoise.SmoothStep(entry.PatchThreshold, entry.PatchThreshold + DetailPatchBlend, value) > NextFloat();
        }

        private Vector2 SampleClusterCenter(FlyPointScatterCategory category, Vector3 center, float minRadius, float maxRadius)
        {
            if (category.AlongPath)
            {
                float along = RandomRange(minRadius, Mathf.Min(maxRadius, _site.PathLength));
                float side = (NextFloat() < 0.5f ? -1f : 1f) * (_site.PathHalfWidth + RandomRange(-0.4f, 1.2f));
                return XZ(center) + _site.PointNearPath(along, side);
            }

            if (category.NearTreeGroups && _treeGroupCenters.Count > 0 && NextFloat() < 0.65f)
            {
                Vector2 anchor = _treeGroupCenters[_random.Next(_treeGroupCenters.Count)];
                return anchor + RandomDirection() * _treeGroupRadius * Mathf.Lerp(0.8f, 1.5f, NextFloat());
            }

            float distance = Mathf.Sqrt(Mathf.Lerp(minRadius * minRadius, maxRadius * maxRadius, NextFloat()));
            return XZ(center) + RandomDirection() * distance;
        }

        private Transform CreateGroup(string groupName)
        {
            GameObject group = new(groupName);
            group.transform.SetParent(_root, false);
            return group.transform;
        }

        private List<FlyPointPrefabEntry> ValidEntries(FlyPointScatterCategory category)
        {
            List<FlyPointPrefabEntry> entries = new();

            FlyPointPrefabEntry[] prefabs = category.Prefabs ?? System.Array.Empty<FlyPointPrefabEntry>();

            foreach (FlyPointPrefabEntry entry in prefabs)
            {
                if (entry != null && entry.Prefab != null && entry.Weight > 0f)
                    entries.Add(entry);
            }

            return entries;
        }

        private PrefabFootprint FootprintOf(GameObject prefab)
        {
            if (_footprints.TryGetValue(prefab, out PrefabFootprint cached))
                return cached;

            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            PrefabFootprint footprint;

            if (renderers.Length == 0)
            {
                footprint = new PrefabFootprint(1f, 1f);
            }
            else
            {
                Bounds bounds = renderers[0].bounds;

                for (int i = 1; i < renderers.Length; i++)
                    bounds.Encapsulate(renderers[i].bounds);

                Vector3 pivot = prefab.transform.position;
                float footprintRadius = Mathf.Max(
                    Mathf.Max(Mathf.Abs(bounds.min.x - pivot.x), Mathf.Abs(bounds.max.x - pivot.x)),
                    Mathf.Max(Mathf.Abs(bounds.min.z - pivot.z), Mathf.Abs(bounds.max.z - pivot.z)));

                footprint = new PrefabFootprint(footprintRadius, bounds.size.y);
            }

            _footprints[prefab] = footprint;
            return footprint;
        }

        // Круг кладётся во все ячейки, которые задевает: раньше он лежал только в ячейке центра, и большой круг
        // (площадка, крона) не находился из соседних ячеек.
        private void AddOccupancy(Vector2 center, float occupancyRadius)
        {
            OccupancyCircle circle = new(center, occupancyRadius);
            Vector2Int min = CellOf(center - Vector2.one * occupancyRadius);
            Vector2Int max = CellOf(center + Vector2.one * occupancyRadius);
            _occupancyCircles.Add(circle);

            for (int z = min.y; z <= max.y; z++)
            {
                for (int x = min.x; x <= max.x; x++)
                {
                    Vector2Int cell = new(x, z);

                    if (!_occupancy.TryGetValue(cell, out List<OccupancyCircle> circles))
                    {
                        circles = new List<OccupancyCircle>();
                        _occupancy[cell] = circles;
                    }

                    circles.Add(circle);
                }
            }
        }

        private bool IsFree(Vector2 center, float occupancyRadius)
        {
            Vector2Int min = CellOf(center - Vector2.one * occupancyRadius);
            Vector2Int max = CellOf(center + Vector2.one * occupancyRadius);

            for (int z = min.y; z <= max.y; z++)
            {
                for (int x = min.x; x <= max.x; x++)
                {
                    Vector2Int cell = new(x, z);

                    if (!_occupancy.TryGetValue(cell, out List<OccupancyCircle> circles))
                        continue;

                    foreach (OccupancyCircle circle in circles)
                    {
                        float minDistance = circle.Radius + occupancyRadius;

                        if ((circle.Center - center).sqrMagnitude < minDistance * minDistance)
                            return false;
                    }
                }
            }

            return true;
        }

        private Vector2Int CellOf(Vector2 position) =>
            new(Mathf.FloorToInt(position.x / OccupancyCellSize), Mathf.FloorToInt(position.y / OccupancyCellSize));

        private FlyPointPrefabEntry PickWeighted(List<FlyPointPrefabEntry> entries)
        {
            float total = 0f;

            foreach (FlyPointPrefabEntry entry in entries)
                total += entry.Weight;

            float pick = NextFloat() * total;

            foreach (FlyPointPrefabEntry entry in entries)
            {
                pick -= entry.Weight;

                if (pick <= 0f)
                    return entry;
            }

            return entries[^1];
        }

        private float PickScale(FlyPointPrefabEntry entry, bool isLeading)
        {
            float t = isLeading ? Mathf.Lerp(0.6f, 1f, NextFloat()) : NextFloat() * 0.85f;
            return Mathf.Lerp(entry.ScaleRange.x, entry.ScaleRange.y, t);
        }

        private bool IsInRing(Vector2 position, Vector3 center, float minRadius, float maxRadius)
        {
            float distance = Vector2.Distance(position, XZ(center));
            return distance >= minRadius && distance <= maxRadius;
        }

        private static bool IsFarFromAll(List<Vector2> points, Vector2 candidate, float spacing)
        {
            float sqrSpacing = spacing * spacing;

            foreach (Vector2 point in points)
            {
                if ((point - candidate).sqrMagnitude < sqrSpacing)
                    return false;
            }

            return true;
        }

        private float NextFloat() => (float)_random.NextDouble();

        private float RandomRange(float min, float max) => Mathf.Lerp(min, max, NextFloat());

        private Vector2 RandomDirection()
        {
            float angle = NextFloat() * Mathf.PI * 2f;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }

        private Vector2 Gaussian()
        {
            float magnitude = Mathf.Sqrt(-2f * Mathf.Log(1f - NextFloat()));
            float angle = NextFloat() * Mathf.PI * 2f;
            return Vector2.ClampMagnitude(new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * magnitude, 2f);
        }

        private int StochasticRound(float value)
        {
            int whole = Mathf.FloorToInt(value);
            return whole + (NextFloat() < value - whole ? 1 : 0);
        }

        private static Vector2 XZ(Vector3 position) => new(position.x, position.z);

        private readonly struct PrefabFootprint
        {
            public PrefabFootprint(float radius, float height)
            {
                Radius = radius;
                Height = height;
            }

            public float Radius { get; }
            public float Height { get; }
        }

        private readonly struct OccupancyCircle
        {
            public OccupancyCircle(Vector2 center, float radius)
            {
                Center = center;
                Radius = radius;
            }

            public Vector2 Center { get; }
            public float Radius { get; }
        }

        // Что вокруг точки (AnalyzeSite): доля леса и голой земли, 0..1; плотность деревьев, цвет и доли карты земель
        // (LandCover, null — оценка без неё) — для отладки.
        private readonly struct SiteContext
        {
            public static readonly SiteContext Default = new(0.5f, 0f, 0f, 0f, null);

            public SiteContext(float treeCover, float bareGround, float treesPerHectare, float redOverGreen, string landCover)
            {
                TreeCover = treeCover;
                BareGround = bareGround;
                TreesPerHectare = treesPerHectare;
                RedOverGreen = redOverGreen;
                LandCover = landCover;
            }

            public float TreeCover { get; }
            public float BareGround { get; }
            public float TreesPerHectare { get; }
            public float RedOverGreen { get; }
            public string LandCover { get; }

            // множитель количества: treeCoverScale — в открытом месте (x) и в лесу (y), bareGroundScale — на голой земле
            public float Scale(Vector2 treeCoverScale, float bareGroundScale) =>
                Mathf.Lerp(treeCoverScale.x, treeCoverScale.y, TreeCover) * Mathf.Lerp(1f, bareGroundScale, BareGround);

            public float TreeScale(Vector2 treeCoverScale) => Mathf.Lerp(treeCoverScale.x, treeCoverScale.y, TreeCover);

            public override string ToString() =>
                $"treeCover={TreeCover:F2} ({TreesPerHectare:F1}/ha) bare={BareGround:F2} (r-g={RedOverGreen:F3})" +
                (LandCover != null ? $" land cover [{LandCover}]" : " no land cover");
        }

        private readonly struct AlphaRegion
        {
            public AlphaRegion(float[,,] maps, int xMin, int zMin, int mapWidth, int mapHeight, int grassLayer, int soilLayer)
            {
                Maps = maps;
                XMin = xMin;
                ZMin = zMin;
                MapWidth = mapWidth;
                MapHeight = mapHeight;
                GrassLayer = grassLayer;
                SoilLayer = soilLayer;
            }

            public float[,,] Maps { get; }
            public int XMin { get; }
            public int ZMin { get; }
            public int MapWidth { get; }
            public int MapHeight { get; }
            public int GrassLayer { get; }
            public int SoilLayer { get; }
        }

        private sealed class DetailGrid
        {
            public DetailGrid(int xMin, int zMin, int width, int height, float cellX, float cellZ, Vector3 origin, Vector2 center)
            {
                XMin = xMin;
                ZMin = zMin;
                Width = width;
                Height = height;
                CellX = cellX;
                CellZ = cellZ;
                Origin = origin;
                Center = center;
                Grass = new float[height, width];
                Soil = new float[height, width];
                EdgeFade = new float[height, width];
                Worn = new float[height, width];
                TreeNear = new float[height, width];
                Blocked = new float[height, width];
                ForestLand = new float[height, width];
                Satellite = new Vector4[height, width];
                ToField = new float[height, width];
                Road = new float[height, width];
            }

            public int XMin { get; }
            public int ZMin { get; }
            public int Width { get; }
            public int Height { get; }
            public float CellX { get; }
            public float CellZ { get; }
            public Vector3 Origin { get; }
            public Vector2 Center { get; }
            public float[,] Grass { get; }
            public float[,] Soil { get; }
            public float[,] EdgeFade { get; }
            public float[,] Worn { get; }
            public float[,] TreeNear { get; }
            public float[,] Blocked { get; }
            // доля леса по карте земель (0 — карты нет)
            public float[,] ForestLand { get; }
            // что на земле по спутнику (SatelliteStates: x зелень, y жёлтое, z тёмное, w голое)
            public Vector4[,] Satellite { get; }
            public bool HasSatellite { get; set; }
            // насколько ячейка растёт как поле (карта зовёт её лугом, а на спутнике пашня; край поля без зелени)
            public float[,] ToField { get; }
            // сколько от дороги по осям (RoadAreas): 1 на полотне, к 0 за его краем
            public float[,] Road { get; }
            public Vector4 SatelliteAt(int x, int z) => HasSatellite ? Satellite[z, x] : SatelliteStates.NoSatellite;
            // зимой — доля голой земли по узору снега (null — снега нет)
            public float[,] SnowFree { get; set; }
            // крутизна склона, градусы (null — лежащего по склону нет)
            public float[,] Steepness { get; set; }
            // классы карты земель в узлах решётки (null — карты нет)
            public byte[] Land { get; private set; }
            // в узлах карты земель: 1 — поле брошено (воронки вокруг, CraterField.AbandonedAt); null — таких нет
            public float[] Abandoned { get; set; }
            public int LandCountX { get; private set; }
            public int LandCountZ { get; private set; }
            public bool HasLand => Land != null;

            private Vector2 _landStart;
            private float _landStep = 1f;

            public void SetLand(byte[] land, Vector2 start, int countX, int countZ, float step)
            {
                Land = land;
                _landStart = start;
                LandCountX = countX;
                LandCountZ = countZ;
                _landStep = step;
            }

            // Ячейка в узлах решётки земли.
            public void LandNode(int x, int z, out float gx, out float gz)
            {
                Vector2 world = Local(x, z) + Center;
                gx = (world.x - _landStart.x) / _landStep;
                gz = (world.y - _landStart.y) / _landStep;
            }

            public Vector2 Local(int x, int z) =>
                new Vector2(Origin.x + (XMin + x + 0.5f) * CellX, Origin.z + (ZMin + z + 0.5f) * CellZ) - Center;

            // Пишет в target максимум value(расстояние от центра ячейки до точки, радиус) по ячейкам круга.
            public void Rasterize(Vector2 world, float radius, System.Func<float, float, float> value, float[,] target)
            {
                int x0 = Mathf.Max(0, Mathf.FloorToInt((world.x - radius - Origin.x) / CellX) - XMin);
                int x1 = Mathf.Min(Width - 1, Mathf.FloorToInt((world.x + radius - Origin.x) / CellX) - XMin);
                int z0 = Mathf.Max(0, Mathf.FloorToInt((world.y - radius - Origin.z) / CellZ) - ZMin);
                int z1 = Mathf.Min(Height - 1, Mathf.FloorToInt((world.y + radius - Origin.z) / CellZ) - ZMin);

                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 cell = Local(x, z) + Center;
                        float cellValue = value(Vector2.Distance(cell, world), radius);

                        if (cellValue > target[z, x])
                            target[z, x] = cellValue;
                    }
                }
            }
        }

        /// <summary>
        /// Форма площадки взлёта: неровное вытоптанное пятно, вытянутое в сторону тропинки (колеи), которая уходит
        /// плавной дугой за край зоны. Всё в локальных метрах от центра точки; параметры — из сида точки.
        /// </summary>
        private sealed class TakeoffSite
        {
            private const float PadStretchAlongPath = 1.3f;
            private const float PadSquashAcrossPath = 0.9f;
            private const float PadEdgeInner = 3.5f;
            private const float PadEdgeOuter = 1f;
            private const float PathStraightStart = 15f;
            private const float PathFadeLength = 45f;
            private const float PathEdgeSoftness = 0.6f;

            private readonly RuntimeNoise _noise;
            private readonly Vector2 _along;
            private readonly Vector2 _across;
            private readonly float _padRadius;
            private readonly float _pathAmplitude;
            private readonly float _pathWavelength;
            private readonly float _pathPhase;

            public TakeoffSite(RuntimeNoise noise, System.Random random, float padRadius, float pathLength)
            {
                float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                _noise = noise;
                _along = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                _across = new Vector2(-_along.y, _along.x);
                _padRadius = padRadius * Mathf.Lerp(0.9f, 1.25f, (float)random.NextDouble());
                _pathAmplitude = Mathf.Lerp(4f, 10f, (float)random.NextDouble());
                _pathWavelength = Mathf.Lerp(35f, 70f, (float)random.NextDouble());
                _pathPhase = (float)random.NextDouble() * Mathf.PI * 2f;
                PathHalfWidth = Mathf.Lerp(0.9f, 1.4f, (float)random.NextDouble());
                PathLength = pathLength;
            }

            public float PathHalfWidth { get; }
            public float PathLength { get; }

            public float Worn(Vector2 local) => Mathf.Max(Trample(local), Path(local));

            public float Trample(Vector2 local)
            {
                float along = Vector2.Dot(local, _along);
                float across = Vector2.Dot(local, _across);
                float stretched = Mathf.Sqrt(Sq(along / PadStretchAlongPath) + Sq(across / PadSquashAcrossPath));
                float edge = _padRadius +
                             (_noise.Perlin(local.x, local.y, 9f, TrampleChannel) - 0.5f) * 0.8f * _padRadius +
                             (_noise.Perlin(local.x, local.y, 3f, TrampleFineChannel) - 0.5f) * 0.3f * _padRadius;
                return 1f - RuntimeNoise.SmoothStep(edge - PadEdgeInner, edge + PadEdgeOuter, stretched);
            }

            public float Path(Vector2 local)
            {
                float along = Vector2.Dot(local, _along);

                if (along < 0f || along > PathLength)
                    return 0f;

                float across = Mathf.Abs(Vector2.Dot(local, _across) - PathOffset(along));
                float halfWidth = PathHalfWidth * Mathf.Lerp(0.75f, 1.25f, _noise.Perlin(along, 0f, 25f, PathChannel));
                float strip = 1f - RuntimeNoise.SmoothStep(halfWidth - PathEdgeSoftness, halfWidth + PathEdgeSoftness, across);
                return strip * (1f - RuntimeNoise.SmoothStep(PathLength - PathFadeLength, PathLength, along));
            }

            // Точка у тропинки: along — вдоль неё от центра, side — вбок от её середины.
            public Vector2 PointNearPath(float along, float side) => _along * along + _across * (PathOffset(along) + side);

            // От площадки тропинка уходит прямо, дальше плавно виляет.
            private float PathOffset(float along) =>
                _pathAmplitude * (Mathf.Sin(along / _pathWavelength + _pathPhase) - Mathf.Sin(_pathPhase)) *
                Mathf.Clamp01(along / PathStraightStart);

            private static float Sq(float value) => value * value;
        }

        private readonly struct RuntimeTerrainDataBackup
        {
            public RuntimeTerrainDataBackup(Terrain terrain, TerrainCollider collider, TerrainData originalData, TerrainData runtimeData, Vector2 anchor)
            {
                Terrain = terrain;
                Collider = collider;
                OriginalData = originalData;
                RuntimeData = runtimeData;
                Anchor = anchor;
            }

            public Terrain Terrain { get; }
            public Vector2 Anchor { get; }
            public TerrainCollider Collider { get; }
            public TerrainData OriginalData { get; }
            public TerrainData RuntimeData { get; }
        }

        private sealed class RuntimeNoise
        {
            // новые каналы только дописываются в конец: смещения прежних не меняются
            private const int ChannelCount = 32;
            private const float ChannelOffsetRange = 5000f;
            private const float Contrast = 1.8f;

            private readonly Vector2[] _offsets = new Vector2[ChannelCount];

            public RuntimeNoise(int seed)
            {
                System.Random random = new(seed);

                for (int i = 0; i < ChannelCount; i++)
                {
                    _offsets[i] = new Vector2(
                        (float)random.NextDouble() * ChannelOffsetRange,
                        (float)random.NextDouble() * ChannelOffsetRange);
                }
            }

            public float Contrasted(float x, float z, float scale, int channel)
            {
                float value = Perlin(x, z, scale, channel);
                return Mathf.Clamp01((value - 0.5f) * Contrast + 0.5f);
            }

            public float Perlin(float x, float z, float scale, int channel)
            {
                Vector2 offset = _offsets[channel % ChannelCount];
                return Mathf.PerlinNoise(x / scale + offset.x, z / scale + offset.y);
            }

            public float SignedFbm(float x, float z, float scale, int channel)
            {
                const int octaves = 3;
                float sum = 0f;
                float amplitude = 1f;
                float normalization = 0f;

                for (int octave = 0; octave < octaves; octave++)
                {
                    sum += (Perlin(x, z, scale, channel + octave) - 0.5f) * 2f * amplitude;
                    normalization += amplitude;
                    amplitude *= 0.5f;
                    scale *= 0.5f;
                }

                return Mathf.Clamp(sum / normalization * Contrast, -1f, 1f);
            }

            public static float SmoothStep(float edge0, float edge1, float value)
            {
                float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
                return t * t * (3f - 2f * t);
            }

            public static float Hash01(int x, int z, int seed)
            {
                unchecked
                {
                    uint hash = (uint)x * 374761393u + (uint)z * 668265263u + (uint)seed * 2246822519u;
                    hash = (hash ^ (hash >> 13)) * 1274126177u;
                    return (hash ^ (hash >> 16)) / (float)uint.MaxValue;
                }
            }
        }
    }
}
