using System.Collections.Generic;
using _Game.Scripts.Enums;
using _Game.Scripts.World;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

namespace _Game.Scripts.Drone
{
    /// <summary>
    /// Опавшие листья разлетаются от потока винтов. Листья на земле — детали террейна (Nature Renderer, материал с
    /// _RotorWashClear): там, где поток их сдул, RotorWash.hlsl по маске вокруг дрона сдвигает их и прячет под землю,
    /// а вместо них взлетают листья-частицы из того же атласа — столько, сколько лежало на этом клочке (плотность слоя
    /// деталей). Частицы несёт воздух от винтов: струя вниз под дроном растекается по земле, а над растекающимся слоем
    /// кольцевой вихрь поднимает листья и тянет обратно к дрону. Лист сильно тормозится воздухом, падает медленно,
    /// порхает и кувыркается, ложится на землю, а сильный поток снова его поднимает; ветер тренировки сносит.
    /// Один на сцену: DroneRotorWash активного дрона каждый кадр сообщает свой поток (Blow).
    /// </summary>
    public sealed class RotorWashLeaves : MonoBehaviour
    {
        private const string LeafMaterialResource = "Materials/RotorLeaves";
        private const string BlowAwayProperty = "_RotorWashClear";
        // маска: 128 × 0.5 м = 64 м вокруг дрона; переезжает за ним, когда он отходит от центра на 10 м
        private const int MaskSize = 128;
        private const float MaskTexel = 0.5f;
        private const float RecenterDistance = 10f;
        // скорость воздуха, растекающегося по земле, в полный поток у самой земли (м/с). У 5–7" FPV под винтами ~10–15,
        // но при 12 листья сдувало, только когда дрон висел почти у земли, — поток взят сильнее
        private const float JetSpeed = 20f;
        // с какой скорости воздуха у земли сухой лист срывается с места (м/с); мокрый в дождь держится в 2.5 раза крепче
        private const float LiftOffSpeed = 1.8f;
        // выше этого (м) поток до листьев не доходит; с MaxWashHeight − 2 м сходит на нет
        public const float MaxWashHeight = 12f;
        // на этой высоте дрона (м) воздух у земли вдвое слабее, чем при дроне у самой земли. Прежний спад (вдвое уже
        // на 1.5 м, ещё и с затуханием потока травы) сдувал листья только почти вплотную
        private const float WashHeight = 4.5f;
        // как медленно воздух у земли слабеет от края струи (~1/r^SpreadDecay). Вместе с шириной струи (0.4 м + 0.8
        // высоты) с газом висения на 3–5 м сдувает почти всё в ~7 м вокруг и шевелит листья до ~20 м; прежде с 5 м
        // сдувало пятно ~2 м прямо под дроном, которого камера не видит
        private const float SpreadDecay = 0.9f;
        // с какого превышения срыва (доля LiftOffSpeed) с клочка улетает всё: слабее — только часть листьев, остальные
        // сдвигаются. Так широкий слабый край не выдувает дочиста десятки метров листвы
        private const float FullClearExcess = 1.5f;
        private const int GroundSpeedSamples = 128;
        // как быстро поток сдувает листья с клочка земли: доля в секунду при скорости вдвое выше срыва
        private const float ClearRate = 2.5f;
        private const float MaxClearRadius = 20f;
        // кольцевой вихрь у края струи: на 0.4 листья только скользили по земле и ложились в ~3 м, не взлетая
        private const float RingStrength = 0.9f;
        private const int MaxParticles = 4000;
        private const int MaxEmitPerFrame = 150;
        private const int MaxLeafMeshes = 4;
        // какую долю подложки-кучи клочка закрывают нарисованные на ней листья
        private const float PileCover = 0.35f;
        private const float Gravity = 9.81f;
        // лежащий лист чуть над землёй, иначе мерцает с террейном
        private const float RestLift = 0.008f;
        // когда частиц почти предел, лежащие дальше этого от дрона (м) тают, уступая место новым
        private const float FarLeafDistance = 40f;
        // дальние листья мелкие в кадре и в основном лишь сдвигаются: частиц с них меньше (в 8 м от дрона — половина)
        private const float EmitFalloff = 8f;
        private const float DataCheckInterval = 0.5f;
        private const float WeatherCheckInterval = 1f;
        private const string RainEffectName = "Rain";
        // сухой лист чуть просвечивает и ловит свет от земли: без этого против солнца летели почти чёрные пятна.
        // Свечение — доля света неба и солнца (как у пыли), ночью гаснет само
        private const float LeafGlow = 0.18f;

        private static readonly int MaskId = Shader.PropertyToID("_RotorWashLeafMask");
        private static readonly int RectId = Shader.PropertyToID("_RotorWashLeafRect");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        // сдутые листья — того же тона, что опад на земле (SeasonFoliage.hlsl): множитель (sRGB) ведёт текстуру листьев
        // тополя (линейная средняя 0.29, 0.18, 0.10) к среднему тону поры — осенью к средней из кучек (жёлтых, рыжих,
        // ржавых, бурых, красных), летом к тёмно-бурым прошлогодним, весной к выцветшим серо-бурым, зимой к мокрым
        private static readonly Color AutumnLeafTint = new(0.72f, 0.68f, 0.55f);
        private static readonly Color SpringLeafTint = new(0.63f, 0.71f, 0.81f);
        private static readonly Color SummerLeafTint = new(0.58f, 0.64f, 0.7f);
        private static readonly Color WinterLeafTint = new(0.53f, 0.57f, 0.63f);
        // скорость падения листа в тихом воздухе (м/с)
        private static readonly Vector2 FallSpeed = new(0.7f, 1.4f);
        // сколько сдутый лист лежит, потом незаметно тает (с)
        private static readonly Vector2 Lifetime = new(90f, 150f);

