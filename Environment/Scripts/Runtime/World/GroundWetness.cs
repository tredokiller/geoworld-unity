using System.Collections.Generic;
using _Game.Scripts.Drone;
using _Game.Scripts.Locations;
using GeoWorld;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif
using UnityEngine;
using UnityEngine.SceneManagement;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Мокрая земля и лужи в дождь. Считает, сколько шёл дождь (Enviro, эффект «Rain»), и отдаёт шейдерам:
    ///  - _GroundWetness: земля темнеет и блестит за ~12 с дождя, сохнет за ~2.5 мин;
    ///  - _PuddleLevel: лужи — с ~30 с дождя в полную силу (земля сперва впитывает), набираются за ~2.5 мин, высыхают
    ///    за ~5 мин; короткая передышка не сбрасывает пропитку, и лужи возвращаются сразу;
    ///  - _WaterRain: сила дождя для колец от капель на реках, озёрах и лужах.
    /// Лужи рисует шейдер террейна Game/Terrain Lit (Wet) (GroundWetness.hlsl): его ставим тайлам GeoWorld вместо URP
    /// Terrain Lit — копией их материала (материалы тайлов из бандлов не трогаем; другие террейны — нет: их наборы
    /// ключевых слов в билде не держит ни один материал), а точке взлёта — через её Resources/Terrain/TakeoffTerrainLit.
    /// Где стоять воде, шейдеры террейна и травы (трава в луже оседает под воду) берут из карты (_PuddleLand, 8 м на
    /// тексель, окно 2 км вокруг камеры) по карте земель GeoWorld и уклону рельефа: R — пригодность под лужи (грунтовки
    /// и тропинки, голая земля, болото, пашня, гравий; асфальт и посёлок реже, луг и лес мало, вода, здания и рельсы —
    /// нет), G — болото, где вода стоит и без дождя, B — пашня (летом зелёный посев снимка — убранное поле, стерня:
    /// Seasons.hlsl SeasonHarvest), A — направление рядков поля OSM (FieldAreas: 1 + азимут/180 × 254, 0 — не поле;
    /// FieldRows.hlsl читает его без интерполяции). На склоне круче ~8° воды нет. Карта строится по строкам за
    /// несколько кадров и переезжает за камерой; под ней дальняя — та же, 32 м на тексель в окне 8 км, на все
    /// загруженные тайлы (с высоты лужи и болото иначе обрывались прямой линией по краю ближнего окна).
    /// В дождь темнеет и обзорная карта GeoWorld за загруженными тайлами — как в среднем мокрая земля, а тайл получает
    /// мокрый материал в кадре, когда загрузился: край загруженного в дождь не виден.
    /// Один на сцену (на объекте WeatherManager).
    /// </summary>
    public sealed class GroundWetness : MonoBehaviour
    {
        private const string RainEffectName = "Rain";
        private const string WetShaderName = "Game/Terrain Lit (Wet)";
        private const string UrpTerrainShaderName = "Universal Render Pipeline/Terrain/Lit";
        private const string PerPixelNormal = "_TERRAIN_INSTANCED_PERPIXEL_NORMAL";
        private const float WeatherInterval = 0.25f;
        private const float TerrainInterval = 0.5f;
        private const float OverviewSearchInterval = 2f;
        // шейдер обзорной карты с порой года и снегом (копия шейдера пакета); материал в Resources держит его в билде
        private const string OverviewSeasonsMaterial = "GeoWorld/Overview (seasons)";
        private const string OverviewSeasonsShader = "Game/GeoWorld Overview (Seasons)";
        // насколько темнеет мокрая обзорная карта на шейдере пакета (без подмены): мокрая земля в шейдере террейна темнеет
        // до 0.8 (трава) – 0.6 (грунт)
        private const float OverviewWetTone = 0.72f;
        // яркость обзорной карты на своём шейдере (мокнет она в нём сама, как земля тайлов)
        private const float OverviewTone = 1f;
        // дождь слабее этого — уже не дождь
        private const float RainThreshold = 0.05f;
        // пропитка (с дождя в полную силу) до первых луж и её спад без дождя
        private const float SoakSeconds = 30f;
        private const float SoakDrySeconds = 240f;
        private const float WetSeconds = 12f;
        private const float WetDrySeconds = 150f;
        private const float FillSeconds = 150f;
        private const float PuddleDrySeconds = 300f;
        // карты мест для воды, ближняя и дальняя: тексели, их размер (м), насколько камера отходит от центра окна, пока оно
        // не переедет за ней (дальнее накрывает загруженные тайлы — до 2.6 км от камеры), и сколько строк считать за кадр
        private const int LandSize = 256;
        // байт на тексель: R лужи, G болото, B пашня, A направление рядков поля (RGBA32)
        private const int LandChannels = 4;
        private const float LandTexel = 8f;
        private const float LandRecenterDistance = 512f;
        private const float FarTexel = 32f;
        private const float FarRecenterDistance = 1400f;
        private const int LandRowsPerFrame = 4;
        // пригодность под лужи там, где карты земель нет (как луг)
        private const float DefaultSuitability = 0.25f;
        // уклон (°), с которого вода не держится, и до которого держится вся — как ровность в шейдере террейна
        private const float FlatSlope = 3.6f;
        private const float SteepSlope = 8.1f;

        private static readonly int WetnessId = Shader.PropertyToID("_GroundWetness");
        private static readonly int PuddleLevelId = Shader.PropertyToID("_PuddleLevel");
        private static readonly int RainId = Shader.PropertyToID("_WaterRain");
        private static readonly int LandId = Shader.PropertyToID("_PuddleLand");
        private static readonly int LandRectId = Shader.PropertyToID("_PuddleLandRect");
        private static readonly int FarLandId = Shader.PropertyToID("_PuddleFarLand");
        private static readonly int FarLandRectId = Shader.PropertyToID("_PuddleFarRect");
        private static readonly int OverviewTintId = Shader.PropertyToID("_Tint");

        public static GroundWetness Instance { get; private set; }

        private readonly Dictionary<Material, Material> _wetMaterials = new();
        private readonly HashSet<Terrain> _notTiles = new();
        private readonly List<Material> _gone = new();
        private readonly LandMap _near = new(LandTexel, LandRecenterDistance, LandId, LandRectId, "Puddle land");
        private readonly LandMap _far = new(FarTexel, FarRecenterDistance, FarLandId, FarLandRectId, "Puddle land (far)");
        private readonly List<MeshRenderer> _overviewLayers = new();
        private Shader _wetShader;
        private GeoOverview _overview;
        private float _overviewWetness = -1f;
        private float _nextOverviewSearch;
        private float _rain;
        private float _soak;
        private float _nextWeather;
        private float _nextTerrains;

        // Окно карты мест для воды, LandChannels байт на тексель: R — пригодность под лужи, G — болото, B — пашня, A — рядки. Новое
        // окно строится во втором массиве и подменяет готовое целиком — шейдеры, WaterNear и WaterAt читают законченную карту.
        private sealed class LandMap
        {
            public readonly float Texel;
            public readonly float RecenterDistance;
            public readonly int TextureId;
            public readonly int RectId;
            public readonly string Name;
            public byte[] Ready = new byte[LandSize * LandSize * LandChannels];
            public byte[] Next = new byte[LandSize * LandSize * LandChannels];
            public Texture2D Texture;
            public Vector2 Origin;
            public Vector2 BuildingOrigin;
            public int BuildingRow = -1;
            public bool Has;

            public LandMap(float texel, float recenterDistance, int textureId, int rectId, string name)
            {
                Texel = texel;
                RecenterDistance = recenterDistance;
                TextureId = textureId;
                RectId = rectId;
                Name = name;
            }

            public float Size => LandSize * Texel;
        }

        /// <summary>Насколько промокла земля (0..1).</summary>
        public float Wetness { get; private set; }

        /// <summary>Насколько набрались лужи (0..1).</summary>
        public float PuddleLevel { get; private set; }

        /// <summary>Может ли рядом с точкой стоять вода на земле: лужи набираются, или в 7 × 7 текселях карты (±24–32 м —
        /// докуда поток винтов рябит воду) есть болото — там вода стоит и в сухую погоду (DroneRotorWash).</summary>
        public bool WaterNear(Vector3 point)
        {
            if (PuddleLevel > 0f)
                return true;

            // вода в воронках стоит и в сухую погоду (CraterField): в 24 м — по зоне брызг
            if (CraterField.Instance is { } field && field.WaterWithin(point.x, point.z, 24f))
                return true;

            if (!_near.Has)
                return false;

            int cx = Mathf.FloorToInt((point.x - _near.Origin.x) / LandTexel);
            int cz = Mathf.FloorToInt((point.z - _near.Origin.y) / LandTexel);

            for (int z = Mathf.Max(cz - 3, 0); z <= Mathf.Min(cz + 3, LandSize - 1); z++)
            {
                for (int x = Mathf.Max(cx - 3, 0); x <= Mathf.Min(cx + 3, LandSize - 1); x++)
                {
                    // как land.g > 0.004 в шейдере террейна
                    if (_near.Ready[(z * LandSize + x) * LandChannels + 1] > 1)
                        return true;
                }
            }

            return false;
        }

        /// <summary>Сколько воды на земле в точке (0..1): лужа или болотная вода — то же решение, что у травы, которая в ней
        /// оседает (PuddleSink.hlsl: пороги и крупные слои шума Puddles.hlsl по карте мест для воды). Из воды под дроном
        /// летят брызги (DroneRotorWash).</summary>
        public float WaterAt(Vector3 point)
        {
            // прудок в воронке — как в шейдере террейна (GroundCraters.hlsl): середина чаши воронки с водой, в дождь — и в
            // остальных; где он и лужа рядом, берётся большее (как water = max(...) в GroundWetness.hlsl)
            float pond = 0f;

            if (CraterField.Instance is { } craters && craters.BowlAt(point.x, point.z, out float bowl, out float waterBowl))
            {
                float level = Mathf.Max(waterBowl > 0.001f ? waterBowl + 0.5f : 0f, bowl + Mathf.Lerp(-0.1f, 0.6f, Mathf.Clamp01(PuddleLevel)));

                if (level > 0.91f)
                {
                    // рябь края — тот же шум, что у шейдера (PuddleNoise(xz / 0.6 + 37.1) · 0.05)
                    float ripple = PuddleNoise(point.x / 0.6f + 37.1f, point.z / 0.6f + 37.1f) * 0.05f;
                    float edge = Mathf.Clamp01((level + ripple - 0.96f) / 0.06f);

                    pond = edge * edge * (3f - 2f * edge);
                }
            }

            if (!_near.Has)
                return pond;

            int x = Mathf.FloorToInt((point.x - _near.Origin.x) / LandTexel);
            int z = Mathf.FloorToInt((point.z - _near.Origin.y) / LandTexel);

            if (x < 0 || z < 0 || x >= LandSize || z >= LandSize)
                return pond;

            int index = (z * LandSize + x) * LandChannels;
            float suitability = _near.Ready[index] / 255f;
            float marsh = _near.Ready[index + 1] / 255f;

            if (PuddleLevel <= 0.001f && marsh <= 0.004f)
                return pond;

            float puddleThreshold = PuddleLevel > 0.001f ? 0.873f - 0.313f * suitability * 0.95f + (1f - PuddleLevel) * 0.3f : 2f;
            // как MarshThreshold в Puddles.hlsl: в сухую погоду вода на ~4 % болота, в долгий дождь — на ~25 %
            float marshThreshold = Mathf.Lerp(1.2f, Mathf.Lerp(0.76f, 0.60f, PuddleLevel), marsh);
            float threshold = Mathf.Min(puddleThreshold, marshThreshold);

            if (threshold > 0.97f || Mathf.Max(suitability, marsh) < 0.02f)
                return pond;

            float field = PuddleNoise(point.x / 7f, point.z / 7f) * 0.55f + PuddleNoise(point.x / 2.5f + 17.1f, point.z / 2.5f + 17.1f) * 0.3f + 0.075f;
            float t = Mathf.Clamp01((field - threshold + 0.02f) / 0.07f);
            return Mathf.Max(pond, t * t * (3f - 2f * t));
        }

        // Шум воды Puddles.hlsl на CPU, бит в бит: значения в клетках — Jenkins-хеш из Random.hlsl Core RP.
        private static float PuddleNoise(float px, float pz)
        {
            float ix = Mathf.Floor(px);
            float iz = Mathf.Floor(pz);
            float fx = px - ix;
            float fz = pz - iz;
            fx = fx * fx * (3f - 2f * fx);
            fz = fz * fz * (3f - 2f * fz);
            int x = (int)ix;
            int z = (int)iz;
            float a = PuddleHash(x, z), b = PuddleHash(x + 1, z);
            float c = PuddleHash(x, z + 1), d = PuddleHash(x + 1, z + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fz);
        }

        private static float PuddleHash(int x, int z)
        {
            uint hash = Jenkins(unchecked((uint)x) ^ Jenkins(unchecked((uint)z) ^ Jenkins(16u)));
            return System.BitConverter.Int32BitsToSingle((int)((hash & 0x007FFFFFu) | 0x3F800000u)) - 1f;
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
            }

            return x;
        }

        public string Stats => $"rain={_rain:F2} soak={_soak:F2} wet={Wetness:F2} puddles={PuddleLevel:F2} " +
                               $"land={(_near.Has ? _near.Origin.ToString("F0") : "none")} building={_near.BuildingRow} " +
                               $"far={(_far.Has ? _far.Origin.ToString("F0") : "none")} farBuilding={_far.BuildingRow} " +
                               $"wetMaterials={_wetMaterials.Count} overview={_overviewLayers.Count}";

