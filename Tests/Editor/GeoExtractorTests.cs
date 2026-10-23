using System;
using System.IO;
using NUnit.Framework;

namespace GeoWorld.Editor.Tests
{
    // The Building Extractor app from Unity: found beside the project while the package is developed (an installed
    // package carries it in Extractor~), its exports where the Geo World window lists them; and the size of an area.
    public class GeoExtractorTests
    {
        [Test] public void TheAppIsFoundAndItsExportsAreListed()
        {
            string app = GeoExtractor.AppFolder;
            Assume.That(app, Is.Not.Null, "Needs the Building Extractor app (the repository, or Extractor~ of the package).");
            Assert.That(File.Exists(Path.Combine(app, "cli.py")));
            Assert.That(Directory.Exists(Path.Combine(app, "extractor")));
            Assert.That(GeoExtractor.ExportsFolder, Is.EqualTo(Path.Combine(GeoExtractor.Home, "exports")));
            Assert.That(GeoDefaults.ExportsFolder, Is.EqualTo(GeoExtractor.ExportsFolder));
        }

        [Test] public void AnAreaIsMeasuredInKilometres()
        {
            // 0.01 degrees of latitude is 1.11 km; of longitude at 49 degrees north about 0.73 km
            var (width, height) = GeoAreaWindow.SizeKm(49.0, 36.0, 49.01, 36.01);
            Assert.That(height, Is.EqualTo(1.113).Within(0.002));
            Assert.That(width, Is.EqualTo(0.730).Within(0.003));
        }

        [Test] public void AnAreaIsAtMostAHundredSquareKilometres()
        {
            const double lat = 49.6, lon = 36.4;
            (double width, double height) Size(double cornerLat, double cornerLon) =>
                GeoAreaWindow.SizeKm(Math.Min(lat, cornerLat), Math.Min(lon, cornerLon), Math.Max(lat, cornerLat), Math.Max(lon, cornerLon));
            // dragged about 30 x 30 km to the north-east and to the south-west: stops at 100 km², square as dragged, on
            // the side it was dragged to
            foreach (var (dLat, dLon) in new[] { (0.27, 0.416), (-0.27, -0.416) })
            {
                var (cornerLat, cornerLon) = GeoAreaWindow.Limit(lat, lon, lat + dLat, lon + dLon);
                var (width, height) = Size(cornerLat, cornerLon);
                Assert.That(width * height, Is.EqualTo(GeoAreaWindow.MaxKm2).Within(0.01));
                Assert.That(width / height, Is.EqualTo(1).Within(0.02));
                Assert.That(Math.Sign(cornerLat - lat), Is.EqualTo(Math.Sign(dLat)));
                Assert.That(Math.Sign(cornerLon - lon), Is.EqualTo(Math.Sign(dLon)));
            }
            // any shape up to the limit: a 50 x 1 km strip and a small area are left as they are
            foreach (var (dLat, dLon) in new[] { (0.009, 0.694), (0.03, -0.05) })
            {
                var (cornerLat, cornerLon) = GeoAreaWindow.Limit(lat, lon, lat + dLat, lon + dLon);
                Assert.That(cornerLat, Is.EqualTo(lat + dLat).Within(1e-12));
                Assert.That(cornerLon, Is.EqualTo(lon + dLon).Within(1e-12));
            }
            var (stripWidth, stripHeight) = Size(lat + 0.009, lon + 0.694);
            Assert.That(stripWidth * stripHeight, Is.LessThan(GeoAreaWindow.MaxKm2));
            Assert.That(stripWidth, Is.GreaterThan(45));
        }

