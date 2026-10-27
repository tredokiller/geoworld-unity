using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif
using UnityEngine;
using Object=UnityEngine.Object;

namespace GeoWorld.Editor
{
    public static class GeoBuildingBuilder
    {
        public static GeoBuildingStyle DefaultStyle(string category)
        {
            string kind=category=="house"||category=="farm"||category=="shed"?"House":category=="apartments"?"Apartments":"Industrial";
            var style=GeoAssets.Load<GeoBuildingStyle>(GeoPaths.Package+"/BuildingStyles/"+kind+".asset");
            if(style)return style;
            string folder=GeoPaths.Project+"/BuildingStyles",path=folder+"/"+kind+".asset";
            style=GeoAssets.Load<GeoBuildingStyle>(path);
            if(style)return style;
#if !UNITY_EDITOR
            return null; // a player has the package's styles (GeoRuntimeAssets) and makes none
#else
            Directory.CreateDirectory(folder);AssetDatabase.Refresh();
            style=ScriptableObject.CreateInstance<GeoBuildingStyle>();style.name=kind;
            AssetDatabase.CreateAsset(style,path);
            Material Material(string name,Color color,float smoothness=0.1f)
            {
                var shader=Shader.Find("Universal Render Pipeline/Lit")??Shader.Find("Standard");
                var mat=new Material(shader){name=name,color=color};
                if(mat.HasProperty("_BaseColor"))mat.SetColor("_BaseColor",color);
                mat.SetFloat("_Smoothness",smoothness);AssetDatabase.AddObjectToAsset(mat,path);return mat;
            }
            style.walls=Material(kind+" walls",kind=="House"?new Color(0.77f,0.72f,0.57f):kind=="Apartments"?new Color(0.68f,0.66f,0.58f):new Color(0.56f,0.60f,0.58f));
            style.roof=Material(kind+" roof",kind=="House"?new Color(0.39f,0.15f,0.10f):new Color(0.22f,0.26f,0.27f));
            style.foundation=Material("Foundation and trim",new Color(0.48f,0.47f,0.42f));
            style.windows=Material("Window glass",new Color(0.12f,0.23f,0.28f),0.7f);
            style.door=Material("Entrance",new Color(0.25f,0.19f,0.14f));
            style.defaultRoof=kind=="House"?GeoRoofShape.Gabled:GeoRoofShape.Flat;
            if(kind=="Apartments")
            {
                var band=ScriptableObject.CreateInstance<GeoBuildingBand>();band.name="Apartment facade band";band.relativeHeight=0.95f;
                AssetDatabase.AddObjectToAsset(band,path);style.modules=new GeoBuildingModule[]{band};
            }
            EditorUtility.SetDirty(style);AssetDatabase.SaveAssets();return style;
#endif
        }

