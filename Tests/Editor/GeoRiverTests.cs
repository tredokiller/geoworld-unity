using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // Rivers: the water's edge is under the bank (never in the air over the bed), the bank has no wall, the river has the width and
    // middle that the ground map shows, and tiles that shape one river on their own agree at their common edge.
    public class GeoRiverTests
    {
        readonly List<Object> owned = new List<Object>();
        [TearDown] public void Cleanup() { foreach (var o in owned) if (o) Object.DestroyImmediate(o); owned.Clear(); }

        const float Level = 20, Size = 1000;
        static readonly GeoBounds Box = new GeoBounds { minX = 0, minZ = 0, maxX = Size, maxZ = Size };
        static GenerationSettings Settings() => new GenerationSettings { carveRivers = true, riverDepth = 1.5f };

        static HeightField Field(Func<float, float, float> ground, GeoBounds bounds = null, int resolution = 513)
        {
            var f = new HeightField(bounds ?? Box, resolution);
            for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++) f.heights[z, x] = ground(f.X(x), f.Z(z));
            return f;
        }

        static PathBuild River(float z, float x0, float x1, float width, string type = "river")
        {
            var controls = new List<Vector3> { new Vector3(x0, 0, z), new Vector3(x1, 0, z) };
            var samples = GeoData.Resample(controls, 2);
            for (int i = 0; i < samples.Count; i++) samples[i] = new Vector3(samples[i].x, Level, samples[i].z);
            return new PathBuild { feature = new LineFeature { id = "river/test", type = type, width = width }, river = true, controls = controls, samples = samples };
        }

        static float Cover(HeightField f) => Mathf.Max(1.2f * f.bounds.Width / (f.resolution - 1), 1.5f);

        [TestCase(0.3f)] [TestCase(2f)] [TestCase(8f)]
        public void RiverEdge_IsUnderTheBankNotInTheAir(float bank)
        {
            // ground like elevation data: a bank above the water with noise that dips below it in places
            var field = Field((x, z) => Level + bank + Mathf.Sin(x / 23) * 0.5f + Mathf.Cos(z / 17) * 0.5f - 0.3f);
            var river = River(500, 100, 900, 14);
            GeoTerrain.Carve(field, new List<PathBuild> { river }, Settings());
            var mesh = GeoMeshes.Ribbon(river, field, 0, Cover(field)); owned.Add(mesh);
            var v = mesh.vertices; Assert.That(v.Length, Is.GreaterThan(5 * 100));
            float worstAir = 0, shallowest = float.MaxValue;
            for (int s = 0; s < v.Length / 5; s++)
            {
                Vector3 left = v[s * 5], mid = v[s * 5 + 2], right = v[s * 5 + 4];
                worstAir = Mathf.Max(worstAir, left.y - field.Sample(left.x, left.z), right.y - field.Sample(right.x, right.z));
                shallowest = Mathf.Min(shallowest, mid.y - field.Sample(mid.x, mid.z));
            }
            Assert.That(worstAir, Is.LessThan(0.03f), "no edge of the water hangs in the air");
            Assert.That(shallowest, Is.GreaterThan(1.2f), "the water is over its bed all along, never buried");
        }

        [Test] public void RiverBank_HigherThanTheWater_EndsInASlopeNotAWall()
        {
            var field = Field((x, z) => Level + 12);
            var river = River(500, 100, 900, 14);
            GeoTerrain.Carve(field, new List<PathBuild> { river }, Settings());
            float cell = Size / 512, worst = 0;
            for (float x = 200; x <= 800; x += 50)
            {
                float previous = field.Sample(x, 500 + 7 + 2 * cell);
                for (float d = 7 + 3 * cell; d <= 7 + 140; d += cell)
                {
                    float h = field.Sample(x, 500 + d); worst = Mathf.Max(worst, Mathf.Abs(h - previous)); previous = h;
                }
            }
            Assert.That(worst, Is.LessThan(1.8f), "no step over a cell steeper than 42 degrees");
            Assert.That(field.Sample(500, 500 + 7 + 300), Is.EqualTo(Level + 12).Within(0.001f), "far from the river nothing changed");
        }

        [Test] public void Ribbon_FollowsTheWidthOfEachSample()
        {
            var field = Field((x, z) => Level + 1);
            var river = River(500, 100, 900, 10);
            river.widths = river.samples.Select((p, i) => 10f + 20f * i / (river.samples.Count - 1)).ToList();
            var mesh = GeoMeshes.Ribbon(river, field, 0, 1.5f); owned.Add(mesh);
            var v = mesh.vertices;
            for (int s = 0; s < river.samples.Count; s += 40)
                Assert.That(Vector3.Distance(v[s * 5], v[s * 5 + 4]), Is.EqualTo(river.widths[s] + 3).Within(0.01f));
            // a path without widths keeps its feature's width
            var plain = River(500, 100, 900, 10);
            var narrow = GeoMeshes.Ribbon(plain, field, 0); owned.Add(narrow);
            Assert.That(Vector3.Distance(narrow.vertices[0], narrow.vertices[4]), Is.EqualTo(10).Within(0.01f));
        }

        [Test] public void ClipPath_KeepsTheWidthsOfTheStretch()
        {
            var river = River(500, 0, 1000, 8);
            river.widths = river.samples.Select((p, i) => 8f + i * 0.1f).ToList();
            var pieces = GeoTiles.ClipPath(river, new GeoBounds { minX = 200, minZ = 400, maxX = 400, maxZ = 600 }, 2);
            Assert.That(pieces.Count, Is.EqualTo(1));
            var piece = pieces[0];
            Assert.That(piece.widths.Count, Is.EqualTo(piece.samples.Count));
            int first = river.samples.IndexOf(piece.samples[0]);
            for (int i = 0; i < piece.widths.Count; i += 20) Assert.That(piece.widths[i], Is.EqualTo(river.widths[first + i]));
        }

        [Test] public void RiverShore_IsTheSameOnBothSidesOfATileEdge()
        {
            Func<float, float, float> ground = (x, z) => Level + 3 + Mathf.Sin(x / 31) * 2 + Mathf.Cos(z / 43) * 2;
            var west = Field(ground, new GeoBounds { minX = 0, minZ = 0, maxX = 512, maxZ = 512 }, 257);
            var east = Field(ground, new GeoBounds { minX = 512, minZ = 0, maxX = 1024, maxZ = 512 }, 257);
            var river = River(250, 50, 980, 20);
            river.widths = river.samples.Select((p, i) => 14f + 10f * Mathf.Sin(i * 0.05f)).ToList();
            GeoTerrain.Carve(west, new List<PathBuild> { river }, Settings());
            GeoTerrain.Carve(east, new List<PathBuild> { river }, Settings());
            int changed = 0;
            for (int z = 0; z < 257; z++)
            {
                Assert.That(west.heights[z, 256], Is.EqualTo(east.heights[z, 0]).Within(1e-4f), "row " + z);
                if (Mathf.Abs(west.heights[z, 256] - ground(512, west.Z(z))) > 0.01f) changed++;
            }
            Assert.That(changed, Is.GreaterThan(4), "the river does reach the edge");
        }

        // ---------------------------------------------------------------- the ground map

        // water where z is in [z0, z1), over the whole 1 km square, 1 m a pixel
        static GeoWaterMask Mask(float z0, float z1, Func<float, float, bool> except = null)
        {
            var water = new bool[1000 * 1000];
            for (int row = 0; row < 1000; row++)
            {
                float z = 1000 - row - 0.5f;
                for (int col = 0; col < 1000; col++)
                    water[row * 1000 + col] = z >= z0 && z < z1 && !(except != null && except(col + 0.5f, z));
            }
            return new GeoWaterMask(water, 1000, 1000, 1, 0, 1000);
        }

        [Test] public void WaterMask_ReadsTheMapNorthRowFirst_AndMeasuresARun()
        {
            var mask = Mask(480, 520);
            Assert.IsTrue(mask.IsWater(500, 500)); Assert.IsFalse(mask.IsWater(500, 530)); Assert.IsFalse(mask.IsWater(500, 470)); Assert.IsFalse(mask.IsWater(-5, 500));
            Assert.IsTrue(mask.Across(new Vector2(300, 490), new Vector2(0, 1), 12, 90, out float lo, out float hi));
            Assert.That(lo, Is.EqualTo(-10).Within(1.01f)); Assert.That(hi, Is.EqualTo(30).Within(1.01f));
            Assert.IsFalse(mask.Across(new Vector2(300, 440), new Vector2(0, 1), 12, 90, out _, out _), "no water near");
        }

        [Test] public void Fit_WidensTheRiverToTheWaterOfTheMap_AndMovesItsMiddleThere()
        {
            var mask = Mask(486, 526); // 40 m of water, its middle at z = 506
            var river = River(500, 100, 900, 14);
            Func<float, float, float> ground = (x, z) => 10 + 0.01f * x;
            Assert.IsTrue(GeoRivers.Fit(river, mask, ground, 2));
            Assert.That(river.widths.Count, Is.EqualTo(river.samples.Count));
            for (int i = 20; i < river.samples.Count - 20; i += 25)
            {
                Assert.That(river.widths[i], Is.EqualTo(40).Within(2.5f), "width " + i);
                Assert.That(river.samples[i].z, Is.EqualTo(506).Within(1.5f), "middle " + i);
                Assert.That(river.samples[i].y, Is.EqualTo(ground(river.samples[i].x, river.samples[i].z) - 0.35f).Within(0.001f), "level " + i);
            }
            Assert.That(river.feature.width, Is.EqualTo(river.widths.Max()), "the box round the river is made from the widest");
        }

        [Test] public void Fit_TheWidthDoesNotJump_ItTapersOverTheLengthItNeeds()
        {
            // 20 m of water for the first half of the river, 60 m for the second: a step in the map
            var water = new bool[1000 * 1000];
            for (int row = 0; row < 1000; row++)
                for (int col = 0; col < 1000; col++)
                {
                    float z = 1000 - row - 0.5f, half = col < 500 ? 10 : 30;
                    water[row * 1000 + col] = Mathf.Abs(z - 500) <= half;
                }
            var river = River(500, 100, 900, 14);
            Assert.IsTrue(GeoRivers.Fit(river, new GeoWaterMask(water, 1000, 1000, 1, 0, 1000), (x, z) => 10, 2));
            for (int i = 1; i < river.widths.Count; i++)
                Assert.That(Mathf.Abs(river.widths[i] - river.widths[i - 1]), Is.LessThanOrEqualTo(1.01f), "no step at sample " + i);
            Assert.That(river.widths[20], Is.EqualTo(20).Within(2.5f)); // x = 140
            Assert.That(river.widths[river.widths.Count - 20], Is.EqualTo(60).Within(2.5f)); // x = 860, well past the taper
            Assert.That(river.widths[river.samples.FindIndex(p => p.x > 500)], Is.LessThan(30), "the wide part has not started yet where the map says it does");
        }

        [Test] public void Fit_IgnoresALakeANarrowRiverAndAMissingMap()
        {
            var river = River(500, 100, 900, 14);
            Func<float, float, float> ground = (x, z) => 10;
            Assert.IsFalse(GeoRivers.Fit(river, null, ground, 2), "no map");
            Assert.IsFalse(GeoRivers.Fit(river, Mask(300, 700), ground, 2), "water 400 m wide is a lake, not the width of a river");
            Assert.IsFalse(GeoRivers.Fit(river, Mask(600, 640), ground, 2), "no water within reach of the line");
            Assert.IsNull(river.widths); Assert.That(river.feature.width, Is.EqualTo(14));
            Assert.That(river.samples.All(p => p.z == 500));
        }

        [Test] public void Fit_AGapInTheWater_ABridge_DoesNotChangeTheWidth()
        {
            var mask = Mask(480, 520, (x, z) => x > 400 && x < 408); // a bridge: no water under it on the map
            var river = River(500, 100, 900, 14);
            Assert.IsTrue(GeoRivers.Fit(river, mask, (x, z) => 10, 2));
            int at = river.samples.FindIndex(p => p.x > 403);
            Assert.That(river.widths[at], Is.EqualTo(40).Within(2.5f));
        }

        [Test] public void SmoothLevels_TakesOutTheNoiseAndKeepsASlope()
        {
            var samples = GeoData.Resample(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(400, 0, 0) }, 2);
            var rnd = new System.Random(5);
            for (int i = 0; i < samples.Count; i++) samples[i] = new Vector3(samples[i].x, 100 - samples[i].x * 0.01f + ((float)rnd.NextDouble() - 0.5f) * 2, 0);
            float roughBefore = 0, roughAfter;
            for (int i = 1; i < samples.Count; i++) roughBefore += Mathf.Abs(samples[i].y - samples[i - 1].y);
            GeoRivers.SmoothLevels(samples, 2, 40);
            roughAfter = 0;
            for (int i = 1; i < samples.Count; i++) roughAfter += Mathf.Abs(samples[i].y - samples[i - 1].y);
            Assert.That(roughAfter, Is.LessThan(roughBefore * 0.3f));
            Assert.That(samples[0].y, Is.EqualTo(100).Within(1.1f)); Assert.That(samples[samples.Count - 1].y, Is.EqualTo(96).Within(1.1f), "the fall of the river stays");
            // a perfectly straight slope is untouched
            var straight = GeoData.Resample(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(200, 0, 0) }, 2);
            for (int i = 0; i < straight.Count; i++) straight[i] = new Vector3(straight[i].x, 50 - straight[i].x * 0.02f, 0);
            var copy = new List<Vector3>(straight);
            GeoRivers.SmoothLevels(straight, 2, 40);
            for (int i = 0; i < straight.Count; i++) Assert.That(straight[i].y, Is.EqualTo(copy[i].y).Within(1e-3f));
        }
    }
}
