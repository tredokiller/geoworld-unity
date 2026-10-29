using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // The land cover of a tiled world: the exports' ground maps on the tiles' grid (GeoGroundMap, GeoLandCoverBaker) and what a
    // game asks of it at runtime (GeoLandCover), across the edges of the tiles.
    public class GeoLandCoverTests
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

        static GenerationSettings Settings() => new GenerationSettings
        {
            heightResolution = 129, textureResolution = 128, roadPaintResolution = 256, buildingMode = GeoBuildingMode.Editable, satelliteImagery = false,
            ruinModels = GeoRuinUse.Off, homeModels = GeoHomeUse.Off, groundTextures = false, maxProps = 2000, vegetation = false
        };

        // A ground map of 1 m cells over the bounds: fill, or what paint says at a cell's middle; ids rows from the south.
        static (MaterialMapInfo info, byte[] ids) Map(GeoBounds b, byte fill, Func<float, float, byte?> paint = null)
        {
            int width = Mathf.RoundToInt(b.Width), height = Mathf.RoundToInt(b.Depth);
            var info = new MaterialMapInfo { file = "materials.png", width = width, height = height, cellSize = 1, minX = b.minX, maxZ = b.maxZ, classes = new List<MaterialClass> {
                new MaterialClass { id = 2, name = "cropland" }, new MaterialClass { id = 3, name = "forest" }, new MaterialClass { id = 11, name = "asphalt" },
                new MaterialClass { id = 40, name = "lava" } } };
            var ids = new byte[width * height];
            for (int row = 0; row < height; row++)
                for (int column = 0; column < width; column++)
                    ids[row * width + column] = paint?.Invoke(b.minX + column + 0.5f, b.minZ + row + 0.5f) ?? fill;
            return (info, ids);
        }

        // A tile of 100 m with cells of 10 m.
        GeoLandCoverData Tile(int x, int z, Func<int, int, GeoLandClass> cell)
        {
            var data = ScriptableObject.CreateInstance<GeoLandCoverData>();
            owned.Add(data);
            var cells = new byte[100];
            for (int row = 0; row < 10; row++) for (int column = 0; column < 10; column++) cells[row * 10 + column] = (byte)cell(column, row);
            data.Set(new Vector2Int(x, z), new Rect(x * 100f, z * 100f, 100, 100), 10, cells, (string[])GeoLandCover.Names.Clone());
            return data;
        }

        [Test] public void Classes_AreTheIdsOfTheAppsGroundMap()
        {
            Assert.That(GeoLandCover.Names, Has.Length.EqualTo(GeoLandCover.ClassCount));
            Assert.That(Enum.GetValues(typeof(GeoLandClass)), Has.Length.EqualTo(GeoLandCover.ClassCount));
            for (int i = 0; i < GeoLandCover.ClassCount; i++)
            {
                Assert.That(GeoLandCover.ClassOf(GeoLandCover.Names[i]), Is.EqualTo((GeoLandClass)i), GeoLandCover.Names[i]);
                Assert.That(((GeoLandClass)i).ToString().ToLowerInvariant(), Is.EqualTo(GeoLandCover.Names[i].Replace("_", "")));
            }
            Assert.That(GeoLandCover.ClassOf("Dirt_Road"), Is.EqualTo(GeoLandClass.DirtRoad), "Names in any case.");
            Assert.That(GeoLandCover.ClassOf("lava"), Is.EqualTo(GeoLandClass.None));
            // the app's own table (Extractor~/extractor/materials.py), whose ids are written into materials.png
            string table = Path.GetFullPath(GeoPaths.Package + "/Extractor~/extractor/materials.py");
            if (!File.Exists(table)) Assert.Ignore("The package has no Building Extractor app here.");
            var ids = Regex.Matches(File.ReadAllText(table), @"^\s*\((\d+), ""(\w+)"",", RegexOptions.Multiline).Cast<Match>()
                .ToDictionary(m => m.Groups[2].Value, m => int.Parse(m.Groups[1].Value));
            Assert.That(ids.Count, Is.EqualTo(GeoLandCover.ClassCount));
            foreach (var pair in ids) Assert.That((int)GeoLandCover.ClassOf(pair.Key), Is.EqualTo(pair.Value), pair.Key);
        }

        // Two exports that overlap by 20 m: the first owns the overlap, as for the terrain. A cell takes the most common class of
        // its points, so a 3 m track leaves the forest's 10 m cell a forest and an 8 m road keeps its cell.
        [Test] public void GroundMap_TakesTheOwnersClass_AndTheMostCommonClassOfACell()
        {
            var westBounds = Bounds(0, 0, 120, 100);
            var eastBounds = Bounds(100, 0, 200, 100);
            var west = Map(westBounds, 3, (x, z) => x >= 52 && x < 55 || x >= 60 && x < 68 ? (byte)11 : (byte?)null);
            var east = Map(eastBounds, 2, (x, z) => z >= 90 ? (byte)40 : (byte?)null);
            var notes = new List<string>();
            var map = new GeoGroundMap(GeoTilePlan.Make(new[] { westBounds, eastBounds }, 100), new[] { west.info, east.info }, new[] { west.ids, east.ids }, notes);

            Assert.That(map.At(110, 50), Is.EqualTo(GeoLandClass.Forest), "The first export owns the overlap.");
            Assert.That(map.At(130, 50), Is.EqualTo(GeoLandClass.Cropland));
            Assert.That(map.At(53, 50), Is.EqualTo(GeoLandClass.Asphalt));
            Assert.That(map.At(-5, 50), Is.EqualTo(GeoLandClass.None), "Off every map.");
            Assert.That(map.At(150, 95), Is.EqualTo(GeoLandClass.None), "A class the runtime does not know is no class.");
            Assert.That(notes, Has.Some.Contains("lava"));

            var cells = map.Cells(new Rect(0, 0, 100, 100), 10);
            Assert.That((GeoLandClass)cells[5 * 10 + 5], Is.EqualTo(GeoLandClass.Forest), "A 3 m track is a quarter of its cell.");
            Assert.That((GeoLandClass)cells[5 * 10 + 6], Is.EqualTo(GeoLandClass.Asphalt), "An 8 m road is most of its cell.");
            cells = map.Cells(new Rect(100, 0, 100, 100), 10);
            Assert.That((GeoLandClass)cells[5 * 10 + 0], Is.EqualTo(GeoLandClass.Forest));
            Assert.That((GeoLandClass)cells[5 * 10 + 1], Is.EqualTo(GeoLandClass.Forest));
            Assert.That((GeoLandClass)cells[5 * 10 + 2], Is.EqualTo(GeoLandClass.Cropland));
            Assert.That((GeoLandClass)cells[9 * 10 + 5], Is.EqualTo(GeoLandClass.None), "No point of the cell has a class.");
        }

        [Test] public void Data_ReadsItsCellsByTheNamesOfItsClasses()
        {
            var data = ScriptableObject.CreateInstance<GeoLandCoverData>();
            owned.Add(data);
            data.Set(Vector2Int.zero, new Rect(0, 0, 20, 20), 2, new byte[] { 1, 2, 3, 0 }, new[] { "none", "orchard", "Forest", "lava" });
            Assert.That(data.At(0, 0), Is.EqualTo(GeoLandClass.Orchard));
            Assert.That(data.At(1, 0), Is.EqualTo(GeoLandClass.Forest), "Names in any case.");
            Assert.That(data.At(0, 1), Is.EqualTo(GeoLandClass.None), "A name the runtime does not know.");
            Assert.That(data.TrySample(5, 5, out var value), Is.True);
            Assert.That(value, Is.EqualTo(GeoLandClass.Orchard));
            Assert.That(data.TrySample(5, 15, out _), Is.False, "No class.");
            Assert.That(data.TrySample(25, 5, out _), Is.False, "Off the tile.");
        }

        // Far from any real world (tile 1000, 1000 of 100 m), so the scene's own worlds do not answer.
        [Test] public void Cover_AnswersAcrossTheEdgeOfTwoTiles()
        {
            var go = new GameObject("Land cover test");
            owned.Add(go);
            var cover = go.AddComponent<GeoLandCover>();
            cover.tileSize = 100; cover.cellSize = 10;
            cover.tiles = new List<GeoLandCoverData> { Tile(1000, 1000, (c, r) => GeoLandClass.Forest), Tile(1001, 1000, (c, r) => c < 5 ? GeoLandClass.Cropland : GeoLandClass.Grass) };
            const float x = 100100, z = 100050; // the edge of the two tiles

            Assert.That(GeoLandCover.TrySample(new Vector3(x - 0.1f, 0, z), out var value), Is.True);
            Assert.That(value, Is.EqualTo(GeoLandClass.Forest));
            Assert.That(GeoLandCover.TrySample(new Vector3(x + 0.1f, 0, z), out value), Is.True);
            Assert.That(value, Is.EqualTo(GeoLandClass.Cropland));
            Assert.That(cover.Sample(x, z, out value) && value == GeoLandClass.Cropland, "The edge belongs to the tile its index says.");
            Assert.That(GeoLandCover.TrySample(new Vector3(x + 150, 0, z), out _), Is.False, "No tile there.");

            var fractions = new float[GeoLandCover.ClassCount];
            Assert.That(GeoLandCover.TryGetFractions(new Vector3(x, 0, z), 30, fractions), Is.True);
            Assert.That(fractions[(int)GeoLandClass.Forest], Is.EqualTo(0.5f).Within(0.001f), "Half the circle is on the western tile.");
            Assert.That(fractions[(int)GeoLandClass.Cropland], Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(fractions.Sum(), Is.EqualTo(1).Within(0.0001f));
            Assert.That(GeoLandCover.Share(fractions, GeoLandClass.Forest, GeoLandClass.Cropland), Is.EqualTo(1).Within(0.0001f));
            Assert.That(GeoLandCover.TryGetFractions(new Vector3(x + 60, 0, z), 1, fractions), Is.True, "A radius under a cell: the cell at the point.");
            Assert.That(fractions[(int)GeoLandClass.Grass], Is.EqualTo(1));
            Assert.That(GeoLandCover.TryGetFractions(new Vector3(x + 160, 0, z), 30, fractions), Is.False);
            Assert.That(fractions.Sum(), Is.Zero);
        }

        // The riverside sample as 4 x 2 tiles of 200 m: generating bakes the land cover, which says what the sample's ground map says.
        [Test] public void TiledSample_HasTheLandCoverOfItsGroundMap()
        {
            string folder = "Assets/GeoWorld/Generated/LandCoverTest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            assets.Add(folder);
            var world = GeoTiles.Generate(Sample + "/environment.json", Sample + "/buildings.json", Settings(), 200, folder, separateScenes: false);
            owned.Add(world.gameObject);
            var cover = world.GetComponent<GeoLandCover>();
            Assert.That(cover, Is.Not.Null, world.report);
            Assert.That(cover.tiles.Count, Is.EqualTo(8));
            Assert.That(cover.cellSize, Is.EqualTo(10));
            Assert.That(cover.tiles.All(t => t.resolution == 20 && AssetDatabase.GetAssetPath(t).StartsWith(folder + "/Land cover/")));
            Assert.That(world.report, Does.Contain("Land cover: 8 tiles"));

            // the sample's areas (GeoSample): woods, a field, built-up ground, rough ground, and grass everywhere else
            Assert.That(cover.Sample(-150, 150, out var value) && value == GeoLandClass.Forest, value.ToString());
            Assert.That(cover.Sample(170, 100, out value) && value == GeoLandClass.Cropland, value.ToString());
            Assert.That(cover.Sample(170, -100, out value) && value == GeoLandClass.Urban, value.ToString());
            Assert.That(cover.Sample(170, -168, out value) && value == GeoLandClass.Scrub, value.ToString());
            Assert.That(cover.Sample(-200, 0, out value) && value == GeoLandClass.Grass, value.ToString());
            // the field lies on the tiles on both sides of x = 200
            var fractions = new float[GeoLandCover.ClassCount];
            Assert.That(cover.Fractions(200, 100, 25, fractions), Is.True);
            Assert.That(fractions[(int)GeoLandClass.Cropland], Is.EqualTo(1).Within(0.001f));

            Assert.That(GeoTiles.BakeLandCover(world), Does.Contain("No tile changed"), "Baking again writes nothing.");

            // a bake saves its own assets and no other: after Play mode, assets changed in memory must stay as they are on disk
            var other = ScriptableObject.CreateInstance<GeoLandCoverData>();
            AssetDatabase.CreateAsset(other, folder + "/Other.asset");
            other.resolution = 7;
            EditorUtility.SetDirty(other);
            var tile = cover.tiles[0];
            tile.cells[0] = (byte)GeoLandClass.Water;
            EditorUtility.SetDirty(tile);
            Assert.That(GeoTiles.BakeLandCover(world), Does.Contain("1 tile written"));
            Assert.That(EditorUtility.IsDirty(other), Is.True, "Another changed asset is left unsaved.");
            Assert.That(EditorUtility.IsDirty(tile), Is.False, "The tile's land cover is saved.");
            Assert.That(tile.cells[0], Is.Not.EqualTo((byte)GeoLandClass.Water));
        }
    }
}
