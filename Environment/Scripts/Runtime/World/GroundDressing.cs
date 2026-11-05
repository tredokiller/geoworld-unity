using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using _Game.Scripts.Locations;
using _Game.Scripts.Locations.FlyPoints;
using GeoWorld;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Трава, цветы и опавшая листва по всей карте, а не только у точки взлёта. Каждый загруженный тайл GeoWorld
    /// получает слои деталей Nature Renderer (ячейка ~2 м), посчитанные в фоновом потоке по спутнику тайла и его
    /// деревьям:
    ///  - зелень — трава и цветы по профилю, голая земля (пахота, стерня) — редкие сорняки, асфальт, крыши и вода — ничего
    ///    (вода, здания, рельсы и дороги любого покрытия с тропинками — по карте земель GeoWorld, если она запечена, иначе по
    ///    цвету спутника);
    ///  - в лесу и саду травы меньше, под деревьями — листва;
    ///  - по карте земель каждый вид растёт по-своему (LandScales записи профиля, LandGroups): на лугу, в поле и у его
    ///    края, в лесу (там листва почти везде), на болоте и берегу, в посёлке, на грунтовках, на песке и голой земле.
    /// Виды травы и их правила — те же записи профиля точки взлёта (DefaultFlyPointEnvironment). Nature Renderer рисует
    /// детали только рядом с камерой (дальность и прореживание — NatureRendererSettings и настройки префабов), поэтому
    /// траву получают только тайлы в пределах dressDistance от камеры или от того места, где она будет через
    /// lookAheadSeconds; дальние так и остаются без неё до выгрузки. Рендерер травы Nature Renderer у одетого тайла есть,
    /// только пока тот не дальше дальности травы (+ShowMargin): ячейки рендереров он перебирает каждый кадр во всех тайлах,
    /// а копия с травой держится и ближе к тайлу собирается заново (ShowGrass). Тайл считается в фоновом потоке (до двух сразу),
    /// а копия TerrainData собирается в главном потоке за несколько кадров. Тайлы, на которых стоит точка взлёта, рисует
    /// её генератор — сеткой 1 м и с этой же травой за краем своей зоны (TryGetWorldDetail).
    /// </summary>
    public sealed class GroundDressing : MonoBehaviour
    {
        // 2 м на пиксель: межи и полосы 3–5 м шириной не размываются
        private const int SatelliteCopySize = 512;
        // крутизна в узлах через 20 м: жёлтый степной склон балки ровный, как стерня, но полем не бывает
        private const int SteepnessNodes = 51;
        private static readonly Vector2 FieldSteepness = new(4.5f, 8f);
        // ячейка стриминга Nature Renderer — это патч, и грузится она целиком за кадр: 512 / 32 — 62.5 м (~12 тыс. стеблей
        // посевов). С 64 (125 м, ~47 тыс.) на 400 км/ч над полем было 16–17 кадров дольше 50 мс за 14 с, с 32 — 1–2.
        // Свой OptimizePatchSize (переписать на 64) Nature Renderer на этих террейнах не делает: SwapGrass его выключает
        private const int DetailPatchResolution = 32;
        private const int MaxDetailPerCell = 255;
        // рендерер травы — у тайлов не дальше дальности травы + ShowMargin (от камеры или точки упреждения), снимается
        // дальше + HideMargin. Nature Renderer перебирает его ячейки каждый кадр (на тайл 62 вида × 256 патчей): с 9
        // одетыми тайлами это ~1.4 мс главного потока из ~4 мс всего Nature Renderer, а видна трава только в 200 м. Собрать
        // заново — ~2 мс (до 4), по одному тайлу за проверку
        private const float ShowMargin = 150f;
        private const float HideMargin = 400f;
        // трава, которую ждёт индикатор смены поры года (NearDressed)
        private const float NearReadyDistance = 300f;
        private const float CheckInterval = 0.25f;
        private const float RuntimeDataDestroyDelay = 2f;
        // фоновых расчётов тайлов одновременно и слоёв травы, что ложатся в копию за кадр (~3.5 мс каждый при 512²)
        private const int MaxParallelJobs = 2;
        private const int LayersPerFrame = 2;
        // скорость камеры для упреждения: сглаживание и порог телепорта
        private const float VelocitySmoothing = 0.5f;
        private const float TeleportSpeed = 250f;
        // лес вокруг ячейки: деревья тайла в квадратах этого размера и пороги плотности на гектар, как у точки взлёта
        private const float ForestCellSize = 25f;
        private const float OpenTreesPerHectare = 1f;
        private const float ForestTreesPerHectare = 30f;
        private const float LitterInner = 1.2f;
        private const float LitterOuter = 5.5f;
        private const float SquareMetersPerHectare = 10000f;
        // карта земель GeoWorld: шаг, которым её читаем (её ячейка ~10 м), и доля твёрдой земли в ячейке, с которой
        // травы нет совсем (узкий ручей в одну ячейку тоже без травы, берег редеет плавно)
        private const float LandCoverStep = 10f;
        // карта читается и на ячейку за краем тайла: край поля и берег на стыке тайлов видят соседа
        private const int LandMargin = 1;
        // дороги читаются и на столько метров за краем тайла: брёвна и штабеля у края не ложатся на дорогу соседа
        private const float RoadReach = 10f;
        private static readonly Vector2 HardGroundEdge = new(0.2f, 0.55f);

        public static GroundDressing Instance { get; private set; }

        // Дальность одевания тайлов: не меньше 1 км, а с дальностью травы больше — до неё (настройки графики).
        public float DressDistance
        {
            get => dressDistance;
            set => dressDistance = Mathf.Max(200f, value);
        }

        // Дальность травы Nature Renderer (настройки графики): рендерер травы — у тайлов не дальше неё + ShowMargin.
        public float GrassDistance
        {
            get => grassDistance;
            set => grassDistance = Mathf.Max(0f, value);
        }

        [Tooltip("Трава по всей карте; выключено — только у точки взлёта.")]
        [SerializeField] private bool dressWorld = true;
        [Tooltip("Плотность травы мира относительно плотности у точки взлёта.")]
        [SerializeField, Range(0.05f, 1f)] private float worldDensity = 0.6f;
        [Tooltip("Ячеек деталей на тайл по стороне (тайл 1 км: 512 — ячейка ~2 м).")]
        [SerializeField] private int resolution = 512;
        [Tooltip("Траву получают тайлы не дальше этого (м) от камеры или от того места, где она будет через lookAheadSeconds.")]
        [SerializeField, Min(200f)] private float dressDistance = 1000f;
        [Tooltip("Упреждение по скорости камеры (с): на 400 км/ч тайлы впереди одеваются заранее.")]
        [SerializeField, Min(0f)] private float lookAheadSeconds = 6f;
        [Tooltip("Дальность травы Nature Renderer (м): рендерер травы — только у одетых тайлов не дальше неё + ShowMargin.")]
        [SerializeField, Min(0f)] private float grassDistance = 200f;

        private readonly Dictionary<Terrain, Tile> _tiles = new();
        private readonly List<Terrain> _gone = new();
        private readonly List<Tile> _running = new();
        // тайлов, чей спутник ещё читается с GPU (расчёт начнётся по готовности)
        private int _reading;
        private FlyPointEnvironmentProfile _profile;
        private Tile _building;
        private int[,] _grid;
        private float _nextCheck;
        private Vector3 _eye;
        private Vector3 _eyeVelocity;
        private bool _hasEye;
        // ближним одетым тайлам ещё собирать траву (по одному за проверку)
        private bool _showPending;
        private int _dressedCount;
        private double _jobMilliseconds;
        private double _mainMilliseconds;
        private double _frameMilliseconds;
        private double _frameMillisecondsMax;

        private sealed class Tile
        {
            public Terrain Terrain;
            public TerrainData Source;
            public TerrainData Copy;
            public Input Input;
            public Task<byte[][]> Job;
            // слои травы из фонового расчёта; держатся, пока копия не собрана целиком
            public byte[][] Counts;
            // сколько слоёв уже легло в копию; копия готова, когда легли все
            public int AppliedLayers;
            // виды травы на момент расчёта: копия и трава для генератора точки — по ним, даже если профиль с тех пор менялся
            public GameObject[] Prototypes;
            public DetailPrototype[] DetailPrototypes;
            public double MainMilliseconds;
            // копия прежней поры года, которую держит генератор точки как исходные данные тайла: сменим её, когда он
            // вернёт её тайлу (ReplaceStaleCopies)
            public TerrainData StaleCopy;
            // пора года и снег, по которым посчитан тайл (SeasonKey)
            public int Season;
            // сборка сломалась (Abandon): без травы до выгрузки
            public bool Failed;
        }

        // Всё, что нужно расчёту в фоновом потоке: без объектов Unity.
        private sealed class Input
        {
            public Vector2 Origin;
            public Vector2 Size;
            public int Resolution;
            public Color32[] Satellite;
            public int SatelliteSize;
            // классы карты земель GeoWorld в узлах через LandCoverStep: LandCells на тайл по стороне и LandMargin узлов
            // за краем; null — у мира карты нет
            public byte[] Land;
            public int LandCells;
            // в тех же узлах: 1 — поле брошено (там шли бои, CraterField.AbandonedAt): растёт как луг и бурьян, а не стерня;
            // null — брошенных нет
            public float[] Abandoned;
            // оси дорог у тайла (RoadAreas); RoadsKnown — тайл в выгрузках дорог, клетки дорог карты земель не в счёт
            public RoadAreas.Segment[] Roads;
            public bool RoadsKnown;
            // воронки у тайла (CraterField): свежая чаша и прудок голы, в старой трава реже, у свежей вокруг — вывороченный грунт
            public CraterField.Crater[] Craters;
            // окопы у тайла: во рву трава реже (у брошенных он зарос), у свежих — гол
            public CraterField.Trench[] Trenches;
            // крутизна рельефа (градусы) и z нормали (−z — юг: там снег тает раньше) в SteepnessNodes² узлах по тайлу
            public float[] Steepness;
            public float[] NormalZ;
            public Vector2[] Trees;
            // сколько листвы у дерева (TreeLitterWeight): у хвойного мало, осенью у голого больше всех
            public float[] TreeLitter;
            public Entry[] Entries;
            public float Density;
            // покров снега зимой (Seasons), 0 — снега нет
            public float Snow;
        }

        private struct Entry
        {
            public float Density;
            public float PatchThreshold;
            public float PatchScale;
            public float ClumpScale;
            public float ClumpStrength;
            public Vector2 TreeCoverScale;
            public float BareGroundScale;
            // множители по спутнику (SatelliteStates); UseSatellite false — прежний BareGroundScale по «голому» (r > g)
            public FlyPointSatelliteScales Satellite;
            public bool UseSatellite;
            public bool UnderTrees;
            public FlyPointLandScales Land;
            // множитель поры года (Seasons); UnderSnow — под снегом записи нет (по его узору, SnowCover)
            public float Season;
            public bool UnderSnow;
            // лежит по склону (опад, ветки, камешки) — не круче FlyPointDetailEntry.MaxGroundSlope
            public bool OnGround;
            // пень, бревно, штабель: сколько занимает на земле (DetailSpacing), 0 — трава и мелочь, разводить не нужно
            public float Apart;
            public int Seed;
        }

        public bool DressWorld
        {
            get => dressWorld;
            set
            {
                if (dressWorld == value)
                    return;

                dressWorld = value;

                if (!value)
                    UndressAll();
            }
        }

        public string Stats =>
            $"tiles={_dressedCount} job={(_dressedCount > 0 ? _jobMilliseconds / _dressedCount : 0):F0}ms " +
            $"main={(_dressedCount > 0 ? _mainMilliseconds / _dressedCount : 0):F1}ms (frame max {_frameMillisecondsMax:F1}ms) " +
            $"dressed now={CountDressed()} with grass={CountShown()} running={_running.Count}";

        // Идёт расчёт или сборка тайла (GrassBenchmark ждёт, пока трава вокруг камеры досчитается).
        public bool Busy => _running.Count > 0 || _reading > 0 || _building != null || _showPending;

        /// <summary>
        /// Трава у камеры готова: каждый тайл не дальше дальности травы, но не дальше NearReadyDistance (+ShowMargin), одет и с
        /// рендерером травы (или сломан — он без травы до выгрузки). Индикатор смены поры года ждёт этого, а не всех тайлов до
        /// dressDistance: с травой на 1–3 км ждать пришлось бы почти все загруженные тайлы, дальняя дорисуется после.
        /// </summary>
        public bool NearDressed
        {
            get
            {
                if (!dressWorld || _profile == null || !_hasEye)
                    return true;

                float limit = Square(Mathf.Min(grassDistance, NearReadyDistance) + ShowMargin);

                foreach (Terrain terrain in Terrain.activeTerrains)
                {
                    // тайл с точкой взлёта рисует её генератор
                    if (terrain == null || terrain.terrainData == null || DistanceToTile(terrain, _eye) > limit ||
                        CustomSpawnRuntimeEnvironmentGenerator.IsRuntimeCopy(terrain.terrainData))
                        continue;

                    if (!_tiles.TryGetValue(terrain, out Tile tile))
                        return false;

                    if (tile.Failed)
                        continue;

                    // считается, читается спутник или собирается копия
                    if (tile.Copy == null || tile.Job != null || tile.Counts != null)
                        return false;

                    if (tile.Terrain.terrainData == tile.Source || NatureRendererTerrain.GrassShown(terrain) == false)
                        return false;
                }

                return true;
            }
        }

        public void Configure(FlyPointEnvironmentProfile profile) => _profile = profile;

        private void OnEnable()
        {
            Instance = this;
            Seasons.Changed += UndressAll;
        }

        private void OnDisable()
        {
            Seasons.Changed -= UndressAll;

            if (Instance == this)
                Instance = null;
        }

        private void OnDestroy() => UndressAll();

        private void Update()
        {
            _frameMilliseconds = 0;
            TrackEye();

            for (int i = _running.Count - 1; i >= 0; i--)
            {
                if (_running[i].Job.IsCompleted)
                    FinishJob(_running[i]);
            }

            if (Time.unscaledTime >= _nextCheck)
                Check();

            // подготовка нового тайла (спутник через GPU, земля) и шаг сборки в одном кадре давали ~20 мс
            if (_building != null && _frameMilliseconds <= 0)
                ContinueBuild(_building);

            _frameMillisecondsMax = System.Math.Max(_frameMillisecondsMax, _frameMilliseconds);
        }

        private void Check()
        {
            _nextCheck = Time.unscaledTime + CheckInterval;
            ForgetUnloadedTiles();
            ReplaceStaleCopies();

            if (!dressWorld || _profile == null)
                return;

            bool changed = UpdateGrassShown();
            _building ??= NextToBuild();

            // за проверку — один новый расчёт: его подготовка читает землю тайла в главном потоке (не в одном кадре со
            // сборкой травы тайла)
            if (!changed && _running.Count + _reading < MaxParallelJobs)
                StartNearestTile();
        }

        // Рендерер травы — только у одетых тайлов рядом: ближнему без него собираем, дальнему снимаем, по одному за
        // проверку. Есть ли он — спрашиваем у Nature Renderer: если тот перезапустится и соберёт траву сам, снимем снова.
        private bool UpdateGrassShown()
        {
            _showPending = false;

            if (!_hasEye)
                return false;

            Vector3 ahead = _eye + _eyeVelocity * lookAheadSeconds;
            Tile toShow = null, toHide = null;
            float nearest = float.MaxValue, farthest = 0f;
            int waiting = 0;

            foreach (Tile tile in _tiles.Values)
            {
                // одет: на тайле его копия (тайл генератора точки и собирающиеся не в счёт)
                if (tile.Terrain == null || tile.Copy == null || tile.Terrain.terrainData != tile.Copy)
                    continue;

                bool? shown = NatureRendererTerrain.GrassShown(tile.Terrain);
                float distance = Mathf.Min(DistanceToTile(tile.Terrain, _eye), DistanceToTile(tile.Terrain, ahead));

                if (shown == false && distance <= Square(grassDistance + ShowMargin))
                {
                    waiting++;

                    if (distance < nearest)
                    {
                        nearest = distance;
                        toShow = tile;
                    }
                }
                else if (shown == true && distance > Square(grassDistance + HideMargin) && distance > farthest)
                {
                    farthest = distance;
                    toHide = tile;
                }
            }

            _showPending = waiting > 1;
            Tile change = toShow ?? toHide;

            if (change == null)
                return false;

            Stopwatch watch = Stopwatch.StartNew();
            NatureRendererTerrain.ShowGrass(change.Terrain, change == toShow);
            _frameMilliseconds += watch.Elapsed.TotalMilliseconds;
            return true;
        }

        private bool NearForGrass(Tile tile)
        {
            Vector3 ahead = _eye + _eyeVelocity * lookAheadSeconds;
            return !_hasEye || Mathf.Min(DistanceToTile(tile.Terrain, _eye), DistanceToTile(tile.Terrain, ahead)) <= Square(grassDistance + ShowMargin);
        }

        private static float Square(float value) => value * value;

        /// <summary>
        /// Трава мира на тайле для генератора точки взлёта: он рисует свою зону сеткой 1 м и за её краем кладёт эту.
        /// source — TerrainData тайла до генератора. Если тайл ещё не посчитан, считает сразу (генерация идёт под
        /// экраном загрузки) и позже сам наденет копию, когда генератор вернёт тайлу source.
        /// </summary>
        public bool TryGetWorldDetail(Terrain terrain, TerrainData source, out GameObject[] prototypes, out int[][,] counts)
        {
            prototypes = null;
            counts = null;

            if (!dressWorld || _profile == null || terrain == null || source == null)
                return false;

            // генератор спросил раньше, чем смена поры сняла траву (порядок подписчиков Seasons.Changed любой): копия
            // прежней поры — у него в исходных данных, считаем заново
            if (_tiles.TryGetValue(terrain, out Tile outdated) && outdated.Season != SeasonKey() && outdated.Job == null &&
                outdated.Copy != null && terrain.terrainData != outdated.Copy && terrain.terrainData != outdated.Source)
            {
                outdated.StaleCopy = outdated.Copy;
                outdated.Copy = null;
                outdated.Counts = null;
            }

            if (!_tiles.TryGetValue(terrain, out Tile tile) || (tile.Copy == null && tile.Counts == null))
            {
                Tile running = _running.Find(candidate => candidate.Terrain == terrain);

                if (running != null)
                    FinishJob(running, wait: true);

                if (!_tiles.TryGetValue(terrain, out tile) || (tile.Copy == null && tile.Counts == null))
                {
                    // после смены поры генератор отдаёт копию прежней поры: считаем по исходным данным тайла
                    TerrainData stale = tile?.StaleCopy;
                    tile = Prepare(terrain, stale != null ? tile.Source : source);
                    tile.StaleCopy = stale;
                    tile.Counts = Compute(tile.Input);
                    _tiles[terrain] = tile;
                }
            }

            prototypes = tile.Prototypes;
            counts = new int[prototypes.Length][,];
            int n = tile.Input.Resolution;

            for (int i = 0; i < counts.Length; i++)
            {
                // пока копия собирается, в ней легли не все слои — берём посчитанные
                counts[i] = tile.Counts != null
                    ? ToGrid(tile.Counts[i], n, new int[n, n])
                    : tile.Copy.GetDetailLayer(0, 0, tile.Copy.detailWidth, tile.Copy.detailHeight, i);
            }

            return true;
        }

        // Камера и её сглаженная скорость — для упреждения; скачок быстрее TeleportSpeed — телепорт, скорость сбрасывается.
        private void TrackEye()
        {
            Camera camera = Camera.main;

            if (camera == null)
                return;

            Vector3 eye = camera.transform.position;
            float deltaTime = Time.unscaledDeltaTime;

            if (_hasEye && deltaTime > 0f)
            {
                Vector3 velocity = (eye - _eye) / deltaTime;
                _eyeVelocity = velocity.sqrMagnitude > TeleportSpeed * TeleportSpeed
                    ? Vector3.zero
                    : Vector3.Lerp(_eyeVelocity, velocity, 1f - Mathf.Exp(-deltaTime / VelocitySmoothing));
            }

            _eye = eye;
            _hasEye = true;
        }

        private static int SeasonKey() =>
            Seasons.Instance != null ? (int)Seasons.Instance.Current * 1000 + Mathf.RoundToInt(Seasons.Instance.SnowCover * 100f) : -1;

        // Ближайший к камере или к тому месту, где она скоро будет, тайл без травы — в фоновый расчёт.
        private bool StartNearestTile()
        {
            if (!_hasEye)
                return false;

            Vector3 ahead = _eye + _eyeVelocity * lookAheadSeconds;
            float limit = dressDistance * dressDistance;
            Terrain nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null || _tiles.ContainsKey(terrain) || !TreesReady(terrain))
                    continue;

                // тайл с точкой взлёта рисует её генератор
                if (CustomSpawnRuntimeEnvironmentGenerator.IsRuntimeCopy(terrain.terrainData))
                    continue;

                float distance = Mathf.Min(DistanceToTile(terrain, _eye), DistanceToTile(terrain, ahead));

                if (distance <= limit && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = terrain;
                }
            }

            if (nearest == null)
                return false;

            Tile tile = Prepare(nearest, nearest.terrainData, readSatellite: false);
            _tiles[nearest] = tile;
            _frameMilliseconds += tile.MainMilliseconds;

            if (!RequestSatellite(tile))
                StartJob(tile);

            return true;
        }

        private void StartJob(Tile tile)
        {
            Input input = tile.Input;
            tile.Job = Task.Run(() => Compute(input));
            _running.Add(tile);
        }

        // Спутник тайла читается с GPU без ожидания, расчёт начинается по готовности: ReadPixels в кадре ждал, пока GPU
        // дорисует очередь кадра, — до ~20 мс. false — читать нечего или асинхронно нельзя (тогда прочитано сразу).
        private bool RequestSatellite(Tile tile)
        {
            Texture satellite = SatelliteOf(tile.Source);

            if (satellite == null || !SystemInfo.supportsAsyncGPUReadback)
            {
                tile.Input.Satellite = satellite != null ? ReadSatellite(satellite) : null;
                return false;
            }

            RenderTexture target = RenderTexture.GetTemporary(
                SatelliteCopySize, SatelliteCopySize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(satellite, target);
            _reading++;

            AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
            {
                _reading--;
                Color32[] pixels = request.hasError ? null : request.GetData<Color32>().ToArray();
                RenderTexture.ReleaseTemporary(target);

                // тайл выгружен, трава снята (пора года) или его уже посчитал генератор точки
                if (this == null || !_tiles.TryGetValue(tile.Terrain, out Tile current) || current != tile)
                    return;

                tile.Input.Satellite = pixels;
                StartJob(tile);
            });

            return true;
        }

        private void FinishJob(Tile tile, bool wait = false)
        {
            if (wait)
            {
                // упавший расчёт Wait бросает дальше — ниже он и так уйдёт в предупреждение
                try
                {
                    tile.Job.Wait();
                }
                catch (System.AggregateException)
                {
                }
            }

            _running.Remove(tile);

            if (tile.Job.IsFaulted)
            {
                Debug.LogWarning($"[GroundDressing] Tile dressing failed: {tile.Job.Exception?.GetBaseException().Message}");
                _tiles.Remove(tile.Terrain);
                return;
            }

            tile.Counts = tile.Job.Result;
            tile.Job = null;
        }

        // Посчитанный тайл ближе всех к камере: его копию собирать следующей. Тайл, который сейчас держит генератор
        // точки, ждёт, пока тот вернёт ему исходный TerrainData.
        private Tile NextToBuild()
        {
            Tile nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (Tile tile in _tiles.Values)
            {
                // считается, выгружен, уже одет или его держит генератор точки
                if (tile.Job != null || tile.Terrain == null || tile.Terrain.terrainData != tile.Source)
                    continue;

                if (tile.Counts == null && tile.Copy == null)
                    continue;

                float distance = _hasEye ? DistanceToTile(tile.Terrain, _eye) : 0f;

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = tile;
                }
            }

            return nearest;
        }

        // Копия TerrainData с травой собирается по кадрам: в первый — копия и прототипы, дальше по LayersPerFrame
        // непустых слоёв, в последний она надевается на тайл. Всё одним кадром давало рывки 35–107 мс.
        private void ContinueBuild(Tile tile)
        {
            // тайл выгружен или его взял генератор точки: собранное подождёт
            if (tile.Terrain == null || tile.Terrain.terrainData != tile.Source)
            {
                _building = null;
                return;
            }

            Stopwatch watch = Stopwatch.StartNew();
            bool bound = false;

            try
            {
                if (tile.Copy == null)
                {
                    tile.Copy = CreateCopy(tile);
                }
                else if (tile.Counts != null)
                {
                    ApplyLayers(tile);
                }
                else
                {
                    Bind(tile.Terrain, tile.Copy, NearForGrass(tile));
                    bound = true;
                }
            }
            catch (System.Exception exception)
            {
                // один сломанный тайл не должен останавливать остальные: он остаётся без травы до выгрузки
                Debug.LogWarning($"[GroundDressing] Tile build failed: {exception.Message}");
                Abandon(tile);
                return;
            }

            watch.Stop();
            double milliseconds = watch.Elapsed.TotalMilliseconds;
            tile.MainMilliseconds += milliseconds;
            _frameMilliseconds += milliseconds;

            if (!bound)
                return;

            _building = null;
            _dressedCount++;
            _mainMilliseconds += tile.MainMilliseconds;
        }

        // Тайл без травы до выгрузки: в _tiles он остаётся, чтобы его не брали заново.
        private void Abandon(Tile tile)
        {
            _building = null;
            tile.Failed = true;
            tile.Counts = null;

            if (tile.Copy != null && (tile.Terrain == null || tile.Terrain.terrainData != tile.Copy))
            {
                Destroy(tile.Copy);
                tile.Copy = null;
            }
        }

        // readSatellite false — спутник прочитает RequestSatellite (асинхронно)
        private Tile Prepare(Terrain terrain, TerrainData source, bool readSatellite = true)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Vector3 origin = terrain.transform.position;
            Vector3 size = source.size;
            TreeInstance[] instances = source.treeInstances;
            Vector2[] trees = new Vector2[instances.Length];

            for (int i = 0; i < instances.Length; i++)
                trees[i] = new Vector2(origin.x + instances[i].position.x * size.x, origin.z + instances[i].position.z * size.z);

            FlyPointDetailEntry[] details = ValidDetails();
            Entry[] entries = new Entry[details.Length];
            GameObject[] prototypes = new GameObject[details.Length];
            _Game.Scripts.Enums.Season season = Seasons.Instance != null ? Seasons.Instance.Current : _Game.Scripts.Enums.Season.Summer;
            TreePrototype[] treePrototypes = source.treePrototypes;
            bool[] conifers = new bool[treePrototypes.Length];
            float[] treeLitter = new float[instances.Length];

            for (int i = 0; i < treePrototypes.Length; i++)
                conifers[i] = ForestFloor.Crown(treePrototypes[i].prefab).y > 0.5f;

            for (int i = 0; i < instances.Length; i++)
            {
                int prototype = instances[i].prototypeIndex;
                bool conifer = prototype >= 0 && prototype < conifers.Length && conifers[prototype];
                treeLitter[i] = TreeLitterWeight(conifer, season == _Game.Scripts.Enums.Season.Autumn && !conifer && ForestFloor.BareInAutumn(trees[i].x, trees[i].y));
            }

            for (int i = 0; i < details.Length; i++)
            {
                FlyPointDetailEntry detail = details[i];
                prototypes[i] = detail.Prototype;
                entries[i] = new Entry
                {
                    Density = detail.DensityPerSquareMeter,
                    PatchThreshold = detail.PatchThreshold,
                    PatchScale = detail.PatchScale,
                    ClumpScale = detail.ClumpScale,
                    ClumpStrength = detail.ClumpStrength,
                    TreeCoverScale = detail.TreeCoverScale,
                    BareGroundScale = detail.BareGroundScale,
                    Satellite = detail.SatelliteScales,
                    UseSatellite = detail.UsesSatelliteScales,
                    UnderTrees = detail.UnderTrees,
                    Land = detail.EffectiveLandScales,
                    Season = detail.SeasonScale(season),
                    UnderSnow = detail.HiddenBySnow(season),
                    OnGround = detail.AlignToGround > 0f,
                    Apart = detail.KeepApart ? DetailSpacing.Radius(detail.Prototype) * detail.WidthRange.y : 0f,
                    Seed = 7919 * (i + 1)
                };
            }

            Tile tile = new()
            {
                Terrain = terrain,
                Source = source,
                Season = SeasonKey(),
                Prototypes = prototypes,
                DetailPrototypes = BuildPrototypes(details),
                Input = new Input
                {
                    Origin = new Vector2(origin.x, origin.z),
                    Size = new Vector2(size.x, size.z),
                    Resolution = Mathf.Max(64, resolution),
                    Satellite = readSatellite ? ReadSatellite(SatelliteOf(source)) : null,
                    SatelliteSize = SatelliteCopySize,
                    Land = ReadLand(origin, size, out int landCells, out float[] abandoned),
                    LandCells = landCells,
                    Abandoned = abandoned,
                    Roads = ReadRoads(origin, size, out bool roadsKnown),
                    RoadsKnown = roadsKnown,
                    Craters = ReadCraters(origin, size),
                    Trenches = ReadTrenches(origin, size),
                    Steepness = ReadRelief(source, out float[] normalZ),
                    NormalZ = normalZ,
                    Trees = trees,
                    TreeLitter = treeLitter,
                    Entries = entries,
                    Density = worldDensity,
                    Snow = Seasons.Instance != null ? Seasons.Instance.SnowCover : 0f
                }
            };

            watch.Stop();
            tile.MainMilliseconds = watch.Elapsed.TotalMilliseconds;
            return tile;
        }

        // Расчёт слоёв травы тайла. Идёт в фоновом потоке: только массивы и своя математика, без API Unity.
        private byte[][] Compute(Input input)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int n = input.Resolution;
            float cellX = input.Size.x / n;
            float cellZ = input.Size.y / n;
            float cellArea = cellX * cellZ;
            float[] forest = ForestCover(input, n);
            float[] litter = TreeProximity(input, n, cellX, cellZ);
            float[] bare = new float[n * n];
            float[] blocked = new float[n * n];
            bool hasLand = input.Land != null;
            int landCount = input.LandCells + 2 * LandMargin;
            float[] hardLattice = hasLand ? LandGroups.Hard(input.Land, !input.RoadsKnown) : null;
            // полотно дорог по осям: голо на нём и ClearMargin за краем, к траве — за RoadAreas.Fade
            float[] road = null;

            if (input.Roads is { Length: > 0 })
            {
                road = new float[n * n];
                RoadAreas.Rasterize(input.Roads, input.Origin, input.Size, n, road);
            }

            // воронки: как голо в ячейке — по доле её площади под чашей (мелкая яма меньше ячейки голит её долю)
            float[] crater = null;

            if (input.Craters is { Length: > 0 } || input.Trenches is { Length: > 0 })
            {
                crater = new float[n * n];

                if (input.Craters != null)
                    RasterizeCraters(input.Craters, input.Origin, input.Size, n, crater);

                if (input.Trenches != null)
                    RasterizeTrenches(input.Trenches, input.Origin, input.Size, n, crater);
            }
            float[] forestLattice = hasLand ? LandGroups.Share(input.Land, FlyPointLandGroup.Forest) : null;
            // доля леса по карте в ячейке: листва там лежит почти везде, а не только у деревьев тайла
            float[] forestLand = hasLand ? new float[n * n] : null;
            bool useSatellite = System.Array.Exists(input.Entries, candidate => candidate.UseSatellite);
            // доли зелени, жёлтого, тёмного и голого по пикселям копии спутника (линейный цвет, сглажено), в ячейку — билинейно
            float[] arable = null;
            Vector4[] satelliteStates = useSatellite ? SatelliteStates.Classify(input.Satellite, input.SatelliteSize, out arable) : null;
            Vector4[] state = useSatellite ? new Vector4[n * n] : null;
            // поле, которое карта зовёт лугом или голой землёй (пашня, стерня по спутнику, не на склоне), и край поля без
            // зелени на спутнике растут как поле
            float[] toField = useSatellite && hasLand ? new float[n * n] : null;
            float[] maybeFieldLattice = toField != null ? LandGroups.MaybeFieldShare(input.Land) : null;
            float[] edgeLattice = toField != null ? LandGroups.FieldEdgeShare(input.Land, landCount, landCount) : null;
            // зимой — сколько земли в ячейке снег оставил голой (узор снега шейдера террейна, SnowCover)
            float[] snowFree = input.Snow > 0f && System.Array.Exists(input.Entries, candidate => candidate.UnderSnow) ? new float[n * n] : null;
            // крутизна в ячейке — для лежащего по склону
            float[] steepness = input.Steepness != null && System.Array.Exists(input.Entries, candidate => candidate.OnGround) ? new float[n * n] : null;

            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    Color colour = SampleSatellite(input, (x + 0.5f) / n, (z + 0.5f) / n);
                    float brightness = (colour.r + colour.g + colour.b) / 3f;
                    float saturation = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b)) - Mathf.Min(colour.r, Mathf.Min(colour.g, colour.b));
                    // вода и глубокие тени тёмные, асфальт, бетон и крыши — светлые и серые
                    float water = 1f - SmoothStep(0.06f, 0.12f, brightness);
                    float paved = (1f - SmoothStep(0.03f, 0.07f, saturation)) * SmoothStep(0.28f, 0.4f, brightness);
                    int cell = z * n + x;
                    bare[cell] = SmoothStep(-0.03f, 0.03f, colour.r - colour.g);
                    blocked[cell] = Mathf.Max(Mathf.Max(water, paved), road != null ? road[cell] : 0f);

                    if (crater != null)
                        blocked[cell] = Mathf.Max(blocked[cell], crater[cell]);

                    if (state != null)
                        state[cell] = SatelliteStates.Sample(satelliteStates, input.SatelliteSize, (x + 0.5f) / n, (z + 0.5f) / n);

                    if (snowFree != null)
                        snowFree[cell] = 1f - SnowAt(input, x, z, n, cellX, cellZ, colour);

                    if (steepness != null)
                        steepness[cell] = LandGroups.Sample(input.Steepness, SteepnessNodes, SteepnessNodes,
                            (x + 0.5f) / n * (SteepnessNodes - 1), (z + 0.5f) / n * (SteepnessNodes - 1));

                    if (hasLand)
                    {
                        float gx = LandNode(x, n, input.LandCells);
                        float gz = LandNode(z, n, input.LandCells);
                        float hard = LandGroups.Sample(hardLattice, landCount, landCount, gx, gz);
                        blocked[cell] = Mathf.Max(blocked[cell], SmoothStep(HardGroundEdge.x, HardGroundEdge.y, hard));
                        forestLand[cell] = LandGroups.Sample(forestLattice, landCount, landCount, gx, gz);

                        if (toField != null)
                        {
                            float u = (x + 0.5f) / n;
                            float v = (z + 0.5f) / n;
                            float steep = input.Steepness != null
                                ? LandGroups.Sample(input.Steepness, SteepnessNodes, SteepnessNodes, u * (SteepnessNodes - 1), v * (SteepnessNodes - 1))
                                : 0f;
                            float field = SatelliteStates.Sample(arable, input.SatelliteSize, u, v) *
                                          (1f - SmoothStep(FieldSteepness.x, FieldSteepness.y, steep));
                            toField[cell] = Mathf.Clamp01(
                                LandGroups.Sample(maybeFieldLattice, landCount, landCount, gx, gz) * field +
                                LandGroups.Sample(edgeLattice, landCount, landCount, gx, gz) * (1f - state[cell].x));

                            // брошенное поле — уже не поле: ни стерни, ни всходов
                            if (input.Abandoned != null)
                                toField[cell] *= 1f - LandGroups.Sample(input.Abandoned, landCount, landCount, gx, gz);
                        }
                    }
                }
            }

            byte[][] counts = new byte[input.Entries.Length][];

            for (int e = 0; e < input.Entries.Length; e++)
            {
                Entry entry = input.Entries[e];
                byte[] layer = new byte[n * n];
                counts[e] = layer;
                // множитель вида по земле: луг, поле и его край, лес, болото и берег, посёлок, дороги, голая земля, залежь
                float[] landScales = hasLand ? LandGroups.EntryScales(input.Land, landCount, landCount, entry.Land, input.Abandoned) : null;

                for (int z = 0; z < n; z++)
                {
                    float worldZ = input.Origin.y + (z + 0.5f) * cellZ;
                    float gz = LandNode(z, n, input.LandCells);

                    for (int x = 0; x < n; x++)
                    {
                        int cell = z * n + x;

                        if (blocked[cell] >= 0.99f)
                            continue;

                        float land = hasLand
                            ? LandGroups.Sample(landScales, landCount, landCount, LandNode(x, n, input.LandCells), gz)
                            : entry.Land.Meadow;

                        if (entry.UseSatellite && toField != null)
                            land = Mathf.Lerp(land, entry.Land.Field, toField[cell]);

                        if (land <= 0.0001f)
                            continue;

                        float worldX = input.Origin.x + (x + 0.5f) * cellX;
                        // листва — под деревьями, а в лесу по карте почти везде; трава под самыми кронами реже
                        float cover = entry.UnderTrees
                            ? hasLand ? Mathf.Max(litter[cell], forestLand[cell] * LandGroups.ForestLitterCover) : litter[cell]
                            : 1f - litter[cell] * 0.5f;
                        // что на земле по спутнику: новая запись — по долям состояний, старая — прежний «голый» (r > g)
                        float ground = entry.UseSatellite
                            ? entry.Satellite.Of(state[cell])
                            : Mathf.Lerp(1f, entry.BareGroundScale, bare[cell]);
                        float expected = entry.Density * entry.Season * input.Density * cellArea * cover * (1f - blocked[cell]) * land *
                                         Mathf.Lerp(entry.TreeCoverScale.x, entry.TreeCoverScale.y, forest[cell]) * ground;

                        if (entry.UnderSnow && snowFree != null)
                            expected *= snowFree[cell];

                        if (entry.OnGround && steepness != null)
                            expected *= FlyPointDetailEntry.GroundSlopeScale(steepness[cell]);

                        if (expected <= 0.0001f)
                            continue;

                        if (entry.PatchThreshold > 0f)
                            expected *= SmoothStep(entry.PatchThreshold, entry.PatchThreshold + 0.12f,
                                Contrasted(worldX, worldZ, entry.PatchScale, entry.Seed));

                        if (entry.ClumpStrength > 0f)
                            expected *= Mathf.Lerp(1f, SmoothStep(0.3f, 0.8f, Contrasted(worldX, worldZ, entry.ClumpScale, entry.Seed + 1)) * 2f,
                                entry.ClumpStrength);

                        if (expected <= 0f)
                            continue;

                        // ячейки — по мировым координатам: на стыке тайлов узор продолжается
                        int cellWorldX = Mathf.FloorToInt(worldX / cellX);
                        int cellWorldZ = Mathf.FloorToInt(worldZ / cellZ);
                        int whole = (int)expected;
                        int extra = Hash01(cellWorldX, cellWorldZ, entry.Seed + 2) < expected - whole ? 1 : 0;
                        layer[cell] = (byte)Mathf.Min(whole + extra, MaxDetailPerCell);
                    }
                }
            }

            KeepApart(input, counts, n, cellX, cellZ);
            watch.Stop();

            lock (_tiles)
                _jobMilliseconds += watch.Elapsed.TotalMilliseconds;

            return counts;
        }

        // Пни, брёвна, штабеля — по одному в ячейке, не у стволов деревьев тайла, не друг у друга и не у дорог (DetailSpacing).
        private static void KeepApart(Input input, byte[][] counts, int n, float cellX, float cellZ)
        {
            List<DetailSpacing.Candidate> candidates = new();

            for (int e = 0; e < input.Entries.Length; e++)
            {
                float radius = input.Entries[e].Apart;

                if (radius <= 0f)
                    continue;

                byte[] layer = counts[e];

                for (int cell = 0; cell < layer.Length; cell++)
                {
                    if (layer[cell] == 0)
                        continue;

                    layer[cell] = 0;
                    int x = cell % n;
                    int z = cell / n;
                    float worldX = input.Origin.x + (x + 0.5f) * cellX;
                    float worldZ = input.Origin.y + (z + 0.5f) * cellZ;
                    candidates.Add(new DetailSpacing.Candidate
                    {
                        Position = new Vector2(worldX, worldZ),
                        Radius = radius,
                        Priority = Hash01(Mathf.FloorToInt(worldX / cellX), Mathf.FloorToInt(worldZ / cellZ), input.Entries[e].Seed + 3),
                        Layer = e,
                        Cell = cell
                    });
                }
            }

            if (candidates.Count == 0)
                return;

            List<Vector3> trunks = new(input.Trees.Length);

            foreach (Vector2 tree in input.Trees)
                trunks.Add(new Vector3(tree.x, tree.y, DetailSpacing.TrunkRadius));

            float slop = 0.5f * Mathf.Sqrt(cellX * cellX + cellZ * cellZ);

            foreach (DetailSpacing.Candidate kept in DetailSpacing.Resolve(candidates, slop, trunks, input.Roads))
                counts[kept.Layer][kept.Cell] = 1;
        }

        // Земля тайла по карте земель GeoWorld, на главном потоке: классы в узлах через LandCoverStep, на узел за краем
        // тайла. Из неё — вода, дороги и здания без травы (по цвету спутника светлые и зеленоватые реки читались лугом)
        // и группы земли для видов травы. null — у мира карты нет, тогда только спутник и деревья тайла.
        // abandoned — узлы брошенных полей (там шли бои: CraterField.AbandonedAt): пашня в них — луг (залежь), null — таких нет
        private static byte[] ReadLand(Vector3 origin, Vector3 size, out int cells, out float[] abandoned)
        {
            cells = 0;
            abandoned = null;

            if (GeoLandCover.At(origin.x + size.x * 0.5f, origin.z + size.z * 0.5f) == null)
                return null;

            cells = Mathf.Max(1, Mathf.RoundToInt(size.x / LandCoverStep));
            int count = cells + 2 * LandMargin;
            byte[] land = new byte[count * count];
            float stepX = size.x / cells;
            float stepZ = size.z / cells;

            for (int z = 0; z < count; z++)
            {
                for (int x = 0; x < count; x++)
                {
                    Vector3 point = new(origin.x + (x - LandMargin + 0.5f) * stepX, 0f, origin.z + (z - LandMargin + 0.5f) * stepZ);

                    if (GeoLandCover.TrySample(point, out GeoLandClass value))
                    {
                        if (CraterField.AbandonedAt(point.x, point.z))
                        {
                            abandoned ??= new float[count * count];
                            abandoned[z * count + x] = 1f;

                            if (value == GeoLandClass.Cropland)
                                value = GeoLandClass.Grass;
                        }

                        land[z * count + x] = (byte)value;
                    }
                }
            }

            return land;
        }

        // Оси дорог у тайла и за его краем на RoadReach (бревно у края тайла не ляжет на дорогу соседнего), на главном потоке;
        // known — тайл в выгрузках дорог. null — дорог рядом нет или их не запекали.
        private static readonly List<int> FoundCraters = new();

        private static CraterField.Crater[] ReadCraters(Vector3 origin, Vector3 size)
        {
            CraterField field = CraterField.Instance;

            if (field == null || field.craters.Length == 0)
                return null;

            field.Query(new Rect(origin.x, origin.z, size.x, size.z), FoundCraters);

            if (FoundCraters.Count == 0)
                return null;

            var result = new CraterField.Crater[FoundCraters.Count];

            for (int i = 0; i < result.Length; i++)
                result[i] = field.craters[FoundCraters[i]];

            return result;
        }

        private static readonly List<int> FoundTrenches = new();

        private static CraterField.Trench[] ReadTrenches(Vector3 origin, Vector3 size)
        {
            CraterField field = CraterField.Instance;

            if (field == null || field.trenches.Length == 0)
                return null;

            field.QueryTrenches(new Rect(origin.x, origin.z, size.x, size.z), FoundTrenches);

            if (FoundTrenches.Count == 0)
                return null;

            var result = new CraterField.Trench[FoundTrenches.Count];

            for (int i = 0; i < result.Length; i++)
                result[i] = field.trenches[FoundTrenches[i]];

            return result;
        }

        // Голость ячейки от окопов. Окоп — меш тайла (GeoTrenchMesh): ров, бровка и брустверы до конца его бровки, террейн
        // под ним срезан ниже, и трава с него проросла бы сквозь меш — там травы нет; дальше, на выброшенном грунте внешнего
        // склона бруствера, — реже.
        private static void RasterizeTrenches(CraterField.Trench[] trenches, Vector2 origin, Vector2 size, int n, float[] result)
        {
            float cellX = size.x / n, cellZ = size.y / n;

            foreach (CraterField.Trench t in trenches)
            {
                if (t.points == null || t.points.Length < 4)
                    continue;

                float half = 0.5f * Mathf.Max(0.3f, t.width) * (1f + 0.5f * t.age);
                var section = new GeoWorld.Editor.GeoTrenchSection(t.width, t.depth, t.age, GeoWorld.Editor.GeoTrenchSection.LipWidth(1f));
                float bare = section.CutTo(1f), bank = Mathf.Lerp(0.6f, 0.2f, t.age);
                float reach = Mathf.Max(bare, half + 2.4f);

                for (int k = 0; k + 3 < t.points.Length; k += 2)
                {
                    var a = new Vector2(t.points[k], t.points[k + 1]);
                    var b = new Vector2(t.points[k + 2], t.points[k + 3]);
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - reach - origin.x) / cellX)), x1 = Mathf.Min(n - 1, Mathf.FloorToInt((Mathf.Max(a.x, b.x) + reach - origin.x) / cellX));
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, b.y) - reach - origin.y) / cellZ)), z1 = Mathf.Min(n - 1, Mathf.FloorToInt((Mathf.Max(a.y, b.y) + reach - origin.y) / cellZ));
                    Vector2 ab = b - a;
                    float lengthSq = Mathf.Max(ab.sqrMagnitude, 1e-6f);

                    for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = new Vector2(origin.x + (x + 0.5f) * cellX, origin.y + (z + 0.5f) * cellZ);
                        float along = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq);
                        float d = Vector2.Distance(p, a + ab * along);
                        float value = d < bare ? 1f : d < reach ? bank : 0f;
                        int cell = z * n + x;

                        if (value > result[cell])
                            result[cell] = value;
                    }
                }
            }
        }

        // Голость ячейки от воронок: в чаше свежей воронки и в прудке трава не растёт, в чаше старой — вдвое реже, у свежей
        // вокруг ямы — вывороченный грунт, тоже реже. Доля — по площади чаши в ячейке (ячейки ~2 м: яма в метр голит четверть).
        private static void RasterizeCraters(CraterField.Crater[] craters, Vector2 origin, Vector2 size, int n, float[] result)
        {
            float cellX = size.x / n, cellZ = size.y / n, cellArea = cellX * cellZ;

            foreach (CraterField.Crater c in craters)
            {
                float radius = Mathf.Max(0.05f, c.diameter * 0.5f);
                bool fresh = c.age < 0.35f;
                float strength = c.water || fresh ? 1f : 0.5f;
                float reach = fresh ? radius * 1.6f : radius;
                int centreX = Mathf.FloorToInt((c.x - origin.x) / cellX), centreZ = Mathf.FloorToInt((c.z - origin.y) / cellZ);
                int x0 = Mathf.Max(0, Mathf.FloorToInt((c.x - reach - origin.x) / cellX)), x1 = Mathf.Min(n - 1, Mathf.FloorToInt((c.x + reach - origin.x) / cellX));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((c.z - reach - origin.y) / cellZ)), z1 = Mathf.Min(n - 1, Mathf.FloorToInt((c.z + reach - origin.y) / cellZ));
                float share = Mathf.Min(1f, Mathf.PI * radius * radius / cellArea);

                for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = origin.x + (x + 0.5f) * cellX - c.x, dz = origin.y + (z + 0.5f) * cellZ - c.z;
                    float s = Mathf.Sqrt(dx * dx + dz * dz) / radius;
                    // ячейка с центром ямы — хотя бы по её доле, остальные — по расстоянию до центра
                    float inBowl = s < 1f ? 1f : x == centreX && z == centreZ ? share : 0f;
                    float spoil = fresh && s < 1.6f ? 0.6f * (1f - SmoothStep(1f, 1.6f, s)) : 0f;
                    float value = Mathf.Max(inBowl * strength, spoil);
                    int cell = z * n + x;

                    if (value > result[cell])
                        result[cell] = value;
                }
            }
        }

        private static RoadAreas.Segment[] ReadRoads(Vector3 origin, Vector3 size, out bool known)
        {
            RoadAreas roads = RoadAreas.Instance;
            known = roads != null && roads.Covers(origin.x + size.x * 0.5f, origin.z + size.z * 0.5f);
            return roads != null
                ? roads.SegmentsIn(new Rect(origin.x - RoadReach, origin.z - RoadReach, size.x + RoadReach * 2f, size.z + RoadReach * 2f))
                : null;
        }

        // Ячейка деталей i из n по стороне тайла — в узлах решётки земли (узлы — центры её ячеек, со сдвигом на край).
        private static float LandNode(int i, int n, int cells) => LandMargin + (i + 0.5f) / n * cells - 0.5f;

        // Доля леса вокруг ячейки: деревья тайла в квадратах ForestCellSize, сглажено билинейно.
        private static float[] ForestCover(Input input, int n)
        {
            int coarse = Mathf.Max(1, Mathf.CeilToInt(input.Size.x / ForestCellSize));
            float[] perCell = new float[coarse * coarse];

            foreach (Vector2 tree in input.Trees)
            {
                int cx = Mathf.Clamp((int)((tree.x - input.Origin.x) / input.Size.x * coarse), 0, coarse - 1);
                int cz = Mathf.Clamp((int)((tree.y - input.Origin.y) / input.Size.y * coarse), 0, coarse - 1);
                perCell[cz * coarse + cx]++;
            }

            float hectares = ForestCellSize * ForestCellSize / SquareMetersPerHectare;

            for (int i = 0; i < perCell.Length; i++)
                perCell[i] = SmoothStep(OpenTreesPerHectare, ForestTreesPerHectare, perCell[i] / hectares);

            float[] cover = new float[n * n];

            for (int z = 0; z < n; z++)
            {
                float fz = Mathf.Clamp((z + 0.5f) / n * coarse - 0.5f, 0f, coarse - 1.001f);
                int z0 = (int)fz;
                float tz = fz - z0;
                int z1 = Mathf.Min(z0 + 1, coarse - 1);

                for (int x = 0; x < n; x++)
                {
                    float fx = Mathf.Clamp((x + 0.5f) / n * coarse - 0.5f, 0f, coarse - 1.001f);
                    int x0 = (int)fx;
                    float tx = fx - x0;
                    int x1 = Mathf.Min(x0 + 1, coarse - 1);
                    float bottom = Mathf.Lerp(perCell[z0 * coarse + x0], perCell[z0 * coarse + x1], tx);
                    float top = Mathf.Lerp(perCell[z1 * coarse + x0], perCell[z1 * coarse + x1], tx);
                    cover[z * n + x] = Mathf.Lerp(bottom, top, tz);
                }
            }

            return cover;
        }

        // Листвы под деревом: хвоя её почти не даёт; осенью голое (SeasonTrees.hlsl, ForestFloor.BareInAutumn) уже сбросило
        // всё — под ним гуще, у ещё одетых — часть.
        private static float TreeLitterWeight(bool conifer, bool bareInAutumn) => conifer ? 0.35f : bareInAutumn ? 1f : 0.7f;

        // Близость к стволам для листвы: у ствола — вес дерева (TreeLitter), 0 дальше LitterOuter.
        private static float[] TreeProximity(Input input, int n, float cellX, float cellZ)
        {
            float[] proximity = new float[n * n];

            for (int t = 0; t < input.Trees.Length; t++)
            {
                Vector2 tree = input.Trees[t];
                float weight = input.TreeLitter != null && t < input.TreeLitter.Length ? input.TreeLitter[t] : 1f;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((tree.x - LitterOuter - input.Origin.x) / cellX));
                int x1 = Mathf.Min(n - 1, Mathf.FloorToInt((tree.x + LitterOuter - input.Origin.x) / cellX));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((tree.y - LitterOuter - input.Origin.y) / cellZ));
                int z1 = Mathf.Min(n - 1, Mathf.FloorToInt((tree.y + LitterOuter - input.Origin.y) / cellZ));

                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = input.Origin.x + (x + 0.5f) * cellX - tree.x;
                        float dz = input.Origin.y + (z + 0.5f) * cellZ - tree.y;
                        float value = (1f - SmoothStep(LitterInner, LitterOuter, Mathf.Sqrt(dx * dx + dz * dz))) * weight;
                        int cell = z * n + x;

                        if (value > proximity[cell])
                            proximity[cell] = value;
                    }
                }
            }

            return proximity;
        }

        // Покров снега в ячейке, как его рисует шейдер террейна: рельеф из узлов крутизны и нормали, земля — цвет спутника.
        private static float SnowAt(Input input, int x, int z, int n, float cellX, float cellZ, Color colour)
        {
            float u = (x + 0.5f) / n * (SteepnessNodes - 1);
            float v = (z + 0.5f) / n * (SteepnessNodes - 1);
            float steep = LandGroups.Sample(input.Steepness, SteepnessNodes, SteepnessNodes, u, v);
            float normalY = Mathf.Cos(steep * Mathf.Deg2Rad);
            float normalZ = LandGroups.Sample(input.NormalZ, SteepnessNodes, SteepnessNodes, u, v);
            float luminance = Mathf.Pow(colour.r, 2.2f) * 0.299f + Mathf.Pow(colour.g, 2.2f) * 0.587f + Mathf.Pow(colour.b, 2.2f) * 0.114f;
            return SnowCover.At(input.Origin.x + (x + 0.5f) * cellX, input.Origin.y + (z + 0.5f) * cellZ,
                new Vector3(0f, normalY, normalZ), luminance, input.Snow, cellX);
        }

        private static Color SampleSatellite(Input input, float u, float v)
        {
            if (input.Satellite == null)
                return new Color(0.3f, 0.36f, 0.22f);

            int size = input.SatelliteSize;
            float fx = Mathf.Clamp(u * size - 0.5f, 0f, size - 1.001f);
            float fy = Mathf.Clamp(v * size - 0.5f, 0f, size - 1.001f);
            int x0 = (int)fx;
            int y0 = (int)fy;
            float tx = fx - x0;
            float ty = fy - y0;
            Color a = input.Satellite[y0 * size + x0];
            Color b = input.Satellite[y0 * size + x0 + 1];
            Color c = input.Satellite[(y0 + 1) * size + x0];
            Color d = input.Satellite[(y0 + 1) * size + x0 + 1];
            return Color.Lerp(Color.Lerp(a, b, tx), Color.Lerp(c, d, tx), ty);
        }

        // Крутизна (градусы) и z нормали в узлах.
        private static float[] ReadRelief(TerrainData data, out float[] normalZ)
        {
            float[] result = new float[SteepnessNodes * SteepnessNodes];
            normalZ = new float[SteepnessNodes * SteepnessNodes];
            float step = 1f / (SteepnessNodes - 1);

            for (int z = 0; z < SteepnessNodes; z++)
            {
                for (int x = 0; x < SteepnessNodes; x++)
                {
                    result[z * SteepnessNodes + x] = data.GetSteepness(x * step, z * step);
                    normalZ[z * SteepnessNodes + x] = data.GetInterpolatedNormal(x * step, z * step).z;
                }
            }

            return result;
        }

        private Texture SatelliteOf(TerrainData source)
        {
            foreach (TerrainLayer layer in source.terrainLayers)
            {
                if (layer == null || layer.diffuseTexture == null ||
                    layer == _profile.GrassLayer || layer == _profile.SoilLayer || layer == _profile.GravelLayer)
                {
                    continue;
                }

                return layer.diffuseTexture;
            }

            return null;
        }

        /// <summary>
        /// Спутник тайла сжат DXT1 Crunched: GetPixel с такой текстуры отдаёт белый даже при Read/Write. Уменьшенная
        /// копия через GPU, 512 px на тайл (~2 м на пиксель): межи и полосы полей не размываются. Сразу — только под
        /// экраном загрузки (TryGetWorldDetail); в полёте — RequestSatellite.
        /// </summary>
        private static Color32[] ReadSatellite(Texture satellite)
        {
            if (satellite == null)
                return null;

            RenderTexture target = RenderTexture.GetTemporary(
                SatelliteCopySize, SatelliteCopySize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(satellite, target);
            RenderTexture.active = target;

            Texture2D copy = new(SatelliteCopySize, SatelliteCopySize, TextureFormat.RGBA32, false, false);
            copy.ReadPixels(new Rect(0, 0, SatelliteCopySize, SatelliteCopySize), 0, 0);
            copy.Apply(false);
            Color32[] pixels = copy.GetPixels32();

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Destroy(copy);
            return pixels;
        }

        private TerrainData CreateCopy(Tile tile)
        {
            TerrainData copy = Instantiate(tile.Source);
            // GeoTerrainTreeLoader и генератор точки узнают данные тайла по имени
            copy.name = tile.Source.name;
            copy.SetDetailResolution(tile.Input.Resolution, DetailPatchResolution);
            copy.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
            copy.detailPrototypes = tile.DetailPrototypes;
            tile.AppliedLayers = 0;
            return copy;
        }

        // Следующие LayersPerFrame непустых слоёв — в копию (пустые в новой копии и так пустые). Когда легли все,
        // посчитанные слои больше не нужны.
        private void ApplyLayers(Tile tile)
        {
            int n = tile.Input.Resolution;

            if (_grid == null || _grid.GetLength(0) != n)
                _grid = new int[n, n];

            for (int applied = 0; applied < LayersPerFrame && tile.AppliedLayers < tile.Counts.Length; tile.AppliedLayers++)
            {
                byte[] layer = tile.Counts[tile.AppliedLayers];

                if (!HasDetail(layer))
                    continue;

                tile.Copy.SetDetailLayer(0, 0, tile.AppliedLayers, ToGrid(layer, n, _grid));
                applied++;
            }

            if (tile.AppliedLayers >= tile.Counts.Length)
                tile.Counts = null;
        }

        // withGrass false — тайл дальше дальности травы: рендерер травы соберёт UpdateGrassShown, когда тот приблизится
        private static void Bind(Terrain terrain, TerrainData data, bool withGrass)
        {
            // тайл без деревьев GeoWorld оставляет без Nature Renderer — ставим свой
            NatureRendererTerrain.Ensure(terrain);
            NatureRendererTerrain.SwapGrass(terrain, data, withGrass);
        }

        // Выключение травы мира: тайлам — исходный TerrainData, копиям — удаление. Копию, которую сейчас держит
        // генератор точки как исходные данные тайла, не трогаем: он вернёт её тайлу, а уберёт её выгрузка тайла.
        private void UndressAll()
        {
            // идущие расчёты не ждём (до ~10 с в главном потоке при смене поры года): их тайлы ниже уходят из _tiles, и
            // результат, когда досчитается, никому не нужен
            _running.Clear();
            _building = null;
            _gone.Clear();

            foreach (KeyValuePair<Terrain, Tile> pair in _tiles)
            {
                Tile tile = pair.Value;
                TerrainData given = tile.StaleCopy != null ? tile.StaleCopy : tile.Copy;

                // тайл держит генератор точки, и его исходные данные — эта копия: её не трогаем, но трава в ней прежней
                // поры — генератору посчитаем свежую (TryGetWorldDetail), а копию сменим, когда он её вернёт
                if (given != null && tile.Terrain != null && tile.Terrain.terrainData != given &&
                    tile.Terrain.terrainData != tile.Source)
                {
                    tile.StaleCopy = given;
                    tile.Copy = null;
                    tile.Counts = null;
                    continue;
                }

                if (tile.Copy != null && tile.Terrain != null && tile.Source != null && tile.Terrain.terrainData == tile.Copy)
                {
                    NatureRendererTerrain.SwapGrass(tile.Terrain, tile.Source);
                    TerrainCollider collider = tile.Terrain.GetComponent<TerrainCollider>();

                    // коллайдеру копию мог отдать генератор точки, когда возвращал тайлу его данные
                    if (collider != null && collider.terrainData == tile.Copy)
                        collider.terrainData = tile.Source;
                }

                if (tile.Copy != null)
                    Destroy(tile.Copy, RuntimeDataDestroyDelay);

                _gone.Add(pair.Key);
            }

            foreach (Terrain terrain in _gone)
                _tiles.Remove(terrain);
        }

        // Выгруженный тайл уничтожен вместе с террейном: его копия больше не нужна.
        private void ForgetUnloadedTiles()
        {
            _gone.Clear();

            foreach (KeyValuePair<Terrain, Tile> pair in _tiles)
            {
                if (pair.Key == null && pair.Value.Job == null)
                    _gone.Add(pair.Key);
            }

            foreach (Terrain terrain in _gone)
            {
                if (!_tiles.Remove(terrain, out Tile tile))
                    continue;

                if (tile.Copy != null)
                    Destroy(tile.Copy, RuntimeDataDestroyDelay);

                if (tile.StaleCopy != null)
                    Destroy(tile.StaleCopy, RuntimeDataDestroyDelay);
            }
        }

        // Генератор точки вернул тайлу копию прежней поры (StaleCopy): тайлу — его исходные данные, а свежую копию соберёт
        // NextToBuild из посчитанного; если посчитать не успели — тайл одевается заново.
        private void ReplaceStaleCopies()
        {
            _gone.Clear();

            foreach (KeyValuePair<Terrain, Tile> pair in _tiles)
            {
                Tile tile = pair.Value;

                if (tile.StaleCopy == null || tile.Terrain == null || tile.Terrain.terrainData != tile.StaleCopy)
                    continue;

                NatureRendererTerrain.SwapGrass(tile.Terrain, tile.Source);
                TerrainCollider collider = tile.Terrain.GetComponent<TerrainCollider>();

                if (collider != null && collider.terrainData == tile.StaleCopy)
                    collider.terrainData = tile.Source;

                Destroy(tile.StaleCopy, RuntimeDataDestroyDelay);
                tile.StaleCopy = null;

                if (tile.Copy == null && tile.Counts == null)
                    _gone.Add(pair.Key);
            }

            foreach (Terrain terrain in _gone)
                _tiles.Remove(terrain);
        }

        private int CountDressed()
        {
            int dressed = 0;

            foreach (Tile tile in _tiles.Values)
            {
                if (tile.Terrain != null && tile.Copy != null && tile.Terrain.terrainData == tile.Copy)
                    dressed++;
            }

            return dressed;
        }

        private int CountShown()
        {
            int shown = 0;

            foreach (Tile tile in _tiles.Values)
            {
                if (tile.Terrain != null && tile.Copy != null && tile.Terrain.terrainData == tile.Copy &&
                    NatureRendererTerrain.GrassShown(tile.Terrain) == true)
                    shown++;
            }

            return shown;
        }

        private FlyPointDetailEntry[] ValidDetails()
        {
            List<FlyPointDetailEntry> details = new();

            foreach (FlyPointDetailEntry detail in _profile.Details ?? System.Array.Empty<FlyPointDetailEntry>())
            {
                if (detail != null && detail.Prototype != null)
                    details.Add(detail);
            }

            return details.ToArray();
        }

        // Те же настройки прототипов, что у генератора точки: на его тайлах прототипы совпадают по объекту.
        private static DetailPrototype[] BuildPrototypes(FlyPointDetailEntry[] details)
        {
            DetailPrototype[] prototypes = new DetailPrototype[details.Length];

            for (int i = 0; i < details.Length; i++)
            {
                FlyPointDetailEntry detail = details[i];
                prototypes[i] = new DetailPrototype
                {
                    prototype = detail.Prototype,
                    usePrototypeMesh = true,
                    renderMode = DetailRenderMode.VertexLit,
                    useInstancing = true,
                    minWidth = detail.WidthRange.x,
                    maxWidth = detail.WidthRange.y,
                    minHeight = detail.HeightRange.x,
                    maxHeight = detail.HeightRange.y,
                    healthyColor = detail.HealthyColor,
                    dryColor = detail.DryColor,
                    noiseSpread = detail.NoiseSpread,
                    alignToGround = detail.PrototypeAlignToGround
                };
            }

            return prototypes;
        }

        private static int[,] ToGrid(byte[] counts, int n, int[,] grid)
        {
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                    grid[z, x] = counts[z * n + x];
            }

            return grid;
        }

        private static bool HasDetail(byte[] counts)
        {
            foreach (byte count in counts)
            {
                if (count != 0)
                    return true;
            }

            return false;
        }

        private static bool TreesReady(Terrain terrain)
        {
            // и виды деревьев расставлены (TileTreeSpecies меняет деревья в той же копии TerrainData, с которой снимается копия травы)
            GeoTerrainTreeLoader trees = terrain.GetComponent<GeoTerrainTreeLoader>();
            return (trees == null || trees.Ready) && TileTreeSpecies.Ready(terrain);
        }

        private static float DistanceToTile(Terrain terrain, Vector3 point)
        {
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            float dx = Mathf.Max(origin.x - point.x, 0f, point.x - (origin.x + size.x));
            float dz = Mathf.Max(origin.z - point.z, 0f, point.z - (origin.z + size.z));
            return dx * dx + dz * dz;
        }

        // Шум для фонового потока (Mathf.PerlinNoise — вызов движка): сглаженный шум по значениям в узлах сетки.
        private static float Contrasted(float x, float z, float scale, int seed) =>
            Mathf.Clamp01((ValueNoise(x / scale, z / scale, seed) - 0.5f) * 1.8f + 0.5f);

        private static float ValueNoise(float x, float z, int seed)
        {
            int x0 = Mathf.FloorToInt(x);
            int z0 = Mathf.FloorToInt(z);
            float tx = x - x0;
            float tz = z - z0;
            tx = tx * tx * (3f - 2f * tx);
            tz = tz * tz * (3f - 2f * tz);
            float bottom = Mathf.Lerp(Hash01(x0, z0, seed), Hash01(x0 + 1, z0, seed), tx);
            float top = Mathf.Lerp(Hash01(x0, z0 + 1, seed), Hash01(x0 + 1, z0 + 1, seed), tx);
            return Mathf.Lerp(bottom, top, tz);
        }

        private static float Hash01(int x, int z, int seed)
        {
            unchecked
            {
                uint hash = (uint)(x * 374761393 + z * 668265263 + seed * 1442695041);
                hash = (hash ^ (hash >> 13)) * 1274126177u;
                hash ^= hash >> 16;
                return (hash & 0x00ffffffu) / 16777216f;
            }
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
