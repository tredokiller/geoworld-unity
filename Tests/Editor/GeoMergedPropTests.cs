using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // A merged city bakes its trees, fences and street props into chunks, and shares the prop limit fairly.
    public class GeoMergedPropTests
    {
        readonly List<Object> disposable = new List<Object>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in disposable) if (o) Object.DestroyImmediate(o);
            disposable.Clear();
        }

        static EnvironmentData Env(float size) => new EnvironmentData { bounds = new GeoBounds { minX = 0, minZ = 0, maxX = size, maxZ = size } };
        static CanopyMask Mask(int size) =>
            new CanopyMask(new MaterialMapInfo { width = size, height = size, cellSize = 1, minX = 0, maxZ = size },
                Enumerable.Repeat(new Color32(255, 255, 255, 255), size * size).ToArray());
        static GenerationSettings Settings(int limit) => new GenerationSettings { treeSpacing = 8, vegetationDensity = 1, maxProps = limit };

        [Test] public void Baked_FencesAreLongerPiecesAndTheLimitIsHigher()
        {
            var env = Env(100); env.barriers.Add(new BarrierFeature { id = "fence", type = "fence", points = new float[] { 10, 50, 50, 50 } });
            var settings = Settings(100);
            var pieces = GeoPropPlanner.Plan(env, new BuildingsData(), settings, null, new List<string>());
            var baked = GeoPropPlanner.Plan(env, new BuildingsData(), settings, null, new List<string>(), null, true);
            Assert.That(pieces.Count, Is.EqualTo(20), "2 m pieces for editable objects");
            Assert.That(baked.Count, Is.EqualTo(5), "8 m pieces when baked");
            Assert.That(baked.Sum(p => p.length), Is.EqualTo(40).Within(0.001));
            Assert.That(GeoPropPlanner.MaxBaked, Is.GreaterThan(GeoPropPlanner.MaxObjects));
        }

        [Test] public void Canopy_IsThinnedEvenlyWhenTheLimitIsSmall()
        {
            var env = Env(200);
            var notes = new List<string>();
            var plan = GeoPropPlanner.Plan(env, new BuildingsData(), Settings(100), Mask(200), notes);
            Assert.That(plan.Count, Is.InRange(60, 100));
            Assert.That(plan.Count(p => p.position.z < 100), Is.GreaterThan(20), "Trees are not all from one edge of the map.");
            Assert.That(plan.Count(p => p.position.z >= 100), Is.GreaterThan(20));
            Assert.That(plan.Count(p => p.position.x < 100), Is.GreaterThan(20));
            Assert.That(plan.Count(p => p.position.x >= 100), Is.GreaterThan(20));
            Assert.IsTrue(notes.Any(n => n.Contains("thinned")), string.Join("\n", notes));
            Assert.That(plan.Select(p => p.id).Distinct().Count(), Is.EqualTo(plan.Count));
            // the same every time
            var again = GeoPropPlanner.Plan(env, new BuildingsData(), Settings(100), Mask(200), new List<string>());
            Assert.That(again.Select(p => p.id), Is.EqualTo(plan.Select(p => p.id)));
        }

        [Test] public void Canopy_KeepsTreesNearBuildingsFirstButNotOnlyThose()
        {
            var env = Env(400);
            var buildings = new BuildingsData();
            buildings.buildings.Add(new BuildingFeature { id = "house", x = 60, z = 60, width = 8, length = 8, footprint = new float[] { -4, -4, 4, -4, 4, 4, -4, 4 } });
            var plan = GeoPropPlanner.Plan(env, buildings, Settings(300), Mask(400), new List<string>());
            Assert.That(plan.Count, Is.InRange(200, 300));
            int near = plan.Count(p => Mathf.Abs(p.position.x - 60) < 130 && Mathf.Abs(p.position.z - 60) < 130);
            int far = plan.Count(p => Mathf.Abs(p.position.x - 330) < 130 && Mathf.Abs(p.position.z - 330) < 130);
            Assert.That(far, Is.GreaterThan(0), "The countryside is not left bare.");
            Assert.That(near, Is.GreaterThan(far * 3), "Trees near the building come first: " + near + " near, " + far + " far.");
        }

        [Test] public void Fences_DoNotUseUpTheTrees()
        {
            var env = Env(200);
            env.barriers.Add(new BarrierFeature { id = "long fence", type = "fence", points = new float[] { 5, 5, 5, 195, 195, 195, 195, 5, 5, 5, 100, 100 } });
            var plan = GeoPropPlanner.Plan(env, new BuildingsData(), Settings(200), Mask(200), new List<string>());
            Assert.That(plan.Count, Is.LessThanOrEqualTo(200));
            Assert.That(plan.Count(p => p.kind == "tree"), Is.GreaterThanOrEqualTo(90), "Trees keep most of the limit.");
            Assert.That(plan.Count(p => p.kind == "fence"), Is.GreaterThanOrEqualTo(40), "Fences get the rest.");
        }

        [Test] public void MergedCity_BakesPropsIntoChunksWithoutAnObjectPerProp()
        {
            var root = new GameObject("Merged props test").AddComponent<GeoWorldRoot>(); disposable.Add(root.gameObject);
            root.settings = new GenerationSettings
            {
                buildingMode = GeoBuildingMode.Merged, mergeProps = true, chunkSize = 100,
                trees = true, mappedProps = true, vegetation = false, maxProps = 1000
            };
            var env = Env(300);
            env.trees.Add(new TreeFeature { id = "tree/a", x = 10, z = 10, height = 7, crown = 4 });
            env.trees.Add(new TreeFeature { id = "tree/b", x = 40, z = 10, height = 9, crown = 5 });
            env.trees.Add(new TreeFeature { id = "tree/c", x = 150, z = 10, height = 7, crown = 4 });
            env.barriers.Add(new BarrierFeature { id = "fence", type = "fence", points = new float[] { 10, 60, 50, 60 } });
            env.props.Add(new PropFeature { id = "bench", type = "bench", x = 30, z = 30, rotation = 20, rotationSource = "tag" });
            var field = new HeightField(env.bounds, 33);
            var notes = new List<string>();
            GeoPropBuilder.Build(root, env, new BuildingsData(), field, notes, o => disposable.Add(o));

            Assert.That(root.GetComponentsInChildren<GeoPlacedProp>(true).Length, Is.EqualTo(0), "No object per prop.");
            var group = root.transform.Find("Props and vegetation/Baked props and vegetation");
            Assert.IsNotNull(group, string.Join("\n", notes));
            Assert.That(group.childCount, Is.EqualTo(1), "Chunks of 100 m: the fence and the bench share one square; the trees are instances.");
            Assert.That(root.GetComponentInChildren<GeoInstancedMeshes>().count, Is.EqualTo(3));
            Assert.That(root.treeCount, Is.EqualTo(3));
            Assert.That(root.propCount, Is.EqualTo(3 + 5 + 1), "three trees, five 8 m fence pieces, one bench");
            foreach (var filter in group.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = filter.sharedMesh;
                Assert.That(mesh.vertexCount, Is.GreaterThan(0));
                Assert.That(mesh.uv.All(u => u.x >= 0 && u.x <= 1 && u.y >= 0 && u.y <= 1), "Palette coordinates stay inside the texture.");
                Assert.That(mesh.normals.All(n => Mathf.Abs(n.magnitude - 1) < 0.01f));
                Assert.That(mesh.vertices.All(v => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z)));
                Assert.That(filter.GetComponent<MeshRenderer>().sharedMaterial, Is.Not.Null);
                Assert.IsFalse(filter.GetComponent<Collider>(), "Baked props have no colliders.");
            }
            var first = group.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).OrderBy(m => m.bounds.center.x).First();
            Assert.That(first.bounds.min.y, Is.GreaterThanOrEqualTo(-0.01f));
            Assert.That(notes.Any(n => n.Contains("baked into 1 chunks") && n.Contains("3 instanced trees")), string.Join("\n", notes));
        }

        [Test] public void MergedCity_DrawsItsTreesAsInstances()
        {
            var root = new GameObject("Terrain trees test").AddComponent<GeoWorldRoot>(); disposable.Add(root.gameObject);
            root.settings = new GenerationSettings
            {
                buildingMode = GeoBuildingMode.Merged, mergeProps = true, chunkSize = 100,
                trees = true, mappedProps = true, vegetation = false, maxProps = 1000
            };
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(300, 50, 300) }; disposable.Add(data);
            var terrainObject = Terrain.CreateTerrainGameObject(data); disposable.Add(terrainObject);
            root.terrain = terrainObject.GetComponent<Terrain>();
            var env = Env(300);
            env.trees.Add(new TreeFeature { id = "tree/a", x = 10, z = 10, height = 7, crown = 4 });
            env.trees.Add(new TreeFeature { id = "tree/b", x = 40, z = 10, height = 9, crown = 5 });
            env.trees.Add(new TreeFeature { id = "tree/c", x = 150, z = 10, height = 7, crown = 4 });
            env.props.Add(new PropFeature { id = "bench", type = "bench", x = 30, z = 30, rotation = 20, rotationSource = "tag" });
            var notes = new List<string>();
            GeoPropBuilder.Build(root, env, new BuildingsData(), new HeightField(env.bounds, 33), notes, o => disposable.Add(o));

            // the terrain keeps no trees of its own: they are instances, a matrix each
            Assert.That(data.treeInstanceCount, Is.EqualTo(0));
            var drawer = root.GetComponentInChildren<GeoInstancedMeshes>();
            Assert.That(drawer, Is.Not.Null, string.Join("\n", notes));
            Assert.That(drawer.count, Is.EqualTo(3));
            Assert.That(drawer.batches.Where(b => b.submesh == 0).Sum(b => b.matrices.Length), Is.EqualTo(3));
            Assert.That(drawer.batches.All(b => b.material && b.material.enableInstancing && b.mesh));
            var tall = drawer.batches.SelectMany(b => b.matrices).OrderByDescending(m => m.GetColumn(1).magnitude).First();
            Assert.That(tall.GetColumn(1).magnitude, Is.EqualTo(9 / 7f).Within(0.001f));
            Assert.That(tall.GetColumn(3).x, Is.EqualTo(40).Within(0.001f));
            foreach (var b in drawer.batches) foreach (var m in b.matrices) Assert.That(b.bounds.Contains(m.GetColumn(3)));
            Assert.That(root.treeCount, Is.EqualTo(3)); Assert.That(root.propCount, Is.EqualTo(4));
            // the bench is still baked; no tree is
            var group = root.transform.Find("Props and vegetation/Baked props and vegetation");
            Assert.That(group.childCount, Is.EqualTo(1));
            Assert.That(group.GetChild(0).name, Does.Contain("0 trees"));
            Assert.That(notes.Any(n => n.Contains("3 instanced trees")), string.Join("\n", notes));
            // trees an older build left in the terrain are cleared
            data.treePrototypes = new[] { new TreePrototype { prefab = GeoTerrainTrees.Prefabs()[0] } };
            data.SetTreeInstances(new[] { new TreeInstance { position = new Vector3(0.5f, 0, 0.5f), widthScale = 1, heightScale = 1, color = Color.white, lightmapColor = Color.white } }, false);
            Assert.That(data.treeInstanceCount, Is.EqualTo(1));
            GeoPropBuilder.Build(root, env, new BuildingsData(), new HeightField(env.bounds, 33), new List<string>(), o => disposable.Add(o));
            Assert.That(data.treeInstanceCount, Is.EqualTo(0));
        }

        [Test] public void CompactChunks_HalveTheVertexSizeAndKeepTheShape()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 30, 250), new Vector3(100, 30, 250) });
            var normals = new List<Vector3> { Vector3.up, new Vector3(0.6f, 0.8f, 0), new Vector3(0, 0.28f, 0.96f), Vector3.right };
            mesh.SetNormals(normals);
            mesh.SetTangents(new List<Vector4> { new Vector4(1, 0, 0, 1), new Vector4(0, 0, 1, -1), new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, -1) });
            var uvs = new List<Vector2> { new Vector2(0.001953125f, 0.00390625f), new Vector2(0.5f, 0.25f), new Vector2(1, 1), new Vector2(0.75f, 0.1f) };
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2; mesh.SetTriangles(new[] { 0, 2, 1 }, 0); mesh.SetTriangles(new[] { 1, 2, 3 }, 1);
            mesh.RecalculateBounds();
            var compact = GeoMeshCompression.Compact(mesh); disposable.Add(compact);
            Assert.That(compact.GetVertexBufferStride(0), Is.EqualTo(24));
            Assert.That(compact.subMeshCount, Is.EqualTo(2));
            Assert.That(compact.GetTriangles(1), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(compact.vertices[3], Is.EqualTo(new Vector3(100, 30, 250)));
            for (int i = 0; i < 4; i++)
            {
                Assert.That(Vector3.Angle(compact.normals[i], normals[i]), Is.LessThan(0.6f));
                Assert.That(Vector2.Distance(compact.uv[i], uvs[i]), Is.LessThan(0.0005f));
            }
            Assert.That(compact.tangents[1].w, Is.EqualTo(-1).Within(0.01f));
            Assert.That(compact.bounds.max.z, Is.EqualTo(250).Within(0.001f));
            // texture coordinates beyond 0..2 keep 32-bit precision
            var tiled = new Mesh(); tiled.SetVertices(new List<Vector3> { Vector3.zero, Vector3.right, Vector3.forward });
            tiled.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up });
            tiled.SetUVs(0, new List<Vector2> { Vector2.zero, new Vector2(37.123f, 0), Vector2.up }); tiled.SetTriangles(new[] { 0, 2, 1 }, 0);
            var wide = GeoMeshCompression.Compact(tiled); disposable.Add(wide);
            Assert.That(wide.uv[1].x, Is.EqualTo(37.123f).Within(0.0001f));
            Assert.That(wide.GetVertexBufferStride(0), Is.EqualTo(24));
            // a road's second texture coordinate (offset across the road, half width) is kept: 16-bit within 64 m
            var road = new Mesh(); road.SetVertices(new List<Vector3> { Vector3.zero, Vector3.right * 500, Vector3.forward * 4 });
            road.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up });
            road.SetUVs(0, new List<Vector2> { Vector2.zero, new Vector2(0, 125.5f), new Vector2(1, 0) });
            var across = new List<Vector2> { new Vector2(-3.5f, 3.5f), new Vector2(0, 3.5f), new Vector2(39.9f, 40) };
            road.SetUVs(1, across); road.SetTriangles(new[] { 0, 2, 1 }, 0);
            var compactRoad = GeoMeshCompression.Compact(road); disposable.Add(compactRoad);
            Assert.That(compactRoad.GetVertexBufferStride(0), Is.EqualTo(12 + 4 + 8 + 4));
            var kept = new List<Vector2>(); compactRoad.GetUVs(1, kept);
            for (int i = 0; i < 3; i++) Assert.That(Vector2.Distance(kept[i], across[i]), Is.LessThan(0.03f), "uv2 " + i);
            Assert.That(compactRoad.uv[1].y, Is.EqualTo(125.5f).Within(0.0001f));
            // beyond 64 it stays 32-bit
            var wideRoad = new Mesh(); wideRoad.SetVertices(new List<Vector3> { Vector3.zero, Vector3.right, Vector3.forward });
            wideRoad.SetUVs(1, new List<Vector2> { Vector2.zero, new Vector2(80.123f, 0), Vector2.up }); wideRoad.SetTriangles(new[] { 0, 2, 1 }, 0);
            var compactWide = GeoMeshCompression.Compact(wideRoad); disposable.Add(compactWide);
            var wideKept = new List<Vector2>(); compactWide.GetUVs(1, wideKept);
            Assert.That(wideKept[1].x, Is.EqualTo(80.123f).Within(0.0001f));
            Assert.That(compactWide.GetVertexBufferStride(0), Is.EqualTo(12 + 4 + 4 + 8));
        }

        [Test] public void EditableWorld_KeepsOneObjectPerProp()
        {
            var root = new GameObject("Editable props test").AddComponent<GeoWorldRoot>(); disposable.Add(root.gameObject);
            root.settings = new GenerationSettings { buildingMode = GeoBuildingMode.Editable, mergeProps = true, trees = true, mappedProps = true, vegetation = false };
            var env = Env(100);
            env.trees.Add(new TreeFeature { id = "tree/a", x = 10, z = 10 });
            env.props.Add(new PropFeature { id = "bench", type = "bench", x = 30, z = 30 });
            GeoPropBuilder.Build(root, env, new BuildingsData(), new HeightField(env.bounds, 33), new List<string>(), o => disposable.Add(o));
            Assert.That(root.GetComponentsInChildren<GeoPlacedProp>(true).Length, Is.EqualTo(2));
            Assert.IsNull(root.transform.Find("Props and vegetation/Baked props and vegetation"));
        }

        [Test] public void PhotoReferenceProps_AreNeverBaked()
        {
            var settings = new GenerationSettings();
            Assert.IsTrue(GeoPropBaker.Bakes(new PropPlacement { kind = "bench", source = "mapped" }, settings));
            Assert.IsTrue(GeoPropBaker.Bakes(new PropPlacement { kind = "tree", source = "generated" }, settings));
            Assert.IsFalse(GeoPropBaker.Bakes(new PropPlacement { kind = "bench", source = "manual_photo" }, settings));
        }
    }
}
