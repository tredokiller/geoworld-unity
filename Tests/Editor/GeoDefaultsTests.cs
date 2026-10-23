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
            Assert.AreEqual(GeoSatelliteCompression.Crunched, settings.satelliteCompression, "the satellite texture is most of a world's size");
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

        // Tiles stream 2 km round the camera unless set otherwise (1-10 km); the settings say how many tiles that keeps loaded,
        // and a tile goes 600 m further before it is unloaded.
        [Test] public void LoadRadiusIsTwoKilometres_AndSaysHowManyTilesItKeeps()
        {
            Assert.AreEqual(GeoTileStreamer.DefaultRadius, new GenerationSettings().loadRadius);
            Assert.AreEqual(2000, GeoTileStreamer.DefaultRadius);
            Assert.AreEqual((9, 12), GeoTileStreamer.TilesWithin(1000, 1000));
            Assert.AreEqual((21, 24), GeoTileStreamer.TilesWithin(2000, 1000));
            Assert.AreEqual((357, 360), GeoTileStreamer.TilesWithin(10000, 1000));
            Assert.AreEqual((9, 12), GeoTileStreamer.TilesWithin(4000, 2000), "the same radius in tiles of twice the size");
            var go = new UnityEngine.GameObject("Streamer test");
            try
            {
                var streamer = go.AddComponent<GeoTileStreamer>();
                streamer.SetRadius(5000);
                Assert.AreEqual(5000, streamer.loadRadius);
                Assert.AreEqual(5600, streamer.UnloadDistance);
                // an unload radius left smaller than the load radius: tiles still go 600 m beyond it
                streamer.loadRadius = 3000; streamer.unloadRadius = 2600;
                Assert.AreEqual(3600, streamer.UnloadDistance);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
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
