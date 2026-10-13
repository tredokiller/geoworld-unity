using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GeoWorld.Editor
{
    public class GeoWorldWindow : EditorWindow
    {
        [SerializeField] string environmentPath="",buildingsPath="";
        [SerializeField] GenerationSettings settings=new GenerationSettings();
        [SerializeField] GeoWorldRoot replace;
        [SerializeField] float tileSize=1000;
        Vector2 scroll;
        string message="Select an exported folder, or build the included riverside village.";

        [MenuItem("Tools/Geo World/Generator window",false,1)]
        public static void Open() { var window=GetWindow<GeoWorldWindow>("Geo World");window.minSize=new Vector2(400,580); }

        // the project's ruin and home libraries are the defaults once the models are imported
        void OnEnable() { if(settings!=null)GeoDefaults.FillLibraries(settings,false); }

        void OnGUI()
        {
            scroll=EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space(12);
            GUILayout.Label("GEO WORLD",new GUIStyle(EditorStyles.boldLabel){fontSize=23});
            GUILayout.Label("Roads, rivers & buildings from your map",EditorStyles.miniLabel);
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("Download Terrain & ground (.zip) from Building Extractor and unzip it. Select its folder below. All coordinates stay in metres: X east, Z north.",MessageType.Info);
            if(GUILayout.Button("Choose export folder...",GUILayout.Height(32)))
            {
                string folder=EditorUtility.OpenFolderPanel("Select extracted terrain and ground package",InitialFolder(),"");
                if(!string.IsNullOrEmpty(folder))
                {
                    environmentPath=File.Exists(Path.Combine(folder,"environment.json"))?Path.Combine(folder,"environment.json"):"";
                    buildingsPath=File.Exists(Path.Combine(folder,"buildings.json"))?Path.Combine(folder,"buildings.json"):"";
                    message=string.IsNullOrEmpty(environmentPath)?"No environment.json found. You can still select a buildings export.":"Export selected. Generate to create a new world.";
                }
            }
            FileField("Environment JSON",ref environmentPath);
            FileField("Buildings JSON",ref buildingsPath);
            EditorGUILayout.Space(12);
            GUILayout.Label("Generation",EditorStyles.boldLabel);
            settings.heightResolution=EditorGUILayout.IntPopup("Heightmap resolution",settings.heightResolution,new[]{"257","513","1025"},new[]{257,513,1025});
            settings.textureResolution=EditorGUILayout.IntPopup("Ground texture resolution",settings.textureResolution,new[]{"256","512","1024"},new[]{256,512,1024});
            settings.roadPaintResolution=EditorGUILayout.IntPopup("Road paint detail",settings.roadPaintResolution,new[]{"1024","2048","4096"},new[]{1024,2048,4096});
            EditorGUILayout.HelpBox("Ground roads and lines are painted directly on the terrain. Higher road detail gives sharper edges and uses more memory.",MessageType.None);
            settings.pathStep=EditorGUILayout.Slider("Road segment length (m)",settings.pathStep,0.5f,8);
            settings.roadShoulder=EditorGUILayout.Slider("Road shoulder (m)",settings.roadShoulder,0,8);
            settings.junctionSnap=EditorGUILayout.Slider("Junction snap distance (m)",settings.junctionSnap,0,3);
            settings.blendMetres=EditorGUILayout.Slider("Ground blend (m)",settings.blendMetres,0,10);
            settings.riverDepth=EditorGUILayout.Slider("River depth (m)",settings.riverDepth,0.5f,5);
            settings.gradeRoads=EditorGUILayout.Toggle("Grade terrain under roads",settings.gradeRoads);
            settings.carveRivers=EditorGUILayout.Toggle("Carve river beds",settings.carveRivers);
            settings.laneMarkings=EditorGUILayout.Toggle("Road centre markings",settings.laneMarkings);
            settings.roadMeshes=EditorGUILayout.Toggle("Road meshes (sharp edges, markings)",settings.roadMeshes);
            settings.buildings=EditorGUILayout.Toggle("Buildings",settings.buildings);
            using(new EditorGUI.DisabledScope(!settings.buildings))
            {
                settings.buildingMode=(GeoBuildingMode)EditorGUILayout.EnumPopup("Building mode",settings.buildingMode);
                settings.mergeAbove=EditorGUILayout.IntSlider("Merge above (buildings)",settings.mergeAbove,100,20000);
                settings.chunkSize=EditorGUILayout.Slider("Merged chunk size (m)",settings.chunkSize,50,500);
                settings.mergedColliders=EditorGUILayout.Toggle("Colliders on merged chunks",settings.mergedColliders);
                settings.applyCondition=EditorGUILayout.Toggle("Draw war damage (condition)",settings.applyCondition);
                settings.estimatedRoofs=EditorGUILayout.Toggle("Typical roofs where not mapped",settings.estimatedRoofs);
                using(new EditorGUI.DisabledScope(!settings.applyCondition))
                {
                    settings.ruinModels=(GeoRuinUse)EditorGUILayout.EnumPopup("Ruin models for",settings.ruinModels);
                    settings.ruinLibrary=(GeoRuinLibrary)EditorGUILayout.ObjectField("Ruin library",settings.ruinLibrary,typeof(GeoRuinLibrary),false);
                    settings.ruinApron=EditorGUILayout.Slider("Ruin yard and debris (m)",settings.ruinApron,0,12);
                    settings.reducedModels=EditorGUILayout.Toggle("Reduced models in a merged city",settings.reducedModels);
                    settings.instancedHomes=EditorGUILayout.Toggle("Homes as model instances (merged)",settings.instancedHomes);
                    if(!settings.ruinLibrary&&GUILayout.Button("Import ruin models (ruins/models)"))
                        Run(()=>{GeoRuinImporter.ImportMenu();settings.ruinLibrary=GeoRuinImporter.Library;});
                }
            }
            EditorGUILayout.HelpBox("Editable: one object per building, for villages and edits. Merged: buildings baked into chunks of mesh, for a city (Auto merges above the limit). Damage comes from the export's building condition; unknown buildings are drawn intact. Roofs come from the export's roof block: mapped and reviewed values always, and values estimated for the kind of building (not observed) unless you turn them off. Damaged buildings are drawn with the ruin library's destroyed-building models where one fits (fitted to the footprint by whole bays and storeys), the rest with procedural damage.",MessageType.None);
            settings.groundTextures=EditorGUILayout.Toggle("Photographic ground textures",settings.groundTextures);
            if(settings.groundTextures&&!GeoTerrainTextures.Available&&GUILayout.Button("Import ground textures from the Godot project"))GeoTerrainTextures.ImportMenu();
            settings.satelliteImagery=EditorGUILayout.Toggle("Satellite imagery on terrain",settings.satelliteImagery);
            if(settings.satelliteImagery)
                using(new EditorGUI.IndentLevelScope())
                {
                    settings.satelliteResolution=EditorGUILayout.IntPopup("Texture size",settings.satelliteResolution,new[]{"Auto (0.4 m a texel)","1024","2048","4096","8192"},new[]{0,1024,2048,4096,8192});
                    settings.satelliteMaxZoom=EditorGUILayout.IntSlider("Deepest tile zoom",settings.satelliteMaxZoom,1,22);
                    settings.satelliteTiles=EditorGUILayout.TextField("Tile URL {z}/{x}/{y}",settings.satelliteTiles);
                    if(GUILayout.Button("Esri World Imagery",GUILayout.Width(160)))settings.satelliteTiles=GeoSatellite.EsriWorldImagery;
                    EditorGUILayout.HelpBox("Replaces the blended ground layers with one texture resampled from map tiles (downloaded once, cached in Library/GeoWorld/SatelliteTiles). "+GeoSatellite.Attribution(settings.satelliteTiles)+". Check the tile server's terms before storing or shipping the imagery. Roads are seen only in the imagery unless Road meshes is on.",MessageType.None);
                }
            settings.trees=EditorGUILayout.Toggle("Mapped trees",settings.trees);
            settings.mappedProps=EditorGUILayout.Toggle("Mapped street props / fences",settings.mappedProps);
            settings.vegetation=EditorGUILayout.Toggle("Vegetation from map / rows",settings.vegetation);
            settings.streetDecoration=EditorGUILayout.Toggle("Generated street decoration",settings.streetDecoration);
            settings.propCatalog=(GeoPropCatalog)EditorGUILayout.ObjectField("Prop prefab catalog",settings.propCatalog,typeof(GeoPropCatalog),false);
            settings.treeSpacing=EditorGUILayout.Slider("Tree spacing (m)",settings.treeSpacing,3,25);
            settings.vegetationDensity=EditorGUILayout.Slider("Vegetation density",settings.vegetationDensity,0,1);
            settings.decorationSeed=EditorGUILayout.IntField("Decoration seed",settings.decorationSeed);
            settings.maxProps=EditorGUILayout.IntSlider("Maximum props",settings.maxProps,100,200000);
            settings.mergeProps=EditorGUILayout.Toggle("Bake props in a merged city",settings.mergeProps);
            EditorGUILayout.HelpBox("Mapped objects use source positions. Vegetation from coverage and tree rows has generated positions. Assign a prop catalog to use your prefabs; otherwise preview models are used. Select a prop and enable Keep On Rebuild to retain edits. Editable worlds stop at 20 000 props; a merged city bakes props into chunks and can hold far more, but they are no longer separate objects.",MessageType.None);
            EditorGUILayout.Space(8);
            replace=(GeoWorldRoot)EditorGUILayout.ObjectField("Replace generated world",replace,typeof(GeoWorldRoot),true);
            EditorGUILayout.HelpBox("Replacement keeps edited road widths and nodes, and rebuilds from the original terrain. Ctrl+Z restores the previous world. Leave empty to add a separate world.",MessageType.None);
            using(new EditorGUI.DisabledScope(Application.isPlaying||(string.IsNullOrEmpty(environmentPath)&&string.IsNullOrEmpty(buildingsPath))))
                if(GUILayout.Button(replace==null?"Generate world":"Rebuild selected world",GUILayout.Height(38)))
                    Run(()=> {replace=GeoGenerator.Generate(environmentPath,buildingsPath,settings,replace);message=replace.report;Frame(replace);});
            EditorGUILayout.Space(12);
            GUILayout.Label("Large area: tiles",EditorStyles.boldLabel);
            tileSize=EditorGUILayout.Slider("Tile size (m)",tileSize,250,4000);
            EditorGUILayout.HelpBox("Cuts the area into square tiles, each with its own terrain, roads, buildings and props, in a scene and asset of its own (Assets/GeoWorld/Tiled/<export>). Heights, roads and lakes are worked out for the whole export, so tiles meet without steps. Rebuilding skips tiles whose export and settings did not change. The resolutions above are per tile.",MessageType.None);
            using(new EditorGUI.DisabledScope(Application.isPlaying||string.IsNullOrEmpty(environmentPath)))
                if(GUILayout.Button("Generate tiled world",GUILayout.Height(32)))
                    Run(()=>{if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;var world=GeoTiles.Generate(environmentPath,buildingsPath,settings,tileSize);message=world.report;});
            EditorGUILayout.Space(12);
            GUILayout.Label("Sample",EditorStyles.boldLabel);
            using(new EditorGUI.DisabledScope(Application.isPlaying))
                if(GUILayout.Button("Create / open riverside sample scene",GUILayout.Height(32))) Run(()=>GeoSample.CreateScene());
            EditorGUILayout.HelpBox(message,MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        string InitialFolder()=>string.IsNullOrEmpty(environmentPath)?Application.dataPath:Path.GetDirectoryName(environmentPath);
        static void FileField(string label,ref string path)
        {
            EditorGUILayout.BeginHorizontal();path=EditorGUILayout.TextField(label,path);
            if(GUILayout.Button("...",GUILayout.Width(28)))
            {string chosen=EditorUtility.OpenFilePanel(label,Application.dataPath,"json");if(!string.IsNullOrEmpty(chosen))path=chosen;}
            EditorGUILayout.EndHorizontal();
        }
        void Run(Action action)
        {try{action();}catch(OperationCanceledException e){message=e.Message;}catch(Exception e){message=e.Message;Debug.LogException(e);}}
        public static void Frame(GeoWorldRoot world)
        {
            if(world.terrain==null||SceneView.lastActiveSceneView==null)return;
            Vector3 size=world.terrain.terrainData.size;
            SceneView.lastActiveSceneView.LookAt(world.terrain.transform.position+size*0.5f,Quaternion.Euler(55,-25,0),Mathf.Max(size.x,size.z)*0.65f);
        }
    }

    [CustomEditor(typeof(GeoWorldRoot))]
    public class GeoWorldRootEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var root=(GeoWorldRoot)target;
            EditorGUILayout.HelpBox(root.report??"Generated world",MessageType.Info);
            // a world made before the ruin models were imported has no library: offer the project's
            if(root.settings!=null&&!root.settings.ruinLibrary&&root.settings.ruinModels!=GeoRuinUse.Off&&GeoRuinImporter.Library)
            {
                EditorGUILayout.HelpBox("The ruin models are imported, but this world has no Ruin Library: its damaged buildings are drawn with procedural damage.",MessageType.Warning);
                if(GUILayout.Button("Use the project's ruin library (then Rebuild buildings only)"))
                {Undo.RecordObject(root,"Use ruin library");root.settings.ruinLibrary=GeoRuinImporter.Library;EditorUtility.SetDirty(root);}
            }
            DrawDefaultInspector();
            if(GUILayout.Button("Rebuild roads, rivers and terrain",GUILayout.Height(32)))
                try {GeoGenerator.Generate(root.environmentPath,root.buildingsPath,root.settings,root);}catch(Exception e){Debug.LogException(e);}
            if(GUILayout.Button("Rebuild buildings only",GUILayout.Height(30)))
                try {GeoGenerator.RebuildBuildings(root);}catch(Exception e){Debug.LogException(e);}
            if(GUILayout.Button("Rebuild props and vegetation only",GUILayout.Height(30)))
                try {GeoPropBuilder.Rebuild(root);}catch(Exception e){Debug.LogException(e);}
            if(GUILayout.Button("Frame world"))GeoWorldWindow.Frame(root);
            EditorGUILayout.HelpBox("Select a Road or River child to edit its width or move its control points. Generated assets are saved under Assets/GeoWorld/Generated.",MessageType.None);
        }
    }

    [CustomEditor(typeof(GeoPath))]
    public class GeoPathEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var path=(GeoPath)target;
            EditorGUILayout.LabelField(path.river?"River path":"Road path",EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Source",path.sourceId);
            EditorGUILayout.LabelField("Source layer",path.layer.ToString());
            EditorGUILayout.HelpBox("Move the numbered nodes in Scene view or edit their X/Z values. Rebuild applies the path and width, regrades the terrain and repaints roads, lines and shoulders. Y is sampled automatically.",MessageType.Info);
            DrawDefaultInspector();
            if(GUILayout.Button("Insert node in longest segment"))
            {
                if(path.points.Count>=2)
                {
                    Undo.RecordObject(path,"Insert path node");int index=1;float length=0;
                    for(int i=1;i<path.points.Count;i++) {float d=(path.points[i]-path.points[i-1]).sqrMagnitude;if(d>length){length=d;index=i;}}
                    path.points.Insert(index,(path.points[index-1]+path.points[index])*0.5f);EditorUtility.SetDirty(path);
                }
            }
            if(GUILayout.Button("Apply path & rebuild world",GUILayout.Height(32)))
            {
                var world=path.GetComponentInParent<GeoWorldRoot>();
                if(world!=null)try{GeoGenerator.Generate(world.environmentPath,world.buildingsPath,world.settings,world);}catch(Exception e){Debug.LogException(e);}
            }
        }

        void OnSceneGUI()
        {
            var path=(GeoPath)target;
            Handles.color=path.river?Color.cyan:new Color(1,0.8f,0.15f);
            for(int i=0;i<path.points.Count;i++)
            {
                Vector3 point=path.points[i];
                Handles.Label(point+Vector3.up, i.ToString());
                EditorGUI.BeginChangeCheck();
                Vector3 moved=Handles.Slider2D(point,Vector3.up,Vector3.right,Vector3.forward,
                    HandleUtility.GetHandleSize(point)*0.09f,Handles.DotHandleCap,Vector2.zero);
                if(EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(path,"Move path node");path.points[i]=new Vector3(moved.x,point.y,moved.z);
                    EditorUtility.SetDirty(path);EditorSceneManager.MarkSceneDirty(path.gameObject.scene);
                }
                if(i>0) Handles.DrawAAPolyLine(3,path.points[i-1],path.points[i]);
            }
        }
    }
}
