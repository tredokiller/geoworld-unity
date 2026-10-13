using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    // The export's "roof" block: shape, ridge, rise and colour, each with the source it came from.
    public class GeoRoofTests
    {
        GameObject root;
        readonly List<Object> owned=new List<Object>();
        [SetUp] public void Setup(){root=new GameObject("Roof test");}
        [TearDown] public void Cleanup()
        {
            if(root)Object.DestroyImmediate(root);
            foreach(var asset in owned)if(asset)Object.DestroyImmediate(asset);
            owned.Clear();
        }

        static BuildingFeature Source(GeoRoofInfo roof=null,float rotation=0,string id="osm/way/7")=>new BuildingFeature{
            id=id,category="house",height=6,levels=1,width=8,length=12,rotation=rotation,roof=roof,
            footprint=new float[]{-4,-6,4,-6,4,6,-4,6}};
        GeoBuilding Create(BuildingFeature source)=>GeoBuildingBuilder.Create(source,root.transform,p=>0,owned.Add,new List<string>());
        static GeoRoofInfo Estimated(string shape,string color="#6e4636",float rise=2.5f)=>new GeoRoofInfo{shape=shape,shapeSource="estimated",shapeConfidence=0.5f,
            color=color,colorSource="estimated",colorConfidence=0.25f,rise=rise,material="metal",materialSource="estimated"};
        static Vector3 WorldAxis(GeoBuilding b){var c=new GeoBuildingContext(b);return b.transform.TransformDirection(new Vector3(c.roofAxis.x,0,c.roofAxis.y));}
        static Vector3 Compass(float degrees)=>new Vector3(Mathf.Sin(degrees*Mathf.Deg2Rad),0,Mathf.Cos(degrees*Mathf.Deg2Rad));

        [Test] public void OlderExport_WithoutARoofBlock_KeepsTheStyleRoof()
        {
            var building=Create(Source());
            Assert.IsNull(building.roofInfo);Assert.That(building.roof,Is.EqualTo(building.style.defaultRoof));
            Assert.IsFalse(building.overrideRoofColor);Assert.That(building.importNote,Is.Empty);
        }

        [TestCase("flat",GeoRoofShape.Flat)] [TestCase("gabled",GeoRoofShape.Gabled)]
        [TestCase("hipped",GeoRoofShape.Hipped)] [TestCase("skillion",GeoRoofShape.Skillion)]
        public void EstimatedShapeRiseAndColour_AreDrawn(string shape,GeoRoofShape expected)
        {
            var building=Create(Source(Estimated(shape)));
            Assert.That(building.roof,Is.EqualTo(expected));
            if(expected!=GeoRoofShape.Flat)Assert.That(building.roofHeight,Is.EqualTo(2.5f));
            Assert.IsTrue(building.overrideRoofColor);
            Assert.That(ColorUtility.ToHtmlStringRGB(building.roofColor),Is.EqualTo("6E4636"));
            var material=building.generatedGeometry.Find("Roof").GetComponent<MeshRenderer>().sharedMaterial;
            Assert.That(ColorUtility.ToHtmlStringRGB(material.HasProperty("_BaseColor")?material.GetColor("_BaseColor"):material.color),Is.EqualTo("6E4636"));
            Assert.That(building.roofInfo.shapeSource,Is.EqualTo("estimated"),"The provenance stays with the building.");
            Assert.That(building.height,Is.EqualTo(6));
        }

        [Test] public void Rise_NeverEatsTheWalls()
        {
            var building=Create(Source(Estimated("gabled",rise:20)));
            Assert.That(building.roofHeight,Is.EqualTo(4),"At least 2 m of wall stay below the roof.");
            Assert.That(building.height,Is.EqualTo(6));
        }

        [Test] public void FlatRoof_IgnoresARise()
        {
            var building=Create(Source(Estimated("flat",rise:3)));
            Assert.That(new GeoBuildingContext(building).roofRise,Is.Zero);
        }

        [TestCase(0f,0f)] [TestCase(30f,30f)] [TestCase(30f,120f)] [TestCase(250f,70f)] [TestCase(175f,5f)]
        public void RidgeBearing_RunsTheRidgeWhereTheExportSays(float rotation,float ridgeBearing)
        {
            var roof=Estimated("gabled");roof.ridge="along";roof.ridgeBearing=ridgeBearing;roof.ridgeSource="mapped";
            var building=Create(Source(roof,rotation));
            var axis=WorldAxis(building);
            var ridge=new Vector3(axis.z,0,-axis.x); // square to the downslope axis
            Assert.That(Mathf.Abs(Vector3.Dot(ridge,Compass(ridgeBearing))),Is.EqualTo(1).Within(0.001f));
            Assert.That(building.roofDirection,Is.InRange(0,179.99f));
        }

        [Test] public void RidgeAlongTheLongSide_NeedsNoTurn_AndAcrossTurnsAQuarter()
        {
            var along=Estimated("gabled");along.ridge="along";along.ridgeBearing=37.5f;
            Assert.That(Create(Source(along,37.5f)).roofDirection,Is.Zero);
            Assert.That(Create(Source(along,37.54f)).roofDirection,Is.Zero,"Bearings are rounded to 0.1 degree; that is still along.");
            var across=Estimated("gabled");across.ridge="across";
            Assert.That(Create(Source(across,37.5f)).roofDirection,Is.EqualTo(90));
        }

        [TestCase(0f,0f)] [TestCase(90f,40f)] [TestCase(225f,300f)] [TestCase(310f,15f)]
        public void MappedSlopeBearing_DrainsASingleSlopeThatWay(float slope,float rotation)
        {
            var roof=new GeoRoofInfo{shape="skillion",shapeSource="mapped",renderAs="flat",slopeBearing=slope,rise=2};
            var building=Create(Source(roof,rotation));
            Assert.That(building.roof,Is.EqualTo(GeoRoofShape.Skillion));
            Assert.That(Vector3.Dot(WorldAxis(building),Compass(slope)),Is.EqualTo(1).Within(0.001f),"The downhill side, not just the line.");
            Assert.That(building.roofDirection,Is.InRange(0,359.99f));
        }

        [Test] public void EstimatedSingleSlope_FacesADeterministicSideThatVariesBetweenBuildings()
        {
            var directions=new HashSet<float>();
            for(int n=0;n<24;n++)
            {
                var first=Create(Source(Estimated("skillion"),id:"osm/way/"+n));var again=Create(Source(Estimated("skillion"),id:"osm/way/"+n));
                Assert.That(again.roofDirection,Is.EqualTo(first.roofDirection),"The same building always drains the same way.");
                directions.Add(first.roofDirection);
            }
            Assert.That(directions,Is.EquivalentTo(new[]{0f,180f}));
        }

        [Test] public void SingleSlope_RisesFromTheEavesToTheFullHeight()
        {
            var style=ScriptableObject.CreateInstance<GeoBuildingStyle>();owned.Add(style);
            var building=root.AddComponent<GeoBuilding>();building.style=style;
            building.footprint=new List<Vector2>{new Vector2(-3,-5),new Vector2(3,-5),new Vector2(3,5),new Vector2(-3,5)};
            building.height=6;building.roof=GeoRoofShape.Skillion;building.roofHeight=2;building.roofDirection=0;
            using(var output=GeoBuildingGeometry.Build(building,0.5f))
            {
                var roof=output.parts.Single(p=>p.name=="Roof").mesh;
                Assert.That(roof.bounds.max.y,Is.EqualTo(6).Within(0.001f));Assert.That(roof.bounds.min.y,Is.EqualTo(4).Within(0.001f));
                foreach(var v in roof.vertices)
                {
                    if(Mathf.Abs(v.x+3)<0.001f)Assert.That(v.y,Is.EqualTo(6).Within(0.001f),"Up the slope, against the downslope axis.");
                    if(Mathf.Abs(v.x-3)<0.001f)Assert.That(v.y,Is.EqualTo(4).Within(0.001f),"Down the slope, at the eaves.");
                }
                var vertices=roof.vertices;var triangles=roof.triangles;float area=0;
                for(int i=0;i<triangles.Length;i+=3)
                {
                    float cross=Vector3.Cross(vertices[triangles[i+1]]-vertices[triangles[i]],vertices[triangles[i+2]]-vertices[triangles[i]]).y;
                    Assert.That(cross,Is.GreaterThan(0),"Roof faces up.");area+=cross*0.5f;
                }
                Assert.That(area,Is.EqualTo(60).Within(0.01f),"The slope covers exactly the footprint.");
                foreach(var n in output.parts.Single(p=>p.name=="Walls").mesh.normals)Assert.That(Mathf.Abs(n.y),Is.LessThan(0.001f));
                foreach(var part in output.parts)foreach(var v in part.mesh.vertices)Assert.IsTrue(GeoData.Finite(v.x+v.y+v.z));
            }
            building.roofDirection=180;
            using(var output=GeoBuildingGeometry.Build(building,0.5f))
                foreach(var v in output.parts.Single(p=>p.name=="Roof").mesh.vertices)
                    if(Mathf.Abs(v.x-3)<0.001f)Assert.That(v.y,Is.EqualTo(6).Within(0.001f),"Turned round, the high side is the other one.");
        }

        [Test] public void SingleSlope_OnAConcaveFootprint_StaysInsideTheOutline()
        {
            var style=ScriptableObject.CreateInstance<GeoBuildingStyle>();owned.Add(style);
            var building=root.AddComponent<GeoBuilding>();building.style=style;
            var outline=new List<Vector2>{new Vector2(0,0),new Vector2(8,0),new Vector2(8,3),new Vector2(3,3),new Vector2(3,8),new Vector2(0,8)};
            building.footprint=outline;building.height=10;building.roof=GeoRoofShape.Skillion;building.roofHeight=3;building.roofDirection=31;
            using(var output=GeoBuildingGeometry.Build(building,0.5f))
            {
                var roof=output.parts.Single(p=>p.name=="Roof").mesh;float area=0;
                for(int i=0;i<roof.triangles.Length;i+=3)
                {
                    var a=roof.vertices[roof.triangles[i]];var b=roof.vertices[roof.triangles[i+1]];var c=roof.vertices[roof.triangles[i+2]];
                    area+=Vector3.Cross(b-a,c-a).y*0.5f;
                    var centre=(a+b+c)/3;Assert.IsTrue(GeoPolygon.Contains(outline,new Vector2(centre.x,centre.z)),"Nothing across the notch.");
                }
                Assert.That(area,Is.EqualTo(39).Within(0.01f));
                Assert.That(roof.bounds.max.y,Is.EqualTo(10).Within(0.001f));
            }
        }

        [Test] public void ShapeTheRendererCannotDraw_UsesTheNearestOne_AndSaysSo()
        {
            var dome=new GeoRoofInfo{shape="onion",shapeSource="mapped",renderAs="hipped"};
            var building=Create(Source(dome));
            Assert.That(building.roof,Is.EqualTo(GeoRoofShape.Hipped));
            StringAssert.Contains("nearest",building.importNote);StringAssert.Contains("onion",building.importNote);
            var unknown=Create(Source(new GeoRoofInfo{shape="sawtooth-ish",shapeSource="mapped"}));
            Assert.That(unknown.roof,Is.EqualTo(unknown.style.defaultRoof),"No nearest shape known: the style decides.");
        }

        [Test] public void MappedAndReviewedDetails_StillWinOverTheRoofBlock()
        {
            var source=Source(Estimated("gabled"));
            source.mappedAppearance=new GeoMappedAppearance{source="osm",roofShape="hipped",roofColor="#336699"};
            var building=Create(source);
            Assert.That(building.roof,Is.EqualTo(GeoRoofShape.Hipped));
            Assert.That(ColorUtility.ToHtmlStringRGB(building.roofColor),Is.EqualTo("336699"));
            source.visualDetails=new GeoBuildingVisualDetails{roofShape="flat",roofColor="#ffffff"};
            var reviewed=Create(source);
            Assert.That(reviewed.roof,Is.EqualTo(GeoRoofShape.Flat));Assert.That(ColorUtility.ToHtmlStringRGB(reviewed.roofColor),Is.EqualTo("FFFFFF"));
        }

        [Test] public void MappedSingleSlope_IsNowDrawnAndKeepsItsDownhillSide()
        {
            var source=Source();source.rotation=20;
            source.mappedAppearance=new GeoMappedAppearance{source="osm",roofShape="skillion",roofDirectionMeaning="downslope_bearing",roofDirection=200};
            var building=Create(source);
            Assert.That(building.roof,Is.EqualTo(GeoRoofShape.Skillion));
            Assert.That(Vector3.Dot(WorldAxis(building),Compass(200)),Is.EqualTo(1).Within(0.001f));
        }

        [Test] public void EstimatedRoofs_CanBeLeftOut_WithoutChangingTheExportData()
        {
            var roof=Estimated("hipped");roof.shapeSource="mapped";roof.shapeConfidence=0.9f; // a mapped shape with an estimated colour, material and rise
            var source=Source(roof);
            Assert.AreSame(source,GeoGenerator.ForSettings(source,new GenerationSettings()),"Estimates are on by default.");
            var off=GeoGenerator.ForSettings(source,new GenerationSettings{estimatedRoofs=false});
            Assert.That(off.roof.shape,Is.EqualTo("hipped"));Assert.IsNull(off.roof.color);Assert.IsNull(off.roof.material);
            Assert.IsFalse(GeoRoofInfo.Has(off.roof.rise),"The rise followed from the shape and is not a mapped value.");
            Assert.That(source.roof.color,Is.EqualTo("#6e4636"),"The export data itself is not changed.");
            Assert.IsFalse(off.roof.HasEstimates());

            var guess=Source(Estimated("gabled"));
            var none=GeoGenerator.ForSettings(guess,new GenerationSettings{estimatedRoofs=false});
            Assert.IsNull(none.roof);
            var building=Create(none);
            Assert.That(building.roof,Is.EqualTo(building.style.defaultRoof));Assert.IsFalse(building.overrideRoofColor);
        }

        [Test] public void ReviewedAndMappedValues_SurviveTurningEstimatesOff()
        {
            var roof=new GeoRoofInfo{shape="gabled",shapeSource="reviewed",color="#aabbcc",colorSource="reviewed",colorConfidence=0.8f,
                pitch=40,pitchSource="mapped",rise=3,riseSource="mapped",material="metal",materialSource="estimated"};
            var kept=roof.WithoutEstimates();
            Assert.That(kept.shape,Is.EqualTo("gabled"));Assert.That(kept.color,Is.EqualTo("#aabbcc"));
            Assert.That(kept.pitch,Is.EqualTo(40));Assert.That(kept.rise,Is.EqualTo(3));Assert.IsNull(kept.material);
        }

        [Test] public void RoofBlock_ReadsFromJsonKeepsZeroAndSurvivesUnitySerialization()
        {
            var source=JsonConvert.DeserializeObject<BuildingFeature>(@"{""id"":""a"",""category"":""house"",""height"":6,""width"":8,""length"":12,
                ""roof"":{""shape"":""gabled"",""shapeSource"":""estimated"",""shapeConfidence"":0.55,""renderAs"":""gabled"",""ridge"":""along"",
                ""ridgeBearing"":0,""pitch"":35,""rise"":2.9,""material"":""metal"",""materialSource"":""estimated"",""color"":""#6e4636"",
                ""colorSource"":""estimated"",""colorConfidence"":0.25}}");
            var roof=source.roof;
            Assert.IsTrue(GeoRoofInfo.Has(roof.ridgeBearing),"A ridge running north is a value, not a missing one.");Assert.That(roof.ridgeBearing,Is.Zero);
            Assert.IsFalse(GeoRoofInfo.Has(roof.slopeBearing));Assert.IsTrue(roof.HasEstimates());
            var restored=JsonUtility.FromJson<GeoRoofInfo>(JsonUtility.ToJson(roof));
            Assert.That(restored.shape,Is.EqualTo("gabled"));Assert.That(restored.ridgeBearing,Is.Zero);Assert.IsFalse(GeoRoofInfo.Has(restored.slopeBearing));
            Assert.That(restored.rise,Is.EqualTo(2.9f));Assert.That(restored.colorSource,Is.EqualTo("estimated"));
            var plain=JsonConvert.DeserializeObject<BuildingFeature>(@"{""id"":""b"",""category"":""house"",""height"":6,""width"":8,""length"":12}");
            Assert.IsNull(plain.roof);
        }

        [Test] public void MergedCity_DrawsTheExportedRoofColourAndHeight()
        {
            var tiled=new GeoRoofInfo{shape="hipped",shapeSource="mapped",color="#cc3322",colorSource="mapped",rise=2.5f};
            var sources=new List<BuildingFeature>{Source(tiled,id:"red")};
            sources[0].x=20;sources[0].z=20;sources[0].y=0;
            var group=new GameObject("Merged roof test").transform;owned.Add(group.gameObject);
            GeoCityBuilder.Build(sources,new GeoBounds{minX=-500,minZ=-500,maxX=500,maxZ=500},group,p=>0f,o=>owned.Add(o),null,new GenerationSettings{chunkSize=1000},null,1);
            var mesh=group.GetComponentInChildren<MeshFilter>().sharedMesh;
            var vertices=mesh.vertices;var normals=mesh.normals;var uv=mesh.uv;
            Assert.That(mesh.bounds.max.y,Is.EqualTo(6).Within(0.001f));
            var roofColours=new List<Color>();
            for(int i=0;i<vertices.Length;i++)
            {
                if(vertices[i].y<=1||normals[i].y<=0.3f)continue; // the sloped roof, not the foundation top
                int index=Mathf.FloorToInt(uv[i].x*256)+256*Mathf.FloorToInt(uv[i].y*128);
                roofColours.Add(GeoCityBuilder.PaletteColor(index));
            }
            Assert.That(roofColours.Count,Is.GreaterThan(3));
            foreach(var colour in roofColours)
            {
                Assert.That(colour.r,Is.EqualTo(0.8f).Within(0.1f));Assert.That(colour.g,Is.EqualTo(0.2f).Within(0.1f));Assert.That(colour.b,Is.EqualTo(0.13f).Within(0.1f));
            }
        }
    }
}
