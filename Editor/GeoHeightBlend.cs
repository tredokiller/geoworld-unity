using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The ground of a world made of several exports. Every export has a heightmap of its own, cut from the elevation data
    // for its own area: where two exports meet their heights differ a little (the elevation data was resampled for each)
    // or by a whole datum (a different ground level at the origin), which would be a wall along the seam. So the height at
    // a point is one function of the point for the whole world, the same for every tile whichever export builds it:
    //
    //  - the exports are first brought to one datum: each is moved up or down by the median difference to the earlier
    //    export it overlaps, over the overlap (Align);
    //  - then the heights of every export that reaches the point are averaged, each weighted by how far inside its bounds
    //    the point lies plus a feather outside them (Feather metres, where its heightmap goes on with its edge value), so the
    //    ground goes over from one export to the next across the overlap instead of stepping.
    //
    // Far from a seam the point is reached by one export and the height is that export's own.
    internal sealed class GeoHeightBlend
    {
        public const float Feather = 24;
        const int MaxLoaded = 4; // a point where four exports meet

        readonly GeoTilePlan plan;
        readonly IList<GeoTiledWorld.Part> parts;
        readonly TerrainInfo[] terrain;
        readonly float[] offset;
        readonly Dictionary<int, GeoHeightSource> loaded = new Dictionary<int, GeoHeightSource>();
        readonly List<int> order = new List<int>();

        public GeoHeightBlend(GeoTilePlan plan, IList<GeoTiledWorld.Part> parts, IList<TerrainInfo> terrain)
        {
            this.plan = plan; this.parts = parts; this.terrain = terrain.ToArray();
            offset = new float[parts.Count];
        }

        // The lowest and highest ground of every export (from their heightmap files' own ranges), moved to the common datum.
        public float Low { get { float v = float.MaxValue; for (int i = 0; i < terrain.Length; i++) { var h = terrain[i]?.heightmap; if (h != null) v = Mathf.Min(v, h.minHeight - h.originGround + offset[i]); } return v == float.MaxValue ? 0 : v; } }
        public float High { get { float v = float.MinValue; for (int i = 0; i < terrain.Length; i++) { var h = terrain[i]?.heightmap; if (h != null) v = Mathf.Max(v, h.maxHeight - h.originGround + offset[i]); } return v == float.MinValue ? 0 : v; } }
        public bool HasHeights => terrain.Any(t => t?.heightmap != null && !string.IsNullOrEmpty(t.heightmap.file));
        public float Offset(int part) => offset[part];

        // An export's heightmap (null: it has none), kept while it is in use: four at a time at most, the ones used last.
        public GeoHeightSource Source(int part)
        {
            if (loaded.TryGetValue(part, out var source)) { if (order[order.Count - 1] != part) { order.Remove(part); order.Add(part); } return source; }
            source = GeoHeightSource.Load(new EnvironmentData { terrain = terrain[part] }, parts[part].environmentPath);
            loaded[part] = source; order.Add(part);
            while (order.Count > MaxLoaded) { loaded.Remove(order[0]); order.RemoveAt(0); }
            return source;
        }

        // How far inside the export's bounds the point is (negative outside), and so its weight: the feather beyond them.
        static float Inside(GeoBounds b, float x, float z) => Mathf.Min(Mathf.Min(x - b.minX, b.maxX - x), Mathf.Min(z - b.minZ, b.maxZ - z));

        public float Sample(float x, float z)
        {
            float sum = 0, total = 0, best = float.MinValue;
            int nearest = -1;
            for (int i = 0; i < terrain.Length; i++)
            {
                if (terrain[i]?.heightmap == null) continue;
                float inside = Inside(plan.bounds[i], x, z);
                if (inside > best) { best = inside; nearest = i; }
                float w = Feather + inside;
                if (w <= 0) continue;
                var source = Source(i);
                if (source == null) continue;
                sum += w * (source.Sample(x, z) + offset[i]); total += w;
            }
            if (total > 0) return sum / total;
            // beyond every feather: the nearest export's edge value
            return nearest < 0 ? 0 : (Source(nearest)?.Sample(x, z) ?? 0) + offset[nearest];
        }

        // Moves each export to the datum of the earlier ones: by the median difference over the overlap with the first earlier
        // export that overlaps it. Differences under 2 cm are noise and are left.
        public void Align()
        {
            for (int i = 1; i < parts.Count; i++)
            {
                offset[i] = 0;
                if (terrain[i]?.heightmap == null) continue;
                for (int j = 0; j < i; j++)
                {
                    if (terrain[j]?.heightmap == null) continue;
                    var cut = GeoTilePlan.Intersection(plan.bounds[i], plan.bounds[j]);
                    if (cut == null) continue;
                    var a = Source(i); var b = Source(j);
                    if (a == null || b == null) continue;
                    var differences = new List<float>();
                    int nx = Mathf.Clamp(Mathf.CeilToInt(cut.Width / 4), 2, 24), nz = Mathf.Clamp(Mathf.CeilToInt(cut.Depth / 4), 2, 24);
                    for (int z = 0; z < nz; z++)
                        for (int x = 0; x < nx; x++)
                        {
                            float px = Mathf.Lerp(cut.minX, cut.maxX, (x + 0.5f) / nx), pz = Mathf.Lerp(cut.minZ, cut.maxZ, (z + 0.5f) / nz);
                            differences.Add(b.Sample(px, pz) + offset[j] - a.Sample(px, pz));
                        }
                    differences.Sort();
                    float median = differences[differences.Count / 2];
                    offset[i] = Mathf.Abs(median) < 0.02f ? 0 : median;
                    break;
                }
            }
        }

        // What the ground of an export depends on besides its own files: the heightmaps of the exports that reach it, and the
        // datum they were moved to. A tile is rebuilt when this changes.
        public string Fingerprint(int part)
        {
            var text = new StringBuilder();
            var around = plan.bounds[part];
            for (int i = 0; i < parts.Count; i++)
            {
                var b = plan.bounds[i];
                if (b.minX > around.maxX + Feather || b.maxX < around.minX - Feather || b.minZ > around.maxZ + Feather || b.maxZ < around.minZ - Feather) continue;
                text.Append(i).Append(':').Append(offset[i].ToString("F2")).Append(':');
                string heightmap = terrain[i]?.heightmap?.file;
                foreach (var file in new[] { parts[i].environmentPath, string.IsNullOrEmpty(heightmap) ? null : GeoData.PackageFile(parts[i].environmentPath, heightmap) })
                    if (!string.IsNullOrEmpty(file) && File.Exists(file)) { var info = new FileInfo(file); text.Append(info.Name).Append(info.Length).Append(info.LastWriteTimeUtc.Ticks).Append(','); }
                text.Append('|');
            }
            return text.ToString();
        }
    }
}
