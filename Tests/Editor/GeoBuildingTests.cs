using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

namespace GeoWorld.Editor.Tests
{
    public class GeoBuildingTests
    {
        GeoBuilding building;
        GeoBuildingStyle style;
        List<Vector2> L()=>new List<Vector2>{new Vector2(0,0),new Vector2(8,0),new Vector2(8,3),new Vector2(3,3),new Vector2(3,8),new Vector2(0,8)};
        [SetUp] public void Setup()
        {
            style=ScriptableObject.CreateInstance<GeoBuildingStyle>();
            building=new GameObject("Building test").AddComponent<GeoBuilding>();building.style=style;building.footprint=L();building.height=10;
        }
        [TearDown] public void Cleanup(){if(building)Object.DestroyImmediate(building.gameObject);if(style)Object.DestroyImmediate(style);}

        [TestCase(false)] [TestCase(true)]
        public void ConcaveRoof_CoversExactFootprintWithoutFillingTheNotch(bool gabled)
        {
            building.roof=gabled?GeoRoofShape.Gabled:GeoRoofShape.Flat;building.roofHeight=3;building.roofDirection=31;
            using(var output=GeoBuildingGeometry.Build(building,0.5f))
            {
                var roof=output.parts.Single(p=>p.name=="Roof").mesh;
                var vertices=roof.vertices;var triangles=roof.triangles;float area=0;
                for(int i=0;i<triangles.Length;i+=3)
                {
                    var a=vertices[triangles[i]];var b=vertices[triangles[i+1]];var c=vertices[triangles[i+2]];
                    float cross=Vector3.Cross(b-a,c-a).y;
                    Assert.That(cross,Is.GreaterThan(0),"Roof normals must face upwards.");area+=cross*0.5f;
                    var center=(a+b+c)/3;Assert.IsTrue(GeoPolygon.Contains(L(),new Vector2(center.x,center.z)),"No roof across the empty notch.");
                }
                Assert.That(area,Is.EqualTo(39).Within(0.001));
                Assert.That(roof.bounds.max.y,Is.EqualTo(10).Within(0.001));
                Assert.That(roof.bounds.min.y,Is.EqualTo(gabled?7:10).Within(0.001));
                var walls=output.parts.Single(p=>p.name=="Walls").mesh;
                foreach(var n in walls.normals)Assert.That(Mathf.Abs(n.y),Is.LessThan(0.001));
            }
        }

        [Test] public void ReversedClosedOutlineAndCollinearCorners_AreNormalized()
        {
            var points=L();points.Insert(1,new Vector2(4,0));points.Add(points[0]);points.Reverse();
            var cleaned=GeoPolygon.Clean(points);
            Assert.That(cleaned.Count,Is.EqualTo(6));Assert.That(GeoPolygon.Area(cleaned),Is.EqualTo(39));
            Assert.That(GeoPolygon.Triangulate(cleaned).Count,Is.EqualTo(12));
        }

        [Test] public void CrossingOutlineAndNonFiniteCorners_AreRejected()
        {
            Assert.Throws<ArgumentException>(()=>GeoPolygon.Clean(new[]{Vector2.zero,new Vector2(4,4),new Vector2(0,4),new Vector2(4,0)}));
            Assert.Throws<ArgumentException>(()=>GeoPolygon.Clean(new[]{Vector2.zero,Vector2.one,new Vector2(float.NaN,2)}));
        }

        [Test] public void AlreadyOrientedJsonFootprint_IsNotRotatedTwice()
        {
            var source=new BuildingFeature{id="rotation",category="house",x=100,z=200,rotation=37,width=8,length=10,height=7,levels=2,
                footprint=new float[]{-3,-5,5,-1,3,5,-5,1}};
            GeoBuildingBuilder.Import(building,source);
            foreach(var local in building.footprint)
            {
                var world=building.transform.TransformPoint(new Vector3(local.x,0,local.y));
                bool found=false;
                for(int i=0;i<source.footprint.Length;i+=2)
                    if(Vector2.Distance(new Vector2(world.x-100,world.z-200),new Vector2(source.footprint[i],source.footprint[i+1]))<0.001f)found=true;
                Assert.IsTrue(found);
            }
            Assert.That(building.floors,Is.EqualTo(2));
        }

        [Test] public void MissingFootprint_UsesExplicitReportedBoxFallback()
        {
            GeoBuildingBuilder.Import(building,new BuildingFeature{id="missing",category="house",height=6,width=8,length=10});
            Assert.That(building.importNote,Does.Contain("fallback"));Assert.That(GeoPolygon.Area(building.footprint),Is.EqualTo(80));
        }

        [Test] public void CustomModule_AddsGeometryWithoutChangingTheCoreGenerator()
        {
            var module=ScriptableObject.CreateInstance<GeoBuildingBand>();style.modules=new GeoBuildingModule[]{module};
            try{using(var output=GeoBuildingGeometry.Build(building,0.5f))Assert.That(output.parts.Count(p=>p.name=="Facade band"),Is.EqualTo(1));}
            finally{Object.DestroyImmediate(module);}
        }

