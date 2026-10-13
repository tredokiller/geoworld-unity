using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // Reduced copies of the library models: about half the triangles on the source's own vertices, used by a merged
    // city's bake and by nothing else.
    public class GeoModelReductionTests
    {
        readonly List<Object> owned = new List<Object>();
        readonly List<string> assets = new List<string>();

        [TearDown] public void Cleanup()
        {
            GeoModelDetail.Use(null);
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var path in assets) AssetDatabase.DeleteAsset(path);
            assets.Clear();
        }

        // A bumpy 10 x 10 m sheet of 40 x 40 cells, its two halves in two sub-meshes (two materials).
        static Mesh Sheet()
        {
            const int n = 40;
            var vertices = new List<Vector3>(); var uvs = new List<Vector2>();
            for (int z = 0; z <= n; z++) for (int x = 0; x <= n; x++)
            {
                float px = x * 10f / n, pz = z * 10f / n;
                vertices.Add(new Vector3(px, 0.3f * Mathf.Sin(px * 0.7f) * Mathf.Cos(pz * 0.5f), pz));
                uvs.Add(new Vector2(px / 10, pz / 10));
            }
            var left = new List<int>(); var right = new List<int>();
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
            {
                int a = z * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                var list = x < n / 2 ? left : right;
                list.AddRange(new[] { a, c, b, b, c, d });
            }
            var mesh = new Mesh { name = "Sheet" };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2; mesh.SetTriangles(left, 0); mesh.SetTriangles(right, 1);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            return mesh;
        }

        [Test] public void Reduce_HalvesTheTrianglesOnTheSourcesOwnVertices()
        {
            var source = Sheet(); owned.Add(source);
            var reduced = GeoModelReduction.Reduce(source, 0.5f); owned.Add(reduced);
            Assert.That(reduced, Is.Not.Null);
            long before = GeoModelReduction.Triangles(source), after = GeoModelReduction.Triangles(reduced);
            Assert.That(after, Is.LessThanOrEqualTo(before / 2 + 2));
            Assert.That(after, Is.GreaterThan(0));
            Assert.That(reduced.subMeshCount, Is.EqualTo(2));
            Assert.That(reduced.GetIndexCount(0), Is.GreaterThan(0)); Assert.That(reduced.GetIndexCount(1), Is.GreaterThan(0));
            // every vertex is one of the source's, with its texture coordinate
            var known = new Dictionary<Vector3, Vector2>();
            var sv = source.vertices; var su = source.uv;
            for (int i = 0; i < sv.Length; i++) known[sv[i]] = su[i];
            var rv = reduced.vertices; var ru = reduced.uv;
            for (int i = 0; i < rv.Length; i++)
            {
                Assert.That(known.ContainsKey(rv[i]), "vertex " + rv[i] + " is not a source vertex");
                Assert.That(ru[i], Is.EqualTo(known[rv[i]]));
            }
            Assert.That(reduced.vertexCount, Is.LessThan(source.vertexCount));
            Assert.That(reduced.bounds.size.x, Is.EqualTo(10).Within(0.01f)); Assert.That(reduced.bounds.size.z, Is.EqualTo(10).Within(0.01f));
            Assert.That(reduced.normals.Length, Is.EqualTo(rv.Length)); Assert.That(reduced.tangents.Length, Is.EqualTo(rv.Length));
        }

        [Test] public void Detail_IsTheReducedCopyOnlyWhileInUse()
        {
            var source = Sheet(); owned.Add(source);
            var reduced = GeoModelReduction.Reduce(source, 0.5f); owned.Add(reduced);
            var set = ScriptableObject.CreateInstance<GeoReducedModels>(); owned.Add(set);
            set.entries.Add(new GeoReducedModels.Entry { source = source, reduced = reduced });
            Assert.That(GeoModelDetail.For(source), Is.SameAs(source));
            GeoModelDetail.Use(set);
            Assert.That(GeoModelDetail.For(source), Is.SameAs(reduced));
            var other = new Mesh(); owned.Add(other);
            Assert.That(GeoModelDetail.For(other), Is.SameAs(other));
            Assert.That(GeoModelDetail.For(null), Is.Null);
            GeoModelDetail.Use(null);
            Assert.That(GeoModelDetail.For(source), Is.SameAs(source));
        }

        static long BuildingTriangles(GeoWorldRoot world)
        {
            long count = 0;
            foreach (var filter in world.GetComponentsInChildren<MeshFilter>())
                if (filter.sharedMesh && filter.sharedMesh.name.StartsWith("Buildings")) count += GeoModelReduction.Triangles(filter.sharedMesh);
            return count;
        }

        [Test] public void MergedCity_DrawsItsModelsReduced()
        {
            var ruins = GeoRuinImporter.Library; var homes = GeoHomeImporter.Library;
            Assume.That(ruins && homes, "Needs the imported ruin and home libraries.");
            const string sample = "Assets/GeoWorld/Samples/RiversideVillage";
            GenerationSettings Settings(bool reduced) => new GenerationSettings
            {
                heightResolution = 129, textureResolution = 128, roadPaintResolution = 256, buildingMode = GeoBuildingMode.Merged,
                ruinModels = GeoRuinUse.AllDamage, ruinLibrary = ruins, homeModels = GeoHomeUse.AllStanding, homeLibrary = homes,
                satelliteImagery = false, groundTextures = false, trees = false, mappedProps = false, vegetation = false, reducedModels = reduced, instancedHomes = false
            };
            var full = GeoGenerator.Generate(sample + "/environment.json", sample + "/buildings.json", Settings(false), null, false);
            owned.Add(full.gameObject); assets.Add(full.assetPath);
            var reducedWorld = GeoGenerator.Generate(sample + "/environment.json", sample + "/buildings.json", Settings(true), null, false);
            owned.Add(reducedWorld.gameObject); assets.Add(reducedWorld.assetPath);
            Assert.That(reducedWorld.buildingCount, Is.EqualTo(full.buildingCount));
            long a = BuildingTriangles(full), b = BuildingTriangles(reducedWorld);
            Assert.That(a, Is.GreaterThan(0));
            // hard-surface models built of small boxes reduce little without losing detail (about 7% over the library)
            Assert.That(b, Is.LessThan(a), $"{b} of {a} triangles");
            Assert.That(reducedWorld.report, Does.Contain("reduced copies"));
            Assert.That(GeoModelDetail.Reduced, Is.False, "Nothing stays in use after the bake.");
        }
    }
}
