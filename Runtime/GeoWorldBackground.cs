using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using GeoWorld.Editor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    // How far and how sharp the background around world packages is (GeoWorldBackground).
    [Serializable]
    public class GeoBackgroundSettings
    {
        // One ring of the background: map tiles of a zoom (Web Mercator) out to reach metres beyond the packages.
        [Serializable]
        public struct Level
        {
            [Range(1, 19)] public int zoom;
            [Min(0)] public float reach;
        }

        [Tooltip("From the sharpest to the coarsest: zoom 15 is about 3 m a pixel at 49° N, 13 about 13 m, 11 about 50 m. A coarser ring is left out where a sharper one covers it.")]
        public Level[] levels = { new Level { zoom = 15, reach = 3000 }, new Level { zoom = 13, reach = 20000 }, new Level { zoom = 11, reach = 100000 } };
        [Tooltip("Height grid vertices along a map tile's side.")]
        [Range(2, 33)] public int cellVertices = 9;
        [Tooltip("Metres the background lies below the ground, and how much deeper each coarser ring lies, so that the sharper one is drawn over it.")]
        [Min(0)] public float depthBelowTerrain = 2, depthPerLevel = 6;
        [Tooltip("The ground falls away with the Earth's curvature (785 m at 100 km): the far rings sink below the horizon as they do.")]
        public bool earthCurvature = true;
        [Tooltip("XYZ tile URL of the imagery; empty: the first package's satellite imagery (its settings), else Esri World Imagery.")]
        public string imagery = "";
        [Tooltip("XYZ tile URL of elevation in Terrarium encoding (AWS Terrain Tiles, a public dataset); empty: a flat background at the ground of the scene's origin.")]
        public string elevation = Terrarium;
        [Tooltip("The elevation tiles are this many zooms coarser than the imagery (zoom 15 imagery, zoom 12 elevation: about 25 m a sample).")]
        [Range(0, 8)] public int elevationZoomOffset = 3;
        [Tooltip("Stretch the far clip of the cameras that see the background to its farthest corner while they render (at most Max Far Clip).")]
        public bool extendFarClip = true;
        [Min(1000)] public float maxFarClip = 150000;
        [Tooltip("At most this many map tiles in all the rings (the farthest are left out above it).")]
        [Min(1)] public int maxCells = 2500;
        [Tooltip("Milliseconds of the main thread a frame that ShowAsync spends making the background.")]
        [Min(0.5f)] public float frameBudgetMs = 4;

        public const string Terrarium = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";
    }

    public struct GeoBackgroundProgress
    {
        public string stage;
        public float value; // 0-1
        public int done, total;
        public override string ToString() => $"{value:P0} {stage} {done}/{total}";
    }

    // What the background of an area is made of: the map tiles of each ring (zoom, x, y) and the elevation tiles under them.
    // The same tiles for the same place whichever package asks: a package next to another shares most of them.
    public sealed class GeoBackgroundPlan
    {
        public readonly GeoBackgroundSettings settings;
        public readonly List<GeoTileCache.Tile>[] levels;
        public readonly List<GeoTileCache.Tile>[] elevation;
        public double south, west, north, east; // the packages' area (degrees)
        public int Cells => levels.Sum(l => l.Count);

        GeoBackgroundPlan(GeoBackgroundSettings settings, int count)
        {
            this.settings = settings;
            levels = new List<GeoTileCache.Tile>[count];
            elevation = new List<GeoTileCache.Tile>[count];
        }

        // The rings around an area (degrees): each the map tiles of its zoom that reach its distance beyond the area; at most
        // settings.maxCells in all (the coarsest ring's farthest tiles go first).
        public static GeoBackgroundPlan For(double south, double west, double north, double east, GeoBackgroundSettings settings)
        {
            var levels = (settings.levels ?? new GeoBackgroundSettings.Level[0]).Where(l => l.zoom > 0).ToArray();
            var plan = new GeoBackgroundPlan(settings, levels.Length) { south = south, west = west, north = north, east = east };
            double middle = (south + north) / 2, centreX = 0, centreY = 0;
            for (int i = 0; i < levels.Length; i++)
            {
                var l = levels[i];
                double dLat = l.reach / 111320.0, dLon = l.reach / (111320.0 * Math.Cos(middle * Math.PI / 180));
                Range(south - dLat, west - dLon, north + dLat, east + dLon, l.zoom, out int x0, out int y0, out int x1, out int y1);
                Tile((south + north) / 2, (west + east) / 2, l.zoom, out centreX, out centreY);
                var cells = new List<GeoTileCache.Tile>();
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) cells.Add(new GeoTileCache.Tile(l.zoom, x, y));
                // nearest first: a cap leaves the farthest out, and they are made last
                double cx = centreX, cy = centreY;
                cells.Sort((a, b) => Distance(a, cx, cy).CompareTo(Distance(b, cx, cy)));
                plan.levels[i] = cells;
                int demZoom = Math.Max(1, l.zoom - settings.elevationZoomOffset), shift = l.zoom - demZoom;
                plan.elevation[i] = cells.Select(c => new GeoTileCache.Tile(demZoom, c.x >> shift, c.y >> shift)).Distinct().ToList();
            }
            int total = plan.Cells;
            for (int i = levels.Length - 1; i >= 0 && total > settings.maxCells; i--)
            {
                int keep = Math.Max(0, plan.levels[i].Count - (total - settings.maxCells));
                total -= plan.levels[i].Count - keep;
                plan.levels[i].RemoveRange(keep, plan.levels[i].Count - keep);
            }
            return plan;

            double Distance(GeoTileCache.Tile t, double x, double y) { double dx = t.x + 0.5 - x, dy = t.y + 0.5 - y; return dx * dx + dy * dy; }
        }

        // The packages' area: their requested areas (degrees), together.
        public static GeoBackgroundPlan For(IEnumerable<GeoWorldManifest> manifests, GeoBackgroundSettings settings)
        {
            double s = 90, w = 180, n = -90, e = -180;
            foreach (var m in manifests)
            {
                var a = Area(m);
                s = Math.Min(s, a.south); w = Math.Min(w, a.west); n = Math.Max(n, a.north); e = Math.Max(e, a.east);
            }
            if (s >= n || w >= e) throw new ArgumentException("No package area for the background.");
            return For(s, w, n, e, settings);
        }

        // A package's area in degrees (its request, else its bounds around its origin).
        public static GeoWorldManifest.Area Area(GeoWorldManifest m)
        {
            var r = m.request;
            if (r != null && r.south < r.north && r.west < r.east) return r;
            var enu = new GeoEnu(m.origin.lat, m.origin.lon);
            var area = new GeoWorldManifest.Area { south = 90, west = 180, north = -90, east = -180 };
            foreach (var (x, z) in new[] { (m.bounds.minX, m.bounds.minZ), (m.bounds.maxX, m.bounds.minZ), (m.bounds.minX, m.bounds.maxZ), (m.bounds.maxX, m.bounds.maxZ) })
            {
                enu.Inverse(x, z, out double lat, out double lon);
                area.south = Math.Min(area.south, lat); area.north = Math.Max(area.north, lat); area.west = Math.Min(area.west, lon); area.east = Math.Max(area.east, lon);
            }
            return area;
        }

        // ---- Web Mercator

        public static void Tile(double lat, double lon, int zoom, out double x, out double y)
        {
            double n = 1 << zoom, s = Math.Sin(Math.Max(-85.05112878, Math.Min(85.05112878, lat)) * Math.PI / 180);
            x = (lon + 180) / 360 * n;
            y = (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * n;
        }

        public static double Lon(double x, int zoom) => x / (1 << zoom) * 360 - 180;
        public static double Lat(double y, int zoom) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / (1 << zoom)))) * 180 / Math.PI;

        public static void Range(double south, double west, double north, double east, int zoom, out int x0, out int y0, out int x1, out int y1)
        {
            Tile(north, west, zoom, out double ax, out double ay);
            Tile(south, east, zoom, out double bx, out double by);
            int max = (1 << zoom) - 1;
            x0 = (int)Math.Floor(ax); x1 = (int)Math.Floor(bx);
            y0 = Math.Max(0, (int)Math.Floor(ay)); y1 = Math.Min(max, (int)Math.Floor(by));
        }
    }

    // The ground around world packages, out to the horizon: map tiles in rings of falling detail (by default zoom 15 to 3 km
    // beyond the packages, 13 to 20 km, 11 to 100 km) on the elevation of the ground, a little below the terrain, cut away where a
    // tile of a package is loaded (GeoLoadedTiles). A package carries none of it: the tiles come from a cache on disk that every
    // package and every generation shares (GeoTileCache), so a zone generated next to another downloads only what the first did
    // not, and one background is drawn for all the packages of a scene (ShowAsync with all of them): no tile twice.
    // Every computer makes its own from the network and its cache (a game's clients too): nothing of it is in a package's
    // contentHash. GeoWorldGenerator downloads a new package's tiles before it reports the package done (PrefetchAsync).
    //
    // Coordinates: the scene's origin is the first package's (its manifest's origin, y 0 at its ground); a package whose origin is
    // another is expected at PackageOffset(package) in the scene.
    [DisallowMultipleComponent]
    public sealed class GeoWorldBackground : MonoBehaviour
    {
        public GeoBackgroundSettings settings = new GeoBackgroundSettings();
        public Color tint = Color.white;

        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap"), TintId = Shader.PropertyToID("_Tint"),
            LoadedTilesId = Shader.PropertyToID("_LoadedTiles"), TileGridId = Shader.PropertyToID("_TileGrid"), TileGridSizeId = Shader.PropertyToID("_TileGridSize");
        const float MaskInterval = 0.1f, EarthRadius = 6371000;

        readonly List<Object> owned = new List<Object>();
        readonly Dictionary<Camera, float> restoreFar = new Dictionary<Camera, float>();
        GeoEnu enu;
        double originLat, originLon;
        float originGround;
        Texture2D mask;
        byte[] maskPixels;
        Vector2 gridOrigin; float gridSize = 1000; Vector2Int gridFirst; int gridColumns, gridRows;
        int maskVersion = -1;
        float nextMask, lowest;
        Rect extent;
        CancellationTokenSource lifetime;

        public bool Shown { get; private set; }
        public int CellCount { get; private set; }
        // The map tiles drawn, by zoom: the same tile is never drawn twice.
        public IReadOnlyCollection<GeoTileCache.Tile> Cells => cells;
        readonly HashSet<GeoTileCache.Tile> cells = new HashSet<GeoTileCache.Tile>();
        public Rect Extent => extent;

        // Where a package whose origin is not the scene's lies in the scene (its origin in the scene's metres).
        public Vector3 PackageOffset(GeoWorldPackage package)
        {
            if (enu == null) return Vector3.zero;
            enu.Forward(package.Manifest.origin.lat, package.Manifest.origin.lon, out double east, out double north);
            return new Vector3((float)east, package.Manifest.origin.ground - originGround, (float)north);
        }

        // Downloads what the background of these packages needs and is not in the cache yet: what a generation does at its end, and a
        // game may do ahead (no objects are made). Returns how many map tiles it has and how many came from the network.
        public static async UniTask<(int tiles, int downloaded)> PrefetchAsync(IEnumerable<GeoWorldManifest> manifests, GeoBackgroundSettings settings = null,
            IProgress<GeoBackgroundProgress> progress = null, CancellationToken cancellation = default)
        {
            settings ??= new GeoBackgroundSettings();
            var list = manifests.ToList();
            var plan = GeoBackgroundPlan.For(list, settings);
            var (images, downloaded) = await Fetch(plan, ImageryUrl(settings, list), settings.elevation, progress, cancellation, false);
            return (images.Sum(level => level.Count(b => b != null)), downloaded);
        }

        // Makes the background of these packages (the one before goes): the missing map tiles are downloaded, then the background is
        // made a few milliseconds a frame. Cancel to stop.
        public UniTask ShowAsync(IReadOnlyList<GeoWorldPackage> packages, IProgress<GeoBackgroundProgress> progress = null, CancellationToken cancellation = default) =>
            Build(packages, progress, cancellation, false);

        // The same at once, without giving frames back (tools, tests).
        public void Show(IReadOnlyList<GeoWorldPackage> packages) => Build(packages, null, default, true).GetAwaiter().GetResult();

        async UniTask Build(IReadOnlyList<GeoWorldPackage> packages, IProgress<GeoBackgroundProgress> progress, CancellationToken cancellation, bool now)
        {
            if (packages == null || packages.Count == 0) throw new ArgumentException("No package for the background.");
            Clear();
            lifetime = now ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(cancellation, this.GetCancellationTokenOnDestroy());
            var token = lifetime.Token;
            var first = packages[0].Manifest;
            originLat = first.origin.lat; originLon = first.origin.lon; originGround = first.origin.ground;
            enu = new GeoEnu(originLat, originLon);
            var plan = GeoBackgroundPlan.For(packages.Select(p => p.Manifest), settings);
            var (images, _) = await Fetch(plan, ImageryUrl(settings, packages.Select(p => p.Manifest)), settings.elevation, progress, token, now);
            var elevation = await Elevation(plan, token, now);
            if (Mathf.Approximately(originGround, 0) && elevation.Count > 0) originGround = Height(elevation, plan.levels.Length > 0 ? plan.levels[0].FirstOrDefault().z : 15, originLat, originLon) ?? 0;
            Mask(packages);
            var template = Resources.Load<Material>(GeoOverview.Resource);
            if (!template) throw new InvalidOperationException("The package's " + GeoOverview.Resource + " material is missing.");

            // a coarser ring leaves out a tile that sharper drawn tiles cover whole
            var drawn = new HashSet<GeoTileCache.Tile>[plan.levels.Length];
            for (int i = 0; i < plan.levels.Length; i++)
            {
                drawn[i] = new HashSet<GeoTileCache.Tile>();
                for (int k = 0; k < plan.levels[i].Count; k++) if (images[i][k] != null && !Covered(plan.levels[i][k], i, drawn)) drawn[i].Add(plan.levels[i][k]);
            }
            int total = drawn.Sum(d => d.Count), done = 0;
            var clock = Stopwatch.StartNew();
            float low = float.MaxValue; var bounds = new Rect(); bool any = false;
            for (int i = 0; i < plan.levels.Length; i++)
                for (int k = 0; k < plan.levels[i].Count; k++)
                {
                    var tile = plan.levels[i][k];
                    if (!drawn[i].Contains(tile)) continue;
                    token.ThrowIfCancellationRequested();
                    var mesh = CellMesh(tile, settings.depthBelowTerrain + i * settings.depthPerLevel, elevation);
                    var texture = CellTexture(images[i][k], tile);
                    if (texture)
                    {
                        Cell(tile, mesh, MaterialFor(template, texture));
                        cells.Add(tile);
                        low = Mathf.Min(low, mesh.bounds.min.y);
                        var r = Rect.MinMaxRect(mesh.bounds.min.x, mesh.bounds.min.z, mesh.bounds.max.x, mesh.bounds.max.z);
                        bounds = any ? Rect.MinMaxRect(Mathf.Min(bounds.xMin, r.xMin), Mathf.Min(bounds.yMin, r.yMin), Mathf.Max(bounds.xMax, r.xMax), Mathf.Max(bounds.yMax, r.yMax)) : r;
                        any = true;
                    }
                    else Destroy(mesh);
                    done++;
                    if (now || clock.Elapsed.TotalMilliseconds < settings.frameBudgetMs) continue;
                    progress?.Report(new GeoBackgroundProgress { stage = "Making the background", value = 0.5f + 0.5f * done / Mathf.Max(1, total), done = done, total = total });
                    await UniTask.Yield(PlayerLoopTiming.Update, token);
                    clock.Restart();
                }
            lowest = any ? low : 0; extent = bounds; CellCount = cells.Count; Shown = any;
            UpdateMask(true);
            progress?.Report(new GeoBackgroundProgress { stage = "Background done", value = 1, done = done, total = total });
        }

        // Removes the background (its objects, meshes, textures and materials).
        public void Clear()
        {
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);
            foreach (var o in owned) if (o) Destroy(o);
            owned.Clear(); cells.Clear();
            Shown = false; CellCount = 0; mask = null; maskVersion = -1;
        }

        new void Destroy(Object o) { if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o); }

        T Own<T>(T thing) where T : Object { owned.Add(thing); return thing; }

        void OnDestroy() => Clear();

        static string ImageryUrl(GeoBackgroundSettings settings, IEnumerable<GeoWorldManifest> manifests)
        {
            if (!string.IsNullOrWhiteSpace(settings.imagery)) return settings.imagery.Trim();
            string own = manifests.Select(m => m.settings?.satelliteTiles).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            return string.IsNullOrWhiteSpace(own) ? "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}?blankTile=false" : own.Trim();
        }

        // The map tiles of every ring and the elevation tiles under them, from the cache or the network (progress from the main thread).
        static async UniTask<(List<byte[]>[] images, int downloaded)> Fetch(GeoBackgroundPlan plan, string imagery, string elevation, IProgress<GeoBackgroundProgress> progress,
            CancellationToken cancellation, bool now)
        {
            var all = plan.levels.SelectMany(l => l).ToList();
            var dem = string.IsNullOrWhiteSpace(elevation) ? new List<GeoTileCache.Tile>() : plan.elevation.SelectMany(l => l).Distinct().ToList();
            int done = 0, total = all.Count + dem.Count;
            var task = Task.Run(async () =>
            {
                var a = await GeoTileCache.GetAsync(imagery, all, (d, t) => Interlocked.Increment(ref done), cancellation);
                int fromNetwork = a.fetched;
                if (dem.Count > 0) fromNetwork += (await GeoTileCache.GetAsync(elevation.Trim(), dem, (d, t) => Interlocked.Increment(ref done), cancellation)).fetched;
                return (a.tiles, fromNetwork);
            }, cancellation);
            while (!now && !task.IsCompleted)
            {
                progress?.Report(new GeoBackgroundProgress { stage = "Downloading the background's map tiles", value = 0.5f * Volatile.Read(ref done) / Mathf.Max(1, total), done = Volatile.Read(ref done), total = total });
                await UniTask.Delay(100, DelayType.Realtime, PlayerLoopTiming.Update, cancellation);
            }
            var (tiles, downloaded) = now ? task.GetAwaiter().GetResult() : await task;
            var images = new List<byte[]>[plan.levels.Length];
            for (int i = 0, k = 0; i < plan.levels.Length; i++)
            {
                images[i] = new List<byte[]>(plan.levels[i].Count);
                for (int j = 0; j < plan.levels[i].Count; j++) images[i].Add(tiles[k++]);
            }
            progress?.Report(new GeoBackgroundProgress { stage = "Background's map tiles ready", value = 0.5f, done = total, total = total });
            return (images, downloaded);
        }

        // ---- elevation (Terrarium: (R * 256 + G + B / 256) - 32768 metres)

        sealed class Heights { public float[] values; public int size; }
        readonly Dictionary<GeoTileCache.Tile, Heights> heights = new Dictionary<GeoTileCache.Tile, Heights>();

        async UniTask<Dictionary<GeoTileCache.Tile, Heights>> Elevation(GeoBackgroundPlan plan, CancellationToken cancellation, bool now)
        {
            heights.Clear();
            if (string.IsNullOrWhiteSpace(settings.elevation)) return heights;
            var dem = plan.elevation.SelectMany(l => l).Distinct().ToList();
            var fetch = Task.Run(() => GeoTileCache.GetAsync(settings.elevation.Trim(), dem, null, cancellation), cancellation);
            var (bytes, _) = now ? fetch.GetAwaiter().GetResult() : await fetch;
            var decoder = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                for (int i = 0; i < dem.Count; i++)
                {
                    if (bytes[i] == null || !decoder.LoadImage(bytes[i])) continue;
                    var pixels = decoder.GetPixels32();
                    int size = decoder.width;
                    var values = new float[pixels.Length];
                    // rows from the north, as the tile's y
                    for (int row = 0; row < decoder.height; row++)
                        for (int col = 0; col < size; col++)
                        {
                            var c = pixels[(decoder.height - 1 - row) * size + col];
                            values[row * size + col] = c.r * 256f + c.g + c.b / 256f - 32768f;
                        }
                    heights[dem[i]] = new Heights { values = values, size = size };
                }
            }
            finally { Destroy(decoder); }
            return heights;
        }

        // The ground (metres above sea level) at a place, from the elevation tiles of a ring of this imagery zoom, or null.
        float? Height(Dictionary<GeoTileCache.Tile, Heights> elevation, int imageryZoom, double lat, double lon)
        {
            int z = Math.Max(1, imageryZoom - settings.elevationZoomOffset);
            GeoBackgroundPlan.Tile(lat, lon, z, out double x, out double y);
            var key = new GeoTileCache.Tile(z, (int)Math.Floor(x), (int)Math.Floor(y));
            if (!elevation.TryGetValue(key, out var h)) return null;
            double px = (x - key.x) * h.size - 0.5, py = (y - key.y) * h.size - 0.5;
            px = Math.Max(0, Math.Min(h.size - 1.001, px)); py = Math.Max(0, Math.Min(h.size - 1.001, py));
            int ix = (int)px, iy = (int)py; float fx = (float)(px - ix), fy = (float)(py - iy);
            float a = h.values[iy * h.size + ix], b = h.values[iy * h.size + ix + 1], c = h.values[(iy + 1) * h.size + ix], d = h.values[(iy + 1) * h.size + ix + 1];
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        // ---- the tiles

        // Is a tile of ring `level` covered whole by drawn tiles of sharper rings?
        static bool Covered(GeoTileCache.Tile tile, int level, HashSet<GeoTileCache.Tile>[] drawn)
        {
            if (level == 0) return false;
            var finer = drawn[level - 1];
            if (finer.Count == 0) return Covered(tile, level - 1, drawn);
            int z = finer.First().z, shift = z - tile.z;
            if (shift <= 0) return false;
            int n = 1 << shift;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var child = new GeoTileCache.Tile(z, (tile.x << shift) + x, (tile.y << shift) + y);
                    if (!finer.Contains(child) && !Covered(child, level - 1, drawn)) return false;
                }
            return true;
        }

        Mesh CellMesh(GeoTileCache.Tile tile, float depth, Dictionary<GeoTileCache.Tile, Heights> elevation)
        {
            int n = Mathf.Max(2, settings.cellVertices);
            var vertices = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double tx = tile.x + i / (double)(n - 1), ty = tile.y + j / (double)(n - 1);
                    double lat = GeoBackgroundPlan.Lat(ty, tile.z), lon = GeoBackgroundPlan.Lon(tx, tile.z);
                    enu.Forward(lat, lon, out double east, out double north);
                    float ground = Height(elevation, tile.z, lat, lon) ?? originGround;
                    float y = ground - originGround - depth;
                    if (settings.earthCurvature) y -= (float)((east * east + north * north) / (2 * EarthRadius));
                    vertices[j * n + i] = new Vector3((float)east, y, (float)north);
                    uvs[j * n + i] = new Vector2(i / (float)(n - 1), 1 - j / (float)(n - 1));
                }
            var triangles = new int[(n - 1) * (n - 1) * 6];
            for (int j = 0, t = 0; j < n - 1; j++)
                for (int i = 0; i < n - 1; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                    triangles[t++] = b; triangles[t++] = d; triangles[t++] = c;
                }
            var mesh = new Mesh { name = "Background " + tile.z + "/" + tile.x + "/" + tile.y };
            mesh.vertices = vertices; mesh.uv = uvs; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        Texture2D CellTexture(byte[] bytes, GeoTileCache.Tile tile)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGB24, true)
                { name = "Background " + tile.z + "/" + tile.x + "/" + tile.y, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            if (!texture.LoadImage(bytes)) { Destroy(texture); return null; }
            texture.Compress(false);
            texture.Apply(false, true);
            return texture;
        }

        Material MaterialFor(Material template, Texture2D texture)
        {
            Own(texture);
            var material = Own(new Material(template) { name = "Background " + texture.name });
            material.SetTexture(BaseMapId, texture);
            material.SetColor(TintId, tint);
            material.SetTexture(LoadedTilesId, mask);
            material.SetVector(TileGridId, new Vector4(gridOrigin.x, gridOrigin.y, gridSize, 0));
            material.SetVector(TileGridSizeId, new Vector4(gridFirst.x, gridFirst.y, gridColumns, gridRows));
            return material;
        }

        void Cell(GeoTileCache.Tile tile, Mesh mesh, Material material)
        {
            Own(mesh);
            var go = new GameObject("Background " + tile.z + "/" + tile.x + "/" + tile.y) { layer = gameObject.layer };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowOcclusionWhenDynamic = false;
        }

        // ---- cut away under loaded tiles

        // A mask over the packages' tile grid (the scene's metres): 255 where a tile of a package is loaded.
        void Mask(IReadOnlyList<GeoWorldPackage> packages)
        {
            gridSize = Mathf.Max(1, packages[0].Manifest.tileSize);
            gridOrigin = Vector2.zero;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var p in packages)
            {
                var at = PackageOffset(p); var b = p.Manifest.bounds;
                minX = Mathf.Min(minX, b.minX + at.x); minZ = Mathf.Min(minZ, b.minZ + at.z); maxX = Mathf.Max(maxX, b.maxX + at.x); maxZ = Mathf.Max(maxZ, b.maxZ + at.z);
            }
            gridFirst = new Vector2Int(Mathf.FloorToInt(minX / gridSize), Mathf.FloorToInt(minZ / gridSize));
            gridColumns = Mathf.Clamp(Mathf.CeilToInt(maxX / gridSize) - gridFirst.x, 1, 512);
            gridRows = Mathf.Clamp(Mathf.CeilToInt(maxZ / gridSize) - gridFirst.y, 1, 512);
            mask = Own(new Texture2D(gridColumns, gridRows, TextureFormat.R8, false, true) { name = "Geo World background: cut", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp });
            maskPixels = new byte[gridColumns * gridRows];
            maskVersion = -1;
        }

        void UpdateMask(bool force)
        {
            if (!mask || (!force && maskVersion == GeoLoadedTiles.Version)) return;
            maskVersion = GeoLoadedTiles.Version;
            for (int row = 0, k = 0; row < gridRows; row++)
                for (int column = 0; column < gridColumns; column++, k++)
                {
                    float x = (gridFirst.x + column + 0.5f) * gridSize, z = (gridFirst.y + row + 0.5f) * gridSize;
                    maskPixels[k] = GeoLoadedTiles.Covers(x, z) ? (byte)255 : (byte)0;
                }
            mask.SetPixelData(maskPixels, 0);
            mask.Apply(false);
        }

        // Cuts the background away under the tiles loaded now (it follows them a few times a second by itself; a game that has just
        // loaded or unloaded tiles may call it for the next frame).
        public void RefreshCut() => UpdateMask(false);

        void Update()
        {
            if (!Shown || Time.unscaledTime < nextMask) return;
            nextMask = Time.unscaledTime + MaskInterval;
            UpdateMask(false);
        }

        // ---- far clip

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            RenderPipelineManager.endCameraRendering += EndCamera;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            RenderPipelineManager.endCameraRendering -= EndCamera;
            foreach (var pair in restoreFar) if (pair.Key) pair.Key.farClipPlane = pair.Value;
            restoreFar.Clear();
        }

        void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!Shown || !settings.extendFarClip || camera.cameraType != CameraType.Game || (camera.cullingMask & (1 << gameObject.layer)) == 0) return;
            if (camera.TryGetComponent(out UniversalAdditionalCameraData extra) && extra.renderType == CameraRenderType.Overlay) return;
            // the same in every frame (a far clip that followed the camera made the water's colour flicker: GeoOverview)
            float far = Mathf.Min(GeoOverview.StableFarClip(extent, lowest), settings.maxFarClip);
            if (far <= camera.farClipPlane) return;
            if (!restoreFar.ContainsKey(camera)) restoreFar.Add(camera, camera.farClipPlane);
            camera.farClipPlane = far;
        }

        void EndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!restoreFar.TryGetValue(camera, out float far)) return;
            restoreFar.Remove(camera);
            camera.farClipPlane = far;
        }
    }
}
