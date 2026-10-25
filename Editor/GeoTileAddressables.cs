using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace GeoWorld.Editor
{
    public static class GeoTileAddressables
    {
        public static void Unregister(GeoTiledWorld.Tile tile)
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            if (settings)
            {
                string guid = AssetDatabase.AssetPathToGUID(tile.scenePath);
                if (settings.FindAssetEntry(guid) != null)
                {
                    settings.RemoveAssetEntry(guid);
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssets();
                }
            }
            tile.sceneAddress = null;
        }

        public static void Register(GeoTiledWorld world, GeoTiledWorld.Tile tile)
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            string groupName = "Geo World " + UnityEngine.Hash128.Compute(world.folder);
            var group = settings.FindGroup(groupName);
            if (!group)
                group = settings.CreateGroup(groupName, false, false, false, null,
                    typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            group.GetSchema<BundledAssetGroupSchema>().BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackSeparately;
            string guid = AssetDatabase.AssetPathToGUID(tile.scenePath);
            var entry = settings.CreateOrMoveEntry(guid, group);
            tile.sceneAddress = "GeoWorld/" + UnityEngine.Hash128.Compute(world.folder) + "/" + tile.index.x + "_" + tile.index.y;
            entry.address = tile.sceneAddress;
            // Addressable scenes must not also be included as built-in scenes.
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(scene => scene.path == tile.scenePath);
            EditorBuildSettings.scenes = scenes.ToArray();
            EditorUtility.SetDirty(group);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        // The terrain material of a tile made before it had GPU instancing on: set. A build strips the instancing variants of a shader
        // that no material has instancing on for, and a terrain drawn instanced with its shader stripped is not drawn at all (the
        // satellite imagery of the whole world was missing in a player). Returns how many materials were changed.
        public static int EnsureTerrainInstancing(GeoTiledWorld world)
        {
            int changed = 0;
            foreach (var tile in world.tiles)
            {
                if (string.IsNullOrEmpty(tile.assetPath) || !File.Exists(tile.assetPath)) continue;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(tile.assetPath))
                    if (o is UnityEngine.Material material && material.name == "Terrain URP" && !material.enableInstancing)
                    {
                        material.enableInstancing = true;
                        EditorUtility.SetDirty(material);
                        changed++;
                    }
            }
            if (changed > 0) AssetDatabase.SaveAssets();
            return changed;
        }

        public static string SharedGroupName(GeoTiledWorld world) => "Geo World " + UnityEngine.Hash128.Compute(world.folder) + " shared";

        // The assets that two or more tiles of the world use (the home and ruin models with their textures, the road and ground
        // pictures, tree prefabs, the shaders of every material) go into a group of their own, one bundle. An asset in no group
        // is copied by Addressables into every bundle that uses it: each tile's bundle had its own copy of the URP shaders and
        // of every home model and texture it shows, which in a town tile is most of its bundle. Assets in a Resources folder
        // are left alone (they are in the player already). Returns how many assets were added.
        public static int ShareDependencies(GeoTiledWorld world)
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            var users = new Dictionary<string, int>();
            int done = 0;
            foreach (var tile in world.tiles)
            {
                if (string.IsNullOrEmpty(tile.scenePath) || !File.Exists(tile.scenePath)) continue;
                if (done++ % 20 == 0) EditorUtility.DisplayProgressBar("Geo World", "Finding the assets the tiles share", done / (float)world.tiles.Count);
                foreach (var dependency in AssetDatabase.GetDependencies(tile.scenePath, true))
                    if (dependency != tile.scenePath && Shareable(dependency)) users[dependency] = users.TryGetValue(dependency, out int n) ? n + 1 : 1;
            }
            EditorUtility.ClearProgressBar();
            string groupName = SharedGroupName(world);
            var group = settings.FindGroup(groupName);
            if (!group)
                group = settings.CreateGroup(groupName, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            // LZ4: assets are read from it one by one, as the tiles that use them load (LZMA would unpack the whole bundle)
            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            int added = 0;
            foreach (var pair in users)
            {
                if (pair.Value < 2) continue; // one tile's own: stays in that tile's bundle
                string guid = AssetDatabase.AssetPathToGUID(pair.Key);
                if (string.IsNullOrEmpty(guid) || settings.FindAssetEntry(guid) != null) continue; // in a group already (this or the user's)
                settings.CreateOrMoveEntry(guid, group, false, false);
                added++;
            }
            if (added > 0) settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            EditorUtility.SetDirty(group);
            AssetDatabase.SaveAssets();
            return added;
        }

        static bool Shareable(string path)
        {
            if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/")) return false; // Unity's built-in resources
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".cs" || extension == ".dll" || extension == ".unity" || extension == ".asmdef") return false;
            return !path.Contains("/Resources/") && !path.Contains("/Editor/");
        }
    }
}
