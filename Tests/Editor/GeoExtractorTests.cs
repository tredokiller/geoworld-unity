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
    }
}
