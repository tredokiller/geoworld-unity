using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // What a home model covers seen from above, in its own space (X along its long side, Z towards its front): the box
    // around everything it draws with the roof a building gives it, and that box's shape as a few rectangles (the main
    // block, a wing, a porch, a summer kitchen in line). A model's length and width are its main block's walls only, and
    // most models reach past them: of the 38 in the library 28 have a porch or veranda 1.5 m or more in front of the walls,
    // N12's summer kitchen runs 5.6 m past its gable. A footprint traced from the imagery holds all of it (the outline is
    // the roof seen from above), so a model is fitted by its plan (GeoHomes.Measure): stretched by its walls it reached into
    // the neighbour's yard, onto the street and through the next house.
    public sealed class GeoHomePlan
    {
        // the box around the plan, x along the model's X and y along its Z
        public Rect bounds;
        // disjoint rectangles covering at least 98 % of the plan, the largest first
        public Rect[] rects;
        // the plan's area in square metres
        public float area;

        public Vector2 Size => bounds.size;

        static readonly Dictionary<GeoHomeModelAsset, Dictionary<string, GeoHomePlan>> cache = new Dictionary<GeoHomeModelAsset, Dictionary<string, GeoHomePlan>>();
        static readonly Dictionary<Mesh, GeoHomePlan> meshes = new Dictionary<Mesh, GeoHomePlan>();

        public static void ClearCache() { cache.Clear(); meshes.Clear(); }

        // The plan of a model with one of its roof variants (its default one when it has no such variant).
        public static GeoHomePlan Of(GeoHomeModelAsset model, string variant)
        {
            if (!model) return null;
            if (!cache.TryGetValue(model, out var plans)) cache[model] = plans = new Dictionary<string, GeoHomePlan>();
            var roof = model.Variant(variant);
            if (!roof) { variant = model.defaultVariant; roof = model.Variant(variant); }
            string key = variant ?? "";
            if (plans.TryGetValue(key, out var plan)) return plan;
            plan = Measure(new[] { model.body, model.roofFixed, roof }, model);
            plans[key] = plan;
            return plan;
        }

        // The plan of one mesh (a part of a home as it was placed: GeoHomeSet).
        public static GeoHomePlan Of(Mesh mesh)
        {
            if (!mesh) return null;
            if (meshes.TryGetValue(mesh, out var plan)) return plan;
            plan = Measure(new[] { mesh }, null);
            meshes[mesh] = plan;
            return plan;
        }

        // The model's main block when its meshes say nothing (none readable): the walls' rectangle, as before plans.
        static GeoHomePlan Walls(GeoHomeModelAsset model)
        {
            float l = model ? Mathf.Max(0.5f, model.length) : 1, w = model ? Mathf.Max(0.5f, model.width) : 1;
            var box = new Rect(-l / 2, -w / 2, l, w);
            return new GeoHomePlan { bounds = box, rects = new[] { box }, area = l * w };
        }

        // Rasterises the meshes from above: every triangle's edges (a wall is a line seen from above) and the inside of every
        // triangle that is not upright (roofs, floors, porch decks), then fills what the walls enclose, and splits the result
        // into rectangles as GeoRuins.Parts splits a footprint.
        static GeoHomePlan Measure(Mesh[] parts, GeoHomeModelAsset model)
        {
            var points = new List<Vector2>();
            var triangles = new List<int>();
            foreach (var mesh in parts)
            {
                if (!mesh || !mesh.isReadable) continue;
                int first = points.Count;
                foreach (var v in mesh.vertices) points.Add(new Vector2(v.x, v.z));
                foreach (int t in mesh.triangles) triangles.Add(first + t);
            }
            if (points.Count < 3 || triangles.Count < 3) return Walls(model);
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (int t in triangles)
            {
                var p = points[t];
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
            }
            if (!(maxX - minX > 0.1f) || !(maxZ - minZ > 0.1f) || maxX - minX > 500 || maxZ - minZ > 500) return Walls(model);
            float cell = Mathf.Max(0.15f, Mathf.Max(maxX - minX, maxZ - minZ) / 400f);
            // one empty cell all round, so the outside is one connected region
            var origin = new Vector2(minX - cell, minZ - cell);
            int nx = Mathf.CeilToInt((maxX - minX) / cell) + 3, nz = Mathf.CeilToInt((maxZ - minZ) / cell) + 3;
            var solid = new bool[nx * nz];
            void Mark(Vector2 p)
            {
                int i = Mathf.Clamp(Mathf.FloorToInt((p.x - origin.x) / cell), 0, nx - 1), j = Mathf.Clamp(Mathf.FloorToInt((p.y - origin.y) / cell), 0, nz - 1);
                solid[j * nx + i] = true;
            }
            void Line(Vector2 a, Vector2 b)
            {
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / (cell * 0.5f)));
                for (int k = 0; k <= steps; k++) Mark(Vector2.Lerp(a, b, k / (float)steps));
            }
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                Vector2 a = points[triangles[t]], b = points[triangles[t + 1]], c = points[triangles[t + 2]];
                Line(a, b); Line(b, c); Line(c, a);
                float twice = GeoPolygon.Cross(b - a, c - a);
                if (Mathf.Abs(twice) < cell * cell * 0.05f) continue;
                int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - origin.x) / cell));
                int i1 = Mathf.Min(nx - 1, Mathf.FloorToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - origin.x) / cell));
                int j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - origin.y) / cell));
                int j1 = Mathf.Min(nz - 1, Mathf.FloorToInt((Mathf.Max(a.y, Mathf.Max(b.y, c.y)) - origin.y) / cell));
                float sign = Mathf.Sign(twice);
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        var p = origin + new Vector2((i + 0.5f) * cell, (j + 0.5f) * cell);
                        if (GeoPolygon.Cross(b - a, p - a) * sign >= 0 && GeoPolygon.Cross(c - b, p - b) * sign >= 0 && GeoPolygon.Cross(a - c, p - c) * sign >= 0)
                            solid[j * nx + i] = true;
                    }
            }
            // what no path from the border reaches is inside the walls
            var outside = new bool[nx * nz];
            var queue = new Queue<int>();
            outside[0] = true; queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int at = queue.Dequeue(), i = at % nx, j = at / nx;
                void Visit(int ni, int nj)
                {
                    if (ni < 0 || nj < 0 || ni >= nx || nj >= nz) return;
                    int n = nj * nx + ni;
                    if (outside[n] || solid[n]) return;
                    outside[n] = true; queue.Enqueue(n);
                }
                Visit(i - 1, j); Visit(i + 1, j); Visit(i, j - 1); Visit(i, j + 1);
            }
            var inside = new bool[nx * nz];
            int count = 0;
            for (int k = 0; k < inside.Length; k++) if (!outside[k]) { inside[k] = true; count++; }
            if (count == 0) return Walls(model);
            // the box from the vertices themselves (the raster's cells reach up to a cell past them)
            var bounds = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            var plan = new GeoHomePlan { bounds = bounds, area = Mathf.Min(count * cell * cell, bounds.width * bounds.height) };
            var free = (bool[])inside.Clone();
            var rects = new List<Rect>();
            int left = count;
            while (rects.Count < 8 && left > count * 0.02f)
            {
                var r = GeoRuins.LargestRectangle(free, nx, nz);
                if (Mathf.Min(r.width, r.height) < 3 || r.width * r.height < count * 0.01f) break;
                for (int j = r.yMin; j < r.yMax; j++) for (int i = r.xMin; i < r.xMax; i++) free[j * nx + i] = false;
                left -= r.width * r.height;
                var rect = Rect.MinMaxRect(Mathf.Max(bounds.xMin, origin.x + r.xMin * cell), Mathf.Max(bounds.yMin, origin.y + r.yMin * cell),
                    Mathf.Min(bounds.xMax, origin.x + r.xMax * cell), Mathf.Min(bounds.yMax, origin.y + r.yMax * cell));
                if (rect.width > 0 && rect.height > 0) rects.Add(rect);
            }
            plan.rects = rects.Count > 0 ? rects.ToArray() : new[] { bounds };
            return plan;
        }
    }
}
