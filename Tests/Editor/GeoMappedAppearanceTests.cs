using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    public class GeoMappedAppearanceTests
    {
        GameObject root;
        readonly List<Object> owned=new List<Object>();
        [SetUp] public void Setup(){root=new GameObject("Mapped appearance test");}
        [TearDown] public void Cleanup()
        {
            if(root)Object.DestroyImmediate(root);
            foreach(var asset in owned)if(asset)Object.DestroyImmediate(asset);
            owned.Clear();
        }
        BuildingFeature Source()=>new BuildingFeature{id="osm/way/1",category="house",height=9,levels=3,width=8,length=12,
            footprint=new float[]{-4,-6,4,-6,4,6,-4,6}};
        GeoBuilding Create(BuildingFeature source,GeoBuilding edited=null)=>
            GeoBuildingBuilder.Create(source,root.transform,p=>0,owned.Add,new List<string>(),edited);
        GeoMappedAppearance Mapped()=>new GeoMappedAppearance{source="osm",sourceUrl="https://www.openstreetmap.org/way/1",
            attribution="OpenStreetMap contributors",license=new GeoReferenceLicense{name="ODbL 1.0"},
            roofShape="hipped",roofHeight=4,roofColor="#336699",roofColorRaw="#369",roofMaterial="metal",
            wallColor="#aabbcc",wallMaterial="brick",buildingUse="residential",architecture="vernacular",startDate="1930",
            tags=new Dictionary<string,string>{{"roof:shape","hipped"},{"roof:colour","#369"}}};

        [Test] public void OlderExport_HasNoMappedMetadataAndKeepsDefaults()
        {
            var source=JsonConvert.DeserializeObject<BuildingFeature>(@"{""id"":""legacy"",""category"":""house"",""height"":9,""width"":8,""length"":12}");
            var building=Create(source);
            Assert.IsNull(source.mappedAppearance);Assert.IsNull(building.mappedAppearance);
            Assert.That(building.roof,Is.EqualTo(building.style.defaultRoof));Assert.That(building.roofDirection,Is.Zero);
            Assert.IsFalse(building.overrideRoofColor);Assert.IsFalse(building.overrideWallColor);
        }

        [Test] public void JsonAndUnitySerialization_PreserveNullableZeroAndRawEvidence()
        {
            var mapped=JsonConvert.DeserializeObject<GeoMappedAppearance>(@"{""source"":""osm"",""roofShape"":""onion"",""roofDirection"":0,
                ""roofDirectionMeaning"":""downslope_bearing"",""roofHeight"":0,""roofLevels"":0,""roofAngle"":0,""minHeight"":0,""minLevel"":0,
                ""tags"":{""roof:shape"":""onion"",""roof:direction"":""N""},""roofMaterial"":""copper"",""roofColorRaw"":""weathered copper""}");
            var restored=JsonUtility.FromJson<GeoMappedAppearance>(JsonUtility.ToJson(mapped));
            Assert.That(restored.roofDirection,Is.EqualTo(0));Assert.That(restored.roofHeight,Is.EqualTo(0));
            Assert.That(restored.roofLevels,Is.EqualTo(0));Assert.That(restored.roofAngle,Is.EqualTo(0));
            Assert.That(restored.minHeight,Is.EqualTo(0));Assert.That(restored.minLevel,Is.EqualTo(0));
            Assert.That(restored.tags["roof:direction"],Is.EqualTo("N"));Assert.That(restored.roofShape,Is.EqualTo("onion"));
            Assert.That(restored.roofMaterial,Is.EqualTo("copper"));Assert.That(restored.roofColorRaw,Is.EqualTo("weathered copper"));
            var absent=JsonUtility.FromJson<GeoMappedAppearance>(JsonUtility.ToJson(new GeoMappedAppearance()));
            Assert.IsNull(absent.roofDirection);Assert.IsNull(absent.roofHeight);Assert.IsNull(absent.minHeight);
            var json=JsonConvert.SerializeObject(restored);
            StringAssert.Contains("\"roofDirection\":0",json);StringAssert.DoesNotContain("mappedRoofDirection",json);
        }

        [TestCase("flat",GeoRoofShape.Flat)] [TestCase("gabled",GeoRoofShape.Gabled)] [TestCase("hipped",GeoRoofShape.Hipped)]
        public void MappedRoofAndColors_AreAppliedWithoutChangingSourceDimensions(string shape,GeoRoofShape expected)
        {
            var source=Source();source.mappedAppearance=Mapped();source.mappedAppearance.roofShape=shape;
            var building=Create(JsonConvert.DeserializeObject<BuildingFeature>(JsonConvert.SerializeObject(source)));
            Assert.That(building.roof,Is.EqualTo(expected));Assert.That(building.roofHeight,Is.EqualTo(4));
            Assert.That(building.height,Is.EqualTo(9));Assert.That(building.floors,Is.EqualTo(3));
            Assert.That(building.mappedAppearance.roofMaterial,Is.EqualTo("metal"));Assert.That(building.mappedAppearance.wallMaterial,Is.EqualTo("brick"));
            var roof=building.generatedGeometry.Find("Roof").GetComponent<MeshRenderer>().sharedMaterial;
            var walls=building.generatedGeometry.Find("Walls").GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(roof,Is.Not.SameAs(building.style.roof));Assert.That(walls,Is.Not.SameAs(building.style.walls));
            Assert.That(ColorUtility.ToHtmlStringRGB(roof.color),Is.EqualTo("336699"));
            Assert.That(ColorUtility.ToHtmlStringRGB(walls.color),Is.EqualTo("AABBCC"));
        }

        [Test] public void PartialPhotoReview_OverridesOnlyItsValidFields()
        {
            var source=Source();source.mappedAppearance=Mapped();
            source.visualDetails=new GeoBuildingVisualDetails{wallColor="#112233",levels=1};
            var building=Create(source);
            Assert.That(building.roof,Is.EqualTo(GeoRoofShape.Hipped));Assert.That(building.roofHeight,Is.EqualTo(4));
            Assert.That(ColorUtility.ToHtmlStringRGB(building.roofColor),Is.EqualTo("336699"));
            Assert.That(ColorUtility.ToHtmlStringRGB(building.wallColor),Is.EqualTo("112233"));
            Assert.IsTrue(building.overrideRoofColor);Assert.IsTrue(building.overrideWallColor);
            Assert.That(building.floors,Is.EqualTo(3));Assert.That(building.height,Is.EqualTo(9));
            source.visualDetails=new GeoBuildingVisualDetails{roofShape="flat",roofColor="#ffffff",wallColor="#bad-value"};
            var flat=Create(source);Assert.That(flat.roof,Is.EqualTo(GeoRoofShape.Flat));
            Assert.That(ColorUtility.ToHtmlStringRGB(flat.roofColor),Is.EqualTo("FFFFFF"));
            Assert.That(ColorUtility.ToHtmlStringRGB(flat.wallColor),Is.EqualTo("AABBCC"));
        }

        [TestCase(0,0)] [TestCase(0,37)] [TestCase(90,37)] [TestCase(135,270)]
        public void DownslopeBearing_ConvertsToTheActualWorldRoofAxis(float direction,float buildingRotation)
        {
            var source=Source();source.rotation=buildingRotation;source.mappedAppearance=Mapped();
            source.mappedAppearance.roofShape="gabled";source.mappedAppearance.roofDirection=direction;
            source.mappedAppearance.roofDirectionMeaning="downslope_bearing";source.mappedAppearance.roofOrientation="along";
            var building=Create(source);var context=new GeoBuildingContext(building);
            var axis=building.transform.TransformDirection(new Vector3(context.roofAxis.x,0,context.roofAxis.y));
            var expected=new Vector3(Mathf.Sin(direction*Mathf.Deg2Rad),0,Mathf.Cos(direction*Mathf.Deg2Rad));
            Assert.That(Mathf.Abs(Vector3.Dot(axis,expected)),Is.EqualTo(1).Within(0.00001));
            Assert.That(building.roofDirection,Is.InRange(0,179.99999f));
        }

        [TestCase("along",0)] [TestCase("across",90)]
        public void Orientation_IsUsedOnlyWithoutAnExplicitDownslopeBearing(string orientation,float expected)
        {
            var source=Source();source.mappedAppearance=Mapped();source.mappedAppearance.roofOrientation=orientation;
            var building=Create(source);Assert.That(building.roofDirection,Is.EqualTo(expected));
            source.mappedAppearance.roofDirection=0;source.mappedAppearance.roofDirectionMeaning="downslope_bearing";
            Assert.That(Create(source).roofDirection,Is.EqualTo(90));
        }

        [Test] public void UnsupportedShapes_StayAsEvidenceWithAnExplicitRendererNote()
        {
            var source=Source();source.mappedAppearance=Mapped();source.mappedAppearance.roofShape="onion";
            source.mappedAppearance.roofOrientation="across";
            var building=Create(source);
            Assert.That(building.mappedAppearance.roofShape,Is.EqualTo("onion"));Assert.That(building.roof,Is.EqualTo(building.style.defaultRoof));
            Assert.That(building.roofHeight,Is.EqualTo(building.style.defaultRoofHeight));Assert.That(building.roofDirection,Is.Zero);
            StringAssert.Contains("not supported",building.importNote);StringAssert.Contains("onion",building.importNote);
        }

        [Test] public void RoofHeight_IsClampedWithoutChangingTheMeasurementOrTotalHeight()
        {
            var source=Source();source.mappedAppearance=Mapped();source.mappedAppearance.roofHeight=20;
            var building=Create(source);Assert.That(building.roofHeight,Is.EqualTo(7));Assert.That(building.height,Is.EqualTo(9));
            Assert.That(building.mappedAppearance.roofHeight,Is.EqualTo(20));StringAssert.Contains("limited",building.importNote);
            source.mappedAppearance.roofHeight=0;var zero=Create(source);Assert.That(zero.roofHeight,Is.Zero);
        }

        [Test] public void EditedClone_PreservesUserSettingsAndOriginalMappedEvidence()
        {
            var source=Source();source.mappedAppearance=Mapped();source.mappedAppearance.roofDirection=0;
            var original=Create(source);original.height=14;original.roofDirection=17;original.roof=GeoRoofShape.Flat;
            original.roofMaterial=new Material(original.style.roof){color=Color.green};owned.Add(original.roofMaterial);
            source.mappedAppearance=new GeoMappedAppearance{source="replacement",roofShape="gabled",roofHeight=1,roofColor="#000000"};
            var retained=Create(source,original);
            Assert.That(retained.height,Is.EqualTo(14));Assert.That(retained.roof,Is.EqualTo(GeoRoofShape.Flat));
            Assert.That(retained.roofDirection,Is.EqualTo(17));Assert.That(retained.roofMaterial,Is.SameAs(original.roofMaterial));
            Assert.That(retained.mappedAppearance.source,Is.EqualTo("osm"));Assert.That(retained.mappedAppearance.roofDirection,Is.EqualTo(0));
            Assert.That(retained.mappedAppearance.tags["roof:shape"],Is.EqualTo("hipped"));
            Assert.That(retained.generatedGeometry.Find("Roof").GetComponent<MeshRenderer>().sharedMaterial,Is.SameAs(original.roofMaterial));
        }
    }
}
