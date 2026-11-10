using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    public static class GeoTreeDataExporter
    {
        // Not in a Resources folder (that would put the tree data of every world into every player): loaded by address
        // (GeoAddressableAssets.TreeData), from the world's shared Addressables group (GeoTileAddressables.ShareDependencies).
        public const string Folder = GeoAddressableAssets.TreeDataRoot + "TerrainChunker/TreeData";

        // A world and tile identify the asset, independently of duplicate TerrainData names.
        public static string ResourceKey(GeoWorldRoot root) => "TerrainChunker/TreeData/" +
            Hash128.Compute(Path.GetDirectoryName(root.assetPath).Replace('\\', '/')) +
            "_" + root.tileIndex.x + "_" + root.tileIndex.y;

        public static void Export(GeoWorldRoot root)
        {
            if (!root || !root.terrain) return;
            string key = ResourceKey(root), path = GeoAddressableAssets.TreeData(key);
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
            var asset = AssetDatabase.LoadAssetAtPath<GeoTreeData>(path);
            if (!asset)
            {
                asset = ScriptableObject.CreateInstance<GeoTreeData>();
                AssetDatabase.CreateAsset(asset, path);
            }
            var terrain = root.terrain;
            asset.SetTrees(terrain.terrainData.name, terrain.terrainData.treeInstances);
            var drawers = root.GetComponentsInChildren<GeoInstancedMeshes>(true);
            var batches = new System.Collections.Generic.List<GeoInstancedMeshes.Batch>();
            asset.instanceCount = 0;
            foreach (var drawer in drawers)
            {
                batches.AddRange(drawer.batches);
                asset.instanceCount += drawer.count;
            }
            asset.batches = batches.ToArray();
            // Persist the external copy before stripping the generated data.
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            terrain.terrainData.SetTreeInstances(Array.Empty<TreeInstance>(), false);
            EditorUtility.SetDirty(terrain.terrainData);
            foreach (var drawer in drawers) UnityEngine.Object.DestroyImmediate(drawer.gameObject);
            var runtime = terrain.GetComponent<GeoTerrainTreeLoader>();
            if (!runtime) runtime = terrain.gameObject.AddComponent<GeoTerrainTreeLoader>();
            runtime.resourceKey = key;
        }
    }
}
