using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // The overview of a tiled world: the world's layer and the far ring around it (GeoOverviewBaker, GeoOverview).
    public class GeoOverviewTests
    {
        [Test] public void TileFingerprint_LeavesTheOverviewOut()
        {
            var a = new GenerationSettings();
            var b = new GenerationSettings { overview = new GeoOverviewSettings { enabled = false, farScale = 3, farResolution = 2048, nearResolution = 2048, cellsPerTile = 8 } };
            string json = GeoTiles.TileSettingsJson(a);
            Assert.That(json, Is.EqualTo(GeoTiles.TileSettingsJson(b)), "Changing the overview must rebuild no tile.");
            Assert.That(json, Does.Not.Contain("overview"));
            Assert.That(JsonUtility.ToJson(a), Does.StartWith(json.Substring(0, json.Length - 1) + ",\"overview\":"),
                "The overview is the settings' last field and the rest is as before it, so tiles built earlier keep their fingerprints.");
            var back = JsonUtility.FromJson<GenerationSettings>(json);
            Assert.That(back.mergeProps, Is.EqualTo(a.mergeProps));
            Assert.That(back.loadRadius, Is.EqualTo(a.loadRadius));
        }

        [Test] public void FarRing_StaysUnderTheWorldsLayer_AndSinksToTheMeanAway()
        {
            var area = new Rect(0, 0, 1000, 1000);
            const int nearCount = 9;
            var near = new float[nearCount * nearCount];
            for (int z = 0; z < nearCount; z++) for (int x = 0; x < nearCount; x++) near[z * nearCount + x] = 40 * Mathf.Sin(x * 1.7f) + 25 * Mathf.Cos(z * 2.3f) + 10;
            var farArea = new Rect(-4500, -4500, 10000, 10000);
            const int farCount = 41; // 250 m a cell
            const float level = 10;
            var far = GeoOverviewBaker.FarHeights(area, nearCount, nearCount, near, farArea, farCount, level);

            // anywhere over the world, between the far grid's vertices too, the far ring is under the world's layer
            for (float z = 0; z <= 1000; z += 37) for (float x = 0; x <= 1000; x += 41)
                Assert.That(Bilinear(far, farCount, farArea, x, z), Is.LessThanOrEqualTo(Bilinear(near, nearCount, area, x, z) + 0.001f), $"At {x}, {z}.");
            Assert.That(far[0], Is.EqualTo(level).Within(0.001f), "Away from the world the ring is at the mean.");
            Assert.That(far[farCount * farCount - 1], Is.EqualTo(level).Within(0.001f));
        }

        [Test] public void FarMesh_CoversTheWholeFarArea()
        {
            var data = ScriptableObject.CreateInstance<GeoOverviewData>();
            try
            {
                data.farArea = new Rect(-1000, -1000, 3000, 3000); data.farVertices = 4; // 1 km cells, 3 x 3
                data.farHeights = new float[16];
                var mesh = GeoOverview.FarMesh(data, 2);
                Assert.That(mesh.triangles.Length / 3, Is.EqualTo(9 * 2), "Under the world too: it shows where a tile has no imagery.");
                Assert.That(mesh.vertices[0].y, Is.EqualTo(-2).Within(0.001f), "Depth metres under the heights.");
                Object.DestroyImmediate(mesh);
            }
            finally { Object.DestroyImmediate(data); }
        }

        [Test] public void FarClip_ReachesTheFarthestCorner()
        {
            float distance = GeoOverview.FarthestDistance(new Rect(-100000, -100000, 200000, 200000), new Vector3(30000, 5000, 0), 0);
            Assert.That(distance, Is.EqualTo(Mathf.Sqrt(130000f * 130000f + 5000f * 5000f + 100000f * 100000f)).Within(1));
        }

        [Test] public void StableFarClip_ReachesTheAreaFromAnywhereOverIt()
        {
            var area = new Rect(-100000, -100000, 200000, 200000);
            float far = GeoOverview.StableFarClip(area, -50);
            foreach (var point in new[] { new Vector3(0, 3000, 0), new Vector3(90000, 7000, -90000), new Vector3(-100000, 19950, 100000) })
                Assert.That(far, Is.GreaterThanOrEqualTo(GeoOverview.FarthestDistance(area, point, -50)), $"From {point}.");
        }

        [Test] public void Package_ShipsTheOverviewMaterial()
        {
            var material = Resources.Load<Material>(GeoOverview.Resource);
            Assert.That(material, Is.Not.Null, "Resources/" + GeoOverview.Resource);
            Assert.That(material.shader.name, Is.EqualTo("GeoWorld/Overview"));
        }

        static float Bilinear(float[] grid, int count, Rect area, float x, float z) => Bilinear(grid, count, count, area, x, z);

        static float Bilinear(float[] grid, int countX, int countZ, Rect area, float x, float z)
        {
            float fx = (x - area.xMin) / area.width * (countX - 1), fz = (z - area.yMin) / area.height * (countZ - 1);
            int i = Mathf.Clamp((int)fx, 0, countX - 2), j = Mathf.Clamp((int)fz, 0, countZ - 2);
            float tx = fx - i, tz = fz - j;
            float a = Mathf.Lerp(grid[j * countX + i], grid[j * countX + i + 1], tx), b = Mathf.Lerp(grid[(j + 1) * countX + i], grid[(j + 1) * countX + i + 1], tx);
            return Mathf.Lerp(a, b, tz);
        }
    }
}
