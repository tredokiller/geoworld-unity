using _Game.Scripts.Locations.FlyPoints;
using _Game.Scripts.Services;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Слой окружения GeoWorld целиком — то, что делает мир похожим на настоящий с дрона: породы деревьев по месту
    /// (TileTreeSpecies, до запуска Nature Renderer тайлов), дальности и тени Nature Renderer (NatureRendererSettings,
    /// ShadowRange), трава, цветы, опад, сухостой и подлесок на тайлах (GroundDressing — по записям профиля plants),
    /// фактура земли и лесная подстилка (GroundDetail, ForestFloor), покрытие дорог (RoadMap), мокрая земля, лужи и болота
    /// (GroundWetness), небо и берег у воды (WaterSky, WaterShore), дымка и тени облаков (AerialHaze, CloudShadows), поры
    /// года со снегом, цветом и звуками (Seasons, SeasonGrading, SeasonAmbience), дождь (RainTuner) и ветер растений и
    /// воды (WorldWind). Висит на объекте мира рядом с его WorldMapData (WorldMapBaker ставит его вместе с ней) и в Awake
    /// ставит на свой объект те системы, которых на нём ещё нет. Раньше их по одной добавляла игра (Interceptor Drones:
    /// LocationsManager, WeatherManager), и в другом проекте мира без них не было. Нужен Enviro 3 в сцене (погода, небо,
    /// дата); игра управляет системами через их свойства: дальности (NatureRendererSettings.SetDistances, ShadowRange,
    /// GroundDressing), пора года (Seasons.Apply), ветер (WorldWind.Set).
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-1000)]
    public sealed class WorldEnvironment : MonoBehaviour
    {
        public const string DefaultPlantsPath = "Packages/com.tredokiller.geoworld/Environment/Profiles/DefaultFlyPointEnvironment.asset";
        public const string WindNoisePath = "Packages/com.tredokiller.geoworld/ThirdParty/NatureManufacture/NatureManufacture Wind/3DNoise.psd";
        public const string WindGustsPath = "Packages/com.tredokiller.geoworld/ThirdParty/NatureManufacture/NatureManufacture Wind/GustNoise.psd";

        [Tooltip("Трава, цветы, опад, сухостой и подлесок на тайлах (GroundDressing): записи профиля, как у точки взлёта.")]
        public FlyPointEnvironmentProfile plants;
        [Tooltip("Шум и маска порывов NatureManufacture — для своего NM_Wind, если в сцене его нет (WorldWind).")]
        public Texture2D windNoise;
        public Texture2D windGusts;

        public static WorldEnvironment Instance { get; private set; }

        public TileTreeSpecies TreeSpecies { get; private set; }
        public NatureRendererSettings Nature { get; private set; }
        public ShadowRange Shadows { get; private set; }
        public GroundDressing Dressing { get; private set; }
        public Seasons Seasons { get; private set; }
        public WorldWind Wind { get; private set; }

        private void Awake()
        {
            Instance = this;
            Install();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Install()
        {
            // породы — до того, как Nature Renderer тайла начнёт рисовать его деревья (держит его запуск с sceneLoaded)
            TreeSpecies = Ensure<TileTreeSpecies>();
            Nature = Ensure<NatureRendererSettings>();
            // тени деревьев ведёт через NatureRendererSettings на том же объекте
            Shadows = Ensure<ShadowRange>();
            Dressing = Ensure<GroundDressing>();

            if (plants != null)
                Dressing.Configure(plants);

            Ensure<AerialHaze>();
            Ensure<CloudShadows>();
            Ensure<GroundWetness>();
            Ensure<WaterSky>();
            Ensure<WaterShore>();
            Ensure<RoadMap>();
            Ensure<CraterMap>();
            // посечённость войной (деревья): по воронкам и руинам загруженных тайлов
            Ensure<WarMap>();
            // пыль, крошка и обломки вокруг руин (GroundRuins.hlsl)
            Ensure<RuinMap>();
            Ensure<GroundDetail>();
            Ensure<ForestFloor>();
            Seasons = Ensure<Seasons>();
            Ensure<SeasonGrading>();
            Ensure<SeasonAmbience>();
            Ensure<RainTuner>();
            Wind = Ensure<WorldWind>();
            Wind.Configure(windNoise, windGusts);
        }

        private T Ensure<T>() where T : Component
        {
            T component = GetComponent<T>();
            return component != null ? component : gameObject.AddComponent<T>();
        }

#if UNITY_EDITOR
        private void Reset() => SetDefaults();

        /// <summary>Профиль растений и текстуры ветра пакета (в редакторе: при добавлении и из WorldMapBaker).</summary>
        public void SetDefaults()
        {
            if (plants == null)
                plants = UnityEditor.AssetDatabase.LoadAssetAtPath<FlyPointEnvironmentProfile>(DefaultPlantsPath);

            if (windNoise == null)
                windNoise = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(WindNoisePath);

            if (windGusts == null)
                windGusts = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(WindGustsPath);
        }
#endif
    }
}