        public static RotorWashLeaves Instance { get; private set; }

        private readonly float[] _blown = new float[MaskSize * MaskSize];
        private readonly Vector2[] _push = new Vector2[MaskSize * MaskSize];
        private readonly float[] _density = new float[MaskSize * MaskSize];
        private readonly Color32[] _pixels = new Color32[MaskSize * MaskSize];
        private readonly float[] _shiftBlown = new float[MaskSize * MaskSize];
        private readonly Vector2[] _shiftPush = new Vector2[MaskSize * MaskSize];
        private readonly Dictionary<TerrainData, int[]> _layers = new();
        private readonly Dictionary<GameObject, bool> _blowable = new();
        private readonly List<Terrain> _windowTerrains = new();
        private readonly List<TerrainData> _windowData = new();
        private readonly List<Mesh> _meshes = new();
        // скорость воздуха у земли по расстоянию от точки под дроном — один раз за кадр для всей маски
        private readonly float[] _groundSpeed = new float[GroundSpeedSamples];
        private float _groundSpeedStep;

        private Texture2D _mask;
        private ParticleSystem _particles;
        private ParticleSystem.Particle[] _buffer;
        private Material _material;
        private float _leavesPerPatch = 1f;
        private Vector2Int _origin;
        private bool _hasWindow;
        private bool _maskDirty;
        private float _nextDataCheck;
        private float _nextWeatherCheck;
        private float _rain;
        private float _emitDebt;
        private int _airborne;
        private Terrain _heightTerrain;

        private Vector3 _ground;
        private float _height;
        private float _spin;
        private Vector3 _wind;
        private Terrain _terrain;
        private int _blowFrame = -1;
        // поток в этом кадре: скорость воздуха у земли под краем струи (м/с) и радиус струи у земли (м)
        private float _power;
        private float _jetRadius;

        public string Stats
        {
            get
            {
                int cleared = 0;

                foreach (float blown in _blown)
                    if (blown > 0.5f)
                        cleared++;

                int count = _particles != null ? _particles.particleCount : 0;
                return $"leaves={count} airborne={_airborne} cleared={cleared * MaskTexel * MaskTexel:F1}m2 " +
                       $"window={(_hasWindow ? _origin.ToString() : "none")} perPatch={_leavesPerPatch} " +
                       $"power={_power:F1} jet={_jetRadius:F2} meshes={_meshes.Count}";
            }
        }

        public static void Ensure(Scene scene)
        {
            if (Instance != null)
                return;

            GameObject leavesObject = new("Rotor wash leaves");

            if (scene.IsValid() && scene.isLoaded)
                SceneManager.MoveGameObjectToScene(leavesObject, scene);

            leavesObject.AddComponent<RotorWashLeaves>();
        }

        // Поток активного дрона в этом кадре: точка на земле под ним, высота над ней, обороты винтов (0..1, с арма —
        // холостые), ветер (м/с, куда дует).
        public void Blow(Vector3 ground, float height, float spin, Vector3 wind, Terrain terrain)
        {
            _ground = ground;
            _height = height;
            _spin = spin;
            _wind = wind;
            _terrain = terrain;
            _blowFrame = Time.frameCount;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            _mask = new Texture2D(MaskSize, MaskSize, TextureFormat.RGBA32, false, true)
            {
                name = "Rotor wash leaf mask",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            ClearMask();
            Upload();
            Shader.SetGlobalTexture(MaskId, _mask);
            Shader.SetGlobalVector(RectId, Vector4.zero);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                Shader.SetGlobalVector(RectId, Vector4.zero);
            }

            if (_mask != null)
                Destroy(_mask);

            if (_material != null)
                Destroy(_material);

            foreach (Mesh mesh in _meshes)
                Destroy(mesh);
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            if (dt <= 0f)
                return;

            // поток сообщают в LateUpdate: прошлый кадр ещё свежий
            bool washing = _blowFrame >= Time.frameCount - 1 && _spin > 0.001f;
            // струя слабеет с высотой и расходится шире
            float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(MaxWashHeight - 2f, MaxWashHeight, _height));
            _power = washing ? JetSpeed * _spin * fade / (1f + _height * _height / (WashHeight * WashHeight)) : 0f;
            _jetRadius = 0.4f + 0.8f * Mathf.Min(_height, MaxWashHeight);

            if (_power > 0.5f && (!_hasWindow || FarFromWindowCenter(_ground)))
                Recenter(_ground);

            if (_hasWindow && Time.unscaledTime >= _nextDataCheck)
            {
                _nextDataCheck = Time.unscaledTime + DataCheckInterval;
                CheckWindowTerrains();
            }

