using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace GeoWorld
{
    // Runtime generation in a player, without the editor: an area's bounds, Generate (the Building Extractor app, then the tiles),
    // the progress, Cancel, and the packages made so far, whose tiles stream around the camera (GeoPreviewCamera: right-drag orbits,
    // wheel zooms, WASD moves, F flies). IMGUI, so it needs nothing else in the scene.
    //
    // From the command line (a check without anyone at the keyboard):
    //   -geoDemoArea S,W,N,E [-geoDemoName name] [-geoDemoNoRadar]   generate this area, then show it
    //   -geoDemoExport <folder>                                       make the package of an export made before
    //   -geoDemoPackage <folder>                                      show a package
    //   -geoDemoReport <file.json> [-geoDemoShot <file.png>] [-geoDemoQuit] [-geoDemoHost: in a second process] [-geoDemoHome <folder>]
    public sealed class GeoWorldRuntimeDemo : MonoBehaviour
    {
        public GeoWorldPackageStreamer streamer;
        public GeoPreviewCamera view;
        public GeoWorldBackground background;
        GeoWorldPackage shown;
        double backgroundSeconds;

        string south = "48.9790", west = "37.7980", north = "48.9970", east = "37.8260", areaName = "demo";
        bool radar = true;
        // generate in a second process of this player (GeoWorldGeneratorHost): the main thread here never waits for a tile
        bool host;
        GeoWorldProgress progress;
        string status = "Set an area and press Generate.";
        CancellationTokenSource running;
        List<string> packages = new List<string>();
        Vector2 scroll;
        float fps, frameMs, worstMs;
        readonly List<string> log = new List<string>();
        bool automatic;
        GeoWorldGenerationReport lastReport;

        void Awake()
        {
            // a generation goes on while the window is in the background (the extraction takes minutes)
            Application.runInBackground = true;
            if (!background) background = FindAnyObjectByType<GeoWorldBackground>() ?? gameObject.AddComponent<GeoWorldBackground>();
            // -geoDemoHome <folder>: exports, the app's cache and packages somewhere else (a cache filled before)
            if (Arg("-geoDemoHome") != null) GeoWorldGenerator.Home = Arg("-geoDemoHome");
            if (!streamer) streamer = FindAnyObjectByType<GeoWorldPackageStreamer>() ?? gameObject.AddComponent<GeoWorldPackageStreamer>();
            if (!view) view = FindAnyObjectByType<GeoPreviewCamera>();
            Refresh();
        }

        void Start()
        {
            string area = Arg("-geoDemoArea"), export = Arg("-geoDemoExport"), package = Arg("-geoDemoPackage");
            if (Arg("-geoDemoName") != null) areaName = Arg("-geoDemoName");
            if (Environment.GetCommandLineArgs().Contains("-geoDemoNoRadar")) radar = false;
            if (Environment.GetCommandLineArgs().Contains("-geoDemoHost")) host = true;
            if (area != null)
            {
                var v = area.Split(',');
                if (v.Length == 4) { south = v[0]; west = v[1]; north = v[2]; east = v[3]; }
            }
            if (area != null || export != null || package != null) { automatic = true; Automatic(export, package).Forget(); }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            frameMs = Mathf.Lerp(frameMs, dt * 1000, 0.1f);
            fps = frameMs > 0 ? 1000 / frameMs : 0;
            if (Time.frameCount > 10) worstMs = Mathf.Max(worstMs, dt * 1000);
            if (recordFrames) frames.Add(dt * 1000);
        }

        readonly List<float> frames = new List<float>();
        bool recordFrames;

        void Refresh()
        {
            string folder = GeoWorldGenerator.PackagesFolder;
            packages = Directory.Exists(folder)
                ? Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, GeoWorldPackage.ManifestFile))).OrderBy(d => d).ToList()
                : new List<string>();
        }

        GeoWorldRequest Request() => new GeoWorldRequest
        {
            south = Parse(south), west = Parse(west), north = Parse(north), east = Parse(east), name = areaName, radar = radar
        };

        static double Parse(string s) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;

        async UniTask<GeoWorldPackage> Generate(string export = null)
        {
            running = new CancellationTokenSource();
            var request = Request();
            request.exportFolder = export;
            try
            {
                if (export == null) GeoWorldGenerator.Validate(request);
                var reporter = new GeoProgress<GeoWorldProgress>(p =>
                {
                    progress = p; status = p.message;
                    string line = $"{p.value:P0} {p.stage} {p.message}";
                    if (log.Count == 0 || log[log.Count - 1] != line) { log.Add(line); if (log.Count > 400) log.RemoveAt(0); if (automatic) Debug.Log("[GeoDemo] " + line); }
                });
                bool second = host && GeoWorldGeneratorHost.Executable != null;
                var package = second ? await GeoWorldGeneratorHost.GenerateAsync(request, reporter, running.Token)
                    : await GeoWorldGenerator.GenerateAsync(request, reporter, running.Token);
                var r = second ? GeoWorldGeneratorHost.LastReport : GeoWorldGenerator.LastReport;
                lastReport = r;
                status = $"Done: {package.Tiles.Count} tiles, {r.packageBytes / 1048576f:F1} MB, extraction {r.extractSeconds:F0} s, generation {r.generateSeconds - r.extractSeconds:F0} s";
                Refresh();
                return package;
            }
            catch (OperationCanceledException) { status = "Cancelled."; return null; }
            catch (Exception e) { status = "Failed: " + e.Message; Debug.LogException(e); return null; }
            finally { running?.Dispose(); running = null; }
        }

        void Show(GeoWorldPackage package)
        {
            streamer.Open(package);
            var b = package.Manifest.bounds;
            var centre = new Vector3((b.minX + b.maxX) / 2, 0, (b.minZ + b.maxZ) / 2);
            if (view)
            {
                view.focus = centre + Vector3.up * (package.Manifest.stats.heightMin + package.Manifest.stats.relief * 0.5f);
                view.distance = Mathf.Clamp(Mathf.Max(b.maxX - b.minX, b.maxZ - b.minZ) * 0.6f, 300, 3000);
                view.moveSpeed = 200;
                view.Apply();
            }
            status = $"Showing {package.Manifest.name}: {package.Tiles.Count} tiles, {package.Manifest.stats.buildings} buildings, relief {package.Manifest.stats.relief:F0} m.";
            shown = package;
            ShowBackground(package).Forget();
        }

        // the ground around the package out to the horizon (from the tiles the generation put in the shared cache)
        async UniTaskVoid ShowBackground(GeoWorldPackage package)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                await background.ShowAsync(new[] { package }, new GeoProgress<GeoBackgroundProgress>(p => backgroundStatus = p.ToString()));
                backgroundSeconds = clock.Elapsed.TotalSeconds;
                backgroundStatus = $"Background: {background.CellCount} map tiles in {backgroundSeconds:F1} s";
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { backgroundStatus = "Background failed: " + e.Message; Debug.LogException(e); }
        }

        string backgroundStatus = "";

        async UniTaskVoid Automatic(string export, string packageFolder)
        {
            string reportPath = Arg("-geoDemoReport"), shot = Arg("-geoDemoShot");
            var result = new DemoReport { args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)) };
            try
            {
                await UniTask.DelayFrame(5);
                GeoWorldPackage package;
                worstMs = 0; frames.Clear(); recordFrames = true;
                if (packageFolder != null) package = GeoWorldPackage.Load(packageFolder);
                else package = await Generate(export);
                recordFrames = false;
                result.worstFrameMsWhileGenerating = worstMs;
                result.framesOver100MsWhileGenerating = frames.Count(f => f > 100);
                result.framesOver1sWhileGenerating = frames.Count(f => f > 1000);
                result.framesWhileGenerating = frames.Count;
                if (package == null) throw new Exception(status);
                result.generation = lastReport;
                result.secondProcess = host;
                result.package = package.Folder; result.contentHash = package.Manifest.contentHash; result.tiles = package.Tiles.Count;
                result.verified = package.Verify(out var problems); result.problems = problems.ToArray();
                result.warmUpMs = GeoWorldPackage.WarmUp();
                await UniTask.DelayFrame(2);
                var clock = Stopwatch.StartNew();
                worstMs = 0;
                Show(package);
                var b = package.Manifest.bounds;
                var centre = new Vector3((b.minX + b.maxX) / 2, 0, (b.minZ + b.maxZ) / 2);
                streamer.loadRadius = Mathf.Max(b.maxX - b.minX, b.maxZ - b.minZ);
                streamer.unloadRadius = streamer.loadRadius + 1000;
                streamer.target = null;
                await streamer.LoadAroundAsync(view ? view.transform.position : centre, streamer.loadRadius);
                result.loadAllSeconds = clock.Elapsed.TotalSeconds;
                result.loadedTiles = streamer.Loaded.Count;
                result.tileLoadSeconds = streamer.loadTimes.Select(t => t.seconds).ToArray();
                result.longestLoadSteps = streamer.Loaded.Values.Select(h => $"{h.Index.x}_{h.Index.y}: {h.LongestStepMs:F0} ms {h.LongestStep}").ToArray();
                result.worstFrameMsWhileLoading = worstMs;
                // the homes of the tiles are made a few milliseconds a frame after they load
                await UniTask.Delay(4000);
                worstMs = 0;
                await UniTask.Delay(2000);
                result.fpsAfter = fps; result.worstFrameMsAfter = worstMs;
                result.monoBytes = Profiler.GetMonoUsedSizeLong(); result.allocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
                GeoProcessMemory.Read(out result.workingSetBytes, out result.peakWorkingSetBytes, out result.privateBytes, out result.peakPrivateBytes);
                result.buildingColliders = streamer.Loaded.Values.Sum(h => h.Root.transform.Find(GeoNavigation.Group) is Transform t ? t.childCount : 0);
                result.missing = streamer.Loaded.Values.SelectMany(h => h.Missing).Distinct().ToArray();
                // the background: made after the tiles, from the shared cache
                var waitBackground = Stopwatch.StartNew();
                while ((!background.Shown || backgroundSeconds == 0) && waitBackground.Elapsed.TotalSeconds < 180 && !backgroundStatus.StartsWith("Background failed")) await UniTask.Yield();
                result.backgroundCells = background.CellCount; result.backgroundSeconds = backgroundSeconds; result.backgroundStatus = backgroundStatus;
                result.backgroundExtentKm = Mathf.Max(background.Extent.width, background.Extent.height) / 1000f;
                if (shot != null)
                {
                    ScreenCapture.CaptureScreenshot(shot);
                    await UniTask.DelayFrame(3);
                    // from a drone's height: the world's edge, and the background to the horizon
                    if (view)
                    {
                        float keep = view.distance, pitch = view.pitch;
                        view.distance = 7000; view.pitch = 25; view.Apply(); await UniTask.Delay(1500);
                        ScreenCapture.CaptureScreenshot(Path.ChangeExtension(shot, null) + "_high.png"); await UniTask.DelayFrame(3);
                        view.distance = keep; view.pitch = pitch; view.Apply();
                    }
                    if (view) { view.distance *= 0.25f; view.pitch = 35; view.Apply(); await UniTask.Delay(1500); ScreenCapture.CaptureScreenshot(Path.ChangeExtension(shot, null) + "_close.png"); await UniTask.DelayFrame(3); }
                }
                result.ok = true;
            }
            catch (Exception e) { result.error = e.ToString(); Debug.LogException(e); }
            if (reportPath != null) File.WriteAllText(reportPath, JsonUtility.ToJson(result, true));
            Debug.Log("[GeoDemo] finished: " + JsonUtility.ToJson(result));
            if (Environment.GetCommandLineArgs().Contains("-geoDemoQuit")) { await UniTask.DelayFrame(2); Application.Quit(result.ok ? 0 : 1); }
        }

        [Serializable]
        sealed class DemoReport
        {
            public bool ok, verified, secondProcess;
            public int backgroundCells;
            public double backgroundSeconds;
            public float backgroundExtentKm;
            public string backgroundStatus;
            public string error, args, package, contentHash;
            public string[] problems, missing;
            public int tiles, loadedTiles, buildingColliders;
            public GeoWorldGenerationReport generation;
            public double loadAllSeconds, warmUpMs;
            public double[] tileLoadSeconds;
            public string[] longestLoadSteps;
            public float worstFrameMsWhileLoading, fpsAfter, worstFrameMsAfter;
            public float worstFrameMsWhileGenerating;
            public int framesWhileGenerating, framesOver100MsWhileGenerating, framesOver1sWhileGenerating;
            public long monoBytes, allocatedBytes, workingSetBytes, peakWorkingSetBytes, privateBytes, peakPrivateBytes;
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 430, Screen.height - 20), GUI.skin.box);
            GUILayout.Label("Geo World: runtime generation", GUI.skin.label);
            bool idle = running == null;
            GUI.enabled = idle;
            Field("South", ref south); Field("West", ref west); Field("North", ref north); Field("East", ref east);
            Field("Name", ref areaName);
            radar = GUILayout.Toggle(radar, "Radar estimate of war damage");
            host = GUILayout.Toggle(host, "Generate in a second process (no stalls here)");
            var r = Request();
            string size = double.IsNaN(r.south + r.west + r.north + r.east) ? "?" : $"{r.WidthKm:F2} x {r.HeightKm:F2} km = {r.WidthKm * r.HeightKm:F1} km²";
            GUILayout.Label("Area: " + size);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Generate")) GenerateAndShow().Forget();
            GUI.enabled = !idle;
            if (GUILayout.Button("Cancel")) running?.Cancel();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            var bar = GUILayoutUtility.GetRect(400, 18);
            GUI.Box(bar, "");
            GUI.Box(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(progress.value), bar.height), "");
            GUILayout.Label($"{progress.value:P0} {progress.stage}" + (progress.tiles > 0 ? $"  tile {progress.tile}/{progress.tiles}" : ""));
            GUILayout.Label(status, GUILayout.MaxWidth(410));
            GUILayout.Space(8);
            GUILayout.Label("Packages in " + GeoWorldGenerator.PackagesFolder, GUILayout.MaxWidth(410));
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(160));
            foreach (var p in packages)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Path.GetFileName(p), GUILayout.Width(250));
                GUI.enabled = idle;
                if (GUILayout.Button("Show")) Show(GeoWorldPackage.Load(p));
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Refresh list")) Refresh();
            GUILayout.Space(8);
            GUILayout.Label($"{fps:F0} fps ({frameMs:F1} ms, worst {worstMs:F0} ms) | tiles loaded {streamer.Loaded.Count}" + (streamer.Busy ? " (loading)" : "") +
                            $" | memory {Profiler.GetTotalAllocatedMemoryLong() / 1048576} MB");
            if (GUILayout.Button("Reset worst frame")) worstMs = 0;
            if (!string.IsNullOrEmpty(backgroundStatus)) GUILayout.Label(backgroundStatus, GUILayout.MaxWidth(410));
            GUILayout.EndArea();
        }

        async UniTaskVoid GenerateAndShow()
        {
            var package = await Generate();
            if (package != null) Show(package);
        }

        static void Field(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(60));
            value = GUILayout.TextField(value ?? "", GUILayout.Width(200));
            GUILayout.EndHorizontal();
        }
    }
}
