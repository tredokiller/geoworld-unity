using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace GeoWorld.Editor
{
    public static class GeoPropBuilder
    {
        // bake: null decides from the settings (a merged city bakes its props into chunks, see GeoPropBaker).
        public static void Build(GeoWorldRoot root,EnvironmentData env,BuildingsData source,HeightField field,
            List<string> notes,Action<Object> save,GeoWorldRoot previous=null,GeoWorldRoot footprintRoot=null,bool? bake=null)
        {
            var settings=root.settings;
            bool baked=bake??(settings.mergeProps&&GeoCityBuilder.ShouldMerge(settings,source.buildings.Count));
            var group=new GameObject("Props and vegetation").transform;group.SetParent(root.transform,false);
            var kept=previous?previous.GetComponentsInChildren<GeoPlacedProp>(true).Where(p=>p.keepOnRebuild).ToArray():new GeoPlacedProp[0];
            var buildings=new BuildingsData();buildings.buildings.AddRange(source.buildings);
            foreach(var b in (footprintRoot?footprintRoot:root).GetComponentsInChildren<GeoBuilding>())
            {
                buildings.buildings.RemoveAll(v=>v.id==b.sourceId);
                var center=b.transform.position;
                buildings.buildings.Add(new BuildingFeature{id=b.sourceId,x=center.x,z=center.z,width=10,length=10,
                    footprint=b.footprint.SelectMany(p=>{var q=b.transform.TransformPoint(new Vector3(p.x,0,p.y))-center;return new[]{q.x,q.z};}).ToArray()});
            }
            var canopy=settings.vegetation&&settings.trees?CanopyMask.Load(env,root.environmentPath,notes):null;
            if(settings.vegetation&&settings.trees&&canopy==null)notes.Add("No usable canopy mask: vegetation uses mapped trees and tree rows only.");
            var plan=GeoPropPlanner.Plan(env,buildings,settings,canopy,notes,kept,baked);
            var visuals=new PreviewModels(save);
            var groups=new Dictionary<string,Transform>();int previewCount=0,prefabCount=0;
            var toBake=new List<PropPlacement>();
            // a merged world's trees are GPU instances of a shared model, not copies of a mesh
            var terrainTrees=new List<PropPlacement>();
            // your tree prefabs: the terrain's trees in a merged world, a copy per tree in an editable one
            var custom=settings.trees?GeoTerrainTrees.Usable(settings.treePrefabs,notes):new GameObject[0];
            var customHeights=System.Array.ConvertAll(custom,GeoTerrainTrees.Height);
            Transform Parent(string name)
            {if(!groups.TryGetValue(name,out var t)){t=new GameObject(name).transform;t.SetParent(group,false);groups[name]=t;}return t;}
            foreach(var old in kept)
            {
                var clone=Object.Instantiate(old.gameObject,Parent("Kept edits"));clone.name=old.name;
                clone.transform.SetPositionAndRotation(old.transform.position,old.transform.rotation);
                clone.transform.localScale=old.transform.lossyScale;
                root.propCount++;if(old.kind=="tree")root.treeCount++;
            }
            foreach(var p in plan)
            {
                p.position.y=field.Sample(p.position.x,p.position.z);
                bool customTree=p.kind=="tree"&&custom.Length>0&&p.source!="manual_photo";
                if(baked&&(customTree||GeoPropBaker.Bakes(p,settings))){if(p.kind=="tree")terrainTrees.Add(p);else toBake.Add(p);continue;}
                var go=new GameObject(p.kind.Replace('_',' ')+" | "+p.id);
                go.transform.SetParent(Parent(p.source=="generated"?"Generated decoration":p.source=="manual_photo"?"Photo reference objects":"Mapped objects"),false);
                go.transform.SetPositionAndRotation(p.position,Quaternion.Euler(0,p.yaw,0));
                var marker=go.AddComponent<GeoPlacedProp>();marker.sourceId=p.id;marker.kind=p.kind;
                marker.placementSource=p.source;marker.evidence=p.evidence;marker.rotationSource=p.rotationSource;
                marker.photoReference=p.photoReference;
                var entry=settings.propCatalog?settings.propCatalog.Find(p.kind):null;
                if(customTree)
                {
                    GeoTerrainTrees.Pick(p,settings.decorationSeed,customHeights,out int index,out float scale);
                    var model=(GameObject)PrefabUtility.InstantiatePrefab(custom[index],go.transform);
                    model.transform.localPosition=Vector3.zero;model.transform.localRotation=Quaternion.identity;
                    model.transform.localScale=custom[index].transform.localScale*scale;
                    prefabCount++;
                }
                else if(entry!=null)
                {
                    var model=(GameObject)PrefabUtility.InstantiatePrefab(entry.prefab,go.transform);
                    if(!model)throw new InvalidOperationException("Prop catalog entry must reference a prefab asset: "+p.kind);
                    model.transform.localPosition=Vector3.zero;model.transform.localRotation=Quaternion.Euler(0,entry.yawOffset,0);
                    float scale=GeoData.Finite(entry.scale)&&entry.scale>0?entry.scale:1;
                    model.transform.localScale=entry.prefab.transform.localScale*scale;
                    if(p.segment)
                    {float reference=GeoData.Finite(entry.segmentLength)&&entry.segmentLength>0?entry.segmentLength:2;
                        var s=model.transform.localScale;s.z*=p.length/(reference*scale);model.transform.localScale=s;}
                    prefabCount++;
                }
                else {visuals.Attach(go,p);previewCount++;}
                root.propCount++;if(p.kind=="tree")root.treeCount++;
            }
            if(root.terrain)GeoTerrainTrees.ClearTerrainTrees(root.terrain);
            // with Nature Renderer chosen (and in the project) the trees are the terrain's own, and Nature Renderer draws them
            bool nature=settings.natureRenderer&&root.terrain&&GeoNatureRendererTrees.Installed;
            if(settings.natureRenderer&&!nature&&terrainTrees.Count>0)notes.Add("Nature Renderer is not in this project: the trees are drawn without it.");
            if(root.terrain&&!nature)GeoNatureRendererTrees.Remove(root.terrain);
            int planted;
            if(custom.Length>0&&root.terrain)
            {
                // your prefabs as the terrain's trees: Unity's terrain draws them, or Nature Renderer
                planted=GeoTerrainTrees.PlantCustom(root.terrain,terrainTrees,settings.decorationSeed,custom);
                if(nature&&planted>0){GeoNatureRendererTrees.Prepare(root.terrain);GeoNatureRendererTrees.CheckPrefabs(custom,notes);}
                else root.terrain.drawTreesAndFoliage=true;
            }
            else planted=nature?GeoNatureRendererTrees.Plant(root.terrain,terrainTrees,settings.decorationSeed):GeoTerrainTrees.Plant(group,terrainTrees,settings.decorationSeed);
            root.propCount+=planted;root.treeCount+=planted;
            int bakedChunks=0;
            if(toBake.Count>0)
            {
                var palette=GeoPropBaker.PaletteOf((footprintRoot?footprintRoot:root).transform,save);
                bakedChunks=GeoPropBaker.Build(toBake,Parent("Baked props and vegetation"),settings.chunkSize,palette,save,settings.decorationSeed);
                root.propCount+=toBake.Count;root.treeCount+=toBake.Count(p=>p.kind=="tree");
            }
            notes.Add(root.propCount+" props / vegetation ("+prefabCount+" prefab instances, "+previewCount+" preview models, "+toBake.Count+" baked into "+bakedChunks+
                " chunks, "+planted+(custom.Length>0?" terrain trees from "+custom.Length+" tree prefabs"+(nature?" drawn by Nature Renderer, ":", "):
                nature?" terrain trees drawn by Nature Renderer, ":" instanced trees, ")+kept.Length+" kept edits). Mapped locations do not imply surveyed appearance; generated trees are labeled.");
        }

        public static void Rebuild(GeoWorldRoot world)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Stop Play mode before rebuilding props.");
            if(!world || !world.terrain || string.IsNullOrEmpty(world.environmentPath))
                throw new InvalidOperationException("Import a terrain and ground package first.");
            if(world.transform.position!=Vector3.zero || world.transform.rotation!=Quaternion.identity || world.transform.lossyScale!=Vector3.one)
                throw new InvalidOperationException("Reset the world's transform before rebuilding props.");
            var env=GeoData.ReadEnvironment(world.environmentPath);var buildings=GeoData.ReadBuildings(world.buildingsPath,env.origin);
            // Read edited path controls, so vegetation avoids roads after the user has moved them.
            foreach(var path in world.GetComponentsInChildren<GeoPath>())
            {
                var list=path.river?env.waterways:env.roads;
                var feature=list.FirstOrDefault(r=>r.id==path.sourceId);
                if(feature!=null){feature.points=path.points.SelectMany(p=>new[]{p.x,p.z}).ToArray();feature.width=path.width;}
            }
            var terrain=world.terrain.terrainData;int resolution=terrain.heightmapResolution;
            var field=new HeightField(env.bounds,resolution);var heights=terrain.GetHeights(0,0,resolution,resolution);
            for(int z=0;z<resolution;z++)for(int x=0;x<resolution;x++)field.heights[z,x]=heights[z,x]*terrain.size.y+world.terrain.transform.position.y;
            var notes=new List<string>();GameObject staging=null;string assetPath=null;
            try
            {
                staging=new GameObject("Props staging");var temp=staging.AddComponent<GeoWorldRoot>();
                temp.settings=world.settings;temp.environmentPath=world.environmentPath;temp.terrain=world.terrain;
                var assets=ScriptableObject.CreateInstance<GeoGenerationAssets>();
                assetPath=AssetDatabase.GenerateUniqueAssetPath(GeoGenerator.OutputFolder+"/Props.asset");AssetDatabase.CreateAsset(assets,assetPath);
                Build(temp,env,buildings,field,notes,obj=>AssetDatabase.AddObjectToAsset(obj,assetPath),world,world);
                assets.summary=string.Join("\n",notes);EditorUtility.SetDirty(assets);AssetDatabase.SaveAssets();
                var replacement=staging.transform.Find("Props and vegetation");
                var old=world.transform.Find("Props and vegetation");var legacy=world.transform.Find("Mapped trees");
                Undo.IncrementCurrentGroup();int undo=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Rebuild props and vegetation");
                Undo.RecordObject(world,"Update prop counts");
                replacement.SetParent(world.transform,true);Undo.RegisterCreatedObjectUndo(replacement.gameObject,"Rebuild props and vegetation");
                if(old)Undo.DestroyObjectImmediate(old.gameObject);if(legacy)Undo.DestroyObjectImmediate(legacy.gameObject);
                world.treeCount=temp.treeCount;world.propCount=temp.propCount;
                if(world.tile) GeoTreeDataExporter.Export(world);
                world.report=world.roadCount+" roads | "+world.buildingCount+" buildings | "+world.treeCount+" trees | "+world.propCount+" props\n"+string.Join("\n",notes);
                Undo.CollapseUndoOperations(undo);EditorUtility.SetDirty(world);EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            }
            catch {if(!string.IsNullOrEmpty(assetPath))AssetDatabase.DeleteAsset(assetPath);throw;}
            finally {if(staging)Object.DestroyImmediate(staging);}
        }

        internal static readonly Color Metal=new Color(0.23f,0.28f,0.29f),Wood=new Color(0.37f,0.23f,0.12f),
            Leaf=new Color(0.21f,0.37f,0.13f),Concrete=new Color(0.53f,0.51f,0.46f),Blue=new Color(0.15f,0.36f,0.65f),
            Light=new Color(0.93f,0.88f,0.68f);

        internal static void AddBox(MeshDraft d,float x,float y,float z,float w,float h,float l)
        {
            float a=x-w/2,b=x+w/2,c=y-h/2,e=y+h/2,f=z-l/2,g=z+l/2;
            d.Quad(new Vector3(a,c,f),new Vector3(a,e,f),new Vector3(b,e,f),new Vector3(b,c,f));
            d.Quad(new Vector3(b,c,g),new Vector3(b,e,g),new Vector3(a,e,g),new Vector3(a,c,g));
            d.Quad(new Vector3(a,c,g),new Vector3(a,e,g),new Vector3(a,e,f),new Vector3(a,c,f));
            d.Quad(new Vector3(b,c,f),new Vector3(b,e,f),new Vector3(b,e,g),new Vector3(b,c,g));
            d.Quad(new Vector3(a,e,f),new Vector3(a,e,g),new Vector3(b,e,g),new Vector3(b,e,f));
            d.Quad(new Vector3(a,c,g),new Vector3(a,c,f),new Vector3(b,c,f),new Vector3(b,c,g));
        }

        // The geometry of a preview model, one draft per colour, before it becomes a mesh.
        internal static Dictionary<Color,MeshDraft> Drafts(string kind)
        {
            var drafts=new Dictionary<Color,MeshDraft>();
            MeshDraft Draft(Color c){if(!drafts.TryGetValue(c,out var d))drafts[c]=d=new MeshDraft();return d;}
            void Box(Color color,float x,float y,float z,float w,float h,float l)=>AddBox(Draft(color),x,y,z,w,h,l);
            void Post(float x,float z,float h,Color c,float width=0.1f)=>Box(c,x,h/2,z,width,h,width);
            Color metal=Metal,wood=Wood,leaf=Leaf,concrete=Concrete,blue=Blue,light=Light;
            switch(kind)
            {
                case "tree":
                    Post(0,0,4,wood,0.3f);
                    var d=Draft(leaf);
                    Vector3 Crown(int i,int j){float a=i*Mathf.PI/4,v=-Mathf.PI/2+j*Mathf.PI/5;return new Vector3(Mathf.Cos(a)*Mathf.Cos(v)*2,4.8f+Mathf.Sin(v)*2.2f,Mathf.Sin(a)*Mathf.Cos(v)*2);}
                    for(int j=0;j<5;j++)for(int i=0;i<8;i++)d.Quad(Crown(i,j),Crown(i,j+1),Crown(i+1,j+1),Crown(i+1,j));
                    break;
                case "hedge": Box(leaf,0,0.5f,0,1,1,1);break;
                case "wall": Box(concrete,0,0.5f,0,1,1,1);break;
                case "fence": case "guard_rail":
                    Box(metal,0,0.5f,-0.48f,1,1,0.05f);Box(metal,0,0.5f,0.48f,1,1,0.05f);
                    Box(metal,0,0.8f,0,0.6f,0.08f,1);Box(metal,0,0.3f,0,0.6f,0.08f,1);
                    if(kind=="fence")for(int k=1;k<5;k++)Box(metal,0,0.5f,-0.5f+k/5f,0.35f,0.8f,0.018f);
                    break;
                case "bench":
                    Box(wood,0,0.48f,0,1.7f,0.12f,0.5f);Box(wood,0,0.83f,-0.22f,1.7f,0.5f,0.1f);
                    foreach(float x in new[]{-0.65f,0.65f})foreach(float z in new[]{-0.15f,0.15f})Post(x,z,0.45f,metal,0.07f);
                    break;
                case "street_lamp":
                    Post(0,0,6,metal,0.14f);Box(metal,0,5.9f,0.55f,0.12f,0.12f,1.2f);Box(light,0,5.85f,1.1f,0.38f,0.18f,0.7f);break;
                case "power_pole":
                    Post(0,0,8,concrete,0.25f);Box(metal,0,7,0,1.8f,0.12f,0.15f);
                    foreach(float x in new[]{-0.7f,0f,0.7f})Box(light,x,7.2f,0,0.12f,0.3f,0.12f);break;
                case "bus_stop": case "post_box":
                    Post(0,0,kind=="bus_stop"?2.7f:1.5f,metal);
                    Box(blue,0,kind=="bus_stop"?2.4f:1.2f,0,0.55f,0.6f,kind=="bus_stop"?0.08f:0.35f);
                    Box(light,0,kind=="bus_stop"?2.4f:1.25f,kind=="bus_stop"?0.045f:0.18f,0.35f,0.08f,0.01f);break;
                case "shelter":
                    foreach(float x in new[]{-1.5f,1.5f})foreach(float z in new[]{-0.7f,0.7f})Post(x,z,2.4f,metal);
                    Box(metal,0,2.4f,0,3.3f,0.15f,1.8f);Box(blue,0,1.2f,-0.7f,3,2.3f,0.06f);
                    Box(wood,0,0.5f,-0.35f,2.4f,0.12f,0.45f);break;
                case "traffic_signals":
                    Post(0,0,3,metal);Box(metal,0,2.65f,0.1f,0.3f,0.9f,0.3f);
                    Box(Color.red,0,2.93f,0.26f,0.19f,0.19f,0.01f);Box(light,0,2.65f,0.26f,0.19f,0.19f,0.01f);Box(leaf,0,2.37f,0.26f,0.19f,0.19f,0.01f);break;
                case "gate":
                    Post(-1.5f,0,1.5f,metal);Post(1.5f,0,1.5f,metal);
                    Box(metal,0,0.9f,0,3,0.08f,0.08f);Box(metal,0,0.4f,0,3,0.08f,0.08f);break;
                case "bicycle_parking":
                    foreach(float x in new[]{-0.6f,0,0.6f}){Post(x,-0.35f,0.8f,metal,0.06f);Post(x,0.35f,0.8f,metal,0.06f);Box(metal,x,0.8f,0,0.06f,0.06f,0.7f);}break;
                case "bollard":Post(0,0,0.9f,metal,0.15f);Box(light,0,0.75f,0,0.16f,0.08f,0.16f);break;
                case "waste_disposal":Box(leaf,0,0.6f,0,1.4f,1.2f,0.9f);Box(metal,0,1.22f,0,1.5f,0.12f,1);break;
                default:Box(leaf,0,0.45f,0,0.45f,0.9f,0.45f);Box(metal,0,0.92f,0,0.5f,0.08f,0.5f);break;
            }
            return drafts;
        }

        // Small shared meshes make the feature usable before a project has a prop asset library.
        // One mesh per kind, with shared materials, instead of dozens of primitive children per prop.
        class PreviewModels
        {
            class Model { public Mesh mesh; public Material[] materials; }
            readonly Dictionary<string,Model> models=new Dictionary<string,Model>();
            readonly Dictionary<Color,Material> materials=new Dictionary<Color,Material>();
            readonly Action<Object> save;
            public PreviewModels(Action<Object> save){this.save=save;}
            Material Material(Color color)
            {
                if(materials.TryGetValue(color,out var material))return material;
                material=new Material(Shader.Find("Universal Render Pipeline/Lit")??Shader.Find("Standard")){name="Prop preview "+ColorUtility.ToHtmlStringRGB(color),color=color,enableInstancing=true};
                if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",color);
                materials[color]=material;save(material);return material;
            }
            public void Attach(GameObject go,PropPlacement p)
            {
                if(!models.TryGetValue(p.kind,out var model)){model=Create(p.kind);models[p.kind]=model;}
                go.AddComponent<MeshFilter>().sharedMesh=model.mesh;go.AddComponent<MeshRenderer>().sharedMaterials=model.materials;
                if(p.kind=="tree")go.transform.localScale=new Vector3(p.width/4,p.height/7,p.width/4);
                else if(p.segment)go.transform.localScale=new Vector3(p.width,p.height,p.length);
                else
                {
                    var size=model.mesh.bounds.size;
                    go.transform.localScale=new Vector3(p.width>0&&GeoData.Finite(p.width)?p.width/Mathf.Max(size.x,0.01f):1,
                        p.height>0&&GeoData.Finite(p.height)?p.height/Mathf.Max(size.y,0.01f):1,1);
                }
                // Trees have a trunk collider; other preview objects use their actual shared mesh.
                if(p.kind=="tree")
                {var c=go.AddComponent<CapsuleCollider>();c.center=new Vector3(0,2,0);c.height=4;c.radius=0.2f;}
                else go.AddComponent<MeshCollider>().sharedMesh=model.mesh;
                go.isStatic=true;
            }
            Model Create(string kind)
            {
                var drafts=Drafts(kind);
                var mesh=new Mesh{name="Preview "+kind,indexFormat=IndexFormat.UInt32};var vertices=new List<Vector3>();var uv=new List<Vector2>();
                var indices=new List<int[]>();var mats=new List<Material>();
                foreach(var pair in drafts)
                {int offset=vertices.Count;vertices.AddRange(pair.Value.vertices);uv.AddRange(pair.Value.uv);indices.Add(pair.Value.triangles.Select(i=>i+offset).ToArray());mats.Add(Material(pair.Key));}
                mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.subMeshCount=indices.Count;
                for(int i=0;i<indices.Count;i++)mesh.SetTriangles(indices[i],i);mesh.RecalculateNormals();mesh.RecalculateBounds();save(mesh);
                return new Model{mesh=mesh,materials=mats.ToArray()};
            }
        }
    }
}
