using _Game.Scripts.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace _Game.Scripts.Drone
{
    /// <summary>
    /// Вода расступается под дроном. Волновое уравнение на GPU в окне 48 × 48 м вокруг дрона (Hidden/Game/Water Ripple
    /// Sim, шаг — Blit): поток винтов продавливает лунку под дроном, от её края струя бежит по воде — толкает кольца,
    /// уходящие от дрона, рвёт воду рябью и сгоняет ряску, а летящий низко дрон тянет за собой след. Воздух у воды — тот
    /// же, что сдувает листья (RotorWashLeaves): FPV-камера смотрит вперёд, и вода «расступается» в кадре, только если
    /// поток достаёт до неё. Окно переезжает за дроном. Вода рек и озёр и лужи читают поле глобально (_WaterRippleTex,
    /// _WaterRippleRect) и строят из него нормали, пену и окна в ряске; место, радиус струи и воздух у её края — в
    /// _WaterWashPosition / _WaterWashPower (по ним тусклый взъерошенный круг, полосы, выдутая лужа).
    /// Один на сцену: DroneRotorWash активного дрона каждый кадр сообщает поток (Blow).
    /// </summary>
    public sealed class RotorWashWater : MonoBehaviour
    {
        // материал в Resources держит скрытый шейдер симуляции в сборке
        private const string SimMaterialResource = "Materials/WaterRippleSim";
        private const int Resolution = 384;
        private const float WindowSize = 48f;
        // окно — за дроном каждые 3 м: краем оно не ближе 21 м, а рябь гаснет кругом к 22 м (квадрата на воде не видно)
        private const float RecenterDistance = 3f;
        // шаг симуляции (с) и не больше шагов за кадр: просадка кадров не разгоняет волны
        private const float StepSeconds = 1f / 60f;
        private const int MaxStepsPerFrame = 4;
        // скорость колец по воде (м/с) и за сколько секунд они гаснут вдвое
        private const float WaveSpeed = 1.6f;
        private const float WaveHalfLife = 2.5f;
        private const float FoamHalfLife = 1.2f;
        // поток у воды — как у листьев (RotorWashLeaves): скорость воздуха в полный поток у самой воды (м/с), высота,
        // на которой он вдвое слабее, и предел, выше которого вода спокойна
        private const float JetSpeed = 20f;
        private const float WashHeight = 4.5f;
        public const float MaxWashHeight = 12f;
        // струя у воды — как у листьев: радиус 0.4 м + 0.8 высоты; лунка под ней — 0.6 радиуса струи, глубина в полный
        // поток (м; настоящая ~2–3 см, взято заметнее)
        private const float JetRadius = 0.4f;
        private const float JetSpread = 0.8f;
        private const float DimpleDepth = 0.07f;
        private const float DimplePull = 0.12f;
        // от края лунки струя бежит по воде: толкает кольца со скоростью волн поля (Гц; толчок за шаг, м) и рябь (толчки
        // по клеткам ~60 см, новые каждые 0.1 с, м за шаг) — обе × воздух^0.7; пена — где воздух у воды быстрее ~5 м/с
        private const float RingWaveHz = 1.6f;
        private const float RingWave = 0.0006f;
        private const float RingChop = 0.0025f;
        private const float RingFoam = 0.5f;
        // окно в ряске раскрывается и затягивается (с)
        private const float ClearOpenSeconds = 0.5f;
        private const float ClearCloseSeconds = 8f;
        // как быстро сила потока у воды меняется (доля в секунду)
        private const float PowerRate = 3f;
        // скорость дрона (м/с), на которой тусклый круг, полосы и выдутая лужа под ним вдвое слабее: в быстром полёте поток
        // сносит назад, и под дроном воздух не стоит — остаётся только след в поле
        private const float SweptSpeed = 6f;
        // шаг с затуханием всего шага (лягушка) гасит волну вдвое за 2 WaveHalfLife: поле успокаивается до 1/64 за столько
        private const float QuietSeconds = 2f * WaveHalfLife * 6f;

        private static readonly int StateRectId = Shader.PropertyToID("_SimRect");
        private static readonly int ParamsId = Shader.PropertyToID("_SimParams");
        private static readonly int WashId = Shader.PropertyToID("_SimWash");
        private static readonly int ForceId = Shader.PropertyToID("_SimForce");
        private static readonly int ShiftId = Shader.PropertyToID("_SimShift");
        private static readonly int WaveId = Shader.PropertyToID("_SimWave");
        private static readonly int ClearId = Shader.PropertyToID("_SimClear");
        private static readonly int RippleTexId = Shader.PropertyToID("_WaterRippleTex");
        private static readonly int RippleRectId = Shader.PropertyToID("_WaterRippleRect");
        private static readonly int RippleTexelId = Shader.PropertyToID("_WaterRippleTexel");
        private static readonly int WashPositionId = Shader.PropertyToID("_WaterWashPosition");
        private static readonly int WashPowerId = Shader.PropertyToID("_WaterWashPower");

        public static RotorWashWater Instance { get; private set; }

        private readonly RenderTexture[] _state = new RenderTexture[2];
        private Material _material;
        private int _current;
        private Vector2Int _origin;
        private bool _hasWindow;
        private float _stepDebt;
        private float _quietSeconds;
        private Vector3 _point;
        private float _height;
        private float _spin;
        private float _airspeed;
        private float _shownPower;
        private bool _settled = true;
        private int _blowFrame = -10;
        // номер шага симуляции — зерно шума ряби: у шагов одного кадра шум разный
        private int _step;
        // фаза колец (рад) и время порывов (с, по кругу 4π: оба синуса порывов на нём замыкаются)
        private float _wavePhase;
        private float _gustTime;

        /// <summary>Воздух у края струи, доля полного потока (20 м/с), 0..1.</summary>
        public float Power { get; private set; }
        public float JetRadiusNow { get; private set; }

        public static void Ensure(Scene scene)
        {
            if (Instance != null)
                return;

            GameObject waterObject = new("Rotor wash water");

            if (scene.IsValid() && scene.isLoaded)
                SceneManager.MoveGameObjectToScene(waterObject, scene);

            waterObject.AddComponent<RotorWashWater>();
        }

        // Поток активного дрона в этом кадре: точка под ним на воде (или на земле — для луж), высота над ней, обороты
        // винтов (0..1), горизонтальная скорость дрона (м/с).
        public void Blow(Vector3 point, float height, float spin, float airspeed)
        {
            _point = point;
            _height = height;
            _spin = spin;
            _airspeed = airspeed;
            _blowFrame = Time.frameCount;
        }

        // Воздух у воды под краем струи (м/с) — тот же, что у листьев (RotorWashLeaves): 20 м/с × обороты у самой воды,
        // вдвое слабее на 4.5 м, с 10 до 12 м сходит на нет. По нему же водяная пыль (DroneRotorWash).
        public static float JetAir(float height, float spin)
        {
            float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(MaxWashHeight - 2f, MaxWashHeight, height));
            return JetSpeed * spin * fade / (1f + height * height / (WashHeight * WashHeight));
        }

        // Радиус струи у воды (м) — как у листьев: 0.4 м у самой воды + 0.8 высоты.
        public static float JetRadiusAt(float height) => JetRadius + JetSpread * Mathf.Min(height, MaxWashHeight);

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            // один на сцену и без материала: иначе каждый дрон создавал бы свой пустой объект, а вода прудов не
            // получала бы свой материал (WaterSurfaces.Update)
            Instance = this;
            Material simMaterial = Resources.Load<Material>(SimMaterialResource);

            if (simMaterial == null)
            {
                Debug.LogWarning($"[RotorWashWater] Material is missing: Resources/{SimMaterialResource}", this);
                return;
            }

            _material = new Material(simMaterial) { hideFlags = HideFlags.HideAndDontSave };

            for (int i = 0; i < _state.Length; i++)
            {
                _state[i] = new RenderTexture(Resolution, Resolution, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = $"Water ripple state {i}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false
                };
                _state[i].Create();
                Clear(_state[i]);
            }

            Shader.SetGlobalTexture(RippleTexId, _state[_current]);
            Shader.SetGlobalVector(RippleTexelId, new Vector4(1f / Resolution, 1f / Resolution, Resolution, Resolution));
            Shader.SetGlobalVector(RippleRectId, Vector4.zero);
            Shader.SetGlobalFloat(WashPowerId, 0f);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                Shader.SetGlobalVector(RippleRectId, Vector4.zero);
                Shader.SetGlobalFloat(WashPowerId, 0f);
            }

            foreach (RenderTexture texture in _state)
            {
                if (texture != null)
                    texture.Release();
            }

            if (_material != null)
                Destroy(_material);
        }

        private void Update()
        {
            // вода новых тайлов: материал прудов, без теней
            WaterSurfaces.Update();
            float dt = Time.deltaTime;

            if (dt <= 0f || _material == null)
                return;

            bool washing = _blowFrame >= Time.frameCount - 1 && _spin > 0.001f;
            float speed = washing ? JetAir(_height, _spin) : 0f;
            // воздух у края струи — доля полного потока, линейно, как у листьев (квадрат напора гасил висение до 0.2 у самой
            // воды и до 0.07 на 4 м); плавно: у берега вода под дроном то есть, то нет, и пятно с полосами мигало
            Power = Mathf.MoveTowards(Power, Mathf.Clamp01(speed / JetSpeed), dt * PowerRate);
            // радиус струи тоже плавно: у берега высота «под дроном» переходит с земли на воду скачком; 4 м/с успевают за
            // подъёмом (0.8 м радиуса на метр высоты)
            float radius = JetRadiusAt(_height);
            JetRadiusNow = Power <= 0.002f ? radius : Mathf.MoveTowards(JetRadiusNow, radius, dt * 4f);
            // шейдерам — поток, который стоит под дроном: в быстром полёте его сносит (поле — в мире, след в нём остаётся)
            float still = 1f / (1f + _airspeed * _airspeed / (SweptSpeed * SweptSpeed));
            _shownPower = Mathf.MoveTowards(_shownPower, washing ? Power * still : Power, dt * PowerRate);
            Shader.SetGlobalVector(WashPositionId, new Vector4(_point.x, _point.y, _point.z, JetRadiusNow));
            Shader.SetGlobalFloat(WashPowerId, _shownPower);

            if (Power > 0.002f)
            {
                _quietSeconds = 0f;
                _settled = false;

                if (!_hasWindow || FarFromWindowCenter(_point))
                    Recenter(_point);
            }
            else
            {
                _quietSeconds += dt;
            }

            // вода успокоилась — шаги не нужны; остаток ряби (1/64) стираем, чтобы на глади не застыл узор
            if (_hasWindow && _quietSeconds > QuietSeconds && !_settled)
            {
                Clear(_state[0]);
                Clear(_state[1]);
                _settled = true;
            }

            if (!_hasWindow || _quietSeconds > QuietSeconds)
                return;

            _stepDebt = Mathf.Min(_stepDebt + dt, StepSeconds * MaxStepsPerFrame);
            float cell = WindowSize / Resolution;
            float courant = WaveSpeed * StepSeconds / cell;
            float damping = Mathf.Pow(0.5f, StepSeconds / WaveHalfLife);
            float foamDecay = Mathf.Pow(0.5f, StepSeconds / FoamHalfLife);
            _material.SetVector(StateRectId, new Vector4(_origin.x * cell, WindowSize, _origin.y * cell, 0f));
            _material.SetVector(WashId, new Vector4(_point.x, JetRadiusNow, _point.z, Power));
            _material.SetVector(ForceId, new Vector4(DimpleDepth, DimplePull, RingChop, RingFoam));
            _material.SetVector(ClearId, new Vector4(StepSeconds / ClearOpenSeconds, StepSeconds / ClearCloseSeconds, 0f, 0f));
            // волновое число колец по дисперсии сетки (вдоль осей): кольца бегут со скоростью волн самого поля и
            // подпитываются на всём пути — ~6.45 рад/м, волна ~0.97 м
            float waveNumber = 2f * Mathf.Asin(Mathf.Sin(Mathf.PI * RingWaveHz * StepSeconds) / courant) / cell;

            while (_stepDebt >= StepSeconds)
            {
                _stepDebt -= StepSeconds;
                _step = (_step + 1) & 0xFFFFFF;
                // фаза — по шагам и в пределах 2π: из номера шага во float она теряла бы точность
                _wavePhase = Mathf.Repeat(_wavePhase + 2f * Mathf.PI * RingWaveHz * StepSeconds, 2f * Mathf.PI);
                _gustTime = Mathf.Repeat(_gustTime + StepSeconds, 4f * Mathf.PI);
                _material.SetVector(ParamsId, new Vector4(courant * courant, damping, _step, foamDecay));
                _material.SetVector(WaveId, new Vector4(RingWave, waveNumber, _wavePhase, _gustTime));
                Graphics.Blit(_state[_current], _state[1 - _current], _material, 0);
                _current = 1 - _current;
            }

            Shader.SetGlobalTexture(RippleTexId, _state[_current]);
        }

        private bool FarFromWindowCenter(Vector3 point)
        {
            float cell = WindowSize / Resolution;
            float half = WindowSize * 0.5f;
            return Mathf.Abs(point.x - (_origin.x * cell + half)) > RecenterDistance ||
                   Mathf.Abs(point.z - (_origin.y * cell + half)) > RecenterDistance;
        }

        // Окно — на дрона; уже посчитанная рябь сдвигается вместе с ним на целое число ячеек.
        private void Recenter(Vector3 center)
        {
            float cell = WindowSize / Resolution;
            Vector2Int origin = new(Mathf.RoundToInt((center.x - WindowSize * 0.5f) / cell), Mathf.RoundToInt((center.z - WindowSize * 0.5f) / cell));

            if (_hasWindow)
            {
                Vector2Int shift = origin - _origin;

                if (Mathf.Abs(shift.x) >= Resolution || Mathf.Abs(shift.y) >= Resolution)
                {
                    Clear(_state[0]);
                    Clear(_state[1]);
                }
                else
                {
                    _material.SetVector(ShiftId, new Vector4(shift.x, shift.y, 0f, 0f));
                    Graphics.Blit(_state[_current], _state[1 - _current], _material, 1);
                    _current = 1 - _current;
                }
            }

            _origin = origin;
            _hasWindow = true;
            Shader.SetGlobalTexture(RippleTexId, _state[_current]);
            Shader.SetGlobalVector(RippleRectId, new Vector4(_origin.x * cell, _origin.y * cell, 1f / WindowSize, 1f));
        }

        private static void Clear(RenderTexture texture)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = texture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = active;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetInstance()
        {
            Instance = null;
            Shader.SetGlobalVector(RippleRectId, Vector4.zero);
            Shader.SetGlobalFloat(WashPowerId, 0f);
        }
    }
}