        public static void Import(GeoBuilding building,BuildingFeature source,List<string> notes=null)
        {
            building.sourceId=source.id;building.category=source.category;building.heightSource=source.heightSource;building.buildingTag=source.buildingTag;
            building.height=Mathf.Clamp(source.height,2,600);building.floors=source.levels>0?source.levels:Mathf.Max(1,Mathf.RoundToInt(building.height/3));
            building.style=DefaultStyle(source.category);building.style.ApplyDefaults(building);
            building.roofHeight=Mathf.Min(building.roofHeight,building.height*0.35f);
            building.roofDirection=0;building.importNote="";
            building.mappedAppearance=source.mappedAppearance;
            building.visualDetails=source.visualDetails;
            building.condition=source.condition;
            building.overrideWallColor=false;building.overrideRoofColor=false;
            building.roofInfo=source.roof;
            bool roofFromExport=source.roof!=null&&ApplyRoofInfo(building,source,notes);
            if(source.mappedAppearance!=null)
            {
                var mapped=source.mappedAppearance;
                bool supportedShape=TryRoofShape(mapped.roofShape,out var shape);
                bool unsupportedShape=!supportedShape&&!string.IsNullOrEmpty(mapped.roofShape);
                if(supportedShape)building.roof=shape;
                else if(unsupportedShape&&!roofFromExport)
                    ImportNote(building,"Mapped roof shape '"+mapped.roofShape+"' is retained as evidence but is not supported by the renderer; style geometry is used unless a supported review or manual edit overrides it.",notes);
                if(TryPhotoColor(mapped.wallColor,out var wall)){building.wallColor=wall;building.overrideWallColor=true;}
                if(TryPhotoColor(mapped.roofColor,out var roof)){building.roofColor=roof;building.overrideRoofColor=true;}
                if(!unsupportedShape&&mapped.roofHeight.HasValue&&GeoData.Finite(mapped.roofHeight.Value)&&mapped.roofHeight.Value>=0)
                {
                    building.roofHeight=Mathf.Clamp(mapped.roofHeight.Value,0,Mathf.Max(0,building.height-2));
                    if(building.roofHeight!=mapped.roofHeight.Value)
                        ImportNote(building,"Mapped roof height was limited to preserve the total height and at least 2 m of walls. The original measurement is retained.",notes);
                }
                if(!unsupportedShape&&mapped.roofDirection.HasValue&&GeoData.Finite(mapped.roofDirection.Value)&&mapped.roofDirectionMeaning=="downslope_bearing")
                {
                    // roofAxis=(cos(a),-sin(a)) is local downslope. Export rotation
                    // is a clockwise bearing, so D = B + a + 90 (modulo 180).
                    // a single slope has a downhill side, so its direction is a full turn rather than half of one
                    building.roofDirection=Mathf.Repeat(mapped.roofDirection.Value-source.rotation-90,building.roof==GeoRoofShape.Skillion?360:180);
                }
                else if(!unsupportedShape&&building.roof!=GeoRoofShape.Skillion&&mapped.roofOrientation=="across")building.roofDirection=90;
                // Along means the ridge follows the building's local long Z axis.
            }
            if(source.visualDetails!=null)
            {
                var details=source.visualDetails;
                if(TryRoofShape(details.roofShape,out var shape))building.roof=shape;
                if(TryPhotoColor(details.wallColor,out var wall)){building.wallColor=wall;building.overrideWallColor=true;}
                if(TryPhotoColor(details.roofColor,out var roof)){building.roofColor=roof;building.overrideRoofColor=true;}
                // Observed levels stay in the evidence. They do not replace source dimensions.
            }
            var rotation=Quaternion.Euler(0,source.rotation,0);
            building.transform.SetPositionAndRotation(new Vector3(source.x,0,source.z),rotation);
            building.transform.localScale=Vector3.one;
            var outline=new List<Vector2>();
            try
            {
                if(source.footprint==null||source.footprint.Length<6||source.footprint.Length%2!=0)throw new ArgumentException("Missing footprint.");
                // Export offsets are already east/north oriented. Store inverse-rotated
                // local corners so TransformPoint returns (source.x+dx, source.z+dz).
                for(int i=0;i<source.footprint.Length;i+=2)
                {var p=Quaternion.Inverse(rotation)*new Vector3(source.footprint[i],0,source.footprint[i+1]);outline.Add(new Vector2(p.x,p.z));}
                outline=GeoPolygon.Clean(outline);GeoPolygon.Triangulate(outline);
            }
            catch(ArgumentException error)
            {
                float x=Mathf.Max(0.5f,source.width)*0.5f,z=Mathf.Max(0.5f,source.length)*0.5f;
                outline=new List<Vector2>{new Vector2(-x,-z),new Vector2(x,-z),new Vector2(x,z),new Vector2(-x,z)};
                ImportNote(building,"Box outline fallback: "+error.Message,notes);
            }
            building.footprint=outline;
        }

