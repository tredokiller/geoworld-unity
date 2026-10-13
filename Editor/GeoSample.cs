using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GeoWorld.Editor
{
    public static class GeoSample
    {
        public const string Folder=GeoPaths.Project+"/Samples/RiversideVillage";
        public const string ScenePath=GeoPaths.Project+"/Scenes/RiversideSample.unity";
        // the Geo World window's sample button
        public static void CreateScene()
        {
            if(Application.isPlaying)throw new InvalidOperationException("Stop Play mode before creating the sample.");
            // Opening an existing sample never overwrites edits to it.
            if(File.Exists(ScenePath))
            {
                if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
                EditorSceneManager.OpenScene(ScenePath);return;
            }
            if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
            WritePackage();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            // a made-up village: real imagery of the spot would not match it, so it keeps the blended ground
            var world=GeoGenerator.Generate(Folder+"/environment.json",Folder+"/buildings.json",new GenerationSettings{satelliteImagery=false});
            SetupView(world);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));AssetDatabase.Refresh();
            EditorSceneManager.SaveScene(scene,ScenePath);GeoWorldWindow.Frame(world);
        }

        public static void SetupView(GeoWorldRoot world)
        {
            var light=new GameObject("Sun").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.8f;
            light.color=new Color(1,0.94f,0.83f);light.shadows=LightShadows.Soft;light.transform.rotation=Quaternion.Euler(48,-35,0);
            RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(0.56f,0.65f,0.73f);RenderSettings.ambientEquatorColor=new Color(0.49f,0.53f,0.48f);
            RenderSettings.ambientGroundColor=new Color(0.30f,0.32f,0.28f);RenderSettings.fog=false;
            var camera=new GameObject("Main Camera").AddComponent<Camera>();camera.tag="MainCamera";
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(0.10f,0.16f,0.20f);
            camera.fieldOfView=48;camera.nearClipPlane=0.3f;camera.farClipPlane=15000;camera.allowHDR=true;
            camera.gameObject.AddComponent<AudioListener>();
            var controller=camera.gameObject.AddComponent<GeoPreviewCamera>();
            var size=world.terrain.terrainData.size;var position=world.terrain.transform.position;
            controller.focus=new Vector3(position.x+size.x/2,world.terrain.SampleHeight(position+size/2)+position.y,position.z+size.z/2);
            controller.moveSpeed=Mathf.Max(size.x,size.z)*0.2f;
            FitView(controller,camera,world.terrain);controller.Apply();
        }

        public static void FitView(GeoPreviewCamera controller,Camera camera,Terrain terrain)
        {
            var bounds=new Bounds(terrain.transform.position+terrain.terrainData.size*0.5f,terrain.terrainData.size+Vector3.up*30);
            Quaternion inverse=Quaternion.Inverse(Quaternion.Euler(controller.pitch,controller.yaw,0));
            float tangent=Mathf.Tan(camera.fieldOfView*0.5f*Mathf.Deg2Rad),distance=20;
            foreach(float x in new[]{bounds.min.x,bounds.max.x})foreach(float y in new[]{bounds.min.y,bounds.max.y})foreach(float z in new[]{bounds.min.z,bounds.max.z})
            {
                var p=inverse*(new Vector3(x,y,z)-controller.focus);
                distance=Mathf.Max(distance,Mathf.Abs(p.y)/tangent-p.z,Mathf.Abs(p.x)/(tangent*camera.aspect)-p.z);
            }
            controller.distance=distance*1.08f;
        }

        public static void WritePackage()
        {
            Directory.CreateDirectory(Folder);
            var origin=new GeoOrigin{lat=49.834,lon=36.6865};
            var bounds=new GeoBounds{minX=-240,minZ=-180,maxX=240,maxZ=180};
            var env=new EnvironmentData{format="geo-environment",version=1,origin=origin,bounds=bounds};
            LineFeature Road(string id,string name,float width,string material,params float[] points)=>new LineFeature{id=id,name=name,width=width,material=material,type="residential",kind="road",points=points};
            env.roads.Add(Road("sample/main-west","Village avenue",8,"asphalt",-240,-30,-130,-30,-40,-30,20,-30));
            var bridge=Road("sample/bridge","River bridge",8,"asphalt",20,-30,90,-30);bridge.bridge=true;env.roads.Add(bridge);
            env.roads.Add(Road("sample/main-east","Farm road",8,"asphalt",90,-30,160,-30,240,-30));
            env.roads.Add(Road("sample/north-south","Market street",6,"asphalt",-130,-180,-130,-90,-130,-30,-130,70,-130,145));
            env.roads.Add(Road("sample/upper","Orchard street",5.5f,"asphalt",-240,70,-130,70,-65,70,-42,90,-36,150));
            env.roads.Add(Road("sample/south","Garden lane",4,"gravel",-230,-90,-130,-90,-50,-90,-18,-115,-5,-180));
            env.roads.Add(Road("sample/footpath","Riverside path",2,"path",-18,-180,0,-115,8,-70,10,-30,15,20,30,65,44,115,45,180));
            env.roads.Add(Road("sample/field","Field track",3,"dirt_road",160,-30,160,35,172,90,180,180));
            var river=new List<float>();for(int z=-180;z<=180;z+=12){river.Add(70+Mathf.Sin(z/60f)*17);river.Add(z);}
            env.waterways.Add(new LineFeature{id="sample/river",name="Willow river",type="river",material="water",width=15,points=river.ToArray()});
            float[] Ellipse(float cx,float cz,float rx,float rz,int count)
            {var ring=new List<float>();for(int i=0;i<count;i++){float a=i*Mathf.PI*2/count;ring.Add(cx+Mathf.Cos(a)*rx);ring.Add(cz+Mathf.Sin(a)*rz);}return ring.ToArray();}
            env.water.Add(new AreaFeature{id="sample/pond",name="Island pond",material="water",rings=new[]{Ellipse(-2,115,28,32,40),Ellipse(-3,115,7,9,16)}});
            env.areas.Add(new AreaFeature{id="sample/field-a",material="cropland",rings=new[]{new float[]{110,35,235,35,235,168,130,168}}});
            env.areas.Add(new AreaFeature{id="sample/woods",material="forest",rings=new[]{new float[]{-235,127,-70,127,-52,177,-235,177}}});
            env.areas.Add(new AreaFeature{id="sample/industrial",material="urban",rings=new[]{new float[]{110,-150,230,-150,230,-55,110,-55}}});
            env.areas.Add(new AreaFeature{id="sample/meadow",material="scrub",rings=new[]{new float[]{100,-178,235,-178,235,-158,100,-158}}});
            var buildings=new BuildingsData{format="geo-buildings",version=1,origin=origin,area=new BuildingArea{bounds=bounds}};
            int id=0;
            foreach(float z in new[]{-57f,-4f,46f,99f})foreach(float x in new[]{-212f,-179f,-149f,-109f,-77f})
                buildings.buildings.Add(new BuildingFeature{id="sample/house/"+ ++id,category="house",x=x,z=z,width=10+(id%3),length=14+(id%2)*3,height=6,rotation=id%2==0?90:0});
            foreach(float x in new[]{-202f,-165f,-102f,-65f})
                buildings.buildings.Add(new BuildingFeature{id="sample/house/"+ ++id,category="house",x=x,z=-116,width=12,length=17,height=7,rotation=90});
            buildings.buildings.Add(new BuildingFeature{id="sample/apartment/1",category="apartments",x=-196,z=-156,width=16,length=45,height=15,rotation=90});
            buildings.buildings.Add(new BuildingFeature{id="sample/apartment/2",category="apartments",x=-74,z=-157,width=15,length=38,height=12,rotation=90});
            buildings.buildings.Add(new BuildingFeature{id="sample/hall",name="Village hall",category="public",x=-188,z=24,width=13,length=30,height=8,rotation=90});
            buildings.buildings.Add(new BuildingFeature{id="sample/warehouse/1",category="industrial",x=140,z=-100,width=25,length=45,height=10,rotation=0});
            buildings.buildings.Add(new BuildingFeature{id="sample/warehouse/2",category="industrial",x=196,z=-100,width=28,length=52,height=12,rotation=0});
            foreach(var b in buildings.buildings)
            {
                b.y=Height(b.x,b.z);var corners=new List<float>();var r=Quaternion.Euler(0,b.rotation,0);
                foreach(var p in new[]{new Vector3(-b.width/2,0,-b.length/2),new Vector3(b.width/2,0,-b.length/2),new Vector3(b.width/2,0,b.length/2),new Vector3(-b.width/2,0,b.length/2)})
                {var q=r*p;corners.Add(q.x);corners.Add(q.z);}b.footprint=corners.ToArray();
            }
            var random=new System.Random(41);
            for(int i=0;i<48;i++)
            {float x=-230+(float)random.NextDouble()*145,z=133+(float)random.NextDouble()*40;env.trees.Add(new TreeFeature{id="sample/tree/"+i,x=x,z=z,height=6+(float)random.NextDouble()*4});}
            for(int z=-160;z<=160;z+=22)
                env.trees.Add(new TreeFeature{id="sample/bank-tree/"+z,x=70+Mathf.Sin(z/60f)*17-16,z=z,height=6});
            const int w=241,h=181;var hm=new HeightmapInfo{file="heightmap.r16",encoding="uint16-le",rowOrder="north-to-south",width=w,height=h,cellSize=2,minX=-240,maxZ=180,minHeight=-4,maxHeight=16,originGround=0};
            env.terrain=new TerrainInfo{heightmap=hm};
            using(var stream=new BinaryWriter(File.Open(Path.Combine(Folder,hm.file),FileMode.Create)))
                for(int row=0;row<h;row++)for(int col=0;col<w;col++)
                    stream.Write((ushort)Mathf.RoundToInt(Mathf.InverseLerp(hm.minHeight,hm.maxHeight,Height(hm.minX+col*2,hm.maxZ-row*2))*65535));
            env.materials=new MaterialMapInfo{file="materials.png",width=480,height=360,cellSize=1,minX=-240,maxZ=180,classes=new List<MaterialClass>{
                new MaterialClass{id=1,name="grass"},new MaterialClass{id=2,name="cropland"},new MaterialClass{id=3,name="forest"},new MaterialClass{id=4,name="scrub"},new MaterialClass{id=10,name="urban"}}};
            var map=new Texture2D(480,360,TextureFormat.RGB24,false,true);var pixels=new Color32[480*360];
            for(int z=0;z<360;z++)for(int x=0;x<480;x++)
            {
                byte value=1;
                foreach(var area in env.areas)if(GeoData.InPolygon(x-239.5f,z-179.5f,area.rings))value=area.material=="cropland"?(byte)2:area.material=="forest"?(byte)3:area.material=="urban"?(byte)10:(byte)4;
                pixels[z*480+x]=new Color32(value,value,value,255);
            }
            map.SetPixels32(pixels);map.Apply();File.WriteAllBytes(Path.Combine(Folder,"materials.png"),map.EncodeToPNG());UnityEngine.Object.DestroyImmediate(map);
            File.WriteAllText(Folder+"/environment.json",JsonConvert.SerializeObject(env,Formatting.Indented));
            File.WriteAllText(Folder+"/buildings.json",JsonConvert.SerializeObject(buildings,Formatting.Indented));
            AssetDatabase.Refresh();
        }
        static float Height(float x,float z)=>4+0.008f*x+1.7f*Mathf.Sin(x/90)*Mathf.Cos(z/120)+Mathf.PerlinNoise(x/170+10,z/170+10)*1.2f;
    }
}
