using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    public class GeoPropTests
    {
        EnvironmentData Env()=>new EnvironmentData{bounds=new GeoBounds{minX=0,minZ=0,maxX=80,maxZ=80}};
        GenerationSettings Settings()=>new GenerationSettings{treeSpacing=8,vegetationDensity=1,maxProps=5000};
        CanopyMask Mask(byte value=255)
        {return new CanopyMask(new MaterialMapInfo{width=80,height=80,cellSize=1,minX=0,maxZ=80},Enumerable.Repeat(new Color32(value,value,value,255),6400).ToArray());}
        List<PropPlacement> Plan(EnvironmentData e,BuildingsData b=null,GenerationSettings s=null,CanopyMask mask=null)
        =>GeoPropPlanner.Plan(e,b??new BuildingsData(),s??Settings(),mask,new List<string>());

        [Test] public void CanopyCoordinates_RespectNorthSouthOrientationAndEdges()
        {
            var mask=new CanopyMask(new MaterialMapInfo{width=2,height=2,cellSize=10,minX=100,maxZ=200},
                new[]{new Color32(0,0,0,255),new Color32(0,0,0,255),new Color32(255,255,255,255),new Color32(0,0,0,255)});
            Assert.IsTrue(mask.Contains(105,195));Assert.IsFalse(mask.Contains(105,185));
            Assert.IsFalse(mask.Contains(115,195));Assert.IsFalse(mask.Contains(99,195));Assert.IsFalse(mask.Contains(105,201));
        }
        [Test] public void OlderExportWithoutCanopy_DoesNotInventVegetation()
        {Assert.IsEmpty(Plan(Env()));Assert.IsEmpty(Plan(Env(),mask:Mask(0)));}

        [Test] public void Vegetation_AvoidsRoadsBuildingsWaterAndEntrances()
        {
            var env=Env();env.roads.Add(new LineFeature{id="road",width=8,points=new float[]{0,20,80,20}});
            env.water.Add(new AreaFeature{rings=new[]{new float[]{0,50,30,50,30,80,0,80}}});
            env.props.Add(new PropFeature{id="door",type="entrance",x=40,z=40});
            var buildings=new BuildingsData();buildings.buildings.Add(new BuildingFeature{id="house",x=55,z=55,footprint=new float[]{-8,-8,8,-8,8,8,-8,8}});
            var plan=Plan(env,buildings,mask:Mask());Assert.That(plan.Count,Is.GreaterThan(5));
            foreach(var p in plan)
            {
                Assert.That(p.kind,Is.EqualTo("tree"));Assert.That(p.source,Is.EqualTo("generated"));
                Assert.That(Mathf.Abs(p.position.z-20),Is.GreaterThanOrEqualTo(7));
                Assert.IsFalse(p.position.x<32.5f&&p.position.z>47.5f,"Water clearance");
                Assert.That(Vector2.Distance(GeoPropPlanner.XZ(p.position),new Vector2(40,40)),Is.GreaterThanOrEqualTo(4.5f));
                Assert.IsFalse(p.position.x>43.5f&&p.position.x<66.5f&&p.position.z>43.5f&&p.position.z<66.5f,"Building access margin");
            }
        }
        [Test] public void Vegetation_IsRepeatableAndKeepsDistanceFromMappedTrees()
        {
            var env=Env();env.trees.Add(new TreeFeature{id="tree/a",x=40,z=40,crown=8});
            var a=Plan(env,mask:Mask());var b=Plan(env,mask:Mask());
            Assert.That(a.Select(p=>p.position),Is.EqualTo(b.Select(p=>p.position)));
            Assert.That(a.Select(p=>p.id).Distinct().Count(),Is.EqualTo(a.Count));
            foreach(var p in a.Where(p=>p.source=="generated"))Assert.That(Vector2.Distance(GeoPropPlanner.XZ(p.position),new Vector2(40,40)),Is.GreaterThanOrEqualTo(6.5f));
            var changed=Settings();changed.decorationSeed=15;
            Assert.That(Plan(env,s:changed,mask:Mask()).Select(p=>p.position),Is.Not.EqualTo(a.Select(p=>p.position)));
        }
        [Test] public void Gate_CutsAnOpeningInFenceAndHedge()
        {
            var env=Env();env.props.Add(new PropFeature{id="gate",type="gate",x=40,z=40,width=4});
            env.barriers.Add(new BarrierFeature{id="fence",type="fence",points=new float[]{20,40,60,40}});
            env.hedges.Add(new BarrierFeature{id="hedge",points=new float[]{20,40,60,40}});
            var plan=Plan(env);Assert.That(plan.Count(p=>p.kind=="gate"),Is.EqualTo(1));
            foreach(var p in plan.Where(p=>p.segment))
            {Assert.IsTrue(p.position.x+p.length/2<=38.001f||p.position.x-p.length/2>=41.999f);Assert.That(p.source,Is.EqualTo("mapped"));}
            Assert.That(plan.Where(p=>p.kind=="fence").Sum(p=>p.length),Is.EqualTo(36).Within(0.001));
        }
        [Test] public void MappedProps_KeepTheirPositionAndExplicitBearing()
        {
            var env=Env();env.props.Add(new PropFeature{id="bench",type="bench",x=11,z=12,rotation=135,rotationSource="tag"});
            var p=Plan(env).Single();Assert.That(p.position,Is.EqualTo(new Vector3(11,0,12)));Assert.That(p.yaw,Is.EqualTo(135));Assert.That(p.rotationSource,Is.EqualTo("tag"));
            var settings=Settings();settings.mappedProps=false;Assert.IsEmpty(Plan(env,s:settings));
        }
        [Test] public void TreeRowPositions_AreMarkedGeneratedAndHaveStableIds()
        {
            var env=Env();env.treeRows.Add(new BarrierFeature{id="row",points=new float[]{10,40,70,40}});
            var plan=Plan(env);Assert.That(plan.Count,Is.GreaterThan(3));
            Assert.IsTrue(plan.All(p=>p.source=="generated"&&p.evidence=="row"));
            Assert.That(plan.Select(p=>p.id),Is.EqualTo(Plan(env).Select(p=>p.id)));
        }
        [Test] public void InvalidPositionsAndDuplicateSourceIds_AreSkipped()
        {
            var env=Env();env.props.Add(new PropFeature{id="bad",type="bench",x=float.NaN,z=12});
            env.props.Add(new PropFeature{id="outside",type="bench",x=90,z=12});
            env.props.Add(new PropFeature{id="same",type="bench",x=11,z=12});env.props.Add(new PropFeature{id="same",type="bench",x=11,z=12});
            Assert.That(Plan(env).Count,Is.EqualTo(1));
        }
        [Test] public void PropLimit_IsBoundedAndReported()
        {
            var env=Env();for(int i=0;i<150;i++)env.props.Add(new PropFeature{id="bench/"+i,type="bench",x=10,z=10});
            var settings=Settings();settings.maxProps=100;var notes=new List<string>();
            var plan=GeoPropPlanner.Plan(env,new BuildingsData(),settings,null,notes);
            Assert.That(plan.Count,Is.EqualTo(100));Assert.IsTrue(notes.Any(n=>n.Contains("limit")));
        }
        [Test] public void StreetDecoration_IsOptInAndRespectsExplicitUnlitRoads()
        {
            var env=Env();env.roads.Add(new LineFeature{id="road",type="residential",width=5,points=new float[]{0,40,80,40}});
            var buildings=new BuildingsData();buildings.buildings.Add(new BuildingFeature{id="house",x=40,z=60,width=6,length=6});
            Assert.IsEmpty(Plan(env,buildings));var settings=Settings();settings.streetDecoration=true;
            var plan=Plan(env,buildings,settings);Assert.IsNotEmpty(plan);
            Assert.IsTrue(plan.All(p=>p.kind=="street_lamp"&&p.source=="generated"&&Mathf.Abs(p.position.z-40)>=5));
            env.roads[0].lit="no";Assert.IsEmpty(Plan(env,buildings,settings));
        }
        [Test] public void PrefabCatalog_IsInstantiatedAndKeptEditsSurviveRebuild()
        {
            string path="Assets/GeoWorld/Generated/PropTest-"+System.Guid.NewGuid().ToString("N")+".prefab";
            var original=GameObject.CreatePrimitive(PrimitiveType.Cube);var root=new GameObject("Prop test").AddComponent<GeoWorldRoot>();
            var next=new GameObject("Next prop test").AddComponent<GeoWorldRoot>();var catalog=ScriptableObject.CreateInstance<GeoPropCatalog>();
            var owned=new List<UnityEngine.Object>();
            try
            {
                var prefab=PrefabUtility.SaveAsPrefabAsset(original,path);
                catalog.prefabs.Add(new GeoPropPrefab{kind="bench",prefab=prefab,scale=2,yawOffset=90});
                root.settings=Settings();root.settings.propCatalog=catalog;root.settings.vegetation=false;
                var env=Env();env.props.Add(new PropFeature{id="bench",type="bench",x=11,z=12,rotationSource="tag",rotation=45});
                var field=new HeightField(env.bounds,33);
                GeoPropBuilder.Build(root,env,new BuildingsData(),field,new List<string>(),owned.Add);
                var placed=root.GetComponentInChildren<GeoPlacedProp>();var child=placed.transform.GetChild(0);
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(child.gameObject));Assert.That(child.localScale,Is.EqualTo(Vector3.one*2));
                placed.keepOnRebuild=true;placed.transform.position=new Vector3(17,3,19);
                next.settings=root.settings;
                GeoPropBuilder.Build(next,env,new BuildingsData(),field,new List<string>(),owned.Add,root);
                var copies=next.GetComponentsInChildren<GeoPlacedProp>();Assert.That(copies.Length,Is.EqualTo(1));
                Assert.That(copies[0].transform.position,Is.EqualTo(new Vector3(17,3,19)));Assert.That(copies[0].sourceId,Is.EqualTo("bench"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(original);UnityEngine.Object.DestroyImmediate(root.gameObject);UnityEngine.Object.DestroyImmediate(next.gameObject);
                UnityEngine.Object.DestroyImmediate(catalog);foreach(var item in owned)if(item)UnityEngine.Object.DestroyImmediate(item);
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
