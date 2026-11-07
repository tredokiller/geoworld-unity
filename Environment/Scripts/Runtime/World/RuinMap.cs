using System.Collections.Generic;
using System.Threading.Tasks;
using GeoWorld;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Земля вокруг руин (война): окно 1024 м вокруг камеры, 1 м на тексель (RGBA8, глобальная _RuinMap, читается
    /// сэмплером sampler_LinearClamp — своих сэмплеров у прохода террейна нет), по руинам загруженных тайлов (RuinField):
    /// R — пыль и крошка (у стены разрушенного здания 1, тяжело повреждённого 0.8, повреждённого 0.4, к краю охвата нуль;
    /// охват — по состоянию и высоте: разрушенное 6 м + 0.6 высоты, до 20 м), G — доля кирпича в крошке (дом и сарай —
    /// больше, панельная многоэтажка — меньше), B — обломки покрупнее у самой стены (у разрушенных, 4 м + 0.1 высоты),
    /// A — внутри контура. Рисует GroundRuins.hlsl (после дорог, до воронок): серо-бежевая пыль, кирпичная и бетонная
    /// крошка, тёмная россыпь обломков, рваный край по шуму; в дождь — серая грязь. Ни копоти, ни гари (правило: бой
    /// окончен, без огня). Пересчёт в фоновом потоке, когда камера ушла на 256 м или пришли другие руины.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RuinMap : MonoBehaviour
    {
        public const float Window = 1024f;
        public const int Texels = 1024;
        private const float Texel = Window / Texels;
        private const float Recentre = 256f;
        private const float MaxReach = 20f;

        private static readonly int MapId = Shader.PropertyToID("_RuinMap");
        private static readonly int RectId = Shader.PropertyToID("_RuinMapRect");

        private struct Stamp
        {
            public Vector2[] outline;
            public Vector2 centre;
            public Rect bounds;
            public float radius, dust, reach, rubble, rubbleReach, brick;
        }

        private Texture2D _map;
        private Task<byte[]> _job;
        private Vector2 _jobOrigin, _origin, _centre;
        private int _jobVersion = -1, _version = -1;
        private bool _has;
        private float _nextCheck;
        private readonly List<RuinField.Ruin> _query = new List<RuinField.Ruin>();

        private void Awake()
        {
            _map = new Texture2D(Texels, Texels, TextureFormat.RGBA32, false, true)
            {
                name = "Ruin map", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
            };
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalVector(RectId, Vector4.zero);
        }

        private void OnDestroy()
        {
            Shader.SetGlobalVector(RectId, Vector4.zero);

            if (_map != null)
                Destroy(_map);
        }

        private void LateUpdate()
        {
            if (_job != null && _job.IsCompleted)
                Finish();

            if (_job != null || Time.unscaledTime < _nextCheck)
                return;

            _nextCheck = Time.unscaledTime + 0.5f;
            Camera cam = GeoWorldCamera.Current;

            if (cam == null)
                return;

            RuinField.Prune();
            var eye = new Vector2(cam.transform.position.x, cam.transform.position.z);
            bool moved = !_has || (eye - _centre).sqrMagnitude > Recentre * Recentre;

            if (!moved && RuinField.Version == _version)
                return;

            Begin(eye);
        }

        private void Begin(Vector2 eye)
        {
            var origin = new Vector2(Mathf.Floor((eye.x - Window * 0.5f) / Texel) * Texel, Mathf.Floor((eye.y - Window * 0.5f) / Texel) * Texel);
            RuinField.Query(new Rect(origin, Vector2.one * Window), MaxReach, _query);
            _centre = eye;
            _jobVersion = RuinField.Version;

            if (_query.Count == 0)
            {
                _version = _jobVersion;
                _has = false;
                Shader.SetGlobalVector(RectId, Vector4.zero);
                return;
            }

            var stamps = new Stamp[_query.Count];

            for (int i = 0; i < stamps.Length; i++)
            {
                RuinField.Ruin r = _query[i];
                bool destroyed = r.state >= GeoWorld.GeoConditionState.Destroyed, heavy = r.state >= GeoWorld.GeoConditionState.HeavilyDamaged;
                stamps[i] = new Stamp
                {
                    outline = r.outline, centre = r.centre, bounds = r.bounds, radius = r.radius,
                    dust = destroyed ? 1f : heavy ? 0.8f : 0.4f,
                    reach = Mathf.Min(MaxReach, destroyed ? 6f + 0.6f * r.height : heavy ? 4f + 0.4f * r.height : 2f + 0.15f * r.height),
                    rubble = destroyed ? 1f : heavy ? 0.55f : 0.1f,
                    rubbleReach = 4f + 0.1f * r.height,
                    brick = r.family == 0 ? 0.7f : r.family == 2 ? 0.45f : 0.25f
                };
            }

            _jobOrigin = origin;
            _job = Task.Run(() => Compute(stamps, origin));
        }

        private void Finish()
        {
            byte[] pixels = null;

            try
            {
                pixels = _job.Result;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[RuinMap] build failed: " + e.Message);
            }

            _job = null;
            _version = _jobVersion;

            if (pixels == null)
                return;

            _map.SetPixelData(pixels, 0);
            _map.Apply(false, false);
            _origin = _jobOrigin;
            _has = true;
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalVector(RectId, new Vector4(_origin.x, _origin.y, 1f / Window, Texels));
        }

        // фоновый поток: каждая руина — по текселям своего охвата, наибольшее из всех
        private static byte[] Compute(Stamp[] stamps, Vector2 origin)
        {
            int n = Texels;
            var dust = new float[n * n];
            var brick = new float[n * n];
            var rubble = new float[n * n];
            var inside = new byte[n * n];

            foreach (Stamp s in stamps)
            {
                float far = s.reach;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((s.bounds.xMin - far - origin.x) / Texel));
                int x1 = Mathf.Min(n - 1, Mathf.FloorToInt((s.bounds.xMax + far - origin.x) / Texel));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((s.bounds.yMin - far - origin.y) / Texel));
                int z1 = Mathf.Min(n - 1, Mathf.FloorToInt((s.bounds.yMax + far - origin.y) / Texel));

                for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(origin.x + (x + 0.5f) * Texel, origin.y + (z + 0.5f) * Texel);
                    float d = Distance(s, p, out bool within);

                    if (d >= far)
                        continue;

                    int i = z * n + x;
                    float t = d / far;
                    float v = s.dust * (1f - t * t * (3f - 2f * t));
                    float rb = s.rubble * Mathf.Clamp01(1f - d / s.rubbleReach);

                    if (within)
                    {
                        v = s.dust;
                        rb = s.rubble;
                        inside[i] = 255;
                    }

                    if (v > dust[i])
                    {
                        // кирпич — того, чья пыль здесь гуще
                        brick[i] = s.brick;
                        dust[i] = v;
                    }

                    if (rb > rubble[i])
                        rubble[i] = rb;
                }
            }

            var pixels = new byte[n * n * 4];

            for (int i = 0; i < n * n; i++)
            {
                pixels[i * 4] = (byte)Mathf.Clamp(Mathf.RoundToInt(dust[i] * 255f), 0, 255);
                pixels[i * 4 + 1] = (byte)Mathf.Clamp(Mathf.RoundToInt(brick[i] * 255f), 0, 255);
                pixels[i * 4 + 2] = (byte)Mathf.Clamp(Mathf.RoundToInt(rubble[i] * 255f), 0, 255);
                pixels[i * 4 + 3] = inside[i];
            }

            return pixels;
        }

        // расстояние от точки до контура (0 внутри); без контура — до круга охвата
        private static float Distance(in Stamp s, Vector2 p, out bool within)
        {
            if (s.outline == null)
            {
                float d = Vector2.Distance(p, s.centre) - s.radius;
                within = d <= 0f;
                return Mathf.Max(0f, d);
            }

            bool inPolygon = false;
            float best = float.MaxValue;
            Vector2[] o = s.outline;

            for (int i = 0, j = o.Length - 1; i < o.Length; j = i++)
            {
                Vector2 a = o[j], b = o[i];

                if ((b.y > p.y) != (a.y > p.y) && p.x < (a.x - b.x) * (p.y - b.y) / (a.y - b.y) + b.x)
                    inPolygon = !inPolygon;

                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                float d = (p - (a + ab * t)).sqrMagnitude;

                if (d < best)
                    best = d;
            }

            within = inPolygon;
            return inPolygon ? 0f : Mathf.Sqrt(best);
        }
    }
}
