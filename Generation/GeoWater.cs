using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The water of a world or of a tile on the terrain's own grid: lakes, the water areas of rivers (OpenStreetMap draws a wide
    // river as an area besides its line) and the rivers' lines, as water that is never drawn twice.
    //
    // A river used to be a ribbon of its own along its line, and its water area a flat lake. Where both were drawn the two
    // surfaces crossed (the lake flat at one level for the whole area, the river falling along its length), where two rivers met
    // their ribbons lay over each other, and at every bend of a wide river the inner side of its ribbon folded back over itself.
    // Two surfaces at nearly the same height flicker as the camera moves, and one comes through the other.
    //
    //  - A lake, a pond or a reservoir is flat, as before (GeoLakes), and a river through it is at its level (SettleRivers).
    //  - The water area of a river (water=river, waterway=riverbank, canals) follows the river: its level at a point is that of
    //    the nearest point of a river's line that runs through such an area.
    //  - Rivers and their areas are one surface on the terrain's grid with one height at every node, so nothing lies over
    //    anything, and tiles, which share the grid, meet exactly. Cells that a lake covers are the lake's.
    //  - The ground is shaped for rivers and their areas at once (GeoShore, from the nearest water's edge), after the lakes, and
    //    never raised inside a lake.
    public sealed class GeoWater
    {
        // The kinds of water area (the export's "type": water=*, or waterway=riverbank) that are a river's own water.
        static readonly HashSet<string> FlowingKinds = new HashSet<string> { "river", "riverbank", "canal", "stream", "ditch", "drain", "tidal_channel", "rapids", "fish_pass" };

        public static bool Flowing(AreaFeature area) => area != null && !string.IsNullOrEmpty(area.type) && FlowingKinds.Contains(area.type.Trim().ToLowerInvariant());

        // Metres along a river over which it goes from a lake's level back to its own.
        public const float LakeTransition = 40;
        // Metres that the water surface may leave a node's level to draw several cells of a row as one quad.
        public const float Tolerance = 0.005f;

        readonly HeightField field;
        readonly IList<AreaFeature> water;
        readonly IList<float> levels;
        readonly List<PathBuild> rivers = new List<PathBuild>();
        readonly float depth, cellX, cellZ, cell;
        readonly int n;
        readonly List<int> lakes = new List<int>(), areas = new List<int>(); // flat lakes and river areas (indices into water)
        readonly List<List<Vector3>>[] rings;
        // per node: the signed distance to the edge of a river or a river's area (negative inside) and the water's level there
        float[,] edge, level;
        // per node: the flat lake it is in (-1: none); per cell (row, column): the flat lake whose surface covers it
        int[,] lakeNode, lakeCell;
        // per node: the level of the nearest point of a line of a river through a river area; NaN where there is none
        float[,] lineLevel;

        // water and levels: the export's water areas (GeoGenerator.WaterLevels); paths: roads and rivers (only rivers are used);
        // depth: the depth of the water (GenerationSettings.riverDepth).
        public GeoWater(HeightField field, IList<AreaFeature> water, IList<float> levels, IList<PathBuild> paths, float depth)
        {
            this.field = field; this.water = water ?? new AreaFeature[0]; this.levels = levels ?? new float[0]; this.depth = depth;
            n = field.resolution;
            cellX = field.bounds.Width / (n - 1); cellZ = field.bounds.Depth / (n - 1); cell = Mathf.Max(cellX, cellZ);
            if (paths != null) foreach (var path in paths) if (path != null && path.river && path.samples != null && path.samples.Count >= 2) rivers.Add(path);
            rings = new List<List<Vector3>>[this.water.Count];
            for (int i = 0; i < this.water.Count; i++)
            {
                rings[i] = Rings(this.water[i]);
                if (rings[i].Count == 0 || i >= this.levels.Count) continue;
                (Flowing(this.water[i]) ? areas : lakes).Add(i);
            }
            Measure();
        }

        // The narrowest a river's water is on the grid: every cell its line crosses has a corner in the water, so a stream narrower
        // than a cell is still a line of water, and its bed is a groove the water lies in (not ground a few millimetres over it).
        public float MinHalfWidth => 0.75f * cell;

        float HalfWidth(PathBuild path, int sample) => Mathf.Max(path.HalfWidth(sample), MinHalfWidth);

        // Shapes the ground: the lakes from their shores (GeoLakes), then rivers and their areas as one water.
        public void Carve()
        {
            var flat = new List<AreaFeature>(); var flatLevels = new List<float>();
            foreach (int i in lakes) { flat.Add(water[i]); flatLevels.Add(levels[i]); }
            if (flat.Count > 0) GeoLakes.Carve(flat, flatLevels, field, depth);
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    if (edge[z, x] == float.MaxValue) continue;
                    float shaped = GeoShore.Shape(field.heights[z, x], level[z, x], edge[z, x], depth, cell);
                    // a river's line through a lake would raise the lake's bed beside it (to just above the river's water)
                    field.heights[z, x] = lakeNode[z, x] >= 0 ? Mathf.Min(field.heights[z, x], shaped) : shaped;
                }
        }

        // The surface of the rivers and their areas within clip (which lies on the grid's lines, as a tile does): a cell is wet when
        // a corner of it is in the water or the outline of a river's area crosses it, and no lake covers it. Each node has one
        // height, the level of the water there (a lake's where the node is on a cell of the lake), so the surface never lies over
        // itself or over a lake; runs of cells along which the level is even are one quad.
        public Mesh Surface(GeoBounds clip)
        {
            var mesh = new MeshDraft();
            if (rivers.Count == 0 && areas.Count == 0) return mesh.Mesh("Empty water");
            int c0 = Mathf.Clamp(Mathf.RoundToInt((clip.minX - field.bounds.minX) / cellX), 0, n - 1), c1 = Mathf.Clamp(Mathf.RoundToInt((clip.maxX - field.bounds.minX) / cellX), 0, n - 1);
            int r0 = Mathf.Clamp(Mathf.RoundToInt((clip.minZ - field.bounds.minZ) / cellZ), 0, n - 1), r1 = Mathf.Clamp(Mathf.RoundToInt((clip.maxZ - field.bounds.minZ) / cellZ), 0, n - 1);
            if (c1 <= c0 || r1 <= r0) return mesh.Mesh("Empty water");
            var wet = new bool[n - 1, n - 1];
            foreach (int i in areas) Cells(field, rings[i], (row, a, b) => { for (int c = a; c < b; c++) wet[row, c] = true; });
            for (int r = r0; r < r1; r++)
                for (int c = c0; c < c1; c++)
                    if (edge[r, c] < 0 || edge[r, c + 1] < 0 || edge[r + 1, c] < 0 || edge[r + 1, c + 1] < 0) wet[r, c] = true;
            // the nodes of the lakes' cells take the lake's level, so that this surface meets a lake's without a step
            var lakeCorner = new int[n, n];
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) lakeCorner[z, x] = -1;
            for (int r = 0; r < n - 1; r++)
                for (int c = 0; c < n - 1; c++)
                {
                    int lake = lakeCell[r, c];
                    if (lake < 0) continue;
                    wet[r, c] = false;
                    lakeCorner[r, c] = lakeCorner[r, c + 1] = lakeCorner[r + 1, c] = lakeCorner[r + 1, c + 1] = lake;
                }
            var vertex = new Dictionary<int, int>();
            int Vertex(int x, int z)
            {
                int key = z * n + x;
                if (vertex.TryGetValue(key, out int index)) return index;
                float px = field.X(x), pz = field.Z(z);
                index = mesh.vertices.Count;
                mesh.vertices.Add(new Vector3(px, NodeLevel(x, z, lakeCorner), pz)); mesh.uv.Add(new Vector2(px, pz) / 10);
                vertex[key] = index;
                return index;
            }
            for (int r = r0; r < r1; r++)
            {
                int c = c0;
                while (c < c1)
                {
                    if (!wet[r, c]) { c++; continue; }
                    // the cells after it while the water along both edges of the row stays within Tolerance of a straight line from
                    // the first node to the last: one quad (a lake, flat, in one run; a river in runs as long as its fall is even).
                    // A neighbouring row's vertices on those edges are then within Tolerance of the quad's edge: no crack to speak of.
                    int end = c + 1;
                    float b0 = NodeLevel(c, r, lakeCorner), b1 = NodeLevel(c, r + 1, lakeCorner);
                    float lo0 = float.MinValue, hi0 = float.MaxValue, lo1 = float.MinValue, hi1 = float.MaxValue;
                    while (end < c1 && wet[r, end])
                    {
                        float d = end - c;
                        float m0 = NodeLevel(end, r, lakeCorner) - b0, m1 = NodeLevel(end, r + 1, lakeCorner) - b1;
                        lo0 = Mathf.Max(lo0, (m0 - Tolerance) / d); hi0 = Mathf.Min(hi0, (m0 + Tolerance) / d);
                        lo1 = Mathf.Max(lo1, (m1 - Tolerance) / d); hi1 = Mathf.Min(hi1, (m1 + Tolerance) / d);
                        float e0 = (NodeLevel(end + 1, r, lakeCorner) - b0) / (d + 1), e1 = (NodeLevel(end + 1, r + 1, lakeCorner) - b1) / (d + 1);
                        if (e0 < lo0 || e0 > hi0 || e1 < lo1 || e1 > hi1) break;
                        end++;
                    }
                    int a = Vertex(c, r), b = Vertex(c, r + 1), dd = Vertex(end, r + 1), e = Vertex(end, r);
                    mesh.triangles.AddRange(new[] { a, b, dd, a, dd, e });
                    c = end;
                }
            }
            return mesh.Mesh("River water surface");
        }

        float NodeLevel(int x, int z, int[,] lakeCorner)
        {
            int lake = lakeCorner[z, x];
            if (lake >= 0) return levels[lake];
            if (edge[z, x] < float.MaxValue) return level[z, x];
            if (lineLevel != null && !float.IsNaN(lineLevel[z, x])) return lineLevel[z, x];
            return field.heights[z, x];
        }

        // The water of every node: which lake it is in, which lake covers each cell, and the distance to the edge of the nearest
        // river or river's area with the level of the water there.
        void Measure()
        {
            lakeNode = new int[n, n]; lakeCell = new int[n - 1, n - 1];
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) lakeNode[z, x] = -1;
            for (int r = 0; r < n - 1; r++) for (int c = 0; c < n - 1; c++) lakeCell[r, c] = -1;
            var crossings = new List<float>();
            foreach (int i in lakes)
            {
                var near = NearRows(rings[i]);
                for (int z = 0; z < n; z++)
                {
                    Crossings(near, field.Z(z), crossings);
                    for (int k = 0; k + 1 < crossings.Count; k += 2)
                        for (int x = Mathf.Max(0, Mathf.CeilToInt((crossings[k] - field.bounds.minX) / cellX)); x < n && field.X(x) <= crossings[k + 1]; x++)
                            if (lakeNode[z, x] < 0) lakeNode[z, x] = i;
                }
                int lake = i;
                Cells(field, rings[i], (row, a, b) => { for (int c = a; c < b; c++) if (lakeCell[row, c] < 0) lakeCell[row, c] = lake; });
            }

            edge = new float[n, n]; level = new float[n, n];
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) edge[z, x] = float.MaxValue;
            float reach = GeoShore.Reach(depth, cell);
            if (areas.Count > 0) lineLevel = LineLevels();
            foreach (int i in areas)
            {
                var near = NearRows(rings[i]);
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var ring in rings[i]) foreach (var p in ring) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
                int x0 = Column(minX - reach, false), x1 = Column(maxX + reach, true), z0 = Row(minZ - reach, false), z1 = Row(maxZ + reach, true);
                if (x1 < x0 || z1 < z0) continue;
                var distance = new float[z1 - z0 + 1, x1 - x0 + 1];
                for (int z = 0; z <= z1 - z0; z++) for (int x = 0; x <= x1 - x0; x++) distance[z, x] = float.MaxValue;
                foreach (var ring in rings[i])
                    for (int k = 0; k < ring.Count; k++)
                    {
                        Vector3 a = ring[k], b = ring[(k + 1) % ring.Count];
                        int ex0 = Mathf.Max(x0, Column(Mathf.Min(a.x, b.x) - reach, false)), ex1 = Mathf.Min(x1, Column(Mathf.Max(a.x, b.x) + reach, true));
                        int ez0 = Mathf.Max(z0, Row(Mathf.Min(a.z, b.z) - reach, false)), ez1 = Mathf.Min(z1, Row(Mathf.Max(a.z, b.z) + reach, true));
                        for (int z = ez0; z <= ez1; z++)
                            for (int x = ex0; x <= ex1; x++)
                            {
                                float d = GeoData.SegmentDistance(new Vector2(field.X(x), field.Z(z)), new Vector2(a.x, a.z), new Vector2(b.x, b.z), out _);
                                if (d < distance[z - z0, x - x0]) distance[z - z0, x - x0] = d;
                            }
                    }
                for (int z = z0; z <= z1; z++)
                {
                    Crossings(near, field.Z(z), crossings);
                    for (int x = x0; x <= x1; x++)
                    {
                        float d = distance[z - z0, x - x0];
                        if (d >= reach && !Inside(crossings, field.X(x))) continue;
                        float s = Inside(crossings, field.X(x)) ? -d : d;
                        if (s >= edge[z, x]) continue;
                        float own = lineLevel != null ? lineLevel[z, x] : float.NaN;
                        edge[z, x] = s; level[z, x] = float.IsNaN(own) ? levels[i] : own;
                    }
                }
            }
            foreach (var path in rivers)
                for (int i = 1; i < path.samples.Count; i++)
                {
                    Vector3 a = path.samples[i - 1], b = path.samples[i];
                    float ha = HalfWidth(path, i - 1), hb = HalfWidth(path, i);
                    field.RasterSegment(a, b, Mathf.Max(ha, hb) + reach, (x, z, d, t) =>
                    {
                        float s = d - Mathf.Lerp(ha, hb, t);
                        if (s < edge[z, x]) { edge[z, x] = s; level[z, x] = Mathf.Lerp(a.y, b.y, t); }
                    });
                }
        }

        // Nodes of the grid between two samples of the level of a river's line (LineLevels).
        const int LineStep = 8;

        // The level of the nearest point of a river's line, at every node near a river's area, from the rivers whose lines run
        // through such an area (null without them). It is worked out exactly every LineStep nodes, on nodes counted from the
        // world's origin, and bilinear in between: tiles share those nodes, and with the lines read RiverReach beyond the margin
        // (a wide river's line may lie outside the tile) they come to the same levels on their common edge. In between the level
        // is smooth also where the nearest point jumps from one stretch of a winding river to another.
        float[,] LineLevels()
        {
            var lines = new List<PathBuild>();
            foreach (var path in rivers) if (ThroughAnArea(path)) lines.Add(path);
            if (lines.Count == 0) return null;
            int offX = Mod(Mathf.RoundToInt(field.bounds.minX / cellX), LineStep), offZ = Mod(Mathf.RoundToInt(field.bounds.minZ / cellZ), LineStep);
            int kx = (n - 1 + offX) / LineStep + 2, kz = (n - 1 + offZ) / LineStep + 2;
            // where it is needed: the boxes of the rivers' areas, and the reach of their banks
            float reach = GeoShore.Reach(depth, cell) + LineStep * cell;
            var boxes = new List<Rect>();
            foreach (int i in areas)
            {
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var ring in rings[i]) foreach (var p in ring) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
                boxes.Add(Rect.MinMaxRect(minX - reach, minZ - reach, maxX + reach, maxZ + reach));
            }
            var coarse = new float[kz, kx];
            for (int j = 0; j < kz; j++)
                for (int i = 0; i < kx; i++)
                {
                    var p = new Vector2(field.bounds.minX + (i * LineStep - offX) * cellX, field.bounds.minZ + (j * LineStep - offZ) * cellZ);
                    coarse[j, i] = float.NaN;
                    if (!boxes.Exists(b => b.Contains(p))) continue;
                    float best = float.MaxValue;
                    foreach (var path in lines)
                        for (int k = 1; k < path.samples.Count; k++)
                        {
                            Vector3 a = path.samples[k - 1], b = path.samples[k];
                            float d = GeoData.SegmentDistance(p, new Vector2(a.x, a.z), new Vector2(b.x, b.z), out float t);
                            if (d < best) { best = d; coarse[j, i] = Mathf.Lerp(a.y, b.y, t); }
                        }
                }
            var result = new float[n, n];
            for (int z = 0; z < n; z++)
            {
                int j = (z + offZ) / LineStep; float tz = (z + offZ - j * LineStep) / (float)LineStep;
                for (int x = 0; x < n; x++)
                {
                    int i = (x + offX) / LineStep; float tx = (x + offX - i * LineStep) / (float)LineStep;
                    float a = coarse[j, i], b = coarse[j, i + 1], c = coarse[j + 1, i], d = coarse[j + 1, i + 1];
                    result[z, x] = float.IsNaN(a) || float.IsNaN(b) || float.IsNaN(c) || float.IsNaN(d) ? Nearest(a, b, c, d, tx, tz)
                        : Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
                }
            }
            return result;
        }

        static int Mod(int a, int m) => ((a % m) + m) % m;

        // at the edge of where the levels are known: the known corner nearest to the point
        static float Nearest(float a, float b, float c, float d, float tx, float tz)
        {
            float best = float.MaxValue, value = float.NaN;
            void Try(float v, float dx, float dz) { if (!float.IsNaN(v) && dx * dx + dz * dz < best) { best = dx * dx + dz * dz; value = v; } }
            Try(a, tx, tz); Try(b, 1 - tx, tz); Try(c, tx, 1 - tz); Try(d, 1 - tx, 1 - tz);
            return value;
        }

        bool ThroughAnArea(PathBuild path)
        {
            foreach (int i in areas)
                for (int k = 0; k < path.samples.Count; k += 4)
                    if (GeoData.InPolygon(path.samples[k].x, path.samples[k].z, water[i].rings)) return true;
            return false;
        }

        int Column(float x, bool up) => Mathf.Clamp(up ? Mathf.CeilToInt((x - field.bounds.minX) / cellX) : Mathf.FloorToInt((x - field.bounds.minX) / cellX), 0, n - 1);
        int Row(float z, bool up) => Mathf.Clamp(up ? Mathf.CeilToInt((z - field.bounds.minZ) / cellZ) : Mathf.FloorToInt((z - field.bounds.minZ) / cellZ), 0, n - 1);

        // The edges of the rings that reach the field's rows (a river's area can be the whole river, hundreds of kilometres of it):
        // only those can cross a row, and the inside of a row is the same with them alone.
        List<List<Vector3>> NearRows(List<List<Vector3>> source)
        {
            float low = field.bounds.minZ, high = field.bounds.maxZ;
            var result = new List<List<Vector3>>();
            foreach (var ring in source)
            {
                var edges = new List<Vector3>();
                for (int k = 0, j = ring.Count - 1; k < ring.Count; j = k++)
                    if (Mathf.Max(ring[j].z, ring[k].z) >= low && Mathf.Min(ring[j].z, ring[k].z) <= high) { edges.Add(ring[j]); edges.Add(ring[k]); }
                if (edges.Count > 0) result.Add(edges);
            }
            return result;
        }

        // The x of every crossing of the edges (pairs of points, from NearRows) with the line at z, in order.
        static void Crossings(List<List<Vector3>> edges, float z, List<float> result)
        {
            result.Clear();
            foreach (var list in edges)
                for (int k = 0; k + 1 < list.Count; k += 2)
                {
                    Vector3 a = list[k], b = list[k + 1];
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

        // The cells of each row that the outline of an area covers or touches, as GeoLakes.Surface draws them: visit(row, from, to)
        // for the cells from..to-1 of the row.
        static void Cells(HeightField field, List<List<Vector3>> rings, Action<int, int, int> visit)
        {
            int n = field.resolution;
            float cellX = field.bounds.Width / (n - 1), cellZ = field.bounds.Depth / (n - 1);
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var ring in rings) foreach (var p in ring) { minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
            int r0 = Mathf.Max(0, Mathf.FloorToInt((minZ - field.bounds.minZ) / cellZ)), r1 = Mathf.Min(n - 1, Mathf.CeilToInt((maxZ - field.bounds.minZ) / cellZ));
            var crossings = new List<float>();
            for (int row = r0; row < r1; row++)
                foreach (float z in new[] { field.Z(row), field.Z(row) + cellZ * 0.5f, field.Z(row + 1) })
                {
                    GeoLakes.Crossings(rings, z, crossings);
                    for (int k = 0; k + 1 < crossings.Count; k += 2)
                    {
                        int a = Mathf.Max(0, Mathf.FloorToInt((crossings[k] - field.bounds.minX) / cellX)), b = Mathf.Min(n - 1, Mathf.CeilToInt((crossings[k + 1] - field.bounds.minX) / cellX));
                        if (b > a) visit(row, a, b);
                    }
                }
        }

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

        // A river through a lake (a pond on a stream, a reservoir, the export's water areas that are not a river's own) is at the
        // lake's level inside it and goes back to its own level over LakeTransition metres outside it: the river's level comes
        // from the elevation data along its line and the lake's from its shore, and where they differed the two waters met at two
        // heights. Works on whole paths (the export's, before they are cut into tiles), so every tile has the same levels.
        public static void SettleRivers(IList<PathBuild> paths, IList<AreaFeature> water, IList<float> levels)
        {
            if (paths == null || water == null || levels == null) return;
            var lakes = new List<int>(); var boxes = new List<Rect>();
            for (int i = 0; i < water.Count && i < levels.Count; i++)
            {
                if (Flowing(water[i]) || water[i]?.rings == null) continue;
                var box = Box(water[i].rings);
                if (box.width <= 0) continue;
                lakes.Add(i); boxes.Add(box);
            }
            if (lakes.Count == 0) return;
            foreach (var path in paths)
            {
                if (path == null || !path.river || path.samples == null || path.samples.Count < 2) continue;
                var s = path.samples; int count = s.Count;
                var lake = new float[count]; bool any = false;
                for (int i = 0; i < count; i++)
                {
                    lake[i] = float.NaN;
                    for (int k = 0; k < lakes.Count; k++)
                        if (boxes[k].Contains(new Vector2(s[i].x, s[i].z)) && GeoData.InPolygon(s[i].x, s[i].z, water[lakes[k]].rings)) { lake[i] = levels[lakes[k]]; any = true; break; }
                }
                if (!any) continue;
                var along = new float[count];
                for (int i = 1; i < count; i++) along[i] = along[i - 1] + Vector2.Distance(new Vector2(s[i - 1].x, s[i - 1].z), new Vector2(s[i].x, s[i].z));
                // the nearest sample in a lake before and after each sample, along the river
                var before = new int[count]; var after = new int[count];
                for (int i = 0, last = -1; i < count; i++) { if (!float.IsNaN(lake[i])) last = i; before[i] = last; }
                for (int i = count - 1, last = -1; i >= 0; i--) { if (!float.IsNaN(lake[i])) last = i; after[i] = last; }
                for (int i = 0; i < count; i++)
                {
                    float y = s[i].y;
                    if (!float.IsNaN(lake[i])) y = lake[i];
                    else
                    {
                        int k = before[i] < 0 ? after[i] : after[i] < 0 ? before[i] : along[i] - along[before[i]] <= along[after[i]] - along[i] ? before[i] : after[i];
                        float distance = Mathf.Abs(along[i] - along[k]);
                        if (distance < LakeTransition) y = Mathf.Lerp(lake[k], y, distance / LakeTransition);
                    }
                    s[i] = new Vector3(s[i].x, y, s[i].z);
                }
            }
        }

        static Rect Box(float[][] rings)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var ring in rings)
            {
                if (ring == null) continue;
                for (int i = 0; i + 1 < ring.Length; i += 2) { minX = Mathf.Min(minX, ring[i]); maxX = Mathf.Max(maxX, ring[i]); minZ = Mathf.Min(minZ, ring[i + 1]); maxZ = Mathf.Max(maxZ, ring[i + 1]); }
            }
            return minX > maxX ? Rect.zero : Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        }
    }
}
