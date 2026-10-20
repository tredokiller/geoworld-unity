using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // A large world cut into square tiles (Tools > Geo World > Generate tiled world). Each tile is a GeoWorldRoot of its
    // own (terrain, roads, buildings, props) in its own scene and asset, so a tile can be rebuilt, loaded and unloaded
    // alone; this component, in the world's main scene, is the index of the tiles.
    [DisallowMultipleComponent]
    public class GeoTiledWorld : MonoBehaviour
    {
        [Serializable]
        public class Tile
        {
            public Vector2Int index;
            [Tooltip("Area of the tile in world metres (x = east, y = north).")]
            public Rect area;
            public string scenePath, assetPath;
            public string sceneAddress;
            [Tooltip("Fingerprint of the export files and settings the tile was built from: an unchanged tile is skipped.")]
            public string hash;
            public int buildings, roads, props;
            public float seconds;
            public bool empty;
        }

        [HideInInspector] public string environmentPath, buildingsPath;
        public GenerationSettings settings = new GenerationSettings();
        [Tooltip("Side of a tile in metres.")]
        public float tileSize = 1000;
        [HideInInspector] public Vector2 origin;
        [HideInInspector] public int columns, rows;
        [HideInInspector] public float baseY, heightRange;
        [Tooltip("Tiles in scenes of their own (Tiles folder next to this scene). Off: all tiles are children of this object.")]
        public bool separateScenes = true;
        [Tooltip("Register generated tile scenes in Addressables and stream them by address.")]
        public bool addressableScenes;
        [HideInInspector] public string folder;
        public List<Tile> tiles = new List<Tile>();
        [HideInInspector] public string report;

        public Tile Find(Vector2Int index) => tiles.Find(t => t.index == index);

        public Vector2Int IndexOf(Vector3 world) =>
            new Vector2Int(Mathf.FloorToInt((world.x - origin.x) / tileSize), Mathf.FloorToInt((world.z - origin.y) / tileSize));
    }
}
