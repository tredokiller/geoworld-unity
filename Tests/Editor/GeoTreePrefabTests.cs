using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // Your own tree prefabs: the usable ones, their height, a prefab and a scale for each tree, and worlds that use them
    // (the terrain's trees in a merged world, a copy per tree in an editable one).
    public class GeoTreePrefabTests
    {
        const string Sample = "Assets/GeoWorld/Samples/RiversideVillage";
        readonly List<Object> owned = new List<Object>();
        readonly List<string> assets = new List<string>();
        string folder;

        [SetUp] public void MakeFolder()
        {
            folder = GeoPaths.Project + "/Generated/TreePrefabTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            assets.Add(folder);
        }

        [TearDown] public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var path in assets) AssetDatabase.DeleteAsset(path);
            assets.Clear();
        }

        // A tree prefab: a cube trunk lifted so that the tree's top is `top` metres above the pivot.
        GameObject Tree(string name, float top, bool renderer = true)
        {
            var root = new GameObject(name);
            try
            {
                if (renderer)
                {
                    var child = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    child.transform.SetParent(root.transform, false);
                    child.transform.localPosition = new Vector3(0, top - 0.5f, 0);
                    root.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                    var trunk = root.AddComponent<MeshRenderer>();
                    trunk.sharedMaterial = child.GetComponent<MeshRenderer>().sharedMaterial;
                    // as tree prefabs are: Unity's terrain wants a LOD Group on a tree (or its old Soft Occlusion shader)
                    root.AddComponent<LODGroup>().SetLODs(new[] { new LOD(0.01f, new Renderer[] { trunk, child.GetComponent<MeshRenderer>() }) });
                }
                return PrefabUtility.SaveAsPrefabAsset(root, folder + "/" + name + ".prefab");
            }
            finally { Object.DestroyImmediate(root); }
        }

        static GenerationSettings Settings(GeoBuildingMode mode) => new GenerationSettings
        {
            heightResolution = 129, textureResolution = 128, roadPaintResolution = 256, buildingMode = mode, satelliteImagery = false,
            ruinModels = GeoRuinUse.Off, homeModels = GeoHomeUse.Off, groundTextures = false, maxProps = 2000
        };

        [Test] public void Usable_KeepsPrefabsThatCanBeTerrainTrees()
        {
            var oak = Tree("Oak", 12);
            var empty = Tree("Empty", 1, renderer: false);
            var notes = new List<string>();
            var usable = GeoTerrainTrees.Usable(new[] { oak, null, empty, oak }, notes);
            Assert.That(usable, Is.EqualTo(new[] { oak }));
            Assert.That(notes.Single(), Does.Contain("Empty"));
            Assert.That(GeoTerrainTrees.Usable(null, null), Is.Empty);
        }

        [Test] public void Height_IsTheTopOfTheFirstLevelOfDetail()
        {
            Assert.That(GeoTerrainTrees.Height(Tree("Oak", 12)), Is.EqualTo(12).Within(1e-3f));
            Assert.That(GeoTerrainTrees.Height(Tree("Bush", 2)), Is.EqualTo(2).Within(1e-3f));
        }

        [Test] public void Pick_ScalesToTheTreesHeightWithinHalfAndTwice()
        {
            var heights = new[] { 10f };
            GeoTerrainTrees.Pick(new PropPlacement { id = "a", height = 15 }, 1, heights, out int prefab, out float scale);
            Assert.That(prefab, Is.Zero); Assert.That(scale, Is.EqualTo(1.5f).Within(1e-4f));
            GeoTerrainTrees.Pick(new PropPlacement { id = "a", height = 2 }, 1, heights, out _, out scale);
            Assert.That(scale, Is.EqualTo(0.5f));
            GeoTerrainTrees.Pick(new PropPlacement { id = "a", height = 40 }, 1, heights, out _, out scale);
            Assert.That(scale, Is.EqualTo(2f));
            GeoTerrainTrees.Pick(new PropPlacement { id = "a" }, 1, heights, out _, out scale);
            Assert.That(scale, Is.EqualTo(1f));
            // several prefabs: every one is used, and a tree always takes the same one
            var picks = Enumerable.Range(0, 200).Select(i => { GeoTerrainTrees.Pick(new PropPlacement { id = "t" + i, height = 8 }, 1, new[] { 8f, 8f, 8f }, out int p, out _); return p; }).ToList();
            Assert.That(picks.Distinct().OrderBy(p => p), Is.EqualTo(new[] { 0, 1, 2 }));
            GeoTerrainTrees.Pick(new PropPlacement { id = "t7", height = 8 }, 1, new[] { 8f, 8f, 8f }, out int again, out _);
            Assert.That(again, Is.EqualTo(picks[7]));
        }

        [Test] public void MergedWorld_TheTreesAreTheTerrainsWithYourPrefabs()
        {
            var oak = Tree("Oak", 12); var birch = Tree("Birch", 9);
            var settings = Settings(GeoBuildingMode.Merged); settings.treePrefabs = new[] { oak, birch };
            var world = GeoGenerator.Generate(Sample + "/environment.json", Sample + "/buildings.json", settings, null, false);
            owned.Add(world.gameObject); assets.Add(world.assetPath);
            var data = world.terrain.terrainData;
            Assert.That(data.treePrototypes.Select(p => p.prefab), Is.EqualTo(new[] { oak, birch }));
            Assert.That(data.treeInstanceCount, Is.GreaterThan(0));
            Assert.That(world.terrain.drawTreesAndFoliage, "Unity's terrain draws them");
            Assert.That(world.GetComponentsInChildren<GeoInstancedMeshes>(true), Is.Empty);
            Assert.That(data.treeInstances.All(t => Mathf.Approximately(t.widthScale, t.heightScale) && t.heightScale >= 0.5f && t.heightScale <= 2f));
        }

        [Test] public void EditableWorld_EachTreeIsACopyOfYourPrefab()
        {
            var oak = Tree("Oak", 12);
            var settings = Settings(GeoBuildingMode.Editable); settings.treePrefabs = new[] { oak };
            var world = GeoGenerator.Generate(Sample + "/environment.json", Sample + "/buildings.json", settings, null, false);
            owned.Add(world.gameObject); assets.Add(world.assetPath);
            var trees = world.GetComponentsInChildren<GeoPlacedProp>(true).Where(p => p.kind == "tree" && p.placementSource != "manual_photo").ToList();
            Assert.That(trees, Is.Not.Empty);
            foreach (var tree in trees)
            {
                var copy = tree.transform.Cast<Transform>().Select(t => t.gameObject).Single();
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(copy), Is.EqualTo(oak));
            }
        }
    }
}
