using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Photographic ground textures (Poly Haven, CC0), the set made for the Godot terrain generator. That project stores
    // each as two packed pictures: <name>_alb_ht.png (albedo RGB, height A) and <name>_nrm_rgh.png (OpenGL normal RGB,
    // roughness A). A Unity terrain layer wants the albedo with smoothness in alpha, and a normal map, so this converts
    // them once into Assets/GeoWorld/Textures/Terrain and the generator uses them in place of the plain colours.
    public static class GeoTerrainTextures
    {
        public const string Folder = GeoPaths.Package + "/Textures/Terrain";
        public const string DefaultSource = "F:/GodotProjects/Terrain Creator/terrain-generator/textures/terrain";

        // Terrain layer (GeoTerrain.LayerNames) -> texture name and metres per tile (from the Godot project's tiles.json).
        // Layer 9, the road paint, stays a plain colour.
        public static readonly string[] Names = { "grass", "forest", "bare", "sand", "gravel", "paving", "cropland", "rock", "asphalt", null, "urban", "dirt_road" };
        static readonly float[] TileMetres = { 3f, 3f, 3f, 2.25f, 2f, 1.8f, 3.15f, 2.7f, 3f, 1f, 2f, 3.1f };
        // How much colour each picture keeps when converted: the gravel road picture is orange and, blended along every
        // road shoulder, drew orange smears.
        static readonly float[] Saturation = { 1f, 1f, 1f, 1f, 0.3f, 1f, 1f, 1f, 1f, 1f, 1f, 0.6f };
        // Per-layer multiplier on the albedo (TerrainLayer diffuse remap): the pictures are autumn-dry, forest floor
        // and lawns are pulled towards green.
        public static Vector4 Remap(int layer) => layer == 0 ? new Vector4(0.8f, 1f, 0.7f, 1f) : layer == 1 ? new Vector4(0.62f, 1f, 0.55f, 1f) :
            layer == 8 ? new Vector4(0.58f, 0.58f, 0.58f, 1f) : Vector4.one;
        // The asphalt is darkened and its cracks are softened (a lifted floor): its cracks are far too bold at a few metres a tile.
        public static Vector4 RemapMin(int layer) => layer == 8 ? new Vector4(0.2f, 0.2f, 0.2f, 0f) : Vector4.zero;
        const int Version = 3; // raise it to convert again after changing the tables above
        static string Stamp => Folder + "/version.txt";

        public static string DiffusePath(int layer) => Folder + "/" + Names[layer] + "_diffuse.png";
        public static string NormalPath(int layer) => Folder + "/" + Names[layer] + "_normal.png";
        public static float Tile(int layer) => TileMetres[layer];
        public static bool HasLayer(int layer) => layer >= 0 && layer < Names.Length && Names[layer] != null && GeoPaths.Exists(DiffusePath(layer)) && GeoPaths.Exists(NormalPath(layer));
        public static bool Available => HasLayer(0) && HasLayer(11) && GeoPaths.Exists(Stamp) && File.ReadAllText(GeoPaths.Disk(Stamp)).Trim() == Version.ToString();

        [MenuItem("Tools/Geo World/Import ground textures (from the Godot project)")]
        public static void ImportMenu()
        {
            string source = DefaultSource;
            if (!Directory.Exists(source)) source = EditorUtility.OpenFolderPanel("Folder with the *_alb_ht.png and *_nrm_rgh.png ground textures", "", "");
            if (string.IsNullOrEmpty(source)) return;
            Debug.Log("[GeoWorld] ground textures: " + Import(source) + " converted from " + source);
        }

        // Called before generating: converts the textures when they are missing and the source is there.
        public static void EnsureImported()
        {
            if (Available || !Directory.Exists(DefaultSource)) return;
            Import(DefaultSource);
        }

        // Returns the number of textures converted.
        public static int Import(string source)
        {
            Directory.CreateDirectory(GeoPaths.Disk(Folder));
            int count = 0;
            var written = new List<string>();
            try
            {
                for (int i = 0; i < Names.Length; i++)
                {
                    if (Names[i] == null) continue;
                    string albedo = Path.Combine(source, Names[i] + "_alb_ht.png"), normal = Path.Combine(source, Names[i] + "_nrm_rgh.png");
                    if (!File.Exists(albedo) || !File.Exists(normal)) continue;
                    var a = Load(albedo); var n = Load(normal);
                    try
                    {
                        if (a.width != n.width || a.height != n.height) throw new InvalidDataException(Names[i] + ": the two pictures differ in size.");
                        var ap = a.GetPixels32(); var np = n.GetPixels32();
                        float keep = Saturation[i];
                        for (int k = 0; k < ap.Length; k++)
                        {
                            if (keep < 1f)
                            {
                                float luma = 0.299f * ap[k].r + 0.587f * ap[k].g + 0.114f * ap[k].b;
                                ap[k].r = (byte)Mathf.Clamp(luma + (ap[k].r - luma) * keep, 0, 255);
                                ap[k].g = (byte)Mathf.Clamp(luma + (ap[k].g - luma) * keep, 0, 255);
                                ap[k].b = (byte)Mathf.Clamp(luma + (ap[k].b - luma) * keep, 0, 255);
                            }
                            ap[k].a = (byte)(255 - np[k].a); // smoothness = 1 - roughness
                            np[k].a = 255;
                        }
                        a.SetPixels32(ap); n.SetPixels32(np);
                        File.WriteAllBytes(GeoPaths.Disk(DiffusePath(i)), a.EncodeToPNG());
                        File.WriteAllBytes(GeoPaths.Disk(NormalPath(i)), n.EncodeToPNG());
                        written.Add(DiffusePath(i)); written.Add(NormalPath(i));
                        count++;
                    }
                    finally { UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(n); }
                }
                string credits = Path.Combine(source, "CREDITS.txt");
                if (File.Exists(credits)) File.Copy(credits, GeoPaths.Disk(Folder + "/CREDITS.txt"), true);
                File.WriteAllText(GeoPaths.Disk(Stamp), Version.ToString());
                AssetDatabase.Refresh();
                foreach (var path in written)
                {
                    var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                    if (importer == null) continue;
                    bool isNormal = path.EndsWith("_normal.png", StringComparison.Ordinal);
                    importer.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    importer.sRGBTexture = !isNormal;
                    importer.alphaSource = isNormal ? TextureImporterAlphaSource.None : TextureImporterAlphaSource.FromInput;
                    importer.alphaIsTransparency = false;
                    importer.mipmapEnabled = true;
                    importer.wrapMode = TextureWrapMode.Repeat;
                    importer.anisoLevel = 8;
                    importer.maxTextureSize = 1024;
                    importer.SaveAndReimport();
                }
            }
            catch { foreach (var p in written) AssetDatabase.DeleteAsset(p); throw; }
            return count;
        }

        static Texture2D Load(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!texture.LoadImage(File.ReadAllBytes(path))) { UnityEngine.Object.DestroyImmediate(texture); throw new InvalidDataException("Cannot read " + path); }
            return texture;
        }
    }
}
