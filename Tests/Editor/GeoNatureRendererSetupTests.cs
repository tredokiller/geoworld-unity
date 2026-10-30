using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // Nature Renderer for a world seen from the air: the settings' tree distance, the tree prefabs' levels of detail, the far
    // fade of Geo World's tree shader, and swapping a tile's grass where the project has no Nature Renderer.
    public class GeoNatureRendererSetupTests
    {
        readonly List<Object> owned = new List<Object>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
        }

        // A tree prefab of a 10 m cube with a level of detail before the last one down to cardAt and culled at cullAt.
        GameObject Tree(float cardAt, float cullAt)
        {
            var root = new GameObject("Tree");
            owned.Add(root);
            var near = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var card = GameObject.CreatePrimitive(PrimitiveType.Cube);
            foreach (var part in new[] { near, card }) { part.transform.SetParent(root.transform, false); part.transform.localScale = Vector3.one * 10; }
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(cardAt, new Renderer[] { near.GetComponent<Renderer>() }), new LOD(cullAt, new Renderer[] { card.GetComponent<Renderer>() }) });
            group.RecalculateBounds();
            return root;
        }

        [Test] public void TreeLods_AreCheckedForTheAir()
        {
            Assert.That(GeoNatureRendererTrees.CheckLods(Tree(0.06f, 0.003f), 1600), Is.Null, "Cards from 6 % of the screen, culled beyond 1.6 km.");
            string note = GeoNatureRendererTrees.CheckLods(Tree(0.3f, 0.01f), 1600);
            Assert.That(note, Does.Contain("last level of detail starts at"));
            Assert.That(note, Does.Contain("culled at"));
            var group = Tree(0.06f, 0.003f).GetComponent<LODGroup>();
            float distance = GeoNatureRendererTrees.CullDistance(group, 0.003f, 60);
            Assert.That(GeoNatureRendererTrees.CullHeight(group, distance, 60), Is.EqualTo(0.003f).Within(0.00001f));
        }

        [Test] public void Settings_TakeTheTreeDistanceFromTheLoadRadius()
        {
            var go = new GameObject("World");
            owned.Add(go);
            var world = go.AddComponent<GeoTiledWorld>();
            var streamer = go.AddComponent<GeoTileStreamer>();
            streamer.world = world;
            streamer.SetRadius(2000);
            var settings = go.AddComponent<GeoNatureRendererSettings>();
            Assert.That(settings.TreeDistance, Is.EqualTo(1600), "400 m short of the 2 km load radius.");
            streamer.SetRadius(1000);
            Assert.That(settings.TreeDistance, Is.EqualTo(600));
            settings.treeDistanceFromLoadRadius = false;
            settings.treeRenderDistance = 900;
            Assert.That(settings.TreeDistance, Is.EqualTo(900));
        }

        [Test] public void TreeShader_FadesInEveryPass_WithOneMaterialLayout()
        {
            var shader = (string)typeof(GeoNatureRendererTrees).GetField("TreeShader", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
            Assert.That(Regex.Matches(shader, @"Pass\s*\{").Count, Is.EqualTo(4));
            Assert.That(Regex.Matches(shader, @"FarFade\(input\.positionOS\.xyz\)").Count, Is.EqualTo(4), "Every pass shrinks the tree the same way, its shadow and depth too.");
            Assert.That(Regex.Matches(shader, "CBUFFER_START").Count, Is.EqualTo(1), "One UnityPerMaterial for every pass (the SRP batcher and Nature Renderer need one layout).");
            StringAssert.Contains("float4 _ScaleFade;", shader);
            StringAssert.Contains("float _NatureRendererDistanceControl;", shader);
            StringAssert.Contains("_NatureRendererDistanceControl(", shader);
        }

        [Test] public void SwapGrass_WithoutNatureRenderer_GivesTheTerrainItsData()
        {
            if (GeoNatureRenderer.Available) Assert.Ignore("Nature Renderer is in this project.");
            var go = new GameObject("Tile");
            owned.Add(go);
            var terrain = go.AddComponent<Terrain>();
            var before = new TerrainData();
            var copy = new TerrainData();
            owned.Add(before); owned.Add(copy);
            terrain.terrainData = before;
            terrain.drawTreesAndFoliage = false;
            Assert.That(GeoNatureRenderer.Ensure(terrain), Is.Null);
            GeoNatureRenderer.SwapGrass(terrain, copy);
            Assert.That(terrain.terrainData, Is.SameAs(copy));
            Assert.That(terrain.drawTreesAndFoliage, Is.True, "Unity draws the grass where Nature Renderer does not.");
            GeoNatureRenderer.Restart(terrain);
        }
    }
}
