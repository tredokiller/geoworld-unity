using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // How the ruin and home models' textures are kept in the package, which every project downloads: colour as JPEG
    // (quality 92: about 1/255 from the original at a fifth of the size; Unity compresses textures again when it
    // imports them anyway), at most 2048 px, the size they are imported at. Normal maps stay lossless (JPEG turns them
    // by several degrees), as PNG of at most 1024 px with the blue channel flat: Unity rebuilds blue from red and green.
    // The 2,000 px source textures of a library were 950 MB; written like this they are about a quarter of that.
    public static class GeoTextureFiles
    {
        public const int MaxSize = 2048, NormalMaxSize = 1024, JpegQuality = 92;

        public static string AlbedoName(string id) => id + "_albedo.jpg";
        public static string NormalName(string id) => id + "_normal.png";

        // Writes a texture from a source image (PNG or JPEG) to `to` (.jpg colour, .png normal map).
        public static void Write(string from, string to, bool normal)
        {
            var source = new Texture2D(2, 2, TextureFormat.RGB24, false);
            Texture2D result = null;
            try
            {
                if (!source.LoadImage(File.ReadAllBytes(from))) throw new InvalidDataException("Cannot read " + from);
                var pixels = source.GetPixels32();
                int width = source.width, height = source.height, limit = normal ? NormalMaxSize : MaxSize;
                while ((width > limit || height > limit) && width % 2 == 0 && height % 2 == 0)
                {
                    pixels = Half(pixels, width, height);
                    width /= 2; height /= 2;
                }
                if (normal) for (int i = 0; i < pixels.Length; i++) pixels[i].b = 255;
                result = new Texture2D(width, height, TextureFormat.RGB24, false);
                result.SetPixels32(pixels);
                result.Apply(false);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to)));
                File.WriteAllBytes(to, normal ? result.EncodeToPNG() : result.EncodeToJPG(JpegQuality));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                if (result) UnityEngine.Object.DestroyImmediate(result);
            }
        }

        // True when `to` is missing or older than `from`.
        public static bool Stale(string from, string to) => !File.Exists(to) || File.GetLastWriteTimeUtc(to) < File.GetLastWriteTimeUtc(from);

        // Import settings of a model texture: a normal map (linear, rebuilt from red and green) or colour (sRGB).
        public static void Configure(string path, bool clamp)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;
            bool normal = path.EndsWith("_normal.png", StringComparison.Ordinal);
            var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            var wrap = clamp ? TextureWrapMode.Clamp : importer.wrapMode;
            if (importer.textureType == type && importer.sRGBTexture == !normal && importer.maxTextureSize == MaxSize && importer.anisoLevel == 4 &&
                importer.mipmapEnabled && importer.wrapMode == wrap) return;
            importer.textureType = type;
            importer.sRGBTexture = !normal;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.mipmapEnabled = true;
            importer.wrapMode = wrap;
            importer.anisoLevel = 4;
            importer.maxTextureSize = MaxSize;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }

        // Rewrites the PNG textures of an imported library (folder per model) in the form above, keeping each
        // texture's GUID (its .meta moves with it), so materials and generated worlds keep their references.
        public static (long before, long after) OptimizeLibrary(string folder, bool clamp)
        {
            long before = 0, after = 0;
            foreach (var png in Directory.GetFiles(folder, "*.png", SearchOption.AllDirectories))
            {
                string path = png.Replace('\\', '/');
                bool normal = path.EndsWith("_normal.png", StringComparison.Ordinal);
                bool albedo = path.EndsWith("_albedo.png", StringComparison.Ordinal);
                if (!normal && !albedo) continue;
                before += new FileInfo(path).Length;
                if (normal)
                {
                    string temp = path + ".tmp";
                    Write(path, temp, true);
                    File.Copy(temp, path, true); File.Delete(temp);
                    after += new FileInfo(path).Length;
                }
                else
                {
                    string jpg = path.Substring(0, path.Length - 4) + ".jpg";
                    Write(path, jpg, false);
                    if (File.Exists(path + ".meta")) File.Move(path + ".meta", jpg + ".meta");
                    File.Delete(path);
                    after += new FileInfo(jpg).Length;
                }
            }
            AssetDatabase.Refresh();
            // folder is an asset path (Assets/... or an embedded package's Packages/...), relative to the project like the
            // editor's working directory, so the files found are asset paths too
            foreach (var file in Directory.GetFiles(folder, "*_*.*", SearchOption.AllDirectories))
            {
                string path = file.Replace('\\', '/');
                if (path.EndsWith("_normal.png", StringComparison.Ordinal) || path.EndsWith("_albedo.jpg", StringComparison.Ordinal))
                    Configure(path, clamp);
            }
            return (before, after);
        }

        static Color32[] Half(Color32[] pixels, int width, int height)
        {
            int w = width / 2, h = height / 2;
            var result = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color32 a = pixels[2 * y * width + 2 * x], b = pixels[2 * y * width + 2 * x + 1];
                    Color32 c = pixels[(2 * y + 1) * width + 2 * x], d = pixels[(2 * y + 1) * width + 2 * x + 1];
                    result[y * w + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) / 4), (byte)((a.g + b.g + c.g + d.g + 2) / 4),
                                                   (byte)((a.b + b.b + c.b + d.b + 2) / 4), 255);
                }
            return result;
        }
    }
}
