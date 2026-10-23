using System.Collections.Generic;
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

        // A flight along a path of several legs (x0, z0, x1, z1, ... in world metres): see RunFlight. The report also says whether
        // every tile near the camera was loaded and every terrain's trees were drawn on the way.
        public static void RunFlight(float[] path, float speed, float height, float streamRadius, string output, float spikeMs = 33)
        {
            if (Application.isPlaying) throw new System.InvalidOperationException("Stop Play mode first.");
            var request = new GeoBenchmark.Request { streamRadius = streamRadius, output = output, warmup = 60, flySpeed = speed, flyHeight = height, flyPath = path, spikeMs = spikeMs };
            SessionState.SetString(GeoBenchmark.SessionKey, JsonUtility.ToJson(request));
            PlayerSettings.enableFrameTimingStats = true;
            EditorApplication.isPlaying = true;
        }

        // The tiles' extent: (min x, min z, max x, max z) in world metres.
        public static Rect Extent(GeoTiledWorld world)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var tile in world.tiles) { minX = Mathf.Min(minX, tile.area.xMin); minZ = Mathf.Min(minZ, tile.area.yMin); maxX = Mathf.Max(maxX, tile.area.xMax); maxZ = Mathf.Max(maxZ, tile.area.yMax); }
            return world.tiles.Count == 0 ? Rect.zero : Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        }

        // A path over the whole world: lanes along its longer side, `spacing` metres apart (two load radii see every tile),
        // joined at their ends, a little inside the edge.
        public static float[] Tour(GeoTiledWorld world, float spacing)
        {
            var e = Extent(world);
            bool alongX = e.width >= e.height;
            float across = alongX ? e.height : e.width, inset = Mathf.Min(spacing * 0.5f, across * 0.5f);
            int lanes = Mathf.Max(1, Mathf.CeilToInt((across - 2 * inset) / spacing) + 1);
            var path = new List<float>();
            for (int i = 0; i < lanes; i++)
            {
                float c = lanes == 1 ? across * 0.5f : inset + (across - 2 * inset) * i / (lanes - 1);
                float a0 = inset, a1 = (alongX ? e.width : e.height) - inset;
                if (i % 2 == 1) { float t = a0; a0 = a1; a1 = t; }
                foreach (float a in new[] { a0, a1 })
                {
                    if (alongX) { path.Add(e.xMin + a); path.Add(e.yMin + c); } else { path.Add(e.xMin + c); path.Add(e.yMin + a); }
                }
            }
            return path.ToArray();
        }

        static float StreamRadius(GeoTiledWorld world)
        {
            var streamer = Object.FindFirstObjectByType<GeoTileStreamer>();
            return streamer ? streamer.loadRadius : 2000;
        }

        [MenuItem("Tools/Geo World/Fly across the open world (play mode)", false, 32)]
        static void FlyAcrossMenu()
        {
            var world = Object.FindFirstObjectByType<GeoTiledWorld>();
            if (!world) { EditorUtility.DisplayDialog("Geo World", "Open the main scene of a tiled world first.", "OK"); return; }
            var e = Extent(world);
            var path = e.width >= e.height ? new[] { e.xMin + 200, e.center.y, e.xMax - 200, e.center.y } : new[] { e.center.x, e.yMin + 200, e.center.x, e.yMax - 200 };
            RunFlight(path, 100, 60, StreamRadius(world), System.IO.Path.Combine(Application.dataPath, "../Logs/GeoFlight.txt"));
        }

        [MenuItem("Tools/Geo World/Fly over the whole open world (play mode)", false, 33)]
        static void TourMenu()
        {
            var world = Object.FindFirstObjectByType<GeoTiledWorld>();
            if (!world) { EditorUtility.DisplayDialog("Geo World", "Open the main scene of a tiled world first.", "OK"); return; }
            float radius = StreamRadius(world);
            RunFlight(Tour(world, 2 * radius), 100, 60, radius, System.IO.Path.Combine(Application.dataPath, "../Logs/GeoFlight.txt"));
        }

        // A Windows player of a tiled world, for benchmarks without the editor's overhead (GeoBuild: with Addressable Scenes the
        // main scene and the bundles of the tiles, otherwise every tile scene): run it with -geoBenchmark request.json
        // (GeoBenchmark.Request). Returns the error, or null.
        public static string BuildPlayer(GeoTiledWorld world, string exe)
        {
            PlayerSettings.enableFrameTimingStats = true;
            PlayerSettings.runInBackground = true;
            string error = GeoBuild.Build(world, exe, BuildTarget.StandaloneWindows64, out string report);
            if (error == null) Debug.Log(report);
            return error;
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
