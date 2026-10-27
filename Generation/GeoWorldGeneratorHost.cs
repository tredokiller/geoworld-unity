using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    // Generation in a second process of the same player, so that the game's main thread never waits for a tile (a dense town tile
    // takes seconds of it). GenerateAsync starts this player's own executable without a window:
    //   <player>.exe -batchmode -geoworld-host <request.json> -geoworld-progress <progress.jsonl> -logFile <log>
    // (not -nographics: a player without a graphics device keeps no values in its materials, and the tiles' materials would be
    // written black).
    // In that process the scenes the player opens with never wake up (their objects are switched off before Awake), the generation
    // runs (GeoWorldGenerator.GenerateAsync: the extraction, then the tiles), its progress goes to the progress file as JSON lines,
    // and it quits. The game reads the file, reports the progress, kills the process on cancel, and loads the package it wrote.
    // The player must allow a second instance (Player settings: Force Single Instance off).
    public static class GeoWorldGeneratorHost
    {
        public const string HostFlag = "-geoworld-host", ProgressFlag = "-geoworld-progress", HomeFlag = "-geoworld-home";

        public static bool IsHost => !Application.isEditor && Arg(HostFlag) != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            if (!IsHost) return;
            // the game itself stays asleep: no Awake for the objects of the first scene (they are loaded, not woken)
            for (int i = 0; i < SceneManager.sceneCount; i++)
                foreach (var go in SceneManager.GetSceneAt(i).GetRootGameObjects()) go.SetActive(false);
            var host = new GameObject("Geo World generator host");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<Runner>();
        }

        sealed class Runner : MonoBehaviour
        {
            void Start() => Run().Forget();

            async UniTaskVoid Run()
            {
                string requestFile = Arg(HostFlag), progressFile = Arg(ProgressFlag), home = Arg(HomeFlag);
                int code = 0;
                using (var progress = progressFile != null ? new StreamWriter(progressFile, false, new UTF8Encoding(false)) { AutoFlush = true } : null)
                {
                    void Line(string json) { progress?.WriteLine(json); }
                    try
                    {
                        if (home != null) GeoWorldGenerator.Home = home;
                        var request = JsonUtility.FromJson<GeoWorldRequest>(File.ReadAllText(requestFile));
                        string last = null;
                        var package = await GeoWorldGenerator.GenerateAsync(request, new GeoProgress<GeoWorldProgress>(p =>
                        {
                            string json = JsonUtility.ToJson(new Message { progress = p.value, stage = (int)p.stage, message = p.message, tile = p.tile, tiles = p.tiles });
                            if (json != last) { Line(json); last = json; }
                        }));
                        Line(JsonUtility.ToJson(new Message { progress = 1, stage = (int)GeoWorldProgress.Stage.Done, done = package.Folder, report = JsonUtility.ToJson(GeoWorldGenerator.LastReport) }));
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        Line(JsonUtility.ToJson(new Message { error = e.Message }));
                        code = 1;
                    }
                }
                Application.Quit(code);
            }
        }

        [Serializable]
        sealed class Message
        {
            public float progress;
            public int stage, tile, tiles;
            public string message, done, error, report;
        }

        // This player's own executable (Windows: <name>_Data's folder, <name>.exe).
        public static string Executable
        {
            get
            {
                string data = Application.dataPath;
                if (!data.EndsWith("_Data", StringComparison.Ordinal)) return null;
                string exe = data.Substring(0, data.Length - "_Data".Length) + ".exe";
                return File.Exists(exe) ? exe : null;
            }
        }

        // Generates in a second process of this player (see above) and loads the package it made. The request's settings cross as
        // their plain values (the libraries are the player's own). executable: another player made from the same project.
        public static async UniTask<GeoWorldPackage> GenerateAsync(GeoWorldRequest request, IProgress<GeoWorldProgress> progress = null,
            CancellationToken cancellation = default, string executable = null)
        {
            executable ??= Executable ?? throw new InvalidOperationException("Generation in a second process needs a standalone player (in the editor use GeoWorldGenerator.GenerateAsync).");
            string work = Path.Combine(GeoWorldGenerator.Home, "host");
            Directory.CreateDirectory(work);
            string id = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            string requestFile = Path.Combine(work, id + ".request.json"), progressFile = Path.Combine(work, id + ".progress.jsonl"), log = Path.Combine(work, id + ".log");
            File.WriteAllText(requestFile, JsonUtility.ToJson(request));
            var start = new ProcessStartInfo(executable,
                $"-batchmode {HostFlag} \"{requestFile}\" {ProgressFlag} \"{progressFile}\" {HomeFlag} \"{GeoWorldGenerator.Home}\" -logFile \"{log}\"")
                { UseShellExecute = false, CreateNoWindow = true };
            using (var process = Process.Start(start) ?? throw new InvalidOperationException("The generator process could not be started."))
            {
                string done = null, error = null; long read = 0;
                try
                {
                    while (true)
                    {
                        bool exited = process.HasExited;
                        if (File.Exists(progressFile))
                            using (var stream = new FileStream(progressFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                stream.Seek(read, SeekOrigin.Begin);
                                using (var reader = new StreamReader(stream, Encoding.UTF8))
                                {
                                    string text = reader.ReadToEnd();
                                    int end = text.LastIndexOf('\n');
                                    if (end >= 0)
                                    {
                                        read += Encoding.UTF8.GetByteCount(text.Substring(0, end + 1));
                                        foreach (var line in text.Substring(0, end).Split('\n'))
                                        {
                                            if (string.IsNullOrWhiteSpace(line)) continue;
                                            var m = JsonUtility.FromJson<Message>(line);
                                            if (!string.IsNullOrEmpty(m.error)) error = m.error;
                                            if (!string.IsNullOrEmpty(m.done)) { done = m.done; if (!string.IsNullOrEmpty(m.report)) LastReport = JsonUtility.FromJson<GeoWorldGenerationReport>(m.report); }
                                            progress?.Report(new GeoWorldProgress { stage = (GeoWorldProgress.Stage)m.stage, value = m.progress, message = m.message, tile = m.tile, tiles = m.tiles });
                                        }
                                    }
                                }
                            }
                        if (exited) break;
                        await UniTask.Delay(100, DelayType.Realtime, PlayerLoopTiming.Update, cancellation);
                    }
                }
                catch (OperationCanceledException)
                {
                    GeoExtractorRunner.Kill(process); // with the app it started
                    throw;
                }
                if (error != null) throw new InvalidOperationException("The generation failed: " + error);
                if (done == null) throw new InvalidOperationException("The generator process stopped without a package (exit code " + process.ExitCode + "; see " + log + ").");
                foreach (var f in new[] { requestFile, progressFile }) try { File.Delete(f); } catch (IOException) { }
                return GeoWorldPackage.Load(done);
            }
        }

        // How the last generation in a second process went (as GeoWorldGenerator.LastReport in that process).
        public static GeoWorldGenerationReport LastReport { get; private set; }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
