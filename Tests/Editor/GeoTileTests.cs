using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // A large world in tiles: meshes and paths cut at a tile's edge, the shared height source, and the riverside sample
    // built as 3 x 2 tiles of 200 m, which must meet at their edges and hold every building exactly once.
    public class GeoTileTests
    {
        const string Sample = "Assets/GeoWorld/Samples/RiversideVillage";
        readonly List<Object> owned = new List<Object>();
        readonly List<string> assets = new List<string>();

        [TearDown] public void Cleanup()
        {
            foreach (var o in owned) if (o) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var path in assets) AssetDatabase.DeleteAsset(path);
            assets.Clear();
        }

        static GeoBounds Bounds(float minX, float minZ, float maxX, float maxZ) => new GeoBounds { minX = minX, minZ = minZ, maxX = maxX, maxZ = maxZ };

        [Test] public void Clip_KeepsTheTrianglesWhoseCentreIsOnTheTile()
        {
            // two quads side by side, x 0..10 and 10..20, in two sub-meshes
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 0, 10), new Vector3(0, 0, 10),
                new Vector3(10, 1, 0), new Vector3(20, 1, 0), new Vector3(20, 1, 10), new Vector3(10, 1, 10) });
            mesh.SetUVs(0, Enumerable.Range(0, 8).Select(i => new Vector2(i, 0)).ToList());
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0); mesh.SetTriangles(new[] { 4, 6, 5, 4, 7, 6 }, 1);
            var clipped = GeoTiles.Clip(mesh, Bounds(0, 0, 10, 10)); owned.Add(clipped);
            Assert.That(clipped.subMeshCount, Is.EqualTo(2));
            Assert.That(clipped.GetTriangles(0).Length, Is.EqualTo(6));
            Assert.That(clipped.GetTriangles(1).Length, Is.EqualTo(0));
            Assert.That(clipped.vertexCount, Is.EqualTo(4));
            Assert.That(clipped.uv.Select(u => (int)u.x), Is.EquivalentTo(new[] { 0, 1, 2, 3 }));
            // a mesh already on the tile comes back as it is
            var inside = new Mesh(); inside.SetVertices(new List<Vector3> { new Vector3(1, 0, 1), new Vector3(2, 0, 1), new Vector3(1, 0, 2) });
            inside.SetTriangles(new[] { 0, 2, 1 }, 0); owned.Add(inside);
            Assert.That(GeoTiles.Clip(inside, Bounds(0, 0, 10, 10)), Is.SameAs(inside));
        }

        [Test] public void ClipPath_CutsOnTheSamplesAndKeepsTheControlsBetween()
        {
            var controls = new List<Vector3> { new Vector3(-50, 0, 0), new Vector3(50, 0, 0), new Vector3(50, 0, 100), new Vector3(150, 0, 100) };
            var samples = GeoData.Resample(controls, 2);
            for (int i = 0; i < samples.Count; i++) samples[i] += Vector3.up * i * 0.01f; // heights to check they are kept
            var path = new PathBuild { feature = new LineFeature { id = "r", width = 6 }, controls = controls, samples = samples };
            var pieces = GeoTiles.ClipPath(path, Bounds(0, -10, 100, 110), 2);
            Assert.That(pieces.Count, Is.EqualTo(1));
            var piece = pieces[0];
            Assert.That(piece.samples.First().x, Is.LessThanOrEqualTo(0)); Assert.That(piece.samples.Last().x, Is.GreaterThanOrEqualTo(100));
            int start = samples.IndexOf(piece.samples[0]);
            Assert.That(start, Is.GreaterThan(0));
            for (int i = 0; i < piece.samples.Count; i++) Assert.That(piece.samples[i], Is.EqualTo(samples[start + i]));
            // the two corners inside are kept as control points
            Assert.That(piece.controls.Count, Is.EqualTo(4));
            Assert.That(piece.controls[1], Is.EqualTo(controls[1])); Assert.That(piece.controls[2], Is.EqualTo(controls[2]));
            // out of the box and back in: two pieces
            Assert.That(GeoTiles.ClipPath(path, Bounds(0, -10, 100, 40), 2).Count, Is.EqualTo(1));
            Assert.That(GeoTiles.ClipPath(path, Bounds(-100, -10, 200, 20), 2).Count, Is.EqualTo(1));
            Assert.That(GeoTiles.ClipPath(path, Bounds(-100, 80, 200, 120), 2).Count, Is.EqualTo(1));
            var wiggle = new PathBuild { feature = path.feature, controls = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(0, 0, 100), new Vector3(10, 0, 100), new Vector3(10, 0, 0) } };
            wiggle.samples = GeoData.Resample(wiggle.controls, 2);
            Assert.That(GeoTiles.ClipPath(wiggle, Bounds(-5, -5, 15, 20), 2).Count, Is.EqualTo(2));
        }

        [Test] public void HeightSource_IsWhatAHeightFieldSamples()
        {
            var env = GeoData.ReadEnvironment(Sample + "/environment.json");
            var field = HeightField.Load(env, Sample + "/environment.json", 65, new List<string>());
            var source = GeoHeightSource.Load(env, Sample + "/environment.json");
            for (int z = 0; z < 65; z += 8) for (int x = 0; x < 65; x += 8)
                Assert.That(source.Sample(field.X(x), field.Z(z)), Is.EqualTo(field.heights[z, x]));
            Assert.That(source.min, Is.LessThan(source.max));
        }

        static GenerationSettings Settings() => new GenerationSettings
        {
            heightResolution = 129, textureResolution = 128, roadPaintResolution = 256, buildingMode = GeoBuildingMode.Editable, satelliteImagery = false,
            ruinModels = GeoRuinUse.Off, homeModels = GeoHomeUse.Off, groundTextures = false, maxProps = 2000, vegetation = false
        };

        [Test] public void TiledSample_TilesMeetAndHoldEveryBuildingOnce()
        {
            string folder = "Assets/GeoWorld/Generated/TileTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            assets.Add(folder);
            var single = GeoGenerator.Generate(Sample + "/environment.json", Sample + "/buildings.json", Settings(), null, false);
            owned.Add(single.gameObject); assets.Add(single.assetPath);
            var world = GeoTiles.Generate(Sample + "/environment.json", Sample + "/buildings.json", Settings(), 200, folder, separateScenes: false);
            owned.Add(world.gameObject);
            var tiles = world.GetComponentsInChildren<GeoWorldRoot>();
            Assert.That(tiles.Length, Is.EqualTo(6)); // 480 x 360 m in 200 m tiles
            Assert.That(world.columns, Is.EqualTo(3)); Assert.That(world.rows, Is.EqualTo(2));

            // every building once, the same ones as a single world
            var ids = tiles.SelectMany(t => t.GetComponentsInChildren<GeoBuilding>()).Select(b => b.sourceId).ToList();
            Assert.That(ids.Count, Is.EqualTo(single.buildingCount));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
            Assert.That(tiles.Sum(t => t.buildingCount), Is.EqualTo(single.buildingCount));

            // neighbours have the same ground along their common edge
            var byIndex = tiles.ToDictionary(t => t.tileIndex);
            int edges = 0;
            foreach (var tile in tiles)
            {
                Assert.That(tile.terrain.groupingID, Is.EqualTo(GeoTiles.TerrainGroup));
                foreach (var step in new[] { Vector2Int.right, Vector2Int.up })
                {
                    if (!byIndex.TryGetValue(tile.tileIndex + step, out var next)) continue;
                    for (int i = 1; i < 10; i++)
                    {
                        float along = i / 10f;
                        Vector3 a = step.x > 0 ? new Vector3(tile.tileArea.xMax - 0.005f, 0, tile.tileArea.yMin + along * 200) : new Vector3(tile.tileArea.xMin + along * 200, 0, tile.tileArea.yMax - 0.005f);
                        Vector3 b = a + new Vector3(step.x, 0, step.y) * 0.01f;
                        float ha = tile.terrain.SampleHeight(a) + tile.terrain.transform.position.y, hb = next.terrain.SampleHeight(b) + next.terrain.transform.position.y;
                        Assert.That(ha, Is.EqualTo(hb).Within(0.03f), $"edge between {tile.tileIndex} and {next.tileIndex} at {along}");
                    }
                    edges++;
                }
            }
            Assert.That(edges, Is.EqualTo(7));

            // roads stay on their tile (a terrain triangle may stick out past the edge), and the tiles hold the roads
            foreach (var tile in tiles)
                foreach (var filter in tile.transform.Find("Roads - select a road to edit").GetComponentsInChildren<MeshFilter>())
                {
                    var box = filter.sharedMesh.bounds;
                    Assert.That(box.min.x, Is.GreaterThan(tile.tileArea.xMin - 4)); Assert.That(box.max.x, Is.LessThan(tile.tileArea.xMax + 4));
                    Assert.That(box.min.z, Is.GreaterThan(tile.tileArea.yMin - 4)); Assert.That(box.max.z, Is.LessThan(tile.tileArea.yMax + 4));
                }
            Assert.That(tiles.Sum(t => t.roadCount), Is.GreaterThanOrEqualTo(single.roadCount));

            // nothing changed: nothing is rebuilt
            var again = GeoTiles.Generate(Sample + "/environment.json", Sample + "/buildings.json", Settings(), 200, folder, separateScenes: false);
            Assert.That(again, Is.SameAs(world));
            Assert.That(again.report, Does.Contain("0 tiles built, 6 unchanged"));
            // a changed setting rebuilds them all
            var changed = Settings(); changed.roadShoulder = 1;
            GeoTiles.Generate(Sample + "/environment.json", Sample + "/buildings.json", changed, 200, folder, separateScenes: false);
            Assert.That(world.report, Does.Contain("6 tiles built"));
            Assert.That(world.GetComponentsInChildren<GeoWorldRoot>().Length, Is.EqualTo(6));
        }

        // The crossings are one object per chunk and material (a city tile had an object for each of 1 700 pieces).
        [Test] public void Crossings_AreOneMeshPerChunkAndMaterial()
        {
            var settings = Settings(); settings.chunkSize = 200;
            var world = GeoGenerator.Generate(Sample + "/environment.json", Sample + "/buildings.json", settings, null, false);
            owned.Add(world.gameObject); assets.Add(world.assetPath);
            Assert.That(world.junctionCount, Is.GreaterThan(0));
            var roads = world.transform.Find("Roads - select a road to edit");
            Assert.That(roads.Cast<Transform>().Where(t => t.name.StartsWith("Junction ")), Is.Empty);
            var crossings = roads.Cast<Transform>().Where(t => t.name.StartsWith("Junctions ")).ToList();
            Assert.That(crossings, Is.Not.Empty);
            var keys = new HashSet<string>();
            foreach (var crossing in crossings)
            {
                var mesh = crossing.GetComponent<MeshFilter>().sharedMesh; var material = crossing.GetComponent<MeshRenderer>().sharedMaterial;
                Assert.That(EditorUtility.IsPersistent(mesh)); Assert.That(mesh.subMeshCount, Is.EqualTo(1)); Assert.That(mesh.vertexCount, Is.GreaterThan(0));
                Assert.That(crossing.GetComponent<Collider>(), Is.Null);
                // named after its chunk, which holds the middle of every piece in it
                var cell = crossing.name.Substring("Junctions ".Length).Split(' ')[0].Split('_').Select(int.Parse).ToArray();
                Assert.That(mesh.bounds.min.x, Is.GreaterThan(cell[0] * 200 - 60)); Assert.That(mesh.bounds.max.x, Is.LessThan(cell[0] * 200 + 260));
                Assert.That(mesh.bounds.min.z, Is.GreaterThan(cell[1] * 200 - 60)); Assert.That(mesh.bounds.max.z, Is.LessThan(cell[1] * 200 + 260));
                Assert.That(keys.Add(cell[0] + "_" + cell[1] + material.name + mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1)), "one object per chunk and material");
            }
        }
    }
}
