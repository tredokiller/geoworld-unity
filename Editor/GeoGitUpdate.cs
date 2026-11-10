using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GeoWorld.Editor
{
    // Fast updates: the package as a git clone in the project's Packages folder (Packages/com.tredokiller.geoworld, a submodule
    // where the project is a git repository) instead of a package that Unity's Package Manager downloads from git.
    //
    // The Package Manager has no updates of a git package: every version is a new download of the whole package (some 300 MB of
    // models and textures, and the Git LFS files of the environment) into a new folder, which Unity then imports again in full.
    // A clone fetches only what changed since the installed version (1.9.0 to 1.17.0 was 1.5 MB), the files change in place, and
    // Unity imports only those. In a project that is a git repository the clone is a submodule: the project's commit of an update
    // is one line (the version the submodule points at), and the others get it with git submodule update --init.
    //
    // Everything here runs git and takes a while: it is called off the main thread (GeoUpdate), and reports what git says (its
    // "Receiving objects: 45%") through progress(stage, fraction).
    //
    // Paths: in the editor Unity maps Packages/<name of an installed package> to where that package really is, for System.IO
    // too, relative or absolute (Path.GetFullPath("Packages/com.tredokiller.geoworld") of a package installed from git is its
    // copy in Library/PackageCache/com.tredokiller.geoworld@<hash>, which is always there; Directory.Exists of the project's
    // absolute .../Packages/com.tredokiller.geoworld is true and Directory.Move into it goes to the cache). So the clone's
    // folder is never looked up or touched through System.IO by that path: it is found in a listing of the project's Packages
    // folder, git clones into the project's Temp folder, and only a finished clone is moved into Packages, by the system's move
    // command (Unity maps nothing for other processes). Switch deletes nothing under Packages or Library.
    static class GeoGitUpdate
    {
        public const string PackageFolder = "com.tredokiller.geoworld";
        public const string FolderName = "Packages/" + PackageFolder;

        // A package folder that is a git clone (a submodule has a .git file, a clone a .git folder).
        public static bool IsClone(string path) =>
            !string.IsNullOrEmpty(path) && (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));

        // The repository a clone came from, or null.
        public static string Origin(string path) => Git(path, "remote get-url origin", null, out string output) == 0 ? output.Trim() : null;

        // Makes the clone of version from the repository (once: a full download), with its Git LFS files, and adds it to the
        // project's git repository as a submodule where there is one. project: the project's folder (the parent of
        // Application.dataPath, read on the main thread); installed: where the Package Manager's copy is (PackageInfo.resolvedPath).
        // Returns the error, or null; note: what to tell the user.
        public static string Switch(string repository, Version version, string project, string installed, Action<string, float> progress, out string note)
        {
            note = null;
            string packages = Path.Combine(project, "Packages"), target = Path.Combine(packages, PackageFolder), tag = "v" + version;
            string blocked = Blocked(Entries(packages), target, installed);
            if (blocked != null) return blocked;
            string lfs = NeedLfs();
            if (lfs != null) return lfs;
            // the clone is made in the project's Temp folder (Unity imports nothing there, and the Package Manager does not take
            // it for a package while git writes it) and moved into Packages when it is complete: a failed download leaves nothing
            // in Packages, and is deleted where Unity maps no path
            string temp = Path.Combine(project, "Temp", "GeoWorldClone");
            TryDelete(temp);
            progress("Downloading Geo World " + version + " (the first time all of it; from then on only what changed)", 0);
            // core.longpaths (kept in the clone's config for later updates): on Windows git refuses paths over 260 characters
            // without it, and the package's deepest files are 143 characters in; the Package Manager's own clone takes them
            if (Git(project, $"clone --depth 1 --branch {tag} --config core.longpaths=true --progress \"{repository}\" \"{temp}\"", Report(progress, 0, 0.7f), out string output, true) != 0)
            {
                TryDelete(temp);
                return "git clone failed: " + Last(output);
            }
            string error = LargeFiles(temp, progress, 0.7f, 0.97f);
            if (error != null) { TryDelete(temp); return error; }
            string moved = Move(temp, target);
            if (moved == null && !Present(Entries(packages))) moved = "it is not in the Packages folder after the move";
            if (moved != null)
            {
                TryDelete(temp);
                return $"the clone could not be moved to {FolderName} ({moved}): nothing was changed in Packages.";
            }
            // a submodule of the project's repository, shallow for the others too
            if (Git(project, "rev-parse --show-toplevel", null, out string top) == 0)
            {
                top = top.Trim();
                string relative = Relative(Path.GetFullPath(top), target);
                if (Git(top, $"submodule add \"{repository}\" \"{relative}\"", null, out string added) == 0)
                {
                    Git(top, $"config -f .gitmodules submodule.{relative}.shallow true", null, out _);
                    note = $"{FolderName} is a git submodule of the project now: commit .gitmodules and it. The others get it with git submodule update --init.";
                }
                else note = $"{FolderName} could not be made a submodule of the project ({Last(added)}): it is a clone of its own; keep it out of the project's commits (.gitignore).";
            }
            return null;
        }

        // The names in the project's Packages folder as they are on disk (a listing of the folder itself, which Unity does not map).
        static string[] Entries(string packages)
        {
            try { return Directory.GetFileSystemEntries(packages); }
            catch (Exception) { return null; }
        }

        // Why the clone cannot go to target (Packages/com.tredokiller.geoworld in the project's folder), or null. entries: the
        // project's Packages folder listed (null: it could not be read); installed: the Package Manager's copy, which must never
        // be the target.
        internal static string Blocked(string[] entries, string target, string installed)
        {
            if (entries == null) return "the project's Packages folder could not be read.";
            string folder = Normal(target);
            if (!folder.EndsWith("/Packages/" + PackageFolder, StringComparison.OrdinalIgnoreCase) || folder.IndexOf("/Library/PackageCache/", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"{target} is not the project's {FolderName}: nothing was changed.";
            if (!string.IsNullOrEmpty(installed) && string.Equals(folder, Normal(installed), StringComparison.OrdinalIgnoreCase))
                return $"{target} is where the Package Manager keeps Geo World: nothing was changed.";
            return Present(entries) ? FolderName + " is there already: move it away, then update again." : null;
        }

        // The listing of the Packages folder has a com.tredokiller.geoworld folder.
        static bool Present(string[] entries)
        {
            if (entries != null)
                foreach (string entry in entries)
                    if (string.Equals(Path.GetFileName(entry.TrimEnd('/', '\\')), PackageFolder, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Normal(string path) => path.Replace('\\', '/').TrimEnd('/');

        // Renames folder from to to (which must not exist) with the system's own command, or returns why it could not. Not
        // Directory.Move: Unity maps the absolute path of Packages/com.tredokiller.geoworld as well, and Directory.Move into it
        // tried to create the Package Manager's copy in Library/PackageCache.
        static string Move(string from, string to)
        {
            bool windows = Path.DirectorySeparatorChar == '\\';
            if (windows) { from = from.Replace('/', '\\'); to = to.Replace('/', '\\'); }
            var start = windows ? new ProcessStartInfo("cmd.exe", $"/c move \"{from}\" \"{to}\"") : new ProcessStartInfo("mv", $"\"{from}\" \"{to}\"");
            start.UseShellExecute = false; start.RedirectStandardOutput = true; start.RedirectStandardError = true; start.CreateNoWindow = true;
            try
            {
                using (var move = Process.Start(start))
                {
                    var output = move.StandardOutput.ReadToEndAsync();
                    string errors = move.StandardError.ReadToEnd();
                    move.WaitForExit();
                    return move.ExitCode == 0 ? null : Last(output.Result + errors);
                }
            }
            catch (Exception e) { return e.Message; }
        }

        // Brings the clone at path to version: fetches the version's tag (only the objects the clone does not have), puts its
        // files in place (removing those it no longer has, and any that are not the package's) and fetches its Git LFS files.
        // Returns the error, or null.
        public static string Update(string path, Version version, Action<string, float> progress)
        {
            string tag = "v" + version;
            string lfs = NeedLfs(path);
            if (lfs != null) return lfs;
            progress("Downloading what changed in Geo World " + version, 0);
            if (Git(path, $"fetch --depth 1 --no-tags --progress origin +refs/tags/{tag}:refs/tags/{tag}", Report(progress, 0, 0.6f), out string output) != 0)
                return "git fetch failed: " + Last(output);
            progress("Putting the new files in place", 0.6f);
            if (Git(path, $"-c advice.detachedHead=false checkout --force --progress {tag}", Report(progress, 0.6f, 0.7f), out output, true) != 0)
                return "git checkout failed: " + Last(output);
            // the files the version no longer has went with the checkout; what is not the package's at all (the extractor's
            // __pycache__, a file put there) goes too, so that nothing of an old version is left for Unity to import
            Git(path, "clean -fd", null, out _);
            return LargeFiles(path, progress, 0.7f, 0.97f);
        }

        // The Git LFS files of the checked-out version (only those the clone does not have yet).
        static string LargeFiles(string path, Action<string, float> progress, float from, float to)
        {
            if (!UsesLfs(path)) return null;
            Git(path, "lfs install --local", null, out _);
            progress("Downloading the large files (Git LFS)", from);
            return Git(path, "lfs pull", Report(progress, from, to), out string output) == 0 ? null : "git lfs pull failed: " + Last(output);
        }

        static bool UsesLfs(string path)
        {
            string attributes = Path.Combine(path, ".gitattributes");
            return File.Exists(attributes) && File.ReadAllText(attributes).Contains("filter=lfs");
        }

        // Geo World keeps its large files in Git LFS: without it they would be pointer files (and the package would not compile).
        static string NeedLfs(string path = null)
        {
            if (path != null && !UsesLfs(path)) return null;
            return Git(null, "lfs version", null, out _) == 0 ? null
                : "Git LFS is not installed (Geo World keeps its large files in it): install it from https://git-lfs.com, then update again.";
        }

        static readonly Regex Percent = new Regex(@"^\s*(?:remote:\s*)?([A-Za-z][A-Za-z ]+?):?\s+(\d+)%", RegexOptions.Compiled);

        // A line of git's progress ("Receiving objects:  45% (1140/2537), 1.20 MiB | 3.1 MiB/s") as progress between from and to.
        static Action<string> Report(Action<string, float> progress, float from, float to) => line =>
        {
            var match = Percent.Match(line);
            if (!match.Success) return;
            float done = int.Parse(match.Groups[2].Value) / 100f;
            progress(line.Trim(), from + (to - from) * done);
        };

        // Runs git in directory (null: the current one); every line it writes to stderr (git's progress, split at \r as well)
        // goes to line. Returns the exit code; output: what it wrote (stdout, then stderr). noLfsSmudge: the LFS files are left to
        // git lfs pull, which says how far it is.
        static int Git(string directory, string arguments, Action<string> line, out string output, bool noLfsSmudge = false)
        {
            var start = new ProcessStartInfo("git", arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            if (directory != null) start.WorkingDirectory = directory;
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0"; // fail instead of waiting for a password
            if (noLfsSmudge) start.EnvironmentVariables["GIT_LFS_SKIP_SMUDGE"] = "1";
            try
            {
                using (var git = Process.Start(start))
                {
                    var stdout = git.StandardOutput.ReadToEndAsync();
                    var errors = new StringBuilder();
                    var current = new StringBuilder();
                    int c;
                    while ((c = git.StandardError.Read()) >= 0)
                    {
                        if (c == '\r' || c == '\n')
                        {
                            if (current.Length > 0) { line?.Invoke(current.ToString()); errors.AppendLine(current.ToString()); current.Clear(); }
                        }
                        else current.Append((char)c);
                    }
                    if (current.Length > 0) { line?.Invoke(current.ToString()); errors.AppendLine(current.ToString()); }
                    git.WaitForExit();
                    output = stdout.Result + errors;
                    return git.ExitCode;
                }
            }
            catch (Exception e)
            {
                output = "git could not be started: " + e.Message;
                return -1;
            }
        }

        static string Last(string output)
        {
            var lines = output.Trim().Split('\n');
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }

        static string Relative(string root, string path)
        {
            string a = root.Replace('\\', '/').TrimEnd('/') + "/", b = path.Replace('\\', '/');
            return b.StartsWith(a, StringComparison.OrdinalIgnoreCase) ? b.Substring(a.Length) : b;
        }

        // Deletes a clone in the project's Temp folder (git leaves its object files read-only, which Directory.Delete refuses).
        static void TryDelete(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(folder, true);
            }
            catch (Exception) { }
        }
    }
}
