using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace GeoWorld.Editor
{
    // Updates of the package from the repository the project installed it from (its tags v1.2.3). Once per editor
    // session the newest version is looked up in the background; when there is a newer one the console and the Scene
    // view say so and the Geo World window shows an Update button. Tools > Geo World > Update Geo World... does the same
    // on demand. A project that develops the package (embedded) has no updates: it publishes them
    // (tools/publish_unity_package.py).
    [InitializeOnLoad]
    public static class GeoUpdate
    {
        public enum Status { Developing, Unknown, Checking, UpToDate, Available, Failed, Updating }

        public static Status State { get; private set; } = Status.Unknown;
        public static Version Latest { get; private set; }
        public static string Error { get; private set; }
        public static string Installed => Info?.version ?? "";
        // raised on the main thread whenever State changes
        public static event Action Changed;

        const string CheckedKey = "GeoWorld.UpdateChecked";
        static PackageInfo info;
        static AddRequest adding;

        static PackageInfo Info => info ?? (info = PackageInfo.FindForAssembly(typeof(GeoUpdate).Assembly));

        static GeoUpdate()
        {
            EditorApplication.delayCall += () =>
            {
                if (Info == null || Info.source != PackageSource.Git) { Set(Status.Developing); return; }
                if (Application.isBatchMode || SessionState.GetBool(CheckedKey, false)) return;
                SessionState.SetBool(CheckedKey, true);
                Check(true);
            };
        }

        // Looks up the newest version in the background; notify: say so in the console and the Scene view when there is one.
        public static void Check(bool notify = false)
        {
            if (Info == null || Info.source != PackageSource.Git) { Set(Status.Developing); return; }
            if (State == Status.Checking || State == Status.Updating) return;
            Set(Status.Checking);
            string repository = Repository(Info), installed = Info.version;
            var lookup = Task.Run(() => { var newest = Newest(repository, out var error); return (newest, error); });
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!lookup.IsCompleted) return;
                EditorApplication.update -= poll;
                (Latest, Error) = lookup.Result;
                Set(Latest == null ? Status.Failed : Latest > Parse(installed) ? Status.Available : Status.UpToDate);
                if (notify && State == Status.Available)
                {
                    Debug.Log($"[GeoWorld] Geo World {Latest} is available (this project has {installed}): Tools > Geo World > Update Geo World..., or the Update button in the Geo World window.");
                    SceneView.lastActiveSceneView?.ShowNotification(new GUIContent($"Geo World {Latest} is available"), 8);
                }
            };
            EditorApplication.update += poll;
        }

        // Updates to Latest (after a check found it), after asking.
        public static void UpdateNow()
        {
            if (State != Status.Available || Latest == null) return;
            if (!EditorUtility.DisplayDialog("Update Geo World", $"Update Geo World {Installed} to {Latest}?\n\nWhat changed is in the package's CHANGELOG.md. " +
                "Unity downloads the new version and recompiles; worlds made with the old one keep working.", "Update", "Cancel")) return;
            Set(Status.Updating);
            adding = Client.Add(Repository(Info) + "#v" + Latest);
            EditorApplication.update += WaitForAdd;
        }

        [MenuItem("Tools/Geo World/Update Geo World...", false, 40)]
        static void UpdateMenu()
        {
            if (Info == null || Info.source != PackageSource.Git)
            {
                EditorUtility.DisplayDialog("Update Geo World", $"This project uses Geo World {Installed} from {Info?.source.ToString().ToLowerInvariant()} files " +
                    "(it is developed here), not from its repository: there is nothing to update. New versions are published with tools/publish_unity_package.py.", "OK");
                return;
            }
            try
            {
                EditorUtility.DisplayProgressBar("Update Geo World", "Looking up the newest version in " + Repository(Info), 0.5f);
                Latest = Newest(Repository(Info), out var error);
                Error = error;
            }
            finally { EditorUtility.ClearProgressBar(); }
            Set(Latest == null ? Status.Failed : Latest > Parse(Installed) ? Status.Available : Status.UpToDate);
            if (State == Status.Available) UpdateNow();
            else EditorUtility.DisplayDialog("Update Geo World", State == Status.UpToDate ? $"Geo World {Installed} is the newest version." :
                "The newest version could not be looked up: " + Error, "OK");
        }

        static void WaitForAdd()
        {
            if (adding == null || !adding.IsCompleted) return;
            EditorApplication.update -= WaitForAdd;
            if (adding.Status == StatusCode.Success) Debug.Log("[GeoWorld] Geo World updated to " + adding.Result.version + ".");
            else
            {
                Set(Status.Available);
                EditorUtility.DisplayDialog("Update Geo World", "The update failed: " + adding.Error?.message, "OK");
            }
            adding = null;
        }

        static void Set(Status state)
        {
            State = state;
            Changed?.Invoke();
        }

        // "com.tredokiller.geoworld@https://github.com/.../geoworld-unity.git#v1.1.0" -> the repository URL
        static string Repository(PackageInfo package)
        {
            string id = package.packageId;
            int at = id.IndexOf('@');
            if (at >= 0) id = id.Substring(at + 1);
            int hash = id.IndexOf('#');
            return hash >= 0 ? id.Substring(0, hash) : id;
        }

        static Version Parse(string version) => Version.TryParse(version, out var v) ? v : new Version(0, 0);

        // The highest v<major>.<minor>.<patch> tag of a repository (git ls-remote, with the computer's git login), or null.
        // Uses no Unity API: it runs off the main thread.
        static Version Newest(string repository, out string error)
        {
            error = null;
            try
            {
                var start = new ProcessStartInfo("git", "ls-remote --tags --refs \"" + repository + "\"")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
                };
                start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0"; // fail instead of waiting for a password
                using (var git = Process.Start(start))
                {
                    var output = git.StandardOutput.ReadToEndAsync();
                    var errors = git.StandardError.ReadToEndAsync();
                    if (!git.WaitForExit(30000)) { git.Kill(); error = "git did not answer in 30 s"; return null; }
                    if (git.ExitCode != 0) { error = errors.Result.Trim(); return null; }
                    Version newest = null;
                    foreach (var line in output.Result.Split('\n'))
                    {
                        int tag = line.IndexOf("refs/tags/v", StringComparison.Ordinal);
                        if (tag < 0) continue;
                        if (Version.TryParse(line.Substring(tag + 11).Trim(), out var v) && (newest == null || v > newest)) newest = v;
                    }
                    if (newest == null) error = "the repository has no version tags";
                    return newest;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }
    }
}
