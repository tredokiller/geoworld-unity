using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // The tiles' trees as tall as what they stand in (GeoTreeHeights): a wood, a shelterbelt among fields, an orchard and a
    // village, with the three tree prefabs of a game (9.8, 15.1 and 13.2 m).
    public class GeoTreeHeightTests
    {
        static readonly float[] Prefabs = { 9.8f, 15.1f, 13.2f };
        readonly List<Object> owned = new List<Object>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        // Tiles of 100 m with cells of 10 m, far from any real world (tile 2000, 2000 and on).
        GeoLandCover World()
        {
            var go = new GameObject("Tree heights test");
            owned.Add(go);
            var cover = go.AddComponent<GeoLandCover>();
            cover.tileSize = 100; cover.cellSize = 10;
            cover.tiles = new List<GeoLandCoverData>
            {
                Tile(2000, 2000, (c, r) => GeoLandClass.Forest),                                          // a wood
                Tile(2001, 2000, (c, r) => c == 5 ? GeoLandClass.Forest : GeoLandClass.Cropland),         // a 10 m belt among fields
                Tile(2000, 2001, (c, r) => GeoLandClass.Orchard),
                Tile(2001, 2001, (c, r) => c < 5 ? GeoLandClass.Urban : GeoLandClass.Grass),              // a village, a meadow by it
            };
            return cover;
        }

        GeoLandCoverData Tile(int x, int z, Func<int, int, GeoLandClass> cell)
        {
            var data = ScriptableObject.CreateInstance<GeoLandCoverData>();
            owned.Add(data);
            var cells = new byte[100];
            for (int row = 0; row < 10; row++) for (int column = 0; column < 10; column++) cells[row * 10 + column] = (byte)cell(column, row);
            data.Set(new Vector2Int(x, z), new Rect(x * 100f, z * 100f, 100, 100), 10, cells, (string[])GeoLandCover.Names.Clone());
            return data;
        }

        // A terrain of 300 x 200 m over the four tiles and a column of nothing east of them.
        static readonly Vector3 Origin = new Vector3(200000, 0, 200000), Size = new Vector3(300, 50, 200);

        // A tree at world x, z of a prototype and a height in metres, a little wider than tall (width over height 1.1).
        static TreeInstance Tree(float x, float z, int prototype, float height)
        {
            float scale = height / Prefabs[prototype];
            return new TreeInstance { position = new Vector3((x - Origin.x) / Size.x, 0, (z - Origin.z) / Size.z), prototypeIndex = prototype,
                heightScale = scale, widthScale = scale * 1.1f, color = Color.white, lightmapColor = Color.white };
        }

        static float Height(TreeInstance tree) => tree.heightScale * Prefabs[tree.prototypeIndex];

        [Test] public void Trees_TakeTheHeightOfWhatTheyStandIn()
        {
            var cover = World();
            var heights = cover.treeHeights;
            var trees = new List<TreeInstance>();
            var kinds = new List<string>();
            void Add(string kind, float x, float z, int prototype, float height) { trees.Add(Tree(Origin.x + x, Origin.z + z, prototype, height)); kinds.Add(kind); }
            for (int i = 0; i < 20; i++)
            {
                Add("forest", 20 + i * 3, 50, i % 3, 5 + i * 0.25f);
                Add("row", 155, 10 + i, i % 3, 7); // more than 60 m from the village
                Add("orchard", 20 + i * 3, 150, i % 3, 6 + i * 0.2f);
                Add("settlement", 110 + i * 2, 120 + i, i % 3, 8);
                Add("settlement", 170 + i, 150, i % 3, 9); // the meadow next to the village
            }
            Add("explicit", 50, 40, 1, 18);  // a mapped tree with its own height in the wood
            Add("off", 250, 50, 0, 7);       // no land cover there
            var before = trees.ToArray();
            var after = trees.ToArray();
            int changed = heights.Apply(cover, after, Origin, Size, Prefabs);

            Assert.That(changed, Is.EqualTo(before.Length - 2));
            for (int i = 0; i < after.Length; i++)
            {
                float h = Height(after[i]), scale = after[i].heightScale;
                string at = kinds[i] + " tree " + i + ": " + h.ToString("0.0") + " m on prefab " + after[i].prototypeIndex;
                switch (kinds[i])
                {
                    case "forest": Assert.That(h, Is.InRange(heights.forest.x, heights.forest.y), at); break;
                    case "row": Assert.That(h, Is.InRange(heights.rows.x, heights.rows.y), at); break;
                    // a 4 m tree would be the smallest prefab at less than half its size: half is the least
                    case "orchard": Assert.That(h, Is.InRange(Prefabs[0] * 0.5f - 0.001f, heights.orchard.y), at); break;
                    case "settlement": Assert.That(h, Is.InRange(heights.settlement.x, heights.settlement.y), at); break;
                    default: Assert.That(after[i].heightScale, Is.EqualTo(before[i].heightScale), at + " keeps its height"); Assert.That(after[i].prototypeIndex, Is.EqualTo(before[i].prototypeIndex)); break;
                }
                Assert.That(scale, Is.InRange(0.5f, 2f), at);
                Assert.That(after[i].widthScale / after[i].heightScale, Is.EqualTo(1.1f).Within(0.0001f), at + ": the width keeps its proportion");
            }
            // a forest tree takes a prefab near its height: never the small one at more than one and a half times its size
            for (int i = 0; i < after.Length; i++)
                if (kinds[i] == "forest") Assert.That(after[i].prototypeIndex == 0 ? after[i].heightScale : 0, Is.LessThanOrEqualTo(1.5f));

            var again = before.Clone() as TreeInstance[];
            heights.Apply(cover, again, Origin, Size, Prefabs);
            for (int i = 0; i < again.Length; i++)
            {
                Assert.That(again[i].heightScale, Is.EqualTo(after[i].heightScale), "The same tile looks the same every time it loads.");
                Assert.That(again[i].prototypeIndex, Is.EqualTo(after[i].prototypeIndex));
            }

            // the world a tile's loader asks for its trees' heights
            Assert.That(GeoLandCover.At(Origin.x + 50, Origin.z + 50), Is.SameAs(cover));
            Assert.That(GeoLandCover.At(Origin.x + 250, Origin.z + 50), Is.Null);
        }

        [Test] public void PrefabHeight_IsTheTopOfItsMeshes()
        {
            var root = new GameObject("Tree");
            owned.Add(root);
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cube);
            trunk.transform.SetParent(root.transform, false);
            trunk.transform.localPosition = new Vector3(0, 3, 0);
            trunk.transform.localScale = new Vector3(1, 6, 1);
            Assert.That(GeoTreeHeights.PrefabHeight(root), Is.EqualTo(6).Within(0.001f));
            var heights = GeoTreeHeights.PrototypeHeights(new[] { new TreePrototype { prefab = root }, new TreePrototype() });
            Assert.That(heights[0], Is.EqualTo(6).Within(0.001f));
            Assert.That(heights[1], Is.Zero, "No prefab.");
            Assert.That(GeoTerrainTrees.Height(root), Is.EqualTo(heights[0]), "The generator measures prefabs as the runtime does.");
        }
    }
}