#if ODIN_INSPECTOR
        [Button("Мокро сейчас (лужи полные)")]
#else
        [ContextMenu("Мокро сейчас (лужи полные)")]
#endif
        private void WetNow()
        {
            _soak = 1f;
            Wetness = 1f;
            PuddleLevel = 1f;
        }

#if ODIN_INSPECTOR
        [Button("Сухо сейчас")]
#else
        [ContextMenu("Сухо сейчас")]
#endif
        private void DryNow()
        {
            _soak = 0f;
            Wetness = 0f;
            PuddleLevel = 0f;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            _wetShader = Shader.Find(WetShaderName);

            if (_wetShader == null)
                Debug.LogWarning($"[GroundWetness] Shader is missing: {WetShaderName}", this);

            foreach (LandMap map in new[] { _near, _far })
            {
                map.Texture = new Texture2D(LandSize, LandSize, TextureFormat.RGBA32, false, true)
                {
                    name = map.Name,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                Shader.SetGlobalTexture(map.TextureId, map.Texture);
            }

            Publish();
        }

        private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;

        private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        private void OnDestroy()
        {
            if (Instance != this)
                return;

            Instance = null;
            Shader.SetGlobalFloat(WetnessId, 0f);
            Shader.SetGlobalFloat(PuddleLevelId, 0f);
            Shader.SetGlobalFloat(RainId, 0f);

            foreach (LandMap map in new[] { _near, _far })
            {
                Shader.SetGlobalVector(map.RectId, Vector4.zero);

                if (map.Texture != null)
                    Destroy(map.Texture);
            }

            TintOverview(0f);

            foreach (Material wet in _wetMaterials.Values)
            {
                if (wet != null)
                    Destroy(wet);
            }
        }

        // Тайл загрузился — мокрый материал ему сразу, до его первого кадра: иначе до полсекунды он светлел сухим среди мокрых.
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => DressTerrains();

        private void Update()
        {
            float dt = Time.deltaTime;

            if (Time.unscaledTime >= _nextWeather)
            {
                _nextWeather = Time.unscaledTime + WeatherInterval;
                _rain = EnvironmentQueries.EffectIntensity(RainEffectName);
            }

            bool raining = _rain > RainThreshold;
            _soak = Mathf.Clamp01(_soak + (raining ? dt * _rain / SoakSeconds : -dt / SoakDrySeconds));
            Wetness = Mathf.Clamp01(Wetness + (raining ? dt * _rain / WetSeconds : -dt / WetDrySeconds));
            float fill = raining ? (_soak >= 1f ? dt * _rain / FillSeconds : 0f) : -dt / PuddleDrySeconds;
            PuddleLevel = Mathf.Clamp01(PuddleLevel + fill);
            Publish();

            if (Time.unscaledTime >= _nextTerrains)
            {
                _nextTerrains = Time.unscaledTime + TerrainInterval;
                DressTerrains();
            }

            UpdateOverview();
            // карта нужна всегда: вода болот стоит и в сухую погоду
            UpdateLand();
        }

        // Обзорная карта GeoWorld (за загруженными тайлами) мокнет вместе с ними, иначе в дождь край загруженных тайлов виден
        // прямой линией. Её слоям — шейдер с порой года (Game/GeoWorld Overview (Seasons)): зимой она белеет вместе с тайлами,
        // а в дождь темнеет и блестит по _GroundWetness, как земля тайлов (тон — только яркость); на шейдере пакета (если
        // подмены нет) её тон темнеет целиком, как в среднем мокрая земля.
        private void UpdateOverview()
        {
            // сцену с обзорной картой выгрузили — её слои ищем заново
            if (_overviewLayers.Count > 0 && _overview == null)
                _overviewLayers.Clear();

            if (_overviewLayers.Count == 0)
            {
                if (Time.unscaledTime < _nextOverviewSearch)
                    return;

                _nextOverviewSearch = Time.unscaledTime + OverviewSearchInterval;
                _overview = FindFirstObjectByType<GeoOverview>();

                // слои обзорная карта строит в Awake; пока их нет — ищем позже
                if (_overview == null || !_overview.Drawn)
                    return;

                Material seasonal = Resources.Load<Material>(OverviewSeasonsMaterial);

                foreach (MeshRenderer layer in _overview.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (layer.sharedMaterial == null || !layer.sharedMaterial.HasProperty(OverviewTintId))
                        continue;

                    // зелень по поре и снег зимой — как у земли тайлов (свойства шейдеры делят, значения сохраняются)
                    if (seasonal != null)
                        layer.sharedMaterial.shader = seasonal.shader;

                    _overviewLayers.Add(layer);
                }

                _overviewWetness = -1f;
            }

            // каждые 1 % мокрости, и точно в сухую и в мокрую землю
            bool settled = Wetness <= 0f || Wetness >= 1f;

            if (Wetness == _overviewWetness || (!settled && Mathf.Abs(Wetness - _overviewWetness) < 0.01f))
                return;

            TintOverview(Wetness);
        }

        private void TintOverview(float wetness)
        {
            _overviewWetness = wetness;

            if (_overview == null)
            {
                _overviewLayers.Clear();
                return;
            }

            foreach (MeshRenderer layer in _overviewLayers)
            {
                // материалы слоёв обзорная карта создаёт сама в Play — ассеты не трогаем
                if (layer == null || layer.sharedMaterial == null)
                    continue;

                bool ownWetness = layer.sharedMaterial.shader.name == OverviewSeasonsShader;
                Color tone = _overview.tint * (ownWetness ? OverviewTone : Mathf.Lerp(1f, OverviewWetTone, wetness));
                tone.a = _overview.tint.a;
                layer.sharedMaterial.SetColor(OverviewTintId, tone);
            }
        }

        private void Publish()
        {
            Shader.SetGlobalFloat(WetnessId, Wetness);
            Shader.SetGlobalFloat(PuddleLevelId, PuddleLevel);
            Shader.SetGlobalFloat(RainId, _rain > RainThreshold ? _rain : 0f);
        }

        // Тайлам — мокрый террейн: копия их материала с нашим шейдером (те же ключевые слова и настройки).
        // Материал точки взлёта (копия TakeoffTerrainLit) уже на нём; копия материала тайла, который сейчас под
        // генератором точки, не делается — генератор узнаёт свой материал по ссылке и потом вернёт тайлу прежний.
        private void DressTerrains()
        {
            if (_wetShader == null)
                return;

            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                Material material = terrain != null ? terrain.materialTemplate : null;

                if (material == null || material.shader == null || material.shader.name != UrpTerrainShaderName)
                    continue;

                if (CustomSpawnRuntimeEnvironmentGenerator.IsRuntimeCopy(terrain.terrainData))
                    continue;

                // только тайлы GeoWorld: у них материал без ключевых слов, как у Resources/Terrain/WorldTerrainWet, и
                // этот вариант есть в билде. Террейн-заплатки локаций (URP TerrainLit.mat с per-pixel normal) в билде
                // получили бы вырезанный вариант
                if (_notTiles.Contains(terrain))
                    continue;

                if (terrain.GetComponentInParent<GeoWorldRoot>() == null)
                {
                    _notTiles.Add(terrain);
                    continue;
                }

                if (!_wetMaterials.TryGetValue(material, out Material wet) || wet == null)
                {
                    wet = new Material(material) { name = material.name + " (wet)", shader = _wetShader };
                    _wetMaterials[material] = wet;
                }

                // у мира с воронками нормаль земли — из сетки высот в каждом пикселе, а не в вершинах лода: врезанные
                // воронки видны и там, где лод их сгладил (вариант держит Resources/Terrain/WorldTerrainWetCraters)
                if (WorldMapData.Craters != null && !wet.IsKeywordEnabled(PerPixelNormal))
                    wet.EnableKeyword(PerPixelNormal);
                else if (WorldMapData.Craters == null && wet.IsKeywordEnabled(PerPixelNormal))
                    wet.DisableKeyword(PerPixelNormal);

                terrain.materialTemplate = wet;
            }

            // тайл выгрузился вместе со своим материалом — его копия больше не нужна
            _notTiles.RemoveWhere(terrain => terrain == null);
            _gone.Clear();

            foreach (KeyValuePair<Material, Material> pair in _wetMaterials)
            {
                if (pair.Key == null)
                    _gone.Add(pair.Key);
            }

            foreach (Material key in _gone)
            {
                if (_wetMaterials.TryGetValue(key, out Material wet) && wet != null)
                    Destroy(wet);

                _wetMaterials.Remove(key);
            }
        }

        // Окна карты пригодности — на камеру; ближнее строится первым (по нему вода у дрона и трава в лужах), дальнее — когда
        // ближнему строить нечего.
        private void UpdateLand()
        {
            Camera camera = GeoWorldCamera.Current;

            if (camera == null)
                return;

            Vector3 eye = camera.transform.position;

            if (!Build(_near, eye))
                Build(_far, eye);
        }

        // Новое окно считается по строкам и подменяет старое целиком; true — в этом кадре строили.
        private static bool Build(LandMap map, Vector3 eye)
        {
            float size = map.Size;

            if (map.BuildingRow < 0)
            {
                Vector2 centre = map.Origin + Vector2.one * (size * 0.5f);

                if (map.Has && Mathf.Abs(eye.x - centre.x) < map.RecenterDistance && Mathf.Abs(eye.z - centre.y) < map.RecenterDistance)
                    return false;

                map.BuildingOrigin = new Vector2(
                    Mathf.Round((eye.x - size * 0.5f) / map.Texel) * map.Texel,
                    Mathf.Round((eye.z - size * 0.5f) / map.Texel) * map.Texel);
                map.BuildingRow = 0;
            }

            int end = Mathf.Min(map.BuildingRow + LandRowsPerFrame, LandSize);
            Terrain[] terrains = Terrain.activeTerrains;
            Terrain terrain = null;

            for (int z = map.BuildingRow; z < end; z++)
            {
                for (int x = 0; x < LandSize; x++)
                {
                    Vector3 point = new(map.BuildingOrigin.x + (x + 0.5f) * map.Texel, 0f, map.BuildingOrigin.y + (z + 0.5f) * map.Texel);
                    bool known = GeoLandCover.TrySample(point, out GeoLandClass land);
                    float suitability = known ? Suitability(land) : DefaultSuitability;
                    float marsh = known && land == GeoLandClass.Wetland ? 1f : 0f;

                    if (suitability > 0f)
                    {
                        float flatness = Flatness(point, terrains, ref terrain);
                        suitability *= flatness;
                        marsh *= flatness;
                    }

                    int index = (z * LandSize + x) * LandChannels;
                    map.Next[index] = (byte)Mathf.RoundToInt(suitability * 255f);
                    map.Next[index + 1] = (byte)Mathf.RoundToInt(marsh * 255f);
                    // где шли бои (воронки, CraterField), поля брошены: ни рядков и колей, ни стерни летом — бурьян
                    bool abandoned = CraterField.AbandonedAt(point.x, point.z);
                    map.Next[index + 2] = (byte)(known && land == GeoLandClass.Cropland && !abandoned ? 255 : 0);
                    map.Next[index + 3] = abandoned ? (byte)0 : FieldDirection(point);
                }
            }

            map.BuildingRow = end;

            if (map.BuildingRow < LandSize)
                return true;

            map.BuildingRow = -1;
            (map.Ready, map.Next) = (map.Next, map.Ready);
            map.Origin = map.BuildingOrigin;
            map.Has = true;
            map.Texture.SetPixelData(map.Ready, 0);
            map.Texture.Apply(false);
            Shader.SetGlobalVector(map.RectId, new Vector4(map.Origin.x, map.Origin.y, 1f / size, 1f));
            return true;
        }

        // Направление рядков поля OSM в точке для шейдера (FieldRows.hlsl): 1..255 — азимут 0..180°, 0 — не поле.
        private static byte FieldDirection(Vector3 point)
        {
            FieldAreas fields = FieldAreas.Instance;

            if (fields == null)
                return 0;

            float rotation = fields.RotationAt(point.x, point.z);
            return rotation < 0f ? (byte)0 : (byte)(1 + Mathf.RoundToInt(Mathf.Repeat(rotation, 180f) / 180f * 254f));
        }

        // Насколько ровно под воду (0..1) по уклону рельефа; где террейн не загружен — ровно (там его и не видно).
        private static float Flatness(Vector3 point, Terrain[] terrains, ref Terrain last)
        {
            if (!Covers(last, point))
            {
                last = null;

                foreach (Terrain terrain in terrains)
                {
                    if (!Covers(terrain, point))
                        continue;

                    last = terrain;
                    break;
                }

                if (last == null)
                    return 1f;
            }

            TerrainData data = last.terrainData;
            Vector3 local = point - last.GetPosition();
            float t = Mathf.InverseLerp(FlatSlope, SteepSlope, data.GetSteepness(local.x / data.size.x, local.z / data.size.z));
            return 1f - t * t * (3f - 2f * t);
        }

        private static bool Covers(Terrain terrain, Vector3 point)
        {
            if (terrain == null || terrain.terrainData == null)
                return false;

            Vector3 origin = terrain.GetPosition();
            Vector3 size = terrain.terrainData.size;
            return point.x >= origin.x && point.x <= origin.x + size.x && point.z >= origin.z && point.z <= origin.z + size.z;
        }

        // Насколько земля держит лужи (0..1).
        private static float Suitability(GeoLandClass land) => land switch
        {
            GeoLandClass.DirtRoad => 1f,
            GeoLandClass.Path => 0.9f,
            GeoLandClass.Bare => 0.75f,
            GeoLandClass.Wetland => 0.7f,
            GeoLandClass.Cropland => 0.5f,
            GeoLandClass.Gravel => 0.5f,
            GeoLandClass.Urban => 0.4f,
            GeoLandClass.Asphalt => 0.35f,
            GeoLandClass.Paving => 0.3f,
            GeoLandClass.Rock => 0.2f,
            GeoLandClass.Scrub or GeoLandClass.Orchard or GeoLandClass.Sand => 0.15f,
            GeoLandClass.Forest => 0.1f,
            GeoLandClass.Water or GeoLandClass.Building or GeoLandClass.Rail or GeoLandClass.Snow => 0f,
            _ => DefaultSuitability
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            Instance = null;
            Shader.SetGlobalFloat(WetnessId, 0f);
            Shader.SetGlobalFloat(PuddleLevelId, 0f);
            Shader.SetGlobalFloat(RainId, 0f);
            Shader.SetGlobalVector(LandRectId, Vector4.zero);
            Shader.SetGlobalVector(FarLandRectId, Vector4.zero);
        }
    }
}
