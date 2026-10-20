using System;
using UnityEngine;

namespace GeoWorld
{
    [CreateAssetMenu(fileName = "GeoTreeData", menuName = "Geo World/Tree Data")]
    public sealed class GeoTreeData : ScriptableObject
    {
        [SerializeField] private string _terrainName;
        [SerializeField] private GeoTreeInstance[] _trees = Array.Empty<GeoTreeInstance>();

        public GeoInstancedMeshes.Batch[] batches = Array.Empty<GeoInstancedMeshes.Batch>();
        public int instanceCount;

        public string TerrainName => _terrainName;
        public int TreeCount => _trees?.Length ?? 0;

        public TreeInstance GetTreeInstance(int index)
        {
            if (_trees == null || index < 0 || index >= _trees.Length)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _trees[index].ToTreeInstance();
        }

        public TreeInstance[] GetTreeInstances()
        {
            if (_trees == null || _trees.Length == 0)
                return Array.Empty<TreeInstance>();

            TreeInstance[] trees = new TreeInstance[_trees.Length];
            for (int i = 0; i < _trees.Length; i++)
            {
                trees[i] = _trees[i].ToTreeInstance();
            }

            return trees;
        }

#if UNITY_EDITOR
        public void SetTrees(string terrainName, TreeInstance[] trees)
        {
            _terrainName = terrainName;
            if (trees == null || trees.Length == 0)
            {
                _trees = Array.Empty<GeoTreeInstance>();
                return;
            }

            _trees = new GeoTreeInstance[trees.Length];
            for (int i = 0; i < trees.Length; i++)
            {
                _trees[i] = GeoTreeInstance.From(trees[i]);
            }
        }
#endif
    }
}
