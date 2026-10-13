using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // Homes of a merged city as instances: the library's own meshes with a transform, which must put every vertex where
    // a baked copy would have it (but for the foot, which a plinth replaces).
    public class GeoHomeInstanceTests
    {
        readonly List<Object> owned = new List<Object>();
        readonly List<string> assets = new List<string>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var path in assets) AssetDatabase.DeleteAsset(path);
            assets.Clear();
        }

        static Bounds Of(IEnumerable<Vector3> points)
        {
            var list = points.ToList();
            var b = new Bounds(list[0], Vector3.zero);
            foreach (var p in list) b.Encapsulate(p);
            return b;
        }

        // the vertices one sub-mesh of a source mesh uses, mapped by a matrix, without the foot
        static IEnumerable<Vector3> Mapped(Mesh mesh, int sub, Matrix4x4 m, bool foot)
        {
            if (!mesh || sub >= mesh.subMeshCount) yield break;
            var v = mesh.vertices;
            foreach (int i in mesh.GetTriangles(sub).Distinct())
                if (!foot || v[i].y >= 0.05f) yield return m.MultiplyPoint3x4(v[i]);
        }

        [Test] public void Instance_PutsTheModelWhereTheBakedCopyIs([Values(false, true)] bool turn, [Values(false, true)] bool mirror)
        {
            var library = GeoHomeImporter.Library;
            Assume.That(library && library.models.Count > 0, "Needs the imported home library.");
            var model = library.models.First(m => m && m.body && m.Variant(m.defaultVariant));
            var go = new GameObject("Home instance test"); owned.Add(go);
            var building = go.AddComponent<GeoBuilding>();
            GeoBuildingBuilder.Import(building, new BuildingFeature
            {
                id = "test/home", category = "house", x = 30, z = -20, width = 9, length = 13, rotation = 27, height = 6.5f, levels = 1,
                footprint = new float[] { -6.5f, -4.5f, 6.5f, -4.5f, 6.5f, 4.5f, -6.5f, 4.5f }
            }, null);
            building.home = model; building.homeLibrary = library; building.homeMode = GeoRuinMode.Model;
            building.homeTurn = turn; building.homeMirror = mirror;
            var context = new GeoBuildingContext(building);
            const float depth = 0.6f;
            GeoCityBuilder.HomeInstance(building, context, depth, out var body, out var roof, out var variant, out var roofMaterial);
            using (var baked = GeoHomes.Build(context, depth))
            {
                var parts = baked.parts.Where(p => p.name == "Model").ToList();
                Assert.That(parts.Count, Is.GreaterThanOrEqualTo(1));
                // the atlas part: sub-mesh 0 of the body, the fixed roof and the roof variant
                var atlas = Mapped(model.body, 0, body, true).Concat(Mapped(model.roofFixed, 0, body, true)).Concat(Mapped(variant, 0, roof, false));
                var expected = Of(atlas);
                var actual = Of(parts[0].mesh.vertices.Where(p => p.y > -0.05f));
                Assert.That(Vector3.Distance(expected.min, actual.min), Is.LessThan(0.002f), $"min {expected.min} vs {actual.min}");
                Assert.That(Vector3.Distance(expected.max, actual.max), Is.LessThan(0.002f), $"max {expected.max} vs {actual.max}");
                // the baked foot reaches down to the foundation; the instance's stops at the ground (a plinth fills it)
                Assert.That(parts[0].mesh.bounds.min.y, Is.EqualTo(-depth).Within(0.01f));
                if (parts.Count > 1)
                {
                    var roofPoints = Mapped(model.body, 1, body, true).Concat(Mapped(model.roofFixed, 1, body, true)).Concat(Mapped(variant, 1, roof, false)).ToList();
                    if (roofPoints.Count > 0)
                    {
                        var r = Of(roofPoints); var rb = Of(parts[1].mesh.vertices.Where(p => p.y > -0.05f));
                        Assert.That(Vector3.Distance(r.center, rb.center), Is.LessThan(0.002f));
                        Assert.That(Vector3.Distance(r.size, rb.size), Is.LessThan(0.004f));
                    }
                }
            }
        }

        [Test] public void Place_TurnsAMatrixIntoTheSameTransform()
        {
            var go = new GameObject("Place test"); owned.Add(go);
            foreach (var (angle, sx, sy, sz) in new[] { (20f, 1.3f, 0.9f, 1.1f), (200f, -1.2f, 1.4f, 0.8f), (95f, 0.7f, 1f, -1.5f), (310f, -0.9f, 1.1f, -1.2f) })
            {
                var axis = Quaternion.Euler(0, angle, 0) * Vector3.right; var across = Vector3.Cross(Vector3.up, axis) * -1;
                var m = new Matrix4x4(axis * sx, new Vector4(0, sy, 0, 0), across * sz, new Vector4(5, 2, -7, 1));
                GeoCityBuilder.Place(go.transform, m);
                foreach (var p in new[] { new Vector3(1, 2, 3), new Vector3(-4, 0.5f, 2), Vector3.zero })
                    Assert.That(Vector3.Distance(go.transform.TransformPoint(p), m.MultiplyPoint3x4(p)), Is.LessThan(1e-4f), $"{angle} {sx} {sz} {p}");
            }
        }

        [Test] public void MergedCity_DrawsHomesAsInstancesOfTheLibrary()
        {
            var homes = GeoHomeImporter.Library;
            Assume.That(homes, "Needs the imported home library.");
            const string sample = "Assets/GeoWorld/Samples/RiversideVillage";
            GenerationSettings Settings(bool instanced) => new GenerationSettings
            {
                heightResolution = 129, textureResolution = 128, roadPaintResolution = 256, buildingMode = GeoBuildingMode.Merged,
                ruinModels = GeoRuinUse.Off, homeModels = GeoHomeUse.AllStanding, homeLibrary = homes, instancedHomes = instanced,
                satelliteImagery = false, groundTextures = false, trees = false, mappedProps = false, vegetation = false
            };
            var baked = GeoGenerator.Generate(sample + "/environment.json", sample + "/buildings.json", Settings(false), null, false);
            owned.Add(baked.gameObject); assets.Add(baked.assetPath);
            var world = GeoGenerator.Generate(sample + "/environment.json", sample + "/buildings.json", Settings(true), null, false);
            owned.Add(world.gameObject); assets.Add(world.assetPath);
            Assert.That(world.buildingCount, Is.EqualTo(baked.buildingCount));
            var instances = world.GetComponentsInChildren<Transform>().Where(t => t.name.StartsWith("Home ")).ToList();
            Assert.That(instances.Count, Is.GreaterThan(0), world.report);
            Assert.That(world.report, Does.Contain(instances.Count + " homes are instances"));
            foreach (var home in instances)
            {
                Assert.That(home.GetComponent<LODGroup>(), Is.Not.Null, "a home is left out when it is tiny on screen");
                foreach (var filter in home.GetComponentsInChildren<MeshFilter>())
                {
                    string path = AssetDatabase.GetAssetPath(filter.sharedMesh);
                    Assert.That(path, Is.Not.Empty.And.Not.EqualTo(world.assetPath), "the library's own mesh, not a copy");
                    Assert.That(filter.GetComponent<MeshRenderer>().sharedMaterials.All(m => m), "every sub-mesh has a material");
                }
            }
            long Triangles(GeoWorldRoot w) => w.GetComponentsInChildren<MeshFilter>().Where(f => f.sharedMesh && f.sharedMesh.name.StartsWith("Buildings"))
                .Sum(f => GeoModelReduction.Triangles(f.sharedMesh));
            Assert.That(Triangles(world), Is.LessThan(Triangles(baked) / 4), "the chunks no longer hold the homes");
        }
    }
}
