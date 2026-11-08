using System;
using System.Collections.Generic;
using System.Linq;
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
                // its category stays: the power lines look for houses (GeoPowerLines.Candidates)
                string category=buildings.buildings.Find(v=>v.id==b.sourceId)?.category;
                buildings.buildings.RemoveAll(v=>v.id==b.sourceId);
                var center=b.transform.position;
                buildings.buildings.Add(new BuildingFeature{id=b.sourceId,category=category,x=center.x,z=center.z,width=10,length=10,
                    footprint=b.footprint.SelectMany(p=>{var q=b.transform.TransformPoint(new Vector3(p.x,0,p.y))-center;return new[]{q.x,q.z};}).ToArray()});
            }
            var canopy=settings.vegetation&&settings.trees?CanopyMask.Load(env,root.environmentPath,notes):null;
            if(settings.vegetation&&settings.trees&&canopy==null)notes.Add("No usable canopy mask: vegetation uses mapped trees and tree rows only.");
            var beyond=new List<PropPlacement>();
            var plan=GeoPropPlanner.Plan(env,buildings,settings,canopy,notes,kept,baked,beyond);
            // after the battle: where there are craters or damaged buildings (their states are the source's, not the
            // footprints of the buildings already in the scene); the poles past the bounds the same way, for the wires
            var war=new GeoPropDamage.War(env,source,settings.applyCondition);
            var gone=new List<PropPlacement>();var beyondGone=new List<PropPlacement>();
            GeoPropDamage.Apply(plan,war,settings,notes,gone);
            GeoPropDamage.Apply(beyond,war,settings,null,beyondGone);
            var visuals=new PreviewModels(save,settings.decorationSeed);
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
                var rotation=GeoPropDamage.Rotation(p);
                go.transform.SetPositionAndRotation(p.position,rotation);
                var marker=go.AddComponent<GeoPlacedProp>();marker.sourceId=p.id;marker.kind=p.kind;
                marker.placementSource=p.source;marker.evidence=p.evidence;marker.rotationSource=p.rotationSource;
                marker.photoReference=p.photoReference;marker.damage=p.damage;
                var entry=settings.propCatalog?settings.propCatalog.Find(p.kind):null;
                if(customTree)
                {
                    GeoTerrainTrees.Pick(p,settings.decorationSeed,customHeights,out int index,out float scale);
                    var model=GeoGeneration.Target.Instantiate(custom[index],go.transform);
                    model.transform.localPosition=Vector3.zero;model.transform.localRotation=Quaternion.identity;
                    model.transform.localScale=custom[index].transform.localScale*scale;
                    prefabCount++;
                }
                else if(entry!=null)
                {
                    var model=GeoGeneration.Target.Instantiate(entry.prefab,go.transform);
                    if(!model)throw new InvalidOperationException("Prop catalog entry must reference a prefab asset: "+p.kind);
                    model.transform.localPosition=Vector3.zero;model.transform.localRotation=Quaternion.Euler(0,entry.yawOffset,0);
                    float scale=GeoData.Finite(entry.scale)&&entry.scale>0?entry.scale:1;
                    model.transform.localScale=entry.prefab.transform.localScale*scale;
                    if(p.segment)
                    {float reference=GeoData.Finite(entry.segmentLength)&&entry.segmentLength>0?entry.segmentLength:2;
                        var s=model.transform.localScale;s.z*=p.length/(reference*scale);model.transform.localScale=s;}
                    // knocked over after the battle: on the ground, not in it (as the preview and baked models)
                    if(p.lean>45)
                    {
                        var renderers=model.GetComponentsInChildren<Renderer>();
                        if(renderers.Length>0)
                        {
                            var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
                            go.transform.position+=Vector3.up*Mathf.Max(0,p.position.y-bounds.min.y-0.03f);
                        }
                    }
                    prefabCount++;
                }
                else {visuals.Attach(go,p,rotation);previewCount++;}
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
            // the wires of the generated power lines, hung from what the war left of their poles
            if(plan.Any(p=>p.kind=="power_pole"&&p.source=="generated")||gone.Any(p=>p.kind=="power_pole"))
                GeoPowerLines.Build(plan,gone,beyond,beyondGone,war,field,Parent("Power lines"),settings.chunkSize,save,settings.decorationSeed,notes);
            notes.Add(root.propCount+" props / vegetation ("+prefabCount+" prefab instances, "+previewCount+" preview models, "+toBake.Count+" baked into "+bakedChunks+
                " chunks, "+planted+(custom.Length>0?" terrain trees from "+custom.Length+" tree prefabs"+(nature?" drawn by Nature Renderer, ":", "):
                nature?" terrain trees drawn by Nature Renderer, ":" instanced trees, ")+kept.Length+" kept edits). Mapped locations do not imply surveyed appearance; generated trees are labeled.");
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

        // A box of w × h × l about its centre, turned by rotation.
        internal static void AddBox(MeshDraft d,Vector3 centre,Quaternion rotation,float w,float h,float l)
        {
            Vector3 P(float x,float y,float z)=>centre+rotation*new Vector3(x*w/2,y*h/2,z*l/2);
            d.Quad(P(-1,-1,-1),P(-1,1,-1),P(1,1,-1),P(1,-1,-1));
            d.Quad(P(1,-1,1),P(1,1,1),P(-1,1,1),P(-1,-1,1));
            d.Quad(P(-1,-1,1),P(-1,1,1),P(-1,1,-1),P(-1,-1,-1));
            d.Quad(P(1,-1,-1),P(1,1,-1),P(1,1,1),P(1,-1,1));
            d.Quad(P(-1,1,-1),P(-1,1,1),P(1,1,1),P(1,1,-1));
            d.Quad(P(-1,-1,1),P(-1,-1,-1),P(1,-1,-1),P(1,-1,1));
        }

        // The geometry of a preview model, one draft per colour, before it becomes a mesh. damage and variant (0..1): the
        // shapes after the battle (GeoPropDamage) — a lamp or pole snapped at its foot, lying along +z with its arm on the
        // ground; a shelter without its panels, or with its roof hanging and a front post knocked down; a bench without its
        // backrest; a gate with its leaf torn off. Other damage is a lean of the whole prop, and a wall's is its section's
        // shape (GeoPropBaker.Segment).
        internal static Dictionary<Color,MeshDraft> Drafts(string kind,int damage=GeoPropDamage.Intact,float variant=0)
        {
            var drafts=new Dictionary<Color,MeshDraft>();
            MeshDraft Draft(Color c){if(!drafts.TryGetValue(c,out var d))drafts[c]=d=new MeshDraft();return d;}
            void Box(Color color,float x,float y,float z,float w,float h,float l)=>AddBox(Draft(color),x,y,z,w,h,l);
            void Post(float x,float z,float h,Color c,float width=0.1f)=>Box(c,x,h/2,z,width,h,width);
            Color metal=Metal,wood=Wood,leaf=Leaf,concrete=Concrete,blue=Blue,light=Light;
            bool broken=damage==GeoPropDamage.Broken,damaged=damage==GeoPropDamage.Damaged;
            float side=variant<0.5f?1:-1;
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
                    Box(wood,0,0.48f,0,1.7f,0.12f,0.5f);
                    // a damaged one has lost its backrest, or half of it
                    if(!damaged)Box(wood,0,0.83f,-0.22f,1.7f,0.5f,0.1f);
                    else if(variant>0.5f)Box(wood,side*0.45f,0.83f,-0.22f,0.8f,0.5f,0.1f);
                    foreach(float x in new[]{-0.65f,0.65f})foreach(float z in new[]{-0.15f,0.15f})Post(x,z,0.45f,metal,0.07f);
                    break;
                case "street_lamp":
                    if(broken)
                    {
                        // snapped at the foot: the stump, the pole lying away from it, its arm and lamp on the ground at its end
                        float stump=0.6f+variant*1.1f,rest=6-stump,end=0.3f+rest;
                        Post(0,0,stump,metal,0.14f);
                        Box(metal,0,0.07f,0.3f+rest/2,0.14f,0.14f,rest);
                        Box(metal,side*0.55f,0.06f,end-0.07f,1.2f,0.12f,0.12f);
                        Box(light,side*1.1f,0.09f,end-0.07f,0.7f,0.18f,0.38f);
                        break;
                    }
                    Post(0,0,6,metal,0.14f);Box(metal,0,5.9f,0.55f,0.12f,0.12f,1.2f);Box(light,0,5.85f,1.1f,0.38f,0.18f,0.7f);break;
                case "power_pole":
                    // the textured pole's far shape (GeoPowerPole draws it; this is its size and a fallback): snapped low when broken,
                    // its top lying along +z
                    GeoPowerPole.Draft(Draft(concrete),damage,variant);break;
                case "bus_stop": case "post_box":
                    Post(0,0,kind=="bus_stop"?2.7f:1.5f,metal);
                    Box(blue,0,kind=="bus_stop"?2.4f:1.2f,0,0.55f,0.6f,kind=="bus_stop"?0.08f:0.35f);
                    Box(light,0,kind=="bus_stop"?2.4f:1.25f,kind=="bus_stop"?0.045f:0.18f,0.35f,0.08f,0.01f);break;
                case "shelter":
                    if(broken)
                    {
                        // the roof down: hanging from the back posts, a front post knocked over, no panels
                        foreach(float x in new[]{-1.5f,1.5f})Post(x,-0.7f,2.4f,metal);
                        var turn=Quaternion.Euler(40+variant*25,0,0);
                        AddBox(Draft(metal),new Vector3(0,2.4f,-0.9f)+turn*new Vector3(0,0,0.9f),turn,3.3f,0.15f,1.8f);
                        Box(metal,side*1.5f,0.05f,1.7f,0.1f,0.1f,2.4f);
                        Box(wood,0,0.5f,-0.35f,2.4f,0.12f,0.45f);
                        break;
                    }
                    foreach(float x in new[]{-1.5f,1.5f})foreach(float z in new[]{-0.7f,0.7f})Post(x,z,2.4f,metal);
                    Box(metal,0,2.4f,0,3.3f,0.15f,1.8f);
                    if(damaged)
                    {
                        // the panels gone: their frame, and a shard left in a corner
                        Box(metal,0,0.12f,-0.7f,3,0.06f,0.06f);Box(metal,0,2.28f,-0.7f,3,0.06f,0.06f);
                        Box(blue,side*(1.05f+variant*0.2f),0.35f+variant*0.5f,-0.7f,0.35f+variant*0.3f,0.4f+variant*0.5f,0.06f);
                    }
                    else Box(blue,0,1.2f,-0.7f,3,2.3f,0.06f);
                    Box(wood,0,0.5f,-0.35f,2.4f,0.12f,0.45f);break;
                case "traffic_signals":
                    Post(0,0,3,metal);Box(metal,0,2.65f,0.1f,0.3f,0.9f,0.3f);
                    Box(Color.red,0,2.93f,0.26f,0.19f,0.19f,0.01f);Box(light,0,2.65f,0.26f,0.19f,0.19f,0.01f);Box(leaf,0,2.37f,0.26f,0.19f,0.19f,0.01f);break;
                case "gate":
                    Post(-1.5f,0,1.5f,metal);Post(1.5f,0,1.5f,metal);
                    if(broken)
                    {
                        // the leaf torn off, on the ground in front of its posts
                        var turn=Quaternion.Euler(0,side*(8+variant*20),0);var at=new Vector3(0,0.04f,0.5f+variant*0.6f);
                        AddBox(Draft(metal),at+turn*new Vector3(0,0,-0.25f),turn,3,0.08f,0.08f);
                        AddBox(Draft(metal),at+turn*new Vector3(0,0,0.25f),turn,3,0.08f,0.08f);
                        break;
                    }
                    Box(metal,0,0.9f,0,3,0.08f,0.08f);Box(metal,0,0.4f,0,3,0.08f,0.08f);break;
                case "bicycle_parking":
                    foreach(float x in new[]{-0.6f,0,0.6f}){Post(x,-0.35f,0.8f,metal,0.06f);Post(x,0.35f,0.8f,metal,0.06f);Box(metal,x,0.8f,0,0.06f,0.06f,0.7f);}break;
                case "bollard":Post(0,0,0.9f,metal,0.15f);Box(light,0,0.75f,0,0.16f,0.08f,0.16f);break;
                case "waste_disposal":Box(leaf,0,0.6f,0,1.4f,1.2f,0.9f);Box(metal,0,1.22f,0,1.5f,0.12f,1);break;
                default:Box(leaf,0,0.45f,0,0.45f,0.9f,0.45f);Box(metal,0,0.92f,0,0.5f,0.08f,0.5f);break;
            }
            return drafts;
        }

        // The size of a kind's intact model: a broken one is scaled as the intact one would be.
        internal static Vector3 IntactSize(string kind)
        {
            Vector3 min=Vector3.one*float.MaxValue,max=Vector3.one*float.MinValue;
            foreach(var draft in Drafts(kind).Values)foreach(var v in draft.vertices){min=Vector3.Min(min,v);max=Vector3.Max(max,v);}
            return min.x>max.x?Vector3.one:max-min;
        }

        // A prop's scale from its width and height (a broken lamp or pole lying along z takes its height as its length).
        internal static Vector3 Scale(PropPlacement p,Vector3 intact)
        {
            float sx=p.width>0&&GeoData.Finite(p.width)?p.width/Mathf.Max(intact.x,0.01f):1;
            float sy=p.height>0&&GeoData.Finite(p.height)?p.height/Mathf.Max(intact.y,0.01f):1;
            return GeoPropDamage.Fallen(p)?new Vector3(sx,1,sy):new Vector3(sx,sy,1);
        }

        // Small shared meshes make the feature usable before a project has a prop asset library.
        // One mesh per kind (and broken shape), with shared materials, instead of dozens of primitive children per prop.
        class PreviewModels
        {
            class Model { public Mesh mesh; public Material[] materials; }
            readonly Dictionary<string,Model> models=new Dictionary<string,Model>();
            readonly Dictionary<string,Vector3> sizes=new Dictionary<string,Vector3>();
            readonly Dictionary<Color,Material> materials=new Dictionary<Color,Material>();
            readonly Action<Object> save;
            readonly int seed;
            readonly GeoPowerPole.Set poles;
            public PreviewModels(Action<Object> save,int seed){this.save=save;this.seed=seed;poles=new GeoPowerPole.Set(save);}
            Material Material(Color color)
            {
                if(materials.TryGetValue(color,out var material))return material;
                material=new Material(GeoAssets.Shader("Universal Render Pipeline/Lit")??GeoAssets.Shader("Standard")){name="Prop preview "+ColorUtility.ToHtmlStringRGB(color),color=color,enableInstancing=true};
                if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",color);
                materials[color]=material;save(material);return material;
            }
            // rotation: the one the object was given (GeoPropDamage.Rotation); a prop lying on its side is lifted onto the ground
            public void Attach(GameObject go,PropPlacement p,Quaternion rotation)
            {
                if(p.kind=="power_pole")
                {
                    // the textured pole, near and far (GeoPowerPole), its far shape as the collider
                    GeoPowerPole.Attach(go,p,poles,seed);
                    go.AddComponent<MeshCollider>().sharedMesh=poles.Of(GeoPowerPole.Key(p,seed)).far;
                    go.isStatic=true;
                    return;
                }
                Model model;
                // a damaged wall section is a shape of its own, in real dimensions
                bool own=p.segment&&p.kind=="wall"&&p.damage!=GeoPropDamage.Intact;
                if(own)model=Create(GeoPropBaker.Segment(p,seed),"Preview wall "+p.id);
                else
                {
                    string key=GeoPropDamage.ModelKey(p,seed);
                    if(!models.TryGetValue(key,out model)){model=Create(Drafts(p.kind,p.damage,GeoPropDamage.Variant(p,seed)),"Preview "+key);models[key]=model;}
                }
                go.AddComponent<MeshFilter>().sharedMesh=model.mesh;go.AddComponent<MeshRenderer>().sharedMaterials=model.materials;
                Vector3 scale;
                if(p.kind=="tree")scale=new Vector3(p.width/4,p.height/7,p.width/4);
                else if(own)scale=Vector3.one;
                else if(p.segment)scale=new Vector3(p.width,p.height,p.length);
                else
                {
                    if(!sizes.TryGetValue(p.kind,out var size))sizes[p.kind]=size=IntactSize(p.kind);
                    scale=Scale(p,size);
                }
                go.transform.localScale=scale;
                go.transform.position+=Vector3.up*GeoPropDamage.Lift(rotation,model.mesh.bounds,scale,p.lean);
                // Trees have a trunk collider; other preview objects use their actual shared mesh.
                if(p.kind=="tree")
                {var c=go.AddComponent<CapsuleCollider>();c.center=new Vector3(0,2,0);c.height=4;c.radius=0.2f;}
                else go.AddComponent<MeshCollider>().sharedMesh=model.mesh;
                go.isStatic=true;
            }
            Model Create(Dictionary<Color,MeshDraft> drafts,string name)
            {
                var mesh=new Mesh{name=name,indexFormat=IndexFormat.UInt32};var vertices=new List<Vector3>();var uv=new List<Vector2>();
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
