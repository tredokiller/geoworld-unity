using System.Collections.Generic;
using System.IO;
using _Game.Scripts.World;
using GeoWorld;
using UnityEditor;
using UnityEngine;

namespace _Game.Scripts.Editor
{
    /// <summary>
    /// Собирает набор фактуры земли (Resources/Terrain/GroundDetailSet, GroundDetail) из Environment/Textures/Ground пакета: по
    /// слою — <имя>_diff.jpg (sRGB, DXT1), <имя>_nor_gl.jpg (карта нормалей, BC5) и <имя>_mask.jpg (линейная, DXT1; маски
    /// пакует Tools~/GroundTextures/pack_ground_masks.py), все 2048 с мипами — из них при старте собираются массивы текстур,
    /// поэтому размер и сжатие у всех одни. Считает средний цвет альбедо (рисунок — отношение к нему) и заполняет слои
    /// классов земли. Подстройка уже собранного набора (повтор, контраст, нормали, AO, слои классов) сохраняется.
    /// </summary>
    public static class GroundDetailSetBuilder
    {
        private const string Root = "Packages/com.tredokiller.geoworld/Environment/Textures/Ground/";
        private const string SetPath = "Packages/com.tredokiller.geoworld/Environment/Resources/Terrain/GroundDetailSet.asset";
        private const int Size = 2048;

        private readonly struct LayerSpec
        {
            public readonly string Name;
            public readonly string Folder;
            public readonly float TileMetres;
            public readonly float Contrast;

            public LayerSpec(string name, string folder, float tileMetres, float contrast = 1f)
            {
                Name = name;
                Folder = folder;
                TileMetres = tileMetres;
                Contrast = contrast;
            }
        }

        // Повтор — по данным Poly Haven (CREDITS.txt); у набора Unity — на глаз по рисунку. Номера слоёв — в таблице классов
        // ниже и в уже собранном наборе (его таблица сохраняется), поэтому новые слои — только в конец, а замена — на месте.
        // Последние три — лесная подстилка (ForestFloor): осенний лист, пёстрый лист, мох; forest_litter — её основной слой.
        private static readonly LayerSpec[] Layers =
        {
            new("meadow", "UnityTerrainPack/Grass_A", 3f),
            new("meadow_dry", "PolyHaven/grass_ground", 2.5f),
            new("grass_soil", "UnityTerrainPack/Grass_Soil", 3f),
            new("moss", "UnityTerrainPack/Grass_Moss", 3f),
            new("furrows", "PolyHaven/farm_furrows", 2.1f, 0.7f),
            new("field_dry", "PolyHaven/dry_mud_field_001", 3f),
            new("forest_litter", "PolyHaven/forest_leaves_04", 1.5f),
            new("pine_floor", "PolyHaven/forrest_ground_03", 2f),
            new("mud", "PolyHaven/brown_mud_03", 1.3f),
            new("bank", "PolyHaven/brown_mud_rocks_01", 1.3f),
            new("dark_soil", "UnityTerrainPack/Muddy", 3f),
            new("gravel", "PolyHaven/gravel_road", 2f),
            new("asphalt", "PolyHaven/asphalt_02", 3f),
            new("paving", "PolyHaven/concrete_pavement_02", 1.8f, 0.8f),
            new("sand", "UnityTerrainPack/Sand", 3f),
            new("rock", "UnityTerrainPack/Rock", 4f),
            new("pebbles", "UnityTerrainPack/Pebbles_C", 2f),
            new("leaves_autumn", "PolyHaven/dry_decay_leaves", 2f),
            new("leaves_mixed", "PolyHaven/leaves_forest_ground", 1.26f),
            new("forest_moss", "PolyHaven/forest_leaves_02", 3f)
        };

        // По GeoLandClass: слои зелёный, голый тёмный, голый светлый (индексы в Layers).
        private static readonly int[,] ClassLayers =
        {
            { 0, 2, 1 },    // none
            { 0, 2, 1 },    // grass: луг; голое — дёрн с почвой; светлое — сухая трава
            { 2, 4, 5 },    // cropland: всходы на почве; пашня; стерня, сухое поле
            { 6, 6, 7 },    // forest: лесная подстилка; светлое — хвоя
            { 0, 2, 1 },    // scrub
            { 2, 10, 5 },   // bare
            { 1, 14, 14 },  // sand
            { 3, 15, 15 },  // rock
            { 3, 8, 1 },    // wetland: мох и сырая трава; грязь; сухой тростник
            { 3, 9, 9 },    // water: дно и берег у воды
            { 0, 2, 13 },   // urban: газоны, огороды, плитка дворов
            { 0, 12, 12 },  // asphalt (зелёное — обочины в той же клетке)
            { 0, 13, 13 },  // paving
            { 1, 11, 11 },  // gravel
            { 1, 10, 5 },   // dirt_road
            { 0, 10, 5 },   // path
            { 1, 16, 16 },  // rail
            { 0, 13, 13 },  // building
            { 1, 5, 5 },    // snow
            { 0, 2, 2 }     // orchard
        };

        [MenuItem("Tools/Environment/Build Ground Detail Set")]
        public static void BuildMenu() => Debug.Log("[GroundDetailSetBuilder] " + Build());

