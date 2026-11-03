using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Ветер растений и воды. Трава и деревья качаются от ветра NatureManufacture (NM_Wind: направление — его поворот,
    /// скорость — WindSpeed в км/ч, порывы — GustSpeed); вода (Game/Water/*) берёт _WaterWind — куда дует и скорость — и
    /// _WaterWindRun — его пробег, по нему рябь бежит, не прыгая, когда ветер меняется. Ветер мира задаёт игра (Set: в
    /// Interceptor Drones — ветер дрона, VegetationWind); без него — лёгкий фон, чтобы трава не стояла замершей, а у воды
    /// лёгкое дыхание. Если в сцене нет NM_Wind, ставит свой (шум и маска порывов — с WorldEnvironment). Ставит его
    /// WorldEnvironment.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class WorldWind : MonoBehaviour
    {
        private const float KilometersPerHourInMetersPerSecond = 3.6f;
        // ветер воды без ветра мира (м/с)
        private const float CalmWaterWind = 0.6f;

        [Tooltip("Ветер растений, когда ветра мира нет (км/ч).")]
        [SerializeField, Min(0f)] private float calmSpeedKmh = 5f;
        [Tooltip("Скорость порывов относительно ветра.")]
        [SerializeField, Min(0f)] private float gustFactor = 1.6f;

        private static readonly int WaterWindId = Shader.PropertyToID("_WaterWind");
        private static readonly int WaterWindRunId = Shader.PropertyToID("_WaterWindRun");

        private NM_Wind _wind;
        private float _sceneTurbulence;
        private bool _windy;
        private float _speed;
        private Vector3 _direction = Vector3.forward;
        private Vector2 _waterWindRun;
        private Texture2D _noise;
        private Texture2D _gusts;

        public static WorldWind Instance { get; private set; }

        /// <summary>Шум и маска порывов для своего NM_Wind (когда в сцене его нет).</summary>
        public void Configure(Texture2D noise, Texture2D gusts)
        {
            _noise = noise;
            _gusts = gusts;
        }

        /// <summary>
        /// Ветер мира: скорость (м/с) и куда дует; 0 — штиль (лёгкий фон). Направление меняется только при ветре — в штиль
        /// растения не разворачиваются.
        /// </summary>
        public void Set(float speed, Vector3 direction)
        {
            _windy = speed > 0.01f;
            _speed = _windy ? speed : 0f;
            direction = Vector3.ProjectOnPlane(direction, Vector3.up);

            if (_windy && direction.sqrMagnitude > 0.0001f)
                _direction = direction.normalized;
        }

        private void OnEnable() => Instance = this;

        private void OnDisable()
        {
            if (Instance == this)
                Instance = null;
        }

        // раньше NM_Wind.Update: он в том же кадре отдаёт шейдерам уже новые значения
        private void Update()
        {
            PublishWaterWind();

            if (!TryGetWind(out NM_Wind wind))
                return;

            wind.WindSpeed = Mathf.Max(calmSpeedKmh, _speed * KilometersPerHourInMetersPerSecond);
            wind.GustSpeed = wind.WindSpeed * gustFactor;
            wind.Turbulence = _sceneTurbulence;

            if (_windy)
                wind.transform.rotation = Quaternion.LookRotation(_direction, Vector3.up);
        }

        private void PublishWaterWind()
        {
            float speed = Mathf.Max(CalmWaterWind, _speed);
            _waterWindRun += new Vector2(_direction.x, _direction.z) * (speed * Time.deltaTime);
            Shader.SetGlobalVector(WaterWindId, new Vector4(_direction.x, _direction.z, speed, 0f));
            Shader.SetGlobalVector(WaterWindRunId, new Vector4(_waterWindRun.x, _waterWindRun.y, 0f, 0f));
        }

        private bool TryGetWind(out NM_Wind wind)
        {
            if (_wind == null)
            {
                _wind = FindFirstObjectByType<NM_Wind>();

                if (_wind == null && _noise != null && _gusts != null)
                {
                    GameObject windObject = new("NM Wind (World Wind)");
                    windObject.transform.SetParent(transform, false);
                    // текстуры — до Start ветра, он отдаёт их шейдерам
                    windObject.SetActive(false);
                    _wind = windObject.AddComponent<NM_Wind>();
                    _wind.NoiseTexture = _noise;
                    _wind.GustMaskTexture = _gusts;
                    windObject.SetActive(true);
                }

                if (_wind != null)
                    _sceneTurbulence = _wind.Turbulence;
            }

            wind = _wind;
            return wind != null;
        }
    }
}
