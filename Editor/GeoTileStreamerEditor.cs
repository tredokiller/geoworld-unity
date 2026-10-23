using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The tile streamer's load radius as kilometres (1-10), with how many tiles that keeps loaded around the camera.
    [CustomEditor(typeof(GeoTileStreamer))]
    public class GeoTileStreamerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var streamer = (GeoTileStreamer)target;
            EditorGUI.BeginChangeCheck();
            float radius = RadiusSlider(streamer.loadRadius, streamer.world ? streamer.world.tileSize : GeoDefaults.TileSize, streamer.world);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(streamer, "Load radius");
                streamer.SetRadius(radius);
                if (streamer.world) { Undo.RecordObject(streamer.world, "Load radius"); streamer.world.settings.loadRadius = radius; EditorUtility.SetDirty(streamer.world); }
                EditorUtility.SetDirty(streamer);
            }
            DrawPropertiesExcluding(serializedObject, "m_Script", "loadRadius", "unloadRadius");
            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.LabelField("Unloaded beyond", (streamer.UnloadDistance / 1000).ToString("0.0#") + " km", EditorStyles.miniLabel);
        }

        // A slider of the load radius in kilometres, 1 to 10 in steps of 0.5 km (radius in metres), and below it the tiles of
        // tileSize metres that stay loaded around the camera (and of a world, how many of its tiles that is at most).
        // Returns the radius in metres.
        public static float RadiusSlider(float radius, float tileSize, GeoTiledWorld world)
        {
            float km = Mathf.Clamp(radius, GeoTileStreamer.MinRadius, GeoTileStreamer.MaxRadius) / 1000;
            km = EditorGUILayout.Slider(new GUIContent("Load radius (km)", "How far around the camera the tiles are loaded while playing. Every tile within it is in memory; " +
                "memory and the time to load grow with the square of the radius. The tiles are not rebuilt when it changes."),
                km, GeoTileStreamer.MinRadius / 1000, GeoTileStreamer.MaxRadius / 1000);
            km = Mathf.Round(km * 2) / 2;
            var (fewest, most) = GeoTileStreamer.TilesWithin(km * 1000, tileSize);
            string tiles = fewest == most ? fewest.ToString() : fewest + "-" + most;
            string text = $"{tiles} tiles of {tileSize / 1000:0.##} km loaded around the camera";
            if (world && world.tiles.Count > 0) text += most >= world.tiles.Count ? $" (all {world.tiles.Count} of the world's)" : $" (of the world's {world.tiles.Count})";
            EditorGUILayout.LabelField(" ", text, EditorStyles.miniLabel);
            if (most > 120)
                EditorGUILayout.HelpBox($"{most} tiles at once: each holds its terrain, satellite texture, buildings and trees in memory (a town tile tens of MB), and they take a while to load at the start. " +
                                        "Check the memory and the frame rate with Tools > Geo World > Fly over the whole open world.", MessageType.Warning);
            return km * 1000;
        }
    }
}
