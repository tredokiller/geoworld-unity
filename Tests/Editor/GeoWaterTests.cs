using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // The water as one surface (GeoWater): never drawn twice (two surfaces at about one height flicker, and one shows through
    // the other), a river's area follows its river instead of lying flat, a river through a lake neither raises the lake's bed
    // nor meets it at another height, a stream narrower than a cell is still a line of water, and two tiles meet exactly.
    public class GeoWaterTests
    {
        readonly List<Object> owned = new List<Object>();
        [TearDown] public void Cleanup() { foreach (var o in owned) if (o) Object.DestroyImmediate(o); owned.Clear(); }

        const float Depth = 1.5f;
        static readonly GeoBounds Box = new GeoBounds { minX = 0, minZ = 0, maxX = 1000, maxZ = 1000 };

        // gently falling to the east, with the noise of elevation data
        static float Ground(float x, float z) => 22.5f + 0.004f * (1000 - x) + 0.6f * Mathf.Sin(x / 37) * Mathf.Cos(z / 29);

        static HeightField Field(Func<float, float, float> ground, GeoBounds bounds = null, int resolution = 513)
        {
            var f = new HeightField(bounds ?? Box, resolution);
            for (int z = 0; z < resolution; z++) for (int x = 0; x < resolution; x++) f.heights[z, x] = ground(f.X(x), f.Z(z));
            return f;
        }

        static PathBuild River(List<Vector3> controls, Func<float, float, float> ground, float width, bool fitted = true)
        {
            var samples = GeoData.Resample(controls, 2);
            for (int i = 0; i < samples.Count; i++) { var p = samples[i]; p.y = ground(p.x, p.z) - 0.35f; samples[i] = p; }
            GeoRivers.SmoothLevels(samples, 2, 40);
            return new PathBuild { feature = new LineFeature { id = "river/" + controls.Count, type = "river", width = width }, river = true, controls = controls, samples = samples,
                widths = fitted ? samples.Select(p => width).ToList() : null };
        }

        static List<Vector3> Line(params float[] xz) => Enumerable.Range(0, xz.Length / 2).Select(i => new Vector3(xz[2 * i], 0, xz[2 * i + 1])).ToList();

        // a polygon `half` metres to each side of a line
        static float[] Buffer(List<Vector3> line, float half)
        {
            var left = new List<Vector3>(); var right = new List<Vector3>();
            for (int i = 0; i < line.Count; i++) { var side = GeoMeshes.Side(line, i); left.Add(line[i] + side * half); right.Add(line[i] - side * half); }
            right.Reverse();
            return left.Concat(right).SelectMany(p => new[] { p.x, p.z }).ToArray();
        }

        // How many upward triangles of the meshes cover each point of a 1 m grid (offset, so no point lies on a grid line), with
        // the highest water there.
        static int[,] Coverage(IEnumerable<Mesh> meshes, out float[,] top)
        {
            var count = new int[1000, 1000]; top = new float[1000, 1000];
            foreach (var mesh in meshes)
            {
                var v = mesh.vertices; var t = mesh.triangles;
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    Vector3 a = v[t[k]], b = v[t[k + 1]], c = v[t[k + 2]];
                    if (Vector3.Cross(b - a, c - a).y <= 0) continue;
                    int i0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)))), i1 = Mathf.Min(999, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                    int j0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)))), j1 = Mathf.Min(999, Mathf.CeilToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z))));
                    float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    for (int j = j0; j <= j1; j++)
                        for (int i = i0; i <= i1; i++)
                        {
                            float x = i + 0.5123f, z = j + 0.5371f;
                            float u = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d, w = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
                            if (u < 0 || w < 0 || u + w > 1) continue;
                            float y = u * a.y + w * b.y + (1 - u - w) * c.y;
                            top[j, i] = count[j, i]++ == 0 ? y : Mathf.Max(top[j, i], y);
                        }
                }
            }
            return count;
        }

        Mesh Own(Mesh mesh) { owned.Add(mesh); return mesh; }

        [Test] public void Flowing_IsTheWaterOfARiver_NotALake()
        {
            foreach (var kind in new[] { "river", "riverbank", "canal", "Stream" })
                Assert.IsTrue(GeoWater.Flowing(new AreaFeature { type = kind }), kind);
            foreach (var kind in new[] { "lake", "pond", "reservoir", "water", "oxbow", "", null })
                Assert.IsFalse(GeoWater.Flowing(new AreaFeature { type = kind }), kind ?? "null");
            Assert.IsFalse(GeoWater.Flowing(null));
        }

        // A wide river on a line with sharp corners, and a tributary meeting it 25 cm higher: as ribbons, the inner side of every
        // corner folded over itself and the two ribbons lay over each other. Here no point has two waters.
        [Test] public void Water_IsNeverDrawnTwice_AtBendsOrWhereRiversMeet()
        {
            var field = Field(Ground);
            var bends = River(Line(20, 300, 200, 360, 330, 220, 480, 400, 600, 230, 760, 360, 980, 270), Ground, 60);
            var main = River(Line(20, 750, 500, 780, 980, 760), Ground, 40);
            var tributary = River(Line(420, 990, 470, 900, 520, 781), (x, z) => Ground(x, z) + 0.25f, 24);
            var water = new GeoWater(field, null, null, new List<PathBuild> { bends, main, tributary }, Depth);
            water.Carve();
            var surface = Own(water.Surface(Box));
            var count = Coverage(new[] { surface }, out var top);
            int wet = 0, twice = 0, buried = 0;
            for (int j = 0; j < 1000; j++)
                for (int i = 0; i < 1000; i++)
                {
                    if (count[j, i] > 1) twice++;
                    if (count[j, i] == 0) continue;
                    wet++;
                    // along the lines, the water is over its bed
                    if (Near(bends, i, j) < 25 || Near(main, i, j) < 15) { if (top[j, i] < field.Sample(i + 0.5123f, j + 0.5371f) + 0.5f) buried++; }
                }
            Assert.That(wet, Is.GreaterThan(90000), "the rivers have water");
            Assert.That(twice, Is.Zero, "no point is covered twice");
            Assert.That(buried, Is.Zero, "the water is at least half a metre over the bed along the rivers");
        }

        // OpenStreetMap's area of a river 130 m wide and its line (14 m: wider than the ground map's reach of a river): the area was
        // a flat lake at one level with the line's ribbon crossing it. Now the area is the river's water at the river's level.
        [Test] public void RiverArea_FollowsItsRiver_AndIsNotALake()
        {
            var field = Field(Ground);
            var line = new List<Vector3>();
            for (int i = 0; i <= 40; i++) { float x = -50 + i * 27.5f; line.Add(new Vector3(x, 0, 500 + 120 * Mathf.Sin(x / 160))); }
            var river = River(line, Ground, 14, fitted: false);
            var area = new AreaFeature { id = "area/river", type = "river", material = "water", rings = new[] { Buffer(line, 65) } };
            var levels = new[] { 20f }; // a flat level, as the lake it used to be: far from the river's
            var water = new GeoWater(field, new[] { area }, levels, new List<PathBuild> { river }, Depth);
            water.Carve();
            var surface = Own(water.Surface(Box));
            var count = Coverage(new[] { surface }, out var top);
            int inside = 0, missing = 0, twice = 0;
            for (int j = 0; j < 1000; j += 2)
                for (int i = 0; i < 1000; i += 2)
                {
                    float x = i + 0.5123f, z = j + 0.5371f;
                    if (count[j, i] > 1) twice++;
                    if (!GeoData.InPolygon(x, z, area.rings) || OnRing(area, x, z) < 3) continue;
                    inside++;
                    if (count[j, i] == 0 || top[j, i] <= field.Sample(x, z) + 0.01f) missing++;
                }
            Assert.That(twice, Is.Zero);
            Assert.That(inside, Is.GreaterThan(30000));
            Assert.That(missing, Is.LessThan(inside / 200), "the whole area is water over its bed (" + missing + " of " + inside + " not)");
            // the level follows the river (it falls about 4 m from west to east), not the area's flat level
            float west = surface.vertices.Where(v => v.x < 100).Average(v => v.y), east = surface.vertices.Where(v => v.x > 900).Average(v => v.y);
            Assert.That(west - east, Is.GreaterThan(2.5f));
            Assert.That(surface.vertices.Min(v => v.y), Is.GreaterThan(levels[0] + 0.5f), "not at the flat level of the area");
        }

        // A pond on a stream: the stream's line used to raise the pond's bed beside it to the stream's water, a ridge of land
        // through the pond, and its ribbon met the pond at another height.
        [Test] public void RiverThroughALake_TakesItsLevel_AndDoesNotRaiseItsBed()
        {
            var field = Field(Ground);
            var ring = new List<float>();
            for (int i = 0; i < 90; i++) { float a = i * Mathf.PI * 2 / 90; ring.Add(500 + Mathf.Cos(a) * 110); ring.Add(500 + Mathf.Sin(a) * 70); }
            var pond = new AreaFeature { id = "pond/1", type = "pond", material = "water", rings = new[] { ring.ToArray() } };
            var levels = new List<float> { GeoData.Points(ring.ToArray()).Select(p => Ground(p.x, p.z)).OrderBy(v => v).ElementAt(22) - 0.3f };
            var stream = River(Line(20, 520, 300, 505, 700, 490, 980, 470), Ground, 8);
            var paths = new List<PathBuild> { stream };
            GeoWater.SettleRivers(paths, new[] { pond }, levels);
            foreach (var p in stream.samples.Where(p => GeoData.InPolygon(p.x, p.z, pond.rings)))
                Assert.That(p.y, Is.EqualTo(levels[0]), "the stream is at the pond's level inside it");
            var water = new GeoWater(field, new[] { pond }, levels, paths, Depth);
            water.Carve();
            // inside the pond, away from its shore, the bed is under the water everywhere: no ridge along the stream
            float highest = float.MinValue;
            for (int z = 0; z < field.resolution; z++)
                for (int x = 0; x < field.resolution; x++)
                {
                    float px = field.X(x), pz = field.Z(z);
                    if (GeoData.InPolygon(px, pz, pond.rings) && OnRing(pond, px, pz) > GeoShore.Bank(Depth)) highest = Mathf.Max(highest, field.heights[z, x]);
                }
            Assert.That(highest, Is.LessThan(levels[0] - Depth * 0.9f), "the pond's bed is at its depth");
            // the stream's water and the pond's do not lie over each other, and meet at one height
            var lake = Own(GeoLakes.Surface(new[] { pond }, levels[0], field, Box));
            var rivers = Own(water.Surface(Box));
            var count = Coverage(new[] { lake, rivers }, out _);
            int twice = 0; foreach (int c in count) if (c > 1) twice++;
            Assert.That(twice, Is.Zero);
            var shared = rivers.vertices.Where(v => lake.vertices.Any(w => (w - v).sqrMagnitude < 1e-4f)).ToList();
            Assert.That(shared.Count, Is.GreaterThan(0), "the stream's water reaches the pond's");
            Assert.That(shared.All(v => v.y == levels[0]), "where they meet both are at the pond's level");
        }

        // A ditch 2 m wide on a 2 m grid: its bed is a groove with water in it all along (it was mostly ground a few millimetres
        // over the water, which flickers and hides it).
        [Test] public void NarrowStream_IsALineOfWater()
        {
            var field = Field(Ground);
            var ditch = River(Line(20, 301, 400, 333, 980, 650), Ground, 2, fitted: false);
            var water = new GeoWater(field, null, null, new List<PathBuild> { ditch }, Depth);
            Assert.That(water.MinHalfWidth, Is.EqualTo(0.75f * 1000 / 512).Within(1e-4f));
            water.Carve();
            var surface = Own(water.Surface(Box));
            var count = Coverage(new[] { surface }, out var top);
            int along = 0, seen = 0;
            for (int k = 1; k < ditch.samples.Count - 1; k++)
            {
                var p = ditch.samples[k]; int i = Mathf.FloorToInt(p.x), j = Mathf.FloorToInt(p.z);
                along++;
                if (count[j, i] > 0 && top[j, i] > field.Sample(i + 0.5123f, j + 0.5371f) + 0.05f) seen++;
            }
            Assert.That(seen, Is.GreaterThan(along * 0.95f), seen + " of " + along + " points of the ditch show water");
        }

        // Two tiles of 1 km (fields with a 96 m margin on the shared grid) and an 800 m wide river area whose line runs 300 m east
        // of their edge, outside the west tile's field: on their common edge the ground and the water are the same.
        [Test] public void Tiles_MeetExactly_AlsoWhereARiversLineIsOutsideOne()
        {
            Func<float, float, float> ground = (x, z) => 25 + 0.003f * z + 0.4f * Mathf.Sin(x / 41) * Mathf.Cos(z / 33);
            var line = new List<Vector3>();
            for (int i = 0; i <= 30; i++) { float z = -500 + i * 70; line.Add(new Vector3(1300 + 40 * Mathf.Sin(z / 200), 0, z)); }
            var river = River(line, ground, 14, fitted: false);
            var area = new[] { new AreaFeature { id = "area/wide", type = "river", material = "water", rings = new[] { Buffer(line, 400) } } };
            var levels = new[] { 26f };
            float cell = 1000f / 512; int margin = Mathf.CeilToInt(GeoTiles.MarginMetres / cell), n = 513 + 2 * margin;
            HeightField Tile(float minX, out Mesh surface)
            {
                var around = new GeoBounds { minX = minX - margin * cell, minZ = -margin * cell, maxX = minX + (512 + margin) * cell, maxZ = (512 + margin) * cell };
                var field = new HeightField(around, n);
                for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) field.heights[z, x] = ground(minX + (x - margin) * cell, (z - margin) * cell);
                var water = new GeoWater(field, area, levels, new List<PathBuild> { river }, Depth);
                water.Carve();
                surface = Own(water.Surface(new GeoBounds { minX = minX, minZ = 0, maxX = minX + 1000, maxZ = 1000 }));
                return field;
            }
            var west = Tile(0, out var westWater); var east = Tile(1000, out var eastWater);
            for (int z = margin; z < margin + 513; z++)
                Assert.That(west.heights[z, margin + 512], Is.EqualTo(east.heights[z, margin]).Within(1e-4f), "ground, row " + z);
            var westEdge = westWater.vertices.Where(v => Mathf.Abs(v.x - 1000) < 0.01f).ToDictionary(v => Mathf.RoundToInt(v.z / cell), v => v.y);
            var eastEdge = eastWater.vertices.Where(v => Mathf.Abs(v.x - 1000) < 0.01f).ToDictionary(v => Mathf.RoundToInt(v.z / cell), v => v.y);
            Assert.That(westEdge.Count, Is.EqualTo(513), "the water covers the whole edge");
            Assert.That(eastEdge.Keys, Is.EquivalentTo(westEdge.Keys));
            foreach (var pair in westEdge) Assert.That(eastEdge[pair.Key], Is.EqualTo(pair.Value).Within(1e-4f), "water at z " + pair.Key * cell);
            Assert.That(westEdge[512] - westEdge[0], Is.GreaterThan(2.5f), "the water follows the river's fall along the edge");
        }

        static float Near(PathBuild path, float x, float z)
        {
            float best = float.MaxValue;
            for (int i = 1; i < path.samples.Count; i++)
                best = Mathf.Min(best, GeoData.SegmentDistance(new Vector2(x, z), new Vector2(path.samples[i - 1].x, path.samples[i - 1].z), new Vector2(path.samples[i].x, path.samples[i].z), out _));
            return best;
        }

        static float OnRing(AreaFeature area, float x, float z)
        {
            var ring = GeoData.Points(area.rings[0]); float best = float.MaxValue;
            for (int i = 0; i < ring.Count; i++)
                best = Mathf.Min(best, GeoData.SegmentDistance(new Vector2(x, z), new Vector2(ring[i].x, ring[i].z), new Vector2(ring[(i + 1) % ring.Count].x, ring[(i + 1) % ring.Count].z), out _));
            return best;
        }
    }
}
