using System.Collections.Generic;
using System.IO;
using System.Linq;
using _Game.Scripts.World;
using GeoWorld;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>
    /// Породы деревьев по месту. Контуры лесов с типом листвы (TreeLeafAreas) — свои у каждого мира: из его выгрузок
    /// (environment.json частей мира, areas[].leafType: OSM leaf_type — пакет его не переносит в тайлы) в папку мира
    /// (WorldMapBaker: &lt;папка мира&gt;/Environment/Tree leaf areas, на его WorldMapData). Общий профиль видов
    /// (Environment/Profiles/TreeSpeciesProfile, TileTreeSpecies) — виды с вариантами: сосны набора и сгенерированный дуб
    /// (Environment/Vegetation/Generated/PineSet, Oak), бук и тополь NatureManufacture — обновляет Update Tree Species
    /// Profile; он пишет в пакет, так что это инструмент разработки пакета. Смеси по месту задаются при первом создании
    /// профиля; потом их правят в инспекторе, а обновление меняет только варианты видов.
    /// </summary>
    public static class TreeLeafAreasBaker
    {
        private const string ProfilePath = GeoAddressableAssets.TreeSpeciesProfile;
        private const string AssetName = "Tree leaf areas";
        private const string Generated = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Generated";
        private const string Vegetation = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Prefabs";

        // порядок видов в профиле — индексы весов смесей
        private static readonly string[] SpeciesNames = { "beech", "poplar", "pine", "oak_forest", "oak_open", "pine_young", "dead", "snag", "broken" };

        [MenuItem("Tools/Environment/Bake Tree Leaf Areas")]
        public static void BakeMenu()
        {
            GeoTiledWorld world = WorldMapBaker.FindWorld();
            WorldMapBaker.Log("TreeLeafAreasBaker", () => world != null ? BakeLeafAreas(world) : WorldMapBaker.NoWorld);
        }

        [MenuItem("Tools/Environment/Update Tree Species Profile")]
        public static void SpeciesMenu() => Debug.Log("[TreeLeafAreasBaker] " + UpdateSpecies());

        /// <summary>Контуры лесов мира с типом листвы — в его папку.</summary>
        public static string BakeLeafAreas(GeoTiledWorld world)
        {
            List<TreeSpeciesProfile.LeafArea> areas = new();
            int files = 0;

            foreach (string path in WorldMapBaker.RequireExports(world))
            {
                files++;
                JObject root = JObject.Parse(File.ReadAllText(path));

                foreach (JToken area in root["areas"] ?? new JArray())
                {
                    string leaf = (string)area["leafType"] ?? "";
                    byte type = leaf == "broadleaved" ? (byte)1 : leaf == "needleleaved" ? (byte)2 : leaf == "mixed" ? (byte)3 : (byte)0;

                    if (type == 0 || area["rings"] is not JArray rings || rings.Count == 0)
                        continue;

                    TreeSpeciesProfile.LeafArea result = new() { type = type, outer = Ring(rings[0]) };
                    List<Vector2> holes = new();
                    List<int> starts = new();

                    for (int r = 1; r < rings.Count; r++)
                    {
                        starts.Add(holes.Count);
                        holes.AddRange(Ring(rings[r]));
                    }

                    result.holes = holes.ToArray();
                    result.holeStarts = starts.ToArray();
                    Vector2 min = result.outer.Aggregate(Vector2.positiveInfinity, Vector2.Min);
                    Vector2 max = result.outer.Aggregate(Vector2.negativeInfinity, Vector2.Max);
                    result.bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

                    if (result.outer.Length >= 3)
                        areas.Add(result);
                }
            }

            TreeLeafAreas asset = WorldMapBaker.Asset<TreeLeafAreas>(world, AssetName, true);
            asset.areas = areas.ToArray();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            WorldMapBaker.Attach(world, data => data.leafAreas = asset);
            return $"{areas.Count} leaf areas from {files} exports ({areas.Count(a => a.type == 2)} conifer, {areas.Count(a => a.type == 1)} broadleaf)";
        }

        /// <summary>Виды профиля и их варианты из папок растений пакета; смеси — по умолчанию у новых видов.</summary>
        public static string UpdateSpecies()
        {
            TreeSpeciesProfile profile = AssetDatabase.LoadAssetAtPath<TreeSpeciesProfile>(ProfilePath);
            bool created = profile == null;

            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
                profile = ScriptableObject.CreateInstance<TreeSpeciesProfile>();
                SetDefaultMixes(profile);
            }

            profile.species = SpeciesNames.Select(name => new TreeSpeciesProfile.Species { name = name, variants = Variants(name), ownHeight = name is "snag" or "broken", lying = name is "broken" }).ToArray();
            ExtendMixes(profile);

            if (created)
                AssetDatabase.CreateAsset(profile, ProfilePath);
            else
                EditorUtility.SetDirty(profile);

            AssetDatabase.SaveAssetIfDirty(profile);
            return "species: " + string.Join(", ", profile.species.Select(s => s.name + " " + s.variants.Length));
        }

        private static Vector2[] Ring(JToken ring)
        {
            float[] flat = ring.Values<float>().ToArray();
            Vector2[] points = new Vector2[flat.Length / 2];

            for (int i = 0; i < points.Length; i++)
                points[i] = new Vector2(flat[i * 2], flat[i * 2 + 1]);

            return points;
        }

        private static GameObject[] Variants(string species)
        {
            string folder = species switch
            {
                "pine" or "pine_young" => Generated + "/PineSet",
                "oak_forest" or "oak_open" or "dead" or "snag" or "broken" => Generated + "/Oak",
                _ => Vegetation
            };

            string[] patterns = species switch
            {
                "beech" => new[] { "prefab_beech_tree_0" },
                "poplar" => new[] { "prefab_Baked_0" },
                "pine" => new[] { "prefab_pine_set_" },
                // молодые и опушечные — сосны набора с низкой кроной (у d длинный чистый ствол бора)
                "pine_young" => new[] { "prefab_pine_set_a", "prefab_pine_set_b" },
                "oak_forest" => new[] { "prefab_oak_forest_" },
                "oak_open" => new[] { "prefab_oak_open_" },
                "dead" => new[] { "prefab_oak_dead_" },
                "snag" => new[] { "prefab_oak_snag_" },
                "broken" => new[] { "prefab_oak_broken_" },
                _ => new string[0]
            };

            if (!AssetDatabase.IsValidFolder(folder))
                return new GameObject[0];

            return AssetDatabase.FindAssets("t:Prefab", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => patterns.Any(Path.GetFileName(p).StartsWith))
                .OrderBy(p => p)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .Where(p => p != null)
                .ToArray();
        }

        // Смеси (веса по видам: бук, тополь, сосна, дуб лесной, дуб одиночный, сосна молодая, сухостой, снаг, бурелом). Бук
        // NatureManufacture стоит за остальные широколиственные (ясень, клён, липа) — своих моделей у них пока нет. Сухостой,
        // снаги и бурелом — по 1.5–2.5 % в лесу, больше на болоте (вымокший лес), нет в посёлках и рядах.
        private static void SetDefaultMixes(TreeSpeciesProfile p)
        {
            p.forestConifer = Mix(0f, 0f, 0.82f, 0.06f, 0f, 0.12f, 0.01f, 0.02f, 0.01f);
            p.forestMixed = Mix(0.15f, 0f, 0.45f, 0.3f, 0f, 0.1f, 0.02f, 0.02f, 0.015f);
            p.forestBroadleaf = Mix(0.4f, 0.04f, 0.03f, 0.53f, 0f, 0f, 0.025f, 0.02f, 0.015f);
            p.forestSandy = Mix(0.08f, 0f, 0.65f, 0.15f, 0f, 0.12f, 0.015f, 0.02f, 0.01f);
            p.rows = Mix(0.3f, 0.5f, 0f, 0f, 0.2f, 0f, 0f, 0f, 0f);
            p.settlement = Mix(0.35f, 0.3f, 0f, 0f, 0.35f, 0f, 0f, 0f, 0f);
            p.wet = Mix(0.3f, 0.7f, 0f, 0f, 0f, 0f, 0.04f, 0.05f, 0.03f);
            p.orchard = new TreeSpeciesProfile.Mix();
        }

        // Смеси уже запечённого профиля — тех видов, что были тогда (подстройка сохраняется); у новых видов — веса по
        // умолчанию.
        private static void ExtendMixes(TreeSpeciesProfile p)
        {
            TreeSpeciesProfile defaults = ScriptableObject.CreateInstance<TreeSpeciesProfile>();
            SetDefaultMixes(defaults);
            Extend(p.forestConifer, defaults.forestConifer);
            Extend(p.forestMixed, defaults.forestMixed);
            Extend(p.forestBroadleaf, defaults.forestBroadleaf);
            Extend(p.forestSandy, defaults.forestSandy);
            Extend(p.rows, defaults.rows);
            Extend(p.settlement, defaults.settlement);
            Extend(p.wet, defaults.wet);
            Object.DestroyImmediate(defaults);
        }

        private static void Extend(TreeSpeciesProfile.Mix mix, TreeSpeciesProfile.Mix defaults)
        {
            if (mix?.weights == null || mix.weights.Length >= defaults.weights.Length)
                return;

            float[] weights = defaults.weights.ToArray();
            System.Array.Copy(mix.weights, weights, mix.weights.Length);
            mix.weights = weights;
        }

        private static TreeSpeciesProfile.Mix Mix(params float[] weights) => new() { weights = weights };
    }
}
