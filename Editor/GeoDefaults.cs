using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GeoWorld.Editor
{
    // How any area the Building Extractor app exports becomes a world: a tiled world (1 km tiles that stream in Play mode)
    // with satellite imagery on the terrain, road meshes with soft edges, ruin models for heavily damaged and destroyed
    // buildings and home models, with the export's roofs, for every building still standing. The defaults of
    // GenerationSettings are this; here come the model libraries (imported first when the project lacks them) and the
    // resolutions of a tile.
    public static class GeoDefaults
    {
        public const float TileSize = 1000;

        // the app's exports next to this project (buildings-extractor/exports), where a "Terrain & ground" package is <area>_env
        public static string ExportsFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "exports"));

        public static GenerationSettings Settings()
        {
            var settings = new GenerationSettings();
            FillLibraries(settings, true);
            return settings;
        }

        // Per 1 km tile: heights every 2 m (the elevation data has 30 m), ground and road paint every 2 m; the satellite
        // texture sizes itself (Auto: 2048, 0.49 m a texel).
        public static GenerationSettings TileSettings()
        {
            var settings = Settings();
            settings.heightResolution = 513; settings.textureResolution = 512; settings.roadPaintResolution = 512;
            return settings;
        }

        // The project's ruin and home libraries; import: brings them in from ruins/models and intact/models first when the
        // project does not have them yet.
        public static void FillLibraries(GenerationSettings settings, bool import)
        {
            if (!settings.ruinLibrary) settings.ruinLibrary = GeoRuinImporter.Library;
            if (!settings.homeLibrary) settings.homeLibrary = GeoHomeImporter.Library;
            if (!import) return;
            if (!settings.ruinLibrary && File.Exists(Path.Combine(GeoRuinImporter.DefaultSource, "INDEX.json")))
                settings.ruinLibrary = GeoRuinImporter.Import(GeoRuinImporter.DefaultSource);
            string homes = GeoHomeImporter.DefaultSource;
            if (!settings.homeLibrary && homes != null) settings.homeLibrary = GeoHomeImporter.Import(homes);
        }

        [MenuItem("Tools/Geo World/Generate world from export...", false, 0)]
        public static void GenerateMenu()
        {
            string start = Directory.Exists(ExportsFolder) ? ExportsFolder : Application.dataPath;
            string folder = EditorUtility.OpenFolderPanel("Select an unzipped Terrain & ground package (<area>_env)", start, "");
            if (!string.IsNullOrEmpty(folder)) Generate(folder);
        }

        // Builds (or brings up to date: unchanged tiles are skipped) the tiled world of a package folder, in
        // Assets/GeoWorld/Tiled/<package>, and opens its main scene.
        public static GeoTiledWorld Generate(string package, float tileSize = TileSize)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before generating.");
            string environment = Path.Combine(package, "environment.json"), buildings = Path.Combine(package, "buildings.json");
            if (!File.Exists(environment))
                throw new FileNotFoundException("No environment.json in " + package + ". Choose the unzipped Terrain & ground package of the app (exports/<area>_env).");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return null;
            GeoTerrainTextures.EnsureImported();
            if (!GeoRoadPhotos.Available) GeoRoadTextures.EnsureGenerated();
            var world = GeoTiles.Generate(environment, File.Exists(buildings) ? buildings : "", TileSettings(), tileSize);
            Debug.Log(world.report, world);
            return world;
        }

        // Unity -batchmode -projectPath ... -executeMethod GeoWorld.Editor.GeoDefaults.BatchGenerate -geoExport <package folder>
        // (the editor must be closed)
        public static void BatchGenerate()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-geoExport");
                if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("-geoExport <unzipped Terrain & ground package> is missing");
                Generate(args[i + 1]);
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }
    }
}
