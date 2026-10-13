using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // Satellite imagery on the terrain: the export's ENU projection (checked against extractor/geo.py), Web Mercator
    // tile maths, zoom choice, and the resampled texture, built offline from a tile cache filled by the test.
    public class GeoSatelliteTests
    {
        const double Lat0 = 48.97583678, Lon0 = 37.81249523;

        [Test] public void Enu_MatchesTheExtractor()
        {
            var enu = new GeoEnu(Lat0, Lon0);
            enu.Forward(48.98, 37.80, out double east, out double north);
            Assert.That(east, Is.EqualTo(-914.6644025728443).Within(1e-6));
            Assert.That(north, Is.EqualTo(463.06406818313974).Within(1e-6));
            enu.Inverse(-450, 360, out double lat, out double lon);
            Assert.That(lat, Is.EqualTo(48.979073755540334).Within(1e-10));
            Assert.That(lon, Is.EqualTo(37.80634789414463).Within(1e-10));
        }

        [Test] public void Enu_RoundTrips()
        {
            var enu = new GeoEnu(Lat0, Lon0);
            foreach (var p in new[] { new Vector2(0, 0), new Vector2(1500, -2200), new Vector2(-3000, 2500) })
            {
                enu.Inverse(p.x, p.y, out double lat, out double lon);
                enu.Forward(lat, lon, out double east, out double north);
                Assert.That(east, Is.EqualTo(p.x).Within(1e-4)); Assert.That(north, Is.EqualTo(p.y).Within(1e-4));
            }
        }

        [Test] public void Mercator_PixelsAndTiles()
        {
            GeoSatellite.Pixel(0, 0, 1, 256, out double x, out double y);
            Assert.That(x, Is.EqualTo(256).Within(1e-9)); Assert.That(y, Is.EqualTo(256).Within(1e-9));
            GeoSatellite.Pixel(85.05112878, -180, 0, 256, out x, out y);
            Assert.That(x, Is.EqualTo(0).Within(1e-9)); Assert.That(y, Is.EqualTo(0).Within(1e-6));
            // Lyman's origin at zoom 18: OpenStreetMap's slippy-map formula gives tile 158606 / 90052
            GeoSatellite.Pixel(Lat0, Lon0, 18, 1, out x, out y);
            Assert.That((int)x, Is.EqualTo(158606)); Assert.That((int)y, Is.EqualTo(90052));
            Assert.That(GeoSatellite.MetresPerPixel(0, 0), Is.EqualTo(156543.03).Within(0.01));
        }

        [Test] public void Zoom_FollowsTheTextureAndTheTileLimit()
        {
            var enu = new GeoEnu(Lat0, Lon0);
            var bounds = new GeoBounds { minX = -300, minZ = -300, maxX = 300, maxZ = 300 };
            int zoom = GeoSatellite.Zoom(enu, bounds, Lat0, 4096, 22);
            Assert.That(GeoSatellite.MetresPerPixel(Lat0, zoom), Is.LessThanOrEqualTo(600.0 / 4096));
            Assert.That(GeoSatellite.MetresPerPixel(Lat0, zoom - 1), Is.GreaterThan(600.0 / 4096));
            Assert.That(GeoSatellite.Zoom(enu, bounds, Lat0, 4096, 17), Is.EqualTo(17));
            int limited = GeoSatellite.Zoom(enu, bounds, Lat0, 4096, 22, 16);
            var tiles = GeoSatellite.Tiles(enu, bounds, limited);
            Assert.That(tiles.width * tiles.height, Is.LessThanOrEqualTo(16));
        }

        [Test] public void Build_ResamplesCachedTilesNorthUp()
        {
            const string template = "https://geoworld.test/tiles/{z}/{x}/{y}.png";
            string cache = GeoSatellite.CacheFolder(template);
            var origin = new GeoOrigin { lat = Lat0, lon = Lon0 };
            var bounds = new GeoBounds { minX = -200, minZ = -150, maxX = 200, maxZ = 250 };
            var settings = new GenerationSettings { satelliteTiles = template, satelliteResolution = 1024, satelliteMaxZoom = 18 };
            var enu = new GeoEnu(Lat0, Lon0);
            var range = GeoSatellite.Tiles(enu, bounds, 17);
            Assert.That(range.width * range.height, Is.GreaterThan(1));
            Texture2D result = null;
            try
            {
                // every tile one colour that names it: red = column, green = row in the range (steps of 32)
                var tile = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                for (int ty = 0; ty < range.height; ty++) for (int tx = 0; tx < range.width; tx++)
                {
                    var colours = new Color32[256 * 256];
                    for (int i = 0; i < colours.Length; i++) colours[i] = new Color32((byte)(tx * 32), (byte)(ty * 32), 128, 255);
                    tile.SetPixels32(colours); tile.Apply();
                    string file = Path.Combine(cache, "17", (range.x + tx).ToString(), (range.y + ty) + ".img");
                    Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(file, tile.EncodeToPNG());
                }
                Object.DestroyImmediate(tile);
                // zoom 18 is not covered: the server answered 404 for each of its tiles
                var deeper = GeoSatellite.Tiles(enu, bounds, 18);
                for (int ty = 0; ty < deeper.height; ty++) for (int tx = 0; tx < deeper.width; tx++)
                {
                    string none = Path.Combine(cache, "18", (deeper.x + tx).ToString(), (deeper.y + ty) + ".none");
                    Directory.CreateDirectory(Path.GetDirectoryName(none)); File.WriteAllBytes(none, new byte[0]);
                }
                var notes = new List<string>();
                result = GeoSatellite.Build(origin, bounds, settings, notes, null);
                Assert.That(result.width, Is.EqualTo(1024)); Assert.That(result.height, Is.EqualTo(1024));
                Assert.That(result.mipmapCount, Is.GreaterThan(1));
                Assert.That(notes[0], Does.Contain("zoom 18 is not covered here, zoom 17 is used"));
                Assert.That(notes[1], Does.Contain("at zoom 17"));
                int checkedTexels = 0;
                foreach (var texel in new[] { new Vector2Int(2, 2), new Vector2Int(1021, 2), new Vector2Int(2, 1021), new Vector2Int(1021, 1021), new Vector2Int(512, 512) })
                {
                    double x = bounds.minX + (texel.x + 0.5) / 1024 * bounds.Width, z = bounds.minZ + (texel.y + 0.5) / 1024 * bounds.Depth;
                    enu.Inverse(x, z, out double lat, out double lon);
                    GeoSatellite.Pixel(lat, lon, 17, 256, out double px, out double py);
                    double fx = px / 256 - range.x, fy = py / 256 - range.y;
                    if (Frac(fx) < 0.02 || Frac(fx) > 0.98 || Frac(fy) < 0.02 || Frac(fy) > 0.98) continue; // on a tile seam
                    var colour = (Color32)result.GetPixel(texel.x, texel.y);
                    Assert.That(colour.r, Is.EqualTo((int)fx * 32).Within(8), "column at texel " + texel);
                    Assert.That(colour.g, Is.EqualTo((int)fy * 32).Within(8), "row at texel " + texel);
                    checkedTexels++;
                }
                Assert.That(checkedTexels, Is.GreaterThanOrEqualTo(3));
                // north is up: the north edge of the texture lies in a tile row at or above the south edge's
                Assert.That(result.GetPixel(512, 1021).g, Is.LessThanOrEqualTo(result.GetPixel(512, 2).g + 0.02f));
            }
            finally
            {
                if (result) Object.DestroyImmediate(result);
                if (Directory.Exists(cache)) Directory.Delete(cache, true);
            }
        }

        [Test] public void Placeholders_RepeatedTilesAreDropped()
        {
            var placeholder = new byte[] { 1, 2, 3 };
            var images = new byte[40][];
            for (int i = 0; i < images.Length; i++) images[i] = i < 30 ? new byte[] { (byte)i, 9 } : (byte[])placeholder.Clone();
            images[5] = null;
            Assert.That(GeoSatellite.DropPlaceholders(images), Is.EqualTo(10));
            Assert.That(images[35], Is.Null); Assert.That(images[4], Is.Not.Null);
            // two equal tiles among many are imagery (a field, a roof), not a placeholder
            var few = new byte[60][];
            for (int i = 0; i < few.Length; i++) few[i] = new[] { (byte)(i < 2 ? 0 : i) };
            Assert.That(GeoSatellite.DropPlaceholders(few), Is.EqualTo(0));
        }

        static double Frac(double v) => v - System.Math.Floor(v);
    }
}
