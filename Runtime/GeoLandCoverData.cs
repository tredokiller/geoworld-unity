using System;
using UnityEngine;

namespace GeoWorld
{
    // One tile's land cover (GeoLandCover): a byte a cell of about 10 m, rows from the south, and the class each byte stands
    // for by name, so that the data says what it holds whatever GeoLandClass becomes. Baked from the export's ground map by
    // GeoLandCoverBaker into "<world>/Land cover"; the tile itself is not touched.
    public sealed class GeoLandCoverData : ScriptableObject
    {
        public Vector2Int tile;
        [Tooltip("The tile in world metres (x = east, y = north).")]
        public Rect area;
        [Tooltip("Cells along a side of the tile.")]
        public int resolution;
        [Tooltip("The class each cell value stands for, by name (GeoLandCover.Names): a cell of value 3 is classes[3].")]
        public string[] classes = Array.Empty<string>();
        [HideInInspector] public byte[] cells = Array.Empty<byte>();

        [NonSerialized] GeoLandClass[] lookup;

        public bool Valid => resolution > 0 && cells != null && cells.Length == resolution * resolution && area.width > 0 && area.height > 0;
        public float CellSize => resolution > 0 ? area.width / resolution : 0;

        // The class of a cell (column from the west, row from the south).
        public GeoLandClass At(int column, int row) => Lookup()[cells[row * resolution + column]];

        // The class at a point of the tile; false off the tile and where the ground map says nothing (None).
        public bool TrySample(float x, float z, out GeoLandClass value)
        {
            value = GeoLandClass.None;
            if (!Valid || x < area.xMin || z < area.yMin || x > area.xMax || z > area.yMax) return false;
            int column = Mathf.Min(resolution - 1, Mathf.FloorToInt((x - area.xMin) / area.width * resolution));
            int row = Mathf.Min(resolution - 1, Mathf.FloorToInt((z - area.yMin) / area.height * resolution));
            value = At(column, row);
            return value != GeoLandClass.None;
        }

        // What a bake writes; the lookup follows the classes it writes.
        public void Set(Vector2Int tile, Rect area, int resolution, byte[] cells, string[] classes)
        {
            this.tile = tile; this.area = area; this.resolution = resolution; this.cells = cells; this.classes = classes;
            lookup = null;
        }

        void OnValidate() => lookup = null;

        // Cell value -> class, by the names (a name the runtime does not know is None).
        GeoLandClass[] Lookup()
        {
            if (lookup != null) return lookup;
            var result = new GeoLandClass[256];
            for (int i = 0; classes != null && i < classes.Length && i < result.Length; i++) result[i] = GeoLandCover.ClassOf(classes[i]);
            return lookup = result;
        }
    }
}
