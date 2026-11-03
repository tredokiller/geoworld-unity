using _Game.Scripts.World;
using GeoWorld;
using UnityEditor;
using UnityEngine;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>Инспектор WorldMapData: что запечено и кнопка запекания из выгрузок мира (WorldMapBaker).</summary>
    [CustomEditor(typeof(WorldMapData))]
    public sealed class WorldMapDataEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            WorldMapData data = (WorldMapData)target;
            GeoTiledWorld world = data.GetComponent<GeoTiledWorld>();
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(data.report)
                ? "Roads, fields and leaf types of this world's map for the environment layer (road surfaces, grass off roads, field rows, " +
                  "tree species by place). Baked from the world's exports into " + (world ? WorldMapBaker.Folder(world) : "the world's folder") + "."
                : data.report, MessageType.None);

            using (new EditorGUI.DisabledScope(world == null || Application.isPlaying))
            {
                if (GUILayout.Button(new GUIContent("Bake roads, fields and leaf types",
                        "From the world's exports (environment.json of its parts); roads are snapped to the tiles' satellite imagery, " +
                        "a few minutes for a large world. Generating tiles bakes it again. Without the exports on this machine the data stays."),
                        GUILayout.Height(24)))
                    Debug.Log("[WorldMapBaker] " + WorldMapBaker.Bake(world), data);
            }
        }
    }
}
