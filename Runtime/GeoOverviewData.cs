using System;
using UnityEngine;

namespace GeoWorld
{
    // What to bake for the overview of a tiled world (GeoOverview): the whole world as one coarse satellite layer under its tiles,
    // and a far ring of low-detail imagery around it, so that from high above the world neither shrinks to the square of loaded
    // tiles nor ends at its own edge. Part of GenerationSettings, but not of what a tile is built from (GeoTileSource.Hash).
    [Serializable]
    public class GeoOverviewSettings
    {
        [Tooltip("Bake the overview when the tiled world is generated or its tiles are rebuilt (Geo Tiled World's Inspector: Bake overview).")]
        public bool enabled = true;
        [Tooltip("Side of the far ring's area in sides of the world: 10 puts a 20 km world in the middle of 200 x 200 km of imagery, whose edge is then 90 km away. 1: no far ring.")]
        [Range(1, 30)] public float farScale = 10;
        [Tooltip("Texels along a side of the far imagery (2048 to 8192): 4096 over 200 km is about 50 m a texel. The tile zoom follows (zoom 12 for 50 m).")]
        public int farResolution = 4096;
        [Tooltip("Texels along a side of the world's own overview, made from the tiles' satellite imagery: at most this, a whole number of texels a tile (4096 over 20 x 20 tiles is 204 a tile, 5 m a texel on 1 km tiles).")]
        public int nearResolution = 4096;
        [Tooltip("Height grid cells along a tile's side in the overview (16 on a 1 km tile: 62.5 m).")]
        [Range(2, 64)] public int cellsPerTile = 16;
    }

    // The baked overview of a tiled world (GeoOverviewBaker), drawn by GeoOverview. Near: the satellite imagery of every tile in one
    // texture and the terrain's height on a coarse grid. Far: imagery of a much larger area around the world and a height grid that
    // goes from the world's edge down to the world's mean height. Heights are world Y of the terrain, rows from the south.
    public class GeoOverviewData : ScriptableObject
    {
        [Tooltip("Name of the Geo Tiled World it was baked from.")]
        public string worldName;

        [Header("Near: the world")]
        public Texture2D near;
        [Tooltip("The world's tiles in metres (x = east, y = north): what the near texture and heights cover.")]
        public Rect area;
        public Vector2 origin;
        public float tileSize = 1000;
        public Vector2Int firstIndex;
        public int columns, rows;
        public int cellsPerTile = 16;
        [HideInInspector] public float[] heights;
        [Tooltip("Tiles of the grid (rows from the south) with no imagery in the near texture (no terrain was exported there): the far ring shows through.")]
        [HideInInspector] public bool[] missing;

        [Header("Far: the ring around it")]
        public Texture2D far;
        [Tooltip("Area of the far imagery in metres; the world is in its middle.")]
        public Rect farArea;
        [Tooltip("Far height grid vertices along each side. Under the world the ring lies below the world's layer, which covers it.")]
        public int farVertices;
        [Tooltip("Mean height of the world's terrain: the far ring's level away from the world.")]
        public float farHeight;
        [HideInInspector] public float[] farHeights;
        [Tooltip("What the far imagery was made from (GeoOverviewBaker.FarKey): a bake keeps it while this is the same.")]
        [HideInInspector] public string farKey;

        public int NearVerticesX => columns * cellsPerTile + 1;
        public int NearVerticesZ => rows * cellsPerTile + 1;
        public bool HasNear => near && columns > 0 && rows > 0 && cellsPerTile > 0 && heights != null && heights.Length == NearVerticesX * NearVerticesZ;
        public bool Missing(int column, int row) => missing != null && missing.Length == columns * rows && missing[row * columns + column];
        public bool HasFar => far && farVertices > 1 && farHeights != null && farHeights.Length == farVertices * farVertices && farArea.width > 0;

        // The area the overview reaches: the far ring's, or the world's without one.
        public Rect Extent => HasFar ? farArea : area;
    }
}
