using NUnit.Framework;

namespace GeoWorld.Editor.Tests
{
    // Any exported area of Ukraine becomes a world the same way: satellite imagery sized to the area, road meshes, and the
    // app's condition as it is: ruins for every building it gives as damaged, homes for the rest.
    public class GeoDefaultsTests
    {
        static GeoBounds Square(float metres) => new GeoBounds { minX = -metres / 2, minZ = -metres / 2, maxX = metres / 2, maxZ = metres / 2 };

        [Test] public void NewSettingsAreTheStandardWorld()
        {
            var settings = new GenerationSettings();
            Assert.IsTrue(settings.satelliteImagery);
            Assert.AreEqual(0, settings.satelliteResolution, "Auto");
            Assert.IsTrue(settings.roadMeshes);
            Assert.AreEqual(GeoRuinUse.AllDamage, settings.ruinModels);
            Assert.AreEqual(GeoHomeUse.IntactAndUnknown, settings.homeModels);
            Assert.IsTrue(settings.applyCondition);
        }

        [Test] public void TileSettingsAreFinerPerMetreButKeepTheStandardContent()
        {
            var tile = GeoDefaults.TileSettings();
            Assert.AreEqual(513, tile.heightResolution);
            Assert.AreEqual(512, tile.textureResolution);
            Assert.AreEqual(0, tile.satelliteResolution);
            Assert.AreEqual(GeoHomeUse.IntactAndUnknown, tile.homeModels);
        }

        [Test] public void AutoSatelliteTextureFollowsTheAreaSize()
        {
            Assert.AreEqual(1024, GeoSatellite.AutoResolution(Square(300)));
            Assert.AreEqual(1024, GeoSatellite.AutoResolution(Square(600)));
            Assert.AreEqual(2048, GeoSatellite.AutoResolution(Square(1000)));
            Assert.AreEqual(4096, GeoSatellite.AutoResolution(Square(2000)));
            Assert.AreEqual(8192, GeoSatellite.AutoResolution(Square(7500)));
            Assert.AreEqual(8192, GeoSatellite.AutoResolution(Square(40000)));
            // the longer side counts
            Assert.AreEqual(4096, GeoSatellite.AutoResolution(new GeoBounds { minX = 0, minZ = 0, maxX = 2000, maxZ = 300 }));
        }

        // From Crimea to Chernihiv oblast, a 1 km tile asks for zoom 18, the imagery's own detail, not deeper
        [TestCase(44.4, 34.0)] [TestCase(46.5, 30.7)] [TestCase(48.98, 37.81)] [TestCase(49.84, 24.03)] [TestCase(52.3, 33.0)]
        public void AOneKilometreTileGetsZoom18AnywhereInUkraine(double lat, double lon)
        {
            var b = Square(1000);
            int zoom = GeoSatellite.Zoom(new GeoEnu(lat, lon), b, lat, GeoSatellite.AutoResolution(b), 19);
            Assert.AreEqual(18, zoom);
        }
    }
}
