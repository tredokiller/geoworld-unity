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
        public const int TerrainGroup = GeoTileGeometry.TerrainGroup;
        public const float MarginMetres = GeoTileGeometry.MarginMetres;
        public const float RiverReach = GeoTileGeometry.RiverReach;
        internal const string Version = GeoTileGeometry.Version;
        // The settings as a tile's fingerprint takes them (GeoTileGeometry.TileSettingsJson).
        public static string TileSettingsJson(GenerationSettings settings) => GeoTileGeometry.TileSettingsJson(settings);

        public static string DefaultFolder(string environmentPath) => GeoPaths.Project + "/Tiled/" + PackageName(environmentPath);
        // Several exports: named after the folder that holds them (the parts of one area), else after the first.
        public static string DefaultFolder(IList<GeoTiledWorld.Part> parts)
        {
            if (parts.Count == 1) return DefaultFolder(parts[0].environmentPath);
            var parents = parts.Select(p => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(p.environmentPath)))).Distinct().ToList();
            string name = parents.Count == 1 ? new DirectoryInfo(parents[0]).Name : PackageName(parts[0].environmentPath) + "_" + parts.Count + "_parts";
            return GeoPaths.Project + "/Tiled/" + name;
        }
        static string PackageName(string environmentPath) => new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(environmentPath))).Name;
        public static string MasterScenePath(string folder) => folder + "/" + Path.GetFileName(folder) + ".unity";
        static string TileName(Vector2Int index) => "Tile " + index.x + "_" + index.y;

        // The exports in a folder: the folder itself when it holds environment.json, else every folder in it that does
        // (the parts of one area), in name order with numbers counted as numbers (part_2 before part_10).
        public static List<GeoTiledWorld.Part> PartsIn(string folder)
        {
            var result = new List<GeoTiledWorld.Part>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return result;
            var folders = File.Exists(Path.Combine(folder, "environment.json")) ? new List<string> { folder }
                : Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, "environment.json"))).OrderBy(d => NaturalKey(Path.GetFileName(d)), StringComparer.Ordinal).ToList();
            foreach (var f in folders)
            {
                string buildings = Path.Combine(f, "buildings.json");
                result.Add(new GeoTiledWorld.Part { environmentPath = Path.Combine(f, "environment.json"), buildingsPath = File.Exists(buildings) ? buildings : "" });
            }
            return result;
        }

        static string NaturalKey(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"\d+", m => m.Value.PadLeft(12, '0'));

        // Builds (or brings up to date) the tiled world of an export. folder: where the main scene, tile scenes and assets
        // go (default Assets/GeoWorld/Tiled/<export folder>). separateScenes off: the tiles are children of the
        // GeoTiledWorld in the open scene (small worlds, tests). only: rebuild just these tiles.
        public static GeoTiledWorld Generate(string environmentPath, string buildingsPath, GenerationSettings settings, float tileSize,
            string folder = null, bool separateScenes = true, bool skipUnchanged = true, ICollection<Vector2Int> only = null)
        {
            if (string.IsNullOrEmpty(environmentPath)) throw new ArgumentException("A tiled world needs environment.json from Terrain & ground export.");
            return Generate(new List<GeoTiledWorld.Part> { new GeoTiledWorld.Part { environmentPath = environmentPath, buildingsPath = buildingsPath } },
                settings, tileSize, folder, separateScenes, skipUnchanged, only);
        }

        // The tiled world of several exports of one area (the parts of a large city, made with one fixed origin). All the
        // exports are cut on the same grid (see GeoTilePlan) and every tile is built once: where exports overlap, the earlier
        // one in the list owns the ground; a tile where exports meet is built from all of them.
        public static GeoTiledWorld Generate(IList<GeoTiledWorld.Part> exports, GenerationSettings settings, float tileSize,
            string folder = null, bool separateScenes = true, bool skipUnchanged = true, ICollection<Vector2Int> only = null)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Stop Play mode before generating.");
            if (exports == null || exports.Count == 0 || exports.Any(e => e == null || string.IsNullOrEmpty(e.environmentPath)))
                throw new ArgumentException("A tiled world needs environment.json from Terrain & ground export.");
            var parts = exports.Select(e => new GeoTiledWorld.Part { environmentPath = e.environmentPath, buildingsPath = e.buildingsPath }).ToList();
            settings = Sanitized(settings);
            tileSize = Mathf.Clamp(tileSize, 100, 8000);
            folder = string.IsNullOrEmpty(folder) ? DefaultFolder(parts) : folder.Replace('\\', '/').TrimEnd('/');
            var watch = Stopwatch.StartNew();
            try
            {
                GeoGenerator.progressPrefix = ""; GeoGenerator.progressFrom = 0; GeoGenerator.progressSpan = 0.02f;
                var plan = Plan(parts, tileSize, out var single, out var terrains);
                // exports that meet share one ground (a datum and a blend over the overlap) and the levels of their lakes
                GeoHeightBlend blend = null;
                if (parts.Count > 1) { blend = new GeoHeightBlend(plan, parts, terrains); blend.Align(); }
                var lakeLevels = new Dictionary<string, float>();
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
                    world = new GameObject("Geo World - " + (parts.Count == 1 ? PackageName(parts[0].environmentPath) : Path.GetFileName(folder)) + " (tiled)").AddComponent<GeoTiledWorld>();
                    world.addressableScenes = true;
                    if (separateScenes) SceneManager.MoveGameObjectToScene(world.gameObject, master);
                }
                // another grid (a size, or a world made before the grid was shared): every tile is new
                if (world.tileSize != tileSize || world.origin != Vector2.zero) { foreach (var old in world.tiles) Discard(world, old); world.tiles.Clear(); }
                world.environmentPath = parts[0].environmentPath; world.buildingsPath = parts[0].buildingsPath; world.parts = parts; world.settings = settings;
                world.tileSize = tileSize; world.origin = Vector2.zero; world.firstIndex = plan.first; world.columns = plan.columns; world.rows = plan.rows;
                world.separateScenes = separateScenes; world.folder = folder;
                // tiles that no export reaches any more
                foreach (var gone in world.tiles.Where(t => !plan.tiles.ContainsKey(t.index)).ToList()) { Discard(world, gone); world.tiles.Remove(gone); }
                if (separateScenes) EnsureStreamer(world).SetRadius(settings.loadRadius);

                // the middle of the world first: an interrupted run leaves the part that is looked at
                var order = plan.tiles.Keys.ToList();
                var middle = new Vector2(plan.first.x + (plan.columns - 1) * 0.5f, plan.first.y + (plan.rows - 1) * 0.5f);
                order.Sort((a, b) => ((Vector2)a - middle).sqrMagnitude.CompareTo(((Vector2)b - middle).sqrMagnitude));
                int built = 0, skipped = 0, number = 0, seams = 0;
                float worldBase = float.MaxValue, worldTop = float.MinValue;
                var notes = new List<string>();
                var pending = new Dictionary<Vector2Int, List<Fragment>>(); // tiles where exports meet: what each export gives, built when all have
                bool view = false;
                for (int p = 0; p < parts.Count; p++)
                {
                    var mine = order.Where(index => plan.tiles[index].Contains(p) && (only == null || only.Contains(index))).ToList();
                    if (mine.Count == 0) continue;
                    GeoGenerator.progressPrefix = parts.Count > 1 ? $"Export {p + 1} / {parts.Count}: " : ""; GeoGenerator.progressFrom = 0; GeoGenerator.progressSpan = 0.02f;
                    Bar("Reading the export and working out roads and lakes", 0);
                    var source = GeoTileSource.Prepare(parts[p].environmentPath, parts[p].buildingsPath, settings, tileSize, Vector2.zero, single, blend, p, lakeLevels);
                    worldBase = Mathf.Min(worldBase, source.baseY); worldTop = Mathf.Max(worldTop, source.baseY + source.range);
                    notes.AddRange(source.notes);
                    if (separateScenes && !view && !Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Any(c => c.gameObject.scene == master)) SetupView(source, plan.Outer());
                    view = true;
                    foreach (var index in mine)
                    {
                        var core = GeoTilePlan.Tile(index, tileSize);
                        if (plan.tiles[index].Count > 1)
                        {
                            if (!pending.TryGetValue(index, out var fragments)) pending[index] = fragments = new List<Fragment>();
                            fragments.Add(new Fragment { part = p, job = source.Job(index, core, folder + "/Tiles", TileName(index), plan, p), hash = source.Hash(core, settings) + "|" + Region(plan.region[p]) });
                            continue;
                        }
                        number++;
                        var entry = world.Find(index);
                        if (entry == null) world.tiles.Add(entry = new GeoTiledWorld.Tile { index = index });
                        entry.area = new Rect(core.minX, core.minZ, core.Width, core.Depth);
                        entry.source = SourceName(parts, plan.tiles[index]);
                        string hash = source.Hash(core, settings);
                        if (Skip(world, entry, hash, separateScenes, skipUnchanged)) { skipped++; continue; }
                        Tick(number, order.Count);
                        BuildTile(source.Job(index, core, folder + "/Tiles", TileName(index)), parts[p], world, entry, folder, settings, master);
                        entry.hash = hash; built++;
                        if (separateScenes) EditorSceneManager.SaveScene(master);
                        // the editor keeps what a closed scene used until it is told to let go
                        if (built % 6 == 0) { EditorUtility.UnloadUnusedAssetsImmediate(); GC.Collect(); }
                    }
                    source = null;
                }

                // where exports meet: each tile from what every export reaching it gives
                foreach (var index in order)
                {
                    if (!pending.TryGetValue(index, out var fragments)) continue;
                    var contributors = plan.tiles[index];
                    fragments = contributors.Select(c => fragments.FirstOrDefault(f => f.part == c)).Where(f => f != null && f.job != null).ToList();
                    if (fragments.Count == 0) continue;
                    number++; seams++;
                    var core = GeoTilePlan.Tile(index, tileSize);
                    var entry = world.Find(index);
                    if (entry == null) world.tiles.Add(entry = new GeoTiledWorld.Tile { index = index });
                    entry.area = new Rect(core.minX, core.minZ, core.Width, core.Depth);
                    entry.source = SourceName(parts, contributors);
                    string hash = Digest(string.Join("||", fragments.Select(f => f.hash)));
                    if (Skip(world, entry, hash, separateScenes, skipUnchanged)) { skipped++; continue; }
                    Tick(number, order.Count);
                    var job = GeoTileJob.Merge(fragments.Select(f => f.job).ToList(), plan);
                    var main = parts[fragments[0].part];
                    BuildTile(job, main, world, entry, folder, settings, master);
                    entry.hash = hash; built++;
                    if (separateScenes) EditorSceneManager.SaveScene(master);
                    if (built % 6 == 0) { EditorUtility.UnloadUnusedAssetsImmediate(); GC.Collect(); }
                    pending.Remove(index);
                }
                if (worldBase < worldTop) { world.baseY = worldBase; world.heightRange = worldTop - worldBase; }
                world.tiles.Sort((a, b) => a.index.y != b.index.y ? a.index.y.CompareTo(b.index.y) : a.index.x.CompareTo(b.index.x));
                int buildings = world.tiles.Sum(t => t.buildings), roads = world.tiles.Sum(t => t.roads), props = world.tiles.Sum(t => t.props);
                var outer = plan.Outer();
                world.report = $"{plan.columns} x {plan.rows} tiles of {tileSize:F0} m ({outer.Width:F0} x {outer.Depth:F0} m)" +
                    (parts.Count > 1 ? $", {parts.Count} exports ({plan.tiles.Values.Count(c => c.Count > 1)} tiles where they meet)" : "") +
                    $" | {buildings} buildings | {roads} roads | {props} props and trees\n" +
                    $"This run: {built} tiles built, {skipped} unchanged tiles skipped, {watch.Elapsed.TotalMinutes:F1} min" +
                    (built > 0 ? $" ({world.tiles.Where(t => t.seconds > 0).Select(t => t.seconds).DefaultIfEmpty(0).Average():F0} s per tile)" : "") + ".\n" +
                    "Terrain heights, roads and lakes are worked out for the whole export, so the tiles meet without steps. " +
                    (parts.Count > 1 ? "The exports share one grid; each tile is built once, and where exports meet from every export that reaches it. " : "") +
                    (separateScenes ? "Every tile is a scene of its own in " + folder + "/Tiles: open the tiles you need from this object's Inspector." : "") +
                    (notes.Count > 0 ? "\n" + string.Join("\n", notes.Distinct()) : "");
                // the whole world seen from high above, and the far ring around it
                if (separateScenes && settings.overview != null && settings.overview.enabled && (built > 0 || !world.GetComponent<GeoOverview>()))
                    world.report += "\n" + BakeOverview(world);
                // what the ground is, for a game to ask anywhere (GeoLandCover): the exports' ground maps on the tiles' grid
                if (built > 0 || !world.GetComponent<GeoLandCover>())
                    world.report += "\n" + BakeLandCover(world);
                // one copy of what the tiles share in the Addressables build, not one in every tile's bundle
                if (separateScenes && world.addressableScenes && built > 0)
                {
                    int shared = GeoTileAddressables.ShareDependencies(world);
                    if (shared > 0) world.report += $"\n{shared} assets the tiles share are in the Addressables group \"{GeoTileAddressables.SharedGroupName(world)}\".";
                }
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

        // Bakes the world's overview (GeoOverviewBaker) and says what came of it, for the world's report.
        public static string BakeOverview(GeoTiledWorld world, bool refreshFar = false)
        {
            var notes = new List<string>();
            try { GeoOverviewBaker.Bake(world, notes, refreshFar); }
            catch (OperationCanceledException) { notes.Add("Overview: cancelled; Bake overview in the world's Inspector makes it."); }
            catch (Exception e) { notes.Add("Overview: not made (" + e.Message + ")."); Debug.LogException(e); }
            return string.Join("\n", notes);
        }

        // Bakes the world's land cover (GeoLandCoverBaker) and says what came of it, for the world's report.
        public static string BakeLandCover(GeoTiledWorld world)
        {
            var notes = new List<string>();
            try { GeoLandCoverBaker.Bake(world, notes); }
            catch (OperationCanceledException) { notes.Add("Land cover: cancelled; Bake land cover in the world's Inspector makes it."); }
            catch (Exception e) { notes.Add("Land cover: not made (" + e.Message + ")."); Debug.LogException(e); }
            return string.Join("\n", notes);
        }

        // What one export gives to a tile it shares with others.
        sealed class Fragment { public int part; public GeoTileJob job; public string hash; }

        static string Region(GeoBounds r) => r.minX.ToString("R") + "," + r.minZ.ToString("R") + "," + r.maxX.ToString("R") + "," + r.maxZ.ToString("R");

        static string SourceName(IList<GeoTiledWorld.Part> parts, IEnumerable<int> which) =>
            string.Join(" + ", which.Select(i => PackageName(parts[i].environmentPath)));

        static string Digest(string text)
        {
            using (var md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        // Is the tile there and built from what it would be built from now?
        static bool Skip(GeoTiledWorld world, GeoTiledWorld.Tile entry, string hash, bool separateScenes, bool skipUnchanged)
        {
            bool present = separateScenes ? !string.IsNullOrEmpty(entry.scenePath) && File.Exists(entry.scenePath) : Child(world, entry.index);
            if (!skipUnchanged || !present || entry.hash != hash) return false;
            if (separateScenes && world.addressableScenes) GeoTileAddressables.Register(world, entry);
            else if (separateScenes) GeoTileAddressables.Unregister(entry);
            return true;
        }

        static void Tick(int number, int count)
        {
            GeoGenerator.progressPrefix = $"Tile {number} / {count}: ";
            GeoGenerator.progressFrom = 0.02f + 0.98f * (number - 1) / count; GeoGenerator.progressSpan = 0.98f / count;
        }

        // The grid of the exports and who builds what. For one export the environment is kept to build from.
        static GeoTilePlan Plan(IList<GeoTiledWorld.Part> parts, float tileSize, out EnvironmentData single, out List<TerrainInfo> terrains)
        {
            single = null;
            terrains = new List<TerrainInfo>();
            var extents = new GeoBounds[parts.Count];
            EnvironmentData first = null;
            for (int i = 0; i < parts.Count; i++)
            {
                Bar(parts.Count > 1 ? $"Reading export {i + 1} / {parts.Count}" : "Reading the export and working out roads and lakes", 0);
                var env = GeoData.ReadEnvironment(parts[i].environmentPath);
                extents[i] = env.bounds; terrains.Add(env.terrain);
                if (first == null) first = env;
                else if (!SameOrigin(first.origin, env.origin))
                    throw new InvalidDataException(parts[0].environmentPath + " and " + parts[i].environmentPath + " have different origins, so their coordinates do not line up. " +
                        "Export all the parts of one area with the same fixed origin.");
                if (parts.Count == 1) single = env;
            }
            return GeoTilePlan.Make(extents, tileSize);
        }

        static bool SameOrigin(GeoOrigin a, GeoOrigin b) =>
            a == null || b == null ? a == b : Math.Abs(a.lat - b.lat) <= 0.000001 && Math.Abs(a.lon - b.lon) <= 0.000001;

        // Gives the files of a tile up (it is no longer in the world).
        static void Discard(GeoTiledWorld world, GeoTiledWorld.Tile entry)
        {
            GeoTileAddressables.Unregister(entry);
            var child = Child(world, entry.index);
            if (child) Object.DestroyImmediate(child.gameObject);
            // a tile open in the editor is closed first: else its scene stayed open and on disk, drawing ground whose data was
            // deleted. One with unsaved changes is kept whole.
            if (!string.IsNullOrEmpty(entry.scenePath))
            {
                var open = SceneManager.GetSceneByPath(entry.scenePath);
                if (open.isLoaded && !open.isDirty) EditorSceneManager.CloseScene(open, true);
                if (SceneManager.GetSceneByPath(entry.scenePath).isLoaded)
                {
                    Debug.LogWarning($"Geo World: tile {entry.index.x}, {entry.index.y} is no longer part of the world, but its scene is open with unsaved changes, so it is kept: {entry.scenePath}");
                    return;
                }
            }
            if (!string.IsNullOrEmpty(entry.scenePath) && File.Exists(entry.scenePath)) AssetDatabase.DeleteAsset(entry.scenePath);
            if (!string.IsNullOrEmpty(entry.assetPath) && File.Exists(entry.assetPath)) AssetDatabase.DeleteAsset(entry.assetPath);
        }

        // job: the tile's data; part: the export it is built from (for a tile of several, the one with most of it)
        static void BuildTile(GeoTileJob job, GeoTiledWorld.Part part, GeoTiledWorld world, GeoTiledWorld.Tile entry, string folder, GenerationSettings settings, Scene master)
        {
            var watch = Stopwatch.StartNew();
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
                    root = GeoGenerator.Generate(part.environmentPath, part.buildingsPath, settings, null, false, job);
                    Count(entry, root); newAsset = root.assetPath;
                    EditorSceneManager.SaveScene(scene, scenePath);
                }
                finally { SceneManager.SetActiveScene(master); EditorSceneManager.CloseScene(scene, true); }
                if (reopen) EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                entry.scenePath = scenePath;
                if (world.addressableScenes) GeoTileAddressables.Register(world, entry);
                else GeoTileAddressables.Unregister(entry);
            }
            else
            {
                var old = Child(world, entry.index);
                root = GeoGenerator.Generate(part.environmentPath, part.buildingsPath, settings, null, false, job);
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
            if (tile.tileParts > 1)
                throw new InvalidOperationException("This tile is built from " + tile.tileParts + " exports. Rebuild it from the Geo Tiled World object (Rebuild all tiles), which has all of them.");
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

        static GenerationSettings Sanitized(GenerationSettings settings) => GeoWorldBuild.TileSettings(settings);

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
            if (!world.addressableScenes)
                foreach (var tile in world.tiles) { GeoTileAddressables.Unregister(tile); Add(tile.scenePath); }
            EditorBuildSettings.scenes = scenes.ToArray();
            return added;
        }

        // Sun, sky and a preview camera over the middle of the world, in the main scene (the tiles have none of their own).
        static void SetupView(GeoTileSource source, GeoBounds area)
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
            var b = area;
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

        public static bool Touches(PathBuild path, GeoBounds b) => GeoTileGeometry.Touches(path, b);
        public static List<PathBuild> ClipPath(PathBuild path, GeoBounds box, float pathStep) => GeoTileGeometry.ClipPath(path, box, pathStep);
        public static Mesh Clip(Mesh mesh, GeoBounds b) => GeoTileGeometry.Clip(mesh, b);

        public static float Distance(GeoTiledWorld.Tile tile, Vector3 point)
        {
            float dx = Mathf.Max(tile.area.xMin - point.x, 0, point.x - tile.area.xMax), dz = Mathf.Max(tile.area.yMin - point.z, 0, point.z - tile.area.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
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
            float before = world.settings.loadRadius;
            DrawDefaultInspector();
            EditorGUILayout.Space(6);
            if (world.separateScenes)
            {
                GUILayout.Label("Streaming", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();
                float radius = GeoTileStreamerEditor.RadiusSlider(world.settings.loadRadius, world.tileSize, world);
                // the slider here, or the settings' own field above
                if (EditorGUI.EndChangeCheck() || world.settings.loadRadius != before)
                {
                    if (world.settings.loadRadius != before) radius = world.settings.loadRadius;
                    Undo.RecordObject(world, "Load radius");
                    world.settings.loadRadius = radius;
                    var streamer = GeoTiles.EnsureStreamer(world);
                    Undo.RecordObject(streamer, "Load radius");
                    streamer.SetRadius(radius);
                    EditorUtility.SetDirty(world); EditorUtility.SetDirty(streamer);
                }
            }
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
                    if (world.addressableScenes)
                    {
                        if (GUILayout.Button(new GUIContent("Share common assets", "Puts the assets two or more tiles use into one Addressables group, so the build has one copy of them instead of one in every tile's bundle.")))
                            Debug.Log(GeoTileAddressables.ShareDependencies(world) + " assets added to the Addressables group \"" + GeoTileAddressables.SharedGroupName(world) + "\".");
                    }
                    else if (GUILayout.Button("Add scenes to Build Settings")) Debug.Log(GeoTiles.AddToBuildSettings(world) + " scenes added to the Build Settings.");
                }
            }
            GUILayout.Label("Rebuild", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild changed tiles", GUILayout.Height(30))) Run(world, true);
                if (GUILayout.Button("Rebuild all tiles", GUILayout.Height(30))) Run(world, false);
            }
            EditorGUILayout.HelpBox("Changed tiles: those whose export files or settings changed since they were built. Select a tile's world object to rebuild it alone.", MessageType.None);
            if (world.separateScenes)
            {
                GUILayout.Label("Overview", EditorStyles.boldLabel);
                var overview = world.GetComponent<GeoOverview>();
                EditorGUILayout.HelpBox(overview && overview.data ? Describe(overview.data)
                    : "No overview: from high above only the tiles around the camera are seen, and the world ends at its edge.", MessageType.None);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Bake overview", "The whole world in one coarse satellite layer under the tiles (from the tiles' imagery; no tile is rebuilt) and the far ring around it " +
                        "(Settings > Overview: Far Scale). The far ring is kept while its area, size and tile server are the same. Rebuilding tiles bakes it again."), GUILayout.Height(24)))
                        Debug.Log("Geo World: " + GeoTiles.BakeOverview(world).Replace("\n", " "), world);
                    if (GUILayout.Button(new GUIContent("Bake far ring again", "Makes the far ring's imagery anew (from the tile cache in Library, or the server for tiles not in it)."), GUILayout.Height(24)))
                        Debug.Log("Geo World: " + GeoTiles.BakeOverview(world, true).Replace("\n", " "), world);
                }
            }
            GUILayout.Label("Land cover", EditorStyles.boldLabel);
            var cover = world.GetComponent<GeoLandCover>();
            EditorGUILayout.HelpBox(cover && cover.tiles.Count > 0 ? cover.report
                : "No land cover: a game cannot ask what the ground is at a point (GeoLandCover.TrySample: forest, orchard, cropland, grass, built-up ground, water...).", MessageType.None);
            if (GUILayout.Button(new GUIContent("Bake land cover", "What the ground is on a grid of 10 m, from the exports' ground maps (ESA WorldCover with OpenStreetMap's land use, water, roads " +
                "and buildings over it), for the game to ask anywhere (GeoLandCover on this object). No tile is rebuilt; generating tiles bakes it again."), GUILayout.Height(24)))
                Debug.Log("Geo World: " + GeoTiles.BakeLandCover(world).Replace("\n", " "), world);
        }

        static string Describe(GeoOverviewData data)
        {
            string near = data.near ? $"World: {data.near.width} x {data.near.height} ({data.area.width / data.near.width:F1} m a texel)." : "World: none.";
            string far = data.HasFar ? $" Far ring: {data.farArea.width / 1000:F0} x {data.farArea.height / 1000:F0} km, {data.far.width} x {data.far.height} ({data.farArea.width / data.far.width:F0} m a texel)." : " No far ring.";
            return near + far;
        }

        static void Run(GeoTiledWorld world, bool skipUnchanged)
        {
            try
            {
                if (world.separateScenes && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                GeoTiles.Generate(world.parts != null && world.parts.Count > 0 ? world.parts : new List<GeoTiledWorld.Part> { new GeoTiledWorld.Part { environmentPath = world.environmentPath, buildingsPath = world.buildingsPath } },
                    world.settings, world.tileSize, world.folder, world.separateScenes, skipUnchanged);
            }
            catch (OperationCanceledException e) { Debug.Log(e.Message); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
