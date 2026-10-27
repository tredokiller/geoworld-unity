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

        // tile: one tile of a tiled world (GeoTiles); null: one world. The world is made by GeoWorldBuild and saved here as a
        // World asset in the output folder (a tile: its folder), with Undo for a world in one piece.
        internal static GeoWorldRoot Generate(string environmentPath, string buildingsPath, GenerationSettings settings, GeoWorldRoot replace, bool recordUndo, GeoTileJob tile)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before generating.");
            if (replace != null && (replace.transform.position != Vector3.zero || replace.transform.rotation != Quaternion.identity || replace.transform.lossyScale != Vector3.one))
                throw new InvalidOperationException("Reset the generated world's transform before rebuilding; coordinates use world-space metres.");
            settings = GeoWorldBuild.Sanitize(settings);
            GeoWorldRoot root = null;
            string assetPath = null;
            try
            {
                string folder = tile?.folder ?? OutputFolder;
                if (!Directory.Exists(folder)) { Directory.CreateDirectory(folder); AssetDatabase.Refresh(); }
                var asset = ScriptableObject.CreateInstance<GeoGenerationAssets>();
                assetPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + (tile?.name ?? "World") + ".asset");
                AssetDatabase.CreateAsset(asset, assetPath);
                string path = assetPath;
                using (GeoGeneration.Use(GeoEditorGeneration.Target))
                    root = GeoWorldBuild.Build(environmentPath, buildingsPath, settings, replace, tile, assetPath, obj => AssetDatabase.AddObjectToAsset(obj, path));
                if (tile != null) GeoTreeDataExporter.Export(root);
                asset.summary = root.report;
                Progress("Saving generated assets", 0.96f);
                EditorUtility.SetDirty(asset); EditorUtility.SetDirty(root); EditorUtility.SetDirty(root.terrain.terrainData);
                AssetDatabase.SaveAssets();
                if (recordUndo && tile == null)
                {
                    Undo.IncrementCurrentGroup(); int undoGroup = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Generate Geo World");
                    Undo.RegisterCreatedObjectUndo(root.gameObject, "Generate Geo World");
                    if (replace != null) Undo.DestroyObjectImmediate(replace.gameObject);
                    Undo.CollapseUndoOperations(undoGroup);
                }
                else if (replace != null) Object.DestroyImmediate(replace.gameObject);
                EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
                if (tile == null) { Selection.activeGameObject = root.gameObject; Debug.Log("Geo World generated. " + root.report, root); }
                return root;
            }
            catch
            {
                if (root != null) Object.DestroyImmediate(root.gameObject);
                // Roll back only the newly allocated asset, never an earlier generation.
                if (!string.IsNullOrEmpty(assetPath)) AssetDatabase.DeleteAsset(assetPath);
                throw;
            }
            finally { if (tile == null) EditorUtility.ClearProgressBar(); }
        }

        // a tiled world shows one bar for all its tiles: each tile's steps fill its share (GeoWorldBuild)
        internal static string progressPrefix { get => GeoWorldBuild.progressPrefix; set => GeoWorldBuild.progressPrefix = value; }
        internal static float progressFrom { get => GeoWorldBuild.progressFrom; set => GeoWorldBuild.progressFrom = value; }
        internal static float progressSpan { get => GeoWorldBuild.progressSpan; set => GeoWorldBuild.progressSpan = value; }
        static void Progress(string label, float value) => GeoWorldBuild.Progress(label, value);

        // The building as the settings want it drawn (GeoWorldBuild.ForSettings).
        public static BuildingFeature ForSettings(BuildingFeature b, GenerationSettings s) => GeoWorldBuild.ForSettings(b, s);
        internal static bool ValidBuilding(BuildingFeature b) => GeoWorldBuild.ValidBuilding(b);
        static Dictionary<string, GeoBuilding> BuildingEdits(GeoWorldRoot root) => GeoWorldBuild.BuildingEdits(root);
        static int CopyCustomBuildings(GeoWorldRoot previous, Transform group, Func<Vector3, float> ground, Action<Object> save, List<string> notes, GenerationSettings settings) =>
            GeoWorldBuild.CopyCustomBuildings(previous, group, ground, save, notes, settings);
        static void CountRuin(Dictionary<string, int> ruins, GeoBuilding building) => GeoWorldBuild.CountRuin(ruins, building);
        static Transform Group(string name, Transform parent) => GeoWorldBuild.Group(name, parent);

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
    }
}
