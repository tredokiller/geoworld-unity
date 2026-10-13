using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    public class GeoConditionTests
    {
        GeoBuilding building;
        GeoBuildingStyle style;
        readonly List<Object> disposable=new List<Object>();

        [SetUp] public void Setup()
        {
            style=ScriptableObject.CreateInstance<GeoBuildingStyle>();
            building=new GameObject("Condition test").AddComponent<GeoBuilding>();
            building.style=style;building.sourceId="osm/way/42";
            building.footprint=new List<Vector2>{new Vector2(0,0),new Vector2(12,0),new Vector2(12,9),new Vector2(0,9)};
            building.height=9;building.floors=3;building.roof=GeoRoofShape.Gabled;building.roofHeight=2;building.windows=true;
        }
        [TearDown] public void Cleanup()
        {
            if(building)Object.DestroyImmediate(building.gameObject);
            if(style)Object.DestroyImmediate(style);
            foreach(var o in disposable)if(o)Object.DestroyImmediate(o);
            disposable.Clear();
        }

        void Set(string state,string basis="reported")=>building.condition=new GeoBuildingCondition{state=state,basis=basis,confidence="medium"};
        GeoBuildingOutput Build()=>GeoBuildingGeometry.Build(building,0.5f);
        static Mesh Part(GeoBuildingOutput output,string name)=>output.parts.SingleOrDefault(p=>p.name==name)?.mesh;

        [Test] public void Condition_ReadsFromTheExportAndKeepsWhatIsMissing()
        {
            var feature=JsonConvert.DeserializeObject<BuildingFeature>("{\"id\":\"osm/way/1\",\"condition\":{\"state\":\"heavily_damaged\",\"basis\":\"estimated\",\"confidence\":\"low\",\"asOf\":\"2026-10-01\",\"score\":9.5,\"sources\":[\"sar-145\"]}}");
            Assert.That(feature.condition.State,Is.EqualTo(GeoConditionState.HeavilyDamaged));
            Assert.IsTrue(feature.condition.Estimated);
            Assert.That(feature.condition.score,Is.EqualTo(9.5f));
            Assert.That(feature.condition.sources,Is.EqualTo(new[]{"sar-145"}));
            var plain=JsonConvert.DeserializeObject<BuildingFeature>("{\"id\":\"osm/way/2\"}");
            Assert.IsNull(plain.condition);
            var unscored=JsonConvert.DeserializeObject<BuildingFeature>("{\"condition\":{\"state\":\"destroyed\"}}");
            Assert.That(unscored.condition.score,Is.LessThan(0),"A missing score must not read as a score of zero.");
            Assert.That(GeoBuildingCondition.Parse("something else"),Is.EqualTo(GeoConditionState.Unknown));
        }

        [Test] public void UnknownAndIntactBuildings_AreDrawnWhole()
        {
            foreach(var state in new[]{null,"intact","unknown"})
            {
                if(state!=null)Set(state);
                using(var output=Build())
                {
                    Assert.IsNotNull(Part(output,"Roof"),"state "+state);Assert.IsNotNull(Part(output,"Walls"));
                    Assert.IsNull(Part(output,"Rubble"));Assert.IsNull(Part(output,"Broken windows"));
                    Assert.IsNotNull(Part(output,"Windows"));
                }
            }
        }

        [Test] public void Damaged_KeepsTheBuildingAndBreaksMostWindows()
        {
            Set("damaged");
            using(var output=Build())
            {
                Assert.IsNotNull(Part(output,"Roof"));
                var walls=Part(output,"Walls");Assert.That(walls.bounds.max.y,Is.EqualTo(9).Within(0.001f).Or.GreaterThan(7.9f),"The walls still stand.");
                var broken=Part(output,"Broken windows");var glass=Part(output,"Windows");
                Assert.IsNotNull(broken);
                Assert.That(broken.vertexCount,Is.GreaterThan(glass==null?0:glass.vertexCount),"Most windows are dark holes.");
            }
        }

        static float Area(Mesh mesh)
        {
            var v=mesh.vertices;var t=mesh.triangles;float area=0;
            for(int i=0;i<t.Length;i+=3)area+=Vector3.Cross(v[t[i+1]]-v[t[i]],v[t[i+2]]-v[t[i]]).magnitude*0.5f;
            return area;
        }

        [Test] public void Damaged_RoofIsScorchedInPatchesOnSomeBuildings()
        {
            float whole;
            using(var output=Build())whole=Area(Part(output,"Roof"));
            Set("damaged");
            int scorched=0;
            for(int i=0;i<80;i++)
            {
                building.sourceId="osm/way/"+(5000+i);
                using(var output=Build())
                {
                    var roof=Part(output,"Roof");var burnt=Part(output,"Burnt roof");
                    Assert.IsNotNull(roof,"The plain roof is always there, building "+i);
                    float area=Area(roof)+(burnt==null?0:Area(burnt));
                    Assert.That(area,Is.EqualTo(whole).Within(whole*0.001f),"Scorched or not, the roof covers the same footprint, building "+i);
                    if(burnt!=null)scorched++;
                }
            }
            Assert.That(scorched,Is.InRange(8,60),"Some damaged roofs are partly scorched, not all and not none.");
            var red=new Color(0.55f,0.2f,0.15f);uint seed=GeoBuildingDamage.Seed("x");
            Assert.That(GeoBuildingDamage.Tint(GeoConditionState.Damaged,"Burnt roof",red,seed).grayscale,
                Is.LessThan(GeoBuildingDamage.Tint(GeoConditionState.Damaged,"Roof",red,seed).grayscale));
            Set("intact");using(var output=Build())Assert.IsNull(Part(output,"Burnt roof"),"A whole building is never scorched.");
        }

        [Test] public void HeavilyDamaged_LosesTheRoofAndTheWallsBreakDown()
        {
            Set("heavily_damaged");
            using(var output=Build())
            {
                Assert.IsNull(Part(output,"Roof"));
                Assert.IsNotNull(Part(output,"Rubble"));
                var walls=Part(output,"Walls");
                Assert.That(walls.bounds.max.y,Is.LessThanOrEqualTo(7.001f),"No wall rises above the eaves.");
                Assert.That(walls.bounds.size.y,Is.GreaterThan(0.5f));
                Assert.IsNotNull(Part(output,"Broken windows"),"The windows that are left are dark holes.");
                Assert.IsNull(Part(output,"Windows"),"No glass survives.");
            }
        }

        [Test] public void Destroyed_IsAStumpAndAMoundOfRubble()
        {
            Set("destroyed");
            using(var output=Build())
            {
                Assert.IsNull(Part(output,"Roof"));Assert.IsNull(Part(output,"Windows"));Assert.IsNull(Part(output,"Broken windows"));
                var walls=Part(output,"Walls");var rubble=Part(output,"Rubble");
                Assert.That(walls.bounds.max.y,Is.LessThanOrEqualTo(5.61f),"Fragments stand, but nothing like the whole building.");
                Assert.That(rubble.bounds.max.y,Is.InRange(0.3f,2.6f));
                Assert.That(rubble.bounds.min.y,Is.GreaterThanOrEqualTo(0f));
                foreach(var part in output.parts)foreach(var v in part.mesh.vertices)
                    Assert.IsTrue(float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z),part.name);
                // the rubble stays inside the footprint
                Assert.That(rubble.bounds.min.x,Is.GreaterThanOrEqualTo(-0.01f));Assert.That(rubble.bounds.max.x,Is.LessThanOrEqualTo(12.01f));
                Assert.That(rubble.bounds.min.z,Is.GreaterThanOrEqualTo(-0.01f));Assert.That(rubble.bounds.max.z,Is.LessThanOrEqualTo(9.01f));
            }
        }

        [Test] public void Damage_IsTheSameEveryTimeForTheSameBuildingAndDiffersBetweenBuildings()
        {
            Set("destroyed");
            Vector3[] first,second,other;
            using(var output=Build())first=Part(output,"Walls").vertices;
            using(var output=Build())second=Part(output,"Walls").vertices;
            building.sourceId="osm/way/43";
            using(var output=Build())other=Part(output,"Walls").vertices;
            Assert.That(second,Is.EqualTo(first));
            Assert.That(other.Length==first.Length&&other.SequenceEqual(first),Is.False);
        }

        [Test] public void BrokenBuildings_StayWithinAVertexBudget()
        {
            Set("destroyed");using(var output=Build())Assert.That(output.parts.Sum(p=>p.mesh.vertexCount),Is.LessThan(700));
            Set("heavily_damaged");using(var output=Build())Assert.That(output.parts.Sum(p=>p.mesh.vertexCount),Is.LessThan(1600));
        }

        [Test] public void Tint_DarkensDamagedPartsAndLeavesWholeBuildingsAlone()
        {
            var cream=new Color(0.8f,0.75f,0.6f);uint seed=GeoBuildingDamage.Seed("osm/way/42");
            Assert.That(GeoBuildingDamage.Tint(GeoConditionState.Intact,"Walls",cream,seed),Is.EqualTo(cream));
            Assert.That(GeoBuildingDamage.Tint(GeoConditionState.Unknown,"Roof",cream,seed),Is.EqualTo(cream));
            foreach(var state in new[]{GeoConditionState.Damaged,GeoConditionState.HeavilyDamaged,GeoConditionState.Destroyed})
            {
                var walls=GeoBuildingDamage.Tint(state,"Walls",cream,seed);
                Assert.That(walls.grayscale,Is.LessThan(cream.grayscale),state.ToString());
                Assert.That(GeoBuildingDamage.Tint(state,"Broken windows",cream,seed).grayscale,Is.LessThan(0.1f));
            }
            var damaged=GeoBuildingDamage.Tint(GeoConditionState.Damaged,"Walls",cream,seed);
            var destroyed=GeoBuildingDamage.Tint(GeoConditionState.Destroyed,"Walls",cream,seed);
            Assert.That(destroyed.grayscale,Is.LessThan(damaged.grayscale),"More damage, more soot.");
        }

        // which buildings are damaged is the app's to say: whatever the evidence, the export's condition is drawn as it is
        [Test] public void TheExportsConditionIsDrawnAsItIs()
        {
            var estimated=new BuildingFeature{id="a",condition=new GeoBuildingCondition{state="destroyed",basis="estimated"}};
            var reported=new BuildingFeature{id="b",condition=new GeoBuildingCondition{state="destroyed",basis="reported"}};
            var settings=new GenerationSettings();
            Assert.AreSame(estimated,GeoGenerator.ForSettings(estimated,settings));
            Assert.AreSame(reported,GeoGenerator.ForSettings(reported,settings));
            Assert.IsNull(GeoGenerator.ForSettings(reported,new GenerationSettings{applyCondition=false}).condition,"Damage switched off: every building whole.");
            Assert.IsNotNull(reported.condition,"The export data itself is not changed.");
        }

        [TestCase(GeoBuildingMode.Auto,100,false)] [TestCase(GeoBuildingMode.Auto,3000,true)]
        [TestCase(GeoBuildingMode.Editable,50000,false)] [TestCase(GeoBuildingMode.Merged,3,true)]
        public void BuildingMode_DecidesBetweenEditableObjectsAndMergedChunks(GeoBuildingMode mode,int count,bool merged)
        {
            Assert.That(GeoCityBuilder.ShouldMerge(new GenerationSettings{buildingMode=mode,mergeAbove=2500},count),Is.EqualTo(merged));
        }

        static BuildingFeature House(string id,float x,float z,string state=null)
        {
            return new BuildingFeature{id=id,category="house",name="",heightSource="test",levels=1,x=x,z=z,y=0,width=8,length=10,rotation=15,height=5,
                footprint=new[]{-4f,-5f,4f,-5f,4f,5f,-4f,5f},
                condition=state==null?null:new GeoBuildingCondition{state=state,basis="reported",confidence="medium"}};
        }

        [Test] public void MergedCity_BakesBuildingsIntoChunksWithoutAnObjectPerBuilding()
        {
            var group=new GameObject("Merged buildings test").transform;disposable.Add(group.gameObject);
            var sources=new List<BuildingFeature>{House("h1",10,10),House("h2",40,30,"destroyed"),House("h3",160,20,"damaged"),
                new BuildingFeature{id="bad",category="house",x=20,z=20,width=0,length=5,height=5}};
            var settings=new GenerationSettings{chunkSize=100,applyCondition=true};
            var notes=new List<string>();
            int count=GeoCityBuilder.Build(sources,new GeoBounds{minX=-500,minZ=-500,maxX=500,maxZ=500},group,p=>2f,o=>disposable.Add(o),notes,settings,null,sources.Count);
            Assert.That(count,Is.EqualTo(3));
            Assert.That(group.childCount,Is.EqualTo(2),"Chunks of 100 m: two squares are populated.");
            Assert.That(group.GetComponentsInChildren<GeoBuilding>().Length,Is.EqualTo(0),"No object per building.");
            var entries=group.GetComponentsInChildren<GeoCityChunk>().SelectMany(c=>c.buildings).ToList();
            Assert.That(entries.Count,Is.EqualTo(3));
            Assert.That(entries.Single(e=>e.id=="h2").state,Is.EqualTo(GeoConditionState.Destroyed));
            Assert.That(entries.Single(e=>e.id=="h1").state,Is.EqualTo(GeoConditionState.Unknown));
            Assert.That(entries.Single(e=>e.id=="h2").position.y,Is.EqualTo(2f).Within(0.01f),"Buildings sit on the ground height.");
            foreach(var filter in group.GetComponentsInChildren<MeshFilter>())
            {
                var mesh=filter.sharedMesh;
                Assert.That(mesh.vertexCount,Is.GreaterThan(0));Assert.That(mesh.subMeshCount,Is.EqualTo(2));
                Assert.That(mesh.uv.All(u=>u.x>=0&&u.x<=1&&u.y>=0&&u.y<=1),"Palette coordinates stay inside the texture.");
                Assert.That(mesh.normals.All(n=>Mathf.Abs(n.magnitude-1)<0.01f));
            }
            Assert.That(notes.Any(n=>n.Contains("Merged city: 3 buildings")),string.Join("\n",notes));
        }

        [Test] public void MergedCity_DrawsDamageAsDarkerColoursAndLowerShapes()
        {
            var sources=new List<BuildingFeature>{House("whole",10,10),House("ruin",10,60,"destroyed")};
            var group=new GameObject("Merged damage test").transform;disposable.Add(group.gameObject);
            GeoCityBuilder.Build(sources,new GeoBounds{minX=-500,minZ=-500,maxX=500,maxZ=500},group,p=>0f,o=>disposable.Add(o),null,new GenerationSettings{chunkSize=1000},null,2);
            var mesh=group.GetComponentInChildren<MeshFilter>().sharedMesh;
            var vertices=mesh.vertices;
            var whole=vertices.Where(v=>v.z<35).Select(v=>v.y).Max();var ruin=vertices.Where(v=>v.z>35).Select(v=>v.y).Max();
            Assert.That(whole,Is.GreaterThan(4f));Assert.That(ruin,Is.LessThan(whole-1f),"The destroyed house is much lower.");
        }

        [Test] public void MergedCity_LeavesConditionOutWhenTheSettingsSayNo()
        {
            var sources=new List<BuildingFeature>{House("ruin",10,10,"destroyed")};
            var group=new GameObject("Merged no damage test").transform;disposable.Add(group.gameObject);
            GeoCityBuilder.Build(sources,new GeoBounds{minX=-500,minZ=-500,maxX=500,maxZ=500},group,p=>0f,o=>disposable.Add(o),null,new GenerationSettings{applyCondition=false},null,1);
            Assert.That(group.GetComponentInChildren<GeoCityChunk>().buildings[0].state,Is.EqualTo(GeoConditionState.Unknown));
            Assert.That(group.GetComponentInChildren<MeshFilter>().sharedMesh.bounds.max.y,Is.GreaterThan(4f));
        }
    }
}