        // 40 x 20 km is too large for one export: the fewest parts of at most 100 km² and 20 km a side, their overlap included,
        // which cover the area and overlap their neighbours by about 100 m.
        [Test] public void ALargeAreaIsCutIntoTheFewestParts()
        {
            Assert.That(GeoExtractor.PartGrid(9, 9), Is.EqualTo((1, 1)));
            Assert.That(GeoExtractor.PartGrid(40, 20), Is.EqualTo((3, 3)));
            foreach (var (width, height) in new[] { (40.0, 20.0), (30.0, 30.0), (12.0, 9.0), (45.0, 4.0), (25.0, 40.0) })
            {
                var (columns, rows) = GeoExtractor.PartGrid(width, height);
                double w = width / columns + (columns > 1 ? GeoExtractor.PartOverlapKm : 0), h = height / rows + (rows > 1 ? GeoExtractor.PartOverlapKm : 0);
                Assert.That(w * h, Is.LessThanOrEqualTo(GeoExtractor.PartKm2 + 1e-9), width + " x " + height);
                Assert.That(Math.Max(w, h), Is.LessThanOrEqualTo(GeoExtractor.PartSideKm + 1e-9), width + " x " + height);
                // one part fewer in either direction would be too large
                foreach (var (c, r) in new[] { (columns - 1, rows), (columns, rows - 1) })
                {
                    if (c < 1 || r < 1) continue;
                    double cw = width / c + (c > 1 ? GeoExtractor.PartOverlapKm : 0), ch = height / r + (r > 1 ? GeoExtractor.PartOverlapKm : 0);
                    Assert.That(cw * ch > GeoExtractor.PartKm2 || Math.Max(cw, ch) > GeoExtractor.PartSideKm, width + " x " + height + ": " + c + " x " + r + " would do");
                }
            }
            // a 40 x 20 km area near Kharkiv: the parts cover it, meet with an overlap, and none is too large
            const double s = 49.8, w0 = 36.0, n = 49.8 + 20 / 111.32, e = 36.0 + 40 / (111.32 * 0.6455);
            var (cols, rws) = GeoExtractor.PartGrid(GeoAreaWindow.SizeKm(s, w0, n, e).width, GeoAreaWindow.SizeKm(s, w0, n, e).height);
            var a = GeoExtractor.PartBounds(s, w0, n, e, cols, rws, 0, 0);
            var east = GeoExtractor.PartBounds(s, w0, n, e, cols, rws, 1, 0);
            var north = GeoExtractor.PartBounds(s, w0, n, e, cols, rws, 0, 1);
            var last = GeoExtractor.PartBounds(s, w0, n, e, cols, rws, cols - 1, rws - 1);
            Assert.That(a.south, Is.EqualTo(s)); Assert.That(a.west, Is.EqualTo(w0));
            Assert.That(last.north, Is.EqualTo(n)); Assert.That(last.east, Is.EqualTo(e));
            Assert.That(GeoAreaWindow.SizeKm(a.south, east.west, a.north, a.east).width, Is.EqualTo(GeoExtractor.PartOverlapKm).Within(0.01), "overlap east");
            Assert.That(GeoAreaWindow.SizeKm(north.south, a.west, a.north, a.east).height, Is.EqualTo(GeoExtractor.PartOverlapKm).Within(0.01), "overlap north");
            var size = GeoAreaWindow.SizeKm(east.south, east.west, east.north, east.east);
            Assert.That(size.width * size.height, Is.LessThanOrEqualTo(GeoExtractor.PartKm2));
        }

        [Test] public void AMovedAreaKeepsItsSizeOnTheGround()
        {
            const double s = 49.6, w = 36.4, n = 49.68, e = 36.52;
            var before = GeoAreaWindow.SizeKm(s, w, n, e);
            // 3 degrees south (a degree of longitude is longer there) and half a degree east
            var (s2, w2, n2, e2) = GeoAreaWindow.Moved(s, w, n, e, -3, 0.5);
            var after = GeoAreaWindow.SizeKm(s2, w2, n2, e2);
            Assert.That(after.width, Is.EqualTo(before.width).Within(1e-9));
            Assert.That(after.height, Is.EqualTo(before.height).Within(1e-9));
            Assert.That(s2, Is.EqualTo(s - 3).Within(1e-12));
            Assert.That((w2 + e2) / 2, Is.EqualTo((w + e) / 2 + 0.5).Within(1e-12));
            Assert.That(e2 - w2, Is.LessThan(e - w)); // fewer degrees for the same kilometres
        }
    }
}
