using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // What the ground is, as the export's ground map (materials.png) says: ESA WorldCover 2021 (10 m) with OpenStreetMap's land
    // use, water, roads, railways and building footprints painted over it. The values are the map's own ids
    // (Extractor~/extractor/materials.py), which stay as they are once there is content.
    public enum GeoLandClass : byte
    {
        None = 0, Grass = 1, Cropland = 2, Forest = 3, Scrub = 4, Bare = 5, Sand = 6, Rock = 7, Wetland = 8, Water = 9, Urban = 10,
        Asphalt = 11, Paving = 12, Gravel = 13, DirtRoad = 14, Path = 15, Rail = 16, Building = 17, Snow = 18, Orchard = 19
    }

    // The land cover of a tiled world, for a game to ask what the ground is anywhere: a forest, an orchard, a field, a meadow,
    // a village. It is the exports' ground maps baked onto the tiles' grid at about 10 m, one GeoLandCoverData a tile
    // (GeoLandCoverBaker: after the tiles are generated, and Bake land cover in the Geo Tiled World's Inspector for a world made
    // earlier; no tile is rebuilt). On the world's object with every tile's data, some 10 KB a square kilometre (4 MB for
    // 20 x 20 km), so it answers for any point of the world whether its tile is loaded or not, and across the tiles' edges.
    [DisallowMultipleComponent, ExecuteAlways]
    public class GeoLandCover : MonoBehaviour
    {
        public const int ClassCount = 20;
        // GeoLandClass's names in the export (materials.py), by value.
        public static readonly string[] Names = { "none", "grass", "cropland", "forest", "scrub", "bare", "sand", "rock", "wetland", "water",
            "urban", "asphalt", "paving", "gravel", "dirt_road", "path", "rail", "building", "snow", "orchard" };

        [Tooltip("The south-west corner of tile (0, 0), as the world's.")]
        public Vector2 origin;
        [Tooltip("Side of a tile in metres, as the world's.")]
        public float tileSize = 1000;
        [Tooltip("Side of a cell in metres: a tile's side over its cells.")]
        public float cellSize = 10;
        public List<GeoLandCoverData> tiles = new List<GeoLandCoverData>();
        [HideInInspector] public string report;

        // Points within radius are counted on a coarser step beyond this many cells along a side (a radius of more than 5 km).
        const int MaxCellsAcross = 1024;

        static readonly List<GeoLandCover> active = new List<GeoLandCover>();
        readonly Dictionary<Vector2Int, GeoLandCoverData> byIndex = new Dictionary<Vector2Int, GeoLandCoverData>();
        // the list the index was made from: a bake puts in a new one
        List<GeoLandCoverData> indexed;
        int indexedCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() => active.Clear();

        void OnEnable() { if (!active.Contains(this)) active.Add(this); indexed = null; }
        void OnDisable() => active.Remove(this);
        void OnValidate() => indexed = null;

        // The class at a point of the worlds that have land cover; false where none has it or its map says nothing (None).
        public static bool TrySample(Vector3 world, out GeoLandClass value)
        {
            foreach (var cover in active) if (cover && cover.Sample(world.x, world.z, out value)) return true;
            value = GeoLandClass.None;
            return false;
        }

        // The share of each class among the cells within radius metres of a point (fractions[(int)GeoLandClass.Forest]; at least
        // ClassCount long), in the first world that has land cover at the point; cells where the map says nothing are left out,
        // and a radius under a cell takes the cell at the point. False where no world has land cover there.
        public static bool TryGetFractions(Vector3 world, float radius, float[] fractions)
        {
            foreach (var cover in active)
                if (cover && cover.Covers(world.x, world.z) && cover.Fractions(world.x, world.z, radius, fractions)) return true;
            if (fractions != null) Array.Clear(fractions, 0, fractions.Length);
            return false;
        }

        // The sum of some classes' shares (fractions as TryGetFractions fills them).
        public static float Share(float[] fractions, params GeoLandClass[] classes)
        {
            float sum = 0;
            foreach (var value in classes) sum += fractions[(int)value];
            return sum;
        }

        // A class by its name in the export (any case); None for a name the runtime does not know.
        public static GeoLandClass ClassOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return GeoLandClass.None;
            for (int i = 0; i < Names.Length; i++) if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return (GeoLandClass)i;
            return GeoLandClass.None;
        }

        public static string NameOf(GeoLandClass value) => (int)value < Names.Length ? Names[(int)value] : Names[0];

        public Vector2Int IndexOf(float x, float z) => new Vector2Int(Mathf.FloorToInt((x - origin.x) / tileSize), Mathf.FloorToInt((z - origin.y) / tileSize));

        public GeoLandCoverData Tile(Vector2Int index)
        {
            if (tiles == null) return null;
            if (!ReferenceEquals(indexed, tiles) || indexedCount != tiles.Count)
            {
                byIndex.Clear();
                foreach (var data in tiles) if (data) byIndex[data.tile] = data;
                indexed = tiles; indexedCount = tiles.Count;
            }
            return byIndex.TryGetValue(index, out var found) ? found : null;
        }

        // Has the tile under the point land cover?
        public bool Covers(float x, float z)
        {
            if (tileSize <= 0) return false;
            var data = Tile(IndexOf(x, z));
            return data && data.Valid;
        }

        // The class at a point; false off the baked tiles and where the map says nothing. The cell is found from the tile's
        // place on the grid, so a point on a tile's edge belongs to the tile its index says, as in GeoTiledWorld.IndexOf.
        public bool Sample(float x, float z, out GeoLandClass value)
        {
            value = GeoLandClass.None;
            if (tileSize <= 0) return false;
            float u = (x - origin.x) / tileSize, v = (z - origin.y) / tileSize;
            var index = new Vector2Int(Mathf.FloorToInt(u), Mathf.FloorToInt(v));
            var data = Tile(index);
            if (!data || !data.Valid) return false;
            int n = data.resolution;
            value = data.At(Mathf.Clamp((int)((u - index.x) * n), 0, n - 1), Mathf.Clamp((int)((v - index.y) * n), 0, n - 1));
            return value != GeoLandClass.None;
        }

        // The shares of the classes among the cells whose middle is within radius of a point, whatever tiles they are on.
        public bool Fractions(float x, float z, float radius, float[] fractions)
        {
            if (fractions == null || fractions.Length < ClassCount)
                throw new ArgumentException("fractions takes a share for each of the " + ClassCount + " classes.", nameof(fractions));
            Array.Clear(fractions, 0, fractions.Length);
            float step = cellSize > 0 ? cellSize : 10;
            step *= Mathf.Max(1, Mathf.Ceil(2 * radius / step / MaxCellsAcross));
            int counted = 0;
            if (radius > 0)
            {
                int i0 = Mathf.FloorToInt((x - radius - origin.x) / step), i1 = Mathf.FloorToInt((x + radius - origin.x) / step);
                int j0 = Mathf.FloorToInt((z - radius - origin.y) / step), j1 = Mathf.FloorToInt((z + radius - origin.y) / step);
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        float cx = origin.x + (i + 0.5f) * step, cz = origin.y + (j + 0.5f) * step;
                        if ((cx - x) * (cx - x) + (cz - z) * (cz - z) > radius * radius || !Sample(cx, cz, out var value)) continue;
                        fractions[(int)value]++;
                        counted++;
                    }
            }
            if (counted == 0)
            {
                if (!Sample(x, z, out var here)) return false;
                fractions[(int)here] = 1;
                return true;
            }
            for (int c = 0; c < fractions.Length; c++) fractions[c] /= counted;
            return true;
        }
    }
}