            if (Time.unscaledTime >= _nextWeatherCheck)
            {
                _nextWeatherCheck = Time.unscaledTime + WeatherCheckInterval;
                _rain = _Game.Scripts.World.EnvironmentQueries.EffectIntensity(RainEffectName);

                if (_material != null)
                {
                    Season season = Seasons.Instance != null ? Seasons.Instance.Current : Season.Summer;
                    Color tint = season switch
                    {
                        Season.Spring => SpringLeafTint,
                        Season.Summer => SummerLeafTint,
                        Season.Winter => WinterLeafTint,
                        _ => AutumnLeafTint
                    };
                    _material.SetColor(BaseColorId, tint);
                    _material.SetColor(EmissionId, _Game.Scripts.World.EnvironmentQueries.DustLight() * LeafGlow * tint);
                }
            }

            float liftOff = LiftOffSpeed * (1f + 1.5f * _rain);
            Simulate(dt, liftOff);

            if (_hasWindow && _particles != null && _power > liftOff)
                Clear(dt, liftOff);

            if (_maskDirty)
                Upload();
        }

        // Поток сдувает листья с земли вокруг дрона: доля сдутого на текселе растёт, пока воздух у земли быстрее срыва, —
        // до той доли, которую этот воздух вообще может сдуть, и столько же листьев, сколько там лежало, взлетает частицами.
        private void Clear(float dt, float liftOff)
        {
            float reach = Mathf.Min(MaxClearRadius, _jetRadius * Mathf.Pow(_power / liftOff, 1f / SpreadDecay) + 1f);
            SampleGroundSpeed(reach);
            int centerX = Mathf.FloorToInt(_ground.x / MaskTexel) - _origin.x;
            int centerZ = Mathf.FloorToInt(_ground.z / MaskTexel) - _origin.y;
            int radius = Mathf.CeilToInt(reach / MaskTexel);
            float texelArea = MaskTexel * MaskTexel;
            int emitted = 0;

            for (int z = Mathf.Max(0, centerZ - radius); z <= Mathf.Min(MaskSize - 1, centerZ + radius); z++)
            {
                for (int x = Mathf.Max(0, centerX - radius); x <= Mathf.Min(MaskSize - 1, centerX + radius); x++)
                {
                    int index = z * MaskSize + x;
                    float blown = _blown[index];

                    if (blown >= 1f)
                        continue;

                    float dx = (_origin.x + x + 0.5f) * MaskTexel - _ground.x;
                    float dz = (_origin.y + z + 0.5f) * MaskTexel - _ground.z;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);

                    if (distance > reach)
                        continue;

                    float speed = GroundSpeed(distance);
                    float excess = (speed - liftOff) / liftOff;
                    // слабый воздух сдувает только самые лёгкие листья клочка, остальные лишь сдвигаются
                    float limit = Mathf.Min(1f, excess / FullClearExcess);

                    if (blown >= limit)
                        continue;

                    float add = Mathf.Min(limit - blown, ClearRate * Mathf.Min(excess, 2f) * dt);
                    Vector2 outward = distance > 0.001f ? new Vector2(dx, dz) / distance : Vector2.right;
                    _blown[index] = blown + add;
                    _push[index] += outward * add;
                    WritePixel(index);
                    _maskDirty = true;

                    _emitDebt += add * _density[index] * texelArea / (1f + distance / EmitFalloff);
                    Vector3 air = new(outward.x * speed, 0f, outward.y * speed);

                    for (; _emitDebt >= 1f && emitted < MaxEmitPerFrame; _emitDebt -= 1f, emitted++)
                        EmitLeaf(x, z, air, speed, liftOff);
                }
            }

