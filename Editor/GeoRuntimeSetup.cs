using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Runtime generation in a project: what a player needs to generate worlds and load world packages without the editor.
    //  - Prepare runtime assets: GeoRuntimeAssets (Assets/GeoWorld/Resources/GeoWorldRuntimeAssets.asset) lists the package's library
    //    (home and ruin models, their reduced copies, tree prefabs, road and ground pictures, building styles, the water material),
    //    the shaders and template materials, so that a build carries them and their shader variants (a build strips the variants
    //    no material in it uses: the generated materials are made in the player).
    //  - A Windows (or other standalone) player build gets the Building Extractor app (the package's Extractor~, with its own
    //    Python) in StreamingAssets/GeoWorld/Extractor (GeoRuntimeBuild).
    public static class GeoRuntimeSetup
    {
        public const string RegistryPath = GeoPaths.Project + "/Resources/" + GeoAssets.ResourceName + ".asset";
        public const string TemplatesFolder = GeoPaths.Project + "/Runtime/Templates";

        [MenuItem("Tools/Geo World/Runtime generation/Prepare runtime assets", false, 200)]
        static void PrepareMenu()
        {
            var registry = Prepare();
            EditorGUIUtility.PingObject(registry);
            Debug.Log($"Geo World: {registry.entries.Count} runtime assets, {registry.shaders.Count} shaders and {registry.templates.Count} template materials in {RegistryPath}. " +
                      "Players built from this project generate worlds and load world packages.");
        }

        public static GeoRuntimeAssets Prepare()
        {
            var homes = AssetDatabase.LoadAssetAtPath<GeoHomeLibrary>(GeoHomeLibrary.DefaultPath);
            var ruins = AssetDatabase.LoadAssetAtPath<GeoRuinLibrary>(GeoRuinLibrary.DefaultPath);
            var reduced = homes || ruins ? GeoModelReduction.Ensure(ruins, homes) : null;
            var trees = GeoTerrainTrees.Prefabs();
            var roots = new List<string>();
            void Root(string path) { if (!string.IsNullOrEmpty(path) && GeoPaths.Exists(path) && !roots.Contains(path)) roots.Add(path); }
            Root(GeoHomeLibrary.DefaultPath); Root(GeoRuinLibrary.DefaultPath);
            if (reduced) Root(AssetDatabase.GetAssetPath(reduced));
            foreach (var t in trees) Root(AssetDatabase.GetAssetPath(t));
            foreach (var guid in AssetDatabase.FindAssets("t:GeoBuildingStyle", new[] { GeoPaths.Package + "/BuildingStyles" })) Root(AssetDatabase.GUIDToAssetPath(guid));
            Root(GeoRoadPhotos.ColorPath(GeoRoadPhotos.Marked)); Root(GeoRoadPhotos.NormalPath(GeoRoadPhotos.Marked));
            foreach (var id in GeoRoadPhotos.Plain) { Root(GeoRoadPhotos.ColorPath(id)); Root(GeoRoadPhotos.NormalPath(id)); }
            for (int v = 0; v < GeoRoadTextures.Variants; v++) { Root(GeoRoadTextures.DiffusePath(v)); Root(GeoRoadTextures.NormalPath(v)); }
            Root(GeoRoadTextures.VersionFile);
            for (int i = 0; i < GeoTerrainTextures.Names.Length; i++) if (GeoTerrainTextures.Names[i] != null) { Root(GeoTerrainTextures.DiffusePath(i)); Root(GeoTerrainTextures.NormalPath(i)); }
            Root(GeoTerrainTextures.Stamp);
            Root(GeoPaths.Package + "/Runtime/Resources/" + GeoWaterLook.Resource + ".mat");
            // the water tiles are made with: the project's own (GeoWaterLook.ProjectResource) where it has one
            if (GeoWaterLook.Material) Root(AssetDatabase.GetAssetPath(GeoWaterLook.Material));

            var registry = AssetDatabase.LoadAssetAtPath<GeoRuntimeAssets>(RegistryPath);
            if (!registry)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath));
                AssetDatabase.Refresh();
                registry = ScriptableObject.CreateInstance<GeoRuntimeAssets>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
            }
            registry.packageVersion = GeoWorldGenerator.Version;
            registry.homeLibrary = homes; registry.ruinLibrary = ruins; registry.reducedModels = reduced;
            registry.entries.Clear();
            var seen = new HashSet<Object>();
            foreach (var path in AssetDatabase.GetDependencies(roots.ToArray(), true).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".asmdef", StringComparison.Ordinal)) continue;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (!o || !Kept(o) || !seen.Add(o)) continue;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long id)) continue;
                    registry.entries.Add(new GeoRuntimeAssets.Entry { key = guid + ":" + id, path = path, asset = o });
                }
            }
            registry.shaders = Shaders.Select(Shader.Find).Where(s => s).ToList();
            registry.templates = Templates();
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssets();
            return registry;
        }

        // The shaders a player draws worlds with. Unity makes the normal map of an instanced terrain (as the tiles' are) with its
        // built-in GenerateNormalmap, which a build leaves out when none of its scenes has a terrain: the terrains a player loads
        // would have no normal map, and Terrain/Lit would light them as if they faced away from the sun (dark grey).
        internal static readonly string[] Shaders =
        {
            "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Terrain/Lit", "GeoWorld/Road", "GeoWorld/Water",
            "Hidden/TerrainEngine/GenerateNormalmap",
        };

        // What a package can refer to: meshes, materials, textures, prefabs (their root), the library assets and text stamps. Not
        // an object of a type a player has no script for (URP keeps an editor-only AssetVersion under every material: a player
        // would log a missing script and a layout error for each one when it loads the registry).
        internal static bool Kept(Object o) =>
            (o.hideFlags & HideFlags.DontSaveInBuild) == 0 && InPlayer(o.GetType()) &&
            (o is Mesh || o is Material || o is Texture || o is Shader || o is TextAsset || o is TerrainLayer ||
             (o is ScriptableObject && !(o is UnityEditor.Editor) && !(o is EditorWindow)) ||
             (o is GameObject go && go.transform.parent == null));

        static HashSet<string> editorAssemblies;

        // A type a player build has: not one of the editor's own modules or of a script assembly only the editor compiles.
        internal static bool InPlayer(Type type)
        {
            string name = type.Assembly.GetName().Name;
            if (name.StartsWith("UnityEditor", StringComparison.Ordinal)) return false;
            editorAssemblies ??= new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Where(a => (a.flags & AssemblyFlags.EditorAssembly) != 0).Select(a => a.name));
            return !editorAssemblies.Contains(name);
        }

        // One material for each shader and keyword set the generator makes (instancing on, as it makes them): a build keeps the
        // shader variants of the materials it holds (Graphics settings strip the rest, instancing variants included).
        static List<Material> Templates()
        {
            Directory.CreateDirectory(TemplatesFolder);
            AssetDatabase.Refresh();
            var result = new List<Material>();
            void Make(string name, string shaderName, params string[] keywords)
            {
                var shader = Shader.Find(shaderName);
                if (!shader) return;
                string path = TemplatesFolder + "/" + name + ".mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (!material) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
                material.shader = shader; material.enableInstancing = true; material.shaderKeywords = keywords;
                EditorUtility.SetDirty(material);
                result.Add(material);
            }
            Make("Lit", "Universal Render Pipeline/Lit");
            Make("Lit normal map", "Universal Render Pipeline/Lit", "_NORMALMAP");
            Make("Road", "GeoWorld/Road");
            Make("Terrain", "Universal Render Pipeline/Terrain/Lit");
            Make("Terrain normal map", "Universal Render Pipeline/Terrain/Lit", "_NORMALMAP");
            Make("Water", "GeoWorld/Water");
            return result;
        }

        // ---------------------------------------------------------------------------------------------- packages in the editor

        [MenuItem("Tools/Geo World/Runtime generation/Make a world package from an export...", false, 201)]
        static void PackageMenu()
        {
            string folder = EditorUtility.OpenFolderPanel("An export (a folder with environment.json)", GeoExtractor.ExportsFolder, "");
            if (string.IsNullOrEmpty(folder)) return;
            string name = new DirectoryInfo(folder).Name;
            try
            {
                var package = GeoWorldGenerator.BuildPackage(folder, new GeoWorldRequest { name = name },
                    new GeoProgress<GeoWorldProgress>(p => { if (EditorUtility.DisplayCancelableProgressBar("Geo World package", p.message, p.value)) throw new OperationCanceledException(); }));
                Debug.Log($"Geo World: package {package.Folder}: {package.Tiles.Count} tiles, content {package.Manifest.contentHash}");
                EditorUtility.RevealInFinder(Path.Combine(package.Folder, GeoWorldPackage.ManifestFile));
            }
            catch (OperationCanceledException) { Debug.Log("Geo World: package cancelled."); }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // Batch: -executeMethod GeoWorld.Editor.GeoRuntimeSetup.BatchPackage -geoExport <folder> -geoOut <package folder> [-geoName n]
        public static void BatchPackage()
        {
            try
            {
                string export = Arg("-geoExport"), output = Arg("-geoOut"), name = Arg("-geoName") ?? new DirectoryInfo(export).Name;
                var package = GeoWorldGenerator.BuildPackage(export, new GeoWorldRequest { name = name, outputFolder = output },
                    new GeoProgress<GeoWorldProgress>(p => Debug.Log("[GeoWorldPackage] " + p)));
                Debug.Log($"[GeoWorldPackage] done {package.Folder} tiles={package.Tiles.Count} content={package.Manifest.contentHash}");
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }

        // Batch: -executeMethod GeoWorld.Editor.GeoRuntimeSetup.BatchPrepare
        public static void BatchPrepare()
        {
            try { var r = Prepare(); Debug.Log($"[GeoWorldRuntime] prepared {r.entries.Count} assets"); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }

        internal static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }

    // A standalone player of a project with runtime generation (GeoRuntimeAssets prepared) gets the Building Extractor app next to
    // its data: <name>_Data/StreamingAssets/GeoWorld/Extractor (the app's code, data and, on Windows, its own Python; about 30 MB).
    // Folders ending in ~ are never part of a build, and an installed package is read-only, so it is copied after the build.
    public sealed class GeoRuntimeBuild : IPostprocessBuildWithReport, IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        static bool Wanted => File.Exists(GeoRuntimeSetup.RegistryPath);

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!Wanted) return;
            var registry = AssetDatabase.LoadAssetAtPath<GeoRuntimeAssets>(GeoRuntimeSetup.RegistryPath);
            if (registry && registry.packageVersion != GeoWorldGenerator.Version)
                Debug.LogWarning($"Geo World: the runtime assets were prepared with {registry.packageVersion}, the package is {GeoWorldGenerator.Version}: " +
                                 "Tools > Geo World > Runtime generation > Prepare runtime assets before shipping.");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (!Wanted) return;
            var target = report.summary.platform;
            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneLinux64 && target != BuildTarget.StandaloneOSX) return;
            string exe = report.summary.outputPath;
            string data = target == BuildTarget.StandaloneOSX ? Path.Combine(exe, "Contents", "Resources", "Data") : Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + "_Data");
            string source = Path.GetFullPath(GeoPaths.Package + "/Extractor~");
            if (!File.Exists(Path.Combine(source, "cli.py"))) { Debug.LogWarning("Geo World: the package has no Building Extractor app (Extractor~): the player cannot extract areas."); return; }
            string destination = Path.Combine(data, "StreamingAssets", GeoExtractorRunner.StreamingFolder);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            long bytes = Copy(source, destination);
            Debug.Log($"Geo World: the Building Extractor app ({bytes / 1048576f:F0} MB) is in {destination}.");
        }

        static readonly string[] Skipped = { "__pycache__", "cache", "exports", ".git" };

        static long Copy(string from, string to)
        {
            long bytes = 0;
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                if (file.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(to, Path.GetFileName(file));
                File.Copy(file, target, true); bytes += new FileInfo(target).Length;
            }
            foreach (var dir in Directory.GetDirectories(from))
                if (!Skipped.Contains(Path.GetFileName(dir))) bytes += Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
            return bytes;
        }
    }
}
