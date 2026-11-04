using System;
using System.Collections.Generic;
using GeoWorld.Editor;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Воронки мира (война): где каждая, её яма (диаметр, глубина), износ (0 — свежая, 1 — оплывшая и заросшая), вода в ней и
    /// откуда она (GeoCraterSource: найдена на снимке, рассыпана там, где шли бои, дана выгрузкой). Тот же список, что
    /// генератор врезал в землю тайлов (GeoCombatGround, combat.json рядом с выгрузкой): слой окружения по нему рисует воронки
    /// ближе и мельче сетки высот (CraterMap, GroundCraters.hlsl), наливает в них воду и держит траву и сухостой в их чашах.
    /// У мира из редактора — ассет в его папке (CratersBaker), у пакета мира — из его extra «world map» (WorldMapPackage);
    /// находится через WorldMapData.Craters.
    /// </summary>
    public sealed class CraterField : ScriptableObject
    {
        [Serializable]
        public struct Crater
        {
            public float x, z, diameter, depth, age;
            public byte source;
            public bool water;
        }

        [Serializable]
        public struct Trench
        {
            // осевая линия (x, z парами), ширина и глубина как вырыт, износ, вид (GeoTrenchKind)
            public float[] points;
            public float width, depth, age;
            public byte kind;
        }

        private const float CellSize = 32;
        // клетка плотности воронок (PerHectareAt): 50 м, плотность — по 3 × 3 клеткам (2.25 га)
        private const float DensityCell = 50;

        /// <summary>Воронок на гектар, с которых поле считается брошенным (там шли бои): без рядков и стерни. С одной на
        /// гектар фон рассыпки (по умолчанию 0.5 на гектар, по 2.25 га) сам по себе бросал ~10 % клеток мирной земли.</summary>
        public const float AbandonedPerHectare = 3f;

        public Crater[] craters = Array.Empty<Crater>();
        public Trench[] trenches = Array.Empty<Trench>();
        [HideInInspector] public string report;

        [NonSerialized] private Dictionary<long, List<int>> _cells;
        [NonSerialized] private Dictionary<long, List<int>> _trenchCells;
        [NonSerialized] private Dictionary<long, int> _density;

        public static CraterField Instance => WorldMapData.Craters;

        public static Trench From(TrenchFeature t) => new()
        {
            points = t.points ?? Array.Empty<float>(), width = t.width, depth = t.depth, age = Mathf.Clamp01(t.age), kind = (byte)Mathf.Clamp(t.kind, 0, 255)
        };

        /// <summary>Индексы окопов, чья зона (ров и брустверы) задевает прямоугольник.</summary>
        public void QueryTrenches(Rect area, List<int> result)
        {
            result.Clear();

            for (int i = 0; i < trenches.Length; i++)
            {
                float[] p = trenches[i].points;

                if (p == null || p.Length < 4)
                    continue;

                float reach = GeoTrenchProfile.Reach(trenches[i].width, trenches[i].age);
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

                for (int k = 0; k + 1 < p.Length; k += 2)
                {
                    minX = Mathf.Min(minX, p[k]);
                    maxX = Mathf.Max(maxX, p[k]);
                    minZ = Mathf.Min(minZ, p[k + 1]);
                    maxZ = Mathf.Max(maxZ, p[k + 1]);
                }

                if (maxX + reach >= area.xMin && minX - reach <= area.xMax && maxZ + reach >= area.yMin && minZ - reach <= area.yMax)
                    result.Add(i);
            }
        }

        public static Crater From(CraterFeature c) => new()
        {
            x = c.x, z = c.z, diameter = c.diameter, depth = c.depth, age = Mathf.Clamp01(c.age),
            source = (byte)Mathf.Clamp(c.source, 0, 255), water = c.water
        };

        public void ResetIndex()
        {
            _cells = null;
            _trenchCells = null;
            _density = null;
        }

        /// <summary>Сколько воронок на гектар вокруг точки (в квадрате 150 м): где шли бои и насколько тяжёлые.</summary>
        public float PerHectareAt(float x, float z)
        {
            if (_density == null)
            {
                _density = new Dictionary<long, int>();

                foreach (Crater c in craters)
                {
                    long key = Key(Mathf.FloorToInt(c.x / DensityCell), Mathf.FloorToInt(c.z / DensityCell));
                    _density[key] = _density.TryGetValue(key, out int count) ? count + 1 : 1;
                }
            }

            int cx = Mathf.FloorToInt(x / DensityCell), cz = Mathf.FloorToInt(z / DensityCell), sum = 0;

            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (_density.TryGetValue(Key(cx + dx, cz + dz), out int count))
                    sum += count;
            }

            return sum / (9f * DensityCell * DensityCell / 10000f);
        }

        /// <summary>Брошено ли поле в точке: вокруг воронок не меньше AbandonedPerHectare.</summary>
        public static bool AbandonedAt(float x, float z)
        {
            CraterField field = Instance;
            return field != null && field.craters.Length > 0 && field.PerHectareAt(x, z) >= AbandonedPerHectare;
        }

        /// <summary>Индексы воронок, чья зона (яма, вал и выброс) задевает прямоугольник.</summary>
        public void Query(Rect area, List<int> result)
        {
            result.Clear();
            Index();
            int x0 = Mathf.FloorToInt(area.xMin / CellSize) - 1, x1 = Mathf.FloorToInt(area.xMax / CellSize) + 1;
            int z0 = Mathf.FloorToInt(area.yMin / CellSize) - 1, z1 = Mathf.FloorToInt(area.yMax / CellSize) + 1;

            for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!_cells.TryGetValue(Key(cx, cz), out List<int> list))
                    continue;

                foreach (int i in list)
                {
                    float reach = craters[i].diameter * 0.5f * GeoCraterProfile.Reach;

                    if (craters[i].x + reach >= area.xMin && craters[i].x - reach <= area.xMax && craters[i].z + reach >= area.yMin && craters[i].z - reach <= area.yMax)
                        result.Add(i);
                }
            }
        }

        /// <summary>
        /// Воронка или окоп в точке: насколько глубоко точка в чаше (во рву) самой глубокой из накрывающих (0 — вне чаш, 1 — на
        /// дне или на оси рва; как канал чаши карты воронок, CraterMap.shader) и в чаше самой глубокой из воронок с водой (во
        /// рву воды нет: в нём стоит только дождевая) — карта держит их порознь, так и здесь. Для брызг винтов над водой
        /// воронок и окопов.
        /// </summary>
        public bool BowlAt(float x, float z, out float bowl, out float waterBowl)
        {
            bowl = 0;
            waterBowl = 0;
            Index();
            long key = Key(Mathf.FloorToInt(x / CellSize), Mathf.FloorToInt(z / CellSize));

            if (_cells.TryGetValue(key, out List<int> list))
            {
                foreach (int i in list)
                {
                    Crater c = craters[i];
                    float radius = c.diameter * 0.5f;
                    float dx = x - c.x, dz = z - c.z;
                    float s2 = (dx * dx + dz * dz) / (radius * radius);

                    if (s2 >= 1)
                        continue;

                    bowl = Mathf.Max(bowl, 1 - s2);

                    if (c.water)
                        waterBowl = Mathf.Max(waterBowl, 1 - s2);
                }
            }

            if (_trenchCells.TryGetValue(key, out List<int> lines))
            {
                foreach (int i in lines)
                {
                    Trench t = trenches[i];
                    float half = GeoTrenchProfile.HalfWidth(t.width, t.age);
                    float u = DistanceToLine(t.points, x, z) / half;

                    if (u < 1)
                        bowl = Mathf.Max(bowl, 1 - u * u);
                }
            }

            return bowl > 0;
        }

        private static float DistanceToLine(float[] p, float x, float z)
        {
            float best = float.MaxValue;
            Vector2 point = new(x, z);

            for (int k = 0; k + 3 < p.Length; k += 2)
            {
                Vector2 a = new(p[k], p[k + 1]), ab = new Vector2(p[k + 2], p[k + 3]) - a;
                float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, Vector2.Distance(point, a + ab * t));
            }

            return best;
        }

        /// <summary>Есть ли воронка с водой в радиусе от точки.</summary>
        public bool WaterWithin(float x, float z, float radius)
        {
            Index();
            int x0 = Mathf.FloorToInt((x - radius) / CellSize), x1 = Mathf.FloorToInt((x + radius) / CellSize);
            int z0 = Mathf.FloorToInt((z - radius) / CellSize), z1 = Mathf.FloorToInt((z + radius) / CellSize);

            for (int cz = z0; cz <= z1; cz++)
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!_cells.TryGetValue(Key(cx, cz), out List<int> list))
                    continue;

                foreach (int i in list)
                {
                    Crater c = craters[i];
                    float reach = radius + c.diameter * 0.5f;

                    if (c.water && (c.x - x) * (c.x - x) + (c.z - z) * (c.z - z) <= reach * reach)
                        return true;
                }
            }

            return false;
        }

        private void Index()
        {
            if (_cells != null)
                return;

            _cells = new Dictionary<long, List<int>>();
            _trenchCells = new Dictionary<long, List<int>>();
            HashSet<long> added = new();

            // ров окопа — по клеткам, которые задевает каждый его отрезок с полушириной рва
            for (int i = 0; i < trenches.Length; i++)
            {
                float[] p = trenches[i].points;

                if (p == null || p.Length < 4)
                    continue;

                float half = GeoTrenchProfile.HalfWidth(trenches[i].width, trenches[i].age);
                added.Clear();

                for (int k = 0; k + 3 < p.Length; k += 2)
                {
                    int x0 = Mathf.FloorToInt((Mathf.Min(p[k], p[k + 2]) - half) / CellSize), x1 = Mathf.FloorToInt((Mathf.Max(p[k], p[k + 2]) + half) / CellSize);
                    int z0 = Mathf.FloorToInt((Mathf.Min(p[k + 1], p[k + 3]) - half) / CellSize), z1 = Mathf.FloorToInt((Mathf.Max(p[k + 1], p[k + 3]) + half) / CellSize);

                    for (int cz = z0; cz <= z1; cz++)
                    for (int cx = x0; cx <= x1; cx++)
                    {
                        long key = Key(cx, cz);

                        if (!added.Add(key))
                            continue;

                        if (!_trenchCells.TryGetValue(key, out List<int> list))
                            _trenchCells[key] = list = new List<int>();

                        list.Add(i);
                    }
                }
            }

            for (int i = 0; i < craters.Length; i++)
            {
                float reach = craters[i].diameter * 0.5f * GeoCraterProfile.Reach;
                int x0 = Mathf.FloorToInt((craters[i].x - reach) / CellSize), x1 = Mathf.FloorToInt((craters[i].x + reach) / CellSize);
                int z0 = Mathf.FloorToInt((craters[i].z - reach) / CellSize), z1 = Mathf.FloorToInt((craters[i].z + reach) / CellSize);

                for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    long key = Key(cx, cz);

                    if (!_cells.TryGetValue(key, out List<int> list))
                        _cells[key] = list = new List<int>();

                    list.Add(i);
                }
            }
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
