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
    // built as 4 x 2 tiles of 200 m, which must meet at their edges and hold every building exactly once; and the grid
    // and the ownership of tiles where several exports of one area meet.
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
            Assert.That(tiles.Length, Is.EqualTo(8)); // 480 x 360 m on the grid of 200 m tiles every export shares: x -400..400, z -200..200
            Assert.That(world.columns, Is.EqualTo(4)); Assert.That(world.rows, Is.EqualTo(2));
            Assert.That(world.origin, Is.EqualTo(Vector2.zero)); Assert.That(world.firstIndex, Is.EqualTo(new Vector2Int(-2, -1)));

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
            Assert.That(edges, Is.EqualTo(10));

            // roads stay on their tile (a terrain triangle may stick out past the edge), and the tiles hold the roads
            foreach (var tile in tiles)
                foreach (var filter in tile.transform.Find("Roads - select a road to edit").GetComponentsInChildren<MeshFilter>())
                {
                    var box = filter.sharedMesh.bounds;
                    Assert.That(box.min.x, Is.GreaterThan(tile.tileArea.xMin - 4)); Assert.That(box.max.x, Is.LessThan(tile.tileArea.xMax + 4));
                    Assert.That(box.min.z, Is.GreaterThan(tile.tileArea.yMin - 4)); Assert.That(box.max.z, Is.LessThan(tile.tileArea.yMax + 4));
                }
            Assert.That(tiles.Sum(t => t.roadCount), Is.GreaterThanOrEqualTo(single.roadCount));
            // a tile draws its ground roads a few meshes per chunk: objects of their own only for bridges and raised roads
            foreach (var tile in tiles)
                Assert.That(tile.transform.Find("Roads - select a road to edit").GetComponentsInChildren<GeoPath>().All(p => p.bridge || p.layer != 0));
            Assert.That(tiles.SelectMany(t => t.transform.Find("Roads - select a road to edit").Cast<Transform>()).Count(t => t.name.StartsWith("Roads ")), Is.GreaterThan(0));
            Assert.That(single.GetComponentsInChildren<GeoPath>().Count(p => !p.river && !p.bridge && p.layer == 0), Is.GreaterThan(0), "one world keeps its roads editable");

            // nothing changed: nothing is rebuilt
            var again = GeoTiles.Generate(Sample + "/environment.json", Sample + "/buildings.json", Settings(), 200, folder, separateScenes: false);
            Assert.That(again, Is.SameAs(world));
            Assert.That(again.report, Does.Contain("0 tiles built, 8 unchanged"));
            // a changed setting rebuilds them all
            var changed = Settings(); changed.roadShoulder = 1;
            GeoTiles.Generate(Sample + "/environment.json", Sample + "/buildings.json", changed, 200, folder, separateScenes: false);
            Assert.That(world.report, Does.Contain("8 tiles built"));
            Assert.That(world.GetComponentsInChildren<GeoWorldRoot>().Length, Is.EqualTo(8));
        }

        // ---- exports of one area on one grid ----------------------------------------------------------------------------

        static double Area(GeoBounds b) => b == null ? 0 : (double)b.Width * b.Depth;

        // The six parts of a city (3 x 2), each overlapping its neighbours by a few metres, as the app exports them.
        static GeoBounds[] SixParts() => new[]
        {
            Bounds(-8000, -4000, -2683, 44), Bounds(-2700, -4000, 2617, 44), Bounds(2600, -4000, 8000, 44),
            Bounds(-8000, 0, -2683, 4000), Bounds(-2700, 0, 2617, 4000), Bounds(2600, 0, 8000, 4000)
        };

        [Test] public void Plan_SixOverlappingParts_AreOneGridAndNothingIsBuiltTwice()
        {
            var parts = SixParts();
            var plan = GeoTilePlan.Make(parts, 2000);
            // the grid is the coordinates' own: tile (i, j) is [i * 2000, (i + 1) * 2000) whatever the exports' bounds are
            Assert.That(plan.first, Is.EqualTo(new Vector2Int(-4, -2)));
            Assert.That(plan.columns, Is.EqualTo(8)); Assert.That(plan.rows, Is.EqualTo(4));
            Assert.That(plan.tiles.Count, Is.EqualTo(32));
            Assert.That(GeoTilePlan.Tile(new Vector2Int(-1, 0), 2000).minX, Is.EqualTo(-2000));
            // the exports' regions do not overlap: every point of the ground has one owner
            for (int i = 0; i < parts.Length; i++)
                for (int j = i + 1; j < parts.Length; j++)
                    Assert.That(Area(GeoTilePlan.Intersection(plan.region[i], plan.region[j])), Is.EqualTo(0).Within(0.01), $"regions {i} and {j}");
            // and nearly all the ground has an owner (only a corner of an overlap, 17 x 44 m, may fall to a neighbour)
            double regions = plan.region.Sum(Area), union = 16000.0 * 8000;
            Assert.That(regions, Is.LessThanOrEqualTo(union + 1)); Assert.That(regions, Is.GreaterThan(union - 4 * 17 * 44 - 1));
            // tiles that no export reaches are not built; the others are built from the exports that own their ground
            Assert.That(plan.tiles.Values.All(c => c.Count >= 1 && c.Count <= 4));
            Assert.That(plan.tiles.Values.Count(c => c.Count > 1), Is.GreaterThan(0), "tiles where exports meet");
            // a tile in the middle of one export is that export's alone
            Assert.That(plan.tiles[new Vector2Int(-3, -1)], Is.EqualTo(new List<int> { 0 }));
            // the seam (x = -2683) is in tile -2: both exports build it, the one with most of it first
            Assert.That(plan.tiles[new Vector2Int(-2, -1)].First(), Is.EqualTo(0));
            Assert.That(plan.tiles[new Vector2Int(-2, -1)], Is.EquivalentTo(new[] { 0, 1 }));
        }

        [Test] public void Plan_CutsAnOverlapOnce_AndEveryPointHasOneOwner()
        {
            var plan = GeoTilePlan.Make(new[] { Bounds(0, 0, 1017, 1000), Bounds(1000, 44, 2000, 1044) }, 500);
            // the later export gives up the strip the earlier one has
            Assert.That(plan.region[0].maxX, Is.EqualTo(1017)); Assert.That(plan.region[1].minX, Is.EqualTo(1017));
            Assert.That(plan.OwnerOf(1010, 100), Is.EqualTo(0)); Assert.That(plan.OwnerOf(1030, 100), Is.EqualTo(1));
            // above the earlier export, beside its strip, only the later one has ground: it keeps it
            Assert.That(plan.OwnerOf(1005, 1020), Is.EqualTo(1));
            Assert.That(plan.OwnerOf(5000, 5000), Is.EqualTo(-1));
            Assert.That(plan.tiles[new Vector2Int(2, 0)].First(), Is.EqualTo(1));
            Assert.That(plan.tiles[new Vector2Int(2, 0)], Is.EquivalentTo(new[] { 0, 1 }));
            // an export wholly inside an earlier one builds nothing
            var inside = GeoTilePlan.Make(new[] { Bounds(0, 0, 1000, 1000), Bounds(100, 100, 400, 400) }, 500);
            Assert.That(inside.region[1], Is.Null);
            Assert.That(inside.tiles.Values.All(c => c.Count == 1 && c[0] == 0));
        }

        [Test] public void Plan_AnExportThatEndsNearTheGrid_HasNoSliverOfATile()
        {
            Assert.That(GeoTilePlan.Make(new[] { Bounds(0, 0, 1000, 1000) }, 500).tiles.Count, Is.EqualTo(4));
            Assert.That(GeoTilePlan.Make(new[] { Bounds(0.2f, -0.2f, 1000.3f, 1000) }, 500).tiles.Count, Is.EqualTo(4));
            // but not a real strip of one
            Assert.That(GeoTilePlan.Make(new[] { Bounds(0, 0, 1040, 1000) }, 500).tiles.Count, Is.EqualTo(6));
            // a single export is cut on the shared grid, not on its own corner
            var one = GeoTilePlan.Make(new[] { Bounds(-240, -180, 240, 180) }, 200);
            Assert.That(one.first, Is.EqualTo(new Vector2Int(-2, -1))); Assert.That(one.columns, Is.EqualTo(4)); Assert.That(one.rows, Is.EqualTo(2));
        }

        // Two exports of the riverside sample that overlap by 20 m: one world, 4 x 2 tiles, every building once, the ground meeting.
        [Test] public void TwoExports_MakeOneWorldOnOneGrid()
        {
            string root = "Assets/GeoWorld/Generated/PartsTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            assets.Add(root);
            var west = Part(root + "/west", -240, 40); var east = Part(root + "/east", 20, 240);
            var single = GeoGenerator.Generate(Sample + "/environment.json", Sample + "/buildings.json", Settings(), null, false);
            owned.Add(single.gameObject); assets.Add(single.assetPath);
            var parts = new List<GeoTiledWorld.Part> { west, east };
            var world = GeoTiles.Generate(parts, Settings(), 200, root + "/world", separateScenes: false);
            owned.Add(world.gameObject);
            Assert.That(world.parts.Count, Is.EqualTo(2));
            Assert.That(world.firstIndex, Is.EqualTo(new Vector2Int(-2, -1)));
            Assert.That(world.columns, Is.EqualTo(4)); Assert.That(world.rows, Is.EqualTo(2));
            var tiles = world.GetComponentsInChildren<GeoWorldRoot>();
            Assert.That(tiles.Length, Is.EqualTo(8)); // once each, not once for every export
            Assert.That(tiles.Select(t => t.tileIndex).Distinct().Count(), Is.EqualTo(8));
            // the tiles on the seam (x 0..200) are built from both exports, the others from one
            Assert.That(tiles.Where(t => t.tileParts == 2).Select(t => t.tileIndex.x).Distinct(), Is.EquivalentTo(new[] { 0 }));
            Assert.That(tiles.Count(t => t.tileParts == 2), Is.EqualTo(2));
            Assert.That(tiles.Count(t => t.tileParts == 1), Is.EqualTo(6));
            Assert.That(world.tiles.Where(t => t.index.x == 0).All(t => t.source.Contains(" + ")));

            // every building once, the same ones as a single world: the export that owns the ground at a building gives it
            var ids = tiles.SelectMany(t => t.GetComponentsInChildren<GeoBuilding>()).Select(b => b.sourceId).ToList();
            Assert.That(ids.Count, Is.EqualTo(single.buildingCount));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
            Assert.That(tiles.Sum(t => t.buildingCount), Is.EqualTo(single.buildingCount));
            Assert.That(tiles.Sum(t => t.roadCount), Is.GreaterThanOrEqualTo(single.roadCount));

            // neighbours have the same ground along their common edge, across the seam too
            var byIndex = tiles.ToDictionary(t => t.tileIndex);
            int edges = 0;
            foreach (var tile in tiles)
                foreach (var step in new[] { Vector2Int.right, Vector2Int.up })
                {
                    if (!byIndex.TryGetValue(tile.tileIndex + step, out var next)) continue;
                    for (int i = 1; i < 10; i++)
                    {
                        float along = i / 10f;
                        Vector3 a = step.x > 0 ? new Vector3(tile.tileArea.xMax - 0.005f, 0, tile.tileArea.yMin + along * 200) : new Vector3(tile.tileArea.xMin + along * 200, 0, tile.tileArea.yMax - 0.005f);
                        Vector3 b = a + new Vector3(step.x, 0, step.y) * 0.01f;
                        float ha = tile.terrain.SampleHeight(a) + tile.terrain.transform.position.y, hb = next.terrain.SampleHeight(b) + next.terrain.transform.position.y;
                        Assert.That(ha, Is.EqualTo(hb).Within(0.05f), $"edge between {tile.tileIndex} and {next.tileIndex} at {along}");
                    }
                    edges++;
                }
            Assert.That(edges, Is.EqualTo(10));

            // nothing changed: nothing is rebuilt; a changed export rebuilds the tiles it builds
            var again = GeoTiles.Generate(parts, Settings(), 200, root + "/world", separateScenes: false);
            Assert.That(again, Is.SameAs(world));
            Assert.That(again.report, Does.Contain("0 tiles built, 8 unchanged"));
            // exports in the other order: the later one now gives way, so the seam tiles are different
            GeoTiles.Generate(new List<GeoTiledWorld.Part> { east, west }, Settings(), 200, root + "/world", separateScenes: false);
            Assert.That(world.GetComponentsInChildren<GeoWorldRoot>().Length, Is.EqualTo(8));
            var ids2 = world.GetComponentsInChildren<GeoWorldRoot>().SelectMany(t => t.GetComponentsInChildren<GeoBuilding>()).Select(b => b.sourceId).ToList();
            Assert.That(ids2.Count, Is.EqualTo(single.buildingCount)); Assert.That(ids2.Distinct().Count(), Is.EqualTo(ids2.Count));
            // a tile of two exports cannot be rebuilt from one of them
            var seam = world.GetComponentsInChildren<GeoWorldRoot>().First(t => t.tileParts == 2);
            Assert.Throws<InvalidOperationException>(() => GeoTiles.Regenerate(seam, Settings()));
        }

        [Test] public void PartsIn_ListsTheExportsOfAFolderInNaturalOrder()
        {
            string root = "Assets/GeoWorld/Generated/PartsTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            assets.Add(root);
            foreach (var name in new[] { "part_10", "part_2", "part_1", "notes" })
            {
                System.IO.Directory.CreateDirectory(root + "/" + name);
                if (name != "notes") System.IO.File.WriteAllText(root + "/" + name + "/environment.json", "{}");
            }
            System.IO.File.WriteAllText(root + "/part_2/buildings.json", "{}");
            var parts = GeoTiles.PartsIn(root);
            Assert.That(parts.Select(p => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(p.environmentPath))), Is.EqualTo(new[] { "part_1", "part_2", "part_10" }));
            Assert.That(parts[1].buildingsPath, Does.EndWith("buildings.json")); Assert.That(parts[0].buildingsPath, Is.Empty);
            // one export: the folder itself
            Assert.That(GeoTiles.PartsIn(root + "/part_1").Count, Is.EqualTo(1));
            Assert.That(GeoTiles.PartsIn(root + "/nowhere"), Is.Empty);
            AssetDatabase.Refresh();
        }

        [Test] public void Exports_WithDifferentOrigins_AreRefused()
        {
            string root = "Assets/GeoWorld/Generated/PartsTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            assets.Add(root);
            var west = Part(root + "/west", -240, 40); var east = Part(root + "/east", 20, 240, 0.01);
            var ex = Assert.Throws<System.IO.InvalidDataException>(() =>
                GeoTiles.Generate(new List<GeoTiledWorld.Part> { west, east }, Settings(), 200, root + "/world", separateScenes: false));
            Assert.That(ex.Message, Does.Contain("different origins"));
        }

        // A copy of the riverside sample whose environment.json covers x from minX to maxX only (the heightmap and the
        // buildings are the whole sample's: an export holds more than its bounds, as exports of neighbouring parts do).
        GeoTiledWorld.Part Part(string folder, float minX, float maxX, double moveOrigin = 0)
        {
            System.IO.Directory.CreateDirectory(folder);
            foreach (var file in new[] { "heightmap.r16", "materials.png", "buildings.json" })
                System.IO.File.Copy(Sample + "/" + file, folder + "/" + file, true);
            var env = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(Sample + "/environment.json"));
            env["bounds"]["minX"] = minX; env["bounds"]["maxX"] = maxX;
            if (moveOrigin != 0)
            {
                env["origin"]["lat"] = (double)env["origin"]["lat"] + moveOrigin;
                var buildings = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(folder + "/buildings.json"));
                buildings["origin"]["lat"] = (double)buildings["origin"]["lat"] + moveOrigin;
                System.IO.File.WriteAllText(folder + "/buildings.json", buildings.ToString());
            }
            System.IO.File.WriteAllText(folder + "/environment.json", env.ToString());
            AssetDatabase.Refresh();
            return new GeoTiledWorld.Part { environmentPath = folder + "/environment.json", buildingsPath = folder + "/buildings.json" };
        }

        // The terrain casts shadows only when asked (in a city they cost 3-6 ms a frame and hardly show).
        [Test] public void Terrain_CastsShadowsOnlyWhenAsked()
        {
            var off = GeoGenerator.Generate(Sample + "/environment.json", Sample + "/buildings.json", Settings(), null, false);
            owned.Add(off.gameObject); assets.Add(off.assetPath);
            Assert.That(off.terrain.shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.Off));
            var settings = Settings(); settings.terrainShadows = true;
            var on = GeoGenerator.Generate(Sample + "/environment.json", Sample + "/buildings.json", settings, null, false);
            owned.Add(on.gameObject); assets.Add(on.assetPath);
            Assert.That(on.terrain.shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.TwoSided));
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
