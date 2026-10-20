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
    }
}
