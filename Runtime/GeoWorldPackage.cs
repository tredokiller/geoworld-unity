using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using GeoWorld.Editor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    // A generated world on disk, made in a player or in the editor (GeoWorldGenerator) and loaded tile by tile without the editor:
    //   <folder>/world.json          the manifest (GeoWorldManifest): format version, origin, bounds, tiles, terrain statistics, hashes
    //   <folder>/tiles/tile_x_z.gwt  one file a tile (GeoWorldPackageFormat)
    // Coordinates: metres east (x) and north (z) of the origin on the tangent plane (local ENU on WGS84), y up; Unity world space
    // with the package's root at the world's origin. A package is self-contained apart from the library it refers to
    // (GeoRuntimeAssets), so the same package can be handed to every client of a session; contentHash says whether two are the same.
    public sealed class GeoWorldPackage
    {
        public const string ManifestFile = "world.json";
        public const string Format = "geoworld-package";
        public const int FormatVersion = 1;

        public string Folder { get; }
        public GeoWorldManifest Manifest { get; }
        public IReadOnlyList<GeoWorldManifest.Tile> Tiles => Manifest.tiles;

        GeoWorldPackage(string folder, GeoWorldManifest manifest) { Folder = folder; Manifest = manifest; }

        public static GeoWorldPackage Load(string folder)
        {
            string path = Path.Combine(folder, ManifestFile);
            if (!File.Exists(path)) throw new FileNotFoundException("No Geo World package here (world.json is missing).", path);
            var manifest = JsonUtility.FromJson<GeoWorldManifest>(File.ReadAllText(path));
            if (manifest == null || manifest.format != Format) throw new InvalidDataException(path + " is not a Geo World package.");
            if (manifest.formatVersion > FormatVersion) throw new InvalidDataException($"The package is format {manifest.formatVersion}; this Geo World reads up to {FormatVersion}.");
            return new GeoWorldPackage(Path.GetFullPath(folder), manifest);
        }

        public static bool TryLoad(string folder, out GeoWorldPackage package)
        {
            try { package = Load(folder); return true; }
            catch (Exception) { package = null; return false; }
        }

        public GeoWorldManifest.Tile Tile(Vector2Int index)
        {
            foreach (var t in Manifest.tiles) if (t.x == index.x && t.z == index.y) return t;
            return null;
        }

        // The tile under a world position (x, z), or null.
        public GeoWorldManifest.Tile TileAt(Vector3 position)
        {
            foreach (var t in Manifest.tiles) if (position.x >= t.minX && position.x < t.maxX && position.z >= t.minZ && position.z < t.maxZ) return t;
            return null;
        }

        // Tiles within radius metres of a point (x, z), nearest first.
        public List<GeoWorldManifest.Tile> TilesAround(Vector3 position, float radius)
        {
            var result = new List<(float, GeoWorldManifest.Tile)>();
            foreach (var t in Manifest.tiles)
            {
                float dx = Mathf.Max(t.minX - position.x, 0, position.x - t.maxX), dz = Mathf.Max(t.minZ - position.z, 0, position.z - t.maxZ);
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d <= radius) result.Add((d, t));
            }
            result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return result.ConvertAll(p => p.Item2);
        }

        // Latitude/longitude of a world position, and back (the export's projection).
        public void ToGeo(Vector3 position, out double lat, out double lon) => new GeoEnu(Manifest.origin.lat, Manifest.origin.lon).Inverse(position.x, position.z, out lat, out lon);
        public Vector3 ToWorld(double lat, double lon, float y = 0)
        {
            new GeoEnu(Manifest.origin.lat, Manifest.origin.lon).Forward(lat, lon, out double east, out double north);
            return new Vector3((float)east, y, (float)north);
        }

        // Loads a tile under parent (null: the scene root), spread over frames (options.frameBudgetMs of the main thread a frame):
        // the file is read and unpacked on a worker thread, the objects made on the main thread. The handle unloads it again.
        public async UniTask<GeoWorldTileHandle> LoadTileAsync(Vector2Int index, Transform parent = null, GeoTileLoadOptions options = null, CancellationToken cancellation = default)
        {
            options ??= new GeoTileLoadOptions();
            var entry = Tile(index) ?? throw new ArgumentException($"The package has no tile {index.x}, {index.y}.");
            string path = Path.Combine(Folder, entry.file);
            var data = await UniTask.RunOnThreadPool(() => GeoWorldPackageFormat.Read(File.ReadAllBytes(path)), cancellationToken: cancellation);
            await UniTask.SwitchToMainThread(cancellation);
            var created = new List<Object>();
            var builder = new GeoWorldPackageFormat.Builder(data, parent, created, options);
            var clock = Stopwatch.StartNew();
            var step = new Stopwatch();
            double longest = 0; string longestStep = null;
            try
            {
                while (true)
                {
                    step.Restart();
                    bool more = builder.Step();
                    if (step.Elapsed.TotalMilliseconds > longest) { longest = step.Elapsed.TotalMilliseconds; longestStep = builder.Last; }
                    if (!more) break;
                    if (clock.Elapsed.TotalMilliseconds < options.frameBudgetMs) continue;
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellation);
                    clock.Restart();
                }
                // the buildings' colliders: meshes here, cooked for the physics on a worker thread (a town tile's outlines took a
                // couple of seconds of one frame), then given to their colliders
                List<(string name, Mesh mesh)> colliders = null;
                if (options.buildingColliders && data.navigation != null)
                {
                    colliders = GeoNavigation.BuildingMeshes(data.navigation, created);
                    var ids = colliders.ConvertAll(c => c.mesh.GetInstanceID());
                    await UniTask.RunOnThreadPool(() => { foreach (int id in ids) Physics.BakeMesh(id, false); }, cancellationToken: cancellation);
                }
                step.Restart();
                var handle = Finish(index, data, builder, created, options, colliders);
                if (step.Elapsed.TotalMilliseconds > longest) { longest = step.Elapsed.TotalMilliseconds; longestStep = "switching the tile on"; }
                handle.LongestStepMs = longest; handle.LongestStep = longestStep;
                return handle;
            }
            catch
            {
                if (builder.Root) Object.Destroy(builder.Root);
                foreach (var o in created) if (o) Object.Destroy(o);
                throw;
            }
        }

        // The same at once, in one frame (tools, tests).
        public GeoWorldTileHandle LoadTile(Vector2Int index, Transform parent = null, GeoTileLoadOptions options = null)
        {
            options ??= new GeoTileLoadOptions();
            var entry = Tile(index) ?? throw new ArgumentException($"The package has no tile {index.x}, {index.y}.");
            var data = GeoWorldPackageFormat.Read(File.ReadAllBytes(Path.Combine(Folder, entry.file)));
            var created = new List<Object>();
            var builder = new GeoWorldPackageFormat.Builder(data, parent, created, options);
            try
            {
                while (builder.Step()) { }
                return Finish(index, data, builder, created, options,
                    options.buildingColliders && data.navigation != null ? GeoNavigation.BuildingMeshes(data.navigation, created) : null);
            }
            catch
            {
                if (builder.Root) Object.DestroyImmediate(builder.Root);
                foreach (var o in created) if (o) Object.DestroyImmediate(o);
                throw;
            }
        }

        static GeoWorldTileHandle Finish(Vector2Int index, GeoWorldPackageFormat.TileData data, GeoWorldPackageFormat.Builder builder, List<Object> created,
            GeoTileLoadOptions options, List<(string name, Mesh mesh)> colliders)
        {
            var root = builder.Root;
            if (options.navigationTags) GeoNavigation.Tag(root.transform);
            if (colliders != null) GeoNavigation.AddBuildingColliders(root.transform, colliders, options.layer);
            builder.Activate();
            Terrain.SetConnectivityDirty();
            var handle = new GeoWorldTileHandle(index, root, data.header, data.navigation, created, builder.missing);
            GeoLoadedTiles.Add(handle);
            if (builder.missing.Count > 0)
                UnityEngine.Debug.LogWarning($"Geo World: tile {index.x}, {index.y} refers to {builder.missing.Count} assets that are not in GeoRuntimeAssets " +
                    "(prepare the runtime assets again with the package version that made the world): " + string.Join(", ", builder.missing));
            return handle;
        }

        // Makes the shaders tiles are drawn with ready (a material of each of GeoRuntimeAssets' templates): the first tile otherwise
        // takes the time in the frame its first material is made (the terrain shader: most of a second). Call it where a pause
        // does not show (a loading screen). Returns the milliseconds it took.
        public static double WarmUp()
        {
            var clock = Stopwatch.StartNew();
            var registry = GeoAssets.Registry;
            if (registry)
                foreach (var template in registry.templates)
                    if (template) Object.Destroy(new Material(template));
            return clock.Elapsed.TotalMilliseconds;
        }

        // Checks every file against the manifest's SHA-256; problems lists what differs.
        public bool Verify(out List<string> problems)
        {
            problems = new List<string>();
            foreach (var t in Manifest.tiles)
            {
                string path = Path.Combine(Folder, t.file);
                if (!File.Exists(path)) { problems.Add("missing " + t.file); continue; }
                if (!string.Equals(Sha256(File.ReadAllBytes(path)), t.sha256, StringComparison.OrdinalIgnoreCase)) problems.Add("changed " + t.file);
            }
            if (!string.Equals(Manifest.ComputeContentHash(), Manifest.contentHash, StringComparison.OrdinalIgnoreCase)) problems.Add("the manifest's content hash does not match");
            return problems.Count == 0;
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

        static string Hex(byte[] hash)
        {
            var b = new StringBuilder(hash.Length * 2);
            foreach (var x in hash) b.Append(x.ToString("x2"));
            return b.ToString();
        }
    }

    public sealed class GeoTileLoadOptions
    {
        [Tooltip("Terrain, bridge and prop colliders.")]
        public bool colliders = true;
        [Tooltip("Mesh colliders of the buildings' footprints (extruded to their height), one per 200 m cell, marked with GeoNavigation (Building): the drawn buildings themselves have no colliders.")]
        public bool buildingColliders = true;
        [Tooltip("GeoNavigation markers on the terrain (ground), water and bridges, for a game that builds its NavMesh from the tile.")]
        public bool navigationTags = true;
        [Tooltip("Layer for every object of the tile; -1 keeps the generator's.")]
        public int layer = -1;
        [Tooltip("Milliseconds of the main thread a frame that LoadTileAsync spends making the tile's objects.")]
        public float frameBudgetMs = 4;
    }

    // A loaded tile: its objects under Root (the tile's GeoWorldRoot), its navigation data (buildings with their footprints), and
    // everything made for it, which Unload destroys.
    public sealed class GeoWorldTileHandle : IDisposable
    {
        readonly List<Object> created;
        public Vector2Int Index { get; }
        public GameObject Root { get; private set; }
        public GeoTileHeader Header { get; }
        public GeoTileNavigation Navigation { get; }
        public IReadOnlyList<string> Missing { get; }
        public bool Loaded => Root;
        // the longest piece of LoadTileAsync's work on the main thread (one frame's worth), and what it was
        public double LongestStepMs { get; internal set; }
        public string LongestStep { get; internal set; }
        public Terrain Terrain => Root ? Root.GetComponentInChildren<Terrain>(true) : null;

        internal GeoWorldTileHandle(Vector2Int index, GameObject root, GeoTileHeader header, GeoTileNavigation navigation, List<Object> created, List<string> missing)
        {
            Index = index; Root = root; Header = header; Navigation = navigation; this.created = created; Missing = missing;
        }

        public void Unload()
        {
            GeoLoadedTiles.Remove(this);
            if (Root) { if (Application.isPlaying) Object.Destroy(Root); else Object.DestroyImmediate(Root); }
            Root = null;
            foreach (var o in created) if (o) { if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o); }
            created.Clear();
            Terrain.SetConnectivityDirty();
        }

        public void Dispose() => Unload();
    }

    // The tiles of world packages loaded now (by LoadTile, LoadTileAsync or a game's own streaming through them), with the ground each
    // covers in the scene (its area, moved with the tile's root): what the background around packages cuts itself away under.
    public static class GeoLoadedTiles
    {
        static readonly Dictionary<GeoWorldTileHandle, Rect> tiles = new Dictionary<GeoWorldTileHandle, Rect>();

        // Changes whenever a tile comes or goes.
        public static int Version { get; private set; }
        public static IEnumerable<Rect> Areas => tiles.Values;
        public static int Count => tiles.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() { tiles.Clear(); Version++; }

        internal static void Add(GeoWorldTileHandle handle)
        {
            if (handle?.Header == null || !handle.Root) return;
            var at = handle.Root.transform.position;
            var h = handle.Header;
            tiles[handle] = Rect.MinMaxRect(h.minX + at.x, h.minZ + at.z, h.maxX + at.x, h.maxZ + at.z);
            Version++;
        }

        internal static void Remove(GeoWorldTileHandle handle)
        {
            if (handle != null && tiles.Remove(handle)) Version++;
        }

        // Is the point (scene x, z) on a loaded tile?
        public static bool Covers(float x, float z)
        {
            foreach (var r in tiles.Values) if (x >= r.xMin && x < r.xMax && z >= r.yMin && z < r.yMax) return true;
            return false;
        }
    }

    // world.json
    [Serializable]
    public sealed class GeoWorldManifest
    {
        public string format = GeoWorldPackage.Format;
        public int formatVersion = GeoWorldPackage.FormatVersion;
        public string name;
        [Tooltip("Geo World package and version that made it, and its tile version (what a tile is made of).")]
        public string generator, tileVersion, unityVersion;
        public Origin origin = new Origin();
        public Area request = new Area();
        public Bounds2 bounds = new Bounds2();
        public float tileSize;
        public int firstTileX, firstTileZ, columns, rows;
        [Tooltip("Lowest point of the terrains and the height range they share (metres).")]
        public float baseY, heightRange;
        public Tile[] tiles = new Tile[0];
        public Stats stats = new Stats();
        public GenerationSettings settings;
        [Tooltip("SHA-256 of what the package was made from: the export's files, the settings, the generator and tile version.")]
        public string inputHash;
        [Tooltip("SHA-256 of the manifest (without this field and created) with every tile's SHA-256 in it: two packages with the same contentHash are the same.")]
        public string contentHash;
        public string[] attribution = new string[0];
        public string[] notes = new string[0];
        [Tooltip("When it was made (UTC, ISO 8601); not part of contentHash.")]
        public string created;

        [Serializable]
        public sealed class Origin
        {
            public double lat, lon;
            public string crs = "local-enu-wgs84";
            [Tooltip("The export's ground height at the origin above sea level (metres): y 0 of the world, where known.")]
            public float ground;
        }

        [Serializable] public sealed class Area { public double south, west, north, east; }
        [Serializable] public sealed class Bounds2 { public float minX, minZ, maxX, maxZ; }

        [Serializable]
        public sealed class Tile
        {
            public int x, z;
            public string file, sha256;
            public long bytes;
            public float minX, minZ, maxX, maxZ;
            public int buildings, roads, trees, props;
            public bool empty;
            public Vector2Int Index => new Vector2Int(x, z);
            public Vector3 Center => new Vector3((minX + maxX) / 2, 0, (minZ + maxZ) / 2);
        }

        [Serializable]
        public sealed class Stats
        {
            public float areaKm2;
            [Tooltip("Ground heights above the world's zero (metres): lowest, highest, their difference, mean slope in degrees.")]
            public float heightMin, heightMax, relief, meanSlope;
            [Tooltip("Shares of the area (0-1) by the export's ground map: forest (tree cover), built-up, water, open ground (grass, crops, bare, the rest).")]
            public float forest, builtUp, water, open;
            public int buildings, damagedBuildings;
            [Tooltip("Share of the area under building footprints (0-1).")]
            public float buildingCover;
            public float roadsKm;
        }

        // The manifest as JSON gives it back (read and written until it no longer changes: numbers written as text may come back with
        // another last bit the first time).
        public static GeoWorldManifest Normalized(GeoWorldManifest manifest)
        {
            string json = JsonUtility.ToJson(manifest);
            for (int i = 0; i < 4; i++)
            {
                var again = JsonUtility.FromJson<GeoWorldManifest>(json);
                string next = JsonUtility.ToJson(again);
                manifest = again;
                if (next == json) break;
                json = next;
            }
            return manifest;
        }

        // The SHA-256 of the manifest without contentHash and created (they say nothing about the content).
        public string ComputeContentHash()
        {
            string hash = contentHash, when = created;
            contentHash = ""; created = "";
            try { return GeoWorldPackage.Sha256(JsonUtility.ToJson(this)); }
            finally { contentHash = hash; created = when; }
        }
    }

    // The JSON header of a tile file.
    [Serializable]
    public sealed class GeoTileHeader
    {
        public int x, z;
        public float minX, minZ, maxX, maxZ, baseY, heightRange;
        public int buildings, roads, rivers, water, trees, props;
        public string report;
    }

    // What a game needs to build its navigation around a tile: every building on it (its centre is on the tile) with its outline,
    // ground and height. The tile's drawn buildings have no colliders (merged chunks and homes); LoadTile makes colliders of
    // these (GeoTileLoadOptions.buildingColliders).
    [Serializable]
    public sealed class GeoTileNavigation
    {
        public Building[] buildings = new Building[0];

        [Serializable]
        public struct Building
        {
            public string id, category;
            public GeoConditionState state;
            [Tooltip("Lowest ground under the outline, and the height above it (metres).")]
            public float baseY, height;
            [Tooltip("Outline in world x, z (counter-clockwise or not; closed implicitly).")]
            public Vector2[] outline;
        }
    }
}
