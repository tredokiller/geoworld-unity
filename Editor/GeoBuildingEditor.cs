using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GeoWorld.Editor
{
    [CustomEditor(typeof(GeoBuilding))]
    public class GeoBuildingEditor : UnityEditor.Editor
    {
        int selectedCorner;
        string error;
        public override void OnInspectorGUI()
        {
            var building=(GeoBuilding)target;
            EditorGUILayout.LabelField("Editable building",EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Source",building.sourceId??"Custom");
            EditorGUILayout.LabelField("Category / height source",(building.category??"Custom")+" / "+(building.heightSource??"manual"));
            if(building.mappedAppearance!=null&&!string.IsNullOrEmpty(building.mappedAppearance.source))
            {
                var mapped=building.mappedAppearance;
                EditorGUILayout.LabelField("Mapped appearance source",mapped.source=="osm"?"OpenStreetMap":mapped.source);
                if(!string.IsNullOrEmpty(mapped.roofShape))EditorGUILayout.LabelField("Mapped roof shape",mapped.roofShape);
                if(!string.IsNullOrEmpty(mapped.roofMaterial))EditorGUILayout.LabelField("Mapped roof material (reference)",mapped.roofMaterial);
                if(!string.IsNullOrEmpty(mapped.wallMaterial))EditorGUILayout.LabelField("Mapped wall material (reference)",mapped.wallMaterial);
                if(!string.IsNullOrEmpty(mapped.attribution))EditorGUILayout.LabelField("Attribution",mapped.attribution);
                if(mapped.license!=null)EditorGUILayout.LabelField("Data license",mapped.license.name??"Unknown");
                EditorGUILayout.HelpBox("Supported mapped roof settings and colors are applied before reviewed photo details. Material names and other tags remain reference data; existing building edits take precedence on rebuild.",MessageType.Info);
            }
            if(building.visualDetails!=null)
            {
                var details=building.visualDetails;
                EditorGUILayout.LabelField("Photo observation",details.observationId??details.photoId??"Reference");
                EditorGUILayout.LabelField("Captured / confidence",(string.IsNullOrEmpty(details.capturedAt)?"Unknown date":details.capturedAt)+" / "+(details.confidence??"unknown"));
                EditorGUILayout.LabelField("Observed wall surface",details.wallMaterial??"unknown");
                if(details.references!=null&&details.references.Count>0)EditorGUILayout.LabelField("Linked observations",details.references.Count.ToString());
                if(!string.IsNullOrEmpty(details.windowStyle))EditorGUILayout.LabelField("Observed window style",details.windowStyle);
                if(!string.IsNullOrEmpty(details.roofNote))EditorGUILayout.HelpBox(details.roofNote,MessageType.None);
                if(details.levels>0)EditorGUILayout.LabelField("Observed floors (reference)",details.levels.ToString());
                if(!string.IsNullOrEmpty(details.description))EditorGUILayout.HelpBox(details.description,MessageType.None);
                EditorGUILayout.HelpBox("Photo details were applied on import. Rebuilds preserve your edits. The capture date describes this reference, not the building's current condition.",MessageType.Info);
            }
            if(building.roofInfo!=null)
            {
                var roof=building.roofInfo;
                if(!string.IsNullOrEmpty(roof.shape))EditorGUILayout.LabelField("Roof data: shape",Described(roof.shape,roof.shapeSource,roof.shapeConfidence)+(roof.renderAs!=null&&roof.renderAs!=roof.shape?", drawn as "+roof.renderAs:""));
                if(!string.IsNullOrEmpty(roof.color))EditorGUILayout.LabelField("Roof data: colour",Described(roof.color,roof.colorSource,roof.colorConfidence));
                if(!string.IsNullOrEmpty(roof.material))EditorGUILayout.LabelField("Roof data: material (reference)",Described(roof.material.Replace('_',' '),roof.materialSource,-1));
                EditorGUILayout.HelpBox(roof.HasEstimates()
                    ?"Parts of this roof are typical for this kind of building (marked estimated); nobody observed them. Mapped and reviewed values from the app replace them. The settings can leave estimates out."
                    :"Roof facts from OpenStreetMap or a reviewer, applied on import.",MessageType.Info);
            }
            if(building.condition!=null&&!string.IsNullOrEmpty(building.condition.state))
            {
                var condition=building.condition;
                EditorGUILayout.LabelField("Condition",condition.state.Replace('_',' ')+" ("+condition.basis+", "+condition.confidence+" confidence"+(string.IsNullOrEmpty(condition.asOf)?"":", "+condition.asOf)+")");
                if(!string.IsNullOrEmpty(condition.note))EditorGUILayout.HelpBox(condition.note,MessageType.None);
                if(condition.sources!=null&&condition.sources.Count>0)EditorGUILayout.LabelField("Evidence sources",string.Join(", ",condition.sources));
                EditorGUILayout.HelpBox(condition.Estimated?"This damage is an estimate from satellite radar (low confidence). It is drawn as damage but is not a report about this building.":"Damage drawn from the evidence above. The condition comes from the app's export; rebuilding redraws it.",MessageType.Info);
            }
            if(!string.IsNullOrEmpty(building.importNote))EditorGUILayout.HelpBox(building.importNote,MessageType.Warning);
            RuinSummary(building);
            EditorGUILayout.HelpBox("Height includes the roof. Edit settings or footprint corners, then rebuild this building. Whole-world rebuilds preserve its edits and custom children.",MessageType.Info);
            var ruinBefore=building.ruin;var modeBefore=building.ruinMode;
            DrawDefaultInspector();
            // a ruin chosen (or cleared) by hand is kept on rebuild
            if(building.ruin!=ruinBefore&&building.ruinMode==modeBefore)
            {
                building.ruinMode=building.ruin?GeoRuinMode.Model:GeoRuinMode.Procedural;
                Changed(building);
            }
            bool editing=GUILayout.Toggle(building.editFootprint,"Edit footprint in Scene view","Button");
            if(editing!=building.editFootprint){building.editFootprint=editing;Changed(building);}
            if(building.editFootprint)
            {
                selectedCorner=EditorGUILayout.IntSlider("Selected corner",selectedCorner,0,Mathf.Max(0,building.footprint.Count-1));
                using(new EditorGUI.DisabledScope(building.footprint.Count<2))
                if(GUILayout.Button("Insert corner after selected"))
                {
                    Undo.RecordObject(building,"Insert building corner");
                    building.footprint.Insert(selectedCorner+1,(building.footprint[selectedCorner]+building.footprint[(selectedCorner+1)%building.footprint.Count])*0.5f);
                    selectedCorner++;Changed(building);
                }
                using(new EditorGUI.DisabledScope(building.footprint.Count<=3))
                if(GUILayout.Button("Remove selected corner"))
                {Undo.RecordObject(building,"Remove building corner");building.footprint.RemoveAt(selectedCorner);selectedCorner=Mathf.Max(0,selectedCorner-1);Changed(building);}
            }
            using(new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if(GUILayout.Button("Rebuild this building",GUILayout.Height(34)))
                    try{GeoBuildingBuilder.Rebuild(building);error=null;}catch(Exception e){error=e.Message;}
                if(GUILayout.Button("Apply style's roof and window defaults")&&building.style)
                {Undo.RecordObject(building,"Apply building style defaults");building.style.ApplyDefaults(building);Changed(building);}
                using(new EditorGUILayout.HorizontalScope())
                {
                    if(GUILayout.Button("Pick ruin model automatically"))
                        try
                        {
                            Undo.RecordObject(building,"Pick ruin model");building.ruinMode=GeoRuinMode.Auto;
                            var world=building.GetComponentInParent<GeoWorldRoot>();
                            var settings=world?world.settings:new GenerationSettings{ruinLibrary=AssetDatabase.LoadAssetAtPath<GeoRuinLibrary>(GeoRuinLibrary.DefaultPath)};
                            if(!settings.ruinLibrary)settings.ruinLibrary=AssetDatabase.LoadAssetAtPath<GeoRuinLibrary>(GeoRuinLibrary.DefaultPath);
                            GeoRuins.Assign(building,settings);Changed(building);
                            GeoBuildingBuilder.Rebuild(building);error=null;
                        }
                        catch(Exception e){error=e.Message;}
                    if(GUILayout.Button("Use procedural damage"))
                        try{Undo.RecordObject(building,"Use procedural damage");building.ruinMode=GeoRuinMode.Procedural;building.ruin=null;Changed(building);GeoBuildingBuilder.Rebuild(building);error=null;}
                        catch(Exception e){error=e.Message;}
                }
            }
            if(GUILayout.Button("Select style asset")&&building.style)Selection.activeObject=building.style;
            if(!string.IsNullOrEmpty(error))EditorGUILayout.HelpBox(error,MessageType.Error);
        }
        // Which ruin model the building is drawn with and how it was fitted to the footprint.
        static void RuinSummary(GeoBuilding building)
        {
            if(!GeoRuins.Draws(building))return;
            var ruin=building.ruin;
            EditorGUILayout.LabelField("Ruin model",ruin.id+(building.ruinMode==GeoRuinMode.Auto?" (picked by the generator)":" (chosen by hand)"));
            string fit;
            try{fit=GeoRuinFit.Measure(ruin,GeoRuins.TargetOf(building,GeoPolygon.Clean(building.footprint))).ToString();}
            catch(ArgumentException e){fit=e.Message;}
            EditorGUILayout.HelpBox((string.IsNullOrEmpty(ruin.title)?"":ruin.title+"\n")+"Fitted to this footprint: "+fit+
                "\nThe model is a type, not a survey of this building: its damage is drawn from the condition above.",MessageType.None);
        }
        static string Described(string value,string source,float confidence)=>
            value+" ("+(string.IsNullOrEmpty(source)?"unknown source":source)+(GeoRoofInfo.Has(confidence)?", "+Mathf.RoundToInt(confidence*100)+"% sure":"")+")";
        static void Changed(GeoBuilding building)
        {EditorUtility.SetDirty(building);EditorSceneManager.MarkSceneDirty(building.gameObject.scene);SceneView.RepaintAll();}
        void OnSceneGUI()
        {
            var building=(GeoBuilding)target;
            if(!building.editFootprint)return;
            Handles.color=new Color(1,0.75f,0.15f);
            for(int i=0;i<building.footprint.Count;i++)
            {
                Vector2 p=building.footprint[i];var point=building.transform.TransformPoint(new Vector3(p.x,0,p.y));
                var q=building.footprint[(i+1)%building.footprint.Count];var next=building.transform.TransformPoint(new Vector3(q.x,0,q.y));
                Handles.DrawAAPolyLine(3,point,next);Handles.Label(point+Vector3.up*0.2f,i.ToString());
                EditorGUI.BeginChangeCheck();
                Vector3 moved=Handles.Slider2D(point,Vector3.up,Vector3.right,Vector3.forward,HandleUtility.GetHandleSize(point)*0.075f,Handles.DotHandleCap,Vector2.zero);
                if(EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(building,"Move building corner");var local=building.transform.InverseTransformPoint(moved);
                    building.footprint[i]=new Vector2(local.x,local.z);selectedCorner=i;Changed(building);
                }
            }
        }

        [MenuItem("GameObject/Geo World/Editable building",false,10)]
        static void CreateBuilding()
        {
            var world=Selection.activeTransform?Selection.activeTransform.GetComponentInParent<GeoWorldRoot>():null;
            var go=new GameObject("Editable building");
            try
            {
                var building=go.AddComponent<GeoBuilding>();building.style=GeoBuildingBuilder.DefaultStyle("house");building.style.ApplyDefaults(building);
                if(world)
                {
                    var group=world.transform.Find("Buildings");
                    if(!group){group=new GameObject("Buildings").transform;group.SetParent(world.transform,false);}
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,world.gameObject.scene);go.transform.SetParent(group,false);
                }
                if(SceneView.lastActiveSceneView)go.transform.position=SceneView.lastActiveSceneView.pivot;
                GeoBuildingBuilder.Rebuild(building,false);Undo.RegisterCreatedObjectUndo(go,"Create building");Selection.activeGameObject=go;
            }
            catch{DestroyImmediate(go);throw;}
        }
    }
}
