using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The tree lines of an export (shelterbelts, the rows of trees between fields where positions of the war stood), for the
    // trenches of a battle (GeoTrenchScatter). OpenStreetMap maps few of them (Klishchiivka's export has none as tree rows), so
    // they come from three places: OSM tree rows; narrow woods (a polygon whose mean width, twice its area over its perimeter,
    // is under 45 m), as the line along their long axis; and the export's ground map (ESA WorldCover with OSM woods): forest
    // and scrub cells at 10 m in strips no more than 50 m wide, thinned to a one-cell skeleton (Zhang-Suen) and traced into
    // lines. Lines shorter than 60 m are dropped; points 10 m apart.
    public static class GeoTreeLines
    {
        const float Cell = 10, MinLength = 60, MaxHalfWidth = 25;

        // half the width of an OSM tree row (one or two rows of crowns)
        const float RowHalfWidth = 4;

        public static List<GeoTreeLine> Find(EnvironmentData env, GeoGroundMap ground, List<string> notes)
        {
            var lines = new List<GeoTreeLine>();
            int rows = 0, woods = 0, mapped = 0;
            foreach (var row in env.treeRows)
            {
                var points = GeoData.Points(row?.points);
                if (points.Count < 2) continue;
                var line = new List<Vector2>();
                foreach (var p in points) line.Add(new Vector2(p.x, p.z));
                if (Length(line) >= MinLength) { lines.Add(new GeoTreeLine { points = line, left = RowHalfWidth, right = RowHalfWidth }); rows++; }
            }
            foreach (var area in env.areas)
            {
                if (area?.rings == null || area.rings.Length == 0 || area.rings[0] == null || area.rings[0].Length < 6) continue;
                string tag = area.tag ?? "";
                if (area.material != "forest" && area.material != "scrub" && !tag.StartsWith("natural=wood") && !tag.StartsWith("landuse=forest")) continue;
                var axis = NarrowAxis(area.rings[0]);
                // a wood along a tree row already found is the same belt
                if (axis == null || Near(axis, lines)) continue;
                var ring = area.rings[0];
                bool InWood(Vector2 p) => InRing(ring, p.x, p.y);
                lines.Add(new GeoTreeLine { points = axis, left = SideWidth(axis, 1, InWood), right = SideWidth(axis, -1, InWood) });
                woods++;
            }
            if (ground != null && ground.Any)
            {
                foreach (var line in FromGround(env.bounds, ground))
                {
                    // a line of the ground map that runs along one already found is the same tree line
                    if (Near(line.points, lines)) continue;
                    lines.Add(line);
                    mapped++;
                }
            }
            notes.Add($"Tree lines for the trenches: {rows} OSM tree rows, {woods} narrow woods, {mapped} from the ground map.");
            return lines;
        }

        // the long axis of a narrow, straight polygon, through its middle, or null when it is not narrow, long and straight
        static List<Vector2> NarrowAxis(float[] ring)
        {
            var pts = new List<Vector2>();
            for (int i = 0; i + 1 < ring.Length; i += 2) pts.Add(new Vector2(ring[i], ring[i + 1]));
            float area = 0, perimeter = 0;
            Vector2 centre = Vector2.zero;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                area += pts[j].x * pts[i].y - pts[i].x * pts[j].y;
                perimeter += Vector2.Distance(pts[j], pts[i]);
                centre += pts[i];
            }
            area = Mathf.Abs(area) * 0.5f;
            centre /= pts.Count;
            float width = 2 * area / Mathf.Max(1, perimeter);
            if (width >= 2 * MaxHalfWidth * 0.9f) return null;
            // principal axis of the outline's points
            float sxx = 0, sxy = 0, syy = 0;
            foreach (var p in pts) { var d = p - centre; sxx += d.x * d.x; sxy += d.x * d.y; syy += d.y * d.y; }
            float angle = 0.5f * Mathf.Atan2(2 * sxy, sxx - syy);
            var axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in pts) { float s = Vector2.Dot(p - centre, axis); min = Mathf.Min(min, s); max = Mathf.Max(max, s); }
            if (max - min < MinLength) return null;
            // an L-shaped, curved or crooked wood is not a strip along one axis (the ground map traces its belts instead):
            // most of its outline lies within its width of the axis
            var across = new Vector2(-axis.y, axis.x);
            int near = 0;
            foreach (var p in pts) if (Mathf.Abs(Vector2.Dot(p - centre, across)) <= width * 0.8f + 6) near++;
            if (near < pts.Count * 0.85f) return null;
            var line = new List<Vector2>();
            for (float s = min + 5; s <= max - 5; s += Cell) line.Add(centre + axis * s);
            return line.Count >= 2 ? line : null;
        }

        // How far the trees reach from the line on one side (side 1: its left, along GeoTrenchScatter's normal), the median of
        // walks across it every point: an even-width strip's skeleton lies half a cell off its middle, and one width for both
        // sides put the fire trench metres inside the trees on the far side.
        static float SideWidth(List<Vector2> line, int side, System.Func<Vector2, bool> inTrees)
        {
            var reach = new List<float>();
            for (int i = 1; i + 1 < line.Count; i++)
            {
                var d = (line[i + 1] - line[i - 1]).normalized;
                var n = new Vector2(-d.y, d.x) * side;
                float s = 0;
                while (s < MaxHalfWidth + 10 && inTrees(line[i] + n * s)) s += 1;
                reach.Add(s);
            }
            if (reach.Count == 0) return 4;
            reach.Sort();
            return Mathf.Clamp(reach[reach.Count / 2], 2, MaxHalfWidth + 10);
        }

        // whether (x, z) lies inside a ring of x, z pairs (even-odd)
        static bool InRing(float[] ring, float x, float z)
        {
            bool inside = false;
            int n = ring.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = ring[2 * i], zi = ring[2 * i + 1], xj = ring[2 * j], zj = ring[2 * j + 1];
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        // narrow strips of forest and scrub on the ground map, thinned and traced
        static IEnumerable<GeoTreeLine> FromGround(GeoBounds b, GeoGroundMap ground)
        {
            int nx = Mathf.Max(3, Mathf.CeilToInt(b.Width / Cell)), nz = Mathf.Max(3, Mathf.CeilToInt(b.Depth / Cell));
            var forest = new bool[nx * nz];
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    var c = ground.At(b.minX + (i + 0.5f) * Cell, b.minZ + (j + 0.5f) * Cell);
                    forest[j * nx + i] = c == GeoLandClass.Forest || c == GeoLandClass.Scrub || c == GeoLandClass.Orchard;
                }
            // distance to open ground (chamfer, in cells): a strip is no wider than twice MaxHalfWidth
            var dist = new float[nx * nz];
            for (int k = 0; k < dist.Length; k++) dist[k] = forest[k] ? 1e6f : 0;
            for (int j = 0; j < nz; j++) for (int i = 0; i < nx; i++) Relax(dist, nx, nz, i, j, 1);
            for (int j = nz - 1; j >= 0; j--) for (int i = nx - 1; i >= 0; i--) Relax(dist, nx, nz, i, j, -1);
            float limit = MaxHalfWidth / Cell + 0.5f;
            // a strip cell belongs to a part of the forest whose middle is never further than limit from its edge
            var strip = new bool[nx * nz];
            var widest = new float[nx * nz];
            for (int k = 0; k < dist.Length; k++) widest[k] = forest[k] ? dist[k] : 0;
            // spread the strip's widest middle over it (two passes of max over 3 x 3, enough for strips up to 5 cells)
            for (int pass = 0; pass < 3; pass++)
            {
                var next = (float[])widest.Clone();
                for (int j = 0; j < nz; j++) for (int i = 0; i < nx; i++)
                {
                    if (!forest[j * nx + i]) continue;
                    for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
                    {
                        int x = i + di, z = j + dj;
                        if (x >= 0 && z >= 0 && x < nx && z < nz && forest[z * nx + x]) next[j * nx + i] = Mathf.Max(next[j * nx + i], widest[z * nx + x]);
                    }
                }
                widest = next;
            }
            for (int k = 0; k < strip.Length; k++) strip[k] = forest[k] && widest[k] <= limit;
            Thin(strip, nx, nz);
            bool InForest(Vector2 p)
            {
                int i = Mathf.FloorToInt((p.x - b.minX) / Cell), j = Mathf.FloorToInt((p.y - b.minZ) / Cell);
                return i >= 0 && j >= 0 && i < nx && j < nz && forest[j * nx + i];
            }
            foreach (var cells in Trace(strip, nx, nz))
            {
                // through the cell centres, smoothed over five cells: a slanted strip's skeleton is a staircase of 10 m steps,
                // whose normals swing by 90 degrees from cell to cell
                var line = new List<Vector2>();
                for (int c = 0; c < cells.Count; c++)
                {
                    Vector2 sum = Vector2.zero;
                    int n = 0;
                    for (int o = -2; o <= 2; o++)
                    {
                        int q = c + o;
                        if (q < 0 || q >= cells.Count || c == 0 || c == cells.Count - 1) continue;
                        sum += new Vector2(cells[q].x, cells[q].y);
                        n++;
                    }
                    var cell = n > 0 ? sum / n : new Vector2(cells[c].x, cells[c].y);
                    line.Add(new Vector2(b.minX + (cell.x + 0.5f) * Cell, b.minZ + (cell.y + 0.5f) * Cell));
                }
                if (Length(line) < MinLength) continue;
                yield return new GeoTreeLine { points = line, left = SideWidth(line, 1, InForest), right = SideWidth(line, -1, InForest) };
            }
        }

        // dir 1: the forward pass (rows up, columns right) reads the row below and the cell to the left; -1: the backward pass
        static void Relax(float[] d, int nx, int nz, int i, int j, int dir)
        {
            int k = j * nx + i;
            if (d[k] == 0) return;
            for (int dj = -1; dj <= 1; dj++) for (int di = -1; di <= 1; di++)
            {
                if (dj * dir > 0 || dj == 0 && di * dir >= 0) continue;
                // forward: dj = -1 (the row below) or dj = 0 and di = -1; backward: dj = 1 or dj = 0 and di = 1
                int x = i + di, z = j + dj;
                if (x < 0 || z < 0 || x >= nx || z >= nz) { d[k] = Mathf.Min(d[k], 1); continue; }
                d[k] = Mathf.Min(d[k], d[z * nx + x] + (di != 0 && dj != 0 ? 1.414f : 1));
            }
        }

        // Zhang-Suen thinning to a one-cell skeleton
        static void Thin(bool[] m, int nx, int nz)
        {
            var remove = new List<int>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int step = 0; step < 2; step++)
                {
                    remove.Clear();
                    for (int j = 1; j < nz - 1; j++)
                        for (int i = 1; i < nx - 1; i++)
                        {
                            if (!m[j * nx + i]) continue;
                            bool p2 = m[(j + 1) * nx + i], p3 = m[(j + 1) * nx + i + 1], p4 = m[j * nx + i + 1], p5 = m[(j - 1) * nx + i + 1];
                            bool p6 = m[(j - 1) * nx + i], p7 = m[(j - 1) * nx + i - 1], p8 = m[j * nx + i - 1], p9 = m[(j + 1) * nx + i - 1];
                            int b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                            if (b < 2 || b > 6) continue;
                            bool[] ring = { p2, p3, p4, p5, p6, p7, p8, p9, p2 };
                            int a = 0;
                            for (int r = 0; r < 8; r++) if (!ring[r] && ring[r + 1]) a++;
                            if (a != 1) continue;
                            if (step == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8)) continue;
                            remove.Add(j * nx + i);
                        }
                    foreach (int k in remove) m[k] = false;
                    if (remove.Count > 0) changed = true;
                }
            }
        }

        // the 8 neighbours around a cell, in order, and the order a chain steps in: sides before corners
        static readonly Vector2Int[] Ring =
        {
            new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(1, 0), new Vector2Int(1, -1),
            new Vector2Int(0, -1), new Vector2Int(-1, -1), new Vector2Int(-1, 0), new Vector2Int(-1, 1)
        };
        static readonly Vector2Int[] Steps =
        {
            new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0),
            new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, -1), new Vector2Int(-1, 1)
        };

        // Chains of skeleton cells, from the ends along their neighbours; junctions end a chain. A cell is told by the runs of
        // skeleton cells around it (its 8 neighbours in order, counting the steps from empty to set): one at an end, two
        // along a line, three or more at a junction. Zhang-Suen leaves a slanted line as a staircase whose corner cells have
        // three neighbours, and counting neighbours broke it there into pieces shorter than MinLength (in a simulation, 28 of
        // 95 belts 10-20 m wide at 0-90 degrees lost all of them); a chain also steps to a side before a corner, so it walks
        // every cell of a staircase instead of cutting its corners and leaving them to start pieces of their own.
        static IEnumerable<List<Vector2Int>> Trace(bool[] m, int nx, int nz)
        {
            var seen = new bool[m.Length];
            bool At(int x, int z) => x >= 0 && z >= 0 && x < nx && z < nz && m[z * nx + x];
            int Runs(int i, int j)
            {
                int runs = 0;
                bool last = At(i + Ring[7].x, j + Ring[7].y);
                foreach (var d in Ring)
                {
                    bool on = At(i + d.x, j + d.y);
                    if (on && !last) runs++;
                    last = on;
                }
                return runs;
            }
            bool Next(int ci, int cj, out int bi, out int bj)
            {
                foreach (var d in Steps)
                {
                    int x = ci + d.x, z = cj + d.y;
                    if (At(x, z) && !seen[z * nx + x]) { bi = x; bj = z; return true; }
                }
                bi = bj = -1;
                return false;
            }
            // from (ci, cj) along unseen cells until a junction (the start cell itself only when it does not begin the chain)
            // or a dead end
            void Walk(int ci, int cj, List<Vector2Int> chain, bool begins)
            {
                while (true)
                {
                    seen[cj * nx + ci] = true;
                    chain.Add(new Vector2Int(ci, cj));
                    if ((chain.Count > 1 || !begins) && Runs(ci, cj) > 2) break;
                    if (!Next(ci, cj, out int bi, out int bj)) break;
                    ci = bi; cj = bj;
                }
            }
            for (int pass = 0; pass < 2; pass++)
                for (int j = 0; j < nz; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int k = j * nx + i;
                        if (!m[k] || seen[k]) continue;
                        // first from the ends, then whatever is left (segments between junctions, loops)
                        if (pass == 0 && Runs(i, j) != 1) continue;
                        var chain = new List<Vector2Int>();
                        Walk(i, j, chain, true);
                        // a start in the middle of a segment is walked both ways: one way only split it in two, often both
                        // shorter than MinLength
                        if (pass == 1 && Runs(i, j) <= 2 && Next(i, j, out int bi, out int bj))
                        {
                            var back = new List<Vector2Int>();
                            Walk(bi, bj, back, false);
                            back.Reverse();
                            back.AddRange(chain);
                            chain = back;
                        }
                        if (chain.Count >= 2) yield return chain;
                    }
        }

        static bool Near(List<Vector2> line, List<GeoTreeLine> others)
        {
            int close = 0;
            foreach (var p in line)
            {
                foreach (var other in others)
                {
                    bool hit = false;
                    var o = other.points;
                    for (int k = 1; k < o.Count && !hit; k++) hit = GeoData.SegmentDistance(p, o[k - 1], o[k], out _) < 20;
                    if (hit) { close++; break; }
                }
            }
            return close > line.Count / 2;
        }

        public static float Length(List<Vector2> line)
        {
            float length = 0;
            for (int i = 1; i < line.Count; i++) length += Vector2.Distance(line[i - 1], line[i]);
            return length;
        }
    }

    // A line of trees for the trenches: its axis and how far the trees reach from it on its left (along the left normal of
    // its direction) and on its right (a trench is dug just outside the trees).
    public sealed class GeoTreeLine
    {
        public List<Vector2> points;
        public float left, right;
    }
}
