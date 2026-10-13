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
    public class GeoGeneratorTests
    {
        string temporary;
        [SetUp] public void SetUp(){temporary=Path.Combine(Path.GetTempPath(),"GeoWorldTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);}
        [TearDown] public void TearDown(){if(Directory.Exists(temporary))Directory.Delete(temporary,true);}

        EnvironmentData Environment()=>new EnvironmentData{format="geo-environment",version=1,bounds=new GeoBounds{minX=10,minZ=20,maxX=12,maxZ=22},origin=new GeoOrigin{lat=49,lon=36}};
        string Save(string name,object data){string p=Path.Combine(temporary,name);File.WriteAllText(p,JsonConvert.SerializeObject(data));return p;}

        [Test] public void RectangularRawHeights_KeepNorthSouthAndAbsoluteElevation()
        {
            var env=Environment();env.bounds.maxX=14;
            env.terrain=new TerrainInfo{heightmap=new HeightmapInfo{file="heightmap.r16",width=3,height=2,cellSize=2,minX=10,maxZ=22,minHeight=100,maxHeight=200,originGround=120,encoding="uint16-le",rowOrder="north-to-south"}};
            using(var writer=new BinaryWriter(File.Create(Path.Combine(temporary,"heightmap.r16"))))
                foreach(ushort n in new ushort[]{65535,32768,0,0,16384,32768})writer.Write(n);
            var f=HeightField.Load(env,Save("environment.json",env),3,new List<string>());
            Assert.That(f.Sample(10,22),Is.EqualTo(80).Within(0.01));
            Assert.That(f.Sample(14,22),Is.EqualTo(-20).Within(0.01));
            Assert.That(f.Sample(10,20),Is.EqualTo(-20).Within(0.01));
            Assert.That(f.Sample(14,20),Is.EqualTo(30).Within(0.01));
            Assert.That(f.Sample(12,21),Is.EqualTo(17.5).Within(0.01));
        }

        [Test] public void TruncatedRawHeightmap_IsRejected()
        {
            var env=Environment();env.terrain=new TerrainInfo{heightmap=new HeightmapInfo{file="heightmap.r16",width=2,height=2,cellSize=2}};
            File.WriteAllBytes(Path.Combine(temporary,"heightmap.r16"),new byte[2]);
            Assert.Throws<InvalidDataException>(()=>HeightField.Load(env,Save("environment.json",env),33,new List<string>()));
        }

        [Test] public void MaterialIds_UseRawBytesAndNorthSouthOrientation_WithNormalizedBlend()
        {
            var env=Environment();env.materials=new MaterialMapInfo{file="materials.png",width=2,height=2,cellSize=1,minX=10,maxZ=22,
                classes=new List<MaterialClass>{new MaterialClass{id=1,name="grass"},new MaterialClass{id=2,name="cropland"},new MaterialClass{id=3,name="forest"},new MaterialClass{id=6,name="sand"}}};
            var tex=new Texture2D(2,2,TextureFormat.RGB24,false,true);
            tex.SetPixels32(new[]{new Color32(1,1,1,255),new Color32(2,2,2,255),new Color32(3,3,3,255),new Color32(6,6,6,255)});tex.Apply();
            File.WriteAllBytes(Path.Combine(temporary,"materials.png"),tex.EncodeToPNG());Object.DestroyImmediate(tex);
            var settings=new GenerationSettings{textureResolution=32,blendMetres=0.12f};
            var a=GeoTerrain.Paint(env,Save("environment.json",env),new List<PathBuild>(),settings,new List<string>());
            Assert.That(a[0,0,0],Is.EqualTo(1).Within(0.001));
            Assert.That(a[0,31,6],Is.EqualTo(1).Within(0.001));
            Assert.That(a[31,0,1],Is.EqualTo(1).Within(0.001));
            Assert.That(a[31,31,3],Is.EqualTo(1).Within(0.001));
            for(int z=0;z<32;z++)for(int x=0;x<32;x++)
            {float sum=0;for(int l=0;l<GeoTerrain.LayerNames.Length;l++)sum+=a[z,x,l];Assert.That(sum,Is.EqualTo(1).Within(0.0001));}
            Assert.That(a[15,15,0],Is.GreaterThan(0).And.LessThan(1));
        }

        [Test] public void RoadMesh_HasUpwardTrianglesAndFollowsTerrainAcrossWidth()
        {
            var field=new HeightField(new GeoBounds{minX=-10,minZ=-10,maxX=10,maxZ=10},33);
            for(int z=0;z<33;z++)for(int x=0;x<33;x++)field.heights[z,x]=field.X(x)*0.2f;
            var p=new PathBuild{feature=new LineFeature{width=6},samples=new List<Vector3>{new Vector3(0,0,-8),new Vector3(0,0,8)}};
            var mesh=GeoMeshes.Ribbon(p,field,0.1f);
            try
            {
                foreach(var v in mesh.vertices)Assert.That(v.y,Is.EqualTo(field.Sample(v.x,v.z)+0.1f).Within(0.001));
                foreach(var normal in mesh.normals)Assert.That(normal.y,Is.GreaterThan(0.9));
                Assert.That(mesh.bounds.size.x,Is.EqualTo(6).Within(0.001));
            }
            finally{Object.DestroyImmediate(mesh);}
        }

        [Test] public void DuplicateAndSharpPathNodes_DoNotProduceSpikesOrNaNs()
        {
            var controls=GeoData.Points(new float[]{0,0,0,0,10,0,0.01f,0.01f});
            Assert.That(controls.Count,Is.EqualTo(3));
            var samples=GeoData.Resample(controls,2);
            for(int i=0;i<samples.Count;i++)
            {var side=GeoMeshes.Side(samples,i);Assert.That(side.magnitude,Is.LessThanOrEqualTo(2.001));Assert.IsTrue(GeoData.Finite(side.x));}
        }

        [Test] public void PolygonHoles_AreExcluded()
        {
            var rings=new[]{new float[]{0,0,10,0,10,10,0,10},new float[]{3,3,7,3,7,7,3,7}};
            Assert.IsTrue(GeoData.InPolygon(1,1,rings));Assert.IsFalse(GeoData.InPolygon(5,5,rings));Assert.IsFalse(GeoData.InPolygon(-1,5,rings));
        }

        [Test] public void OriginMismatch_IsRejectedBeforeGeneration()
        {
            var data=new BuildingsData{format="geo-buildings",version=1,origin=new GeoOrigin{lat=50,lon=36}};
            Assert.Throws<InvalidDataException>(()=>GeoData.ReadBuildings(Save("buildings.json",data),new GeoOrigin{lat=49,lon=36}));
        }

        [Test] public void PackageFile_CannotEscapeExportDirectory()
        { Assert.Throws<InvalidDataException>(()=>GeoData.PackageFile(Path.Combine(temporary,"environment.json"),"../outside.r16")); }

        [Test] public void BridgeGrading_DoesNotFlattenRiverbed()
        {
            var field=new HeightField(new GeoBounds{minX=-10,minZ=-10,maxX=10,maxZ=10},33);
            var bridge=new PathBuild{feature=new LineFeature{width=6,bridge=true},samples=new List<Vector3>{new Vector3(-10,8,0),new Vector3(10,8,0)}};
            GeoTerrain.Grade(field,new List<PathBuild>{bridge},new GenerationSettings());
            Assert.That(field.Sample(0,0),Is.EqualTo(0));
        }

        [Test] public void GroundRoadGrading_SupportsWholeWidthOnASteepCrossSlope()
        {
            var field=new HeightField(new GeoBounds{minX=-20,minZ=-20,maxX=20,maxZ=20},33);
            for(int z=0;z<33;z++)for(int x=0;x<33;x++)field.heights[z,x]=field.X(x)*0.7f;
            var road=new PathBuild{feature=new LineFeature{width=5},samples=new List<Vector3>{new Vector3(0,0,-20),new Vector3(0,0,20)}};
            GeoTerrain.Grade(field,new List<PathBuild>{road},new GenerationSettings{roadShoulder=2});
            foreach(float x in new[]{-2.5f,-1.7f,0,1.7f,2.5f})Assert.That(field.Sample(x,0),Is.EqualTo(0).Within(0.001));
            Assert.That(field.Sample(15,0),Is.EqualTo(10.5f).Within(0.001),"Terrain beyond the shoulders remains unchanged.");
        }

        [Test] public void SavedGeneration_PreservesTerrainLayersMeshesAndRoadEdits_AndRejectsBadReplacement()
        {
            // Test Runner supplies an isolated untitled scene. Only our root is modified.
            var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            GeoWorldRoot world=null;var generated=new List<string>();
            try
            {
                var env=Environment();env.bounds=new GeoBounds{minX=-20,minZ=-20,maxX=20,maxZ=20};
                env.areas.Add(new AreaFeature{material="cropland",rings=new[]{new float[]{1,1,19,1,19,19,1,19}}});
                env.roads.Add(new LineFeature{id="test/road",width=4,material="asphalt",points=new float[]{-18,0,18,0}});
                env.roads.Add(new LineFeature{id="test/tunnel",width=4,tunnel=true,points=new float[]{0,-18,0,18}});
                string path=Save("environment.json",env);
                // roads painted into the terrain only (road meshes, the default, would add their own surface)
                var settings=new GenerationSettings{heightResolution=33,textureResolution=32,roadPaintResolution=32,trees=false,satelliteImagery=false,roadMeshes=false};
                world=GeoGenerator.Generate(path,"",settings,recordUndo:false);generated.Add(world.assetPath);
                Assert.That(world.roadCount,Is.EqualTo(1));
                var data=world.terrain.terrainData;
                Assert.IsTrue(EditorUtility.IsPersistent(data));
                Assert.That(data.GetAlphamaps(22,27,1,1)[0,0,6],Is.GreaterThan(0.9));
                Assert.That(data.GetAlphamaps(16,16,1,1)[0,0,GeoTerrain.AsphaltLayer],Is.GreaterThan(0.99));
                Assert.That(world.GetComponentInChildren<GeoPath>().GetComponentsInChildren<Renderer>(),Is.Empty,"Ground roads must have no separate visible surface.");
                Assert.That(world.GetComponentsInChildren<MeshCollider>(),Is.Empty,"Only TerrainCollider supports a ground road.");
                Assert.That(world.terrain.GetComponent<TerrainCollider>().Raycast(new Ray(new Vector3(0,100,0),Vector3.down),out var roadHit,200),Is.True);
                Assert.That(roadHit.point.y,Is.EqualTo(world.terrain.SampleHeight(Vector3.zero)+world.terrain.transform.position.y).Within(0.001));
                foreach(var filter in world.GetComponentsInChildren<MeshFilter>())Assert.IsTrue(EditorUtility.IsPersistent(filter.sharedMesh));
                var road=world.GetComponentInChildren<GeoPath>();road.width=8;road.points[1]=new Vector3(18,0,6);
                var rebuilt=GeoGenerator.Generate(path,"",settings,world,false);world=rebuilt;generated.Add(world.assetPath);
                road=world.GetComponentInChildren<GeoPath>();
                Assert.That(road.width,Is.EqualTo(8));Assert.That(road.points[1].z,Is.EqualTo(6));
                string scenePath=AssetDatabase.GenerateUniqueAssetPath("Assets/GeoWorld/Generated/TestPersistence.unity");
                generated.Add(scenePath);EditorSceneManager.SaveScene(scene,scenePath,true);
                var reloaded=AssetDatabase.LoadAssetAtPath<GeoGenerationAssets>(world.assetPath);
                Assert.IsNotNull(reloaded);
                var terrain=AssetDatabase.LoadAllAssetsAtPath(world.assetPath).OfType<TerrainData>().First();
                Assert.That(terrain.GetAlphamaps(22,27,1,1)[0,0,6],Is.GreaterThan(0.9));
                File.WriteAllText(path,"{\"format\":\"wrong\"}");
                Assert.Throws<InvalidDataException>(()=>GeoGenerator.Generate(path,"",settings,world,false));
                Assert.IsTrue(world!=null);Assert.IsTrue(world.gameObject.activeInHierarchy);
                var preview=EditorSceneManager.OpenPreviewScene(scenePath);
                try
                {
                    var saved=preview.GetRootGameObjects().Select(g=>g.GetComponent<GeoWorldRoot>()).First(w=>w!=null);
                    Assert.That(saved.terrain.terrainData.GetAlphamaps(22,27,1,1)[0,0,6],Is.GreaterThan(0.9));
                    Assert.That(saved.GetComponentInChildren<GeoPath>().width,Is.EqualTo(8));
                    Assert.That(saved.GetComponentInChildren<GeoPath>().GetComponentsInChildren<MeshFilter>(),Is.Empty);
                }
                finally{EditorSceneManager.ClosePreviewScene(preview);}
            }
            finally
            {
                if(world!=null)Object.DestroyImmediate(world.gameObject);
                foreach(string path in generated)AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
