using System;
using _Game.Scripts.Enums;
using Enviro;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Пора года тренировки (кнопки «ПОРА РОКУ» в условиях пресета, у каждой поры один вид). Даёт:
    ///  - дату Enviro — середину поры на широте Харькова: солнце своей высоты (в полдень зимой ~20°, летом ~60°) и длину
    ///    дня; долгота и UTC — ноль, поэтому полдень остаётся в 12:00 часов тренировки;
    ///  - шейдерам — _Season (x весна, y лето, z осень, w зима, одна единица), _SnowCover (снег на земле зимой, 0..1) и
    ///    текстуру снега (_SnowAlbedo, _SnowNormal из Resources/Seasons — для Snow.hlsl: земля, лёд, дальняя карта);
    ///  - погоду поры (EffectiveWeather): зимой дождь — мокрый снег, гроза — метель, летом снега нет;
    ///  - событие Changed — траве (GroundDressing), листве на земле, воде.
    /// Один на сцену (на объекте WeatherManager).
    /// </summary>
    public sealed class Seasons : MonoBehaviour
    {
        private const float Latitude = 49.99f;

        private const string SnowAlbedoPath = "Seasons/SnowAlbedo";
        private const string SnowNormalPath = "Seasons/SnowNormal";

        private static readonly int SeasonId = Shader.PropertyToID("_Season");
        private static readonly int SnowCoverId = Shader.PropertyToID("_SnowCover");
        private static readonly int SnowAlbedoId = Shader.PropertyToID("_SnowAlbedo");
        private static readonly int SnowNormalId = Shader.PropertyToID("_SnowNormal");
        private static readonly int SnowTexturesId = Shader.PropertyToID("_SnowTextures");

        public static Seasons Instance { get; private set; }

        /// <summary>
        /// Сменилась пора года или снег (в кадре после Apply): трава тайлов и точка взлёта пересчитываются (у точки — секунды
        /// в одном кадре).
        /// </summary>
        public static event Action Changed;

        /// <summary>
        /// Пора года или снег сменятся в следующем кадре (Changed): индикатор загрузки показывается заранее и успевает
        /// отрисоваться до пересчёта. Шейдеры получают новую пору сразу.
        /// </summary>
        public static event Action Changing;

        /// <summary>Changed ещё впереди (пора уже другая, трава и точка взлёта — ещё прежние).</summary>
        public bool ChangePending { get; private set; }

        public Season Current { get; private set; } = Season.Summer;

        /// <summary>Снег на земле (0..1): зимой — покров из пресета, в другие поры ноль.</summary>
        public float SnowCover { get; private set; }

        private bool _hasPendingDate = true;
        private int _changeFrame;

        public void Apply(Season season, float snowCover)
        {
            float snow = season == Season.Winter ? Mathf.Clamp01(snowCover) : 0f;
            bool changed = season != Current || !Mathf.Approximately(snow, SnowCover);
            Current = season;
            SnowCover = snow;
            Publish();
            _hasPendingDate = true;
            TryApplyDate();

            if (!changed)
                return;

            ChangePending = true;
            _changeFrame = Time.frameCount;
            Changing?.Invoke();
        }

        /// <summary>Вода этого вида подо льдом: зимой стоячая (озеро, болото, водохранилище); река течёт и не замерзает.</summary>
        public static bool Frozen(WaterKind kind) => Instance != null && Instance.Current == Season.Winter && kind != WaterKind.River;

        /// <summary>Зима: вода луж и болот на земле — лёд.</summary>
        public static bool IsWinter => Instance != null && Instance.Current == Season.Winter;

        /// <summary>Погода, которую на деле включать в эту пору: зимой вместо дождя и грозы — снег, летом вместо снега — тучи.</summary>
        public static WeatherType EffectiveWeather(Season season, WeatherType weather) => season switch
        {
            Season.Winter when weather is WeatherType.Rain or WeatherType.Thunder => WeatherType.Snow,
            Season.Summer when weather == WeatherType.Snow => WeatherType.Cloudy,
            _ => weather
        };

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            PublishSnowTextures();
            Publish();
        }

        private void OnDestroy()
        {
            if (Instance != this)
                return;

            Instance = null;
            ResetGlobals();
        }

        private void Update()
        {
            if (_hasPendingDate)
                TryApplyDate();

            // не в кадре Apply: тот кадр с индикатором загрузки должен успеть отрисоваться
            if (ChangePending && Time.frameCount > _changeFrame)
            {
                ChangePending = false;
                Changed?.Invoke();
            }
        }

        private void Publish()
        {
            Shader.SetGlobalVector(SeasonId, new Vector4(
                Current == Season.Spring ? 1f : 0f,
                Current == Season.Summer ? 1f : 0f,
                Current == Season.Autumn ? 1f : 0f,
                Current == Season.Winter ? 1f : 0f));
            Shader.SetGlobalFloat(SnowCoverId, SnowCover);
        }

        // Без текстур снег в шейдерах ровного цвета (надувы и пятна остаются).
        private static void PublishSnowTextures()
        {
            Texture2D albedo = Resources.Load<Texture2D>(SnowAlbedoPath);
            Texture2D normal = Resources.Load<Texture2D>(SnowNormalPath);
            bool loaded = albedo != null && normal != null;

            if (loaded)
            {
                Shader.SetGlobalTexture(SnowAlbedoId, albedo);
                Shader.SetGlobalTexture(SnowNormalId, normal);
            }
            else
            {
                Debug.LogWarning($"[Seasons] Snow textures not found in Resources ({SnowAlbedoPath}, {SnowNormalPath})");
            }

            Shader.SetGlobalFloat(SnowTexturesId, loaded ? 1f : 0f);
        }

        // Дата — когда Enviro уже готов (его конфигурацию грузит WeatherManager); час не трогаем.
        private void TryApplyDate()
        {
            EnviroManager enviro = EnviroManager.instance;

            if (enviro == null || enviro.Time == null)
                return;

            EnviroTime settings = enviro.Time.Settings;
            settings.calenderType = EnviroTime.CalenderType.Realistic;
            settings.latitude = Latitude;
            settings.longitude = 0f;
            settings.utcOffset = 0;
            (int day, int month) = MiddleOf(Current);
            enviro.Time.SetDateTime(settings.secSerial, settings.minSerial, settings.hourSerial, day, month, settings.yearSerial);
            _hasPendingDate = false;
        }

        private static (int Day, int Month) MiddleOf(Season season) => season switch
        {
            Season.Spring => (20, 4),
            Season.Autumn => (10, 10),
            Season.Winter => (15, 1),
            _ => (10, 7)
        };

        private static void ResetGlobals()
        {
            Shader.SetGlobalVector(SeasonId, new Vector4(0f, 1f, 0f, 0f));
            Shader.SetGlobalFloat(SnowCoverId, 0f);
            Shader.SetGlobalFloat(SnowTexturesId, 0f);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            Instance = null;
            Changed = null;
            Changing = null;
            ResetGlobals();
        }
    }
}
