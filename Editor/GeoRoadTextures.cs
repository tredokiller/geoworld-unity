using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Ukrainian asphalt, made rather than downloaded: no free seamless texture of a Ukrainian road exists, and a photograph
    // of one cannot be tiled. What a small-town road looks like there (see the Lyman city-entrance photograph in the app's
    // street references): grey asphalt worn into two wheel tracks, rectangular repair patches with dark sealed edges, long
    // and transverse cracks, alligator cracking, a few potholes, no markings on ordinary streets, and sandy dust creeping
    // in from the verge. The base grain is the Poly Haven asphalt (CC0) that GeoTerrainTextures converts.
    //
    // Each picture is a strip across one road: u is the width (6 m nominal), v is the length (12 m, tiles along the road).
    // Four variants run from nearly new to heavily broken, chosen per road from its wear.
    public static class GeoRoadTextures
    {
        public const string Folder = GeoPaths.Package + "/Textures/Roads";
        public const int Variants = 4, Width = 512, Height = 1024;
        public const float WidthMetres = 6f, LengthMetres = 12f;
        const int Version = 1;
        static readonly float[] WearOf = { 0.15f, 0.4f, 0.65f, 0.92f };
        static string VersionFile => Folder + "/version.txt";

        public static string DiffusePath(int variant) => Folder + "/asphalt_road_" + variant + "_diffuse.png";
        public static string NormalPath(int variant) => Folder + "/asphalt_road_" + variant + "_normal.png";
        public static bool Available => GeoPaths.Exists(VersionFile) && File.ReadAllText(GeoPaths.Disk(VersionFile)).Trim() == Version.ToString() && GeoPaths.Exists(DiffusePath(Variants - 1)) && File.Exists(NormalPath(Variants - 1));

        // Which variant a road gets: its wear (the export's, or a middling guess) with a little variety between roads.
        public static int VariantOf(string id, float wear)
        {
            if (!(wear > 0)) wear = 0.55f;
            float jitter = GeoBuildingDamage.Hash01(GeoBuildingDamage.Seed(id ?? ""), 5) - 0.5f;
            return Mathf.Clamp(Mathf.FloorToInt(wear * Variants + jitter * 1.6f), 0, Variants - 1);
        }

        [MenuItem("Tools/Geo World/Generate Ukrainian road textures")]
        public static void GenerateMenu() { Generate(); Debug.Log("[GeoWorld] road textures made in " + Folder); }

        public static void EnsureGenerated() { if (!Available) Generate(); }

        public static void Generate()
        {
            Directory.CreateDirectory(GeoPaths.Disk(Folder));
            Color32[] grain = null, grainNormal = null; int gw = 0, gh = 0;
            if (GeoTerrainTextures.HasLayer(8))
            {
                var d = Load(GeoTerrainTextures.DiffusePath(8)); var n = Load(GeoTerrainTextures.NormalPath(8));
                grain = d.GetPixels32(); grainNormal = n.GetPixels32(); gw = d.width; gh = d.height;
                UnityEngine.Object.DestroyImmediate(d); UnityEngine.Object.DestroyImmediate(n);
            }
            var written = new List<string>();
            for (int v = 0; v < Variants; v++)
            {
                Render(WearOf[v], 71 + v * 13, Width, Height, grain, grainNormal, gw, gh, out var albedo, out var normal);
                var a = new Texture2D(Width, Height, TextureFormat.RGBA32, false, false); a.SetPixels32(albedo);
                var n = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true); n.SetPixels32(normal);
                File.WriteAllBytes(GeoPaths.Disk(DiffusePath(v)), a.EncodeToPNG()); File.WriteAllBytes(GeoPaths.Disk(NormalPath(v)), n.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(n);
                written.Add(DiffusePath(v)); written.Add(NormalPath(v));
            }
            File.WriteAllText(GeoPaths.Disk(VersionFile), Version.ToString());
            AssetDatabase.Refresh();
            foreach (var path in written)
            {
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                if (importer == null) continue;
                bool isNormal = path.EndsWith("_normal.png", StringComparison.Ordinal);
                importer.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !isNormal;
                importer.mipmapEnabled = true;
                importer.wrapModeU = TextureWrapMode.Clamp; importer.wrapModeV = TextureWrapMode.Repeat;
                importer.anisoLevel = 8; importer.maxTextureSize = 1024;
                importer.SaveAndReimport();
            }
        }

        static Texture2D Load(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!texture.LoadImage(File.ReadAllBytes(path))) { UnityEngine.Object.DestroyImmediate(texture); throw new InvalidDataException("Cannot read " + path); }
            return texture;
        }

        // ------------------------------------------------------------------------------------------ the picture

        // Deterministic for a seed. Coordinates are in metres, so the picture looks the same at any resolution. It tiles along v.
        public static void Render(float wear, int seed, int w, int h, Color32[] grain, Color32[] grainNormal, int gw, int gh,
            out Color32[] albedo, out Color32[] normal)
        {
            var rng = new System.Random(seed);
            float pxX = WidthMetres / w, pxY = LengthMetres / h;
            var r = new float[w * h]; var g = new float[w * h]; var b = new float[w * h];
            var height = new float[w * h]; var nx = new float[w * h]; var ny = new float[w * h];
            var crack = new float[w * h];
            float RandomIn(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

            // 1. the base: grain, tone, wheel tracks, wear polish
            float tone = Mathf.Lerp(0.5f, 0.78f, wear);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = y * w + x; float X = (x + 0.5f) * pxX, Y = (y + 0.5f) * pxY;
                float cr = 0.38f, cg = 0.38f, cb = 0.39f, bx = 0, by = 0;
                if (grain != null)
                {
                    Sample(grain, gw, gh, X / 3f, Y / 3f, out cr, out cg, out cb);
                    Sample(grainNormal, gw, gh, X / 3f, Y / 3f, out bx, out by, out _); bx = bx * 2 - 1; by = by * 2 - 1;
                }
                else { float n = Fbm(X / WidthMetres, Y / LengthMetres, 12, 24, 4, seed); cr = cg = cb = 0.30f + n * 0.2f; }
                float large = 1 + 0.16f * (Fbm(X / WidthMetres, Y / LengthMetres, 2, 3, 3, seed + 1) - 0.5f);
                float track = Mathf.Exp(-Sq((X - 1.65f) / 0.34f)) + Mathf.Exp(-Sq((X - 4.35f) / 0.34f));
                float shade = tone * large * (1 - 0.14f * track);
                r[i] = cr * shade * 1.15f; g[i] = cg * shade * 1.15f; b[i] = cb * shade * 1.18f;
                nx[i] = bx; ny[i] = by;
            }

            // 2. repair patches: rectangles with their own grain, darker or lighter, and a dark sealed edge
            int patches = Mathf.RoundToInt(2 + 10 * wear);
            for (int k = 0; k < patches; k++)
            {
                float px = RandomIn(0.7f, 5.3f), py = RandomIn(0, LengthMetres), pw = RandomIn(0.6f, 2.6f), pl = RandomIn(0.8f, 4.2f), a = RandomIn(-0.07f, 0.07f);
                float factor = rng.NextDouble() < 0.65 ? RandomIn(0.62f, 0.85f) : RandomIn(1.08f, 1.3f);
                float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
                for (int wrap = -1; wrap <= 1; wrap++)
                {
                    float cy = py + wrap * LengthMetres;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((px - pw) / pxX)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt((px + pw) / pxX));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt((cy - pl) / pxY)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt((cy + pl) / pxY));
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                    {
                        float dx = (x + 0.5f) * pxX - px, dy = (y + 0.5f) * pxY - cy;
                        float lx = dx * cos + dy * sin, ly = -dx * sin + dy * cos;
                        float edge = Mathf.Min(pw * 0.5f - Mathf.Abs(lx), pl * 0.5f - Mathf.Abs(ly));
                        if (edge < 0) continue;
                        int i = y * w + x;
                        float grainNoise = 0.92f + 0.16f * Fbm(x / (float)w, y / (float)h, 96, 192, 2, seed + 9);
                        float f = factor * grainNoise;
                        r[i] *= f; g[i] *= f; b[i] *= f;
                        height[i] = 0.012f;
                        nx[i] *= 0.4f; ny[i] *= 0.4f;
                        if (edge < 0.014f) { r[i] *= 0.5f; g[i] *= 0.5f; b[i] *= 0.5f; height[i] = -0.012f; }
                    }
                }
            }

            // 3. cracks: long ones that wander, transverse ones, and alligator cracking in worn zones
            int longCracks = Mathf.RoundToInt(1 + 5 * wear), transverse = Mathf.RoundToInt(wear * 8);
            for (int k = 0; k < longCracks; k++)
                Crack(crack, w, h, pxX, pxY, RandomIn(0.4f, 5.6f), RandomIn(0, LengthMetres), Mathf.PI / 2 + RandomIn(-0.12f, 0.12f), RandomIn(5f, 14f), rng, 0.45f);
            for (int k = 0; k < transverse; k++)
                Crack(crack, w, h, pxX, pxY, RandomIn(0.2f, 3.0f), RandomIn(0, LengthMetres), RandomIn(-0.4f, 0.4f), RandomIn(1.5f, 5.5f), rng, 0.3f);
            int zones = wear > 0.5f ? Mathf.RoundToInt((wear - 0.4f) * 4) : 0;
            for (int k = 0; k < zones; k++) Alligator(crack, w, h, pxX, pxY, RandomIn(1.0f, 4.0f), RandomIn(0, LengthMetres), RandomIn(0.9f, 1.8f), RandomIn(1.2f, 3f), rng);

            // 4. potholes of a worn road: dark, rough, with a broken rim
            int holes = wear < 0.5f ? 0 : Mathf.RoundToInt((wear - 0.45f) * 14);
            for (int k = 0; k < holes; k++)
            {
                float hx = RandomIn(0.8f, 5.2f), hy = RandomIn(0, LengthMetres), hr = RandomIn(0.14f, 0.5f);
                for (int wrap = -1; wrap <= 1; wrap++)
                {
                    float cy = hy + wrap * LengthMetres;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((hx - hr * 1.4f) / pxX)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt((hx + hr * 1.4f) / pxX));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt((cy - hr * 1.4f) / pxY)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt((cy + hr * 1.4f) / pxY));
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                    {
                        float dx = (x + 0.5f) * pxX - hx, dy = (y + 0.5f) * pxY - cy;
                        float reach = hr * (0.7f + 0.6f * Fbm(x / (float)w, y / (float)h, 24, 48, 3, seed + 17));
                        float d = Mathf.Sqrt(dx * dx + dy * dy) / reach;
                        int i = y * w + x;
                        if (d < 1)
                        {
                            float rough = 0.5f + 0.6f * Fbm(x / (float)w, y / (float)h, 128, 256, 2, seed + 23);
                            float m = 0.72f * rough;
                            r[i] = 0.30f * m; g[i] = 0.29f * m; b[i] = 0.28f * m;
                            height[i] = -0.05f * (1 - d * d);
                            crack[i] = 0;
                        }
                        else if (d < 1.25f) { r[i] *= 1.12f; g[i] *= 1.12f; b[i] *= 1.12f; height[i] = Mathf.Max(height[i], 0.004f); }
                    }
                }
            }

            // 5. cracks darken and sink the surface
            for (int i = 0; i < r.Length; i++)
            {
                float c = Mathf.Clamp01(crack[i]);
                float keep = 1 - 0.82f * c;
                r[i] *= keep; g[i] *= keep; b[i] *= keep; height[i] -= 0.02f * c;
            }

            // 6. the verge: dust and sand creep in from both edges, ragged and heavier on a neglected road
            var dust = new Color(0.50f, 0.45f, 0.36f);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = y * w + x; float X = (x + 0.5f) * pxX, Y = (y + 0.5f) * pxY;
                float e = Mathf.Min(X, WidthMetres - X);
                float side = X < WidthMetres * 0.5f ? 0 : 1;
                float ragged = Fbm(Y / LengthMetres, side * 0.37f + 0.1f, 36, 3, 3, seed + 31);
                float band = (0.10f + 0.34f * ragged) * (0.6f + 0.8f * wear);
                float fine = Fbm(x / (float)w, y / (float)h, 90, 180, 2, seed + 37);
                float m = Mathf.Clamp01((band - e) / (band * 0.55f)) * (0.55f + 0.6f * fine);
                m = Mathf.Clamp01(m * 1.2f);
                if (m <= 0) continue;
                float shade = 0.85f + 0.3f * fine;
                r[i] = Mathf.Lerp(r[i], dust.r * shade, m * 0.9f); g[i] = Mathf.Lerp(g[i], dust.g * shade, m * 0.9f); b[i] = Mathf.Lerp(b[i], dust.b * shade, m * 0.9f);
                height[i] -= 0.01f * m;
            }

            // 7. pack: colour, and a normal map from the base normals and the height of every feature
            albedo = new Color32[w * h]; normal = new Color32[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                albedo[i] = new Color32(To8(r[i]), To8(g[i]), To8(b[i]), 255);
                int xl = Mathf.Max(0, x - 1), xr = Mathf.Min(w - 1, x + 1), yd = (y + h - 1) % h, yu = (y + 1) % h;
                float sx = (height[y * w + xr] - height[y * w + xl]) / ((xr - xl) * pxX);
                float sy = (height[yu * w + x] - height[yd * w + x]) / (2 * pxY);
                var n = new Vector3(nx[i] * 0.8f - sx * 3.2f, ny[i] * 0.8f - sy * 3.2f, 1).normalized;
                normal[i] = new Color32(To8(n.x * 0.5f + 0.5f), To8(n.y * 0.5f + 0.5f), To8(n.z * 0.5f + 0.5f), 255);
            }
        }

        static byte To8(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
        static float Sq(float v) => v * v;

        // A crack: a wandering polyline stamped into the mask, with branches. Wraps along v.
        static void Crack(float[] mask, int w, int h, float pxX, float pxY, float x, float y, float heading, float length, System.Random rng, float branch)
        {
            float step = 0.012f, walked = 0, width = 0.011f + (float)rng.NextDouble() * 0.008f;
            float turn = 0, drift = 0, direction = heading;
            while (walked < length)
            {
                // a crack wanders but keeps its general direction
                turn += ((float)rng.NextDouble() - 0.5f) * 0.4f; turn *= 0.8f;
                drift = Mathf.Clamp(drift + turn * 0.1f, -0.5f, 0.5f);
                heading = direction + drift;
                x += Mathf.Cos(heading) * step; y += Mathf.Sin(heading) * step; walked += step;
                if (x < 0.02f || x > WidthMetres - 0.02f) break;
                Stamp(mask, w, h, pxX, pxY, x, y, width * (1 - 0.5f * walked / length));
                if (rng.NextDouble() < branch * step * 1.4 && length - walked > 0.6f)
                    Crack(mask, w, h, pxX, pxY, x, y, heading + ((float)rng.NextDouble() - 0.5f) * 1.6f, Mathf.Min(1.2f, length - walked) * (float)(0.3 + rng.NextDouble() * 0.7), rng, 0);
            }
        }

        static void Stamp(float[] mask, int w, int h, float pxX, float pxY, float x, float y, float width)
        {
            float rad = Mathf.Max(0.9f, width / pxX * 0.5f + 0.5f);
            int cx = Mathf.FloorToInt(x / pxX), cy = Mathf.FloorToInt(y / pxY), n = Mathf.CeilToInt(rad);
            for (int dy = -n; dy <= n; dy++) for (int dx = -n; dx <= n; dx++)
            {
                int px = cx + dx, py = ((cy + dy) % h + h) % h;
                if (px < 0 || px >= w) continue;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float v = Mathf.Clamp01(1 - d / rad);
                int i = py * w + px; if (v > mask[i]) mask[i] = v;
            }
        }

        // Alligator cracking: the edges between the cells of a Voronoi diagram, inside a rectangle.
        static void Alligator(float[] mask, int w, int h, float pxX, float pxY, float cx, float cy, float zw, float zl, System.Random rng)
        {
            int seeds = Mathf.RoundToInt(zw * zl * 22);
            var sx = new float[seeds]; var sy = new float[seeds];
            for (int k = 0; k < seeds; k++) { sx[k] = cx + ((float)rng.NextDouble() - 0.5f) * zw; sy[k] = cy + ((float)rng.NextDouble() - 0.5f) * zl; }
            int x0 = Mathf.Max(0, Mathf.FloorToInt((cx - zw / 2) / pxX)), x1 = Mathf.Min(w - 1, Mathf.CeilToInt((cx + zw / 2) / pxX));
            int y0 = Mathf.FloorToInt((cy - zl / 2) / pxY), y1 = Mathf.CeilToInt((cy + zl / 2) / pxY);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                float X = (x + 0.5f) * pxX, Y = (y + 0.5f) * pxY;
                float best = float.MaxValue, second = float.MaxValue;
                for (int k = 0; k < seeds; k++)
                {
                    float d = Mathf.Sqrt(Sq(X - sx[k]) + Sq(Y - sy[k]));
                    if (d < best) { second = best; best = d; } else if (d < second) second = d;
                }
                float fade = Mathf.Clamp01(Mathf.Min(1, (zw / 2 - Mathf.Abs(X - cx)) / 0.25f)) * Mathf.Clamp01((zl / 2 - Mathf.Abs(Y - cy)) / 0.25f);
                float v = Mathf.Clamp01(1 - (second - best) / 0.016f) * fade;
                int py = ((y % h) + h) % h, i = py * w + x;
                if (v > mask[i]) mask[i] = v;
            }
        }

        static void Sample(Color32[] pixels, int w, int h, float u, float v, out float r, out float g, out float b)
        {
            u -= Mathf.Floor(u); v -= Mathf.Floor(v);
            float fx = u * w - 0.5f, fy = v * h - 0.5f; int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy); float tx = fx - x0, ty = fy - y0;
            Color32 P(int px, int py) => pixels[(((py % h) + h) % h) * w + (((px % w) + w) % w)];
            var a = P(x0, y0); var c = P(x0 + 1, y0); var d = P(x0, y0 + 1); var e = P(x0 + 1, y0 + 1);
            r = Mathf.Lerp(Mathf.Lerp(a.r, c.r, tx), Mathf.Lerp(d.r, e.r, tx), ty) / 255f;
            g = Mathf.Lerp(Mathf.Lerp(a.g, c.g, tx), Mathf.Lerp(d.g, e.g, tx), ty) / 255f;
            b = Mathf.Lerp(Mathf.Lerp(a.b, c.b, tx), Mathf.Lerp(d.b, e.b, tx), ty) / 255f;
        }

        // Value noise that repeats every cx x cy lattice cells over the unit square; Fbm adds octaves, doubling the lattice.
        static float Lattice(int ix, int iy, int seed)
        {
            unchecked
            {
                uint hsh = (uint)(ix * 374761393 + iy * 668265263 + seed * 2246822519u);
                hsh = (hsh ^ (hsh >> 13)) * 1274126177u; hsh ^= hsh >> 16;
                return (hsh & 0xFFFFFF) / 16777216f;
            }
        }
        static float Noise(float x, float y, int cx, int cy, int seed)
        {
            float fx = x * cx, fy = y * cy; int ix = Mathf.FloorToInt(fx), iy = Mathf.FloorToInt(fy); float tx = fx - ix, ty = fy - iy;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            int ax = ((ix % cx) + cx) % cx, bx = (ax + 1) % cx, ay = ((iy % cy) + cy) % cy, by = (ay + 1) % cy;
            return Mathf.Lerp(Mathf.Lerp(Lattice(ax, ay, seed), Lattice(bx, ay, seed), tx), Mathf.Lerp(Lattice(ax, by, seed), Lattice(bx, by, seed), tx), ty);
        }
        static float Fbm(float x, float y, int cx, int cy, int octaves, int seed)
        {
            float sum = 0, amplitude = 0.5f, total = 0;
            for (int o = 0; o < octaves; o++) { sum += Noise(x, y, cx, cy, seed + o * 31) * amplitude; total += amplitude; amplitude *= 0.5f; cx *= 2; cy *= 2; }
            return sum / total;
        }
    }
}
