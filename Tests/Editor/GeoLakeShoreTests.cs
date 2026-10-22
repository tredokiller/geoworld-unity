using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // The shore of a lake on ground that is not flat: a bank higher than the water must not end in a wall, and the edge of the
    // water must not hang over land that is lower than it (that edge is what shows as steps and squares along the shore).
    public class GeoLakeShoreTests
    {
        readonly List<Object> owned = new List<Object>();
        [TearDown] public void Cleanup() { foreach (var o in owned) if (o) Object.DestroyImmediate(o); owned.Clear(); }

        const float Size = 1000, Level = 20, Depth = 1.5f;
        static readonly GeoBounds Box = new GeoBounds { minX = 0, minZ = 0, maxX = Size, maxZ = Size };

        static HeightField Field(Func<float, float, float> ground)
        {
            var f = new HeightField(Box, 513); // 1.95 m grid, as a 2 km tile
            for (int z = 0; z < 513; z++) for (int x = 0; x < 513; x++) f.heights[z, x] = ground(f.X(x), f.Z(z));
            return f;
        }

        static AreaFeature Lake(float cx, float cz, float r)
        {
            var ring = new List<float>();
            for (int i = 0; i < 120; i++) { float a = i * Mathf.PI * 2 / 120; ring.Add(cx + Mathf.Cos(a) * r); ring.Add(cz + Mathf.Sin(a) * r); }
            return new AreaFeature { id = "lake/test", material = "water", rings = new[] { ring.ToArray() } };
        }

        // The largest step between neighbouring nodes of the land along rays from the middle of the lake, from one cell beyond the
        // shore on (the first cell is the face of the bank at the water line, under the water for the most part).
        static float WorstStep(HeightField field, float cx, float cz, float r)
        {
            float cell = Size / 512, worst = 0;
            for (int angle = 0; angle < 360; angle += 10)
            {
                var d = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
                float previous = field.Sample(cx + d.x * (r + cell), cz + d.y * (r + cell));
                for (float k = r + 2 * cell; k <= r + 120; k += cell)
                {
                    float h = field.Sample(cx + d.x * k, cz + d.y * k);
                    worst = Mathf.Max(worst, Mathf.Abs(h - previous)); previous = h;
                }
            }
            return worst;
        }

        // no step over a cell steeper than 42 degrees (a gentle bank is 27)
        const float SteepestStep = 1.8f;

        [TestCase(8f)] [TestCase(20f)]
        public void BankHigherThanTheWater_EndsInASlopeNotAWall(float bankHeight)
        {
            var field = Field((x, z) => Level + bankHeight);
            var lake = Lake(500, 500, 60);
            GeoLakes.Carve(new[] { lake }, new[] { Level }, field, Depth);
            Assert.That(WorstStep(field, 500, 500, 60), Is.LessThan(SteepestStep), "no step over a cell is steeper than a bank");
            Assert.That(field.Sample(500, 500 + 60 + 300), Is.EqualTo(Level + bankHeight).Within(0.001f), "far from the lake nothing changed");
        }

        [Test] public void GroundLowerThanTheWater_NeverLeavesTheEdgeOfTheWaterHangingOverIt()
        {
            // the ground slopes 4 % across the lake: its west shore is lower than the water, its east shore higher
            Func<float, float, float> ground = (x, z) => Level + (x - 500) * 0.04f;
            var field = Field(ground);
            var lake = Lake(500, 500, 60);
            GeoLakes.Carve(new[] { lake }, new[] { Level }, field, Depth);
            var mesh = GeoLakes.Surface(new[] { lake }, Level, field, Box); owned.Add(mesh);
            Assert.That(mesh.vertexCount, Is.GreaterThan(0));
            var ring = lake.rings[0];
            Func<float, float, bool> inside = (x, z) => GeoData.InPolygon(x, z, new[] { ring });
            int outside = 0; float worstHang = 0;
            foreach (var v in mesh.vertices)
            {
                if (inside(v.x, v.z)) continue;
                outside++;
                worstHang = Mathf.Max(worstHang, Level - field.Sample(v.x, v.z)); // metres of air under the edge of the water
            }
            Assert.That(outside, Is.GreaterThan(20), "the surface reaches a cell beyond the outline");
            Assert.That(worstHang, Is.LessThan(0.03f), "the edge of the water has ground under it everywhere");
            Assert.That(WorstStep(field, 500, 500, 60), Is.LessThan(SteepestStep));
        }

        [Test] public void ShoreOnLowLand_RaisedToTheWaterOnlyNearIt()
        {
            var field = Field((x, z) => Level - 3); // the land is 3 m under the water level all round
            var lake = Lake(500, 500, 60);
            GeoLakes.Carve(new[] { lake }, new[] { Level }, field, Depth);
            Assert.That(field.Sample(500 + 60 + 1, 500), Is.GreaterThanOrEqualTo(Level - 0.02f), "a shoulder just outside the shore");
            Assert.That(field.Sample(500 + 60 + 200, 500), Is.EqualTo(Level - 3).Within(0.001f), "far away the land is as it was");
            Assert.That(WorstStep(field, 500, 500, 60), Is.LessThan(SteepestStep));
        }

        // Two tiles of one world shape the shore of a lake that crosses their edge on their own, from the same outline: the
        // ground on the edge they share must come out the same (a wall at the seam otherwise).
        [Test] public void Shore_IsTheSameOnBothSidesOfATileEdge()
        {
            Func<float, float, float> ground = (x, z) => Level + 6 + Mathf.Sin(x / 37) * 4 + Mathf.Cos(z / 51) * 3 - (x - 512) * 0.03f;
            var west = new HeightField(new GeoBounds { minX = 0, minZ = 0, maxX = 512, maxZ = 512 }, 257);
            var east = new HeightField(new GeoBounds { minX = 512, minZ = 0, maxX = 1024, maxZ = 512 }, 257);
            foreach (var field in new[] { west, east })
                for (int z = 0; z < 257; z++) for (int x = 0; x < 257; x++) field.heights[z, x] = ground(field.X(x), field.Z(z));
            var lake = Lake(512, 256, 90);
            GeoLakes.Carve(new[] { lake }, new[] { Level }, west, Depth);
            GeoLakes.Carve(new[] { lake }, new[] { Level }, east, Depth);
            int changed = 0;
            for (int z = 0; z < 257; z++)
            {
                Assert.That(west.heights[z, 256], Is.EqualTo(east.heights[z, 0]).Within(1e-4f), "row " + z);
                if (Mathf.Abs(west.heights[z, 256] - ground(512, west.Z(z))) > 0.01f) changed++;
            }
            Assert.That(changed, Is.GreaterThan(60), "the lake does reach the edge");
        }
    }
}
