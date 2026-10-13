using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    public class GeoPhotoReferenceTests
    {
        GameObject root;
        readonly List<Object> owned=new List<Object>();
        [SetUp] public void Setup(){root=new GameObject("Photo reference test");}
        [TearDown] public void Cleanup()
        {
            if(root)Object.DestroyImmediate(root);
            foreach(var asset in owned)if(asset)Object.DestroyImmediate(asset);
            owned.Clear();
        }
        BuildingFeature Source()=>new BuildingFeature{id="building/1",category="house",height=9,levels=3,width=8,length=12,
            footprint=new float[]{-4,-6,4,-6,4,6,-4,6}};
        GeoBuilding Create(BuildingFeature source,GeoBuilding edited=null)=>
            GeoBuildingBuilder.Create(source,root.transform,p=>0,owned.Add,new List<string>(),edited);
        static void AssertColor(Color actual,Color expected)
        {
            // Material color storage can introduce a small floating-point round trip.
            Assert.That(actual.r,Is.EqualTo(expected.r).Within(0.00001f));
            Assert.That(actual.g,Is.EqualTo(expected.g).Within(0.00001f));
            Assert.That(actual.b,Is.EqualTo(expected.b).Within(0.00001f));
            Assert.That(actual.a,Is.EqualTo(expected.a).Within(0.00001f));
        }
        GeoBuildingVisualDetails Details()=>new GeoBuildingVisualDetails{source="photo_reference",photoId="photo/1",observationId="review/1",
            sourceUrl="https://example.org/reference",capturedAt="2018-04-21",confidence="medium",description="Reviewed roof and facade colors.",
            wallColor="#bd884f",roofColor="#5c2b20",wallMaterial="brick",roofShape="hipped",levels=2,
            libraryId="lyman",windowStyle="tall rectangular",style="historic civic building",roofNote="Roof rise remains an estimate.",
            references=new List<GeoPhotoReference>{new GeoPhotoReference{photoId="photo/1",observationId="review/1",evidence="visible",
                attribution="Example contributor",license=new GeoReferenceLicense{name="CC BY-SA 4.0",url="https://creativecommons.org/licenses/by-sa/4.0/"}},
                new GeoPhotoReference{photoId="photo/2",observationId="review/entrance",description="Entrance detail."}}};

        [Test] public void LegacyAndUnknownVisualDetails_KeepStyleDefaults()
        {
            var legacy=Create(Source());
            Assert.That(legacy.roof,Is.EqualTo(legacy.style.defaultRoof));
            Assert.IsFalse(legacy.overrideWallColor);Assert.IsFalse(legacy.overrideRoofColor);
            Assert.IsNull(legacy.visualDetails);
            Assert.That(legacy.generatedGeometry.Find("Walls").GetComponent<MeshRenderer>().sharedMaterial,Is.SameAs(legacy.style.walls));
            var unknown=Source();unknown.visualDetails=new GeoBuildingVisualDetails{roofShape="unknown",wallColor="red",roofColor="#wrong"};
            var fallback=Create(unknown);
            Assert.That(fallback.roof,Is.EqualTo(legacy.roof));
            Assert.IsFalse(fallback.overrideWallColor);Assert.IsFalse(fallback.overrideRoofColor);
            Assert.That(fallback.height,Is.EqualTo(9));Assert.That(fallback.floors,Is.EqualTo(3));
        }

        [TestCase("flat",GeoRoofShape.Flat)]
        [TestCase("gabled",GeoRoofShape.Gabled)]
        [TestCase("hipped",GeoRoofShape.Hipped)]
        public void ReviewedAppearance_UsesSeparateMaterialsAndRetainsEvidence(string shape,GeoRoofShape expected)
        {
            var source=Source();source.visualDetails=Details();source.visualDetails.roofShape=shape;
            // Exercise the export contract rather than relying only on object initialization.
            source=JsonConvert.DeserializeObject<BuildingFeature>(JsonConvert.SerializeObject(source));
            var building=Create(source);var styleColor=building.style.walls.color;
            Assert.That(building.roof,Is.EqualTo(expected));
            Assert.That(building.height,Is.EqualTo(9));Assert.That(building.floors,Is.EqualTo(3),"Observed levels do not rewrite source dimensions.");
            Assert.That(building.visualDetails.levels,Is.EqualTo(2));
            Assert.That(building.visualDetails.observationId,Is.EqualTo("review/1"));
            Assert.That(building.visualDetails.capturedAt,Is.EqualTo("2018-04-21"));
            Assert.That(building.visualDetails.wallMaterial,Is.EqualTo("brick"));
            Assert.That(building.visualDetails.references.Count,Is.EqualTo(2));
            Assert.That(building.visualDetails.references[0].license.name,Is.EqualTo("CC BY-SA 4.0"));
            Assert.That(building.visualDetails.references[0].attribution,Is.EqualTo("Example contributor"));
            Assert.That(building.visualDetails.references[1].observationId,Is.EqualTo("review/entrance"));
            Assert.That(building.visualDetails.libraryId,Is.EqualTo("lyman"));Assert.That(building.visualDetails.windowStyle,Is.EqualTo("tall rectangular"));
            var walls=building.generatedGeometry.Find("Walls").GetComponent<MeshRenderer>().sharedMaterial;
            var roof=building.generatedGeometry.Find("Roof").GetComponent<MeshRenderer>().sharedMaterial;
            ColorUtility.TryParseHtmlString("#bd884f",out var expectedWall);ColorUtility.TryParseHtmlString("#5c2b20",out var expectedRoof);
            Assert.That(walls,Is.Not.SameAs(building.style.walls));Assert.That(roof,Is.Not.SameAs(building.style.roof));
            AssertColor(walls.color,expectedWall);AssertColor(roof.color,expectedRoof);
            Assert.That(building.style.walls.color,Is.EqualTo(styleColor),"Photo colors must not modify the shared style.");
            Assert.Contains(walls,owned);Assert.Contains(roof,owned);
        }

        [Test] public void RetainedBuildingEdits_TakePrecedenceOverPhotoDetails()
        {
            var source=Source();source.visualDetails=Details();var original=Create(source);
            original.roof=GeoRoofShape.Flat;original.height=12;original.wallColor=Color.blue;
            original.wallMaterial=new Material(original.style.walls){color=Color.green};owned.Add(original.wallMaterial);
            source.visualDetails=new GeoBuildingVisualDetails{photoId="replacement",roofShape="gabled",wallColor="#ff0000"};
            var retained=Create(source,original);
            Assert.That(retained.roof,Is.EqualTo(GeoRoofShape.Flat));Assert.That(retained.height,Is.EqualTo(12));
            Assert.That(retained.wallColor,Is.EqualTo(Color.blue));
            Assert.That(retained.generatedGeometry.Find("Walls").GetComponent<MeshRenderer>().sharedMaterial,Is.SameAs(original.wallMaterial));
            Assert.That(retained.visualDetails.photoId,Is.EqualTo("photo/1"));
        }

        [TestCase(false)] [TestCase(true)]
        public void HippedRoof_HasUpwardFacesAndPreservesFootprintArea(bool concave)
        {
            var source=Source();source.visualDetails=Details();
            if(concave)source.footprint=new float[]{0,0,8,0,8,3,3,3,3,8,0,8};
            var building=Create(source);var context=new GeoBuildingContext(building);
            using(var output=GeoBuildingGeometry.Build(building,0.5f))
            {
                var roof=output.parts.Single(p=>p.name=="Roof").mesh;var vertices=roof.vertices;var triangles=roof.triangles;float area=0;
                for(int i=0;i<triangles.Length;i+=3)
                {
                    var a=vertices[triangles[i]];var b=vertices[triangles[i+1]];var c=vertices[triangles[i+2]];
                    float projected=Vector3.Cross(b-a,c-a).y;Assert.That(projected,Is.GreaterThan(0));area+=projected*0.5f;
                    var center=(a+b+c)/3;Assert.IsTrue(GeoPolygon.Contains(building.footprint,new Vector2(center.x,center.z)));
                }
                Assert.That(area,Is.EqualTo(concave?39:96).Within(0.001));
                Assert.That(roof.bounds.min.y,Is.EqualTo(context.eaves).Within(0.001));
                Assert.That(roof.bounds.max.y,Is.LessThanOrEqualTo(building.height+0.001f));
                if(!concave)
                {
                    Assert.That(roof.bounds.max.y,Is.EqualTo(building.height).Within(0.001));
                    Assert.That(context.RoofHeight(new Vector2(0,6)),Is.EqualTo(context.eaves));
                    Assert.That(context.RoofHeight(new Vector2(4,0)),Is.EqualTo(context.eaves));
                }
            }
        }

        [Test] public void PhotoProps_KeepManualPositionOrientationAndProvenance()
        {
            var prop=JsonConvert.DeserializeObject<PropFeature>(@"{""id"":""photo-prop/1"",""type"":""bench"",""source"":""photo_reference"",
                ""placementSource"":""manual_photo"",""x"":11,""z"":12,""rotation"":135,""rotationSource"":""manual"",
                ""photoId"":""photo/1"",""observationId"":""review/2"",""sourceUrl"":""https://example.org/reference"",
                ""capturedAt"":null,""confidence"":""medium"",""description"":""Bench shape reviewed in photo."",
                ""evidence"":""visible"",""attribution"":""Example contributor"",""license"":{""name"":""CC BY-SA 4.0"",""url"":""https://creativecommons.org/licenses/by-sa/4.0/""}}");
            var env=new EnvironmentData{bounds=new GeoBounds{minX=0,minZ=0,maxX=80,maxZ=80}};env.props.Add(prop);
            var world=root.AddComponent<GeoWorldRoot>();world.settings=new GenerationSettings{vegetation=false};
            GeoPropBuilder.Build(world,env,new BuildingsData(),new HeightField(env.bounds,33),new List<string>(),owned.Add);
            var marker=root.GetComponentInChildren<GeoPlacedProp>();
            Assert.That(marker.transform.position,Is.EqualTo(new Vector3(11,0,12)));Assert.That(marker.transform.eulerAngles.y,Is.EqualTo(135).Within(0.001));
            Assert.That(marker.placementSource,Is.EqualTo("manual_photo"));Assert.That(marker.rotationSource,Is.EqualTo("manual"));
            Assert.That(marker.evidence,Is.EqualTo("visible"));Assert.That(marker.photoReference.photoId,Is.EqualTo("photo/1"));
            Assert.That(marker.photoReference.observationId,Is.EqualTo("review/2"));Assert.That(marker.photoReference.evidence,Is.EqualTo("visible"));
            Assert.That(marker.photoReference.license.name,Is.EqualTo("CC BY-SA 4.0"));Assert.That(marker.photoReference.attribution,Is.EqualTo("Example contributor"));
            Assert.That(marker.photoReference.source,Is.EqualTo("photo_reference"));Assert.That(marker.photoReference.capturedAt,Is.Null);
            Assert.That(marker.photoReference.confidence,Is.EqualTo("medium"));Assert.IsNotNull(root.transform.Find("Props and vegetation/Photo reference objects"));
        }
    }
}
