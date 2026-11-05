using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // What the generator knows about the buildings around one: which "houses" are a yard's outbuildings, and how the
    // buildings two exports give a tile on their seam are made to stand apart.
    public class GeoHomeSiteTests
    {
        static BuildingFeature Box(string id, float x, float z, float length, float width, string category = "house", string categorySource = "guessed", string source = "microsoft")
        {
            float l = length / 2, w = width / 2;
            return new BuildingFeature
            {
                id = id, category = category, categorySource = categorySource, source = source, buildingTag = "yes",
                x = x, z = z, width = width, length = length, height = 4, rotation = 0,
                footprint = new[] { -l, -w, l, -w, l, w, -l, w }
            };
        }

        static GeoStreets StreetAlongX(float z) =>
            new GeoStreets(new[] { new LineFeature { type = "residential", kind = "road", points = new[] { -200f, z, 200f, z } } });

        [Test] public void AGuessedHouseBehindABiggerOneIsItsYardsOutbuilding()
        {
            var buildings = new List<BuildingFeature>
            {
                Box("ms/house", 0, 10, 10, 9),                // 90 m², 10 m from the street
                Box("ms/kitchen", 2, 26, 7, 6),               // 42 m², 26 m from it: behind the house
                Box("ms/next door", 22, 10, 9, 8),            // the neighbour's house beside it, as far from the street
                Box("osm/way/1", -3, 27, 7, 6, categorySource: "tag", source: "osm"),   // mapped as a house: stays one
                Box("ms/barn", 40, 30, 12, 9),                // 108 m² alone at the back: no bigger house near
            };
            var outbuildings = GeoHomeSites.Outbuildings(buildings, StreetAlongX(0));
            CollectionAssert.AreEquivalent(new[] { "ms/kitchen" }, outbuildings.ToArray());
            var site = GeoHomeSites.For(buildings, StreetAlongX(0), new GenerationSettings());
            Assert.IsTrue(site.IsOutbuilding("ms/kitchen"));
            Assert.IsFalse(GeoHomeSites.For(buildings, StreetAlongX(0), new GenerationSettings { yardOutbuildings = false }).IsOutbuilding("ms/kitchen"), "the setting turns it off");
            var drawn = GeoHomeSites.AsOutbuilding(buildings[1]);
            Assert.AreEqual("shed", drawn.category);
            Assert.AreEqual("house", buildings[1].category, "the export's building is not changed (tiles share it)");
        }

        [Test] public void WithoutAStreetASmallHouseBesideAMuchBiggerOneIsAnOutbuilding()
        {
            var buildings = new List<BuildingFeature> { Box("ms/house", 0, 0, 12, 9), Box("ms/shed", 14, 0, 7, 5) };
            CollectionAssert.AreEquivalent(new[] { "ms/shed" }, GeoHomeSites.Outbuildings(buildings, null).ToArray());
        }

        static float Overlap(BuildingFeature a, BuildingFeature b)
        {
            var outline = GeoHomeSites.Outline(b);
            var box = GeoRuinFit.FrameOf(GeoHomeSites.Outline(a));
            // a's outline as a box (the cut ones stay rectangles here)
            return GeoPolygon.AreaInside(outline, new GeoPolygon.Box(box.centre, box.axis, box.length / 2, box.width / 2));
        }

        [Test] public void TheSameHouseFromTwoExportsIsKeptOnceAndALightOverlapIsCut()
        {
            var mapped = Box("osm/way/5", 0, 0, 10, 8, categorySource: "tag", source: "osm");
            var twin = Box("ms/50.1,37.1", 0.8f, 0.4f, 10, 8);       // Microsoft's outline of the same house, from the other part
            var beside = Box("ms/50.2,37.2", 8.5f, 0, 8, 6);          // reaches 1.5 m into the house
            var apart = Box("ms/50.3,37.3", 30, 0, 8, 6);
            var result = GeoBuildingConflicts.Resolve(new List<BuildingFeature> { twin, beside, apart, mapped });
            Assert.AreEqual(1, result.dropped, "the twin under the mapped house");
            Assert.AreEqual(1, result.cut);
            var ids = result.buildings.Select(b => b.id).ToList();
            CollectionAssert.AreEquivalent(new[] { "osm/way/5", "ms/50.2,37.2", "ms/50.3,37.3" }, ids);
            var cut = result.buildings.First(b => b.id == "ms/50.2,37.2");
            Assert.AreNotSame(beside, cut, "a cut building is a copy");
            Assert.AreEqual(8, beside.footprint.Length, "the export's own is untouched");
            Assert.Less(Overlap(cut, mapped), 0.5f, "cut back along the house's wall");
            Assert.AreSame(apart, result.buildings.First(b => b.id == "ms/50.3,37.3"));
        }
    }
}
