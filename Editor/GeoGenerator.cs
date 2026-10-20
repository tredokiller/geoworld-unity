using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    public static class GeoGenerator
    {
        public const string OutputFolder = GeoPaths.Project + "/Generated";

        public static GeoWorldRoot Generate(string environmentPath, string buildingsPath, GenerationSettings settings, GeoWorldRoot replace = null, bool recordUndo = true)
        {
            // one tile of a tiled world is rebuilt from the whole export, so that it still meets its neighbours
            if (replace != null && replace.tile) return GeoTiles.Regenerate(replace, settings);
            return Generate(environmentPath, buildingsPath, settings, replace, recordUndo, null);
        }

        // tile: one tile of a tiled world (GeoTiles), with the export already read and cropped, its height field with a margin,
        // paths and water levels worked out for the whole export, and the shared height scale. Null: one world.
        internal static GeoWorldRoot Generate(string environmentPath, string buildingsPath, GenerationSettings settings, GeoWorldRoot replace, bool recordUndo, GeoTileJob tile)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before generating.");
            if (replace != null && (replace.transform.position != Vector3.zero || replace.transform.rotation != Quaternion.identity || replace.transform.lossyScale != Vector3.one))
                throw new InvalidOperationException("Reset the generated world's transform before rebuilding; coordinates use world-space metres.");
            settings = JsonUtility.FromJson<GenerationSettings>(JsonUtility.ToJson(settings));
            settings.heightResolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(settings.heightResolution-1),32,1024)+1;
            settings.textureResolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(settings.textureResolution),32,1024);
            settings.roadPaintResolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(settings.roadPaintResolution > 0 ? settings.roadPaintResolution : 2048),32,4096);
            settings.pathStep = Mathf.Clamp(settings.pathStep,0.5f,10);
            settings.roadShoulder = Mathf.Clamp(settings.roadShoulder,0,12);
            settings.junctionSnap = Mathf.Clamp(settings.junctionSnap,0,3);
            settings.blendMetres = Mathf.Clamp(settings.blendMetres,0,12);
            settings.riverDepth = Mathf.Clamp(settings.riverDepth,0.5f,8);
            // 0 is Auto: GeoSatellite sizes the texture to the area
            settings.satelliteResolution = settings.satelliteResolution > 0 ? Mathf.Clamp(Mathf.ClosestPowerOfTwo(settings.satelliteResolution),1024,8192) : 0;
            var notes = new List<string>();
            GeoWorldRoot root = null;
            string assetPath = null;
            try
            {
                Progress("Reading export and checking coordinates",0.02f);
                EnvironmentData env;
                BuildingsData buildings;
                if (tile != null) { env = tile.env; buildings = tile.buildings; }
                else if (string.IsNullOrEmpty(environmentPath))
                {
                    buildings = GeoData.ReadBuildings(buildingsPath,null);
                    GeoData.ValidateBounds(buildings.area?.bounds);
                    env = new EnvironmentData { format="geo-environment",version=1,bounds=buildings.area.bounds,origin=buildings.origin };
                    notes.Add("Buildings-only import: roads and water require environment.json from Terrain & ground export.");
                }
                else
                {
                    env=GeoData.ReadEnvironment(environmentPath);
                    buildings=GeoData.ReadBuildings(buildingsPath,env.origin);
                }
                if (replace != null && tile == null) ApplyOverrides(env,replace);
                var field=tile?.field??HeightField.Load(env,environmentPath,settings.heightResolution,notes);
                var paths=tile?.paths??PreparePaths(env,field.Sample,settings,notes);
                var roadNetwork=new GeoRoadNetwork(paths,field);
                roadNetwork.Connect(settings.junctionSnap,settings.pathStep);
                Progress("Carving water and grading roads",0.12f);
                var waterLevels=tile?.waterLevels??WaterLevels(env.water,field.Sample);
                if(settings.carveRivers) CarveLakes(env.water,waterLevels,field,settings.riverDepth);
                GeoTerrain.Carve(field,paths,settings);
                GeoTerrain.Grade(field,paths,settings);
                if(settings.gradeRoads) roadNetwork.GradeJunctions(settings.roadShoulder);
                if(roadNetwork.snappedEndpoints>0) notes.Add(roadNetwork.snappedEndpoints+" nearby road endpoints snapped into connections.");

                string folder=tile?.folder??OutputFolder;
                if(!Directory.Exists(folder)){Directory.CreateDirectory(folder);AssetDatabase.Refresh();}
                var asset=ScriptableObject.CreateInstance<GeoGenerationAssets>();
                assetPath=AssetDatabase.GenerateUniqueAssetPath(folder+"/"+(tile?.name??"World")+".asset");
                AssetDatabase.CreateAsset(asset,assetPath);
                var go=new GameObject(tile!=null?"Geo World tile "+tile.index.x+", "+tile.index.y:
                    "Geo World - "+(string.IsNullOrEmpty(environmentPath)?"Buildings":new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(environmentPath))).Name));
                root=go.AddComponent<GeoWorldRoot>();
                root.environmentPath=environmentPath;root.buildingsPath=buildingsPath;root.settings=settings;root.assetPath=assetPath;
                if(tile!=null)
                {
                    root.tile=true;root.tileIndex=tile.index;root.tileSize=tile.size;root.tileBaseY=tile.baseY;root.tileHeightRange=tile.range;
                    root.tileArea=new Rect(tile.core.minX,tile.core.minZ,tile.core.Width,tile.core.Depth);
                }
                if(replace!=null) UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,replace.gameObject.scene);
                var context=new BuildContext(assetPath,root){clip=tile?.core};
                Progress("Building terrain and blending ground layers",0.22f);
                Texture2D satellite=null;
                if(settings.satelliteImagery)
                {
                    try{satellite=GeoSatellite.Build(env.origin,env.bounds,settings,notes,(label,f)=>Progress(label,0.22f+f*0.03f));}
                    catch(Exception e) when(!(e is OperationCanceledException))
                    {notes.Add("Satellite imagery not used ("+e.Message+"); the terrain has the blended ground layers.");Debug.LogWarning("Geo World: satellite imagery failed: "+e);}
                }
                var terrainData=GeoTerrain.CreateData(tile!=null?tile.Core(field):field,settings,out float baseY,tile?.baseY??0,tile?.range??0);
                // One layer needs no detailed splat map: the imagery carries all the detail.
                if(satellite) terrainData.alphamapResolution=32;
                // Persist before painting: Unity creates alphamap texture sub-assets on disk.
                // Adding a transient painted TerrainData to an asset resets its native splat data.
                context.Save(terrainData);
                TerrainLayer[] layers;
                if(satellite)
                {
                    // the texture spans the whole terrain once: one tile the size of the world, no offset
                    layers=new[]{context.Save(new TerrainLayer{name="Satellite imagery",diffuseTexture=context.Save(satellite),
                        tileSize=new Vector2(env.bounds.Width,env.bounds.Depth),smoothness=0,metallic=0})};
                    terrainData.terrainLayers=layers;
                    int n=terrainData.alphamapResolution;var full=new float[n,n,1];
                    for(int z=0;z<n;z++)for(int x=0;x<n;x++)full[z,x,0]=1;
                    terrainData.SetAlphamaps(0,0,full);
                }
                else
                {
                    layers=new TerrainLayer[GeoTerrain.LayerNames.Length];
                    for(int i=0;i<layers.Length;i++)
                    {
                        if(settings.groundTextures&&GeoTerrainTextures.HasLayer(i))
                        {
                            // photographic textures (Poly Haven, CC0) imported by GeoTerrainTextures
                            var diffuse=AssetDatabase.LoadAssetAtPath<Texture2D>(GeoTerrainTextures.DiffusePath(i));
                            var normal=AssetDatabase.LoadAssetAtPath<Texture2D>(GeoTerrainTextures.NormalPath(i));
                            if(diffuse&&normal)
                            {
                                float repeat=GeoTerrainTextures.Tile(i);
                                layers[i]=context.Save(new TerrainLayer {name=GeoTerrain.LayerNames[i],diffuseTexture=diffuse,normalMapTexture=normal,
                                    tileSize=new Vector2(repeat,repeat),smoothness=0.08f,normalScale=1f,
                                    diffuseRemapMin=GeoTerrainTextures.RemapMin(i),diffuseRemapMax=GeoTerrainTextures.Remap(i)});
                                continue;
                            }
                        }
                        var texture=context.Save(GeoTerrain.Texture(GeoTerrain.LayerNames[i],GeoTerrain.LayerColors[i],i+31,i==6));
                        layers[i]=context.Save(new TerrainLayer {name=GeoTerrain.LayerNames[i],diffuseTexture=texture,tileSize=new Vector2(8,8),smoothness=0.05f});
                    }
                    terrainData.terrainLayers=layers;
                    var ground=GeoTerrain.Paint(env,environmentPath,paths,settings,notes);
                    var roadPaint=new GeoRoadPaint(env.bounds,roadNetwork,settings.laneMarkings&&!settings.roadMeshes);
                    roadPaint.Apply(terrainData,ground,f=>Progress("Painting roads into the terrain",0.25f+f*0.14f));
                }
                var terrainObject=Terrain.CreateTerrainGameObject(terrainData);
                terrainObject.name=satellite?"Terrain - satellite imagery":"Terrain - blended ground"; terrainObject.transform.SetParent(root.transform);
                terrainObject.transform.position=new Vector3(env.bounds.minX,baseY,env.bounds.minZ);
                root.terrain=terrainObject.GetComponent<Terrain>();
                root.terrain.heightmapPixelError=1;
                // a terrain casting shadows is drawn again for every shadow cascade at full detail: 3-6 ms of a city frame
                // for shadows that hardly show (Kharkiv: no pixel changed). Buildings and trees still cast theirs on it.
                root.terrain.shadowCastingMode=settings.terrainShadows?ShadowCastingMode.TwoSided:ShadowCastingMode.Off;
                // tiles of one world join up: Unity stitches the edges of neighbouring terrains of one group
                if(tile!=null){root.terrain.groupingID=GeoTiles.TerrainGroup;root.terrain.allowAutoConnect=true;}
                if(satellite) root.terrain.basemapDistance=20000;
                // terrain drawn with GPU instancing (far fewer draw calls), trees as far as they are worth drawing
                root.terrain.drawInstanced=true;root.terrain.treeDistance=1500;
                var terrainShader=Shader.Find("Universal Render Pipeline/Terrain/Lit");
                if(terrainShader!=null) root.terrain.materialTemplate=context.Save(new Material(terrainShader){name="Terrain URP"});

                var roads=Group("Roads - select a road to edit",root.transform);
                var rivers=Group("Rivers",root.transform);
                root.junctionCount=tile!=null?roadNetwork.junctions.Count(j=>tile.core.Contains(j.center.x,j.center.y)):roadNetwork.junctions.Count;
                int pathIndex=0;bool roadsBuilt=false;
                // A tile's ground roads are drawn a few meshes per chunk and material, like the crossings: a 1 km tile of a
                // city has over a thousand pieces of road, and an object for each was most of what the tile had to load
                // (and editor windows wait while a tile is read). Their objects only served editing, which a tile rebuild
                // does not keep (a road is cut into tiles). Bridges, raised roads and rivers keep their objects.
                var groundRoads=tile!=null?new MeshBatches("Roads",settings.chunkSize):null;
                foreach(var path in paths)
                {
                    if(pathIndex++%20==0) Progress("Creating roads and rivers",0.40f+0.15f*pathIndex/Mathf.Max(1,paths.Count));
                    // a tile builds the roads around it too (they grade its edges) but keeps only what lies on it
                    if(tile!=null&&!GeoTiles.Touches(path,tile.core)) continue;
                    bool batched=groundRoads!=null&&!path.river&&!path.Elevated;
                    GameObject item=null;
                    if(!batched)
                    {
                        string label=string.IsNullOrEmpty(path.feature.name)?path.feature.id:path.feature.name;
                        item=new GameObject((path.river?"River | ":path.feature.bridge?"Bridge | ":"Road | ")+label);
                        item.transform.SetParent(path.river?rivers:roads);
                        var editable=item.AddComponent<GeoPath>();editable.sourceId=path.feature.id;editable.river=path.river;
                        editable.width=path.feature.width;editable.bridge=path.feature.bridge;editable.surface=GeoData.Surface(path.feature.material);
                        editable.layer=path.feature.layer;
                        editable.points=path.controls.Select(p=>new Vector3(p.x,field.Sample(p.x,p.z)+0.5f,p.z)).ToList();
                    }
                    // Ground roads, intersections and markings are Terrain layers. Their
                    // editable path objects carry no renderer or second collision surface.
                    if(path.river||path.Elevated)
                    {
                        var material=path.river?context.Water:context.Surface(GeoData.Surface(path.feature.material));
                        var roadMesh=context.Clip(GeoMeshes.Ribbon(path,field,path.river?0:GeoRoadNetwork.SurfaceOffset));
                        if(roadMesh.vertexCount>0)context.MeshObject(item,roadMesh,material,!path.river);
                        else Object.DestroyImmediate(roadMesh);
                    }
                    if(path.river) root.riverCount++; else root.roadCount++;
                    if(settings.laneMarkings&&!path.river&&path.Elevated)
                    {
                        var mesh=context.Clip(GeoMeshes.Markings(path,field,roadNetwork));
                        if(mesh.vertexCount>0) context.MeshChild("Centre markings",item.transform,mesh,context.Marking,false);
                        else Object.DestroyImmediate(mesh);
                    }
                    if(settings.roadMeshes&&!path.river&&!path.Elevated)
                    {
                        // the road surface, clipped to the terrain triangles under it (built once, before the first road)
                        if(!roadsBuilt){Progress("Building road surfaces",0.40f);roadNetwork.Build(f=>Progress("Building road surfaces",0.40f+0.1f*f));roadsBuilt=true;}
                        if(roadNetwork.roads.TryGetValue(path,out var draft)&&draft.triangles.Count>0)
                        {
                            var surface=GeoData.Surface(path.feature.material);
                            Material material=null;
                            if(surface==GroundSurface.Asphalt&&GeoRoadPhotos.Available)
                            {
                                // a photo-scanned road (ambientCG), edge to edge across the road, repeating along it
                                material=context.RoadPhoto(GeoRoadPhotos.For(path.feature));
                                float start=GeoBuildingDamage.Hash01(GeoBuildingDamage.Seed(path.feature.id??""),11);
                                GeoMeshes.RoadUv(path,draft,Mathf.Max(3,path.feature.width),start);
                            }
                            else if(surface==GroundSurface.Asphalt&&GeoRoadTextures.Available)
                            {
                                // worn Ukrainian asphalt, laid along the road: u across, v along, a different start for every road
                                int variant=GeoRoadTextures.VariantOf(path.feature.id,path.feature.wear);
                                material=context.RoadStrip(variant);
                                float start=GeoBuildingDamage.Hash01(GeoBuildingDamage.Seed(path.feature.id??""),11);
                                GeoMeshes.RoadUv(path,draft,GeoRoadTextures.LengthMetres,start);
                            }
                            GeoMeshes.RoadEdges(path,draft); // soft edges (GeoWorld/Road)
                            var surfaceMesh=context.Clip(draft.Mesh("Road surface",roadNetwork.Normal));
                            var surfaceMaterial=context.Blend(material?material:context.Surface(surface));
                            if(batched) groundRoads.Add(surfaceMesh,new[]{surfaceMaterial});
                            else if(surfaceMesh.vertexCount>0) context.MeshObject(item,surfaceMesh,surfaceMaterial,false);
                            else Object.DestroyImmediate(surfaceMesh);
                        }
                        // the photo of a main road has its lines in it
                        bool photoLines=GeoRoadPhotos.Available&&GeoData.Surface(path.feature.material)==GroundSurface.Asphalt&&GeoRoadPhotos.IsMarked(path.feature);
                        if(settings.laneMarkings&&!photoLines)
                        {
                            var lines=context.Clip(GeoMeshes.GroundMarkings(path,roadNetwork));
                            if(batched) groundRoads.Add(lines,new[]{context.Marking});
                            else if(lines.vertexCount>0) context.MeshChild("Road markings",item.transform,lines,context.Marking,false);
                            else Object.DestroyImmediate(lines);
                        }
                    }
                    if(path.feature.bridge&&!path.river) BridgeRails(path,context,item.transform);
                }
                groundRoads?.Build(roads,context);
                if(settings.roadMeshes&&roadsBuilt)
                {
                    // the crossings are drawn a few meshes per chunk: a 1 km tile of a city has 1 700 pieces of crossing, and
                    // an object for each made it slow to load. Nothing edits them (a road is edited through its own object).
                    var crossings=new MeshBatches("Junctions",settings.chunkSize);
                    foreach(var junction in roadNetwork.junctions)
                    {
                        Material asphalt=null;
                        if(GeoRoadPhotos.Available||GeoRoadTextures.Available)
                        {
                            // the wear of the widest road decides the picture; every piece is laid like its own road
                            PathBuild main=null;
                            foreach(var path in junction.paths)
                                if(GeoData.Surface(path.feature.material)==GroundSurface.Asphalt&&(main==null||path.feature.width>main.feature.width||
                                    (path.feature.width==main.feature.width&&string.CompareOrdinal(path.feature.id,main.feature.id)<0)))main=path;
                            var draft=junction.surfaces[(int)GroundSurface.Asphalt];
                            if(main!=null&&draft.vertices.Count>0)
                            {
                                if(GeoRoadPhotos.Available)
                                {
                                    // every piece of the crossing is a piece of one road: it takes that road's own photo, laid the
                                    // way it is laid along the road, so the asphalt (and a main road's lines) run on through
                                    var owners=junction.owners[(int)GroundSurface.Asphalt];
                                    GeoMeshes.JunctionUv(draft,owners,0,0,1);
                                    GeoMeshes.JunctionEdges(draft,owners);
                                    var pieces=new Dictionary<string,MeshDraft>();
                                    for(int t=0;t<draft.triangles.Count;t+=3)
                                    {
                                        var owner=owners[draft.triangles[t]];
                                        string id=owner!=null?GeoRoadPhotos.For(owner.feature):GeoRoadPhotos.PlainFor(main.feature);
                                        if(!pieces.TryGetValue(id,out var part))pieces[id]=part=new MeshDraft();
                                        for(int k=0;k<3;k++)
                                        {
                                            int v=draft.triangles[t+k];
                                            part.vertices.Add(draft.vertices[v]);part.uv.Add(draft.uv[v]);part.uv2.Add(draft.uv2[v]);part.triangles.Add(part.vertices.Count-1);
                                        }
                                    }
                                    foreach(var pair in pieces)
                                    {
                                        var material=context.RoadPhoto(pair.Key);
                                        if(!material)continue;
                                        crossings.Add(context.Clip(pair.Value.Mesh("Junction asphalt",roadNetwork.Normal)),new[]{context.Blend(material)});
                                    }
                                    // the asphalt is drawn; the junction mesh below keeps only its other surfaces
                                    draft.vertices.Clear();draft.uv.Clear();draft.uv2.Clear();draft.triangles.Clear();
                                }
                                else
                                {
                                    asphalt=context.RoadStrip(GeoRoadTextures.VariantOf(main.feature.id,main.feature.wear));
                                    if(asphalt)GeoMeshes.JunctionUv(draft,junction.owners[(int)GroundSurface.Asphalt],GeoRoadTextures.LengthMetres,0.1f,0.9f);
                                }
                            }
                        }
                        for(int k=0;k<4;k++)GeoMeshes.JunctionEdges(junction.surfaces[k],junction.owners[k]);
                        var mesh=context.Clip(roadNetwork.JunctionMesh(junction,out var surfaces));
                        crossings.Add(mesh,surfaces.Select(s=>context.Blend(s==GroundSurface.Asphalt&&asphalt?asphalt:context.Surface(s))).ToArray());
                    }
                    crossings.Build(roads,context);
                }
                var waters=Group("Water bodies",root.transform);
                for(int i=0;i<env.water.Count;i++)
                {
                    var mesh=LakeMesh(env.water[i],waterLevels[i],env.bounds);
                    if(mesh.vertexCount==0) { Object.DestroyImmediate(mesh);continue; }
                    context.MeshChild("Water | "+env.water[i].id,waters,mesh,context.Water,false);root.waterCount++;
                }
                Progress("Placing buildings",0.63f);
                if(settings.buildings)
                {
                    var group=Group("Buildings",root.transform);
                    // ruin models turn their fronts to the nearest street
                    var streets=new GeoStreets(env.roads);
                    // a city is baked into chunks of mesh; a village keeps one editable object per building
                    bool merged=GeoCityBuilder.ShouldMerge(settings,buildings.buildings.Count);
                    if(merged)
                        root.buildingCount+=GeoCityBuilder.Build(buildings.buildings,env.bounds,group,p=>field.Sample(p.x,p.z),obj=>context.Save(obj),notes,settings,
                            f=>Progress("Baking buildings",0.63f+0.22f*f),buildings.buildings.Count,streets);
                    var edited=BuildingEdits(replace);
                    int index=0,skipped=0;var ruins=new Dictionary<string,int>();
                    GeoRuinFit.ClearCache();
                    foreach(var building in merged?new List<BuildingFeature>():buildings.buildings)
                    {
                        if(index++%100==0) Progress("Placing buildings "+index+" / "+buildings.buildings.Count,0.63f+0.22f*index/Mathf.Max(1,buildings.buildings.Count));
                        if(!ValidBuilding(building)||!env.bounds.Contains(building.x,building.z)) { skipped++;continue; }
                        edited.TryGetValue(building.id??"",out var previous);
                        CountRuin(ruins,GeoBuildingBuilder.Create(ForSettings(building,settings),group,p=>field.Sample(p.x,p.z),obj=>context.Save(obj),notes,previous,settings,streets));root.buildingCount++;
                    }
                    root.buildingCount+=CopyCustomBuildings(replace,group,p=>field.Sample(p.x,p.z),obj=>context.Save(obj),notes,settings);
                    if(ruins.Count>0) notes.Add(GeoCityBuilder.RuinNote(ruins));
                    if(skipped>0&&tile==null) notes.Add(skipped+" buildings outside bounds or with invalid dimensions were skipped.");
                }
                Progress("Placing props and vegetation",0.87f);
                GeoPropBuilder.Build(root,env,buildings,field,notes,obj=>context.Save(obj),replace);
                notes.Add("Editable buildings use JSON footprints, floor counts and height. Roof styles and facade details are procedural defaults. Bridges use a simple raised deck. Tunnels are skipped.");
                if(!satellite) notes.Add("Ground roads and centre lines are painted into Terrain; terrain supplies their collision. Road detail: "+terrainData.alphamapResolution+" ("+(Mathf.Max(env.bounds.Width,env.bounds.Depth)/terrainData.alphamapResolution).ToString("F2")+" m per texel).");
                root.report=$"{root.roadCount} roads | {root.junctionCount} junctions | {root.riverCount} waterways | {root.waterCount} water bodies | {root.buildingCount} buildings | {root.treeCount} trees | {root.propCount} props\n"+
                    $"{env.bounds.Width:F0} x {env.bounds.Depth:F0} metres | {settings.heightResolution} height samples | {(satellite?"satellite imagery":layers.Length+" blended terrain layers")}\n"+string.Join("\n",notes);
                if(tile!=null) GeoTreeDataExporter.Export(root);
                asset.summary=root.report;
                Progress("Saving generated assets",0.96f);
                EditorUtility.SetDirty(asset);EditorUtility.SetDirty(root);EditorUtility.SetDirty(terrainData);
                AssetDatabase.SaveAssets();
                if(recordUndo&&tile==null)
                {
                    Undo.IncrementCurrentGroup();int undoGroup=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Generate Geo World");
                    Undo.RegisterCreatedObjectUndo(root.gameObject,"Generate Geo World");
                    if(replace!=null) Undo.DestroyObjectImmediate(replace.gameObject);
                    Undo.CollapseUndoOperations(undoGroup);
                }
                else if(replace!=null) Object.DestroyImmediate(replace.gameObject);
                EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
                if(tile==null){Selection.activeGameObject=root.gameObject;Debug.Log("Geo World generated. "+root.report,root);}
                return root;
            }
            catch
            {
                if(root!=null) Object.DestroyImmediate(root.gameObject);
                // Roll back only the newly allocated asset, never an earlier generation.
                if(!string.IsNullOrEmpty(assetPath)) AssetDatabase.DeleteAsset(assetPath);
                throw;
            }
            finally { if(tile==null) EditorUtility.ClearProgressBar(); }
        }

        // a tiled world shows one bar for all its tiles: each tile's steps fill its share
        internal static string progressPrefix="";
        internal static float progressFrom,progressSpan=1;
        static void Progress(string label,float value)
        { if(EditorUtility.DisplayCancelableProgressBar("Geo World Generator",progressPrefix+label,progressFrom+value*progressSpan)) throw new OperationCanceledException("Generation cancelled. Previous world preserved."); }

        // The building as the settings want it drawn: without its condition when damage is switched off (which buildings
        // are damaged, and on what evidence, is the app's to say); without the typical roof values when estimated roofs
        // are off. The export data itself is never changed.
        public static BuildingFeature ForSettings(BuildingFeature b,GenerationSettings s)
        {
            if(b==null)return b;
            bool dropCondition=b.condition!=null&&!s.applyCondition;
            bool dropRoof=!s.estimatedRoofs&&b.roof!=null&&b.roof.HasEstimates();
            if(!dropCondition&&!dropRoof)return b;
            var copy=Newtonsoft.Json.JsonConvert.DeserializeObject<BuildingFeature>(Newtonsoft.Json.JsonConvert.SerializeObject(b));
            if(dropCondition)copy.condition=null;
            if(dropRoof)copy.roof=copy.roof.WithoutEstimates();
            return copy;
        }

        internal static bool ValidBuilding(BuildingFeature b) => b!=null&&GeoData.Finite(b.x)&&GeoData.Finite(b.z)&&GeoData.Finite(b.width)&&GeoData.Finite(b.length)&&GeoData.Finite(b.height)&&GeoData.Finite(b.rotation)&&b.width>0&&b.length>0&&b.width<1000&&b.length<1000&&b.height<600;

        static Dictionary<string,GeoBuilding> BuildingEdits(GeoWorldRoot root)=>root?
            root.GetComponentsInChildren<GeoBuilding>(true).Where(b=>!string.IsNullOrEmpty(b.sourceId)).GroupBy(b=>b.sourceId).ToDictionary(g=>g.Key,g=>g.First()):
            new Dictionary<string,GeoBuilding>();

        static int CopyCustomBuildings(GeoWorldRoot previous,Transform group,Func<Vector3,float> ground,Action<Object> save,List<string> notes,GenerationSettings settings)
        {
            if(!previous)return 0;
            var oldGroup=previous.transform.Find("Buildings");if(!oldGroup)return 0;
            int count=0;
            // Imported buildings have a source ID. Manually created direct children
            // have none and must survive both building-only and whole-world rebuilds.
            foreach(Transform child in oldGroup)
            {
                var building=child.GetComponent<GeoBuilding>();
                if(!building||!string.IsNullOrEmpty(building.sourceId))continue;
                GeoBuildingBuilder.Create(null,group,ground,save,notes,building,settings);count++;
            }
            return count;
        }

        static void CountRuin(Dictionary<string,int> ruins,GeoBuilding building)
        {
            if(!GeoRuins.Draws(building))return;
            string id=building.ruin.id??building.ruin.name;
            ruins[id]=ruins.TryGetValue(id,out int used)?used+1:1;
        }

        public static void RebuildBuildings(GeoWorldRoot world,bool recordUndo=true)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Stop Play mode before rebuilding buildings.");
            if(!world.terrain)throw new InvalidOperationException("This world needs a terrain first.");
            if(world.transform.position!=Vector3.zero||world.transform.rotation!=Quaternion.identity||world.transform.lossyScale!=Vector3.one)
                throw new InvalidOperationException("Reset the world transform before rebuilding; building coordinates use world-space metres.");
            if(string.IsNullOrEmpty(world.buildingsPath))throw new InvalidOperationException("Select a buildings JSON file first.");
            var environment=string.IsNullOrEmpty(world.environmentPath)?null:GeoData.ReadEnvironment(world.environmentPath);
            var data=GeoData.ReadBuildings(world.buildingsPath,environment?.origin);
            var streets=new GeoStreets(environment?.roads);
            var edits=BuildingEdits(world);var old=world.transform.Find("Buildings");
            var terrain=world.terrain;var position=terrain.transform.position;var size=terrain.terrainData.size;
            var bounds=new GeoBounds{minX=position.x,minZ=position.z,maxX=position.x+size.x,maxZ=position.z+size.z};
            var notes=new List<string>();string path=null;Transform group=null;
            try
            {
                Directory.CreateDirectory(OutputFolder);AssetDatabase.Refresh();
                path=AssetDatabase.GenerateUniqueAssetPath(OutputFolder+"/Buildings.asset");
                var asset=ScriptableObject.CreateInstance<GeoGenerationAssets>();AssetDatabase.CreateAsset(asset,path);
                group=Group("Buildings",world.transform);int count=0;
                bool merged=GeoCityBuilder.ShouldMerge(world.settings,data.buildings.Count);
                if(merged)count+=GeoCityBuilder.Build(data.buildings,bounds,group,p=>terrain.SampleHeight(p)+position.y,obj=>AssetDatabase.AddObjectToAsset(obj,path),notes,world.settings,null,data.buildings.Count,streets);
                var ruins=new Dictionary<string,int>();GeoRuinFit.ClearCache();
                foreach(var source in merged?new List<BuildingFeature>():data.buildings)
                {
                    if(!ValidBuilding(source)||!bounds.Contains(source.x,source.z))continue;
                    edits.TryGetValue(source.id??"",out var previous);
                    CountRuin(ruins,GeoBuildingBuilder.Create(ForSettings(source,world.settings),group,p=>terrain.SampleHeight(p)+position.y,obj=>AssetDatabase.AddObjectToAsset(obj,path),notes,previous,world.settings,streets));count++;
                }
                count+=CopyCustomBuildings(world,group,p=>terrain.SampleHeight(p)+position.y,obj=>AssetDatabase.AddObjectToAsset(obj,path),notes,world.settings);
                if(ruins.Count>0)notes.Add(GeoCityBuilder.RuinNote(ruins));
                asset.summary=count+" editable footprint buildings.\n"+string.Join("\n",notes);
                EditorUtility.SetDirty(asset);AssetDatabase.SaveAssets();
                if(recordUndo)
                {
                    Undo.IncrementCurrentGroup();int undo=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Rebuild buildings");
                    Undo.RecordObject(world,"Rebuild buildings");Undo.RegisterCreatedObjectUndo(group.gameObject,"Rebuild buildings");
                    if(old)Undo.DestroyObjectImmediate(old.gameObject);Undo.CollapseUndoOperations(undo);
                }
                else if(old)Object.DestroyImmediate(old.gameObject);
                world.buildingCount=count;
                world.report=$"{world.roadCount} roads | {world.junctionCount} junctions | {world.riverCount} waterways | {world.waterCount} water bodies | {count} editable buildings | {world.treeCount} trees\n"+
                    "Buildings follow exported footprints. Select a building's parent to edit its outline, height, roof, floors and style.\n"+
                    $"Terrain: {terrain.terrainData.heightmapResolution} height samples; road paint: {terrain.terrainData.alphamapResolution}.\n"+string.Join("\n",notes);
                EditorUtility.SetDirty(world);EditorSceneManager.MarkSceneDirty(world.gameObject.scene);Selection.activeGameObject=group.gameObject;
                Debug.Log(asset.summary,world);
            }
            catch
            {
                if(group)Object.DestroyImmediate(group.gameObject);
                if(path!=null)AssetDatabase.DeleteAsset(path);throw;
            }
        }

        static void ApplyOverrides(EnvironmentData env,GeoWorldRoot root)
        {
            foreach(var path in root.GetComponentsInChildren<GeoPath>(true))
            {
                var target=(path.river?env.waterways:env.roads).FirstOrDefault(p=>p.id==path.sourceId);
                if(target==null) continue;
                target.width=path.width;target.bridge=path.bridge;target.material=GeoData.Material(path.surface);
                target.points=path.points.SelectMany(p=>new[]{p.x,p.z}).ToArray();
            }
        }

        // height: the ground under a point (the world's height field, or the export's heightmap for a tiled world)
        internal static List<PathBuild> PreparePaths(EnvironmentData env,Func<float,float,float> height,GenerationSettings settings,List<string> notes)
        {
            var result=new List<PathBuild>();int skipped=0,tunnels=0;
            for(int kind=0;kind<2;kind++) foreach(var feature in kind==0?env.roads:env.waterways)
            {
                if(feature==null) { skipped++;continue; }
                if(feature.tunnel) { tunnels++;continue; }
                var controls=GeoData.Points(feature.points);
                if(controls.Count<2||!GeoData.Finite(feature.width)) { skipped++;continue; }
                feature.width=Mathf.Clamp(feature.width>0?feature.width:kind==0?5.5f:3,0.5f,120);
                if(string.IsNullOrEmpty(feature.material)) feature.material=kind==0?"asphalt":"water";
                var samples=GeoData.Resample(controls,settings.pathStep);
                for(int i=0;i<samples.Count;i++)
                {
                    var p=samples[i];p.y=height(p.x,p.z)-(kind==1?0.35f:0);samples[i]=p;
                }
                if(feature.bridge&&kind==0)
                {
                    float start=samples[0].y,end=samples[samples.Count-1].y,total=0;
                    var distances=new float[samples.Count];
                    for(int i=1;i<samples.Count;i++) { total+=Vector3.Distance(samples[i-1],samples[i]);distances[i]=total; }
                    float clearance=Mathf.Clamp(total*0.10f,1,5);
                    for(int i=0;i<samples.Count;i++)
                    { var p=samples[i];float t=distances[i]/Mathf.Max(total,0.001f);p.y=Mathf.Lerp(start,end,t)+Mathf.Sin(t*Mathf.PI)*clearance;samples[i]=p; }
                }
                else if(feature.layer!=0&&kind==0)
                {
                    // Layer is topological, not surveyed height. Keep separate-level roads
                    // separate using a stated placeholder offset until real deck heights exist.
                    for(int i=0;i<samples.Count;i++) {var p=samples[i];p.y+=feature.layer*5f;samples[i]=p;}
                    notes.Add("Road "+feature.id+": estimated height offset of "+(feature.layer*5)+" m for source layer "+feature.layer+".");
                }
                result.Add(new PathBuild {feature=feature,river=kind==1,controls=controls,samples=samples});
            }
            if(skipped>0) notes.Add(skipped+" invalid or empty paths were skipped.");
            if(tunnels>0) notes.Add(tunnels+" tunnels/culverts skipped (no surface road or water).");
            return result;
        }

        internal static float[] WaterLevels(List<AreaFeature> water,Func<float,float,float> height)
        {
            var result=new float[water.Count];
            for(int i=0;i<water.Count;i++)
            {
                var values=new List<float>();
                foreach(var p in GeoData.Points(water[i].rings?.FirstOrDefault())) values.Add(height(p.x,p.z));
                values.Sort();result[i]=values.Count>0?values[Mathf.Clamp(values.Count/4,0,values.Count-1)]-0.3f:0;
            }
            return result;
        }

        static void CarveLakes(List<AreaFeature> water,float[] levels,HeightField field,float depth)
        {
            for(int i=0;i<water.Count;i++) for(int z=0;z<field.resolution;z++) for(int x=0;x<field.resolution;x++)
                if(GeoData.InPolygon(field.X(x),field.Z(z),water[i].rings)) field.heights[z,x]=Mathf.Min(field.heights[z,x],levels[i]-depth);
        }

        static Mesh LakeMesh(AreaFeature water,float level,GeoBounds bounds)
        {
            var mesh=new MeshDraft();var ring=GeoData.Points(water.rings?.FirstOrDefault());
            if(ring.Count<3)return mesh.Mesh("Empty water");
            float minX=Mathf.Max(bounds.minX,ring.Min(p=>p.x)),maxX=Mathf.Min(bounds.maxX,ring.Max(p=>p.x));
            float minZ=Mathf.Max(bounds.minZ,ring.Min(p=>p.z)),maxZ=Mathf.Min(bounds.maxZ,ring.Max(p=>p.z));
            float step=Mathf.Max(1.5f,Mathf.Max(maxX-minX,maxZ-minZ)/384);
            for(float z=minZ;z<maxZ;z+=step) for(float x=minX;x<maxX;x+=step)
            {
                float x1=Mathf.Min(x+step,maxX),z1=Mathf.Min(z+step,maxZ);
                // Test corners and centre so islands (inner rings) remain dry.
                if(!GeoData.InPolygon(x,z,water.rings)||!GeoData.InPolygon(x1,z,water.rings)||
                    !GeoData.InPolygon(x,z1,water.rings)||!GeoData.InPolygon(x1,z1,water.rings)||
                    !GeoData.InPolygon((x+x1)/2,(z+z1)/2,water.rings))continue;
                mesh.Quad(new Vector3(x,level,z),new Vector3(x,level,z1),new Vector3(x1,level,z1),new Vector3(x1,level,z),
                    new Vector2(x,z)/10,new Vector2(x,z1)/10,new Vector2(x1,z1)/10,new Vector2(x1,z)/10);
            }
            return mesh.Mesh("Water body surface");
        }

        static void BridgeRails(PathBuild path,BuildContext context,Transform parent)
        {
            var draft=new MeshDraft();
            for(int i=1;i<path.samples.Count;i++)foreach(float sign in new[]{-1f,1f})
            {
                Vector3 a=path.samples[i-1]+GeoMeshes.Side(path.samples,i-1)*(path.feature.width*0.5f*sign);
                Vector3 b=path.samples[i]+GeoMeshes.Side(path.samples,i)*(path.feature.width*0.5f*sign);
                Vector3 lift=Vector3.up*0.9f,thickness=GeoMeshes.Side(path.samples,i)*0.1f;
                draft.Quad(a+lift-thickness,b+lift-thickness,b+lift+thickness,a+lift+thickness);
                draft.Quad(a,b,b+lift,a+lift);draft.Quad(b,a,a+lift,b+lift);
            }
            var rails=context.Clip(draft.Mesh("Bridge parapets"));
            if(rails.vertexCount>0) context.MeshChild("Bridge parapets",parent,rails,context.Foundation,true);
            else Object.DestroyImmediate(rails);
        }

        static Transform Group(string name,Transform parent) {var go=new GameObject(name);go.transform.SetParent(parent);return go.transform;}

        // Pieces of road or crossing, gathered per chunk (by their middle) and material and drawn as one mesh each, an
        // object named "<name> x_z | material". Pieces with soft edges (TexCoord1) and without are kept apart.
        class MeshBatches
        {
            class Piece {public Vector3[] vertices,normals;public List<Vector2> uv=new List<Vector2>(),uv2=new List<Vector2>();public Mesh mesh;}
            readonly string name;
            readonly float size;
            readonly Dictionary<(int x,int z,Material material,bool edges),List<(Piece piece,int submesh)>> groups=new Dictionary<(int,int,Material,bool),List<(Piece,int)>>();
            readonly List<Mesh> sources=new List<Mesh>();
            public MeshBatches(string name,float chunkSize){this.name=name;size=Mathf.Max(50,chunkSize);}

            public void Add(Mesh mesh,Material[] materials)
            {
                sources.Add(mesh);
                if(mesh.vertexCount==0)return;
                var piece=new Piece{mesh=mesh,vertices=mesh.vertices,normals=mesh.normals};
                mesh.GetUVs(0,piece.uv);mesh.GetUVs(1,piece.uv2);
                bool edges=piece.uv2.Count==piece.vertices.Length;
                var middle=mesh.bounds.center;int x=Mathf.FloorToInt(middle.x/size),z=Mathf.FloorToInt(middle.z/size);
                for(int s=0;s<mesh.subMeshCount&&s<materials.Length;s++)
                {
                    if(!materials[s]||mesh.GetIndexCount(s)==0)continue;
                    var key=(x,z,materials[s],edges);
                    if(!groups.TryGetValue(key,out var list))groups[key]=list=new List<(Piece,int)>();
                    list.Add((piece,s));
                }
            }

            public void Build(Transform parent,BuildContext context)
            {
                foreach(var pair in groups.OrderBy(g=>g.Key.z).ThenBy(g=>g.Key.x).ThenBy(g=>g.Key.material.name,StringComparer.Ordinal).ThenBy(g=>g.Key.edges))
                {
                    var draft=new MeshDraft();var normals=new List<Vector3>();
                    foreach(var (piece,submesh) in pair.Value)
                    {
                        // only the vertices this sub-mesh uses
                        var index=new Dictionary<int,int>();
                        foreach(int v in piece.mesh.GetTriangles(submesh))
                        {
                            if(!index.TryGetValue(v,out int n))
                            {
                                n=draft.vertices.Count;index[v]=n;draft.vertices.Add(piece.vertices[v]);
                                normals.Add(piece.normals.Length>v?piece.normals[v]:Vector3.up);
                                draft.uv.Add(piece.uv.Count>v?piece.uv[v]:Vector2.zero);
                                if(pair.Key.edges)draft.uv2.Add(piece.uv2[v]);
                            }
                            draft.triangles.Add(n);
                        }
                    }
                    var mesh=draft.Mesh("Junctions",p=>Vector3.up);mesh.SetNormals(normals);
                    var go=new GameObject(name+" "+pair.Key.x+"_"+pair.Key.z+" | "+pair.Key.material.name);go.transform.SetParent(parent,false);
                    context.MeshObject(go,mesh,pair.Key.material,false);
                }
                foreach(var mesh in sources)Object.DestroyImmediate(mesh);
            }
        }

        class BuildContext
        {
            readonly string path;
            readonly Dictionary<string,Material> materials=new Dictionary<string,Material>();
            readonly Shader shader;
            // a tile: meshes keep only the triangles on the tile (by their centre), so neighbours meet without overlap
            public GeoBounds clip;
            public Mesh Clip(Mesh mesh)=>clip!=null?GeoTiles.Clip(mesh,clip):mesh;
            public BuildContext(string path,GeoWorldRoot root)
            {this.path=path;shader=Shader.Find("Universal Render Pipeline/Lit")??Shader.Find("Standard");}
            public T Save<T>(T asset) where T:Object {AssetDatabase.AddObjectToAsset(asset,path);return asset;}
            Material Mat(string name,Color color,float smoothness=0.1f,bool texture=false)
            {
                if(materials.TryGetValue(name,out var cached))return cached;
                var material=new Material(shader){name=name,enableInstancing=true};
                material.color=color;if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",color);
                material.SetFloat("_Smoothness",smoothness);
                if(texture)
                {
                    var tex=Save(GeoTerrain.Texture(name+" texture",color,73));
                    material.mainTexture=tex;material.color=Color.white;
                    if(material.HasProperty("_BaseMap"))material.SetTexture("_BaseMap",tex);
                    if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",Color.white);
                }
                materials[name]=Save(material);return material;
            }
            // A road material from the photographic ground textures (UVs are world metres / 4).
            Material Photo(string name,int layer,Color tint,float tile)
            {
                if(materials.TryGetValue(name,out var cached))return cached;
                var diffuse=AssetDatabase.LoadAssetAtPath<Texture2D>(GeoTerrainTextures.DiffusePath(layer));
                var normal=AssetDatabase.LoadAssetAtPath<Texture2D>(GeoTerrainTextures.NormalPath(layer));
                if(!diffuse||!normal)return null;
                var material=new Material(shader){name=name,enableInstancing=true};
                material.SetTexture("_BaseMap",diffuse);material.mainTexture=diffuse;
                material.SetColor("_BaseColor",tint);material.color=tint;
                material.SetTextureScale("_BaseMap",new Vector2(4/tile,4/tile));
                material.SetTexture("_BumpMap",normal);material.SetTextureScale("_BumpMap",new Vector2(4/tile,4/tile));
                material.EnableKeyword("_NORMALMAP");material.SetFloat("_BumpScale",1f);
                material.SetFloat("_Smoothness",0.1f);
                materials[name]=Save(material);return material;
            }
            bool photosConfigured;
            // A photo-scanned road surface (GeoRoadPhotos): u across the road, v along it.
            public Material RoadPhoto(string id)
            {
                string name="Road "+id;
                if(materials.TryGetValue(name,out var cached))return cached;
                if(!photosConfigured){GeoRoadPhotos.Configure();photosConfigured=true;}
                var diffuse=AssetDatabase.LoadAssetAtPath<Texture2D>(GeoRoadPhotos.ColorPath(id));
                var normal=AssetDatabase.LoadAssetAtPath<Texture2D>(GeoRoadPhotos.NormalPath(id));
                if(!diffuse||!normal)return null;
                var material=new Material(shader){name=name,enableInstancing=true};
                material.SetTexture("_BaseMap",diffuse);material.mainTexture=diffuse;
                material.SetColor("_BaseColor",Color.white);material.color=Color.white;
                material.SetTexture("_BumpMap",normal);material.EnableKeyword("_NORMALMAP");material.SetFloat("_BumpScale",1f);
                material.SetFloat("_Smoothness",0.15f);
                materials[name]=Save(material);return material;
            }
            // One strip of generated worn asphalt (GeoRoadTextures): u across the road, v along it.
            public Material RoadStrip(int variant)
            {
                string name="Ukrainian asphalt "+variant;
                if(materials.TryGetValue(name,out var cached))return cached;
                var diffuse=AssetDatabase.LoadAssetAtPath<Texture2D>(GeoRoadTextures.DiffusePath(variant));
                var normal=AssetDatabase.LoadAssetAtPath<Texture2D>(GeoRoadTextures.NormalPath(variant));
                if(!diffuse||!normal)return null;
                var material=new Material(shader){name=name,enableInstancing=true};
                material.SetTexture("_BaseMap",diffuse);material.mainTexture=diffuse;
                material.SetColor("_BaseColor",Color.white);material.color=Color.white;
                material.SetTexture("_BumpMap",normal);material.EnableKeyword("_NORMALMAP");material.SetFloat("_BumpScale",1f);
                material.SetFloat("_Smoothness",0.12f);
                materials[name]=Save(material);return material;
            }
            // The material a road surface is drawn with: a copy of its URP Lit material on GeoWorld/Road, which looks the
            // same but fades out over the road's edges (the mesh's TexCoord1, GeoMeshes.RoadEdges) into the ground under it.
            readonly Dictionary<Material,Material> blended=new Dictionary<Material,Material>();
            public Material Blend(Material source)
            {
                if(!source)return source;
                if(blended.TryGetValue(source,out var found))return found;
                var road=Shader.Find("GeoWorld/Road");
                if(!road){blended[source]=source;return source;}
                var material=new Material(road){name=source.name+" (soft edges)",enableInstancing=true};
                foreach(var texture in new[]{"_BaseMap","_BumpMap"})
                    if(source.HasProperty(texture))
                    {
                        material.SetTexture(texture,source.GetTexture(texture));
                        material.SetTextureScale(texture,source.GetTextureScale(texture));material.SetTextureOffset(texture,source.GetTextureOffset(texture));
                    }
                foreach(var number in new[]{"_BumpScale","_Smoothness"})if(source.HasProperty(number))material.SetFloat(number,source.GetFloat(number));
                if(source.HasProperty("_BaseColor"))material.SetColor("_BaseColor",source.GetColor("_BaseColor"));
                if(!source.IsKeywordEnabled("_NORMALMAP"))material.SetFloat("_BumpScale",0);
                blended[source]=material=Save(material);
                return material;
            }
            public Material Surface(GroundSurface s)
            {
                if(GeoTerrainTextures.Available)
                {
                    Material photo=null;
                    switch(s)
                    {case GroundSurface.Gravel:photo=Photo("Gravel road",4,new Color(0.9f,0.9f,0.9f),2f);break;
                     case GroundSurface.Dirt:photo=Photo("Dirt track",11,Color.white,3.1f);break;
                     case GroundSurface.Paving:photo=Photo("Paved road",5,Color.white,1.8f);break;
                     default:photo=Photo("Asphalt road",8,new Color(0.62f,0.62f,0.64f),5f);break;}
                    if(photo)return photo;
                }
                switch(s)
                {case GroundSurface.Gravel:return Mat("Gravel road",new Color(0.45f,0.43f,0.36f),0.04f,true);
                 case GroundSurface.Dirt:return Mat("Dirt track",new Color(0.38f,0.27f,0.16f),0.02f,true);
                 case GroundSurface.Paving:return Mat("Paved road",new Color(0.55f,0.52f,0.46f),0.12f,true);
                 default:return Mat("Asphalt road",new Color(0.13f,0.15f,0.16f),0.18f,true);}
            }
            public Material Water=>Mat("River water",new Color(0.08f,0.35f,0.39f),0.88f);
            public Material Marking=>Mat("Road paint",new Color(0.74f,0.74f,0.70f),0.05f);
            public Material Foundation=>Mat("Concrete",new Color(0.43f,0.43f,0.39f));
            public Material Roof=>Mat("Terracotta roofs",new Color(0.39f,0.15f,0.10f));
            public Material FlatRoof=>Mat("Flat roofs",new Color(0.22f,0.26f,0.27f));
            public Material Windows=>Mat("Window glass",new Color(0.12f,0.23f,0.28f),0.7f);
            public Material Trunk=>Mat("Tree bark",new Color(0.26f,0.19f,0.12f));
            public Material Leaves=>Mat("Tree foliage",new Color(0.16f,0.29f,0.12f));
            public Material Wall(string category)=>category=="industrial"?Mat("Industrial walls",new Color(0.56f,0.60f,0.58f)):
                category=="apartments"?Mat("Apartment walls",new Color(0.71f,0.68f,0.57f)):Mat("House walls",new Color(0.77f,0.72f,0.57f));
            public void MeshObject(GameObject go,Mesh mesh,Material material,bool collider)
            {MeshObject(go,mesh,new[]{material},collider);}
            public void MeshObject(GameObject go,Mesh mesh,Material[] materials,bool collider)
            {mesh=GeoMeshCompression.Compact(mesh);Save(mesh);go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterials=materials;if(collider&&mesh.vertexCount>=3)go.AddComponent<MeshCollider>().sharedMesh=mesh;}
            public void MeshChild(string name,Transform parent,Mesh mesh,Material material,bool collider)
            {var go=new GameObject(name);go.transform.SetParent(parent,false);MeshObject(go,mesh,material,collider);}
            public void Primitive(string name,PrimitiveType type,Transform parent,Vector3 position,Vector3 scale,Material material,bool collider)
            {var go=GameObject.CreatePrimitive(type);go.name=name;go.transform.SetParent(parent,false);go.transform.localPosition=position;go.transform.localScale=scale;go.GetComponent<Renderer>().sharedMaterial=material;if(!collider)Object.DestroyImmediate(go.GetComponent<Collider>());}
        }
    }
}
