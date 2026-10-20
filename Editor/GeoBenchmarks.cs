using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Starts a GeoBenchmark run of the open scene: the request goes into the session (it survives entering play mode),
    // play mode starts, and the benchmark writes its report and stops play mode again.
    public static class GeoBenchmarks
    {
        // focus: the point the views are set around (world metres). streamRadius > 0: tiles are streamed around the
        // camera; 0: whatever is open is measured as it is.
        public static void Run(Vector2 focus, float streamRadius, string output, int warmup = 90, int frames = 300)
        {
            if (Application.isPlaying) throw new System.InvalidOperationException("Stop Play mode first.");
            var request = new GeoBenchmark.Request { focusX = focus.x, focusZ = focus.y, streamRadius = streamRadius, output = output, warmup = warmup, frames = frames };
            SessionState.SetString(GeoBenchmark.SessionKey, JsonUtility.ToJson(request));
            // CPU and GPU times per frame (FrameTimingManager)
            PlayerSettings.enableFrameTimingStats = true;
            EditorApplication.isPlaying = true;
        }

        // A flight over the open tiled world while its tiles stream (from and to in world metres): lists the frames
        // slower than spikeMs and what took the time, to find and measure loading hitches.
        public static void RunFlight(Vector2 from, Vector2 to, float speed, float height, float streamRadius, string output, float spikeMs = 33)
        {
            if (Application.isPlaying) throw new System.InvalidOperationException("Stop Play mode first.");
            var request = new GeoBenchmark.Request { streamRadius = streamRadius, output = output, warmup = 60, flySpeed = speed, flyHeight = height,
                flyFromX = from.x, flyFromZ = from.y, flyToX = to.x, flyToZ = to.y, spikeMs = spikeMs };
            SessionState.SetString(GeoBenchmark.SessionKey, JsonUtility.ToJson(request));
            EditorApplication.isPlaying = true;
        }

        // A Windows player of a tiled world (its main scene and every tile), for benchmarks without the editor's overhead:
        // run it with -geoBenchmark request.json (GeoBenchmark.Request). Returns the error, or null.
        public static string BuildPlayer(GeoTiledWorld world, string exe)
        {
            var scenes = new System.Collections.Generic.List<string> { world.gameObject.scene.path };
            foreach (var tile in world.tiles) if (!string.IsNullOrEmpty(tile.scenePath) && System.IO.File.Exists(tile.scenePath)) scenes.Add(tile.scenePath);
            PlayerSettings.enableFrameTimingStats = true;
            PlayerSettings.runInBackground = true;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes.ToArray(), locationPathName = exe, target = BuildTarget.StandaloneWindows64, options = BuildOptions.None
            });
            return report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? null :
                report.summary.result + ": " + report.summary.totalErrors + " errors";
        }

#if GEOWORLD_DEVELOPMENT // only where the package is developed (its project defines this)
        [MenuItem("Tools/Geo World/Developer/Benchmark the open world (play mode)")]
#endif
        static void RunMenu()
        {
            var world = Object.FindFirstObjectByType<GeoTiledWorld>();
            Vector2 focus = Vector2.zero;
            if (world) focus = world.origin + (new Vector2(world.firstIndex.x, world.firstIndex.y) + new Vector2(world.columns, world.rows) * 0.5f) * world.tileSize;
            else { var root = Object.FindFirstObjectByType<GeoWorldRoot>(); if (root && root.terrain) { var p = root.terrain.transform.position + root.terrain.terrainData.size * 0.5f; focus = new Vector2(p.x, p.z); } }
            Run(focus, world ? 2000 : 0, System.IO.Path.Combine(Application.dataPath, "../Logs/GeoBenchmark.txt"));
        }
    }
}