        // The export's merged roof block (reviewed over mapped over estimated, each value with its source) is the
        // baseline for shape, rise, direction and colour. The raw mapped and reviewed details in Import restate the
        // same facts afterwards and win where they differ. Returns whether the block decided the roof shape.
        static bool ApplyRoofInfo(GeoBuilding building,BuildingFeature source,List<string> notes)
        {
            var info=source.roof;
            bool exact=TryRoofShape(info.shape,out var shape);
            bool nearest=!exact&&TryRoofShape(info.renderAs,out shape);
            if(exact||nearest)
            {
                building.roof=shape;
                if(nearest&&info.shapeSource!=GeoRoofInfo.Estimated)
                    ImportNote(building,"Roof shape '"+info.shape+"' is drawn as the nearest supported shape, "+info.renderAs+".",notes);
                if(shape!=GeoRoofShape.Flat&&GeoRoofInfo.Has(info.rise))
                    building.roofHeight=Mathf.Clamp(info.rise,0,Mathf.Max(0,building.height-2));
                if(shape==GeoRoofShape.Skillion)
                {
                    // the downhill side is known only when it was mapped; otherwise it is a deterministic coin toss per building,
                    // so a row of sheds does not all drain the same way
                    building.roofDirection=GeoRoofInfo.Has(info.slopeBearing)
                        ?Mathf.Repeat(info.slopeBearing-source.rotation-90,360)
                        :GeoBuildingDamage.Hash01(GeoBuildingDamage.Seed(source.id),77)<0.5f?0:180;
                }
                else if(shape==GeoRoofShape.Gabled||shape==GeoRoofShape.Hipped)
                {
                    // roofDirection turns the downslope axis; the ridge is square to it, so it is the ridge bearing minus the building's
                    if(GeoRoofInfo.Has(info.ridgeBearing))
                    {
                        float turn=Mathf.Repeat(info.ridgeBearing-source.rotation,180);
                        building.roofDirection=turn<0.25f||turn>179.75f?0:turn; // bearings are rounded to 0.1 degree
                    }
                    else if(info.ridge=="across")building.roofDirection=90;
                }
            }
            if(TryPhotoColor(info.color,out var color)){building.roofColor=color;building.overrideRoofColor=true;}
            return exact||nearest;
        }

        static void ImportNote(GeoBuilding building,string note,List<string> notes)
        {
            building.importNote+=(string.IsNullOrEmpty(building.importNote)?"":"\n")+note;
            notes?.Add("Building "+building.sourceId+": "+note);
        }

        public static bool TryRoofShape(string value,out GeoRoofShape shape)
        {
            shape=GeoRoofShape.Flat;
            switch(value)
            {
                case "flat":return true;
                case "gabled":shape=GeoRoofShape.Gabled;return true;
                case "hipped":shape=GeoRoofShape.Hipped;return true;
                case "skillion":shape=GeoRoofShape.Skillion;return true;
                default:return false;
            }
        }

        // settings: the generation settings that pick a ruin model for an automatic building (null keeps its ruin as it is);
        // streets turns the model's front towards the nearest road.
        public static GeoBuilding Create(BuildingFeature source,Transform parent,Func<Vector3,float> ground,Action<Object> save,List<string> notes,GeoBuilding edited=null,GenerationSettings settings=null,GeoStreets streets=null)
        {
            GameObject go=edited?Object.Instantiate(edited.gameObject):new GameObject((source.category??"Building")+" | "+(string.IsNullOrEmpty(source.name)?source.id:source.name));
            if(edited)go.name=edited.name;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,parent.gameObject.scene);
            go.transform.SetParent(parent,true);
            try
            {
                var building=edited?go.GetComponent<GeoBuilding>():go.AddComponent<GeoBuilding>();
                if(!edited)Import(building,source,notes);
                GeoRuins.Assign(building,settings,streets!=null&&!streets.Empty?p=>streets.Toward(p):(Func<Vector2,Vector2?>)null);
                GeoHomes.Assign(building,settings,streets!=null&&!streets.Empty?p=>streets.Toward(p):(Func<Vector2,Vector2?>)null);
                if(building.generatedGeometry)Object.DestroyImmediate(building.generatedGeometry.gameObject);
                building.generatedGeometry=null;
                var context=new GeoBuildingContext(building);
                Ground(building,context,ground,out float baseY,out float depth);
                var output=GeoBuildingGeometry.Build(building,depth,Local(building,ground,baseY));
                building.transform.position=new Vector3(building.transform.position.x,baseY,building.transform.position.z);
                building.generatedGeometry=Install(building,output,save);
                return building;
            }
            catch{Object.DestroyImmediate(go);throw;}
        }

