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
