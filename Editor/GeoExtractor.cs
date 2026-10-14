using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GeoWorld.Editor
{
    // The Building Extractor app, run from Unity. The package carries the app (Extractor~: its Python code and data) and,
    // on Windows, a private Python, so nothing has to be installed; where the package is developed the app is the
    // repository's own and runs with the Python of the computer. The app keeps its cache and exports in a folder of the
    // user's (LocalApplicationData/GeoWorld), because an installed package is read-only.
    // Extract runs cli.py in the background and reads its progress (JSON lines); OpenFullApp starts the web app (the map,
    // the condition review, reports) and opens it in the browser.
    public static class GeoExtractor
    {
        public class Job
        {
            public string name;
            public float progress;
            public string stage = "Starting";
            public readonly List<string> log = new List<string>();
            public string export, package, error;
            public int buildings;
            public bool Running => process != null && error == null && export == null && !cancelled;
            public bool Finished => export != null;
            internal Process process;
            internal bool cancelled;
            internal readonly ConcurrentQueue<string> lines = new ConcurrentQueue<string>();
        }

        public static Job Current { get; private set; }
        // raised on the main thread when the current job reports progress or ends
        public static event Action Changed;

        static Process server;
        static int serverPort;

        static GeoExtractor()
        {
            EditorApplication.quitting += () => { Cancel(); StopServer(); };
        }

        // ---------------------------------------------------------------- where things are

        // The app's folder (cli.py, server.py, extractor/), or null when there is none.
        public static string AppFolder
        {
            get
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(GeoExtractor).Assembly);
                if (info != null)
                {
                    string bundled = Path.Combine(info.resolvedPath, "Extractor~");
                    if (File.Exists(Path.Combine(bundled, "cli.py"))) return bundled;
                }
                // the repository the package is developed in: the app sits a few folders above the Unity project
                var dir = new DirectoryInfo(Application.dataPath);
                for (int up = 0; up < 5 && dir != null; up++, dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "cli.py")) && Directory.Exists(Path.Combine(dir.FullName, "extractor"))) return dir.FullName;
                return null;
            }
        }

        static bool Bundled(string app) => app != null && Path.GetFileName(app) == "Extractor~";

        // Where the app keeps its cache and exports: the user's folder for the bundled app, the repository's own folders
        // for the repository's app.
        public static string Home
        {
            get
            {
                string app = AppFolder;
                return Bundled(app) || app == null
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeoWorld")
                    : app;
            }
        }

        public static string ExportsFolder => Path.Combine(Home, "exports");

        public static bool Available => AppFolder != null;

        // The Python that runs the app: the package's own (Windows), else the computer's.
        static (string file, string prefix) Python(string app)
        {
            string own = Path.Combine(app, "python", "python.exe");
            if (File.Exists(own)) return (own, "");
            if (Application.platform == RuntimePlatform.WindowsEditor) return ("py", "-3 ");
            return ("python3", "");
        }

        static ProcessStartInfo StartInfo(string app, string arguments)
        {
            var (python, prefix) = Python(app);
            var start = new ProcessStartInfo(python, prefix + arguments)
            {
                WorkingDirectory = app, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Bundled(app)) start.EnvironmentVariables["BUILDING_EXTRACTOR_HOME"] = Home;
            start.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            return start;
        }

        // ---------------------------------------------------------------- extraction

        // Extracts an area (south, west, north, east in degrees) into the exports folder, in the background.
        public static Job Extract(double south, double west, double north, double east, string name, bool radar)
        {
            if (Current != null && Current.Running) throw new InvalidOperationException("An area is being extracted already.");
            string app = AppFolder ?? throw new FileNotFoundException("The Building Extractor app is not in this package.");
            Directory.CreateDirectory(ExportsFolder);
            string bbox = string.Join(",", Array.ConvertAll(new[] { south, west, north, east }, v => v.ToString("0.######", CultureInfo.InvariantCulture)));
            string arguments = $"cli.py --bbox {bbox} --name \"{name.Replace("\"", "")}\" --machine" + (radar ? "" : " --no-radar");
            var job = new Job { name = name };
            var process = new Process { StartInfo = StartInfo(app, arguments), EnableRaisingEvents = true };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) job.lines.Enqueue(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) job.lines.Enqueue("!" + e.Data); };
            process.Exited += (s, e) => job.lines.Enqueue("#exit");
            try { process.Start(); }
            catch (Exception e) { throw new InvalidOperationException("Python could not be started (" + StartInfo(app, "").FileName + "): " + e.Message, e); }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            job.process = process;
            Current = job;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
            Changed?.Invoke();
            return job;
        }

        public static void Cancel()
        {
            var job = Current;
            if (job == null || !job.Running) return;
            job.cancelled = true;
            job.stage = "Cancelled";
            try { if (!job.process.HasExited) job.process.Kill(); } catch (InvalidOperationException) { }
            Changed?.Invoke();
        }

        // Applies what the app printed, on the main thread.
        static void Pump()
        {
            var job = Current;
            if (job == null) { EditorApplication.update -= Pump; return; }
            bool changed = false, ended = false;
            var errors = new List<string>();
            while (job.lines.TryDequeue(out var line))
            {
                changed = true;
                if (line == "#exit") { ended = true; continue; }
                if (line.StartsWith("!")) { errors.Add(line.Substring(1)); continue; }
                JObject message;
                try { message = JObject.Parse(line); } catch (Newtonsoft.Json.JsonException) { job.log.Add(line); continue; }
                if (message["progress"] != null && message["progress"].Type != JTokenType.Null) job.progress = (float)message["progress"];
                if (message["stage"] != null && message["stage"].Type != JTokenType.Null) job.stage = (string)message["stage"];
                if (message["log"] != null) job.log.Add((string)message["log"]);
                if (message["error"] != null) job.error = (string)message["error"];
                if (message["done"] is JObject done)
                {
                    job.package = (string)done["package"];
                    job.buildings = (int?)done["buildings"] ?? 0;
                    job.export = (string)done["export"];
                    job.progress = 1;
                    job.stage = "Done";
                }
            }
            if (ended)
            {
                EditorApplication.update -= Pump;
                if (job.export == null && job.error == null && !job.cancelled)
                    job.error = errors.Count > 0 ? errors[errors.Count - 1] : "The extraction stopped without a result.";
                if (job.error != null) Debug.LogWarning("[GeoWorld] Extracting " + job.name + " failed: " + job.error);
                else if (job.export != null) Debug.Log($"[GeoWorld] Extracted {job.name}: {job.buildings} buildings into {job.package}");
            }
            if (changed) Changed?.Invoke();
        }

        // ---------------------------------------------------------------- the full app

        // Starts the web app (once) and opens it in the browser.
        public static void OpenFullApp()
        {
            string app = AppFolder ?? throw new FileNotFoundException("The Building Extractor app is not in this package.");
            if (server == null || server.HasExited)
            {
                serverPort = FreePort(8765);
                Directory.CreateDirectory(ExportsFolder);
                server = Process.Start(StartInfo(app, "server.py --port " + serverPort));
                server.OutputDataReceived += (s, e) => { };
                server.ErrorDataReceived += (s, e) => { };
                server.BeginOutputReadLine();
                server.BeginErrorReadLine();
                // give it a moment to listen before the browser asks
                System.Threading.Thread.Sleep(1200);
            }
            Application.OpenURL("http://localhost:" + serverPort);
        }

        static void StopServer()
        {
            try { if (server != null && !server.HasExited) server.Kill(); } catch (InvalidOperationException) { }
            server = null;
        }

        static int FreePort(int preferred)
        {
            for (int port = preferred; port < preferred + 20; port++)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch (SocketException) { }
            }
            return preferred;
        }
    }
}
