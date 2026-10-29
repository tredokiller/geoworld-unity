using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace GeoWorld
{
    // How tall the trees of a tiled world's tiles are, by what they stand in (GeoLandCover). The generator plants its trees
    // 5 to 10 m tall (a mapped tree without a height 7 m), a young wood everywhere; around Kharkiv a mature forest is 18-25 m,
    // a shelterbelt 8-14 m, an orchard 4-7 m. When a tile's trees load (GeoTerrainTreeLoader), before Nature Renderer sees
    // them, a tree with a generated height takes a height from the range of its kind and the prefab whose own height is
    // nearest it, so that its scale stays within half and twice the prefab's; its width keeps its proportion. A tree whose
    // height is outside the generated range had it from the map and keeps it. The kind: an orchard's tree; a tree in a wood
    // (forest within ForestRadius around it; a narrower belt is a row); a settlement's (on built-up ground or with it around);
    // else a tree of a row, a belt or a hedgerow among fields. Nothing is baked, and a tile looks the same every time it
    // loads: a tree's height and prefab come from a hash of its place.
    [Serializable]
    public class GeoTreeHeights
    {
        public enum Kind { Forest, Row, Orchard, Settlement }

        [Tooltip("Size the tiles' trees by the land cover when a tile loads in Play mode, before Nature Renderer draws them. Off: the heights they were generated with.")]
        public bool enabled = true;
        [Tooltip("Height in metres, from and to, of a tree in a wood.")]
        public Vector2 forest = new Vector2(15, 24);
        [Tooltip("Tree rows, shelterbelts, hedgerow trees and trees standing alone among fields and meadows.")]
        public Vector2 rows = new Vector2(8, 14);
        [Tooltip("Orchards and vineyards.")]
        public Vector2 orchard = new Vector2(4, 7);
        [Tooltip("Single trees in villages and towns: gardens, yards, streets.")]
        public Vector2 settlement = new Vector2(6, 12);
        [Tooltip("The heights the generator gives (5-10 m, and 7 m to a mapped tree without one): only trees within them are sized; a tree outside them has its height from the map and keeps it.")]
        public Vector2 generated = new Vector2(5, 10);

        // Forest within this radius (metres) from which a tree in a wood is a forest tree.
        public const float ForestRadius = 50, ForestShare = 0.45f;
        // Built-up ground within this radius from which a tree is a settlement's.
        public const float BuiltRadius = 60, BuiltShare = 0.1f;
        // The prefabs whose own height fits within this factor of the best fit may be taken (by the hash).
        const float FitSpread = 1.3f;
        const float MinScale = 0.5f, MaxScale = 2f;

        // Trees sized and the milliseconds it took, since Play mode started (every tile): for checks.
        public static int SizedTrees { get; private set; }
        public static double SizedMilliseconds { get; private set; }

        static readonly Dictionary<GameObject, float> prefabHeights = new Dictionary<GameObject, float>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() { SizedTrees = 0; SizedMilliseconds = 0; prefabHeights.Clear(); }

        public Vector2 Range(Kind kind) => kind == Kind.Forest ? forest : kind == Kind.Orchard ? orchard : kind == Kind.Settlement ? settlement : rows;

        // Sizes a terrain's trees (positions 0..1 of its size, the terrain at origin; prototypeHeights: the own height of each
        // prototype, PrototypeHeights). Trees where the land cover says nothing are left as they are. Returns how many changed.
        public int Apply(GeoLandCover cover, TreeInstance[] trees, Vector3 origin, Vector3 size, float[] prototypeHeights)
        {
            if (!cover || trees == null || trees.Length == 0 || prototypeHeights == null || prototypeHeights.Length == 0) return 0;
            var watch = Stopwatch.StartNew();
            var around = new Neighbourhood(cover, origin, size);
            int changed = 0;
            for (int i = 0; i < trees.Length; i++)
            {
                var tree = trees[i];
                int prototype = tree.prototypeIndex;
                if (prototype < 0 || prototype >= prototypeHeights.Length || prototypeHeights[prototype] <= 0 || tree.heightScale <= 0) continue;
                float height = tree.heightScale * prototypeHeights[prototype];
                if (height < generated.x - 0.01f || height > generated.y + 0.01f) continue;
                float x = origin.x + tree.position.x * size.x, z = origin.z + tree.position.z * size.z;
                if (!around.KindAt(x, z, out var kind)) continue;
                var range = Range(kind);
                float target = Mathf.Lerp(range.x, range.y, (Hash(x, z, 1) + Hash(x, z, 2)) * 0.5f);
                int pick = Fit(prototypeHeights, target, Hash(x, z, 3));
                float scale = Mathf.Clamp(target / prototypeHeights[pick], MinScale, MaxScale);
                tree.widthScale = scale * tree.widthScale / tree.heightScale;
                tree.heightScale = scale;
                tree.prototypeIndex = pick;
                trees[i] = tree;
                changed++;
            }
            SizedTrees += changed;
            SizedMilliseconds += watch.Elapsed.TotalMilliseconds;
            return changed;
        }

        // The prototype for a tree of a height: one of those whose own height is nearest it (within FitSpread of the best),
        // by pick (0..1).
        static int Fit(float[] heights, float target, float pick)
        {
            float best = float.MaxValue;
            for (int p = 0; p < heights.Length; p++) if (heights[p] > 0) best = Mathf.Min(best, Mathf.Abs(Mathf.Log(target / heights[p])));
            float limit = best + Mathf.Log(FitSpread);
            int count = 0;
            for (int p = 0; p < heights.Length; p++) if (heights[p] > 0 && Mathf.Abs(Mathf.Log(target / heights[p])) <= limit) count++;
            int k = Mathf.Min(count - 1, (int)(pick * count));
            for (int p = 0; p < heights.Length; p++)
                if (heights[p] > 0 && Mathf.Abs(Mathf.Log(target / heights[p])) <= limit && k-- == 0) return p;
            return 0;
        }

        // 0..1 from a place (to the centimetre) and a salt.
        static float Hash(float x, float z, int salt)
        {
            unchecked
            {
                uint h = (uint)Mathf.RoundToInt(x * 100) * 73856093u ^ (uint)Mathf.RoundToInt(z * 100) * 19349663u ^ (uint)salt * 83492791u;
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        static bool Built(GeoLandClass value) =>
            value == GeoLandClass.Urban || value == GeoLandClass.Building || value == GeoLandClass.Asphalt || value == GeoLandClass.Paving;

        // A prefab's height above its pivot, from its first level of detail (or all its meshes).
        public static float PrefabHeight(GameObject prefab)
        {
            var lods = prefab.GetComponent<LODGroup>() ? prefab.GetComponent<LODGroup>().GetLODs() : null;
            var renderers = lods != null && lods.Length > 0 ? lods[0].renderers : prefab.GetComponentsInChildren<Renderer>(true);
            var toRoot = prefab.transform.worldToLocalMatrix;
            float top = 0;
            foreach (var renderer in renderers)
            {
                var filter = renderer ? renderer.GetComponent<MeshFilter>() : null;
                if (!filter || !filter.sharedMesh) continue;
                var box = filter.sharedMesh.bounds;
                var matrix = toRoot * renderer.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    var corner = box.center + Vector3.Scale(box.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    top = Mathf.Max(top, matrix.MultiplyPoint3x4(corner).y);
                }
            }
            return top > 0.1f ? top : 1;
        }

        // The own height of each of a terrain's tree prototypes (0 where one has no prefab), kept for the prefabs seen before.
        public static float[] PrototypeHeights(TreePrototype[] prototypes)
        {
            var result = new float[prototypes?.Length ?? 0];
            for (int i = 0; i < result.Length; i++)
            {
                var prefab = prototypes[i]?.prefab;
                if (!prefab) continue;
                if (!prefabHeights.TryGetValue(prefab, out float height)) prefabHeights[prefab] = height = PrefabHeight(prefab);
                result[i] = height;
            }
            return result;
        }

        // The land cover around a terrain on the land cover's cells, and the share of forest and of built-up ground around each
        // cell (summed-area tables over the cells that have a class).
        sealed class Neighbourhood
        {
            readonly GeoLandCover cover;
            readonly float cell;
            readonly int i0, j0, width, depth, forestCells, builtCells;
            readonly GeoLandClass[] classes;
            readonly int[] forest, built, known;

            public Neighbourhood(GeoLandCover cover, Vector3 origin, Vector3 size)
            {
                this.cover = cover;
                cell = cover.cellSize > 0 ? cover.cellSize : 10;
                float margin = Mathf.Max(ForestRadius, BuiltRadius);
                i0 = Mathf.FloorToInt((origin.x - margin - cover.origin.x) / cell);
                j0 = Mathf.FloorToInt((origin.z - margin - cover.origin.y) / cell);
                width = Mathf.FloorToInt((origin.x + size.x + margin - cover.origin.x) / cell) - i0 + 1;
                depth = Mathf.FloorToInt((origin.z + size.z + margin - cover.origin.y) / cell) - j0 + 1;
                forestCells = Mathf.Max(1, Mathf.RoundToInt(ForestRadius / cell));
                builtCells = Mathf.Max(1, Mathf.RoundToInt(BuiltRadius / cell));
                classes = new GeoLandClass[width * depth];
                forest = new int[(width + 1) * (depth + 1)];
                built = new int[forest.Length];
                known = new int[forest.Length];
                for (int j = 0; j < depth; j++)
                    for (int i = 0; i < width; i++)
                    {
                        cover.Sample(cover.origin.x + (i0 + i + 0.5f) * cell, cover.origin.y + (j0 + j + 0.5f) * cell, out var value);
                        classes[j * width + i] = value;
                        int s = (j + 1) * (width + 1) + i + 1, left = s - 1, below = s - width - 1, corner = s - width - 2;
                        forest[s] = (value == GeoLandClass.Forest ? 1 : 0) + forest[left] + forest[below] - forest[corner];
                        built[s] = (Built(value) ? 1 : 0) + built[left] + built[below] - built[corner];
                        known[s] = (value != GeoLandClass.None ? 1 : 0) + known[left] + known[below] - known[corner];
                    }
            }

            public bool KindAt(float x, float z, out Kind kind)
            {
                kind = Kind.Row;
                int i = Mathf.FloorToInt((x - cover.origin.x) / cell) - i0, j = Mathf.FloorToInt((z - cover.origin.y) / cell) - j0;
                if (i < 0 || j < 0 || i >= width || j >= depth) return false;
                var value = classes[j * width + i];
                if (value == GeoLandClass.None) return false;
                if (value == GeoLandClass.Orchard) kind = Kind.Orchard;
                else if ((value == GeoLandClass.Forest || value == GeoLandClass.Scrub) && Share(forest, i, j, forestCells) >= ForestShare) kind = Kind.Forest;
                else if (Built(value) || Share(built, i, j, builtCells) >= BuiltShare) kind = Kind.Settlement;
                return true;
            }

            float Share(int[] table, int i, int j, int r)
            {
                int a0 = Mathf.Max(0, i - r), a1 = Mathf.Min(width - 1, i + r), b0 = Mathf.Max(0, j - r), b1 = Mathf.Min(depth - 1, j + r);
                int count = Box(known, a0, a1, b0, b1);
                return count > 0 ? Box(table, a0, a1, b0, b1) / (float)count : 0;
            }

            int Box(int[] table, int a0, int a1, int b0, int b1)
            {
                int w = width + 1;
                return table[(b1 + 1) * w + a1 + 1] - table[b0 * w + a1 + 1] - table[(b1 + 1) * w + a0] + table[b0 * w + a0];
            }
        }
    }
}