        public static string Build()
        {
            GroundDetailSet set = AssetDatabase.LoadAssetAtPath<GroundDetailSet>(SetPath);
            bool created = set == null;

            if (created)
                set = ScriptableObject.CreateInstance<GroundDetailSet>();

            Dictionary<string, GroundDetailSet.Layer> previous = new();

            foreach (GroundDetailSet.Layer layer in set.layers)
            {
                if (layer != null && !string.IsNullOrEmpty(layer.name))
                    previous[layer.name] = layer;
            }

            List<GroundDetailSet.Layer> layers = new();
            List<string> problems = new();

            try
            {
                for (int i = 0; i < Layers.Length; i++)
                {
                    LayerSpec spec = Layers[i];
                    EditorUtility.DisplayProgressBar("Ground detail set", spec.Name, (float)i / Layers.Length);
                    string name = Path.GetFileName(spec.Folder);
                    string folder = Root + spec.Folder + "/" + name;
                    string albedoPath = folder + "_diff.jpg";
                    string normalPath = folder + "_nor_gl.jpg";
                    string maskPath = folder + "_mask.jpg";

                    if (!File.Exists(albedoPath) || !File.Exists(normalPath) || !File.Exists(maskPath))
                    {
                        problems.Add(spec.Name + ": missing " + folder + "_diff/_nor_gl/_mask.jpg");
                        continue;
                    }

                    Configure(albedoPath, TextureImporterType.Default, true, TextureImporterCompression.Compressed);
                    Configure(normalPath, TextureImporterType.NormalMap, false, TextureImporterCompression.CompressedHQ);
                    Configure(maskPath, TextureImporterType.Default, false, TextureImporterCompression.Compressed);

                    GroundDetailSet.Layer layer = previous.TryGetValue(spec.Name, out GroundDetailSet.Layer old)
                        ? old
                        : new GroundDetailSet.Layer { name = spec.Name, tileMetres = spec.TileMetres, contrast = spec.Contrast };
                    layer.albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
                    layer.normal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
                    layer.mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
                    layer.average = AverageLinear(albedoPath);
                    layers.Add(layer);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            set.layers = layers;

            if (set.classes == null || set.classes.Length != GeoLandCover.ClassCount)
                set.classes = new GroundDetailSet.ClassLayers[GeoLandCover.ClassCount];

            for (int i = 0; i < set.classes.Length; i++)
            {
                if (set.classes[i] != null && !string.IsNullOrEmpty(set.classes[i].landClass))
                    continue;

                set.classes[i] = new GroundDetailSet.ClassLayers
                {
                    landClass = GeoLandCover.NameOf((GeoLandClass)i),
                    green = ClassLayers[i, 0],
                    bareDark = ClassLayers[i, 1],
                    bareLight = ClassLayers[i, 2]
                };
            }

            if (created)
                AssetDatabase.CreateAsset(set, SetPath);
            else
                EditorUtility.SetDirty(set);

            AssetDatabase.SaveAssetIfDirty(set);
            CheckFormats(set, problems);
            return $"{layers.Count} layers in {SetPath}" + (problems.Count > 0 ? "\n" + string.Join("\n", problems) : "");
        }

        private static void Configure(string path, TextureImporterType type, bool sRGB, TextureImporterCompression compression)
        {
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);

            if (importer.textureType == type && importer.sRGBTexture == sRGB && importer.textureCompression == compression &&
                importer.maxTextureSize == Size && importer.mipmapEnabled && !importer.isReadable && !importer.crunchedCompression &&
                importer.wrapMode == TextureWrapMode.Repeat && importer.anisoLevel == 8)
                return;

            importer.textureType = type;
            importer.sRGBTexture = sRGB;
            importer.textureCompression = compression;
            importer.crunchedCompression = false;
            importer.maxTextureSize = Size;
            importer.mipmapEnabled = true;
            importer.isReadable = false;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.anisoLevel = 8;
            importer.SaveAndReimport();
        }

        // Средний цвет картинки в линейном пространстве — так его делит шейдер (альбедо sRGB читается линейным).
        private static Color AverageLinear(string path)
        {
            Texture2D image = new(2, 2, TextureFormat.RGBA32, false, true);

            try
            {
                image.LoadImage(File.ReadAllBytes(path));
                float[] linear = new float[256];

                for (int i = 0; i < linear.Length; i++)
                    linear[i] = Mathf.GammaToLinearSpace(i / 255f);

                Color32[] pixels = image.GetPixels32();
                double r = 0, g = 0, b = 0;

                foreach (Color32 pixel in pixels)
                {
                    r += linear[pixel.r];
                    g += linear[pixel.g];
                    b += linear[pixel.b];
                }

                return new Color((float)(r / pixels.Length), (float)(g / pixels.Length), (float)(b / pixels.Length), 1f);
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        // Массивы собираются копией на GPU: у всех текстур одной роли должны совпасть размер, формат и число мипов.
        private static void CheckFormats(GroundDetailSet set, List<string> problems)
        {
            if (set.layers.Count == 0)
                return;

            foreach (System.Func<GroundDetailSet.Layer, Texture2D> pick in new System.Func<GroundDetailSet.Layer, Texture2D>[]
                     { layer => layer.albedo, layer => layer.normal, layer => layer.mask })
            {
                Texture2D first = pick(set.layers[0]);

                foreach (GroundDetailSet.Layer layer in set.layers)
                {
                    Texture2D texture = pick(layer);

                    if (texture == null || texture.width != first.width || texture.height != first.height ||
                        texture.graphicsFormat != first.graphicsFormat || texture.mipmapCount != first.mipmapCount)
                        problems.Add($"{layer.name}: {(texture != null ? texture.name + " " + texture.width + " " + texture.graphicsFormat : "none")} differs from {first.name} {first.width} {first.graphicsFormat}");
                }
            }
        }
    }
}
