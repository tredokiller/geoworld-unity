using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // One tile for GeoGenerator: its share of the export, read once for the whole world.
    internal sealed class GeoTileJob
    {
        public EnvironmentData env;     // bounds = the tile; the features that reach into its margin
        public BuildingsData buildings; // centres on the tile or its margin (those on the margin only keep trees back)
        public HeightField field;       // the tile and its margin, on the grid all tiles share
        public int margin;              // cells of margin on each side of the field
        public List<PathBuild> paths;   // roads and rivers cut to the margin, with heights from the whole export
        public float[] waterLevels;     // for env.water, from the whole lake
        public GeoBounds core;
        public Vector2Int index;
        public float size, baseY, range;
        public string folder, name;

        // the height field of the tile alone: the terrain
        public HeightField Core(HeightField field)
        {
            int n = field.resolution - 2 * margin;
            var result = new HeightField(core, n);
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) result.heights[z, x] = field.heights[z + margin, x + margin];
            return result;
        }
    }

    // A large world cut into square tiles. Everything that depends on more than its surroundings is worked out once for
    // the whole export: road and river heights (a bridge rises between its two ends), lake levels (from the whole shore)
    // and the height scale of the terrain. Then every tile is built like a small world from its share of the export
    // with a margin around it: the roads in the margin grade its edges, the buildings in the margin keep its trees back.
    // The tiles' height grids line up, so neighbours have the same ground at their common edge; roads, junctions and
    // markings are cut where the tiles meet (by triangle centre); a building, tree or prop belongs to the tile its centre
    // is on. A tile is an ordinary GeoWorldRoot in a scene and asset of its own, so memory stays flat however large the
    // world is, a tile can be rebuilt alone, and tiles whose export and settings did not change are skipped.
    public static class GeoTiles
    {
        public const int TerrainGroup = 4127;
        // Far more than any road grading or junction reaches, and room for the buildings next to a tile.
        public const float MarginMetres = 96;
        // bump when the generator draws tiles differently, so that "Rebuild changed tiles" rebuilds them
        internal const string Version = "geo-tiles-12"; // 12: crossings one mesh per chunk and material; 11: Auto building mode merges in tiles; 10: homes made from a list when the tile loads

        public static string DefaultFolder(string environmentPath) => GeoPaths.Project + "/Tiled/" + PackageName(environmentPath);
        static string PackageName(string environmentPath) => new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(environmentPath))).Name;
        public static string MasterScenePath(string folder) => folder + "/" + Path.GetFileName(folder) + ".unity";
        static string TileName(Vector2Int index) => "Tile " + index.x + "_" + index.y;

        // Builds (or brings up to date) the tiled world of an export. folder: where the main scene, tile scenes and assets
        // go (default Assets/GeoWorld/Tiled/<export folder>). separateScenes off: the tiles are children of the
        // GeoTiledWorld in the open scene (small worlds, tests). only: rebuild just these tiles.
        public static GeoTiledWorld Generate(string environmentPath, string buildingsPath, GenerationSettings settings, float tileSize,
            string folder = null, bool separateScenes = true, bool skipUnchanged = true, ICollection<Vector2Int> only = null)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before generating.");
            if (string.IsNullOrEmpty(environmentPath)) throw new ArgumentException("A tiled world needs environment.json from Terrain & ground export.");
            settings = Sanitized(settings);
            tileSize = Mathf.Clamp(tileSize, 100, 8000);
            folder = string.IsNullOrEmpty(folder) ? DefaultFolder(environmentPath) : folder.Replace('\\', '/').TrimEnd('/');
            var watch = Stopwatch.StartNew();
            try
            {
                GeoGenerator.progressPrefix = ""; GeoGenerator.progressFrom = 0; GeoGenerator.progressSpan = 0.02f;
                Bar("Reading the export and working out roads and lakes", 0);
                var source = GeoTileSource.Prepare(environmentPath, buildingsPath, settings, tileSize, null);
                Scene master = default;
                GeoTiledWorld world;
                if (separateScenes)
                {
                    master = OpenMaster(folder);
                    world = Object.FindObjectsByType<GeoTiledWorld>(FindObjectsSortMode.None).FirstOrDefault(w => w.gameObject.scene == master);
                }
                else world = Object.FindObjectsByType<GeoTiledWorld>(FindObjectsSortMode.None).FirstOrDefault(w => w.folder == folder && !w.separateScenes);
                if (!world)
                {
                    world = new GameObject("Geo World - " + PackageName(environmentPath) + " (tiled)").AddComponent<GeoTiledWorld>();
                    if (separateScenes) SceneManager.MoveGameObjectToScene(world.gameObject, master);
                }
                if (world.tileSize != tileSize || world.origin != source.origin) world.tiles.Clear(); // another grid: every tile is new
                world.environmentPath = environmentPath; world.buildingsPath = buildingsPath; world.settings = settings;
                world.tileSize = tileSize; world.origin = source.origin; world.columns = source.columns; world.rows = source.rows;
                world.baseY = source.baseY; world.heightRange = source.range; world.separateScenes = separateScenes; world.folder = folder;
                if (separateScenes && !Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Any(c => c.gameObject.scene == master))
                    SetupView(source);
                if (separateScenes) EnsureStreamer(world);

                // the middle of the world first: an interrupted run leaves the part that is looked at
                var order = new List<Vector2Int>();
                for (int z = 0; z < source.rows; z++) for (int x = 0; x < source.columns; x++) order.Add(new Vector2Int(x, z));
                var middle = new Vector2((source.columns - 1) * 0.5f, (source.rows - 1) * 0.5f);
                order.Sort((a, b) => (a - middle).sqrMagnitude.CompareTo((b - middle).sqrMagnitude));
                int built = 0, skipped = 0, number = 0;
                foreach (var index in order)
                {
                    number++;
                    if (only != null && !only.Contains(index)) continue;
                    var core = source.Core(index);
                    var entry = world.Find(index);
                    if (entry == null) world.tiles.Add(entry = new GeoTiledWorld.Tile { index = index });
                    entry.area = new Rect(core.minX, core.minZ, core.Width, core.Depth);
                    string hash = source.Hash(core, settings);
                    bool present = separateScenes ? !string.IsNullOrEmpty(entry.scenePath) && File.Exists(entry.scenePath) : Child(world, index);
                    if (skipUnchanged && present && entry.hash == hash) { skipped++; continue; }
                    GeoGenerator.progressPrefix = $"Tile {number} / {order.Count} ({index.x}, {index.y}): ";
                    GeoGenerator.progressFrom = 0.02f + 0.98f * (number - 1) / order.Count; GeoGenerator.progressSpan = 0.98f / order.Count;
                    BuildTile(source, world, entry, folder, settings, master);
                    entry.hash = hash; built++;
                    if (separateScenes) EditorSceneManager.SaveScene(master);
                    // the editor keeps what a closed scene used until it is told to let go
                    if (built % 6 == 0) { EditorUtility.UnloadUnusedAssetsImmediate(); GC.Collect(); }
                }
                world.tiles.Sort((a, b) => a.index.y != b.index.y ? a.index.y.CompareTo(b.index.y) : a.index.x.CompareTo(b.index.x));
                int buildings = world.tiles.Sum(t => t.buildings), roads = world.tiles.Sum(t => t.roads), props = world.tiles.Sum(t => t.props);
                world.report = $"{source.columns} x {source.rows} tiles of {tileSize:F0} m ({source.env.bounds.Width:F0} x {source.env.bounds.Depth:F0} m) | " +
                    $"{buildings} buildings | {roads} roads | {props} props and trees\n" +
                    $"This run: {built} tiles built, {skipped} unchanged tiles skipped, {watch.Elapsed.TotalMinutes:F1} min" +
                    (built > 0 ? $" ({world.tiles.Where(t => t.seconds > 0).Select(t => t.seconds).DefaultIfEmpty(0).Average():F0} s per tile)" : "") + ".\n" +
                    "Terrain heights, roads and lakes are worked out for the whole export, so the tiles meet without steps. " +
                    (separateScenes ? "Every tile is a scene of its own in " + folder + "/Tiles: open the tiles you need from this object's Inspector." : "") +
                    (source.notes.Count > 0 ? "\n" + string.Join("\n", source.notes.Distinct()) : "");
                EditorUtility.SetDirty(world);
                if (separateScenes) EditorSceneManager.SaveScene(master);
                else EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                Selection.activeGameObject = world.gameObject;
                return world;
            }
            finally
            {
                GeoGenerator.progressPrefix = ""; GeoGenerator.progressFrom = 0; GeoGenerator.progressSpan = 1;
                EditorUtility.ClearProgressBar();
            }
        }

        static void BuildTile(GeoTileSource source, GeoTiledWorld world, GeoTiledWorld.Tile entry, string folder, GenerationSettings settings, Scene master)
        {
            var watch = Stopwatch.StartNew();
            var job = source.Job(entry.index, source.Core(entry.index), folder + "/Tiles", TileName(entry.index));
            string oldAsset = entry.assetPath, newAsset;
            GeoWorldRoot root;
            if (world.separateScenes)
            {
                string scenePath = folder + "/Tiles/Tile_" + entry.index.x + "_" + entry.index.y + ".unity";
                var open = SceneManager.GetSceneByPath(scenePath);
                bool reopen = open.IsValid() && open.isLoaded;
                if (reopen)
                {
                    if (open.isDirty) throw new InvalidOperationException("Save or close the open tile scene " + scenePath + " before rebuilding it.");
                    EditorSceneManager.CloseScene(open, true);
                }
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                try
                {
                    SceneManager.SetActiveScene(scene);
                    root = GeoGenerator.Generate(source.environmentPath, source.buildingsPath, settings, null, false, job);
                    Count(entry, root); newAsset = root.assetPath;
                    EditorSceneManager.SaveScene(scene, scenePath);
                }
                finally { SceneManager.SetActiveScene(master); EditorSceneManager.CloseScene(scene, true); }
                if (reopen) EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                entry.scenePath = scenePath;
            }
            else
            {
                var old = Child(world, entry.index);
                root = GeoGenerator.Generate(source.environmentPath, source.buildingsPath, settings, null, false, job);
                root.transform.SetParent(world.transform, true);
                Count(entry, root); newAsset = root.assetPath;
                if (old) Object.DestroyImmediate(old.gameObject);
            }
            entry.assetPath = newAsset; entry.seconds = (float)watch.Elapsed.TotalSeconds;
            if (!string.IsNullOrEmpty(oldAsset) && oldAsset != newAsset && File.Exists(oldAsset)) AssetDatabase.DeleteAsset(oldAsset);
        }

        static void Count(GeoTiledWorld.Tile entry, GeoWorldRoot root)
        {
            entry.buildings = root.buildingCount; entry.roads = root.roadCount; entry.props = root.propCount;
            entry.empty = root.buildingCount == 0 && root.roadCount == 0 && root.riverCount == 0 && root.propCount == 0;
        }

        static GeoWorldRoot Child(GeoTiledWorld world, Vector2Int index)
        {
            foreach (Transform child in world.transform)
            {
                var root = child.GetComponent<GeoWorldRoot>();
                if (root && root.tile && root.tileIndex == index) return root;
            }
            return null;
        }

        // Rebuilds one tile (from its Inspector or the generator window) from the whole export, in its own scene, keeping the
        // height scale of its world so that it still meets its neighbours. Edited buildings and kept props survive as in a
        // normal rebuild; edited road nodes do not (a road is cut into tiles).
        public static GeoWorldRoot Regenerate(GeoWorldRoot tile, GenerationSettings settings)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before generating.");
            settings = Sanitized(settings ?? tile.settings);
            var core = new GeoBounds { minX = tile.tileArea.xMin, minZ = tile.tileArea.yMin, maxX = tile.tileArea.xMax, maxZ = tile.tileArea.yMax };
            var origin = new Vector2(core.minX - tile.tileIndex.x * tile.tileSize, core.minZ - tile.tileIndex.y * tile.tileSize);
            try
            {
                GeoGenerator.progressPrefix = ""; GeoGenerator.progressFrom = 0; GeoGenerator.progressSpan = 1;
                Bar("Reading the export and working out roads and lakes", 0);
                var source = GeoTileSource.Prepare(tile.environmentPath, tile.buildingsPath, settings, tile.tileSize, origin);
                source.baseY = tile.tileBaseY; source.range = tile.tileHeightRange;
                string folder = Path.GetDirectoryName(tile.assetPath)?.Replace('\\', '/') ?? GeoGenerator.OutputFolder;
                var job = source.Job(tile.tileIndex, core, folder, TileName(tile.tileIndex));
                string oldAsset = tile.assetPath;
                var index = tile.tileIndex;
                var root = GeoGenerator.Generate(tile.environmentPath, tile.buildingsPath, settings, tile, false, job);
                if (!string.IsNullOrEmpty(oldAsset) && oldAsset != root.assetPath && File.Exists(oldAsset)) AssetDatabase.DeleteAsset(oldAsset);
                foreach (var world in Object.FindObjectsByType<GeoTiledWorld>(FindObjectsSortMode.None))
                {
                    var entry = world.Find(index);
                    if (entry == null || Mathf.Abs(world.tileSize - root.tileSize) > 0.01f) continue;
                    entry.assetPath = root.assetPath; entry.hash = source.Hash(core, settings); Count(entry, root);
                    EditorUtility.SetDirty(world); EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                }
                Selection.activeGameObject = root.gameObject;
                Debug.Log("Geo World tile rebuilt. " + root.report, root);
                return root;
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static GenerationSettings Sanitized(GenerationSettings settings)
        {
            settings = JsonUtility.FromJson<GenerationSettings>(JsonUtility.ToJson(settings ?? new GenerationSettings()));
            settings.heightResolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(settings.heightResolution - 1), 32, 1024) + 1;
            settings.pathStep = Mathf.Clamp(settings.pathStep, 0.5f, 10);
            settings.riverDepth = Mathf.Clamp(settings.riverDepth, 0.5f, 8);
            // Road meshes draw the roads: the paint under them needs no more detail than the ground (three RGBA maps
            // of 1024 are 16 MB in every tile, of 512 a quarter of that).
            if (settings.roadMeshes) settings.roadPaintResolution = Mathf.Min(settings.roadPaintResolution, settings.textureResolution);
            // A tiled world is a town or a city: Auto merges its buildings and props. Counted per tile, Auto would keep an
            // object per building wherever a tile has fewer than Merge Above buildings (a tile of Kharkiv's apartment
            // blocks has a few hundred): 15 000 objects and 7 500 mesh colliders a tile, 250 MB, seconds to load.
            if (settings.buildingMode == GeoBuildingMode.Auto) settings.buildingMode = GeoBuildingMode.Merged;
            return settings;
        }

        static void Bar(string label, float value)
        { if (EditorUtility.DisplayCancelableProgressBar("Geo World Generator", label, value)) throw new OperationCanceledException("Generation cancelled."); }

        static Scene OpenMaster(string folder)
        {
            string path = MasterScenePath(folder);
            var scene = SceneManager.GetSceneByPath(path);
            if (scene.IsValid() && scene.isLoaded) { SceneManager.SetActiveScene(scene); return scene; }
            if (File.Exists(path)) return EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, path);
            return scene;
        }

        // The streamer that loads the tiles around the camera while playing, in the main scene next to the index.
        public static GeoTileStreamer EnsureStreamer(GeoTiledWorld world)
        {
            var streamer = Object.FindObjectsByType<GeoTileStreamer>(FindObjectsSortMode.None).FirstOrDefault(s => s.gameObject.scene == world.gameObject.scene);
            if (streamer) { if (!streamer.world) streamer.world = world; return streamer; }
            streamer = new GameObject("Geo tile streamer").AddComponent<GeoTileStreamer>();
            SceneManager.MoveGameObjectToScene(streamer.gameObject, world.gameObject.scene);
            streamer.world = world;
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            return streamer;
        }

        // A player loads only scenes listed in the Build Settings: the main scene first, then every tile.
        public static int AddToBuildSettings(GeoTiledWorld world)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            int added = 0;
            void Add(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || scenes.Any(s => s.path == path)) return;
                scenes.Add(new EditorBuildSettingsScene(path, true)); added++;
            }
            Add(world.gameObject.scene.path);
            foreach (var tile in world.tiles) Add(tile.scenePath);
            EditorBuildSettings.scenes = scenes.ToArray();
            return added;
        }

        // Sun, sky and a preview camera over the middle of the world, in the main scene (the tiles have none of their own).
        static void SetupView(GeoTileSource source)
        {
            var light = new GameObject("Sun").AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.8f;
            light.color = new Color(1, 0.94f, 0.83f); light.shadows = LightShadows.Soft; light.transform.rotation = Quaternion.Euler(48, -35, 0);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.56f, 0.65f, 0.73f); RenderSettings.ambientEquatorColor = new Color(0.49f, 0.53f, 0.48f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.32f, 0.28f); RenderSettings.fog = false;
            var camera = new GameObject("Main Camera").AddComponent<Camera>(); camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.10f, 0.16f, 0.20f);
            camera.fieldOfView = 48; camera.nearClipPlane = 0.3f; camera.farClipPlane = 15000; camera.allowHDR = true;
            camera.gameObject.AddComponent<AudioListener>();
            var controller = camera.gameObject.AddComponent<GeoPreviewCamera>();
            var b = source.env.bounds;
            float x = (b.minX + b.maxX) / 2, z = (b.minZ + b.maxZ) / 2;
            controller.focus = new Vector3(x, source.heights?.Sample(x, z) ?? 0, z);
            controller.moveSpeed = Mathf.Clamp(source.tileSize * 0.2f, 50, 400);
            controller.distance = Mathf.Min(Mathf.Max(b.Width, b.Depth), source.tileSize * 3) * 0.9f;
            controller.Apply();
        }

        // --- tiles in the editor -------------------------------------------------------------------------------------

        public static int OpenTiles(GeoTiledWorld world, Func<GeoTiledWorld.Tile, bool> which)
        {
            int opened = 0;
            foreach (var tile in world.tiles)
            {
                if (string.IsNullOrEmpty(tile.scenePath) || !File.Exists(tile.scenePath) || !which(tile)) continue;
                var scene = SceneManager.GetSceneByPath(tile.scenePath);
                if (scene.IsValid() && scene.isLoaded) continue;
                EditorSceneManager.OpenScene(tile.scenePath, OpenSceneMode.Additive); opened++;
            }
            Terrain.SetConnectivityDirty();
            return opened;
        }

        public static void CloseTiles(GeoTiledWorld world, Func<GeoTiledWorld.Tile, bool> which)
        {
            var scenes = new List<Scene>();
            foreach (var tile in world.tiles)
            {
                if (string.IsNullOrEmpty(tile.scenePath) || !which(tile)) continue;
                var scene = SceneManager.GetSceneByPath(tile.scenePath);
                if (scene.IsValid() && scene.isLoaded && scene != world.gameObject.scene) scenes.Add(scene);
            }
            if (!EditorSceneManager.SaveModifiedScenesIfUserWantsTo(scenes.ToArray())) return;
            foreach (var scene in scenes) EditorSceneManager.CloseScene(scene, true);
        }

        public static float Distance(GeoTiledWorld.Tile tile, Vector3 point)
        {
            float dx = Mathf.Max(tile.area.xMin - point.x, 0, point.x - tile.area.xMax), dz = Mathf.Max(tile.area.yMin - point.z, 0, point.z - tile.area.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // --- geometry -------------------------------------------------------------------------------------------------

        // Does the road or river (with its width) reach onto the bounds?
        public static bool Touches(PathBuild path, GeoBounds b)
        {
            float pad = path.feature.width * 0.5f + 2;
            var s = path.samples;
            for (int i = 1; i < s.Count; i++)
                if (Mathf.Max(s[i - 1].x, s[i].x) + pad >= b.minX && Mathf.Min(s[i - 1].x, s[i].x) - pad <= b.maxX &&
                    Mathf.Max(s[i - 1].z, s[i].z) + pad >= b.minZ && Mathf.Min(s[i - 1].z, s[i].z) - pad <= b.maxZ) return true;
            return false;
        }

        // The pieces of a road or river inside the bounds, on the path's own samples (so that every tile sees the same
        // points and heights), each with its control points in that stretch.
        public static List<PathBuild> ClipPath(PathBuild path, GeoBounds box, float pathStep)
        {
            var result = new List<PathBuild>();
            var s = path.samples;
            if (s == null || s.Count < 2) return result;
            var controlAt = ControlSamples(path, pathStep);
            int segment = 0, segments = s.Count - 1;
            while (segment < segments)
            {
                while (segment < segments && !Overlaps(s[segment], s[segment + 1], box)) segment++;
                if (segment >= segments) break;
                int first = segment;
                while (segment < segments && Overlaps(s[segment], s[segment + 1], box)) segment++;
                int last = segment; // samples first..last
                var controls = new List<Vector3> { s[first] };
                if (controlAt != null) { for (int c = 0; c < controlAt.Length; c++) if (controlAt[c] > first && controlAt[c] < last) controls.Add(path.controls[c]); }
                else for (int i = first + 1; i < last; i++) controls.Add(s[i]);
                controls.Add(s[last]);
                result.Add(new PathBuild { feature = path.feature, river = path.river, controls = controls, samples = s.GetRange(first, last - first + 1) });
            }
            return result;
        }

        static bool Overlaps(Vector3 a, Vector3 b, GeoBounds box) =>
            Mathf.Max(a.x, b.x) >= box.minX && Mathf.Min(a.x, b.x) <= box.maxX && Mathf.Max(a.z, b.z) >= box.minZ && Mathf.Min(a.z, b.z) <= box.maxZ;

        // The sample index of each control point (GeoData.Resample puts every control point among the samples), or null when
        // the samples were not made from these controls.
        static int[] ControlSamples(PathBuild path, float pathStep)
        {
            var c = path.controls;
            if (c == null || c.Count < 2) return null;
            float step = Mathf.Max(0.5f, pathStep);
            var result = new int[c.Count];
            int at = 0;
            for (int i = 1; i < c.Count; i++) { at += Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(c[i - 1], c[i]) / step)); result[i] = at; }
            return at == path.samples.Count - 1 ? result : null;
        }

        // The triangles of a world-space mesh whose centre is on the bounds (west and south edges included, east and north
        // not, so that every triangle belongs to one tile). Returns the mesh itself when it lies on the bounds already.
        public static Mesh Clip(Mesh mesh, GeoBounds b)
        {
            var vertices = mesh.vertices;
            if (vertices.Length == 0) return mesh;
            var box = mesh.bounds;
            if (box.min.x >= b.minX && box.max.x < b.maxX && box.min.z >= b.minZ && box.max.z < b.maxZ) return mesh;
            var remap = new int[vertices.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var kept = new List<int>();
            var triangles = new List<int>[mesh.subMeshCount];
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var source = mesh.GetTriangles(sub); var list = triangles[sub] = new List<int>(source.Length);
                for (int t = 0; t + 2 < source.Length; t += 3)
                {
                    Vector3 p = vertices[source[t]], q = vertices[source[t + 1]], r = vertices[source[t + 2]];
                    float cx = (p.x + q.x + r.x) / 3, cz = (p.z + q.z + r.z) / 3;
                    if (cx < b.minX || cx >= b.maxX || cz < b.minZ || cz >= b.maxZ) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = source[t + k];
                        if (remap[v] < 0) { remap[v] = kept.Count; kept.Add(v); }
                        list.Add(remap[v]);
                    }
                }
            }
            var normals = mesh.normals; var tangents = mesh.tangents; var uv = mesh.uv; var uv2 = mesh.uv2; var colors = mesh.colors32;
            var result = new Mesh { name = mesh.name, indexFormat = kept.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            result.SetVertices(kept.Select(v => vertices[v]).ToList());
            if (normals.Length == vertices.Length) result.SetNormals(kept.Select(v => normals[v]).ToList());
            if (tangents.Length == vertices.Length) result.SetTangents(kept.Select(v => tangents[v]).ToList());
            if (uv.Length == vertices.Length) result.SetUVs(0, kept.Select(v => uv[v]).ToList());
            if (uv2.Length == vertices.Length) result.SetUVs(1, kept.Select(v => uv2[v]).ToList());
            if (colors.Length == vertices.Length) result.SetColors(kept.Select(v => colors[v]).ToList());
            result.subMeshCount = triangles.Length;
            for (int sub = 0; sub < triangles.Length; sub++) result.SetTriangles(triangles[sub], sub, false);
            result.RecalculateBounds();
            Object.DestroyImmediate(mesh);
            return result;
        }
    }

    // The whole export, read and prepared once for all its tiles.
    internal sealed class GeoTileSource
    {
        public EnvironmentData env;
        public BuildingsData buildings;
        public GeoHeightSource heights;
        public List<PathBuild> paths;
        public float[] waterLevels;
        public string environmentPath, buildingsPath;
        public float tileSize, baseY, range;
        public Vector2 origin;
        public int columns, rows, resolution;
        public readonly List<string> notes = new List<string>();
        GenerationSettings settings;
        string files;
        Rect[] pathBox, roadBox, waterwayBox, waterBox, areaBox, barrierBox, hedgeBox, rowBox, railBox;
        readonly Dictionary<Vector2Int, List<BuildingFeature>> cells = new Dictionary<Vector2Int, List<BuildingFeature>>();

        // origin: the south-west corner of tile (0, 0); null: the export's
        public static GeoTileSource Prepare(string environmentPath, string buildingsPath, GenerationSettings settings, float tileSize, Vector2? origin)
        {
            var s = new GeoTileSource { environmentPath = environmentPath, buildingsPath = buildingsPath, settings = settings, tileSize = tileSize };
            s.env = GeoData.ReadEnvironment(environmentPath);
            s.buildings = string.IsNullOrEmpty(buildingsPath)
                ? new BuildingsData { format = "geo-buildings", version = 1, origin = s.env.origin }
                : GeoData.ReadBuildings(buildingsPath, s.env.origin);
            s.heights = GeoHeightSource.Load(s.env, environmentPath);
            if (s.heights == null) s.notes.Add("No heightmap: generated flat terrain at Y = 0.");
            var heights = s.heights;
            Func<float, float, float> height = heights != null ? (Func<float, float, float>)heights.Sample : (x, z) => 0;
            s.paths = GeoGenerator.PreparePaths(s.env, height, settings, s.notes);
            s.waterLevels = GeoGenerator.WaterLevels(s.env.water, height);
            // room below for river beds and lakes, as a single world has
            s.baseY = (heights?.min ?? 0) - 8 - (settings.carveRivers ? settings.riverDepth + 1 : 0);
            s.range = Mathf.Max(32, (heights?.max ?? 0) - s.baseY + 8);
            var b = s.env.bounds;
            s.origin = origin ?? new Vector2(b.minX, b.minZ);
            s.columns = Mathf.Max(1, Mathf.CeilToInt((b.maxX - s.origin.x) / tileSize - 0.001f));
            s.rows = Mathf.Max(1, Mathf.CeilToInt((b.maxZ - s.origin.y) / tileSize - 0.001f));
            s.resolution = settings.heightResolution;
            s.pathBox = s.paths.Select(p => Box(p.samples, p.feature.width)).ToArray();
            s.roadBox = s.env.roads.Select(f => Box(f?.points, f?.width ?? 0)).ToArray();
            s.waterwayBox = s.env.waterways.Select(f => Box(f?.points, f?.width ?? 0)).ToArray();
            s.railBox = s.env.railways.Select(f => Box(f?.points, f?.width ?? 0)).ToArray();
            s.waterBox = s.env.water.Select(f => Box(f?.rings)).ToArray();
            s.areaBox = s.env.areas.Select(f => Box(f?.rings)).ToArray();
            s.barrierBox = s.env.barriers.Select(f => Box(f?.points, f?.width ?? 0)).ToArray();
            s.hedgeBox = s.env.hedges.Select(f => Box(f?.points, f?.width ?? 0)).ToArray();
            s.rowBox = s.env.treeRows.Select(f => Box(f?.points, f?.width ?? 0)).ToArray();
            foreach (var building in s.buildings.buildings)
            {
                if (building == null || !GeoData.Finite(building.x) || !GeoData.Finite(building.z)) continue;
                var cell = s.Cell(building.x, building.z);
                if (!s.cells.TryGetValue(cell, out var list)) s.cells[cell] = list = new List<BuildingFeature>();
                list.Add(building);
            }
            var text = new StringBuilder();
            foreach (var file in new[] { environmentPath, buildingsPath, Package(s.env.terrain?.heightmap?.file), Package(s.env.materials?.file), Package(s.env.materials?.canopyFile) })
                if (!string.IsNullOrEmpty(file) && File.Exists(file)) { var info = new FileInfo(file); text.Append(info.Name).Append(info.Length).Append(info.LastWriteTimeUtc.Ticks).Append('|'); }
            s.files = text.ToString();
            return s;

            string Package(string name) => string.IsNullOrEmpty(name) ? null : GeoData.PackageFile(environmentPath, name);
        }

        Vector2Int Cell(float x, float z) => new Vector2Int(Mathf.FloorToInt((x - origin.x) / tileSize), Mathf.FloorToInt((z - origin.y) / tileSize));

        public GeoBounds Core(Vector2Int index)
        {
            float x = (float)(origin.x + (double)index.x * tileSize), z = (float)(origin.y + (double)index.y * tileSize);
            return new GeoBounds { minX = x, minZ = z, maxX = (float)(origin.x + (double)(index.x + 1) * tileSize), maxZ = (float)(origin.y + (double)(index.y + 1) * tileSize) };
        }

        public GeoTileJob Job(Vector2Int index, GeoBounds core, string folder, string name)
        {
            double cell = (double)tileSize / (resolution - 1);
            int margin = Mathf.CeilToInt(GeoTiles.MarginMetres / (float)cell);
            var around = new GeoBounds { minX = (float)(core.minX - margin * cell), minZ = (float)(core.minZ - margin * cell),
                maxX = (float)(core.minX + (resolution - 1 + margin) * cell), maxZ = (float)(core.minZ + (resolution - 1 + margin) * cell) };
            // every grid point from the tile's corner and the shared cell size, so neighbours sample the same places
            int n = resolution + 2 * margin;
            var field = new HeightField(around, n);
            if (heights != null)
                for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
                    field.heights[z, x] = heights.Sample((float)(core.minX + (x - margin) * cell), (float)(core.minZ + (z - margin) * cell));
            var area = new Rect(around.minX, around.minZ, around.Width, around.Depth);
            var e = new EnvironmentData { format = env.format, version = env.version, origin = env.origin, bounds = core, terrain = env.terrain,
                materials = env.materials, surround = around, outer = env.bounds };
            e.roads = Pick(env.roads, roadBox, area); e.waterways = Pick(env.waterways, waterwayBox, area); e.railways = Pick(env.railways, railBox, area);
            e.areas = Pick(env.areas, areaBox, area); e.barriers = Pick(env.barriers, barrierBox, area);
            e.hedges = Pick(env.hedges, hedgeBox, area); e.treeRows = Pick(env.treeRows, rowBox, area);
            var levels = new List<float>();
            for (int i = 0; i < env.water.Count; i++) if (waterBox[i].Overlaps(area)) { e.water.Add(env.water[i]); levels.Add(waterLevels[i]); }
            e.trees = env.trees.Where(t => t != null && around.Contains(t.x, t.z)).ToList();
            e.props = env.props.Where(p => p != null && around.Contains(p.x, p.z)).ToList();
            var pieces = new List<PathBuild>();
            for (int i = 0; i < paths.Count; i++) if (pathBox[i].Overlaps(area)) pieces.AddRange(GeoTiles.ClipPath(paths[i], around, settings.pathStep));
            var nearby = new BuildingsData { format = buildings.format, version = buildings.version, origin = buildings.origin, area = new BuildingArea { bounds = core } };
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                if (cells.TryGetValue(new Vector2Int(index.x + dx, index.y + dz), out var list))
                    nearby.buildings.AddRange(list.Where(b => around.Contains(b.x, b.z)));
            return new GeoTileJob { env = e, buildings = nearby, field = field, margin = margin, paths = pieces, waterLevels = levels.ToArray(),
                core = core, index = index, size = tileSize, baseY = baseY, range = range, folder = folder, name = name };
        }

        // Fingerprint of what a tile is built from: the export files, the settings (libraries by asset), the tile and the grid.
        public string Hash(GeoBounds core, GenerationSettings settings)
        {
            var copy = JsonUtility.FromJson<GenerationSettings>(JsonUtility.ToJson(settings));
            string libraries = Guid(copy.ruinLibrary) + "|" + Guid(copy.homeLibrary) + "|" + Guid(copy.propCatalog);
            copy.ruinLibrary = null; copy.homeLibrary = null; copy.propCatalog = null;
            string text = string.Join("|", GeoTiles.Version, files, JsonUtility.ToJson(copy), libraries,
                core.minX.ToString("R"), core.minZ.ToString("R"), tileSize.ToString("R"), baseY.ToString("R"), range.ToString("R"));
            using (var md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        static string Guid(Object asset) => asset ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) : "";

        static List<T> Pick<T>(List<T> features, Rect[] boxes, Rect area)
        {
            var result = new List<T>();
            for (int i = 0; i < features.Count; i++) if (features[i] != null && boxes[i].Overlaps(area)) result.Add(features[i]);
            return result;
        }

        static Rect Box(List<Vector3> points, float width)
        {
            if (points == null || points.Count == 0) return Rect.zero;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var p in points) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); }
            float pad = width * 0.5f + 2;
            return Rect.MinMaxRect(minX - pad, minZ - pad, maxX + pad, maxZ + pad);
        }

        static Rect Box(float[] flat, float width) => Box(GeoData.Points(flat), width);

        static Rect Box(float[][] rings)
        {
            if (rings == null) return Rect.zero;
            var points = new List<Vector3>();
            foreach (var ring in rings) points.AddRange(GeoData.Points(ring));
            return Box(points, 0);
        }
    }

    [CustomEditor(typeof(GeoTiledWorld))]
    public class GeoTiledWorldEditor : UnityEditor.Editor
    {
        static float radius = 1500;

        public override void OnInspectorGUI()
        {
            var world = (GeoTiledWorld)target;
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(world.report) ? "Tiled world" : world.report, MessageType.Info);
            DrawDefaultInspector();
            EditorGUILayout.Space(6);
            if (world.separateScenes)
            {
                GUILayout.Label("Tiles in the editor", EditorStyles.boldLabel);
                radius = EditorGUILayout.Slider("Around the view (m)", radius, 200, 10000);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Open tiles around the view"))
                    {
                        var view = SceneView.lastActiveSceneView;
                        Vector3 point = view ? view.pivot : Vector3.zero;
                        GeoTiles.OpenTiles(world, t => GeoTiles.Distance(t, point) <= radius);
                    }
                    if (GUILayout.Button("Open all tiles")) GeoTiles.OpenTiles(world, t => true);
                    if (GUILayout.Button("Close tiles")) GeoTiles.CloseTiles(world, t => true);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Stream tiles while playing")) Selection.activeObject = GeoTiles.EnsureStreamer(world);
                    if (GUILayout.Button("Add tile scenes to Build Settings")) Debug.Log(GeoTiles.AddToBuildSettings(world) + " scenes added to the Build Settings.");
                }
            }
            GUILayout.Label("Rebuild", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild changed tiles", GUILayout.Height(30))) Run(world, true);
                if (GUILayout.Button("Rebuild all tiles", GUILayout.Height(30))) Run(world, false);
            }
            EditorGUILayout.HelpBox("Changed tiles: those whose export files or settings changed since they were built. Select a tile's world object to rebuild it alone.", MessageType.None);
        }

        static void Run(GeoTiledWorld world, bool skipUnchanged)
        {
            try
            {
                if (world.separateScenes && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                GeoTiles.Generate(world.environmentPath, world.buildingsPath, world.settings, world.tileSize, world.folder, world.separateScenes, skipUnchanged);
            }
            catch (OperationCanceledException e) { Debug.Log(e.Message); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
