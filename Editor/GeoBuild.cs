using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GeoWorld.Editor
{
    // A player of a tiled world, built the way it streams, and what it weighs.
    //
    // A world with Addressable Scenes: the assets its tiles share go into one bundle (GeoTileAddressables.ShareDependencies),
    // Addressables content is built, and only the main scene goes into the player; the tiles are bundles that the streamer loads.
    // (Tile scenes in Build Settings as well would be in the player twice.) A world without: the main scene and every tile scene.
    // The player's own data is compressed with LZ4HC (smaller, as fast to load).
    //
    // The report lists the build by kind (tile bundles, the shared bundle, the player's data, the tree data in Resources) with
    // the largest tiles, and what the world's assets weigh in the project, so that it is clear where the size is.
    //
    // Batch: Unity -batchmode -quit -projectPath <project> -executeMethod GeoWorld.Editor.GeoBuild.Batch
    //   -geoScene <the world's main scene> -geoOut <player .exe> [-geoTarget StandaloneWindows64]
    // writes the report next to the player (GeoBuildReport.txt) and to the log.
    public static class GeoBuild
    {
        [MenuItem("Tools/Geo World/Build player of the open world...", false, 30)]
        static void BuildMenu()
        {
            var world = UnityEngine.Object.FindFirstObjectByType<GeoTiledWorld>();
            if (!world) { EditorUtility.DisplayDialog("Geo World", "Open the main scene of a tiled world first.", "OK"); return; }
            string exe = EditorUtility.SaveFilePanel("Build player of " + world.gameObject.scene.name, "Builds", world.gameObject.scene.name, "exe");
            if (string.IsNullOrEmpty(exe)) return;
            string error = Build(world, exe, EditorUserBuildSettings.activeBuildTarget, out string report);
            Debug.Log(report);
            EditorUtility.DisplayDialog("Geo World", error == null ? "Built. " + FirstLine(report) + "\n\nThe whole report is in the console and next to the player (GeoBuildReport.txt)." : "The build failed: " + error, "OK");
        }

        [MenuItem("Tools/Geo World/Report the open world's size", false, 31)]
        static void SizeMenu()
        {
            var world = UnityEngine.Object.FindFirstObjectByType<GeoTiledWorld>();
            if (!world) { EditorUtility.DisplayDialog("Geo World", "Open the main scene of a tiled world first.", "OK"); return; }
            Debug.Log(ProjectReport(world));
        }

        public static void Batch()
        {
            string scene = Argument("-geoScene"), exe = Argument("-geoOut"), target = Argument("-geoTarget");
            int code = 1;
            try
            {
                if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(exe)) throw new ArgumentException("-geoScene <main scene> and -geoOut <player> are needed");
                EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
                var world = UnityEngine.Object.FindFirstObjectByType<GeoTiledWorld>() ?? throw new InvalidOperationException(scene + " has no Geo Tiled World");
                var buildTarget = string.IsNullOrEmpty(target) ? BuildTarget.StandaloneWindows64 : (BuildTarget)Enum.Parse(typeof(BuildTarget), target);
                string error = Build(world, exe, buildTarget, out string report);
                Debug.Log(report);
                if (error != null) Debug.LogError("Geo World build failed: " + error); else code = 0;
            }
            catch (Exception e) { Debug.LogException(e); }
            EditorApplication.Exit(code);
        }

        // Builds the player; returns the error, or null. report: the size report (also written next to the player).
        public static string Build(GeoTiledWorld world, string exe, BuildTarget target, out string report)
        {
            report = "";
            var scenes = new List<string> { world.gameObject.scene.path };
            var notes = new StringBuilder();
            int terrains = GeoTileAddressables.EnsureTerrainInstancing(world);
            if (terrains > 0) notes.AppendLine($"{terrains} tiles' terrain materials got GPU Instancing (tiles made by an older version; without it the terrain is not drawn in a player).");
            AddressableAssetSettings settings = null;
            var previous = AddressableAssetSettings.PlayerBuildOption.PreferencesValue;
            if (world.addressableScenes)
            {
                int shared = GeoTileAddressables.ShareDependencies(world);
                notes.AppendLine($"Addressables: {shared} assets the tiles share were added to \"{GeoTileAddressables.SharedGroupName(world)}\".");
                // tile scenes are bundles only
                EditorBuildSettings.scenes = EditorBuildSettings.scenes.Where(s => !world.tiles.Any(t => t.scenePath == s.path)).ToArray();
                settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                if (result != null && !string.IsNullOrEmpty(result.Error)) return "Addressables: " + result.Error;
                // built just now: not again by the player build
                previous = settings.BuildAddressablesWithPlayerBuild;
                settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            }
            else
                foreach (var tile in world.tiles) if (!string.IsNullOrEmpty(tile.scenePath) && File.Exists(tile.scenePath)) scenes.Add(tile.scenePath);
            UnityEditor.Build.Reporting.BuildReport build;
            try
            {
                build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes.ToArray(), locationPathName = exe, target = target, targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                    options = BuildOptions.CompressWithLz4HC
                });
            }
            finally { if (settings) settings.BuildAddressablesWithPlayerBuild = previous; }
            if (build.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                return build.summary.result + ": " + build.summary.totalErrors + " errors";
            // the player build reopens the scenes that were open: the world object of before is gone
            if (!world) world = UnityEngine.Object.FindFirstObjectByType<GeoTiledWorld>();
            if (!world) return "The build succeeded, but the open scene lost its Geo Tiled World, so there is no size report.";
            report = PlayerReport(world, Path.GetDirectoryName(Path.GetFullPath(exe)), build.summary.totalTime) + notes + "\n" + ProjectReport(world);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe)), "GeoBuildReport.txt"), report);
            return null;
        }

        // The built player by kind of file.
        public static string PlayerReport(GeoTiledWorld world, string folder, TimeSpan time)
        {
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
            long total = files.Sum(f => f.Length);
            var bundles = files.Where(f => f.Extension == ".bundle").ToList();
            var tileBundles = bundles.Where(f => f.Name.Contains("_scenes_")).OrderByDescending(f => f.Length).ToList();
            var sharedBundles = bundles.Where(f => f.Name.Contains("shared")).ToList();
            var otherBundles = bundles.Except(tileBundles).Except(sharedBundles).ToList();
            var resources = files.Where(f => f.Name.StartsWith("resources.assets")).ToList();
            var data = files.Except(bundles).Except(resources).Where(f => f.FullName.Contains("_Data")).ToList();
            var text = new StringBuilder();
            text.AppendLine($"Geo World build of {world.gameObject.scene.name}  {DateTime.Now:yyyy-MM-dd HH:mm}  built in {time.TotalMinutes:F0} min");
            text.AppendLine($"total {Size(total)} in {files.Count} files ({world.tiles.Count} tiles of {world.tileSize:F0} m, {world.columns} x {world.rows})");
            if (tileBundles.Count > 0)
            {
                var sizes = tileBundles.Select(f => f.Length).OrderBy(v => v).ToList();
                text.AppendLine($"  tile bundles      {Size(sizes.Sum()),10}  {tileBundles.Count} bundles, a tile {Size(sizes[sizes.Count / 2])} (median), {Size(sizes[sizes.Count - 1])} at most");
                foreach (var f in tileBundles.Take(10)) text.AppendLine($"      {Size(f.Length),10}  {f.Name}");
            }
            if (sharedBundles.Count > 0) text.AppendLine($"  shared bundle     {Size(sharedBundles.Sum(f => f.Length)),10}  the models, pictures and shaders the tiles share");
            if (otherBundles.Count > 0) text.AppendLine($"  other bundles     {Size(otherBundles.Sum(f => f.Length)),10}  {otherBundles.Count} bundles");
            if (resources.Count > 0) text.AppendLine($"  Resources         {Size(resources.Sum(f => f.Length)),10}  the tiles' tree data (Assets/Resources/TerrainChunker/TreeData) and other Resources");
            text.AppendLine($"  player data       {Size(data.Sum(f => f.Length)),10}  scenes in Build Settings, the engine, scripts");
            text.AppendLine($"  executable etc.   {Size(total - bundles.Sum(f => f.Length) - resources.Sum(f => f.Length) - data.Sum(f => f.Length)),10}");
            return text.ToString();
        }

        // What the world's assets weigh in the project (before a build compresses them as bundles): the tiles' assets (terrain,
        // satellite texture, meshes), their scenes and their tree data, and how many satellite textures are crunched.
        public static string ProjectReport(GeoTiledWorld world)
        {
            long assets = 0, scenes = 0, trees = 0; int tiles = 0, empty = 0;
            var largest = new List<(long size, string name)>();
            foreach (var tile in world.tiles)
            {
                long size = Length(tile.assetPath), scene = Length(tile.scenePath);
                assets += size; scenes += scene; tiles++; if (tile.empty) empty++;
                largest.Add((size + scene, tile.index.x + "_" + tile.index.y));
            }
            string hash = Hash128.Compute(world.folder ?? "").ToString();
            if (Directory.Exists(GeoTreeDataExporter.Folder))
                foreach (var file in Directory.GetFiles(GeoTreeDataExporter.Folder, hash + "_*.asset")) trees += new FileInfo(file).Length;
            var text = new StringBuilder();
            text.AppendLine($"Geo World assets of {world.gameObject.scene.name} in the project: {tiles} tiles ({empty} without buildings, roads or rivers)");
            text.AppendLine($"  tile assets       {Size(assets),10}  terrain, satellite texture ({world.settings.satelliteCompression}" +
                            (world.settings.satelliteCompression == GeoSatelliteCompression.Crunched ? $", quality {world.settings.satelliteQuality}" : "") + "), meshes, materials");
            text.AppendLine($"  tile scenes       {Size(scenes),10}");
            text.AppendLine($"  tree data         {Size(trees),10}  ({GeoTreeDataExporter.Folder})");
            text.AppendLine($"  a tile            {Size(tiles > 0 ? (assets + scenes + trees) / tiles : 0),10}  on average");
            foreach (var t in largest.OrderByDescending(t => t.size).Take(5)) text.AppendLine($"      {Size(t.size),10}  tile {t.name}");
            return text.ToString();
        }

        static long Length(string path) => !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;

        static string Size(long bytes) => bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("F2") + " GB" : (bytes / (double)(1L << 20)).ToString("F1") + " MB";

        static string FirstLine(string text) { int i = text.IndexOf('\n', text.IndexOf('\n') + 1); return i > 0 ? text.Substring(0, i) : text; }

        static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
