using System;
using System.Collections.Generic;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Дороги, грунтовки и тропинки (OSM highway, railway) из выгрузок GeoWorld — оси с шириной. Карта земель пакета
    /// (GeoLandCover) знает их только клетками ~10 м: грунтовка в 3 м там то пропадает, то становится пятном в 10 м, и трава
    /// росла прямо на колее. Пакет оси дорог в тайлы не переносит (земляные дороги тайла — общие меши и снимок), поэтому их
    /// запекает RoadAreasBaker (Tools/Environment/Bake Road Areas, WorldMapBaker) в папку мира — ассет висит на его WorldMapData.
    /// По ним трава карты
    /// (GroundDressing) и точки взлёта не растёт на полотне дороги и редеет к его краю; там, где выгрузки дорог есть
    /// (Covers), клетки дорог карты земель траву больше не вырезают.
    /// </summary>
    public sealed class RoadAreas : ScriptableObject
    {
        // от края полотна: ещё столько голо (карточка травы шириной ~1 м из соседней клетки ложится на колею), дальше
        // столько — трава возвращается
        public const float ClearMargin = 0.5f;
        public const float Fade = 1f;
        private const float BucketSize = 250f;

        // Покрытие дороги — по материалу экстрактора (материал OSM surface или типичный для highway): как её рисует террейн
        // (GroundRoads.hlsl, RoadMap). Значения — в карте дорог, менять их нельзя.
        public enum Kind : byte
        {
            Dirt = 0,
            Asphalt = 1,
            Paving = 2,
            Gravel = 3,
            Path = 4,
            Rail = 5
        }

        // Конец дороги (RoadJunctions редактора; значения — в карте дорог): круглый — полотно огибает конец (как до
        // разметки концов); тупик — обрезано поперёк и сходит на нет; продолжение — другая дорога начинается здесь же
        // (круглый, стык незаметен); примыкание — упирается в другую дорогу: у конца уступает ей полотно.
        public enum End : byte
        {
            Round = 0,
            Free = 1,
            Joined = 2,
            Attached = 3
        }

        [Serializable]
        public sealed class Road
        {
            public float width;
            // рамка полотна (оси с половиной ширины)
            public Rect bounds;
            public Vector2[] points = Array.Empty<Vector2>();
            public Kind kind;
            // разметка: магистрали и дороги областного и районного значения (в Украине размечены), улицы — нет
            public bool marked;
            // износ 0..1 (экстрактор: по классу дороги и её тегам)
            public float wear;
            // асфальт в городе (по карте земель вокруг — застройка): ровная кромка с бетонным бордюром вместо обочины
            public bool curb;
            // концы: у первой точки и у последней
            public End start;
            public End end;
        }

        // Отрезок оси с половиной ширины полотна: для расчёта в фоновом потоке, без объектов Unity. AlongA, AlongB —
        // сколько метров от начала дороги до концов отрезка (рисунок вдоль дороги: колеи, разметка), Length — длина всей
        // дороги (где её концы).
        public struct Segment
        {
            public Vector2 A;
            public Vector2 B;
            public float HalfWidth;
            public float AlongA;
            public float AlongB;
            public Kind Kind;
            public bool Marked;
            public float Wear;
            public bool Curb;
            // номер дороги в roads: где полотна разных дорог накрывают друг друга — перекрёсток (RoadMap)
            public int Road;
            public float Length;
            public End Start;
            public End End;
        }

        public Road[] roads = Array.Empty<Road>();
        // границы выгрузок, из которых взяты дороги: внутри них дороги известны осями
        public Rect[] coverage = Array.Empty<Rect>();

        [NonSerialized] private Dictionary<long, List<int>> _buckets;

        /// <summary>Дороги загруженного мира (WorldMapData); null — у него их нет.</summary>
        public static RoadAreas Instance => WorldMapData.Roads;

        // Запекание переписало дороги: корзины поиска — заново.
        public void ResetIndex() => _buckets = null;

        // Известны ли дороги в точке осями (выгрузка её покрывает).
        public bool Covers(float x, float z)
        {
            foreach (Rect rect in coverage)
            {
                if (rect.Contains(new Vector2(x, z)))
                    return true;
            }

            return false;
        }

        // Отрезки дорог, полотно которых с краем (ClearMargin + Fade) задевает область; на главном потоке.
        public Segment[] SegmentsIn(Rect area)
        {
            _buckets ??= BuildBuckets();
            float reach = ClearMargin + Fade;
            HashSet<int> seen = new();
            List<Segment> result = new();

            for (int bx = Mathf.FloorToInt((area.xMin - reach) / BucketSize); bx <= Mathf.FloorToInt((area.xMax + reach) / BucketSize); bx++)
            {
                for (int bz = Mathf.FloorToInt((area.yMin - reach) / BucketSize); bz <= Mathf.FloorToInt((area.yMax + reach) / BucketSize); bz++)
                {
                    if (!_buckets.TryGetValue(Key(bx, bz), out List<int> list))
                        continue;

                    foreach (int index in list)
                    {
                        if (!seen.Add(index))
                            continue;

                        Road road = roads[index];
                        float half = road.width * 0.5f;
                        Rect grown = Rect.MinMaxRect(area.xMin - half - reach, area.yMin - half - reach, area.xMax + half + reach, area.yMax + half + reach);

                        if (!road.bounds.Overlaps(grown, true))
                            continue;

                        float along = 0f;
                        float total = 0f;

                        for (int i = 1; i < road.points.Length; i++)
                            total += Vector2.Distance(road.points[i - 1], road.points[i]);

                        for (int i = 1; i < road.points.Length; i++)
                        {
                            Vector2 a = road.points[i - 1];
                            Vector2 b = road.points[i];
                            float length = Vector2.Distance(a, b);
                            along += length;

                            if (Mathf.Max(a.x, b.x) < grown.xMin || Mathf.Min(a.x, b.x) > grown.xMax ||
                                Mathf.Max(a.y, b.y) < grown.yMin || Mathf.Min(a.y, b.y) > grown.yMax)
                                continue;

                            result.Add(new Segment
                            {
                                A = a, B = b, HalfWidth = half, AlongA = along - length, AlongB = along,
                                Kind = road.kind, Marked = road.marked, Wear = road.wear, Curb = road.curb, Road = index,
                                Length = total, Start = road.start, End = road.end
                            });
                        }
                    }
                }
            }

            return result.ToArray();
        }

        // Сколько от дороги в точке: 1 на полотне и ClearMargin за его краем, к 0 за Fade.
        public float CoverAt(float x, float z)
        {
            Segment[] segments = SegmentsIn(new Rect(x, z, 0f, 0f));
            return Cover(segments, new Vector2(x, z));
        }

        public static float Cover(Segment[] segments, Vector2 point)
        {
            float cover = 0f;

            foreach (Segment segment in segments)
                cover = Mathf.Max(cover, Cover(segment, Distance(segment, point)));

            return cover;
        }

        // Сколько от точки до края ближайшего полотна, м (на полотне — меньше нуля), если он ближе reach; иначе reach.
        public float EdgeDistanceAt(float x, float z, float reach) =>
            Mathf.Min(reach, EdgeDistance(SegmentsIn(new Rect(x - reach, z - reach, reach * 2f, reach * 2f)), new Vector2(x, z)));

        // То же по готовым отрезкам; дорог нет — float.MaxValue. Своя математика: годится для фонового потока.
        public static float EdgeDistance(Segment[] segments, Vector2 point)
        {
            float distance = float.MaxValue;

            if (segments == null)
                return distance;

            foreach (Segment segment in segments)
                distance = Mathf.Min(distance, EdgeDistance(segment, point));

            return distance;
        }

        public static float EdgeDistance(Segment segment, Vector2 point) => Distance(segment, point) - segment.HalfWidth;

        // Доля дороги в каждой из n × n ячеек области (по строкам z) — максимум с тем, что уже в target. Только своя
        // математика: годится для фонового потока.
        public static void Rasterize(Segment[] segments, Vector2 origin, Vector2 size, int n, float[] target)
        {
            if (segments == null)
                return;

            float cellX = size.x / n;
            float cellZ = size.y / n;

            foreach (Segment segment in segments)
            {
                float reach = segment.HalfWidth + ClearMargin + Fade;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(segment.A.x, segment.B.x) - reach - origin.x) / cellX));
                int x1 = Mathf.Min(n - 1, Mathf.FloorToInt((Mathf.Max(segment.A.x, segment.B.x) + reach - origin.x) / cellX));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(segment.A.y, segment.B.y) - reach - origin.y) / cellZ));
                int z1 = Mathf.Min(n - 1, Mathf.FloorToInt((Mathf.Max(segment.A.y, segment.B.y) + reach - origin.y) / cellZ));

                for (int z = z0; z <= z1; z++)
                {
                    float worldZ = origin.y + (z + 0.5f) * cellZ;

                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 point = new(origin.x + (x + 0.5f) * cellX, worldZ);
                        float cover = Cover(segment, Distance(segment, point));
                        int cell = z * n + x;

                        if (cover > target[cell])
                            target[cell] = cover;
                    }
                }
            }
        }

        private static float Cover(Segment segment, float distance)
        {
            float t = Mathf.Clamp01((distance - segment.HalfWidth - ClearMargin) / Fade);
            return 1f - t * t * (3f - 2f * t);
        }

        private static float Distance(Segment segment, Vector2 point)
        {
            Vector2 ab = segment.B - segment.A;
            float length = ab.sqrMagnitude;
            float t = length > 1e-6f ? Mathf.Clamp01(Vector2.Dot(point - segment.A, ab) / length) : 0f;
            return Vector2.Distance(point, segment.A + ab * t);
        }

        private Dictionary<long, List<int>> BuildBuckets()
        {
            Dictionary<long, List<int>> buckets = new();

            for (int i = 0; i < roads.Length; i++)
            {
                Rect b = roads[i].bounds;

                for (int bx = Mathf.FloorToInt(b.xMin / BucketSize); bx <= Mathf.FloorToInt(b.xMax / BucketSize); bx++)
                {
                    for (int bz = Mathf.FloorToInt(b.yMin / BucketSize); bz <= Mathf.FloorToInt(b.yMax / BucketSize); bz++)
                    {
                        long key = Key(bx, bz);

                        if (!buckets.TryGetValue(key, out List<int> list))
                            buckets[key] = list = new List<int>();

                        list.Add(i);
                    }
                }
            }

            return buckets;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