        // The ground under a local point of the building, relative to its base and in its local units.
        internal static Func<Vector2,float> Local(GeoBuilding building,Func<Vector3,float> ground,float baseY)
        {
            var transform=building.transform;float scale=Mathf.Max(0.0001f,transform.lossyScale.y);
            return p=>(ground(transform.TransformPoint(new Vector3(p.x,0,p.y)))-baseY)/scale;
        }

        internal static void Ground(GeoBuilding building,GeoBuildingContext context,Func<Vector3,float> ground,out float baseY,out float depth)
        {
            var scale=building.transform.lossyScale;
            if(scale.x<=0||scale.y<=0||scale.z<=0||Vector3.Dot(building.transform.up,Vector3.up)<0.9999f)
                throw new ArgumentException("Buildings support positive scale and Y rotation. Remove tilt before rebuilding.");
            if(!GeoData.Finite(building.groundOffset))throw new ArgumentException("Ground offset must be finite.");
            float min=float.MaxValue,max=float.MinValue;
            void Sample(Vector2 local)
            {
                float h=ground(building.transform.TransformPoint(new Vector3(local.x,0,local.y)));
                min=Mathf.Min(min,h);max=Mathf.Max(max,h);
            }
            for(int i=0;i<context.outline.Count;i++)
            {
                var a=context.outline[i];var b=context.outline[(i+1)%context.outline.Count];
                int steps=Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(a,b)*Mathf.Max(scale.x,scale.z)/2),1,1024);
                for(int k=0;k<=steps;k++)Sample(Vector2.Lerp(a,b,k/(float)steps));
            }
            // Also sample interiors: a hill may peak away from all footprint corners.
            float minX=context.outline.Min(p=>p.x),maxX=context.outline.Max(p=>p.x),minZ=context.outline.Min(p=>p.y),maxZ=context.outline.Max(p=>p.y);
            float step=Mathf.Max(2/Mathf.Max(scale.x,scale.z),Mathf.Max(maxX-minX,maxZ-minZ)/64);
            for(float z=minZ+step*0.5f;z<maxZ;z+=step)for(float x=minX+step*0.5f;x<maxX;x+=step)
                if(GeoPolygon.Contains(context.outline,new Vector2(x,z)))Sample(new Vector2(x,z));
            baseY=max+building.groundOffset;depth=Mathf.Max(0.2f,(baseY-min+0.25f)/scale.y);
        }

        static Transform Install(GeoBuilding building,GeoBuildingOutput output,Action<Object> save)
        {
            var generated=new GameObject("Generated building geometry");generated.transform.SetParent(building.transform,false);
            foreach(var part in output.parts)
            {
                save(part.mesh);
                var go=new GameObject(part.name);go.transform.SetParent(generated.transform,false);
                go.AddComponent<MeshFilter>().sharedMesh=part.mesh;
                Material material=part.material;
                bool wall=part.name=="Walls"&&building.overrideWallColor&&!building.wallMaterial;
                bool roof=part.name=="Roof"&&building.overrideRoofColor&&!building.roofMaterial;
                // a ruin model has its damage painted in; it keeps its own material
                bool damaged=GeoBuildingDamage.StateOf(building)>=GeoConditionState.Damaged&&part.name!="Ruin"&&part.name!="Model";
                if((wall||roof||damaged)&&material)
                {
                    var color=wall?building.wallColor:roof?building.roofColor:PartColor(material);
                    if(damaged)color=GeoBuildingDamage.Tint(building,part.name,color);
                    material=new Material(material){name=part.name+" color | "+building.sourceId,color=color};
                    if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",color);
                    save(material);
                }
                go.AddComponent<MeshRenderer>().sharedMaterial=material;
                if(part.collider)go.AddComponent<MeshCollider>().sharedMesh=part.mesh;
            }
            return generated.transform;
        }

        // The colour a material is drawn with (URP keeps it in _BaseColor).
        internal static Color PartColor(Material material)=>material?(material.HasProperty("_BaseColor")?material.GetColor("_BaseColor"):material.color):Color.gray;

        static bool TryPhotoColor(string value,out Color color)
        {
            color=Color.white;
            return value!=null&&value.Length==7&&value[0]=='#'&&ColorUtility.TryParseHtmlString(value,out color);
        }

