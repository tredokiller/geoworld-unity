using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Lakes, ponds and reservoirs: the bed carved into the terrain and the water surface over it.
    //
    // Both work on the terrain's own height grid, which every tile of a world shares, so a lake that crosses tiles has the
    // same bed and the same shore on both sides of the edge, and its surface meets the next tile's surface exactly.
    //
    // The bed slopes from the shore (at the water level) down to the full depth over a bank, and the ground just outside
    // the shore is brought down to the water level on a gentle slope, so water and terrain meet along the real outline of
    // the lake and not at a wall. The surface is one flat quad for each run of wet cells in a row of the grid (the cells
    // that the outline touches are wet: the terrain's bank hides the edge of the first one), so it follows the outline
    // to the grid's resolution and has no gaps against the terrain.
    public static class GeoLakes
    {
        // Metres the bed takes to reach its full depth: a bank of about one to three, 4 m at least.
        public static float Bank(float depth) => GeoShore.Bank(depth);

        // Lowers the field to the lakes' beds. levels: the water level of each lake.
        public static void Carve(IList<AreaFeature> water, IList<float> levels, HeightField field, float depth)
        {
            int n = field.resolution;
            // a single world's field need not be square: its cells are as wide as the world over its columns and as deep as it is
            // over its rows
            double cellX = (double)field.bounds.Width / (n - 1), cellZ = (double)field.bounds.Depth / (n - 1), cell = Math.Max(cellX, cellZ);
            float reach = GeoShore.Reach(depth, (float)cell);
            for (int i = 0; i < water.Count; i++)
            {
                var rings = Rings(water[i]);
                if (rings.Count == 0) continue;
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var ring in rings) foreach (var p in ring) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
                // the nodes that the bank or the slope outside can reach
                int x0 = Mathf.Max(0, Mathf.FloorToInt((float)((minX - reach - field.bounds.minX) / cellX))), x1 = Mathf.Min(n - 1, Mathf.CeilToInt((float)((maxX + reach - field.bounds.minX) / cellX)));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((float)((minZ - reach - field.bounds.minZ) / cellZ))), z1 = Mathf.Min(n - 1, Mathf.CeilToInt((float)((maxZ + reach - field.bounds.minZ) / cellZ)));
                if (x1 < x0 || z1 < z0) continue;
                int w = x1 - x0 + 1, h = z1 - z0 + 1;
                var distance = new float[h, w];
                for (int z = 0; z < h; z++) for (int x = 0; x < w; x++) distance[z, x] = float.MaxValue;
                // the distance to the outline, of the nodes within reach of it
                foreach (var ring in rings)
                    for (int k = 0; k < ring.Count; k++)
                    {
                        Vector3 a = ring[k], b = ring[(k + 1) % ring.Count];
                        int ex0 = Mathf.Max(x0, Mathf.FloorToInt((float)((Mathf.Min(a.x, b.x) - reach - field.bounds.minX) / cellX))), ex1 = Mathf.Min(x1, Mathf.CeilToInt((float)((Mathf.Max(a.x, b.x) + reach - field.bounds.minX) / cellX)));
                        int ez0 = Mathf.Max(z0, Mathf.FloorToInt((float)((Mathf.Min(a.z, b.z) - reach - field.bounds.minZ) / cellZ))), ez1 = Mathf.Min(z1, Mathf.CeilToInt((float)((Mathf.Max(a.z, b.z) + reach - field.bounds.minZ) / cellZ)));
                        for (int z = ez0; z <= ez1; z++)
                            for (int x = ex0; x <= ex1; x++)
                            {
                                float d = GeoData.SegmentDistance(new Vector2(Node(field, x, true), Node(field, z, false)), new Vector2(a.x, a.z), new Vector2(b.x, b.z), out _);
                                if (d < distance[z - z0, x - x0]) distance[z - z0, x - x0] = d;
                            }
                    }
                // inside the lake: the nodes of each row between an odd and an even crossing of the outline
                var crossings = new List<float>();
                for (int z = z0; z <= z1; z++)
                {
                    Crossings(rings, Node(field, z, false), crossings);
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = Node(field, x, true), d = distance[z - z0, x - x0];
                        bool inside = Inside(crossings, px);
                        float level = levels[i];
                        // inside: the water level at the shore, the full depth a bank in; outside: the shore's slope and shoulder (GeoShore)
                        if (inside) field.heights[z, x] = GeoShore.Shape(field.heights[z, x], level, -d, depth, (float)cell);
                        else if (d < float.MaxValue) field.heights[z, x] = GeoShore.Shape(field.heights[z, x], level, d, depth, (float)cell);
                    }
                }
            }
        }

        // The water surface of one lake (its outlines, more than one where the same lake came from several exports) on the
        // field's grid, within clip (which lies on the grid's lines, as a tile does): flat quads at the water level.
        public static Mesh Surface(IList<AreaFeature> lake, float level, HeightField field, GeoBounds clip)
        {
            var mesh = new MeshDraft();
            // each outline (with its islands) on its own: two outlines of one lake, from two exports, must not cancel each other out
            var outlines = lake.Select(Rings).Where(r => r.Count > 0).ToList();
            if (outlines.Count == 0) return mesh.Mesh("Empty water");
            var rings = outlines.SelectMany(r => r).ToList();
            int n = field.resolution;
            double cell = (double)field.bounds.Width / (n - 1), cellZ = (double)field.bounds.Depth / (n - 1);
            float minZ = rings.Min(r => r.Min(p => p.z)), maxZ = rings.Max(r => r.Max(p => p.z));
            // the cells of the grid inside clip, and of those the rows the lake reaches
            int c0 = Mathf.Max(0, Mathf.RoundToInt((float)((clip.minX - field.bounds.minX) / cell))), c1 = Mathf.Min(n - 1, Mathf.RoundToInt((float)((clip.maxX - field.bounds.minX) / cell)));
            int r0 = Mathf.Max(Mathf.Max(0, Mathf.RoundToInt((float)((clip.minZ - field.bounds.minZ) / cellZ))), Mathf.FloorToInt((float)((minZ - field.bounds.minZ) / cellZ)));
            int r1 = Mathf.Min(Mathf.Min(n - 1, Mathf.RoundToInt((float)((clip.maxZ - field.bounds.minZ) / cellZ))), Mathf.CeilToInt((float)((maxZ - field.bounds.minZ) / cellZ)));
            var crossings = new List<float>();
            var spans = new List<Vector2Int>();
            for (int row = r0; row < r1; row++)
            {
                // the cell's two edges and its middle: a cell the outline only touches is wet, so no cell next to the shore is dry
                spans.Clear();
                foreach (float z in new[] { Node(field, row, false), Node(field, row, false) + (float)(cellZ * 0.5), Node(field, row + 1, false) })
                {
                    foreach (var outline in outlines)
                    {
                        Crossings(outline, z, crossings);
                        for (int k = 0; k + 1 < crossings.Count; k += 2)
                        {
                            int a = Mathf.FloorToInt((float)((crossings[k] - field.bounds.minX) / cell)), b = Mathf.CeilToInt((float)((crossings[k + 1] - field.bounds.minX) / cell));
                            a = Mathf.Max(a, c0); b = Mathf.Min(b, c1);
                            if (b > a) spans.Add(new Vector2Int(a, b));
                        }
                    }
                }
                if (spans.Count == 0) continue;
                spans.Sort((p, q) => p.x.CompareTo(q.x));
                int from = spans[0].x, to = spans[0].y;
                for (int k = 1; k <= spans.Count; k++)
                {
                    if (k < spans.Count && spans[k].x <= to) { to = Mathf.Max(to, spans[k].y); continue; }
                    float xa = Node(field, from, true), xb = Node(field, to, true), za = Node(field, row, false), zb = Node(field, row + 1, false);
                    mesh.Quad(new Vector3(xa, level, za), new Vector3(xa, level, zb), new Vector3(xb, level, zb), new Vector3(xb, level, za),
                        new Vector2(xa, za) / 10, new Vector2(xa, zb) / 10, new Vector2(xb, zb) / 10, new Vector2(xb, za) / 10);
                    if (k < spans.Count) { from = spans[k].x; to = spans[k].y; }
                }
            }
            return mesh.Mesh("Water body surface");
        }

        // Every ring of a lake (the outline and the islands) as points; empty rings left out.
        static List<List<Vector3>> Rings(AreaFeature feature)
        {
            var result = new List<List<Vector3>>();
            if (feature?.rings == null) return result;
            foreach (var ring in feature.rings)
            {
                var points = GeoData.Points(ring);
                if (points.Count >= 3) result.Add(points);
            }
            return result;
        }

        static float Node(HeightField field, int index, bool x) =>
            (float)(x ? field.bounds.minX + index * ((double)field.bounds.Width / (field.resolution - 1)) : field.bounds.minZ + index * ((double)field.bounds.Depth / (field.resolution - 1)));

        // The x of every crossing of the outlines with the line at z, in order: the inside lies between the first and the
        // second, the third and the fourth, and so on (islands and holes come out right).
        internal static void Crossings(List<List<Vector3>> rings, float z, List<float> result)
        {
            result.Clear();
            foreach (var ring in rings)
                for (int k = 0, j = ring.Count - 1; k < ring.Count; j = k++)
                {
                    Vector3 a = ring[j], b = ring[k];
                    if ((a.z <= z) == (b.z <= z)) continue;
                    result.Add(a.x + (z - a.z) / (b.z - a.z) * (b.x - a.x));
                }
            result.Sort();
        }

        static bool Inside(List<float> crossings, float x)
        {
            bool inside = false;
            for (int k = 0; k < crossings.Count && crossings[k] <= x; k++) inside = !inside;
            return inside;
        }

        static float Smooth(float t) => t * t * (3 - 2 * t);
    }
}
