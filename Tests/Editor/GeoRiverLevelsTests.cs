using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // The levels of rivers (GeoRiverLevels): never rising downstream, no step where OpenStreetMap's lines of one river meet, a
    // stream at its river's level where it flows into it (or short of it), a side channel at its river's level where it leaves it,
    // streams at a pond's level where they flow into it and out of it, a lake that a river flows through flat at the river's level
    // with no step at its inlet and the water beyond its dam, one it touches at the river's level where they meet, and the same
    // levels for two exports that overlap.
    public class GeoRiverLevelsTests
    {
        // falling 4 m over a kilometre to the east, with the noise of elevation data: over a metre up and down over hundreds of metres
        static float Ground(float x, float z) => 22.5f + 0.004f * (1000 - x) + 0.9f * Mathf.Sin(x / 83) + 0.4f * Mathf.Sin(x / 31 + z / 47);

        static PathBuild River(string id, float width, params float[] xz)
        {
            var controls = Enumerable.Range(0, xz.Length / 2).Select(i => new Vector3(xz[2 * i], 0, xz[2 * i + 1])).ToList();
            var samples = GeoData.Resample(controls, 2);
            for (int i = 0; i < samples.Count; i++) { var p = samples[i]; p.y = Ground(p.x, p.z) - 0.35f; samples[i] = p; }
            GeoRivers.SmoothLevels(samples, 2, 40);
            return new PathBuild { feature = new LineFeature { id = id, type = "river", width = width }, river = true, controls = controls, samples = samples };
        }

        // the line's data set by its x (a valley falling evenly to the east, without the noise of Ground)
        static PathBuild Data(PathBuild path, System.Func<float, float> height)
        {
            for (int i = 0; i < path.samples.Count; i++) { var p = path.samples[i]; p.y = height(p.x); path.samples[i] = p; }
            return path;
        }

        static AreaFeature Lake(string id, float cx, float cz, float rx, float rz)
        {
            var ring = new List<float>();
            for (int i = 0; i < 72; i++) { float a = i * Mathf.PI * 2 / 72; ring.Add(cx + Mathf.Cos(a) * rx); ring.Add(cz + Mathf.Sin(a) * rz); }
            return new AreaFeature { id = id, type = "lake", material = "water", rings = new[] { ring.ToArray() } };
        }

        static void AssertFalling(PathBuild river, string what)
        {
            for (int i = 1; i < river.samples.Count; i++)
                Assert.That(river.samples[i].y, Is.LessThanOrEqualTo(river.samples[i - 1].y + 1e-4f), what + " rises at sample " + i);
        }

        static float LevelNear(PathBuild river, float x, float z) =>
            river.samples.OrderBy(p => (new Vector2(p.x, p.z) - new Vector2(x, z)).sqrMagnitude).First().y;

        // The noise of the data made the water rise and fall by over a metre along a stream: it now only falls, never stands above
        // the data (a bump is cut through, not filled: filling raised streams over their banks), and has no step.
        [Test] public void Level_NeverRisesDownstream_NorStandsAboveTheData()
        {
            var river = River("stream/1", 6, 0, 500, 1000, 500);
            var raw = river.samples.Select(p => p.y).ToList();
            Assert.That(raw.Zip(raw.Skip(1), (a, b) => Mathf.Max(0, b - a)).Sum(), Is.GreaterThan(1), "the data rises somewhere");
            var levels = new GeoRiverLevels();
            levels.Level(new List<PathBuild> { river }, new List<AreaFeature>(), new List<float>(), 2);
            AssertFalling(river, "the stream");
            // the smoothing lifts the bottom of a dip a little, nothing more
            for (int i = 0; i < raw.Count; i++)
                Assert.That(river.samples[i].y, Is.LessThan(raw[i] + 0.3f), "not above the data at sample " + i);
            float mean = river.samples.Select((p, i) => Mathf.Abs(p.y - raw[i])).Average();
            Assert.That(mean, Is.LessThan(1.2f), "near the data (mean difference " + mean + ")");
            // 2 m apart: a fall of 10 cm is a slope of 5 %, not a step
            for (int i = 1; i < river.samples.Count; i++)
                Assert.That(river.samples[i - 1].y - river.samples[i].y, Is.LessThan(0.1f), "no step at sample " + i);
            Assert.That(levels.Chains, Is.EqualTo(1));
        }

        // A wide river is the nearest profile that never rises: a dip in the data is filled by MaxFill at most and a bump is cut,
        // so it lies in neither a trench (the lowest level so far) nor over its banks, and has no step.
        [Test] public void WideRiver_StandsAMetreAboveTheDataAtMost()
        {
            var river = River("river/1", 40, 0, 500, 1000, 500);
            var raw = river.samples.Select(p => p.y).ToList();
            var levels = new GeoRiverLevels();
            levels.Level(new List<PathBuild> { river }, new List<AreaFeature>(), new List<float>(), 2);
            AssertFalling(river, "the river");
            for (int i = 0; i < raw.Count; i++)
                Assert.That(river.samples[i].y, Is.LessThan(raw[i] + GeoRiverLevels.MaxFill + 0.3f), "a metre above the data at most at sample " + i);
            float mean = river.samples.Select((p, i) => Mathf.Abs(p.y - raw[i])).Average();
            Assert.That(mean, Is.LessThan(0.8f), "near the data (mean difference " + mean + ")");
            for (int i = 1; i < river.samples.Count; i++)
                Assert.That(river.samples[i - 1].y - river.samples[i].y, Is.LessThan(0.1f), "no step at sample " + i);
        }

        // OpenStreetMap draws a long river as several lines: each was smoothed on its own and the level changed where one ended.
        [Test] public void LinesOfOneRiver_MeetWithoutAStep()
        {
            var upper = River("river/upper", 20, 0, 500, 480, 503);
            var lower = River("river/lower", 20, 481, 503, 1000, 500);
            var levels = new GeoRiverLevels();
            levels.Level(new List<PathBuild> { lower, upper }, new List<AreaFeature>(), new List<float>(), 2);
            Assert.That(levels.Chains, Is.EqualTo(1), "one chain");
            AssertFalling(upper, "the upper line"); AssertFalling(lower, "the lower line");
            float end = upper.samples[upper.samples.Count - 1].y, start = lower.samples[0].y;
            Assert.That(Mathf.Abs(end - start), Is.LessThan(0.06f), "they meet at one level");
        }

        // A stream that flows into a river met it at the stream's own height, a metre or two off.
        [Test] public void Stream_ComesToItsRiversLevel_WhereItFlowsIn()
        {
            var river = River("river/main", 40, 0, 500, 1000, 500);
            // a stream from the north whose data at its mouth is above the river's
            var stream = River("stream/1", 4, 600, 900, 560, 700, 520, 520);
            for (int i = 0; i < stream.samples.Count; i++) { var p = stream.samples[i]; p.y += 2; stream.samples[i] = p; }
            var levels = new GeoRiverLevels();
            levels.Level(new List<PathBuild> { river, stream }, new List<AreaFeature>(), new List<float>(), 2);
            AssertFalling(river, "the river"); AssertFalling(stream, "the stream");
            var mouth = stream.samples[stream.samples.Count - 1];
            Assert.That(mouth.y, Is.EqualTo(LevelNear(river, mouth.x, mouth.z)).Within(0.03f), "the stream ends at the river's level");
            Assert.That(levels.Tributaries, Is.EqualTo(1));
        }

        // A stream whose data at its mouth is a little below the river is backed up to the river's level. One far below it was
        // backed up by a metre and met the river with a step (more built a dyke along it: the banks are shaped from the water); it
        // stops short of the river now, with ground between them.
        [Test] public void LowStream_IsBackedUp_OrStopsShortOfTheRiver()
        {
            var river = River("river/main", 40, 0, 500, 1000, 500);
            // the river's levels first, to know how far below it to put the streams' mouths
            var probe = River("river/main", 40, 0, 500, 1000, 500);
            new GeoRiverLevels().Level(new List<PathBuild> { probe }, new List<AreaFeature>(), new List<float>(), 2);
            // streams from the south whose data rises by 1 % away from their mouths at the river's bank
            float nearMouth = LevelNear(probe, 350, 500) - 0.6f, farMouth = LevelNear(probe, 650, 500) - 3f;
            var near = River("stream/near", 4, 350, 100, 350, 480);
            var far = River("stream/far", 4, 650, 100, 650, 480);
            for (int i = 0; i < near.samples.Count; i++) { var p = near.samples[i]; p.y = nearMouth + 0.01f * (480 - p.z); near.samples[i] = p; }
            for (int i = 0; i < far.samples.Count; i++) { var p = far.samples[i]; p.y = farMouth + 0.01f * (480 - p.z); far.samples[i] = p; }
            var farData = far.samples.Select(p => p.y).ToList();
            var paths = new List<PathBuild> { river, near, far };
            var levels = new GeoRiverLevels();
            levels.Level(paths, new List<AreaFeature>(), new List<float>(), 2);
            AssertFalling(near, "the near stream");
            var mouth = near.samples[near.samples.Count - 1];
            Assert.That(mouth.y, Is.EqualTo(LevelNear(river, mouth.x, mouth.z)).Within(0.03f), "backed up to the river");
            var runs = paths.Where(p => p.feature.id == "stream/far").ToList();
            Assert.That(runs.Count, Is.EqualTo(1), "the far stream, short of the river");
            var end = runs[0].samples[runs[0].samples.Count - 1];
            Assert.That(500 - end.z, Is.GreaterThanOrEqualTo(20 + 2 + GeoRiverLevels.Separation - 0.5f), "ground between it and the river");
            for (int i = 0; i < runs[0].samples.Count; i++)
                Assert.That(runs[0].samples[i].y, Is.LessThan(farData[i] + 0.3f), "not backed up over its data (the median's window is short at the end) at sample " + i);
            Assert.That(levels.Short, Is.EqualTo(1));
        }

        // A side channel that leaves the river and comes back to it: at the river's level where it leaves it and where it meets it
        // again, though its data is higher (it is kept from rising above the river) or lower (it comes down from it).
        [TestCase(1.5f)] [TestCase(-0.5f)]
        public void SideChannel_LeavesTheRiverAtItsLevel(float offset)
        {
            var river = Data(River("river/main", 40, 0, 500, 1000, 500), x => 30 - 0.002f * x);
            var channel = Data(River("river/arm", 8, 300, 500, 380, 620, 620, 620, 700, 500), x => 30 - 0.002f * x + offset);
            new GeoRiverLevels().Level(new List<PathBuild> { river, channel }, new List<AreaFeature>(), new List<float>(), 2);
            AssertFalling(channel, "the channel");
            var head = channel.samples[0]; var end = channel.samples[channel.samples.Count - 1];
            Assert.That(head.y, Is.EqualTo(LevelNear(river, head.x, head.z)).Within(0.03f), "at the river's level where it leaves it");
            Assert.That(end.y, Is.EqualTo(LevelNear(river, end.x, end.z)).Within(0.03f), "and where it comes back");
        }

        // A pond that a river flows through, a stream from the north that ends at its shore and one that leaves its southern shore:
        // the one in comes down to the pond's level, the one out is never higher than the pond.
        [Test] public void Streams_MeetAPondAtItsLevel()
        {
            var river = Data(River("river/main", 10, 0, 500, 1000, 500), x => 30 - 0.002f * x);
            var pond = Lake("lake/pond", 500, 500, 80, 50);
            var into = River("stream/in", 4, 500, 800, 500, 552);
            for (int i = 0; i < into.samples.Count; i++) { var p = into.samples[i]; p.y = 31.25f + 0.001f * (p.z - 552); into.samples[i] = p; }
            var outOf = River("stream/out", 4, 520, 448, 520, 100);
            for (int i = 0; i < outOf.samples.Count; i++) { var p = outOf.samples[i]; p.y = 29.5f - 0.002f * (448 - p.z); outOf.samples[i] = p; }
            var lakeLevels = new List<float> { 25 };
            new GeoRiverLevels().Level(new List<PathBuild> { river, into, outOf }, new List<AreaFeature> { pond }, lakeLevels, 2);
            AssertFalling(into, "the stream in"); AssertFalling(outOf, "the stream out");
            Assert.That(lakeLevels[0], Is.EqualTo(30 - 0.002f * 580).Within(0.1f), "the pond at the river's level at its outlet");
            Assert.That(into.samples[into.samples.Count - 1].y, Is.EqualTo(lakeLevels[0]).Within(0.03f), "the stream in ends at the pond's level");
            Assert.That(outOf.samples[0].y, Is.EqualTo(lakeLevels[0]).Within(0.03f), "the stream out starts at it");
        }

        // A lake that a river flows through: flat at the river's level inside, and the river falls into it and out of it.
        [Test] public void LakeOnTheRiver_IsFlat_AtTheRiversLevel()
        {
            var river = River("river/1", 20, 0, 500, 1000, 500);
            var lake = Lake("lake/on", 500, 500, 120, 60);
            var water = new List<AreaFeature> { lake };
            var lakeLevels = new List<float> { 40 }; // a shore level far off the river's
            new GeoRiverLevels().Level(new List<PathBuild> { river }, water, lakeLevels, 2);
            AssertFalling(river, "the river");
            var inside = river.samples.Where(p => GeoData.InPolygon(p.x, p.z, lake.rings)).ToList();
            Assert.That(inside.Count, Is.GreaterThan(50));
            Assert.That(inside.All(p => Mathf.Abs(p.y - lakeLevels[0]) < 1e-4f), "the river is at the lake's level inside it");
            Assert.That(lakeLevels[0], Is.LessThan(39), "the lake is at the river's level, not its shore's");
            Assert.That(lakeLevels[0], Is.LessThanOrEqualTo(LevelNear(river, 370, 500)), "the river falls into the lake");
            Assert.That(lakeLevels[0], Is.GreaterThanOrEqualTo(LevelNear(river, 630, 500)), "and out of it");
        }

        // An oxbow beside the river: it kept its shore's level, two metres over the river where they met.
        [Test] public void LakeTheRiverTouches_TakesTheRiversLevel()
        {
            var river = River("river/1", 20, 0, 500, 1000, 500);
            var oxbow = Lake("lake/oxbow", 400, 560, 80, 46); // its south shore 4 m from the river's bank
            var water = new List<AreaFeature> { oxbow };
            var lakeLevels = new List<float> { Ground(400, 560) + 2 };
            var shared = new Dictionary<string, float> { { "lake/oxbow", lakeLevels[0] } };
            var levels = new GeoRiverLevels();
            levels.Level(new List<PathBuild> { river }, water, lakeLevels, 2, shared);
            Assert.That(lakeLevels[0], Is.EqualTo(LevelNear(river, 400, 500)).Within(0.05f), "at the river's level where they meet");
            Assert.That(shared["lake/oxbow"], Is.EqualTo(lakeLevels[0]), "written back for the other exports");
            Assert.That(levels.LakesLevelled, Is.EqualTo(1));
        }

        // A lake whose shore runs along the river's bank (an oxbow, a backwater) is the river's water: at the river's level where
        // they meet, though its own is lower.
        [Test] public void LakeAtTheRiversBank_IsTheRiversWater()
        {
            var river = River("river/1", 20, 0, 500, 1000, 500);
            var lake = Lake("lake/bank", 600, 444, 80, 46); // its north shore on the river's bank (z = 490)
            var probe = River("river/1", 20, 0, 500, 1000, 500);
            new GeoRiverLevels().Level(new List<PathBuild> { probe }, new List<AreaFeature>(), new List<float>(), 2);
            var lakeLevels = new List<float> { LevelNear(probe, 600, 500) - 1.5f };
            new GeoRiverLevels().Level(new List<PathBuild> { river }, new List<AreaFeature> { lake }, lakeLevels, 2);
            Assert.That(lakeLevels[0], Is.EqualTo(LevelNear(river, 600, 500)).Within(0.05f));
        }

        // An oxbow below the river with ground between them: raised towards it by MaxLakeRise at most (its shore lies just above
        // its own level, and a higher lake would ring itself with a dyke).
        [Test] public void LakeBelowTheRiver_IsRaisedALittleAtMost()
        {
            var river = River("river/1", 20, 0, 500, 1000, 500);
            var oxbow = Lake("lake/low", 600, 438, 80, 46); // its north shore 6 m from the river's bank
            var water = new List<AreaFeature> { oxbow };
            var probe = River("river/1", 20, 0, 500, 1000, 500);
            new GeoRiverLevels().Level(new List<PathBuild> { probe }, new List<AreaFeature>(), new List<float>(), 2);
            float own = LevelNear(probe, 600, 500) - 2;
            var lakeLevels = new List<float> { own };
            new GeoRiverLevels().Level(new List<PathBuild> { river }, water, lakeLevels, 2);
            Assert.That(lakeLevels[0], Is.EqualTo(own + GeoRiverLevels.MaxLakeRise).Within(0.01f));
        }

        // Two exports of a world with the same river in their overlap: the second keeps the first's levels there, so the tiles
        // where they meet have one water.
        [Test] public void SecondExport_KeepsTheFirstsLevels_WhereTheyOverlap()
        {
            var shared = new GeoRiverLevels();
            var first = River("river/1", 20, 0, 500, 640, 500);
            shared.Level(new List<PathBuild> { first }, new List<AreaFeature>(), new List<float>(), 2);
            // the second export's line: clipped elsewhere and moved a metre by its ground map, with noisier data
            var second = River("river/1", 20, 500, 501, 1000, 501);
            for (int i = 0; i < second.samples.Count; i++) { var p = second.samples[i]; p.y += 0.3f * Mathf.Sin(i * 0.37f); second.samples[i] = p; }
            shared.Level(new List<PathBuild> { second }, new List<AreaFeature>(), new List<float>(), 2);
            AssertFalling(second, "the second export's river");
            foreach (var p in second.samples.Where(p => p.x > 505 && p.x < 635))
                Assert.That(p.y, Is.EqualTo(LevelNear(first, p.x, p.z)).Within(0.02f), "the first export's level at x = " + p.x);
        }

        // A line that meets nothing and climbs along its points is drawn against its flow: it is levelled the other way round.
        [Test] public void LineDrawnUpstream_FallsTheOtherWay()
        {
            var river = River("river/backwards", 10, 1000, 300, 0, 300); // drawn from east to west, up the valley
            new GeoRiverLevels().Level(new List<PathBuild> { river }, new List<AreaFeature>(), new List<float>(), 2);
            for (int i = 1; i < river.samples.Count; i++)
                Assert.That(river.samples[i].y, Is.GreaterThanOrEqualTo(river.samples[i - 1].y - 1e-4f), "never falling along its points at " + i);
            Assert.That(river.samples[river.samples.Count - 1].y - river.samples[0].y, Is.GreaterThan(2), "it still falls down the valley");
        }

        // A stream through a lake whose data falls three metres across it: the stream comes down to the lake's level before it,
        // never more steeply than MaxSlope, where it fell into the lake with a step.
        [Test] public void StreamIntoALake_HasNoStepAtTheInlet()
        {
            var stream = Data(River("stream/1", 12, 0, 500, 1000, 500), x => x < 400 ? 30 - 0.0005f * x : x < 600 ? 29.8f - 3 * (x - 400) / 200 : 26.8f - 0.0005f * (x - 600));
            var lake = Lake("lake/long", 500, 500, 100, 40);
            var lakeLevels = new List<float> { 25 };
            new GeoRiverLevels().Level(new List<PathBuild> { stream }, new List<AreaFeature> { lake }, lakeLevels, 2);
            AssertFalling(stream, "the stream");
            foreach (var p in stream.samples.Where(p => GeoData.InPolygon(p.x, p.z, lake.rings)))
                Assert.That(p.y, Is.EqualTo(lakeLevels[0]).Within(1e-4f), "at the lake's level inside it");
            for (int i = 1; i < stream.samples.Count; i++)
            {
                float along = new Vector2(stream.samples[i].x - stream.samples[i - 1].x, stream.samples[i].z - stream.samples[i - 1].z).magnitude;
                Assert.That(stream.samples[i - 1].y - stream.samples[i].y, Is.LessThanOrEqualTo(GeoRiverLevels.MaxSlope * along + 1e-3f), "no step at sample " + i);
            }
        }

        // A pond held by a dam (the stream's line crosses its crest, 3 m over the pond in the data) with the stream 3 m below the
        // pond beyond it: the water starts again Separation beyond the pond, as below the dam's pipe, where it met the pond's
        // shore with a wall of water.
        [Test] public void Dam_TheStreamStartsBeyondIt()
        {
            var stream = Data(River("stream/1", 6, 0, 500, 1000, 500), x => x < 400 ? 29 + 0.001f * (400 - x) : x < 600 ? 29 : x < 612 ? 32 : 26 - 0.001f * (x - 612));
            var pond = Lake("lake/pond", 500, 500, 100, 40);
            var paths = new List<PathBuild> { stream };
            var lakeLevels = new List<float> { 25 };
            var levels = new GeoRiverLevels();
            levels.Level(paths, new List<AreaFeature> { pond }, lakeLevels, 2);
            Assert.That(levels.Dams, Is.EqualTo(1));
            Assert.That(lakeLevels[0], Is.EqualTo(29).Within(0.2f), "the pond at its level");
            var below = paths.Where(p => p.samples[0].x > 550).ToList();
            Assert.That(below.Count, Is.EqualTo(1), "the stream below the dam");
            Assert.That(below[0].samples[0].x, Is.GreaterThanOrEqualTo(600 + 3 + GeoRiverLevels.Separation - 0.5f), "ground between the pond and the water below it");
            Assert.That(lakeLevels[0] - below[0].samples[0].y, Is.GreaterThan(GeoRiverLevels.OutletDrop), "below the dam");
        }

        // An embankment across a stream, 6 m high and 100 m wide in the data: the water stops at it and goes on beyond (as under a
        // culvert or a bridge) instead of a gorge cut through it.
        [Test] public void WideBump_LeavesTheStreamDry_NotAGorge()
        {
            var river = River("stream/bank", 6, 0, 500, 1000, 500);
            for (int i = 0; i < river.samples.Count; i++) { var p = river.samples[i]; if (p.x > 450 && p.x < 550) p.y += 6; river.samples[i] = p; }
            var paths = new List<PathBuild> { river };
            var levels = new GeoRiverLevels();
            levels.Level(paths, new List<AreaFeature>(), new List<float>(), 2);
            Assert.That(paths.Count, Is.EqualTo(2), "two stretches of water");
            Assert.That(paths.SelectMany(p => p.samples).Any(p => p.x > 470 && p.x < 530), Is.False, "no water on the embankment");
            Assert.That(levels.Gaps, Is.EqualTo(1));
            foreach (var p in paths) AssertFalling(p, "each stretch");
        }
    }
}
