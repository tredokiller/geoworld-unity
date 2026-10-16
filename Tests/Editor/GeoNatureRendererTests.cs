using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // Trees for Nature Renderer: the tree shader with its procedural instancing (compiled here against a stand-in for
    // Nature Renderer's include, which comes with Nature Renderer), and trees planted as the terrain's own.
    public class GeoNatureRendererTests
    {
        readonly List<Object> owned = new List<Object>();
        readonly List<string> folders = new List<string>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var folder in folders) AssetDatabase.DeleteAsset(folder);
            folders.Clear();
        }

        [Test] public void TreeShader_CompilesWithNatureRenderersTagAndInclude()
        {
            string folder = GeoPaths.Project + "/Generated/NatureRendererTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            folders.Add(folder);
            Directory.CreateDirectory(folder);
            string include = folder + "/Nature Renderer.templatex";
            File.WriteAllText(include, "#ifndef GEO_TEST_NATURE_RENDERER\n#define GEO_TEST_NATURE_RENDERER\nvoid SetupNatureRenderer() {}\n#endif\n");
            AssetDatabase.Refresh();

            var shader = GeoNatureRendererTrees.WriteShader(folder + "/Tree.shader", include);
            Assert.That(shader, Is.Not.Null);
            var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity == ShaderCompilerMessageSeverity.Error).Select(m => m.message).ToList();
            Assert.That(errors, Is.Empty);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            Assert.That(shader.FindSubshaderTagValue(0, new ShaderTagId("NatureRendererInstancing")).name, Is.EqualTo("True").IgnoreCase);
            Assert.That(File.ReadAllText(folder + "/Tree.shader"), Does.Contain("#include \"" + include + "\""));
            Assert.That(shader.passCount, Is.EqualTo(4)); // lit, shadows, depth, depth and normals

            var material = new Material(shader) { enableInstancing = true };
            owned.Add(material);
            Assert.That(material.HasProperty("_BaseColor"));
            // Nature Renderer can draw it; not URP's own Lit
            Assert.That(GeoNatureRendererTrees.Drawable(material));
            var lit = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            owned.Add(lit);
            Assert.That(GeoNatureRendererTrees.Drawable(lit), Is.False);
        }

        [Test] public void PlantOnTerrain_TheTreesAreTheTerrainsOwn()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(200, 50, 200) };
            owned.Add(data);
            var go = Terrain.CreateTerrainGameObject(data);
            owned.Add(go);
            go.transform.position = new Vector3(100, 10, 100);
            var terrain = go.GetComponent<Terrain>();
            var trees = new List<PropPlacement>
            {
                new PropPlacement { id = "a", kind = "tree", position = new Vector3(150, 20, 250), width = 8, height = 14, yaw = 90 },
                new PropPlacement { id = "b", kind = "tree", position = new Vector3(299, 35, 101) },
                new PropPlacement { id = "outside", kind = "tree", position = new Vector3(50, 20, 150) },
            };

            int planted = GeoTerrainTrees.PlantOnTerrain(terrain, trees, 137, GeoTerrainTrees.Prefabs());

            Assert.That(planted, Is.EqualTo(2));
            Assert.That(data.treePrototypes.Length, Is.EqualTo(GeoTerrainTrees.Tones));
            Assert.That(data.treeInstanceCount, Is.EqualTo(2));
            var first = data.GetTreeInstance(0);
            Assert.That(first.position.x, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(first.position.y, Is.EqualTo(0.2f).Within(1e-4f));
            Assert.That(first.position.z, Is.EqualTo(0.75f).Within(1e-4f));
            Assert.That(first.widthScale, Is.EqualTo(2).Within(1e-4f)); // 8 m of the model's 4
            Assert.That(first.heightScale, Is.EqualTo(2).Within(1e-4f)); // 14 m of the model's 7
            Assert.That(first.rotation, Is.EqualTo(Mathf.PI / 2).Within(1e-4f));
            Assert.That(data.GetTreeInstance(1).heightScale, Is.EqualTo(1)); // no size: the model's own
            // the same placements give the same greens
            int[] tones = Enumerable.Range(0, 2).Select(i => data.GetTreeInstance(i).prototypeIndex).ToArray();
            GeoTerrainTrees.PlantOnTerrain(terrain, trees, 137, GeoTerrainTrees.Prefabs());
            Assert.That(Enumerable.Range(0, 2).Select(i => data.GetTreeInstance(i).prototypeIndex), Is.EqualTo(tones));
        }

        [Test] public void WithNatureRenderer_TheTerrainGetsItsComponentAndPatches()
        {
            if (!GeoNatureRendererTrees.Installed) Assert.Ignore("Nature Renderer is not in this project.");
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(1000, 50, 1000) };
            data.SetDetailResolution(0, 32); // as the generator's terrains: no detail map
            owned.Add(data);
            var go = Terrain.CreateTerrainGameObject(data);
            owned.Add(go);
            var terrain = go.GetComponent<Terrain>();
            var trees = new List<PropPlacement> { new PropPlacement { id = "a", kind = "tree", position = new Vector3(500, 10, 500) } };

            Assert.That(GeoNatureRendererTrees.Plant(terrain, trees, 1), Is.EqualTo(1));
            var natureRenderer = terrain.GetComponent(GeoNatureRendererTrees.Component);
            Assert.That(natureRenderer, Is.Not.Null);
            // started a moment after its tile loads, with Unity's own tree drawing kept off meanwhile
            Assert.That(terrain.GetComponent<GeoNatureRendererStart>(), Is.Not.Null);
            Assert.That(GeoNatureRendererTrees.Component.GetProperty("EnableUnityRendererWhenDisabled").GetValue(natureRenderer), Is.False);
            GeoNatureRendererTrees.Remove(terrain);
            Assert.That(terrain.GetComponent<GeoNatureRendererStart>(), Is.Null);
            Assert.That((bool)terrain.GetComponent(GeoNatureRendererTrees.Component), Is.False);
            GeoNatureRendererTrees.Prepare(terrain);
            // without a detail map Nature Renderer does not start (its Optimize Patch Size then picks the patches itself)
            Assert.That(data.detailResolution, Is.EqualTo(128));
            Assert.That(data.treePrototypes.All(p => p.prefab.GetComponent<MeshRenderer>().sharedMaterials.All(m => m.shader.name == "GeoWorld/Tree (Nature Renderer)")));
        }

        [Test] public void WithoutNatureRenderer_TheWindowAndGeneratorKeepGeoWorldsTrees()
        {
            // this project does not have Nature Renderer: nothing to find, and removing it from a terrain is harmless
            if (GeoNatureRendererTrees.Installed) Assert.Ignore("Nature Renderer is in this project.");
            Assert.That(GeoNatureRendererTrees.Component, Is.Null);
            var go = new GameObject("Terrain", typeof(Terrain));
            owned.Add(go);
            Assert.DoesNotThrow(() => GeoNatureRendererTrees.Remove(go.GetComponent<Terrain>()));
            Assert.Throws<InvalidOperationException>(() => GeoNatureRendererTrees.Plant(go.GetComponent<Terrain>(), new List<PropPlacement>(), 1));
        }
    }
}
