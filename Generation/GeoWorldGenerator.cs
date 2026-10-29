using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using GeoWorld.Editor;
using UnityEngine;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    // What to make: an area of the map (degrees, WGS84), its name, and how (null settings: the tiled world defaults).
    [Serializable]
    public sealed class GeoWorldRequest
    {
        public double south, west, north, east;
        public string name = "area";
        public GenerationSettings settings;
        [Tooltip("Side of a tile in metres (the grid all Geo World worlds use).")]
        public float tileSize = 1000;
        [Tooltip("Where the package goes; empty: <home>/packages/<name>.")]
        public string outputFolder;
        [Tooltip("An export made before (a folder with environment.json): no extraction.")]
        public string exportFolder;
        [Tooltip("The app's radar estimate of war damage (made once per area, a minute or two more).")]
        public bool radar = true;
        [Tooltip("Extract again even when the same area was extracted before under this name.")]
        public bool freshExtraction;
        [Tooltip("Download the map tiles of the background around the package (GeoWorldBackground) into the cache all packages share, before the package is reported done: a zone next to an earlier one downloads only what that one did not. Not part of the package.")]
        public bool background = true;
        [Tooltip("The background's rings (null: GeoBackgroundSettings' defaults); the same settings for GeoWorldBackground.ShowAsync.")]
        public GeoBackgroundSettings backgroundSettings;

        public double WidthKm => (east - west) * 111.32 * Math.Cos((south + north) / 2 * Math.PI / 180);
        public double HeightKm => (north - south) * 111.32;
    }

    public struct GeoWorldProgress
    {
        public enum Stage { Extracting, Preparing, Tiles, Writing, Done, Background }
        public Stage stage;
        public float value;     // 0-1 for the whole request
        public string message;
        public int tile, tiles; // the tile being made (1-based) and how many
        public override string ToString() => $"{value:P0} {stage}" + (tiles > 0 ? $" {tile}/{tiles}" : "") + ": " + message;
    }

    // Generation without the editor (a player, or the editor itself): the Building Extractor app downloads the area, then every
    // tile is generated with the same code as the editor's tiled worlds (GeoWorldBuild) and written to a world package
    // (GeoWorldPackage), which loads tile by tile. One generation at a time; it runs on the main thread between frames (a tile
    // at a time) and reads the export on a worker thread. Progress by stage and tile; the cancellation token stops it (the
    // extraction's process too), leaving no package.
    public static class GeoWorldGenerator
    {
        // The package version this code is (a package says which version made it): keep with package.json.
        public const string Version = "1.13.1";
        public const string PackageName = GeoPaths.PackageName;
        public static bool Busy { get; private set; }

        // Where generation keeps its things: exports and the app's cache, satellite tiles, packages. A player's own folder, or the
        // editor's (the same as GeoExtractor's).
        public static string Home
        {
            get => home ??= Application.isEditor
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeoWorld")
                : Path.Combine(Application.persistentDataPath, "GeoWorld");
            set
            {
                home = value;
                // a player's map tiles (satellite imagery, the background) are kept with its other things
                if (!Application.isEditor && !string.IsNullOrEmpty(value)) GeoTileCache.Root = Path.Combine(value, "cache", "satellite");
            }
        }
        static string home;

        public static string PackagesFolder => Path.Combine(Home, "packages");

        // The tiled world defaults (as the editor's GeoDefaults.TileSettings) with the package's home and ruin libraries.
        public static GenerationSettings DefaultSettings()
        {
            var s = new GenerationSettings { heightResolution = 513, textureResolution = 512, roadPaintResolution = 512 };
            FillLibraries(s);
            return s;
        }

        static void FillLibraries(GenerationSettings s)
        {
            if (!s.homeLibrary) s.homeLibrary = GeoAssets.Load<GeoHomeLibrary>(GeoHomeLibrary.DefaultPath) ?? GeoAssets.HomeLibrary;
            if (!s.ruinLibrary) s.ruinLibrary = GeoAssets.Load<GeoRuinLibrary>(GeoRuinLibrary.DefaultPath) ?? GeoAssets.RuinLibrary;
        }

        // One export: up to 100 km² and 20 km a side (the app's limit for an export).
        public const double MaxKm2 = 100, MaxSideKm = 20;

        // Extracts the area (unless request.exportFolder) and makes its package.
        public static async UniTask<GeoWorldPackage> GenerateAsync(GeoWorldRequest request, IProgress<GeoWorldProgress> progress = null, CancellationToken cancellation = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            string export = request.exportFolder;
            const float extractShare = 0.45f;
            var watch = Stopwatch.StartNew();
            double extractSeconds = 0;
            if (string.IsNullOrEmpty(export))
            {
                Validate(request);
                float extracted = 0; string stage = "Extracting";
                var extraction = await GeoExtractorRunner.ExtractAsync(request.south, request.west, request.north, request.east, request.name, Home, request.radar,
                    new GeoProgress<(float progress, string stage)>(p =>
                    {
                        // a message without a share (waiting for a server) keeps the share reached
                        if (p.progress >= 0) extracted = Mathf.Max(extracted, Mathf.Clamp01(p.progress));
                        if (!string.IsNullOrEmpty(p.stage)) stage = p.stage;
                        progress?.Report(new GeoWorldProgress { stage = GeoWorldProgress.Stage.Extracting, value = extracted * extractShare, message = stage });
                    }),
                    cancellation, request.freshExtraction);
                export = extraction.folder;
                extractSeconds = watch.Elapsed.TotalSeconds;
            }
            var package = await BuildPackageAsync(export, request, new GeoProgress<GeoWorldProgress>(p =>
            {
                if (string.IsNullOrEmpty(request.exportFolder)) p.value = extractShare + (1 - extractShare) * p.value;
                progress?.Report(p);
            }), cancellation, extractSeconds);
            return package;
        }

        public static void Validate(GeoWorldRequest r)
        {
            if (!(r.south < r.north) || !(r.west < r.east)) throw new ArgumentException("The area needs south < north and west < east.");
            if (r.south < -85 || r.north > 85 || r.west < -180 || r.east > 180) throw new ArgumentException("The area is off the map.");
            double w = r.WidthKm, h = r.HeightKm;
            if (w * h > MaxKm2 || w > MaxSideKm || h > MaxSideKm)
                throw new ArgumentException($"The area is {w:F1} x {h:F1} km: one generation takes up to {MaxKm2:F0} km² and {MaxSideKm:F0} km a side.");
        }

        // Makes the package of an export (a folder with environment.json and buildings.json) at once, without giving frames back
        // (the editor's menu and batch mode, tests).
        public static GeoWorldPackage BuildPackage(string exportFolder, GeoWorldRequest request, IProgress<GeoWorldProgress> progress = null) =>
            BuildPackageAsync(exportFolder, request, progress, default, 0, true).GetAwaiter().GetResult();

        static UniTask Pause(bool now, CancellationToken cancellation) => now ? UniTask.CompletedTask : UniTask.Yield(cancellation);
        static UniTask<T> Work<T>(bool now, Func<T> work, CancellationToken cancellation) => now ? UniTask.FromResult(work()) : UniTask.RunOnThreadPool(work, cancellationToken: cancellation);

        // Makes the package of an export (a folder with environment.json and buildings.json): a tile a frame or so, the export read
        // and the files written on worker threads.
        public static async UniTask<GeoWorldPackage> BuildPackageAsync(string exportFolder, GeoWorldRequest request, IProgress<GeoWorldProgress> progress = null,
            CancellationToken cancellation = default, double extractSeconds = 0, bool now = false)
        {
            if (Busy) throw new InvalidOperationException("A world is being generated already.");
            // a player started with -nographics has no shaders to give its materials their values: every colour and number of a
            // material made there reads as zero (the editor has them either way)
            if (!Application.isEditor && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("Generation needs a graphics device: start the player without -nographics (-batchmode alone keeps it windowless).");
            Busy = true;
            var watch = Stopwatch.StartNew();
            string output = null, staging = null;
            long peakManaged = 0, peakProcess = 0;
            void Memory()
            {
                peakManaged = Math.Max(peakManaged, Profiler.GetTotalAllocatedMemoryLong() + Profiler.GetMonoUsedSizeLong());
                if (GeoProcessMemory.Read(out long workingSet, out _, out _, out _)) peakProcess = Math.Max(peakProcess, workingSet);
            }
            try
            {
                string envPath = Path.Combine(exportFolder, "environment.json");
                if (!File.Exists(envPath)) throw new FileNotFoundException("The export has no environment.json.", envPath);
                string buildingsPath = Path.Combine(exportFolder, "buildings.json");
                if (!File.Exists(buildingsPath)) buildingsPath = "";
                // as the editor's tiled worlds: the tiled world's settings (GeoTiles), then the generator's (GeoGenerator)
                var settings = GeoWorldBuild.Sanitize(GeoWorldBuild.TileSettings(request.settings ?? DefaultSettings()));
                FillLibraries(settings);
                float tileSize = Mathf.Clamp(request.tileSize, 100, 8000);
                output = string.IsNullOrEmpty(request.outputFolder) ? Path.Combine(PackagesFolder, GeoExtractorRunner.SafeName(request.name)) : request.outputFolder;
                staging = output + ".part";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(Path.Combine(staging, "tiles"));
                if (!Application.isEditor) GeoSatellite.CacheRoot = Path.Combine(Home, "cache", "satellite");

                void Report(GeoWorldProgress.Stage stage, float value, string message, int tile = 0, int tiles = 0) =>
                    progress?.Report(new GeoWorldProgress { stage = stage, value = value, message = message, tile = tile, tiles = tiles });

                Report(GeoWorldProgress.Stage.Preparing, 0, "Reading the export");
                await Pause(now, cancellation);
                // the export's JSON on a worker thread (tens of megabytes for a town)
                var env = await Work(now, () => GeoData.ReadEnvironment(envPath), cancellation);
                var plan = GeoTilePlan.Make(new[] { env.bounds }, tileSize);
                Report(GeoWorldProgress.Stage.Preparing, 0.01f, "Working out roads, rivers and lakes for the whole area");
                await Pause(now, cancellation);
                var target = new GeoMemoryTarget(null, cancellation);
                GeoTileSource source;
                using (GeoGeneration.Use(target))
                    source = GeoTileSource.Prepare(envPath, buildingsPath, settings, tileSize, Vector2.zero, env);
                Memory();
                var indices = plan.tiles.Keys.OrderBy(i => i.y).ThenBy(i => i.x).ToList();
                var tiles = new List<GeoWorldManifest.Tile>();
                var notes = new List<string>(source.notes);
                var stats = new StatsBuilder();
                var tileSeconds = new List<double>();
                var stages = new Dictionary<string, double>();
                for (int k = 0; k < indices.Count; k++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var index = indices[k];
                    var core = GeoTilePlan.Tile(index, tileSize);
                    float from = 0.03f + 0.95f * k / indices.Count, span = 0.95f / indices.Count;
                    string label = $"Tile {k + 1} / {indices.Count}: ";
                    Report(GeoWorldProgress.Stage.Tiles, from, label + "starting", k + 1, indices.Count);
                    await Pause(now, cancellation);
                    var clock = Stopwatch.StartNew();
                    var job = source.Job(index, core, "", "Tile " + index.x + "_" + index.y);
                    var made = new List<Object>();
                    GeoWorldRoot root = null;
                    byte[] bytes;
                    var tileNotes = new List<string>();
                    string stageNow = null; var stageClock = Stopwatch.StartNew();
                    var tileTarget = new GeoMemoryTarget((text, value) =>
                    {
                        string name = StageName(text);
                        if (name != stageNow) { if (stageNow != null) Add(stages, stageNow, stageClock.Elapsed.TotalSeconds); stageNow = name; stageClock.Restart(); }
                        Report(GeoWorldProgress.Stage.Tiles, from + span * Mathf.Clamp01(value), label + text, k + 1, indices.Count);
                    }, cancellation);
                    try
                    {
                        using (GeoGeneration.Use(tileTarget))
                        {
                            GeoWorldBuild.progressPrefix = ""; GeoWorldBuild.progressFrom = 0; GeoWorldBuild.progressSpan = 1;
                            root = GeoWorldBuild.Build(envPath, buildingsPath, settings, null, job, "", o => made.Add(o));
                        }
                        if (stageNow != null) Add(stages, stageNow, stageClock.Elapsed.TotalSeconds);
                        stageClock.Restart();
                        var navigation = Navigation(job, out float footprintArea, out int damaged);
                        var header = new GeoTileHeader
                        {
                            x = index.x, z = index.y, minX = core.minX, minZ = core.minZ, maxX = core.maxX, maxZ = core.maxZ, baseY = job.baseY, heightRange = job.range,
                            buildings = root.buildingCount, roads = root.roadCount, rivers = root.riverCount, water = root.waterCount, trees = root.treeCount, props = root.propCount,
                            report = root.report
                        };
                        bytes = GeoWorldPackageFormat.Write(root.gameObject, header, navigation, tileNotes);
                        Add(stages, "Writing the tile", stageClock.Elapsed.TotalSeconds);
                        stats.Add(job, core, navigation, footprintArea, damaged);
                        tiles.Add(new GeoWorldManifest.Tile
                        {
                            x = index.x, z = index.y, file = "tiles/tile_" + index.x + "_" + index.y + ".gwt", bytes = bytes.Length,
                            minX = core.minX, minZ = core.minZ, maxX = core.maxX, maxZ = core.maxZ,
                            buildings = root.buildingCount, roads = root.roadCount, trees = root.treeCount, props = root.propCount,
                            empty = root.buildingCount == 0 && root.roadCount == 0 && root.riverCount == 0 && root.propCount == 0
                        });
                        Memory();
                    }
                    finally
                    {
                        if (root) Object.DestroyImmediate(root.gameObject);
                        foreach (var o in made) if (o) Object.DestroyImmediate(o);
                        foreach (var o in tileTarget.created) if (o) Object.DestroyImmediate(o);
                    }
                    notes.AddRange(tileNotes);
                    var entry = tiles[tiles.Count - 1];
                    string file = Path.Combine(staging, entry.file);
                    entry.sha256 = await Work(now, () => { File.WriteAllBytes(file, bytes); return GeoWorldPackage.Sha256(bytes); }, cancellation);
                    tileSeconds.Add(clock.Elapsed.TotalSeconds);
                }

                Report(GeoWorldProgress.Stage.Writing, 0.98f, "Writing the manifest");
                var outer = plan.Outer();
                var manifest = new GeoWorldManifest
                {
                    name = request.name,
                    generator = PackageName + " " + Version,
                    tileVersion = GeoTileGeometry.Version,
                    unityVersion = Application.unityVersion,
                    origin = new GeoWorldManifest.Origin { lat = env.origin?.lat ?? 0, lon = env.origin?.lon ?? 0, ground = env.terrain?.heightmap?.originGround ?? 0 },
                    request = Area(request, env),
                    bounds = new GeoWorldManifest.Bounds2 { minX = env.bounds.minX, minZ = env.bounds.minZ, maxX = env.bounds.maxX, maxZ = env.bounds.maxZ },
                    tileSize = tileSize, firstTileX = plan.first.x, firstTileZ = plan.first.y, columns = plan.columns, rows = plan.rows,
                    baseY = source.baseY, heightRange = source.range,
                    tiles = tiles.ToArray(),
                    stats = stats.Finish(env, envPath, outer),
                    settings = Stored(settings),
                    inputHash = InputHash(envPath, buildingsPath, env, settings, tileSize),
                    attribution = Attribution(settings),
                    notes = notes.Distinct().ToArray(),
                };
                // as it will read back from world.json: a double written as text may come back a bit different, and the hash must be
                // that of what a reader has
                manifest = GeoWorldManifest.Normalized(manifest);
                manifest.contentHash = manifest.ComputeContentHash();
                manifest.created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                File.WriteAllText(Path.Combine(staging, GeoWorldPackage.ManifestFile), JsonUtility.ToJson(manifest, true));
                Memory();
                // the background's map tiles into the shared cache (not into the package: its contentHash stays as it is)
                int backgroundTiles = 0, backgroundDownloaded = 0; double backgroundSeconds = 0;
                if (request.background && !now)
                {
                    var backgroundClock = Stopwatch.StartNew();
                    try
                    {
                        (backgroundTiles, backgroundDownloaded) = await GeoWorldBackground.PrefetchAsync(new[] { manifest }, request.backgroundSettings,
                            new GeoProgress<GeoBackgroundProgress>(b => Report(GeoWorldProgress.Stage.Background, 0.98f + 0.019f * Mathf.Clamp01(b.value * 2), $"Background: map tiles {b.done} / {b.total}")),
                            cancellation);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) { Debug.LogWarning("Geo World: the background's map tiles were not downloaded (" + e.Message + "); GeoWorldBackground downloads them when it shows the package."); }
                    backgroundSeconds = backgroundClock.Elapsed.TotalSeconds;
                }
                // how it went (not part of the package's content)
                var report = new GeoWorldGenerationReport
                {
                    extractSeconds = extractSeconds, generateSeconds = watch.Elapsed.TotalSeconds, tiles = tiles.Count,
                    secondsPerTile = tileSeconds.Count > 0 ? tileSeconds.Average() : 0, slowestTileSeconds = tileSeconds.Count > 0 ? tileSeconds.Max() : 0,
                    packageBytes = tiles.Sum(t => t.bytes), peakManagedBytes = peakManaged, peakWorkingSetBytes = peakProcess,
                    processPeakWorkingSetBytes = ProcessPeak(out long peakPrivate), processPeakPrivateBytes = peakPrivate,
                    backgroundTiles = backgroundTiles, backgroundDownloaded = backgroundDownloaded, backgroundSeconds = backgroundSeconds,
                    platform = Application.platform.ToString(), unity = Application.unityVersion,
                    stages = stages.OrderByDescending(p => p.Value).Select(p => $"{p.Key}: {p.Value:F1} s").ToArray()
                };
                File.WriteAllText(Path.Combine(staging, "generation-report.json"), JsonUtility.ToJson(report, true));
                // the finished package replaces an earlier one of the name in one step
                if (Directory.Exists(output)) Directory.Delete(output, true);
                Directory.Move(staging, output);
                staging = null;
                Report(GeoWorldProgress.Stage.Done, 1, $"{tiles.Count} tiles in {watch.Elapsed.TotalSeconds:F0} s");
                LastReport = report;
                return GeoWorldPackage.Load(output);
            }
            finally
            {
                Busy = false;
                if (staging != null && Directory.Exists(staging)) { try { Directory.Delete(staging, true); } catch (IOException) { } }
            }
        }

        public static GeoWorldGenerationReport LastReport { get; private set; }

        // a progress message without its counts ("Downloading satellite tiles (12 / 121, zoom 18)", "Placing buildings 300 / 2000")
        static string StageName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int cut = text.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) text = text.Substring(0, cut);
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s*\d+\s*/\s*\d+$", "");
        }

        static void Add(Dictionary<string, double> stages, string name, double seconds) => stages[name] = (stages.TryGetValue(name, out var s) ? s : 0) + seconds;

        // The area asked for, or for an export made before (no area in the request) its bounds in degrees.
        static GeoWorldManifest.Area Area(GeoWorldRequest r, EnvironmentData env)
        {
            if (r.south < r.north && r.west < r.east || env.origin == null) return new GeoWorldManifest.Area { south = r.south, west = r.west, north = r.north, east = r.east };
            var enu = new GeoEnu(env.origin.lat, env.origin.lon);
            var area = new GeoWorldManifest.Area { south = 90, west = 180, north = -90, east = -180 };
            foreach (var (x, z) in new[] { (env.bounds.minX, env.bounds.minZ), (env.bounds.maxX, env.bounds.minZ), (env.bounds.minX, env.bounds.maxZ), (env.bounds.maxX, env.bounds.maxZ) })
            {
                enu.Inverse(x, z, out double lat, out double lon);
                area.south = Math.Min(area.south, lat); area.north = Math.Max(area.north, lat); area.west = Math.Min(area.west, lon); area.east = Math.Max(area.east, lon);
            }
            return area;
        }

        // the process's peaks since it started (the editor's or the player's, not only this generation's)
        static long ProcessPeak(out long peakPrivate)
        {
            GeoProcessMemory.Read(out _, out long peak, out _, out peakPrivate);
            return peak;
        }

        // The settings as a package keeps them (no references to assets: the libraries are the registry's).
        static GenerationSettings Stored(GenerationSettings s)
        {
            var copy = JsonUtility.FromJson<GenerationSettings>(JsonUtility.ToJson(s));
            copy.homeLibrary = null; copy.ruinLibrary = null; copy.propCatalog = null; copy.treePrefabs = new GameObject[0];
            return copy;
        }

        static string[] Attribution(GenerationSettings s)
        {
            var list = new List<string> { "© OpenStreetMap contributors (ODbL)", "Ground cover: ESA WorldCover 10 m (CC BY 4.0)", "Elevation: Copernicus DEM / SRTM" };
            if (s.satelliteImagery) list.Insert(0, GeoSatellite.Attribution(string.IsNullOrWhiteSpace(s.satelliteTiles) ? GeoSatellite.EsriWorldImagery : s.satelliteTiles));
            return list.ToArray();
        }

        // What the package is made from: the export's files, the settings (libraries by key), the tile size, this code.
        static string InputHash(string envPath, string buildingsPath, EnvironmentData env, GenerationSettings settings, float tileSize)
        {
            var text = new StringBuilder();
            text.Append(PackageName).Append(' ').Append(Version).Append('|').Append(GeoTileGeometry.Version).Append('|').Append(GeoWorldPackageFormat.Version).Append('|');
            foreach (var file in new[] { envPath, buildingsPath, Package(env.terrain?.heightmap?.file), Package(env.materials?.file), Package(env.materials?.canopyFile) })
                if (!string.IsNullOrEmpty(file) && File.Exists(file)) text.Append(Path.GetFileName(file)).Append('=').Append(GeoWorldPackage.Sha256(File.ReadAllBytes(file))).Append('|');
            text.Append(JsonUtility.ToJson(Stored(settings))).Append('|');
            text.Append(GeoAssets.Key(settings.homeLibrary)).Append('|').Append(GeoAssets.Key(settings.ruinLibrary)).Append('|');
            text.Append(tileSize.ToString("R", CultureInfo.InvariantCulture));
            return GeoWorldPackage.Sha256(text.ToString());

            string Package(string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                try { return GeoData.PackageFile(envPath, name); } catch (Exception) { return null; }
            }
        }

        // The buildings whose centre is on the tile, with their outlines in world x, z and the lowest ground under them.
        static GeoTileNavigation Navigation(GeoTileJob job, out float footprintArea, out int damaged)
        {
            var list = new List<GeoTileNavigation.Building>();
            footprintArea = 0; damaged = 0;
            foreach (var b in job.buildings.buildings)
            {
                if (!GeoWorldBuild.ValidBuilding(b) || b.x < job.core.minX || b.x >= job.core.maxX || b.z < job.core.minZ || b.z >= job.core.maxZ) continue;
                var outline = new List<Vector2>();
                if (b.footprint != null && b.footprint.Length >= 6 && b.footprint.Length % 2 == 0)
                    for (int i = 0; i < b.footprint.Length; i += 2) outline.Add(new Vector2(b.x + b.footprint[i], b.z + b.footprint[i + 1]));
                else
                {
                    // the exported box
                    float a = -b.rotation * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
                    foreach (var (u, v) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                    {
                        float x = u * b.width / 2, z = v * b.length / 2;
                        outline.Add(new Vector2(b.x + x * c - z * s, b.z + x * s + z * c));
                    }
                }
                float ground = float.MaxValue;
                foreach (var p in outline) ground = Mathf.Min(ground, job.field.Sample(p.x, p.y));
                var state = b.condition != null ? b.condition.State : GeoConditionState.Unknown;
                if (state >= GeoConditionState.Damaged) damaged++;
                footprintArea += Mathf.Abs(GeoPolygon.Area(outline));
                list.Add(new GeoTileNavigation.Building { id = b.id, category = b.category, state = state, baseY = ground, height = b.height, outline = outline.ToArray() });
            }
            return new GeoTileNavigation { buildings = list.ToArray() };
        }

        // The terrain statistics of the world: relief from the tiles' height fields, cover from the export's ground map.
        sealed class StatsBuilder
        {
            float min = float.MaxValue, max = float.MinValue; double slope; long slopes; int buildings, damaged; double footprints;

            public void Add(GeoTileJob job, GeoBounds core, GeoTileNavigation navigation, float footprintArea, int damagedHere)
            {
                var field = job.Core(job.field);
                int n = field.resolution;
                float cell = core.Width / (n - 1);
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        float h = field.heights[z, x];
                        min = Mathf.Min(min, h); max = Mathf.Max(max, h);
                        if (x % 4 != 0 || z % 4 != 0 || x + 1 >= n || z + 1 >= n) continue;
                        float dx = field.heights[z, x + 1] - h, dz = field.heights[z + 1, x] - h;
                        slope += Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz) / cell) * Mathf.Rad2Deg; slopes++;
                    }
                buildings += navigation.buildings.Length; damaged += damagedHere; footprints += footprintArea;
            }

            public GeoWorldManifest.Stats Finish(EnvironmentData env, string envPath, GeoBounds outer)
            {
                double area = (double)outer.Width * outer.Depth;
                var s = new GeoWorldManifest.Stats
                {
                    areaKm2 = (float)(area / 1e6), heightMin = min, heightMax = max, relief = max - min, meanSlope = slopes > 0 ? (float)(slope / slopes) : 0,
                    buildings = buildings, damagedBuildings = damaged, buildingCover = area > 0 ? (float)(footprints / area) : 0, roadsKm = RoadsKm(env, outer)
                };
                GeoGroundStats.Cover(env, envPath, outer, out s.forest, out s.builtUp, out s.water, out s.open);
                return s;
            }

            static float RoadsKm(EnvironmentData env, GeoBounds b)
            {
                double total = 0;
                foreach (var road in env.roads)
                {
                    if (road == null || road.tunnel) continue;
                    var points = GeoData.Points(road.points);
                    for (int i = 1; i < points.Count; i++)
                        if (b.Contains((points[i - 1].x + points[i].x) / 2, (points[i - 1].z + points[i].z) / 2)) total += Vector3.Distance(points[i - 1], points[i]);
                }
                return (float)(total / 1000);
            }
        }
    }

    // Progress delivered at once, on the thread that reports it (System.Progress posts to the next frame).
    public sealed class GeoProgress<T> : IProgress<T>
    {
        readonly Action<T> report;
        public GeoProgress(Action<T> report) { this.report = report; }
        public void Report(T value) => report?.Invoke(value);
    }

    // How a generation went (generation-report.json next to world.json; not part of the package's content).
    [Serializable]
    public sealed class GeoWorldGenerationReport
    {
        public double extractSeconds, generateSeconds, secondsPerTile, slowestTileSeconds, backgroundSeconds;
        [Tooltip("Map tiles of the background the cache has for the package, and how many of them this generation downloaded.")]
        public int backgroundTiles, backgroundDownloaded;
        public int tiles;
        // peakWorkingSetBytes: the largest working set seen while generating; processPeak*: the process's own peaks since it started
        public long packageBytes, peakManagedBytes, peakWorkingSetBytes, processPeakWorkingSetBytes, processPeakPrivateBytes;
        public string platform, unity;
        [Tooltip("Seconds of all tiles together by step of the generator, the longest first.")]
        public string[] stages;
    }

    // Shares of the export's ground map (materials.png: ESA WorldCover with OpenStreetMap water burned in) by kind.
    public static class GeoGroundStats
    {
        public static void Cover(EnvironmentData env, string envPath, GeoBounds area, out float forest, out float builtUp, out float water, out float open)
        {
            forest = builtUp = water = 0; open = 1;
            var info = env?.materials;
            if (info == null || string.IsNullOrEmpty(info.file) || info.width < 1 || info.height < 1 || info.cellSize <= 0) return;
            string path;
            try { path = GeoData.PackageFile(envPath, info.file); } catch (Exception) { return; }
            var kinds = new int[256]; // 0 open, 1 forest, 2 built-up, 3 water
            foreach (var c in info.classes ?? new List<MaterialClass>())
            {
                if (c.id < 0 || c.id > 255) continue;
                string name = (c.name ?? "").ToLowerInvariant();
                kinds[c.id] = name.Contains("water") ? 3 : name.Contains("tree") || name.Contains("forest") || name.Contains("wood") || name.Contains("mangrove") ? 1
                    : name.Contains("built") || name.Contains("urban") || name.Contains("building") ? 2 : 0;
            }
            var map = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!map.LoadImage(File.ReadAllBytes(path)) || map.width != info.width || map.height != info.height) return;
                var pixels = map.GetPixels32();
                var counts = new long[4]; long total = 0;
                for (int row = 0; row < info.height; row++)
                {
                    float z = info.maxZ - (row + 0.5f) * info.cellSize;
                    if (z < area.minZ || z > area.maxZ) continue;
                    for (int col = 0; col < info.width; col++)
                    {
                        float x = info.minX + (col + 0.5f) * info.cellSize;
                        if (x < area.minX || x > area.maxX) continue;
                        counts[kinds[pixels[(info.height - 1 - row) * info.width + col].r]]++; total++;
                    }
                }
                if (total == 0) return;
                open = counts[0] / (float)total; forest = counts[1] / (float)total; builtUp = counts[2] / (float)total; water = counts[3] / (float)total;
            }
            finally { Object.DestroyImmediate(map); }
        }
    }
}
