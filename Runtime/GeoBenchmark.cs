using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GeoWorld
{
    // A repeatable frame-rate measurement of a generated world: the main camera is put at a few fixed views around a
    // point (in the street, over the roofs, over the town, towards the horizon), the tile streamer (if any) is given
    // time to load what each view needs, and then frame time, CPU and GPU time, batches, draw calls, SetPass calls and
    // triangles are sampled for a number of frames. Started from the editor (GeoBenchmarks.Run), which leaves the
    // request in the session; the report is written as text and play mode ends.
    public class GeoBenchmark : MonoBehaviour
    {
        [Serializable]
        public class Request
        {
            public float focusX, focusZ;
            public string output;
            public int warmup = 90, frames = 300;
            public float streamRadius; // > 0: use a tile streamer with this load radius
            // diagnostics: leave out a kind of content while measuring
            public bool noTrees, noHomes, noRoads;
            // flight (streaming hitches) instead of the views: from (flyFromX, flyFromZ) to (flyToX, flyToZ) at flySpeed m/s,
            // flyHeight above the ground at the start; every frame slower than spikeMs is listed with what took the time
            public float flySpeed, flyFromX, flyFromZ, flyToX, flyToZ, flyHeight = 60, spikeMs = 33;
            // a flight of several legs instead of from-to: x0, z0, x1, z1, ... in world metres (GeoBenchmarks.Tour: over a whole world)
            public float[] flyPath;
            // what is checked on the way, every checkEvery seconds: the tiles within checkRadius metres of the camera (0: three
            // quarters of the load radius) that are not loaded, and the terrains whose trees Nature Renderer is not drawing
            // natureGrace seconds after they loaded
            public float checkEvery = 0.5f, checkRadius, natureGrace = 5;
        }

        public struct View { public string name; public Vector2 offset; public float distance, pitch, yaw, lift; }

        // target = focus + offset; the camera stands `distance` back from it at `pitch` (looking down) and `yaw`
        public static readonly View[] Views =
        {
            new View { name = "street", offset = new Vector2(-60, 250), distance = 45, pitch = 10, yaw = 90, lift = 1.7f },
            new View { name = "roofs", offset = Vector2.zero, distance = 300, pitch = 25, yaw = 30 },
            new View { name = "town", offset = Vector2.zero, distance = 1200, pitch = 35, yaw = 30 },
            new View { name = "horizon", offset = new Vector2(0, 600), distance = 40, pitch = 2, yaw = 0, lift = 25 },
        };

        public Request request;

        public const string SessionKey = "GeoWorld.Benchmark", Argument = "-geoBenchmark";

        // The request: in the editor from the session (GeoBenchmarks.Run), in a player from a JSON file named on the
        // command line (-geoBenchmark request.json).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartRequested()
        {
            string json = null;
#if UNITY_EDITOR
            json = UnityEditor.SessionState.GetString(SessionKey, "");
            UnityEditor.SessionState.EraseString(SessionKey);
#else
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == Argument) json = File.ReadAllText(args[i + 1]);
#endif
            if (string.IsNullOrEmpty(json)) return;
            var go = new GameObject("Geo benchmark");
            go.AddComponent<GeoBenchmark>().request = JsonUtility.FromJson<Request>(json);
        }

        IEnumerator Start()
        {
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
            var camera = Camera.main;
            var report = new StringBuilder();
            GeoTileStreamer streamer = null;
            if (request.streamRadius > 0)
            {
                streamer = FindFirstObjectByType<GeoTileStreamer>();
                if (!streamer)
                {
                    streamer = new GameObject("Geo tile streamer (benchmark)").AddComponent<GeoTileStreamer>();
                    streamer.world = FindFirstObjectByType<GeoTiledWorld>();
                }
                streamer.loadRadius = request.streamRadius; streamer.unloadRadius = request.streamRadius + 600; streamer.target = camera.transform;
            }
            // a camera controller would move the camera back
            foreach (var behaviour in camera.GetComponents<MonoBehaviour>()) if (behaviour.GetType().Name == "GeoPreviewCamera") behaviour.enabled = false;
            report.AppendLine($"Geo World benchmark  {DateTime.Now:yyyy-MM-dd HH:mm}  {SystemInfo.graphicsDeviceName}  {Screen.width}x{Screen.height}  " +
                              $"stream radius {request.streamRadius:F0} m  render pipeline {(QualitySettings.renderPipeline ? QualitySettings.renderPipeline.name : "?")}");
            if (request.flySpeed > 0) yield return Flight(camera, streamer, report);
            else using (var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count"))
            using (var draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count"))
            using (var setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count"))
            using (var triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count"))
            {
                foreach (var view in Views)
                {
                    var target = new Vector3(request.focusX + view.offset.x, 0, request.focusZ + view.offset.y);
                    // stand above the spot first, so the tiles under it load, then find the ground
                    Pose(camera.transform, target, view, 0);
                    yield return Settle(streamer);
                    float ground = Ground(target);
                    Pose(camera.transform, target, view, ground);
                    yield return Settle(streamer);
                    Strip();
                    for (int i = 0; i < request.warmup; i++) yield return null;
                    var times = new List<float>(); var gpu = new List<double>(); var cpu = new List<double>();
                    long b = 0, d = 0, s = 0, t = 0; int n = 0;
                    var timings = new FrameTiming[1];
                    for (int i = 0; i < request.frames; i++)
                    {
                        yield return null;
                        times.Add(Time.unscaledDeltaTime * 1000);
                        FrameTimingManager.CaptureFrameTimings();
                        if (FrameTimingManager.GetLatestTimings(1, timings) > 0) { gpu.Add(timings[0].gpuFrameTime); cpu.Add(timings[0].cpuMainThreadFrameTime); }
                        b += batches.LastValue; d += draws.LastValue; s += setPass.LastValue; t += triangles.LastValue; n++;
                    }
                    times.Sort();
                    float median = times[times.Count / 2], p95 = times[(int)(times.Count * 0.95f)];
                    int slow = times.FindAll(x => x > 1000f / 60).Count;
                    report.AppendLine($"{view.name,-8} frame median {median,6:F2} ms ({1000 / median,5:F0} fps), 95% under {p95,6:F2} ms, {100f * slow / times.Count,3:F0}% over 16.7 ms | CPU main {Mean(cpu),6:F2} ms, GPU {Mean(gpu),6:F2} ms | " +
                                      $"batches {b / n,6}, draw calls {d / n,6}, SetPass {s / n,4}, triangles {t / n / 1000000.0,6:F2} M" +
                                      (streamer ? $" | tiles {streamer.LoadedTiles}" : ""));
                }
            }
            if (!string.IsNullOrEmpty(request.output)) File.WriteAllText(request.output, report.ToString());
            Debug.Log(report.ToString());
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // Flies the camera over the world (a straight line, or the legs of flyPath) while the streamer loads and unloads tiles,
        // and reports how steady the frame rate is (median, 1 % and 0.1 % lows, the spread), every frame slower than spikeMs
        // with the main-thread profiler markers that took the most of it and the tiles that were loaded or unloaded around it,
        // and whether the world was there all the way: tiles near the camera that were not loaded, and terrains whose trees
        // Nature Renderer had not started drawing a while after their tile loaded.
        IEnumerator Flight(Camera camera, GeoTileStreamer streamer, StringBuilder report)
        {
            var path = new List<Vector3>();
            if (request.flyPath != null && request.flyPath.Length >= 4)
                for (int i = 0; i + 1 < request.flyPath.Length; i += 2) path.Add(new Vector3(request.flyPath[i], 0, request.flyPath[i + 1]));
            else { path.Add(new Vector3(request.flyFromX, 0, request.flyFromZ)); path.Add(new Vector3(request.flyToX, 0, request.flyToZ)); }
            var along = new float[path.Count];
            for (int i = 1; i < path.Count; i++) along[i] = along[i - 1] + Vector3.Distance(path[i - 1], path[i]);
            float length = along[path.Count - 1];
            Vector3 At(float distance, out Vector3 direction)
            {
                int leg = 1;
                while (leg < path.Count - 1 && along[leg] < distance) leg++;
                float span = Mathf.Max(0.001f, along[leg] - along[leg - 1]);
                direction = (path[leg] - path[leg - 1]) / span;
                return Vector3.Lerp(path[leg - 1], path[leg], (distance - along[leg - 1]) / span);
            }
            At(0, out var heading);
            camera.transform.SetPositionAndRotation(path[0] + Vector3.up * 300, Quaternion.LookRotation(heading.sqrMagnitude > 0 ? heading : Vector3.forward) * Quaternion.Euler(12, 0, 0));
            yield return Settle(streamer);
            float height = Ground(path[0]) + request.flyHeight;
            camera.transform.position = path[0] + Vector3.up * height;
            yield return Settle(streamer);
            for (int i = 0; i < request.warmup; i++) yield return null;

            var world = streamer && streamer.world ? streamer.world : FindFirstObjectByType<GeoTiledWorld>();
            float checkRadius = request.checkRadius > 0 ? request.checkRadius : (streamer ? streamer.loadRadius * 0.75f : 0);
            var recorders = MainThreadMarkers();
            var events = new List<(int frame, string text)>();
            int frame = 0;
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> onLoaded = (scene, mode) => events.Add((frame, "loaded " + scene.name));
            UnityEngine.Events.UnityAction<Scene> onUnloaded = scene => events.Add((frame, "unloaded " + scene.name));
            SceneManager.sceneLoaded += onLoaded;
            SceneManager.sceneUnloaded += onUnloaded;
            var times = new List<float>();
            var spikes = new List<(int frame, float ms, float at, string top)>();
            // the world on the way: checks with a tile missing, the most missing at once, where; Nature Renderer per terrain
            int checks = 0, checksWithHoles = 0, mostHoles = 0; float mostHolesAt = 0;
            var holeTiles = new HashSet<string>();
            var natureSeen = new Dictionary<GeoNatureRendererStart, float>();
            var natureDelays = new List<float>();
            var natureLate = new HashSet<string>();
            float nextCheck = 0, travelled = 0, clock = 0;
            var rotation = camera.transform.rotation;
            try
            {
                while (travelled < length)
                {
                    yield return null;
                    frame++;
                    float ms = Time.unscaledDeltaTime * 1000;
                    times.Add(ms);
                    clock += Time.unscaledDeltaTime;
                    if (ms > request.spikeMs) spikes.Add((frame, ms, travelled, Top(recorders, 22)));
                    travelled += request.flySpeed * Mathf.Min(Time.unscaledDeltaTime, 0.1f); // a long frame does not jump ahead
                    var position = At(Mathf.Min(travelled, length), out var direction) + Vector3.up * height;
                    if (direction.sqrMagnitude > 0)
                        rotation = Quaternion.RotateTowards(rotation, Quaternion.LookRotation(direction) * Quaternion.Euler(12, 0, 0), 45 * Time.unscaledDeltaTime);
                    camera.transform.SetPositionAndRotation(position, rotation);
                    if (clock < nextCheck) continue;
                    nextCheck = clock + Mathf.Max(0.05f, request.checkEvery);
                    checks++;
                    if (world && streamer && checkRadius > 0)
                    {
                        int holes = 0;
                        foreach (var tile in world.tiles)
                            if (!string.IsNullOrEmpty(tile.scenePath) && GeoTileStreamer.Distance(tile.area, position) <= checkRadius && !streamer.IsLoaded(tile.scenePath))
                            { holes++; holeTiles.Add(tile.index.x + "_" + tile.index.y); }
                        if (holes > 0) checksWithHoles++;
                        if (holes > mostHoles) { mostHoles = holes; mostHolesAt = travelled; }
                    }
                    foreach (var start in FindObjectsByType<GeoNatureRendererStart>(FindObjectsSortMode.None))
                    {
                        if (!natureSeen.TryGetValue(start, out float since)) natureSeen[start] = since = clock;
                        if (since < 0) continue; // drawing already
                        if (start.Drawing) { natureDelays.Add(clock - since); natureSeen[start] = -1; }
                        else if (clock - since > request.natureGrace) natureLate.Add(start.gameObject.scene.name);
                    }
                }
            }
            finally
            {
                SceneManager.sceneLoaded -= onLoaded;
                SceneManager.sceneUnloaded -= onUnloaded;
                foreach (var recorder in recorders) recorder.recorder.Dispose();
            }
            var sorted = new List<float>(times);
            sorted.Sort();
            float mean = 0; foreach (var t in times) mean += t; mean /= Mathf.Max(1, times.Count);
            float spread = 0; foreach (var t in times) spread += (t - mean) * (t - mean); spread = Mathf.Sqrt(spread / Mathf.Max(1, times.Count));
            float p99 = sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.99f))], p999 = sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.999f))];
            report.AppendLine($"flight {length:F0} m in {path.Count - 1} legs at {request.flySpeed:F0} m/s, {request.flyHeight:F0} m up: {times.Count} frames, " +
                              $"median {sorted[sorted.Count / 2]:F1} ms, mean {1000 / mean:F0} fps, 1% low {1000 / p99:F0} fps ({p99:F1} ms), 0.1% low {1000 / p999:F0} fps, " +
                              $"spread {spread:F1} ms, worst {sorted[sorted.Count - 1]:F1} ms; " +
                              $"over 33 ms: {sorted.FindAll(x => x > 33).Count}, over 50 ms: {sorted.FindAll(x => x > 50).Count}, over 100 ms: {sorted.FindAll(x => x > 100).Count}; " +
                              $"{events.FindAll(e => e.text.StartsWith("loaded")).Count} tiles loaded, {events.FindAll(e => e.text.StartsWith("unloaded")).Count} unloaded");
            if (world && streamer && checkRadius > 0)
                report.AppendLine(checksWithHoles == 0
                    ? $"tiles: every tile within {checkRadius:F0} m of the camera was loaded at all {checks} checks"
                    : $"tiles: a tile within {checkRadius:F0} m of the camera was missing at {checksWithHoles} of {checks} checks, at most {mostHoles} at once (at {mostHolesAt:F0} m); " +
                      $"{holeTiles.Count} tiles were late: {string.Join(" ", holeTiles)} (fly slower, or raise the streamer's Loads At Once)");
            if (natureSeen.Count > 0)
            {
                natureDelays.Sort();
                report.AppendLine($"Nature Renderer: drawing on {natureDelays.Count} of {natureSeen.Count} terrains" +
                                  (natureDelays.Count > 0 ? $", {natureDelays[natureDelays.Count / 2]:F1} s after the tile loaded (median), {natureDelays[natureDelays.Count - 1]:F1} s at most" : "") +
                                  (natureLate.Count > 0 ? $"; not drawing {request.natureGrace:F0} s after loading: {string.Join(", ", natureLate)}" : "; none was late"));
            }
            foreach (var spike in spikes)
            {
                var near = events.FindAll(e => Math.Abs(e.frame - spike.frame) <= 2).ConvertAll(e => e.text);
                report.AppendLine($"  frame {spike.frame,5} at {spike.at,5:F0} m: {spike.ms,6:F1} ms | {spike.top}" + (near.Count > 0 ? " | " + string.Join(", ", near) : ""));
            }
        }

        // Recorders of every timed main-thread marker, except the loops that contain everything else.
        static List<(string name, ProfilerRecorder recorder)> MainThreadMarkers()
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var result = new List<(string, ProfilerRecorder)>();
            var seen = new HashSet<string>();
            foreach (var handle in handles)
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                if (description.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds || !seen.Add(description.Name)) continue;
                if (description.Name == "PlayerLoop" || description.Name == "EditorLoop" || description.Name.StartsWith("Main Thread")) continue;
                result.Add((description.Name, ProfilerRecorder.StartNew(description.Category, description.Name, 1,
                    ProfilerRecorderOptions.Default | ProfilerRecorderOptions.CollectOnlyOnCurrentThread)));
            }
            return result;
        }

        // markers that only contain the work of others (the player loop, the editor's game view that runs it)
        static readonly string[] Containers = { "UpdateScene", "Application.Tick", "Application.UpdateScene", "GUIView.", "GameView.",
            "UIElementsUtility.", "UIR.", "EarlyUpdate.", "PreUpdate.", "Update.", "PreLateUpdate.", "PostLateUpdate.", "FixedUpdate.",
            "Initialization.", "Profiler.", "Preload Single Step", "Loading.UpdatePreloading", "UpdatePreloading" };

        static string Top(List<(string name, ProfilerRecorder recorder)> recorders, int count)
        {
            var spent = new List<(string name, double ms)>();
            foreach (var r in recorders)
            {
                double ms = r.recorder.LastValue / 1e6;
                if (ms >= 1 && Array.FindIndex(Containers, c => r.name.StartsWith(c)) < 0) spent.Add((r.name, ms));
            }
            spent.Sort((a, b) => b.ms.CompareTo(a.ms));
            return string.Join(", ", spent.GetRange(0, Math.Min(count, spent.Count)).ConvertAll(s => $"{s.name} {s.ms:F1}"));
        }

        void Strip()
        {
            if (request.noTrees) foreach (var terrain in Terrain.activeTerrains) terrain.drawTreesAndFoliage = false;
            if (!request.noHomes && !request.noRoads) return;
            foreach (var t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
                if ((request.noHomes && t.name.StartsWith("Homes ")) || (request.noRoads && t.name.StartsWith("Roads - "))) t.gameObject.SetActive(false);
        }

        static double Mean(List<double> values)
        {
            if (values.Count == 0) return 0;
            double sum = 0; foreach (var v in values) sum += v; return sum / values.Count;
        }

        static IEnumerator Settle(GeoTileStreamer streamer)
        {
            yield return null; yield return null;
            float until = Time.realtimeSinceStartup + 600;
            while (streamer && streamer.Busy && Time.realtimeSinceStartup < until) yield return null;
        }

        static float Ground(Vector3 at)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                Vector3 p = terrain.transform.position, size = terrain.terrainData.size;
                if (at.x >= p.x && at.x <= p.x + size.x && at.z >= p.z && at.z <= p.z + size.z) return terrain.SampleHeight(at) + p.y;
            }
            return 0;
        }

        static void Pose(Transform camera, Vector3 target, View view, float ground)
        {
            target.y = ground + view.lift;
            var rotation = Quaternion.Euler(view.pitch, view.yaw, 0);
            camera.SetPositionAndRotation(target - rotation * Vector3.forward * view.distance, rotation);
        }
    }
}