#if UNITY_EDITOR
        public static void Rebuild(GeoBuilding building,bool recordUndo=true)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Stop Play mode before rebuilding a building.");
            var world=building.GetComponentInParent<GeoWorldRoot>();var terrain=world?world.terrain:Terrain.activeTerrain;
            // an automatic ruin is picked again with the world's settings (a building outside a world keeps its ruin);
            // a failed rebuild puts the old choice back
            var ruin=building.ruin;bool mirror=building.ruinMirror,turn=building.ruinTurn;float apron=building.ruinApron;
            void Restore(){building.ruin=ruin;building.ruinMirror=mirror;building.ruinTurn=turn;building.ruinApron=apron;}
            GeoBuildingOutput output;float baseY;
            try
            {
                GeoRuinFit.ClearCache();
                if(world)
                {
                    // the same street as when the world was generated, so the model faces the same way
                    GeoStreets streets=null;
                    if(building.ruinMode==GeoRuinMode.Auto&&!string.IsNullOrEmpty(world.environmentPath)&&File.Exists(world.environmentPath))
                        streets=new GeoStreets(GeoData.ReadEnvironment(world.environmentPath).roads);
                    GeoRuins.Assign(building,world.settings,streets!=null&&!streets.Empty?p=>streets.Toward(p):(Func<Vector2,Vector2?>)null);
                    GeoHomes.Assign(building,world.settings,streets!=null&&!streets.Empty?p=>streets.Toward(p):(Func<Vector2,Vector2?>)null);
                }
                var context=new GeoBuildingContext(building);
                Func<Vector3,float> sample=terrain?(Func<Vector3,float>)(p=>terrain.SampleHeight(p)+terrain.transform.position.y):p=>building.transform.position.y-building.groundOffset;
                Ground(building,context,sample,out baseY,out float depth);
                output=GeoBuildingGeometry.Build(building,depth,Local(building,sample,baseY));
            }
            catch{Restore();throw;}
            string assetPath=null;Transform generated=null;
            try
            {
                Directory.CreateDirectory(GeoPaths.Project+"/Generated");AssetDatabase.Refresh();
                assetPath=AssetDatabase.GenerateUniqueAssetPath(GeoPaths.Project+"/Generated/Building.asset");
                var asset=ScriptableObject.CreateInstance<GeoGenerationAssets>();asset.summary="Editable building: "+building.sourceId;
                AssetDatabase.CreateAsset(asset,assetPath);
                generated=Install(building,output,obj=>AssetDatabase.AddObjectToAsset(obj,assetPath));
                AssetDatabase.SaveAssets();
                if(recordUndo)
                {
                    // the undo step starts from the building as it was before the ruin was picked again
                    var picked=building.ruin;bool pickedMirror=building.ruinMirror,pickedTurn=building.ruinTurn;float pickedApron=building.ruinApron;
                    Restore();
                    Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Rebuild building");
                    Undo.RecordObjects(new Object[]{building,building.transform},"Rebuild building");
                    building.ruin=picked;building.ruinMirror=pickedMirror;building.ruinTurn=pickedTurn;building.ruinApron=pickedApron;
                    Undo.RegisterCreatedObjectUndo(generated.gameObject,"Rebuild building");
                    if(building.generatedGeometry)Undo.DestroyObjectImmediate(building.generatedGeometry.gameObject);
                    building.generatedGeometry=generated;building.transform.position=new Vector3(building.transform.position.x,baseY,building.transform.position.z);
                    Undo.CollapseUndoOperations(group);
                }
                else
                {
                    if(building.generatedGeometry)Object.DestroyImmediate(building.generatedGeometry.gameObject);
                    building.generatedGeometry=generated;building.transform.position=new Vector3(building.transform.position.x,baseY,building.transform.position.z);
                }
                EditorUtility.SetDirty(building);EditorSceneManager.MarkSceneDirty(building.gameObject.scene);
            }
            catch
            {
                Restore();
                if(generated)Object.DestroyImmediate(generated.gameObject);
                if(assetPath!=null)AssetDatabase.DeleteAsset(assetPath);else output.Dispose();
                throw;
            }
        }
#endif
    }
}
