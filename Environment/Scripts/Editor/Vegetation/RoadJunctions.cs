using System.Collections.Generic;
using _Game.Scripts.World;
using UnityEngine;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>
    /// Стыки дорог (RoadAreas) по осям OSM, до подтяжки к снимку. Части мира перекрываются (~110 м), и одна линия OSM
    /// приходит из обеих кусками, обрезанными по их границам: куски одной линии сшиваются в одну дорогу (иначе в полосе
    /// перекрытия лежали две оси, и каждая подтягивалась к снимку по-своему). Каждый конец дороги получает вид
    /// (RoadAreas.End): продолжение — здесь же кончается другая дорога; примыкание — конец лежит на полотне другой дороги
    /// (общая точка OSM — на её оси); тупик — ни того ни другого. Тупик, который смотрит на дорогу в нескольких метрах
    /// впереди (линии OSM не сведены), дотягивается до её оси и становится примыканием. Для подтяжки (RoadSatelliteSnap)
    /// остаётся, к какой дороге и где примкнул конец и какие концы сходятся в одной точке, — чтобы стыки после сдвигов не
    /// разъезжались (концы боковых дорог оставались на старой оси главной и торчали из неё круглыми петлями).
    /// </summary>
    public static class RoadJunctions
    {
        // концы разных дорог в одной точке (общая точка OSM)
        private const float JoinTolerance = 0.5f;
        // конец на полотне другой дороги: до её оси не дальше полуширины и этого
        private const float AttachMargin = 0.25f;
        // тупик дотягивается до дороги, если до её кромки не дальше этого и она впереди по ходу (угол не больше)
        private const float ExtendReach = 4f;
        private const float ExtendAngle = 50f;
        // куски одной линии: конец одного лежит на другом
        private const float StitchTolerance = 0.1f;
        private const float CellSize = 32f;

        public sealed class Link
        {
            public RoadAreas.End Type = RoadAreas.End.Free;
            // дорога, на полотне которой конец (примыкание; у продолжения — сквозная дорога через ту же точку), и метры
            // вдоль неё до этой точки
            public int Other = -1;
            public float OtherAlong;
            // продолжение: номер общей точки концов
            public int Node = -1;
        }

        public sealed class Links
        {
            public Link[] Start;
            public Link[] End;
        }

        // Сшивает куски одной линии OSM (ids — параллельно roads). Возвращает, сколько кусков ушло.
        public static int Stitch(List<RoadAreas.Road> roads, List<string> ids)
        {
            var groups = new Dictionary<string, List<int>>();

            for (int i = 0; i < roads.Count; i++)
            {
                if (string.IsNullOrEmpty(ids[i]))
                    continue;

                if (!groups.TryGetValue(ids[i], out List<int> group))
                    groups[ids[i]] = group = new List<int>();

                group.Add(i);
            }

            bool[] removed = new bool[roads.Count];
            int merged = 0;

            foreach (List<int> group in groups.Values)
            {
                bool changed = group.Count > 1;

                while (changed)
                {
                    changed = false;

                    foreach (int i in group)
                    {
                        foreach (int j in group)
                        {
                            if (i == j || removed[i] || removed[j] || !TryStitch(roads[i].points, roads[j].points, out Vector2[] points))
                                continue;

                            roads[i].points = points;
                            RecomputeBounds(roads[i]);
                            removed[j] = true;
                            merged++;
                            changed = true;
                            break;
                        }

                        if (changed)
                            break;
                    }
                }
            }

            for (int i = roads.Count - 1; i >= 0; i--)
            {
                if (!removed[i])
                    continue;

                roads.RemoveAt(i);
                ids.RemoveAt(i);
            }

            return merged;
        }

        // p и q — куски одной линии (направление у них общее): q лежит в p целиком — p; конец p лежит на q, а начало q — на
        // p — p и продолжение q за концом p.
        private static bool TryStitch(Vector2[] p, Vector2[] q, out Vector2[] result)
        {
            result = null;
            bool qStartOnP = Nearest(p, q[0], out _, out _) < StitchTolerance;
            bool qEndOnP = Nearest(p, q[q.Length - 1], out _, out _) < StitchTolerance;

            if (qStartOnP && qEndOnP)
            {
                result = p;
                return true;
            }

            if (!qStartOnP || Nearest(q, p[p.Length - 1], out int segment, out _) >= StitchTolerance)
                return false;

            var points = new List<Vector2>(p);

            for (int k = segment + 1; k < q.Length; k++)
            {
                if (Vector2.Distance(q[k], points[points.Count - 1]) > 0.05f)
                    points.Add(q[k]);
            }

            result = points.ToArray();
            return true;
        }

        // Виды концов; тупики, смотрящие на дорогу рядом, дотягиваются до её оси (точка добавляется в roads).
        public static Links Detect(List<RoadAreas.Road> roads, out string summary)
        {
            var grid = new Dictionary<long, List<int>>();

            for (int r = 0; r < roads.Count; r++)
            {
                Rect b = roads[r].bounds;
                float grow = ExtendReach + 1f;

                for (int x = Cell(b.xMin - grow); x <= Cell(b.xMax + grow); x++)
                {
                    for (int z = Cell(b.yMin - grow); z <= Cell(b.yMax + grow); z++)
                    {
                        long key = Key(x, z);

                        if (!grid.TryGetValue(key, out List<int> list))
                            grid[key] = list = new List<int>();

                        list.Add(r);
                    }
                }
            }

            var links = new Links { Start = new Link[roads.Count], End = new Link[roads.Count] };
            // концы, сходящиеся в одной точке: union-find по номеру конца (2 × дорога + 0 начало / 1 конец)
            int[] parent = new int[roads.Count * 2];

            for (int i = 0; i < parent.Length; i++)
                parent[i] = i;

            for (int r = 0; r < roads.Count; r++)
            {
                links.Start[r] = new Link();
                links.End[r] = new Link();
            }

            // дотянуть тупики — после разбора всех концов: метры вдоль считаются по осям OSM
            var extensions = new List<(int road, int end, Vector2 target)>();

            for (int r = 0; r < roads.Count; r++)
            {
                for (int e = 0; e < 2; e++)
                {
                    Vector2[] points = roads[r].points;
                    Vector2 p = e == 0 ? points[0] : points[points.Length - 1];
                    Link link = e == 0 ? links.Start[r] : links.End[r];
                    bool joined = false;
                    float bestInterior = float.PositiveInfinity;

                    if (!grid.TryGetValue(Key(Cell(p.x), Cell(p.y)), out List<int> near))
                        continue;

                    foreach (int o in near)
                    {
                        if (o == r)
                            continue;

                        Vector2[] other = roads[o].points;

                        for (int oe = 0; oe < 2; oe++)
                        {
                            if (Vector2.Distance(oe == 0 ? other[0] : other[other.Length - 1], p) < JoinTolerance)
                            {
                                Union(parent, r * 2 + e, o * 2 + oe);
                                joined = true;
                            }
                        }

                        // на полотне o (не у его концов — там продолжение)
                        float distance = Nearest(other, p, out int segment, out float along);

                        if (distance > roads[o].width * 0.5f + AttachMargin || Vector2.Distance(other[0], p) < JoinTolerance ||
                            Vector2.Distance(other[other.Length - 1], p) < JoinTolerance)
                            continue;

                        if (distance < bestInterior)
                        {
                            bestInterior = distance;
                            link.Other = o;
                            link.OtherAlong = along;
                        }
                    }

                    if (joined)
                        link.Type = RoadAreas.End.Joined;
                    else if (link.Other >= 0)
                        link.Type = RoadAreas.End.Attached;
                    else if (Ahead(roads, r, e, near, link, out Vector2 target))
                    {
                        link.Type = RoadAreas.End.Attached;
                        extensions.Add((r, e, target));
                    }
                }
            }

            // у дороги, дотянутой в начале, метры вдоль сдвигаются на добавленный отрезок
            float[] shift = new float[roads.Count];

            foreach ((int r, int e, Vector2 target) in extensions)
            {
                var extended = new List<Vector2>(roads[r].points);

                if (e == 0)
                {
                    shift[r] = Vector2.Distance(target, extended[0]);
                    extended.Insert(0, target);
                }
                else
                    extended.Add(target);

                roads[r].points = extended.ToArray();
                RecomputeBounds(roads[r]);
            }

            for (int r = 0; r < roads.Count; r++)
            {
                foreach (Link link in new[] { links.Start[r], links.End[r] })
                {
                    if (link.Other >= 0)
                        link.OtherAlong += shift[link.Other];
                }
            }

            int free = 0, joinedEnds = 0, attached = 0, node = 0;
            var nodes = new Dictionary<int, int>();

            for (int r = 0; r < roads.Count; r++)
            {
                for (int e = 0; e < 2; e++)
                {
                    Link link = e == 0 ? links.Start[r] : links.End[r];

                    if (link.Type == RoadAreas.End.Joined)
                    {
                        int root = Find(parent, r * 2 + e);

                        if (!nodes.TryGetValue(root, out int id))
                            nodes[root] = id = node++;

                        link.Node = id;
                        joinedEnds++;
                    }
                    else if (link.Type == RoadAreas.End.Attached)
                        attached++;
                    else
                        free++;

                    if (e == 0)
                        roads[r].start = link.Type;
                    else
                        roads[r].end = link.Type;
                }
            }

            summary = $"ends: {joinedEnds} joined at {node} points, {attached} attached ({extensions.Count} extended to the road ahead), {free} free";
            return links;
        }

        // Дорога впереди тупика: до её кромки не дальше ExtendReach, направление на ближайшую точку оси (target) — не дальше
        // ExtendAngle от хода дороги.
        private static bool Ahead(List<RoadAreas.Road> roads, int r, int e, List<int> near, Link link, out Vector2 target)
        {
            Vector2[] points = roads[r].points;
            Vector2 p = e == 0 ? points[0] : points[points.Length - 1];
            Vector2 inner = e == 0 ? points[1] : points[points.Length - 2];
            Vector2 heading = (p - inner).normalized;
            float best = float.PositiveInfinity;
            target = p;

            foreach (int o in near)
            {
                if (o == r)
                    continue;

                Vector2[] other = roads[o].points;
                float distance = Nearest(other, p, out int segment, out float along);

                if (distance - roads[o].width * 0.5f > ExtendReach || distance >= best || distance < 0.05f)
                    continue;

                Vector2 a = other[segment];
                Vector2 ab = other[segment + 1] - a;
                Vector2 closest = a + ab * Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));

                if (Vector2.Angle(heading, closest - p) > ExtendAngle)
                    continue;

                best = distance;
                target = closest;
                link.Other = o;
                link.OtherAlong = along;
            }

            return link.Other >= 0;
        }

        // Расстояние от точки до ломаной; segment — номер отрезка, along — метры вдоль ломаной до ближайшей точки.
        internal static float Nearest(Vector2[] points, Vector2 p, out int segment, out float along)
        {
            float best = float.PositiveInfinity;
            float start = 0f;
            segment = 0;
            along = 0f;

            for (int i = 1; i < points.Length; i++)
            {
                Vector2 a = points[i - 1];
                Vector2 ab = points[i] - a;
                float length = ab.magnitude;
                float t = length > 1e-4f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / (length * length)) : 0f;
                float distance = Vector2.Distance(p, a + ab * t);

                if (distance < best)
                {
                    best = distance;
                    segment = i - 1;
                    along = start + t * length;
                }

                start += length;
            }

            return best;
        }

        internal static void RecomputeBounds(RoadAreas.Road road)
        {
            float half = road.width * 0.5f;
            Vector2 min = Vector2.positiveInfinity, max = Vector2.negativeInfinity;

            foreach (Vector2 point in road.points)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            road.bounds = Rect.MinMaxRect(min.x - half, min.y - half, max.x + half, max.y + half);
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];

            return i;
        }

        private static void Union(int[] parent, int a, int b) => parent[Find(parent, a)] = Find(parent, b);

        private static int Cell(float value) => Mathf.FloorToInt(value / CellSize);

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
