using System;
using System.Collections.Generic;
using GeoWorld;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Насколько посечено место войной (0 — боёв не было, 1 — самое пекло) в окне 8192 м вокруг камеры, 8 м на тексель
    /// (R8, глобальная _WarMap, читается LOAD — без сэмплера): наибольшее из плотности воронок (CraterField: 1 на гектар —
    /// 0, 15 и больше — 1; гектар — круг ~60 м) и близости руин (RuinField: разрушенное здание — 0.95 у стены, тяжело
    /// повреждённое 0.7, повреждённое 0.35, к 35 м плюс высота/2 — нуль). По ней деревья (SeasonTrees.hlsl SeasonWarStrip)
    /// стоят с посечёнными кронами: каждое своим случаем по корню — голые ветки или прореженная листва; окно — на всю
    /// дальность деревьев (2 км) и впереди камеры, когда она ушла от центра на 1 км до пересборки (4096 − 1024 ≥ 2000 +
    /// запас). Плотность воронок у края окна считается и по воронкам за ним (поле счёта шире окна на радиус круга).
    /// Карта и решение по дереву есть и на CPU (At, TreeStrip): лесная подстилка и опад под деревьями (ForestFloor —
    /// в фоновом потоке, GroundDressing) — бит в бит как шейдер; читается одним снимком (байты и начало окна вместе).
    /// Нет воронок и руин — _WarTreesOn = 0, шейдер карту не читает. Пересобирается, когда камера ушла на 1 км от центра
    /// или изменились воронки и руины (новые тайлы).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WarMap : MonoBehaviour
    {
        public const float Window = 8192f;
        public const int Texels = 1024;
        public const float Texel = Window / Texels;
        private const float Recentre = 1024f;
        // круг воронок вокруг текселя: ~60 м (7 текселей), площадь — для плотности на гектар
        private const int CraterRadiusTexels = 7;

        private static readonly int MapId = Shader.PropertyToID("_WarMap");
        private static readonly int RectId = Shader.PropertyToID("_WarMapRect");
        private static readonly int OnId = Shader.PropertyToID("_WarTreesOn");

        // карта для CPU одним объектом: фоновый поток (ForestFloor) берёт ссылку раз и читает согласованные байты и начало
        private sealed class Snapshot
        {
            public byte[] bytes;
            public Vector2 origin;
        }

        private static Snapshot _cpu;

        private Texture2D _map;
        private CraterField _craters;
        private int _ruinVersion = -1;
        private Vector2 _centre;
        private float _nextCheck;

        public static WarMap Instance { get; private set; }

        /// <summary>Меняется с каждой пересборкой карты (ForestFloor пересобирается вслед).</summary>
        public static int Version { get; private set; }

        /// <summary>Посечённость места (0..1) по карте; 0 вне окна и без войны.</summary>
        public static float At(float x, float z)
        {
            Snapshot map = _cpu;

            if (map == null)
                return 0f;

            int ix = Mathf.FloorToInt((x - map.origin.x) / Texel), iz = Mathf.FloorToInt((z - map.origin.y) / Texel);

            if (ix < 0 || iz < 0 || ix >= Texels || iz >= Texels)
                return 0f;

            return map.bytes[iz * Texels + ix] / 255f;
        }

        /// <summary>
        /// Насколько посечена крона дерева с корнем в (x, z): 0 — целая, 1 — голые ветки (SeasonTrees.hlsl SeasonWarStrip:
        /// saturate(посечённость × 1.6 − случай дерева), случай — Jenkins-хеш корня с точностью 10 см и солью 45).
        /// </summary>
        public static float TreeStrip(float x, float z)
        {
            float s = At(x, z);

            if (s <= 0f)
                return 0f;

            return Mathf.Clamp01(s * 1.6f - TreeRandom(x, z, 45u));
        }

        // SeasonTrees.hlsl SeasonTreeRandom(salt), бит в бит (Random.hlsl GenerateHashedRandomFloat: Jenkins)
        public static float TreeRandom(float x, float z, uint salt)
        {
            uint ix = unchecked((uint)(int)Mathf.Floor(x * 10f));
            uint iz = unchecked((uint)(int)Mathf.Floor(z * 10f));
            uint hash = Jenkins(ix ^ Jenkins(iz ^ Jenkins(salt)));
            return BitConverter.Int32BitsToSingle((int)((hash & 0x007FFFFFu) | 0x3F800000u)) - 1f;
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
                return x;
            }
        }

        private void Awake()
        {
            Instance = this;
            _map = new Texture2D(Texels, Texels, TextureFormat.R8, false, true)
            {
                name = "War map", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave
            };
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalFloat(OnId, 0f);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                _cpu = null;
                Shader.SetGlobalFloat(OnId, 0f);
            }

            if (_map != null)
                Destroy(_map);
        }

        private void LateUpdate()
        {
            if (Time.unscaledTime < _nextCheck)
                return;

            _nextCheck = Time.unscaledTime + 0.5f;
            Camera cam = GeoWorldCamera.Current;

            if (cam == null)
                return;

            RuinField.Prune();
            CraterField craters = CraterField.Instance;
            var at = new Vector2(cam.transform.position.x, cam.transform.position.z);
            bool moved = _cpu == null || (at - _centre).sqrMagnitude > Recentre * Recentre;

            if (!moved && craters == _craters && RuinField.Version == _ruinVersion)
                return;

            Build(at, craters);
        }

        private void Build(Vector2 centre, CraterField craters)
        {
            _craters = craters;
            _ruinVersion = RuinField.Version;
            _centre = centre;
            var origin = new Vector2(Mathf.Floor((centre.x - Window * 0.5f) / Texel) * Texel, Mathf.Floor((centre.y - Window * 0.5f) / Texel) * Texel);
            bool war = (craters != null && craters.craters.Length > 0) || RuinField.Count > 0;

            if (!war)
            {
                if (_cpu != null) Version++;
                _cpu = null;
                Shader.SetGlobalFloat(OnId, 0f);
                return;
            }

            var value = new float[Texels * Texels];

            // воронки: счёт по текселям поля шире окна на радиус круга (у края окна — и воронки за ним), потом сумма по
            // квадрату ~120 м (суммы по таблице) — воронок на гектар
            if (craters != null && craters.craters.Length > 0)
            {
                const int r = CraterRadiusTexels;
                int wide = Texels + 2 * r, n1 = wide + 1;
                var count = new int[n1 * n1];

                foreach (CraterField.Crater c in craters.craters)
                {
                    int ix = Mathf.FloorToInt((c.x - origin.x) / Texel) + r, iz = Mathf.FloorToInt((c.z - origin.y) / Texel) + r;

                    if (ix >= 0 && iz >= 0 && ix < wide && iz < wide)
                        count[(iz + 1) * n1 + ix + 1]++;
                }

                for (int z = 1; z < n1; z++)
                for (int x = 1; x < n1; x++)
                    count[z * n1 + x] += count[(z - 1) * n1 + x] + count[z * n1 + x - 1] - count[(z - 1) * n1 + x - 1];

                float hectares = (2 * r + 1) * (2 * r + 1) * Texel * Texel / 10000f;

                for (int z = 0; z < Texels; z++)
                for (int x = 0; x < Texels; x++)
                {
                    // тексель (x, z) окна — (x + r, z + r) поля; его круг — поле [x, x + 2r]
                    int x0 = x, x1 = x + 2 * r + 1, z0 = z, z1 = z + 2 * r + 1;
                    int sum = count[z1 * n1 + x1] - count[z0 * n1 + x1] - count[z1 * n1 + x0] + count[z0 * n1 + x0];
                    float perHectare = sum / hectares;
                    float t = Mathf.Clamp01((perHectare - 1f) / 14f);
                    value[z * Texels + x] = t * t * (3 - 2 * t);
                }
            }

            // руины: у стены по состоянию, к краю охвата (35 м плюс половина высоты) — нуль
            foreach (RuinField.Ruin ruin in RuinField.All)
            {
                float peak = ruin.state >= GeoWorld.GeoConditionState.Destroyed ? 0.95f : ruin.state >= GeoWorld.GeoConditionState.HeavilyDamaged ? 0.7f : 0.35f;
                float reach = 35f + 0.5f * ruin.height;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((ruin.bounds.xMin - reach - origin.x) / Texel));
                int x1 = Mathf.Min(Texels - 1, Mathf.FloorToInt((ruin.bounds.xMax + reach - origin.x) / Texel));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((ruin.bounds.yMin - reach - origin.y) / Texel));
                int z1 = Mathf.Min(Texels - 1, Mathf.FloorToInt((ruin.bounds.yMax + reach - origin.y) / Texel));

                for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(origin.x + (x + 0.5f) * Texel, origin.y + (z + 0.5f) * Texel);
                    float d = Mathf.Max(0f, Vector2.Distance(p, ruin.centre) - ruin.radius);
                    float v = peak * Mathf.Clamp01(1f - d / reach);
                    int i = z * Texels + x;

                    if (v > value[i])
                        value[i] = v;
                }
            }

            var bytes = new byte[Texels * Texels];

            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(value[i] * 255f), 0, 255);

            _map.SetPixelData(bytes, 0);
            _map.Apply(false, false);
            _cpu = new Snapshot { bytes = bytes, origin = origin };
            Version++;
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalVector(RectId, new Vector4(origin.x, origin.y, 1f / Window, Texels));
            Shader.SetGlobalFloat(OnId, 1f);
        }
    }
}
