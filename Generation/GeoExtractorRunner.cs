using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GeoWorld
{
    // The Building Extractor app (cli.py, with its own Python on Windows) run as a process of its own, from a player or the editor:
    // it downloads buildings, roads, terrain and the ground map of an area and writes an export (environment.json, buildings.json
    // and the rasters) that GeoWorldGenerator builds a world from. Its progress comes as JSON lines on stdout; cancelling kills it
    // (and the processes it started).
    //
    // Where the app is in a player: <data>/StreamingAssets/GeoWorld/Extractor (a player build copies the package's Extractor~ there,
    // GeoRuntimeBuild), or a folder named GeoWorldExtractor next to the executable; in the editor the package's Extractor~.
    public static class GeoExtractorRunner
    {
        public const string StreamingFolder = "GeoWorld/Extractor";

        // Set to use an app somewhere else.
        public static string AppFolderOverride;

        public static string AppFolder
        {
            get
            {
                if (!string.IsNullOrEmpty(AppFolderOverride)) return AppFolderOverride;
                var candidates = new List<string> { Path.Combine(Application.streamingAssetsPath, StreamingFolder) };
                string exe = Path.GetDirectoryName(Application.dataPath);
                if (exe != null) candidates.Add(Path.Combine(exe, "GeoWorldExtractor"));
                if (Application.isEditor) candidates.Insert(0, Path.GetFullPath(GeoPaths.Package + "/Extractor~"));
                foreach (var c in candidates) if (File.Exists(Path.Combine(c, "cli.py"))) return c;
                return null;
            }
        }

        public static bool Available => AppFolder != null;

        // The Python that runs the app: its own (Windows), else the computer's.
        static (string file, string prefix) Python(string app)
        {
            string own = Path.Combine(app, "python", "python.exe");
            if (File.Exists(own)) return (own, "");
            if (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer) return ("py", "-3 ");
            return ("python3", "");
        }

        public sealed class Result
        {
            public string exportJson;   // buildings of the area as the app saves them (<name>.json)
            public string folder;       // the export: environment.json, buildings.json, rasters
            public int buildings;
            public double seconds;
            public readonly List<string> log = new List<string>();
        }

        // Extracts an area (degrees) into <home>/exports/<name>/<name>_env. An export already there is used as it is (the same
        // name and area: a cancelled or repeated request does not download it again) unless fresh.
        public static async UniTask<Result> ExtractAsync(double south, double west, double north, double east, string name, string home,
            bool radar, IProgress<(float progress, string stage)> progress, CancellationToken cancellation, bool fresh = false)
        {
            string app = AppFolder ?? throw new FileNotFoundException("The Building Extractor app is not with this build (StreamingAssets/" + StreamingFolder + ").");
            string safe = SafeName(name);
            string folder = Path.Combine(home, "exports", safe);
            Directory.CreateDirectory(folder);
            string json = Path.Combine(folder, safe + ".json"), env = Path.Combine(folder, safe + "_env");
            string stamp = Path.Combine(folder, "request.txt");
            string box = Box(south, west, north, east);
            if (!fresh && File.Exists(Path.Combine(env, "environment.json")) && File.Exists(stamp) && File.ReadAllText(stamp).Trim() == box + (radar ? "" : " no-radar"))
            {
                progress?.Report((1, "Using the export extracted before"));
                return new Result { exportJson = json, folder = env };
            }
            if (File.Exists(stamp)) File.Delete(stamp);
            string arguments = $"cli.py --bbox {box} --out \"{json}\" --environment \"{env}\" --machine" + (radar ? "" : " --no-radar");
            var watch = Stopwatch.StartNew();
            var result = new Result { exportJson = json, folder = env };
            var lines = new ConcurrentQueue<string>();
            var (python, prefix) = Python(app);
            var start = new ProcessStartInfo(python, prefix + arguments)
            {
                WorkingDirectory = app, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.EnvironmentVariables["BUILDING_EXTRACTOR_HOME"] = home;
            start.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            // the app's folder may be read-only (an installed game)
            start.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";
            using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
            {
                bool exited = false;
                process.OutputDataReceived += (s, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lines.Enqueue("!" + e.Data); };
                process.Exited += (s, e) => Volatile.Write(ref exited, true);
                try { process.Start(); }
                catch (Exception e) { throw new InvalidOperationException("Python could not be started (" + python + "): " + e.Message, e); }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                string error = null, lastError = null; bool done = false;
                try
                {
                    while (true)
                    {
                        while (lines.TryDequeue(out var line))
                        {
                            if (line.StartsWith("!")) { lastError = line.Substring(1); result.log.Add(lastError); continue; }
                            JObject message;
                            try { message = JObject.Parse(line); } catch (Newtonsoft.Json.JsonException) { result.log.Add(line); continue; }
                            if (message["progress"] != null && message["progress"].Type != JTokenType.Null)
                                progress?.Report(((float)message["progress"], message["stage"]?.Type == JTokenType.String ? (string)message["stage"] : null));
                            else if (message["stage"]?.Type == JTokenType.String) progress?.Report((-1, (string)message["stage"]));
                            if (message["log"] != null) result.log.Add((string)message["log"]);
                            if (message["error"] != null) error = (string)message["error"];
                            if (message["done"] is JObject d)
                            {
                                done = true;
                                result.buildings = (int?)d["buildings"] ?? 0;
                                if (d["package"]?.Type == JTokenType.String) result.folder = (string)d["package"];
                                if (d["export"]?.Type == JTokenType.String) result.exportJson = (string)d["export"];
                            }
                        }
                        if (Volatile.Read(ref exited) && lines.IsEmpty) break;
                        await UniTask.Delay(50, DelayType.Realtime, PlayerLoopTiming.Update, cancellation);
                    }
                }
                catch (OperationCanceledException)
                {
                    Kill(process);
                    throw;
                }
                process.WaitForExit(2000);
                if (error != null) throw new InvalidOperationException("The extraction failed: " + error);
                if (!done || !File.Exists(Path.Combine(result.folder, "environment.json")))
                    throw new InvalidOperationException("The extraction stopped without a result" + (lastError != null ? ": " + lastError : "."));
            }
            File.WriteAllText(stamp, box + (radar ? "" : " no-radar"));
            result.seconds = watch.Elapsed.TotalSeconds;
            return result;
        }

        // The app and everything it started (Python may run workers).
        internal static void Kill(Process process)
        {
            try
            {
                if (process.HasExited) return;
                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
                {
                    using (var kill = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + process.Id) { CreateNoWindow = true, UseShellExecute = false }))
                        kill?.WaitForExit(5000);
                }
                if (!process.HasExited) process.Kill();
            }
            catch (Exception e) { Debug.LogWarning("Geo World: could not stop the extraction: " + e.Message); }
        }

        public static string Box(double south, double west, double north, double east) =>
            string.Join(",", Array.ConvertAll(new[] { south, west, north, east }, v => v.ToString("0.######", CultureInfo.InvariantCulture)));

        public static string SafeName(string name)
        {
            string safe = string.IsNullOrWhiteSpace(name) ? "area" : name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return safe.Replace(' ', '_');
        }
    }
}