            // больше, чем успели выпустить, не копим: в следующем кадре листья полетят уже с другой земли
            _emitDebt = Mathf.Min(_emitDebt, 1f);
        }

        // Воздух у земли растекается от точки под дроном одинаково во все стороны: скорость зависит только от расстояния.
        private void SampleGroundSpeed(float reach)
        {
            _groundSpeedStep = Mathf.Max(reach, 0.01f) / (GroundSpeedSamples - 1);
            Vector3 point = _ground;

            for (int i = 0; i < GroundSpeedSamples; i++)
            {
                point.x = _ground.x + i * _groundSpeedStep;
                _groundSpeed[i] = Horizontal(WashAir(point, 0f));
            }
        }

        private float GroundSpeed(float distance)
        {
            float at = distance / _groundSpeedStep;
            int index = Mathf.Min((int)at, GroundSpeedSamples - 2);
            return Mathf.Lerp(_groundSpeed[index], _groundSpeed[index + 1], at - index);
        }

        private void EmitLeaf(int x, int z, Vector3 air, float speed, float liftOff)
        {
            Vector3 position = new((_origin.x + x + Random.value) * MaskTexel, 0f, (_origin.y + z + Random.value) * MaskTexel);
            position.y = GroundHeight(position) + 0.02f;
            ParticleSystem.EmitParams leaf = new()
            {
                position = position,
                velocity = Kick(air, speed, liftOff),
                rotation3D = new Vector3((Random.value < 0.5f ? 0f : 180f) + Random.Range(-20f, 20f), Random.Range(0f, 360f), Random.Range(-20f, 20f)),
                startSize = Random.Range(0.85f, 1.15f),
                startLifetime = Random.Range(Lifetime.x, Lifetime.y),
                randomSeed = (uint)Random.Range(1, int.MaxValue)
            };
            _particles.Emit(leaf, 1);
        }

        // Движение листьев: силы считаем здесь, переносит по скорости и рисует сама система частиц.
        private void Simulate(float dt, float liftOff)
        {
            if (_particles == null)
                return;

            int count = _particles.particleCount;

            if (count == 0)
            {
                _airborne = 0;
                return;
            }

            // все лежат, а поднять их нечем
            if (_airborne == 0 && _power < liftOff)
                return;

            count = _particles.GetParticles(_buffer, count);
            bool crowded = count > MaxParticles - MaxEmitPerFrame;
            float time = Time.time;
            int airborne = 0;

            for (int i = 0; i < count; i++)
            {
                ref ParticleSystem.Particle leaf = ref _buffer[i];
                Vector3 position = leaf.position;
                Vector3 velocity = leaf.velocity;
                uint seed = leaf.randomSeed;

                // лежит на земле
                if (velocity == Vector3.zero)
                {
                    if (crowded && (position - _ground).sqrMagnitude > FarLeafDistance * FarLeafDistance)
                        leaf.remainingLifetime = Mathf.Min(leaf.remainingLifetime, 0.5f);

                    if (_power < liftOff)
                        continue;

                    Vector3 air = WashAir(position, 0f);
                    float speed = Horizontal(air);

                    if (speed < liftOff)
                        continue;

                    leaf.velocity = Kick(air, speed, liftOff);
                    leaf.position = position + Vector3.up * 0.02f;
                    airborne++;
                    continue;
                }

                float ground = GroundHeight(position);
                float height = position.y - ground;
                float fall = Mathf.Lerp(FallSpeed.x, FallSpeed.y, Hash(seed, 1));
                Vector3 wash = WashAir(position, Mathf.Max(0f, height));
                Vector3 target = _wind + wash + Turbulence(wash, time, seed) + Flutter(time, seed, fall) + Vector3.down * fall;
                // сопротивление воздуха: лист догоняет воздух за ~0.1 с, в тихом воздухе падает со скоростью fall
                velocity = target + (velocity - target) * Mathf.Exp(-dt * Gravity / fall);
                // кувыркается тем быстрее, чем быстрее летит
                leaf.rotation3D += Spin(seed) * ((0.35f + Mathf.Min(velocity.magnitude, 6f) * 0.25f) * dt);

                if (height <= RestLift && velocity.y <= 0f)
                {
                    float groundSpeed = _power > 0f ? Horizontal(WashAir(position, 0f)) : 0f;

                    if (groundSpeed <= liftOff)
                    {
                        position.y = ground + RestLift * (0.5f + Hash(seed, 4));
                        leaf.position = position;
                        leaf.velocity = Vector3.zero;
                        leaf.rotation3D = Flat(leaf.rotation3D, seed);
                        continue;
                    }

                    // поток тащит лист по земле и подбрасывает
                    position.y = ground + RestLift;
                    velocity.y = -velocity.y * 0.3f + (groundSpeed - liftOff) * 0.3f;
                    velocity.x *= 0.8f;
                    velocity.z *= 0.8f;
                    leaf.position = position;
                }

                leaf.velocity = velocity;
                airborne++;
            }

            _particles.SetParticles(_buffer, count);
            _airborne = airborne;
        }

        // Воздух от винтов (м/с) на высоте height над землёй. Струя бьёт в землю под дроном и растекается: быстрее
        // всего у края струи, дальше слабеет ~1/r, тонким слоем у земли. Над этим слоем — кольцевой вихрь: снаружи
        // воздух поднимается, сверху идёт обратно к дрону и снова вниз, так что листья кружат вокруг.
        private Vector3 WashAir(Vector3 position, float height)
        {
            if (_power <= 0f)
                return Vector3.zero;

            float dx = position.x - _ground.x;
            float dz = position.z - _ground.z;
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            float jet = _jetRadius;

            if (distance > jet * 16f + MaxClearRadius)
                return Vector3.zero;

            float spread = distance < jet ? Mathf.Lerp(0.6f, 1f, distance / jet) : Mathf.Pow(jet / distance, SpreadDecay);
            float radial = _power * spread * Mathf.Exp(-height / (0.2f + 0.1f * distance));
            float core = Mathf.Clamp01(1f - distance / (jet * 1.2f));
            float vertical = -0.8f * _power * core * Mathf.Clamp01(height / 0.5f) * (height < _height + 0.3f ? 1f : 0.3f);

            float ringRadius = 1.5f * jet + 0.3f;
            float ringHeight = 0.15f + 0.3f * _height;
            float ringCore = 0.3f + 0.15f * _height;
            float across = distance - ringRadius;
            float up = height - ringHeight;
            float squared = across * across + up * up;
            float apart = Mathf.Sqrt(squared) + 0.0001f;
            float coreSquared = ringCore * ringCore;
            float swirl = RingStrength * _power * (1f - Mathf.Exp(-squared / coreSquared)) * ringCore / apart *
                          Mathf.Exp(-squared / (9f * coreSquared));
            // по кольцу: под ядром — от дрона, снаружи — вверх, над ядром — к дрону, внутри — вниз
            radial -= swirl * up / apart;
            vertical += swirl * across / apart;

            float inverse = distance > 0.001f ? 1f / distance : 0f;
            return new Vector3(dx * inverse * radial, vertical, dz * inverse * radial);
        }

        // Лист срывается с земли: по потоку и вверх, тем выше, чем сильнее дует.
        private static Vector3 Kick(Vector3 air, float speed, float liftOff)
        {
            float along = (speed - liftOff) * Random.Range(0.4f, 0.9f) / speed;
            float up = Random.Range(0.5f, 1.5f) * Mathf.Min(4f, 0.5f * speed);
            return new Vector3(air.x * along, up, air.z * along);
        }

        // Поток рваный: каждый лист болтает по-своему, сильнее там, где дует сильнее; порывы у земли чаще
        // подбрасывают, чем прижимают.
        private static Vector3 Turbulence(Vector3 wash, float time, uint seed)
        {
            float amount = 0.45f * wash.magnitude;

            if (amount < 0.01f)
                return Vector3.zero;

            float phase = Hash(seed, 10) * 50f;
            return new Vector3(
                Mathf.Sin(time * 7.3f + phase),
                0.7f * Mathf.Sin(time * 5.9f + phase * 1.3f) + 0.3f,
                Mathf.Sin(time * 6.7f + phase * 0.7f)) * amount;
        }

        // Падающий лист порхает: ходит кругами и то планирует, то ныряет.
        private static Vector3 Flutter(float time, uint seed, float fall)
        {
            float angle = time * Mathf.Lerp(3f, 6f, Hash(seed, 3)) + Hash(seed, 2) * 6.2831853f;
            float swing = fall * Mathf.Lerp(0.4f, 0.8f, Hash(seed, 11));
            return new Vector3(Mathf.Cos(angle) * swing, 0.5f * fall * Mathf.Sin(2f * angle), Mathf.Sin(angle) * swing);
        }

        // Скорость кувырка (°/с) по осям, у каждого листа своя.
        private static Vector3 Spin(uint seed) =>
            new Vector3(Hash(seed, 7) - 0.5f, (Hash(seed, 8) - 0.5f) * 0.5f, Hash(seed, 9) - 0.5f) * 900f;

        // Лёг как упал — лицом вверх или вниз, с небольшим наклоном на неровной подстилке.
        private static Vector3 Flat(Vector3 rotation, uint seed)
        {
            bool flipped = Mathf.Cos(rotation.x * Mathf.Deg2Rad) * Mathf.Cos(rotation.z * Mathf.Deg2Rad) < 0f;
            return new Vector3((flipped ? 180f : 0f) + (Hash(seed, 5) - 0.5f) * 16f, rotation.y, (Hash(seed, 6) - 0.5f) * 16f);
        }

        private static float Horizontal(Vector3 vector) => Mathf.Sqrt(vector.x * vector.x + vector.z * vector.z);

        private static float Hash(uint seed, uint salt)
        {
            unchecked
            {
                uint hash = seed * 747796405u + salt * 2891336453u;
                hash ^= hash >> 16;
                hash *= 0x7feb352du;
                hash ^= hash >> 15;
                hash *= 0x846ca68bu;
                hash ^= hash >> 16;
                return (hash & 0xFFFFFF) / 16777216f;
            }
        }

        private float GroundHeight(Vector3 position)
        {
            Terrain terrain = _Game.Scripts.World.EnvironmentQueries.TerrainContains(_heightTerrain, position) ? _heightTerrain
                : _Game.Scripts.World.EnvironmentQueries.TerrainContains(_terrain, position) ? _terrain
                : FindTerrain(position);
            _heightTerrain = terrain;
            return terrain != null ? terrain.SampleHeight(position) + terrain.transform.position.y : _ground.y;
        }

        private static Terrain FindTerrain(Vector3 position)
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
                if (_Game.Scripts.World.EnvironmentQueries.TerrainContains(terrain, position))
                    return terrain;

            return null;
        }

        private bool FarFromWindowCenter(Vector3 point)
        {
            float half = MaskSize * MaskTexel * 0.5f;
            return Mathf.Abs(point.x - (_origin.x * MaskTexel + half)) > RecenterDistance ||
                   Mathf.Abs(point.z - (_origin.y * MaskTexel + half)) > RecenterDistance;
        }

        private void Recenter(Vector3 center)
        {
            Vector2Int origin = new(
                Mathf.FloorToInt(center.x / MaskTexel) - MaskSize / 2,
                Mathf.FloorToInt(center.z / MaskTexel) - MaskSize / 2);

            if (_hasWindow)
                Shift(origin - _origin);
            else
                ClearMask();

            _origin = origin;
            _hasWindow = true;
            SampleDensity();
            Shader.SetGlobalVector(RectId, new Vector4(_origin.x * MaskTexel, _origin.y * MaskTexel, 1f / (MaskSize * MaskTexel), 1f));
        }

        // Окно переехало на offset текселей: общая часть остаётся, на новой земле листья нетронутые.
        private void Shift(Vector2Int offset)
        {
            System.Array.Clear(_shiftBlown, 0, _shiftBlown.Length);
            System.Array.Clear(_shiftPush, 0, _shiftPush.Length);

            for (int z = 0; z < MaskSize; z++)
            {
                int fromZ = z + offset.y;

                if (fromZ < 0 || fromZ >= MaskSize)
                    continue;

                for (int x = 0; x < MaskSize; x++)
                {
                    int fromX = x + offset.x;

                    if (fromX < 0 || fromX >= MaskSize)
                        continue;

                    _shiftBlown[z * MaskSize + x] = _blown[fromZ * MaskSize + fromX];
                    _shiftPush[z * MaskSize + x] = _push[fromZ * MaskSize + fromX];
                }
            }

            System.Array.Copy(_shiftBlown, _blown, _blown.Length);
            System.Array.Copy(_shiftPush, _push, _push.Length);

            for (int i = 0; i < _pixels.Length; i++)
                WritePixel(i);

            _maskDirty = true;
        }

        private void ClearMask()
        {
            System.Array.Clear(_blown, 0, _blown.Length);
            System.Array.Clear(_push, 0, _push.Length);

            for (int i = 0; i < _pixels.Length; i++)
                WritePixel(i);

            _maskDirty = true;
        }

        private void WritePixel(int index)
        {
            Vector2 push = _push[index];
            float length = push.magnitude;
            Vector2 direction = length > 0.00001f ? push / length : Vector2.zero;
            _pixels[index] = new Color32(
                (byte)(Mathf.Clamp01(_blown[index]) * 255f + 0.5f),
                (byte)(direction.x * 127.5f + 127.5f),
                (byte)(direction.y * 127.5f + 127.5f),
                255);
        }

        private void Upload()
        {
            _mask.SetPixels32(_pixels);
            _mask.Apply(false);
            _maskDirty = false;
        }

        // Сколько листьев лежит на каждом текселе маски: слои деталей с листьями у террейнов под окном.
        private void SampleDensity()
        {
            System.Array.Clear(_density, 0, _density.Length);
            _windowTerrains.Clear();
            _windowData.Clear();

            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (!UnderWindow(terrain))
                    continue;

                TerrainData data = terrain.terrainData;
                _windowTerrains.Add(terrain);
                _windowData.Add(data);
                int[] layers = BlowableLayers(data);

                if (layers.Length > 0)
                    AddDensity(terrain, data, layers);
            }
        }

        private void AddDensity(Terrain terrain, TerrainData data, int[] layers)
        {
            Vector3 corner = terrain.transform.position;
            Vector3 size = data.size;
            float left = _origin.x * MaskTexel;
            float bottom = _origin.y * MaskTexel;
            float extent = MaskSize * MaskTexel;
            int resolution = data.detailResolution;
            float cellX = size.x / resolution;
            float cellZ = size.z / resolution;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((left - corner.x) / cellX), 0, resolution - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((left + extent - corner.x) / cellX), 0, resolution - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((bottom - corner.z) / cellZ), 0, resolution - 1);
            int z1 = Mathf.Clamp(Mathf.FloorToInt((bottom + extent - corner.z) / cellZ), 0, resolution - 1);
            // слой деталей хранит число клочков на ячейку, в клочке _leavesPerPatch листьев
            float leavesPerCount = _leavesPerPatch / (cellX * cellZ);

            foreach (int layer in layers)
            {
                int[,] counts = data.GetDetailLayer(x0, z0, x1 - x0 + 1, z1 - z0 + 1, layer);

                for (int tz = 0; tz < MaskSize; tz++)
                {
                    float localZ = (_origin.y + tz + 0.5f) * MaskTexel - corner.z;

                    if (localZ < 0f || localZ >= size.z)
                        continue;

                    int cz = Mathf.Min(Mathf.FloorToInt(localZ / cellZ), resolution - 1) - z0;

                    if (cz < 0 || cz > z1 - z0)
                        continue;

                    for (int tx = 0; tx < MaskSize; tx++)
                    {
                        float localX = (_origin.x + tx + 0.5f) * MaskTexel - corner.x;

                        if (localX < 0f || localX >= size.x)
                            continue;

                        int cx = Mathf.Min(Mathf.FloorToInt(localX / cellX), resolution - 1) - x0;

                        if (cx >= 0 && cx <= x1 - x0)
                            _density[tz * MaskSize + tx] += counts[cz, cx] * leavesPerCount;
                    }
                }
            }
        }

        private bool UnderWindow(Terrain terrain)
        {
            if (terrain == null || terrain.terrainData == null)
                return false;

            Vector3 corner = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            float left = _origin.x * MaskTexel;
            float bottom = _origin.y * MaskTexel;
            float extent = MaskSize * MaskTexel;
            return corner.x < left + extent && corner.x + size.x > left &&
                   corner.z < bottom + extent && corner.z + size.z > bottom;
        }

        // Террейн под окном сменил TerrainData (точку перегенерировали, тайл перезагрузили) — листья там новые:
        // маска и лежащие частицы больше не про эту землю. Добавился тайл под окном — только пересчёт листьев.
        private void CheckWindowTerrains()
        {
            bool replaced = false;

            for (int i = 0; i < _windowTerrains.Count && !replaced; i++)
                replaced = _windowTerrains[i] == null || _windowTerrains[i].terrainData != _windowData[i];

            int under = 0;

            foreach (Terrain terrain in Terrain.activeTerrains)
                if (UnderWindow(terrain))
                    under++;

            if (replaced)
            {
                ClearMask();
                _layers.Clear();

                if (_particles != null)
                    _particles.Clear();

                _airborne = 0;
            }

            if (replaced || under != _windowTerrains.Count)
                SampleDensity();
        }

        private int[] BlowableLayers(TerrainData data)
        {
            if (_layers.TryGetValue(data, out int[] layers))
                return layers;

            List<int> found = new();
            DetailPrototype[] prototypes = data.detailPrototypes;

            for (int i = 0; i < prototypes.Length; i++)
            {
                GameObject prefab = prototypes[i].usePrototypeMesh ? prototypes[i].prototype : null;

                if (prefab == null || !IsBlowable(prefab))
                    continue;

                found.Add(i);
                EnsureParticles(prefab);
            }

            layers = found.ToArray();
            _layers[data] = layers;
            return layers;
        }

        private bool IsBlowable(GameObject prefab)
        {
            if (_blowable.TryGetValue(prefab, out bool blowable))
                return blowable;

            Renderer renderer = LeafRenderer(prefab);
            Material material = renderer != null ? renderer.sharedMaterial : null;
            blowable = material != null && material.HasProperty(BlowAwayProperty) && material.GetFloat(BlowAwayProperty) > 0.5f;
            _blowable[prefab] = blowable;
            return blowable;
        }

        // Ближний LOD клочка: в нём листья отдельными четырёхугольниками.
        private static Renderer LeafRenderer(GameObject prefab)
        {
            LODGroup group = prefab.GetComponent<LODGroup>();
            LOD[] lods = group != null ? group.GetLODs() : null;

            if (lods != null && lods.Length > 0)
                foreach (Renderer renderer in lods[0].renderers)
                    if (renderer != null)
                        return renderer;

            return prefab.GetComponentInChildren<MeshRenderer>();
        }

        private void EnsureParticles(GameObject prefab)
        {
            if (_particles != null)
                return;

            Renderer renderer = LeafRenderer(prefab);
            MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            Mesh source = filter != null ? filter.sharedMesh : null;
            Material template = Resources.Load<Material>(LeafMaterialResource);

            if (source == null || !source.isReadable || template == null)
            {
                Debug.LogWarning($"[RotorWashLeaves] No flying leaves for {prefab.name}: readable mesh " +
                                 $"{source != null && source.isReadable}, material Resources/{LeafMaterialResource} " +
                                 $"{(template != null ? "found" : "missing")}", this);
                return;
            }

            // лист — как в префабе вместе с масштабом корня: Nature Renderer рисует клочок с ним. ReplaceUnsupportedDetails
            // ставит в прототип террейна не корень префаба, а его LOD0, у которого свой масштаб 1, а у корня — 4
            Transform root = renderer.transform.root;
            Matrix4x4 toPrefab = Matrix4x4.Scale(root.localScale) * root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            _leavesPerPatch = CutLeaves(source, toPrefab, _meshes);

            if (_meshes.Count == 0)
                return;

            Material leaf = renderer.sharedMaterial;
            _material = new Material(template) { name = $"{template.name} ({prefab.name})" };
            CopyTexture(leaf, "_BaseColorMap", _material, "_BaseMap");
            CopyTexture(leaf, "_BaseColorMap", _material, "_EmissionMap");
            CopyTexture(leaf, "_NormalMap", _material, "_BumpMap");

            if (leaf.HasProperty("_AlphaCutoff"))
                _material.SetFloat("_Cutoff", leaf.GetFloat("_AlphaCutoff"));

            _particles = CreateParticles(_material);
            _buffer = new ParticleSystem.Particle[MaxParticles];
        }

        private static void CopyTexture(Material from, string fromName, Material to, string toName)
        {
            Texture texture = from.HasProperty(fromName) ? from.GetTexture(fromName) : null;

            if (texture != null)
                to.SetTexture(toName, texture);
        }

        // Делит клочок на отдельные листья (связные треугольники); большая подложка-куча под ними — не лист.
        // Возвращает, сколько листьев в клочке; в meshes — до 4 разных листьев (разные места атласа) для частиц.
        private static float CutLeaves(Mesh source, Matrix4x4 toPrefab, List<Mesh> meshes)
        {
            Vector3[] vertices = source.vertices;
            Vector3[] normals = source.normals;
            Vector2[] uvs = source.uv;
            int[] triangles = source.triangles;

            if (uvs.Length != vertices.Length || triangles.Length == 0)
                return 1f;

            int[] parent = new int[vertices.Length];

            for (int i = 0; i < parent.Length; i++)
                parent[i] = i;

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Union(parent, triangles[t], triangles[t + 1]);
                Union(parent, triangles[t], triangles[t + 2]);
            }

            Dictionary<int, List<int>> pieces = new();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                int root = Root(parent, triangles[t]);

                if (!pieces.TryGetValue(root, out List<int> piece))
                    pieces[root] = piece = new List<int>();

                piece.Add(t);
            }

            List<(List<int> triangles, float area, Vector2 uv)> parts = new();

            foreach (List<int> piece in pieces.Values)
            {
                float area = 0f;
                Vector2 uv = Vector2.zero;

                foreach (int t in piece)
                {
                    Vector3 a = toPrefab.MultiplyPoint3x4(vertices[triangles[t]]);
                    Vector3 b = toPrefab.MultiplyPoint3x4(vertices[triangles[t + 1]]);
                    Vector3 c = toPrefab.MultiplyPoint3x4(vertices[triangles[t + 2]]);
                    area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                    uv += (uvs[triangles[t]] + uvs[triangles[t + 1]] + uvs[triangles[t + 2]]) / 3f;
                }

                parts.Add((piece, area, uv / piece.Count));
            }

            parts.Sort((a, b) => a.area.CompareTo(b.area));
            float median = parts[parts.Count / 2].area;
            // подложка нарисована кучей листьев и исчезает вместе с клочком: считаем, сколько листьев на ней видно,
            // иначе сдутая куча оставляла на земле проплешину, а взлетало заметно меньше листьев
            float pileLeaves = 0f;

            foreach (var (_, area, _) in parts)
                if (area > median * 3f)
                    pileLeaves += area / median * PileCover;

            parts.RemoveAll(part => part.area > median * 3f);
            parts.Reverse();
            List<Vector2> used = new();

            foreach (var (piece, _, uv) in parts)
            {
                if (meshes.Count >= MaxLeafMeshes)
                    break;

                if (used.Exists(other => (other - uv).sqrMagnitude < 0.0025f))
                    continue;

                used.Add(uv);
                meshes.Add(LeafMesh(piece, triangles, vertices, normals, uvs, toPrefab));
            }

            return parts.Count + Mathf.Round(pileLeaves);
        }

        private static Mesh LeafMesh(List<int> piece, int[] triangles, Vector3[] vertices, Vector3[] normals, Vector2[] uvs, Matrix4x4 toPrefab)
        {
            Dictionary<int, int> remap = new();
            List<Vector3> positions = new();
            List<Vector3> leafNormals = new();
            List<Vector2> leafUvs = new();
            List<int> front = new();

            foreach (int t in piece)
            {
                for (int k = 0; k < 3; k++)
                {
                    int index = triangles[t + k];

                    if (!remap.TryGetValue(index, out int local))
                    {
                        remap[index] = local = positions.Count;
                        positions.Add(toPrefab.MultiplyPoint3x4(vertices[index]));
                        leafNormals.Add(index < normals.Length ? toPrefab.MultiplyVector(normals[index]).normalized : Vector3.up);
                        leafUvs.Add(uvs[index]);
                    }

                    front.Add(local);
                }
            }

            // вокруг центра листа он и кувыркается
            Vector3 center = Vector3.zero;

            foreach (Vector3 position in positions)
                center += position;

            center /= positions.Count;
            int count = positions.Count;

            // обратная сторона — свои треугольники с обратной нормалью: лист снизу освещён как надо
            for (int i = 0; i < count; i++)
            {
                positions[i] -= center;
                positions.Add(positions[i]);
                leafNormals.Add(-leafNormals[i]);
                leafUvs.Add(leafUvs[i]);
            }

            List<int> indices = new(front);

            for (int i = 0; i < front.Count; i += 3)
            {
                indices.Add(front[i] + count);
                indices.Add(front[i + 2] + count);
                indices.Add(front[i + 1] + count);
            }

            Mesh mesh = new() { name = "Rotor wash leaf" };
            mesh.SetVertices(positions);
            mesh.SetNormals(leafNormals);
            mesh.SetUVs(0, leafUvs);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        private static int Root(int[] parent, int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        private static void Union(int[] parent, int a, int b)
        {
            a = Root(parent, a);
            b = Root(parent, b);

            if (a != b)
                parent[b] = a;
        }

        private ParticleSystem CreateParticles(Material material)
        {
            GameObject particlesObject = new("Rotor wash leaf particles");
            particlesObject.transform.SetParent(transform, false);
            ParticleSystem particles = particlesObject.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = particles.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = MaxParticles;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            main.startRotation3D = true;
            // листья вне кадра тоже летят и ложатся
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

            ParticleSystem.EmissionModule emission = particles.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = particles.shape;
            shape.enabled = false;

            // в конце жизни лежащий лист незаметно тает
            ParticleSystem.SizeOverLifetimeModule size = particles.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.95f, 1f), new Keyframe(1f, 0f)));

            ParticleSystemRenderer renderer = particlesObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.SetMeshes(_meshes.ToArray(), Mathf.Min(_meshes.Count, MaxLeafMeshes));
            renderer.meshDistribution = ParticleSystemMeshDistribution.UniformRandom;
            renderer.alignment = ParticleSystemRenderSpace.World;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.enableGPUInstancing = false;
            renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream>
            {
                ParticleSystemVertexStream.Position,
                ParticleSystemVertexStream.Normal,
                ParticleSystemVertexStream.Tangent,
                ParticleSystemVertexStream.Color,
                ParticleSystemVertexStream.UV
            });
            particles.Play();
            return particles;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetInstance()
        {
            Instance = null;
            Shader.SetGlobalVector(RectId, Vector4.zero);
        }
    }
}
