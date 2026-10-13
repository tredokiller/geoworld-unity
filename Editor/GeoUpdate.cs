using System;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace GeoWorld.Editor
{
    // Tools > Geo World > Update Geo World...: the newest version of the package, from the repository the project
    // installed it from (its tags v1.2.3), after asking. Once per editor session the newest version is also looked up in
    // the background, and the console says when there is one. A project that develops the package (embedded) has no
    // update: it publishes versions (tools/publish_unity_package.py).
    [InitializeOnLoad]
    public static class GeoUpdate
    {
        const string CheckedKey = "GeoWorld.UpdateChecked";
        static AddRequest adding;

        static GeoUpdate()
        {
            if (SessionState.GetBool(CheckedKey, false) || UnityEngine.Application.isBatchMode) return;
            SessionState.SetBool(CheckedKey, true);
            EditorApplication.delayCall += () =>
            {
                var info = Installed();
                if (info == null || info.source != PackageSource.Git) return;
                string repository = Repository(info), current = info.version;
                // git can take a few seconds: off the main thread (Debug.Log may be called from any thread)
                new Thread(() =>
                {
                    var newest = Newest(repository, out _);
                    if (newest != null && newest > Parse(current))
                        Debug.Log($"[GeoWorld] Geo World {newest} is available (this project has {current}): Tools > Geo World > Update Geo World...");
                }) { IsBackground = true }.Start();
            };
        }

        [MenuItem("Tools/Geo World/Update Geo World...", false, 40)]
        static void UpdateMenu()
        {
            var info = Installed();
            if (info == null) return;
            if (info.source != PackageSource.Git)
            {
                EditorUtility.DisplayDialog("Update Geo World", $"This project uses Geo World {info.version} from {info.source.ToString().ToLowerInvariant()} files " +
                    "(it is developed here), not from its repository: there is nothing to update. New versions are published with tools/publish_unity_package.py.", "OK");
                return;
            }
            string repository = Repository(info);
            Version newest;
            string error;
            try
            {
                EditorUtility.DisplayProgressBar("Update Geo World", "Looking up the newest version in " + repository, 0.5f);
                newest = Newest(repository, out error);
            }
            finally { EditorUtility.ClearProgressBar(); }
            if (newest == null)
            {
                EditorUtility.DisplayDialog("Update Geo World", "The newest version could not be looked up: " + error, "OK");
                return;
            }
            if (newest <= Parse(info.version))
            {
                EditorUtility.DisplayDialog("Update Geo World", $"Geo World {info.version} is the newest version.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Update Geo World", $"Update Geo World {info.version} to {newest}?\n\nWhat changed is in the package's CHANGELOG.md. " +
                "Unity downloads the new version and recompiles; worlds made with the old one keep working.", "Update", "Cancel")) return;
            adding = Client.Add(repository + "#v" + newest);
            EditorApplication.update += WaitForAdd;
        }

        static void WaitForAdd()
        {
            if (adding == null || !adding.IsCompleted) return;
            EditorApplication.update -= WaitForAdd;
            if (adding.Status == StatusCode.Success) Debug.Log("[GeoWorld] Geo World updated to " + adding.Result.version + ".");
            else EditorUtility.DisplayDialog("Update Geo World", "The update failed: " + adding.Error?.message, "OK");
            adding = null;
        }

        static PackageInfo Installed() => PackageInfo.FindForAssembly(typeof(GeoUpdate).Assembly);

        // "com.tredokiller.geoworld@https://github.com/.../geoworld-unity.git#v1.1.0" -> the repository URL
        static string Repository(PackageInfo info)
        {
            string id = info.packageId;
            int at = id.IndexOf('@');
            if (at >= 0) id = id.Substring(at + 1);
            int hash = id.IndexOf('#');
            return hash >= 0 ? id.Substring(0, hash) : id;
        }

        static Version Parse(string version) => Version.TryParse(version, out var v) ? v : new Version(0, 0);

        // The highest v<major>.<minor>.<patch> tag of a repository (git ls-remote, with the computer's git login), or null.
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