        [Test] public void EveryChuhuivFootprint_ProducesFiniteRoofAndWallsWithoutFallback()
        {
            var data=GeoData.ReadBuildings("Assets/GeoWorld/Samples/Chuhuiv/buildings.json",null);
            foreach(var source in data.buildings)
            {
                GeoBuildingBuilder.Import(building,source);
                Assert.That(building.importNote,Is.Empty,source.id);
                using(var output=GeoBuildingGeometry.Build(building,1))
                    foreach(var part in output.parts)
                        foreach(var v in part.mesh.vertices)Assert.IsTrue(GeoData.Finite(v.x)&&GeoData.Finite(v.y)&&GeoData.Finite(v.z),source.id);
            }
        }

        [Test] public void RebuildAndWorldRegeneration_PreserveEditsAttachmentsCollisionAndSavedMeshes()
        {
            string temp=Path.Combine(Path.GetTempPath(),"GeoBuildings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
            var assets=new HashSet<string>();GeoWorldRoot world=null;
            try
            {
                var data=new BuildingsData{format="geo-buildings",version=1,area=new BuildingArea{bounds=new GeoBounds{minX=-20,minZ=-20,maxX=20,maxZ=20}},
                    buildings=new List<BuildingFeature>{new BuildingFeature{id="editable",category="house",x=0,z=0,width=8,length=8,height=7,levels=2,footprint=L().SelectMany(p=>new[]{p.x,p.y}).ToArray()}}};
                string source=Path.Combine(temp,"buildings.json");File.WriteAllText(source,JsonConvert.SerializeObject(data));
                var settings=new GenerationSettings{heightResolution=33,textureResolution=32,roadPaintResolution=32,trees=false,satelliteImagery=false};
                world=GeoGenerator.Generate("",source,settings,recordUndo:false);assets.Add(world.assetPath);
                var editable=world.GetComponentInChildren<GeoBuilding>();
                var custom=new GameObject("Manual building").AddComponent<GeoBuilding>();custom.transform.SetParent(world.transform.Find("Buildings"),false);
                custom.style=editable.style;custom.height=4;custom.transform.localPosition=new Vector3(-12,0,-12);
                editable.height=12;editable.floors=4;editable.roof=GeoRoofShape.Flat;editable.footprint[1]=new Vector2(10,0);
                new GameObject("User attachment").transform.SetParent(editable.transform,false);
                GeoBuildingBuilder.Rebuild(editable,false);
                foreach(var mesh in editable.GetComponentsInChildren<MeshFilter>())assets.Add(AssetDatabase.GetAssetPath(mesh.sharedMesh));
                var previous=editable.generatedGeometry;
                var validOutline=new List<Vector2>(editable.footprint);editable.footprint[1]=new Vector2(float.NaN,0);
                Assert.Throws<ArgumentException>(()=>GeoBuildingBuilder.Rebuild(editable,false));Assert.That(editable.generatedGeometry,Is.EqualTo(previous));
                editable.footprint=validOutline;
                var terrain=world.terrain.terrainData;
                GeoGenerator.RebuildBuildings(world,false);
                editable=world.GetComponentInChildren<GeoBuilding>();
                foreach(var mesh in editable.GetComponentsInChildren<MeshFilter>())assets.Add(AssetDatabase.GetAssetPath(mesh.sharedMesh));
                Assert.That(world.terrain.terrainData,Is.SameAs(terrain),"Building-only rebuild must not replace terrain.");
                world=GeoGenerator.Generate("",source,settings,world,false);assets.Add(world.assetPath);
                editable=world.GetComponentInChildren<GeoBuilding>();
                Assert.That(editable.height,Is.EqualTo(12));Assert.That(editable.floors,Is.EqualTo(4));Assert.That(editable.footprint[1].x,Is.EqualTo(10));
                Assert.IsNotNull(editable.transform.Find("User attachment"));
                Assert.That(world.GetComponentsInChildren<GeoBuilding>().Count(b=>string.IsNullOrEmpty(b.sourceId)),Is.EqualTo(1));
                Assert.That(editable.name,Does.Not.Contain("Clone"));
                Physics.SyncTransforms();
                var roof=editable.generatedGeometry.Find("Roof").GetComponent<MeshCollider>();
                Assert.IsTrue(roof.Raycast(new Ray(new Vector3(1,30,1),Vector3.down),out var hit,100));
                Assert.That(hit.point.y,Is.EqualTo(12).Within(0.01));
                Assert.IsFalse(roof.Raycast(new Ray(new Vector3(6,30,6),Vector3.down),out _,100),"The concave notch must stay empty in collision too.");
                string scene=AssetDatabase.GenerateUniqueAssetPath(GeoGenerator.OutputFolder+"/BuildingPersistence.unity");assets.Add(scene);
                EditorSceneManager.SaveScene(world.gameObject.scene,scene,true);
                var preview=EditorSceneManager.OpenPreviewScene(scene);
                try
                {
                    var saved=preview.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<GeoBuilding>()).First(b=>b.sourceId=="editable");
                    Assert.That(saved.height,Is.EqualTo(12));Assert.That(saved.footprint[1].x,Is.EqualTo(10));
                    foreach(var filter in saved.GetComponentsInChildren<MeshFilter>())Assert.IsTrue(EditorUtility.IsPersistent(filter.sharedMesh));
                }
                finally{EditorSceneManager.ClosePreviewScene(preview);}
            }
            finally
            {
                if(world)Object.DestroyImmediate(world.gameObject);
                foreach(string asset in assets)if(!string.IsNullOrEmpty(asset))AssetDatabase.DeleteAsset(asset);
                Directory.Delete(temp,true);
            }
        }
    }
}
