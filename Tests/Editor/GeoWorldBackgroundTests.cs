using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // The background around world packages (GeoWorldBackground): its rings of map tiles, the cache every package shares (a zone next
    // to another downloads only the tiles the first did not), one background for several packages with no tile twice, the ground's
    // elevation and the Earth's curvature, and the cut where a package's tile is loaded. Map tiles come from a stand-in server.
    public class GeoWorldBackgroundTests
    {
        const string Sample = "Assets/GeoWorld/Samples/RiversideVillage";
        const string Imagery = "test://imagery/{z}/{x}/{y}", Elevation = "test://elevation/{z}/{x}/{y}";
        const float Ground = 150; // the stand-in elevation everywhere (metres above sea level)

        string root, oldRoot;
        Func<string, CancellationToken, Task<byte[]>> oldFetch;
        readonly List<string> asked = new List<string>();
        readonly List<GameObject> owned = new List<GameObject>();
        readonly List<GeoWorldTileHandle> handles = new List<GeoWorldTileHandle>();
        byte[] image, height;

        [SetUp] public void Setup()
        {
            oldRoot = GeoTileCache.Root; oldFetch = GeoTileCache.Fetch;
            root = Path.Combine(Path.GetTempPath(), "geoworld-background-tests", Guid.NewGuid().ToString("N").Substring(0, 8));
            GeoTileCache.Root = Path.Combine(root, "cache");
            image = Png(new Color32(90, 120, 70, 255));
            float v = Ground + 32768;
            height = Png(new Color32((byte)(v / 256), (byte)(v % 256), (byte)((v - Mathf.Floor(v)) * 256), 255));
            asked.Clear();
            GeoTileCache.Fetch = (url, cancellation) =>
            {
                lock (asked) asked.Add(url);
                return Task.FromResult(url.StartsWith("test://elevation") ? height : image);
            };
        }

        [TearDown] public void Cleanup()
        {
            foreach (var h in handles) h.Unload();
            handles.Clear();
            foreach (var go in owned) if (go) Object.DestroyImmediate(go);
            owned.Clear();
            GeoTileCache.Root = oldRoot; GeoTileCache.Fetch = oldFetch;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        static byte[] Png(Color32 colour)
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
            texture.SetPixels32(Enumerable.Repeat(colour, 64).ToArray());
            texture.Apply();
            var bytes = texture.EncodeToPNG();
            Object.DestroyImmediate(texture);
            return bytes;
        }

        static GeoBackgroundSettings Settings() => new GeoBackgroundSettings
        {
            imagery = Imagery, elevation = Elevation, maxCells = 400,
            levels = new[] { new GeoBackgroundSettings.Level { zoom = 15, reach = 600 }, new GeoBackgroundSettings.Level { zoom = 13, reach = 3000 } }
        };

        [Test] public void Plan_RingsOfFallingDetailAroundTheArea()
        {
            var plan = GeoBackgroundPlan.For(49.83, 36.68, 49.84, 36.69, Settings());
            Assert.That(plan.levels.Length, Is.EqualTo(2));
            Assert.That(plan.levels[0].All(t => t.z == 15) && plan.levels[1].All(t => t.z == 13));
            // the sharp ring reaches 600 m beyond the area, the coarse one 3 km: every tile of the area is in both
            GeoBackgroundPlan.Range(49.83, 36.68, 49.84, 36.69, 15, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Assert.That(plan.levels[0], Has.Member(new GeoTileCache.Tile(15, x, y)));
            Assert.That(plan.levels[0].Distinct().Count(), Is.EqualTo(plan.levels[0].Count));
            // elevation three zooms coarser
            Assert.That(plan.elevation[0].All(t => t.z == 12));
            // the cap leaves the farthest tiles of the coarsest ring out
            var capped = Settings(); capped.maxCells = plan.levels[0].Count + 3;
            var small = GeoBackgroundPlan.For(49.83, 36.68, 49.84, 36.69, capped);
            Assert.That(small.Cells, Is.EqualTo(capped.maxCells));
            Assert.That(small.levels[0].Count, Is.EqualTo(plan.levels[0].Count));
        }

        [Test] public void Mercator_RoundTrip()
        {
            GeoBackgroundPlan.Tile(49.834, 36.6865, 15, out double x, out double y);
            Assert.That(GeoBackgroundPlan.Lat(y, 15), Is.EqualTo(49.834).Within(1e-9));
            Assert.That(GeoBackgroundPlan.Lon(x, 15), Is.EqualTo(36.6865).Within(1e-9));
        }

        [Test] public void NeighbouringZone_DownloadsOnlyWhatTheFirstDidNot()
        {
            var settings = Settings();
            var first = GeoBackgroundPlan.For(49.830, 36.680, 49.840, 36.695, settings);
            var tiles = first.levels.SelectMany(l => l).ToList();
            var a = GeoTileCache.GetAsync(Imagery, tiles, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(a.fetched, Is.EqualTo(tiles.Count));
            Assert.That(a.tiles.All(t => t != null));
            // a zone right next to it (east): only its own tiles come from the network, the shared ones from the cache
            asked.Clear();
            var second = GeoBackgroundPlan.For(49.830, 36.695, 49.840, 36.710, settings);
            var next = second.levels.SelectMany(l => l).ToList();
            var b = GeoTileCache.GetAsync(Imagery, next, null, CancellationToken.None).GetAwaiter().GetResult();
            int shared = next.Count(t => tiles.Contains(t));
            Assert.That(shared, Is.GreaterThan(next.Count / 2), "neighbours share most of their background");
            Assert.That(b.fetched, Is.EqualTo(next.Count - shared));
            Assert.That(asked.Count, Is.EqualTo(next.Count - shared));
            Assert.That(b.tiles.All(t => t != null));
            // and the same zone again downloads nothing
            asked.Clear();
            var again = GeoTileCache.GetAsync(Imagery, next, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(again.fetched, Is.Zero); Assert.That(asked, Is.Empty);
        }

        [Test] public void Cache_IsTheSatelliteImagerysCache()
        {
            Assert.That(GeoSatellite.CacheFolder(Imagery), Is.EqualTo(GeoTileCache.Folder(Imagery)));
            Assert.That(GeoTileCache.File(Imagery, 15, 1, 2), Does.StartWith(GeoTileCache.Root));
        }

        GeoWorldPackage Package(string name)
        {
            string output = Path.Combine(root, name);
            return GeoWorldGenerator.BuildPackage(Path.GetFullPath(Sample), new GeoWorldRequest
            {
                name = name, tileSize = 200, outputFolder = output,
                settings = new GenerationSettings { heightResolution = 65, textureResolution = 64, roadPaintResolution = 64, satelliteImagery = false, buildings = false, vegetation = false, mappedProps = false }
            });
        }

        // a copy of a package with its origin moved north by `metres` (a zone next to it)
        GeoWorldPackage Moved(GeoWorldPackage package, string name, double metres)
        {
            string folder = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(folder, "tiles"));
            foreach (var f in Directory.GetFiles(Path.Combine(package.Folder, "tiles"))) File.Copy(f, Path.Combine(folder, "tiles", Path.GetFileName(f)));
            var manifest = JsonUtility.FromJson<GeoWorldManifest>(File.ReadAllText(Path.Combine(package.Folder, GeoWorldPackage.ManifestFile)));
            double dLat = metres / 111320.0;
            manifest.origin.lat += dLat; manifest.request.south += dLat; manifest.request.north += dLat;
            File.WriteAllText(Path.Combine(folder, GeoWorldPackage.ManifestFile), JsonUtility.ToJson(manifest));
            return GeoWorldPackage.Load(folder);
        }

        [Test] public void Background_OneLayerForSeveralPackages()
        {
            var a = Package("zone_a");
            var b = Moved(a, "zone_b", 700);
            var go = new GameObject("Background"); owned.Add(go);
            var background = go.AddComponent<GeoWorldBackground>();
            background.settings = Settings();
            background.Show(new[] { a, b });
            Assert.That(background.Shown);
            var names = go.GetComponentsInChildren<MeshRenderer>().Select(r => r.name).ToList();
            Assert.That(names.Count, Is.EqualTo(background.CellCount));
            Assert.That(names.Distinct().Count(), Is.EqualTo(names.Count), "no map tile drawn twice");
            // both zones' ground is in the sharp ring, and the coarse ring is left out where the sharp one covers it
            var sharp = background.Cells.Where(t => t.z == 15).ToList();
            foreach (var p in new[] { a, b })
            {
                var area = p.Manifest.request;
                GeoBackgroundPlan.Tile((area.south + area.north) / 2, (area.west + area.east) / 2, 15, out double x, out double y);
                Assert.That(sharp, Has.Member(new GeoTileCache.Tile(15, (int)x, (int)y)), p.Manifest.name);
            }
            foreach (var coarse in background.Cells.Where(t => t.z == 13))
            {
                bool whole = true;
                for (int dy = 0; dy < 4; dy++) for (int dx = 0; dx < 4; dx++) whole &= sharp.Contains(new GeoTileCache.Tile(15, coarse.x * 4 + dx, coarse.y * 4 + dy));
                Assert.That(whole, Is.False, "a coarse tile under sharp ones is not drawn");
            }
            // the second zone is where its origin is in the first's metres
            Assert.That(background.PackageOffset(b).z, Is.EqualTo(700).Within(2f));
            Assert.That(background.PackageOffset(b).x, Is.EqualTo(0).Within(2f));
            // the ground: the elevation everywhere (the scene's zero at it), a little below, and falling with the Earth's curvature
            var near = go.GetComponentsInChildren<MeshFilter>().First(f => f.name.StartsWith("Background 15/"));
            var v = near.sharedMesh.vertices.OrderBy(p => p.x * p.x + p.z * p.z).First();
            Assert.That(v.y, Is.EqualTo(-background.settings.depthBelowTerrain - (v.x * v.x + v.z * v.z) / (2 * 6371000f)).Within(0.05f));
            var far = go.GetComponentsInChildren<MeshFilter>().Where(f => f.name.StartsWith("Background 13/")).SelectMany(f => f.sharedMesh.vertices).OrderByDescending(p => p.x * p.x + p.z * p.z).First();
            float d2 = far.x * far.x + far.z * far.z;
            Assert.That(far.y, Is.EqualTo(-background.settings.depthBelowTerrain - background.settings.depthPerLevel - d2 / (2 * 6371000f)).Within(0.05f));
            Assert.That(background.Extent.width, Is.GreaterThan(6000), "the coarse ring reaches kilometres out");
        }

        [Test] public void Background_IsCutWhereATileIsLoaded()
        {
            var a = Package("zone_cut");
            var go = new GameObject("Background"); owned.Add(go);
            var background = go.AddComponent<GeoWorldBackground>();
            background.settings = Settings();
            background.Show(new[] { a });
            var material = go.GetComponentInChildren<MeshRenderer>().sharedMaterial;
            var mask = (Texture2D)material.GetTexture("_LoadedTiles");
            var grid = material.GetVector("_TileGridSize"); var gridOrigin = material.GetVector("_TileGrid");
            Assert.That(gridOrigin.z, Is.EqualTo(200), "the packages' tile grid");
            var tile = a.Tiles[0];
            int column = tile.x - (int)grid.x, row = tile.z - (int)grid.y;
            Assert.That(mask.GetPixel(column, row).r, Is.LessThan(0.5f), "not cut while the tile is not loaded");
            var handle = a.LoadTile(tile.Index); handles.Add(handle);
            Assert.That(GeoLoadedTiles.Covers(tile.Center.x, tile.Center.z));
            // the mask follows the loaded tiles (a few times a second while playing; RefreshCut at once)
            background.RefreshCut();
            Assert.That(mask.GetPixel(column, row).r, Is.GreaterThan(0.5f), "cut under the loaded tile");
            handle.Unload(); handles.Clear();
            Assert.That(GeoLoadedTiles.Covers(tile.Center.x, tile.Center.z), Is.False);
            background.RefreshCut();
            Assert.That(mask.GetPixel(column, row).r, Is.LessThan(0.5f), "back when it is unloaded");
        }
    }
}
