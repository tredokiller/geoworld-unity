using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The grid a tiled world is cut on and, where several exports meet, who builds what.
    //
    // The grid is the same for every export: tile (i, j) is the square [i * size, (i + 1) * size) x [j * size, (j + 1) * size)
    // of the export's own coordinates, whatever the export's bounds are. So two exports of one area (made with one fixed
    // origin) cut their common ground into the same tiles, and a tile has one index, one place and one name in every world.
    //
    // Exports of neighbouring parts overlap a little. The exports are listed in order and each owns the ground of its
    // bounds that no earlier export owns: its region, a rectangle (the largest piece that is left when an earlier export's
    // bounds are cut away). Regions never overlap. A tile is built once, from every export whose region reaches it: a
    // tile in the middle of one export from that export alone, a tile where exports meet from all of them, each
    // providing the ground, the roads and the buildings inside its own region.
    public sealed class GeoTilePlan
    {
        public readonly float tileSize;
        public readonly GeoBounds[] bounds;  // the area of each export
        public readonly GeoBounds[] region;  // the ground each export builds; null: none (an earlier export covers all of it)
        // each tile: the exports that build it, the one with most of the tile first
        public readonly Dictionary<Vector2Int, List<int>> tiles = new Dictionary<Vector2Int, List<int>>();
        public Vector2Int first;             // lowest tile index in each direction
        public int columns, rows;            // tiles from there to the highest

        GeoTilePlan(float tileSize, int parts) { this.tileSize = tileSize; bounds = new GeoBounds[parts]; region = new GeoBounds[parts]; }

        // A sliver of a tile (a thousandth of its side) is not a tile: exports rarely end on the grid.
        float MinSide => Mathf.Max(0.5f, tileSize * 0.001f);

        public static GeoTilePlan Make(IList<GeoBounds> exports, float tileSize)
        {
            if (exports == null || exports.Count == 0) throw new ArgumentException("A world needs at least one export.");
            var plan = new GeoTilePlan(tileSize, exports.Count);
            var share = new Dictionary<Vector2Int, Dictionary<int, double>>();
            for (int i = 0; i < exports.Count; i++)
            {
                plan.bounds[i] = Copy(exports[i]);
                var r = Copy(exports[i]);
                for (int j = 0; j < i && r != null; j++) r = plan.Without(r, exports[j]);
                plan.region[i] = r;
                if (r == null) continue;
                int x0 = Mathf.FloorToInt((float)(r.minX / tileSize + 0.001)), x1 = Mathf.CeilToInt((float)(r.maxX / tileSize - 0.001)) - 1;
                int z0 = Mathf.FloorToInt((float)(r.minZ / tileSize + 0.001)), z1 = Mathf.CeilToInt((float)(r.maxZ / tileSize - 0.001)) - 1;
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var index = new Vector2Int(x, z);
                        var cut = Intersection(Tile(index, tileSize), r);
                        if (cut == null || cut.Width <= plan.MinSide || cut.Depth <= plan.MinSide) continue;
                        if (!share.TryGetValue(index, out var parts)) share[index] = parts = new Dictionary<int, double>();
                        parts[i] = (double)cut.Width * cut.Depth;
                    }
            }
            if (share.Count == 0) throw new InvalidOperationException("The exports hold no ground to build.");
            foreach (var pair in share)
                plan.tiles[pair.Key] = pair.Value.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key).ToList();
            plan.first = new Vector2Int(plan.tiles.Keys.Min(k => k.x), plan.tiles.Keys.Min(k => k.y));
            plan.columns = plan.tiles.Keys.Max(k => k.x) - plan.first.x + 1;
            plan.rows = plan.tiles.Keys.Max(k => k.y) - plan.first.y + 1;
            return plan;
        }

        // The ground of a tile of the grid.
        public static GeoBounds Tile(Vector2Int index, float tileSize) => new GeoBounds
        {
            minX = (float)((double)index.x * tileSize), minZ = (float)((double)index.y * tileSize),
            maxX = (float)((double)(index.x + 1) * tileSize), maxZ = (float)((double)(index.y + 1) * tileSize)
        };

        // The export that builds the ground at a point: the one whose region holds it; else the first whose bounds hold it
        // (the strip that cutting away an earlier export left out of every region); -1 where there is none.
        public int OwnerOf(float x, float z)
        {
            for (int i = 0; i < region.Length; i++) if (region[i] != null && region[i].Contains(x, z)) return i;
            for (int i = 0; i < bounds.Length; i++) if (bounds[i].Contains(x, z)) return i;
            return -1;
        }

        // The box round all the exports.
        public GeoBounds Outer()
        {
            return new GeoBounds { minX = bounds.Min(b => b.minX), minZ = bounds.Min(b => b.minZ), maxX = bounds.Max(b => b.maxX), maxZ = bounds.Max(b => b.maxZ) };
        }

        // The rectangle r keeps when the bounds s are cut away: the largest of the four pieces that lie beside s; r itself
        // when they hardly meet; null when nothing is left.
        GeoBounds Without(GeoBounds r, GeoBounds s)
        {
            var cut = Intersection(r, s);
            if (cut == null || cut.Width <= MinSide || cut.Depth <= MinSide) return r;
            GeoBounds best = null;
            double bestArea = 0;
            void Consider(float minX, float minZ, float maxX, float maxZ)
            {
                if (maxX - minX <= MinSide || maxZ - minZ <= MinSide) return;
                double area = (double)(maxX - minX) * (maxZ - minZ);
                if (area > bestArea) { bestArea = area; best = new GeoBounds { minX = minX, minZ = minZ, maxX = maxX, maxZ = maxZ }; }
            }
            Consider(r.minX, r.minZ, s.minX, r.maxZ); // west of s
            Consider(s.maxX, r.minZ, r.maxX, r.maxZ); // east
            Consider(r.minX, r.minZ, r.maxX, s.minZ); // south
            Consider(r.minX, s.maxZ, r.maxX, r.maxZ); // north
            return best;
        }

        public static GeoBounds Intersection(GeoBounds a, GeoBounds b)
        {
            float minX = Mathf.Max(a.minX, b.minX), minZ = Mathf.Max(a.minZ, b.minZ), maxX = Mathf.Min(a.maxX, b.maxX), maxZ = Mathf.Min(a.maxZ, b.maxZ);
            return maxX > minX && maxZ > minZ ? new GeoBounds { minX = minX, minZ = minZ, maxX = maxX, maxZ = maxZ } : null;
        }

        static GeoBounds Copy(GeoBounds b) => new GeoBounds { minX = b.minX, minZ = b.minZ, maxX = b.maxX, maxZ = b.maxZ };
    }
}
