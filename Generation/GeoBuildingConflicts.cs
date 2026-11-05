using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Buildings of two exports that stand in each other where the exports meet. The extractor resolves overlaps within an
    // export (Extractor~/extractor/conflicts.py: OpenStreetMap over Microsoft's detections, the larger over the smaller,
    // a building half under another dropped, else cut along its wall), but each part of a world is extracted on its own
    // and a tile on their seam takes every building from the part that owns its centre: a house Microsoft detected in one
    // part and OpenStreetMap mapped in the other came twice, a few metres apart, and one part's outline was never cut
    // against the next part's building. The same pass runs here on the buildings a tile gathers from several parts.
    public static class GeoBuildingConflicts
    {
        // as the extractor has them
        public const float MinOverlap = 0.5f, DropShare = 0.5f, KeepShare = 0.5f, MinWidth = 1.5f;

        sealed class Entry
        {
            public BuildingFeature source;
            public Vector2[] ring;   // world X/Z, counter-clockwise
            public Rect box;
            public float area0;
            public bool osm, cut;
        }

        public struct Result
        {
            public List<BuildingFeature> buildings;
            public int dropped, cut;
        }

        // Microsoft's outlines have IDs "ms/<lat>,<lon>" (source "microsoft"); the rest are mapped.
        public static bool IsMapped(BuildingFeature b) =>
            b.source != null ? b.source != "microsoft" : b.id == null || !b.id.StartsWith("ms/", StringComparison.Ordinal);

        // The buildings with every overlap resolved; a cut building is a copy (the list a tile gets is shared with the
        // tiles around it), the others are the same objects. The winners first: each building is checked against the ones
        // already kept, which all outrank it.
        public static Result Resolve(IList<BuildingFeature> buildings)
        {
            var result = new Result { buildings = new List<BuildingFeature>(buildings.Count) };
            var entries = new List<Entry>(buildings.Count);
            foreach (var b in buildings)
            {
                if (b == null) continue;
                var ring = Ring(b);
                if (ring == null) { result.buildings.Add(b); continue; }
                entries.Add(new Entry { source = b, ring = ring, box = Box(ring), area0 = GeoPolygon.Area(ring), osm = IsMapped(b) });
            }
            entries.Sort((a, b) =>
            {
                if (a.osm != b.osm) return a.osm ? -1 : 1;
                int area = b.area0.CompareTo(a.area0);
                return area != 0 ? area : string.CompareOrdinal(b.source.id, a.source.id);
            });
            const float Cell = 50;
            var grid = new Dictionary<Vector2Int, List<Entry>>();
            var scratch = new Scratch();
            foreach (var e in entries)
            {
                var winners = new List<Entry>();
                var seen = new HashSet<Entry>();
                for (int x = Mathf.FloorToInt(e.box.xMin / Cell); x <= Mathf.FloorToInt(e.box.xMax / Cell); x++)
                    for (int z = Mathf.FloorToInt(e.box.yMin / Cell); z <= Mathf.FloorToInt(e.box.yMax / Cell); z++)
                        if (grid.TryGetValue(new Vector2Int(x, z), out var list))
                            foreach (var w in list) if (seen.Add(w) && w.box.Overlaps(e.box)) winners.Add(w);
                winners.Sort((a, b) => string.CompareOrdinal(a.source.id, b.source.id));
                bool dropped = false;
                foreach (var w in winners)
                {
                    // a winner with an L or U outline may take more than one cut
                    for (int k = 0; k < 4 && !dropped; k++)
                    {
                        float shared = Intersection(e.ring, w.ring, scratch);
                        if (shared < MinOverlap) break;
                        if (shared >= DropShare * GeoPolygon.Area(e.ring)) { dropped = true; break; }
                        var piece = Cut(e.ring, w.ring, new Vector2(e.source.x, e.source.z), scratch);
                        if (piece == null) { dropped = true; break; }
                        e.ring = piece; e.cut = true;
                    }
                    if (dropped) break;
                }
                if (!dropped && e.cut && !Good(e.ring, e.area0 * KeepShare)) dropped = true;
                if (dropped) { result.dropped++; continue; }
                if (e.cut) { result.cut++; e.box = Box(e.ring); }
                for (int x = Mathf.FloorToInt(e.box.xMin / Cell); x <= Mathf.FloorToInt(e.box.xMax / Cell); x++)
                    for (int z = Mathf.FloorToInt(e.box.yMin / Cell); z <= Mathf.FloorToInt(e.box.yMax / Cell); z++)
                    {
                        var key = new Vector2Int(x, z);
                        if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<Entry>();
                        list.Add(e);
                    }
                result.buildings.Add(e.cut ? Reshaped(e.source, e.ring) : e.source);
            }
            return result;
        }

        // The outline in world X/Z, counter-clockwise, or null when it has none worth checking.
        static Vector2[] Ring(BuildingFeature b)
        {
            if (b.footprint == null || b.footprint.Length < 6 || b.footprint.Length % 2 != 0) return null;
            var ring = GeoHomeSites.Outline(b);
            foreach (var p in ring) if (!float.IsFinite(p.x) || !float.IsFinite(p.y)) return null;
            float area = GeoPolygon.Area(ring);
            if (Mathf.Abs(area) < 0.01f) return null;
            if (area < 0) Array.Reverse(ring);
            return ring;
        }

        static Rect Box(Vector2[] ring)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var p in ring) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y); }
            return Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        }

        sealed class Scratch
        {
            public readonly List<Vector2> a = new List<Vector2>(), b = new List<Vector2>(), c = new List<Vector2>();
            public readonly Dictionary<Vector2[], List<int>> triangles = new Dictionary<Vector2[], List<int>>();
        }

        // The area two simple outlines share: one clipped by each triangle of the other (Sutherland-Hodgman is exact for any
        // outline against a convex one).
        static float Intersection(Vector2[] ring, Vector2[] other, Scratch s)
        {
            if (!s.triangles.TryGetValue(other, out var triangles))
            {
                try { triangles = GeoPolygon.Triangulate(other); }
                catch (ArgumentException) { triangles = null; }
                s.triangles[other] = triangles;
            }
            if (triangles == null) return 0;
            float sum = 0;
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                s.a.Clear(); s.a.AddRange(ring);
                Vector2 p0 = other[triangles[t]], p1 = other[triangles[t + 1]], p2 = other[triangles[t + 2]];
                if (GeoPolygon.Cross(p1 - p0, p2 - p0) < 0) { var swap = p1; p1 = p2; p2 = swap; }
                Clip(s.a, s.b, p0, p1); Clip(s.b, s.a, p1, p2); Clip(s.a, s.b, p2, p0);
                if (s.b.Count >= 3) sum += Mathf.Abs(GeoPolygon.Area(s.b));
            }
            return sum;
        }

        // keeps what lies to the left of a -> b (inside a counter-clockwise edge)
        static void Clip(List<Vector2> input, List<Vector2> output, Vector2 a, Vector2 b)
        {
            output.Clear();
            var edge = b - a;
            for (int i = 0; i < input.Count; i++)
            {
                var current = input[i]; var next = input[(i + 1) % input.Count];
                float dc = GeoPolygon.Cross(edge, current - a), dn = GeoPolygon.Cross(edge, next - a);
                if (dc >= 0) output.Add(current);
                if ((dc >= 0) != (dn >= 0)) output.Add(Vector2.Lerp(current, next, dc / (dc - dn)));
            }
        }

        // The largest part of ring outside one of the winner's walls (the line through it), preferring a cut that clears
        // the overlap; null when no wall leaves anything. A piece must still be a footprint the importer takes (centre: the
        // building's, from which GeoPolygon.Clean sees it in local metres).
        static Vector2[] Cut(Vector2[] ring, Vector2[] winner, Vector2 centre, Scratch s)
        {
            Vector2[] best = null; bool bestClears = false; float bestArea = 0;
            for (int i = 0; i < winner.Length; i++)
            {
                Vector2 a = winner[i], b = winner[(i + 1) % winner.Length];
                if ((b - a).sqrMagnitude < 1e-10f) continue;
                // outside a counter-clockwise outline's wall is to the right of it: clip by the reversed edge
                s.c.Clear(); s.c.AddRange(ring);
                Clip(s.c, s.a, b, a);
                if (s.a.Count < 3) continue;
                List<Vector2> piece;
                var local = new List<Vector2>(s.a.Count);
                foreach (var p in s.a) local.Add(p - centre);
                try { piece = GeoPolygon.Clean(local); }
                catch (ArgumentException) { continue; }
                float area = GeoPolygon.Area(piece);
                if (area <= 0) continue;
                var candidate = new Vector2[piece.Count];
                for (int k = 0; k < piece.Count; k++) candidate[k] = piece[k] + centre;
                bool clears = Intersection(candidate, winner, s) < MinOverlap;
                if (best == null || (clears && !bestClears) || (clears == bestClears && area > bestArea))
                { best = candidate; bestClears = clears; bestArea = area; }
            }
            return best;
        }

        // big enough and not a sliver
        static bool Good(Vector2[] ring, float keepArea)
        {
            if (ring.Length < 3 || GeoPolygon.Area(ring) < Mathf.Max(keepArea, 4)) return false;
            var frame = GeoRuinFit.FrameOf(ring);
            return frame.width >= MinWidth;
        }

        // A copy of the building with its new outline, about the same centre (it stays in the tile and part that own it).
        static BuildingFeature Reshaped(BuildingFeature b, Vector2[] ring)
        {
            var copy = b.Clone();
            copy.footprint = new float[ring.Length * 2];
            for (int i = 0; i < ring.Length; i++) { copy.footprint[2 * i] = ring[i].x - b.x; copy.footprint[2 * i + 1] = ring[i].y - b.z; }
            return copy;
        }
    }
}
