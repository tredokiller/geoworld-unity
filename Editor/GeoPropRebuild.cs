using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Rebuild props and vegetation of a world in the editor (the world Inspector's button), with Undo.
    public static class GeoPropRebuild
    {
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
                using(GeoGeneration.Use(GeoEditorGeneration.Target))
                    GeoPropBuilder.Build(temp,env,buildings,field,notes,obj=>AssetDatabase.AddObjectToAsset(obj,assetPath),world,world);
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

    }
}
