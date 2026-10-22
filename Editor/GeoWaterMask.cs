using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Where the export's ground map (materials.png: ESA WorldCover at 10 m, OpenStreetMap water polygons burned in) says there is
    // water. A river that OpenStreetMap draws as a line, with the width of its kind (14 m for any "river"), is as wide as this map
    // shows it, which is also what the satellite picture on the terrain shows: that width and that middle are what the river is
    // built with, so that the water is where the picture has it. Rivers narrower than the map's resolution are not in it and keep
    // the width they have.
    public sealed class GeoWaterMask
    {
        readonly bool[] water; // row 0 is the north edge
        readonly int width, height;
        readonly float cell, minX, maxZ;

        public GeoWaterMask(bool[] water, int width, int height, float cell, float minX, float maxZ)
        { this.water = water; this.width = width; this.height = height; this.cell = cell; this.minX = minX; this.maxZ = maxZ; }

        // Null when the export has no ground map, or its map has no water class.
        public static GeoWaterMask Load(EnvironmentData env, string environmentPath)
        {
            var info = env?.materials;
            if (info == null || string.IsNullOrEmpty(info.file) || string.IsNullOrEmpty(environmentPath)) return null;
            if (info.width < 1 || info.height < 1 || (long)info.width * info.height > 17000000 || info.cellSize <= 0) return null;
            var ids = new bool[256]; bool any = false;
            foreach (var c in info.classes ?? new List<MaterialClass>())
                if (c.name == "water" && c.id >= 0 && c.id < 256) { ids[c.id] = true; any = true; }
            if (!any) return null;
            string path;
            try { path = GeoData.PackageFile(environmentPath, info.file); } catch (System.Exception) { return null; }
            var map = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!map.LoadImage(File.ReadAllBytes(path)) || map.width != info.width || map.height != info.height) return null;
                var pixels = map.GetPixels32();
                var water = new bool[pixels.Length];
                // the texture's rows start at the south; the mask's at the north
                for (int row = 0; row < info.height; row++)
                    for (int col = 0; col < info.width; col++)
                        water[row * info.width + col] = ids[pixels[(info.height - 1 - row) * info.width + col].r];
                return new GeoWaterMask(water, info.width, info.height, info.cellSize, info.minX, info.maxZ);
            }
            finally { Object.DestroyImmediate(map); }
        }

        public bool IsWater(float x, float z)
        {
            int col = Mathf.FloorToInt((x - minX) / cell), row = Mathf.FloorToInt((maxZ - z) / cell);
            return col >= 0 && col < width && row >= 0 && row < height && water[row * width + col];
        }

        // The water across a line: from the point p along the unit direction d (x, z), the stretch of water that starts nearest to p
        // within `search` metres, as offsets lo..hi along d. False where there is no water that near, and where the water goes on
        // for `reach` metres either way (a lake, the mouth of a river: not the width of a river).
        public bool Across(Vector2 p, Vector2 d, float search, float reach, out float lo, out float hi)
        {
            lo = hi = 0;
            float step = Mathf.Max(0.5f, cell * 0.5f), start = float.NaN;
            for (float t = 0; t <= search && float.IsNaN(start); t += step)
            {
                if (Water(p + d * t)) start = t;
                else if (t > 0 && Water(p - d * t)) start = -t;
            }
            if (float.IsNaN(start)) return false;
            hi = Walk(p, d, start, 1, reach, step); lo = Walk(p, d, start, -1, reach, step);
            return Mathf.Abs(hi) < reach && Mathf.Abs(lo) < reach;
        }

        bool Water(Vector2 q) => IsWater(q.x, q.y);

        // Out from `start` in one direction while there is water; a gap of up to two pixels is a speck, not the bank.
        float Walk(Vector2 p, Vector2 d, float start, int sign, float reach, float step)
        {
            float last = start, gap = 0;
            for (float t = start; Mathf.Abs(t) <= reach; t += sign * step)
            {
                if (Water(p + d * t)) { last = t; gap = 0; }
                else if ((gap += step) > 2 * cell) break;
            }
            return last;
        }
    }
}
