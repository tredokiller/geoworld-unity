using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    public class GeoRoadTextureTests
    {
        static float Grey(Color32 c) => (c.r + c.g + c.b) / 765f;

        [Test] public void RoadPicture_IsTheSameEveryTimeAndTilesAlongTheRoad()
        {
            GeoRoadTextures.Render(0.7f, 5, 64, 128, null, null, 0, 0, out var a, out var an);
            GeoRoadTextures.Render(0.7f, 5, 64, 128, null, null, 0, 0, out var b, out _);
            Assert.That(b, Is.EqualTo(a));
            // the last row continues into the first: as close to it as any two neighbouring rows are, on average
            float Row(int y0, int y1) { float d = 0; for (int x = 0; x < 64; x++) d += Mathf.Abs(Grey(a[y0 * 64 + x]) - Grey(a[y1 * 64 + x])); return d; }
            float seam = Row(127, 0), average = 0;
            for (int y = 0; y < 127; y++) average += Row(y, y + 1);
            average /= 127;
            Assert.That(seam, Is.LessThan(average * 3f + 0.2f), "No visible seam where the picture repeats.");
            Assert.That(an.All(n => n.a == 255));
        }

        [Test] public void WornRoads_AreDarkerInPlacesAndLighterThanNewOnesAtTheEdge()
        {
            GeoRoadTextures.Render(0.1f, 3, 64, 128, null, null, 0, 0, out var fresh, out _);
            GeoRoadTextures.Render(0.95f, 3, 64, 128, null, null, 0, 0, out var broken, out _);
            float Spread(Color32[] p) { var v = p.Select(Grey).ToArray(); float m = v.Average(); return v.Average(x => Mathf.Abs(x - m)); }
            Assert.That(Spread(broken), Is.GreaterThan(Spread(fresh)), "A broken road has more patches, cracks and holes.");
            // the verge: dust at the edges, asphalt in the middle
            float Edge(Color32[] p) { float s = 0; for (int y = 0; y < 128; y++) s += Grey(p[y * 64]) + Grey(p[y * 64 + 63]); return s / 256; }
            float Middle(Color32[] p) { float s = 0; for (int y = 0; y < 128; y++) s += Grey(p[y * 64 + 32]); return s / 128; }
            Assert.That(Edge(broken), Is.GreaterThan(Middle(broken)), "Dust lightens the edge of the asphalt.");
        }

        [Test] public void Variants_DependOnWearAndStayInRange()
        {
            Assert.That(GeoRoadTextures.VariantOf("a", 0.05f), Is.InRange(0, 1));
            Assert.That(GeoRoadTextures.VariantOf("a", 1f), Is.InRange(2, GeoRoadTextures.Variants - 1));
            for (int i = 0; i < 50; i++) Assert.That(GeoRoadTextures.VariantOf("road/" + i, 0.65f), Is.InRange(0, GeoRoadTextures.Variants - 1));
            Assert.That(GeoRoadTextures.VariantOf("a", 0), Is.EqualTo(GeoRoadTextures.VariantOf("a", 0.55f)), "Unknown wear is a middling guess.");
        }

        [Test] public void RoadUv_RunsAcrossAndAlongTheRoad()
        {
            var feature = new LineFeature { id = "r", width = 6 };
            var path = new PathBuild { feature = feature, samples = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(0, 0, 10), new Vector3(0, 0, 20) } };
            var draft = new MeshDraft();
            draft.vertices.AddRange(new[] { new Vector3(-3, 0, 5), new Vector3(3, 0, 5), new Vector3(0, 0, 15) });
            GeoMeshes.RoadUv(path, draft, 12, 0);
            Assert.That(draft.uv.Count, Is.EqualTo(3));
            Assert.That(Mathf.Abs(draft.uv[0].x - draft.uv[1].x), Is.EqualTo(1f).Within(0.001f), "The two edges are the full width apart.");
            Assert.That(draft.uv[2].x, Is.EqualTo(0.5f).Within(0.001f), "The centreline is the middle.");
            Assert.That(draft.uv[0].y, Is.EqualTo(5f / 12).Within(0.001f));
            Assert.That(draft.uv[2].y, Is.EqualTo(15f / 12).Within(0.001f));
        }

        [Test] public void JunctionUv_EachPieceTakesItsOwnRoadAndStaysOffTheVerge()
        {
            var along = new PathBuild { feature = new LineFeature { id = "main", width = 6 }, samples = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 0), new Vector3(0, 0, 20) } };
            var across = new PathBuild { feature = new LineFeature { id = "side", width = 4 }, samples = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 10), new Vector3(20, 0, 10) } };
            var draft = new MeshDraft();
            draft.vertices.AddRange(new[] { new Vector3(-2.9f, 0, 8), new Vector3(0, 0, 8), new Vector3(8, 0, 10.5f) });
            var owners = new System.Collections.Generic.List<PathBuild> { along, along, across };
            GeoMeshes.JunctionUv(draft, owners, 12, 0.1f, 0.9f);
            Assert.That(draft.uv.Count, Is.EqualTo(3));
            Assert.That(draft.uv[0].x, Is.EqualTo(0.9f).Within(0.001f), "Near the verge the picture is held at its asphalt.");
            Assert.That(draft.uv[1].x, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(draft.uv[2].x, Is.InRange(0.1f, 0.9f), "The side road is laid across its own width.");
            Assert.That(draft.uv[2].x, Is.Not.EqualTo(0.5f).Within(0.01f));
        }
    }
}
