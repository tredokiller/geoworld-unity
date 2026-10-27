using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Rendering;
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
        public List<PathBuild> rivers;  // rivers cut to RiverReach beyond the margin, for the water (GeoWater): the line of a wide river's area may lie beyond the margin
        public float[] waterLevels;     // for env.water, from the whole lake
        public GeoBounds core;
        public Vector2Int index;
        public float size, baseY, range;
        public string folder, name;
        public int part;                // the export this comes from, in a tile that several exports build
        public int parts = 1;           // exports the tile is built from

        // One tile from what each export that reaches it gives (the first has most of the tile): their roads, rivers,
        // lakes, buildings and props side by side, and the ground from the export that owns each point of it.
        public static GeoTileJob Merge(IList<GeoTileJob> fragments, GeoTilePlan plan)
        {
            var main = fragments[0];
            var env = new EnvironmentData { format = main.env.format, version = main.env.version, origin = main.env.origin, bounds = main.core,
                terrain = main.env.terrain, materials = main.env.materials, surround = main.env.surround, outer = plan.Outer() };
            var buildings = new BuildingsData { format = main.buildings.format, version = main.buildings.version, origin = main.buildings.origin, area = new BuildingArea { bounds = main.core } };
            var levels = new List<float>();
            var pieces = new List<PathBuild>(); var rivers = new List<PathBuild>();
            foreach (var f in fragments)
            {
                env.roads.AddRange(f.env.roads); env.waterways.AddRange(f.env.waterways); env.railways.AddRange(f.env.railways); env.water.AddRange(f.env.water);
                env.areas.AddRange(f.env.areas); env.barriers.AddRange(f.env.barriers); env.hedges.AddRange(f.env.hedges); env.treeRows.AddRange(f.env.treeRows);
                env.trees.AddRange(f.env.trees); env.props.AddRange(f.env.props);
                buildings.buildings.AddRange(f.buildings.buildings);
                levels.AddRange(f.waterLevels); pieces.AddRange(f.paths); if (f.rivers != null) rivers.AddRange(f.rivers);
            }
            var field = new HeightField(main.field.bounds, main.field.resolution);
            var byPart = new Dictionary<int, GeoTileJob>();
            foreach (var f in fragments) byPart[f.part] = f;
            for (int z = 0; z < field.resolution; z++)
                for (int x = 0; x < field.resolution; x++)
                {
                    var from = byPart.TryGetValue(plan.OwnerOf(field.X(x), field.Z(z)), out var owner) ? owner : main;
                    field.heights[z, x] = from.field.heights[z, x];
                }
            float baseY = fragments.Min(f => f.baseY), top = fragments.Max(f => f.baseY + f.range);
            return new GeoTileJob { env = env, buildings = buildings, field = field, margin = main.margin, paths = pieces, rivers = rivers, waterLevels = levels.ToArray(),
                core = main.core, index = main.index, size = main.size, baseY = baseY, range = top - baseY, folder = main.folder, name = main.name, part = main.part, parts = fragments.Count };
        }

        // the height field of the tile alone: the terrain
        public HeightField Core(HeightField field)
        {
            int n = field.resolution - 2 * margin;
            var result = new HeightField(core, n);
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) result.heights[z, x] = field.heights[z + margin, x + margin];
            return result;
        }
    }

    // The whole export, read and prepared once for all its tiles.
    internal sealed class GeoTileSource
    {
        public EnvironmentData env;
        public BuildingsData buildings;
        public GeoHeightSource heights;
        public Func<float, float, float> heightAt; // the ground: the export's own heights, or where exports meet the world's
        public List<PathBuild> paths;
        public float[] waterLevels;
        public string environmentPath, buildingsPath;
        public float tileSize, baseY, range;
        public Vector2 origin;
        public int resolution;
        public readonly List<string> notes = new List<string>();
        GenerationSettings settings;
        string files;
        Rect[] pathBox, roadBox, waterwayBox, waterBox, areaBox, barrierBox, hedgeBox, rowBox, railBox;
        readonly Dictionary<Vector2Int, List<BuildingFeature>> cells = new Dictionary<Vector2Int, List<BuildingFeature>>();

        // origin: the south-west corner of tile (0, 0); null: the corner of the coordinates (0, 0), the same for every export
        // (see GeoTilePlan). env: the export when it has been read already.
        // blend, part: the ground of an export that meets others (GeoHeightBlend); lakeLevels: the level of each lake by its id,
        // so that a lake in two exports has one level.
        public static GeoTileSource Prepare(string environmentPath, string buildingsPath, GenerationSettings settings, float tileSize, Vector2? origin, EnvironmentData env = null,
            GeoHeightBlend blend = null, int part = 0, Dictionary<string, float> lakeLevels = null)
        {
            var s = new GeoTileSource { environmentPath = environmentPath, buildingsPath = buildingsPath, settings = settings, tileSize = tileSize };
            s.env = env ?? GeoData.ReadEnvironment(environmentPath);
            s.buildings = string.IsNullOrEmpty(buildingsPath)
                ? new BuildingsData { format = "geo-buildings", version = 1, origin = s.env.origin }
                : GeoData.ReadBuildings(buildingsPath, s.env.origin);
            s.heights = blend != null ? blend.Source(part) : GeoHeightSource.Load(s.env, environmentPath);
            if (s.heights == null && (blend == null || !blend.HasHeights)) s.notes.Add("No heightmap: generated flat terrain at Y = 0.");
            var heights = s.heights;
            // exports that meet: the ground is the world's one, not this export's own
            Func<float, float, float> height = blend != null ? (Func<float, float, float>)blend.Sample : heights != null ? (Func<float, float, float>)heights.Sample : (x, z) => 0;
            s.heightAt = blend != null || heights != null ? height : null;
            s.paths = GeoWorldBuild.PreparePaths(s.env, height, settings, s.notes, GeoWaterMask.Load(s.env, environmentPath));
            s.waterLevels = GeoWorldBuild.WaterLevels(s.env.water, height);
            if (lakeLevels != null)
                for (int i = 0; i < s.env.water.Count; i++)
                {
                    string id = s.env.water[i].id;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (lakeLevels.TryGetValue(id, out var level)) s.waterLevels[i] = level; else lakeLevels[id] = s.waterLevels[i];
                }
            // a river through a lake is at the lake's level (on the whole river, so every tile has the same)
            GeoWater.SettleRivers(s.paths, s.env.water, s.waterLevels);
            // room below for river beds and lakes, as a single world has (for exports that meet: below the lowest of them)
            s.baseY = (blend != null ? blend.Low : heights?.min ?? 0) - 8 - (settings.carveRivers ? settings.riverDepth + 1 : 0);
            s.range = Mathf.Max(32, (blend != null ? blend.High : heights?.max ?? 0) - s.baseY + 8);
            s.origin = origin ?? Vector2.zero;
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
            if (blend != null) text.Append(blend.Fingerprint(part));
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

        // plan, part: a tile that several exports build is cut for this export (part): it gives what lies on its own
        // region. Null: the export builds the whole tile.
        public GeoTileJob Job(Vector2Int index, GeoBounds core, string folder, string name, GeoTilePlan plan = null, int part = -1)
        {
            double cell = (double)tileSize / (resolution - 1);
            int margin = Mathf.CeilToInt(GeoTileGeometry.MarginMetres / (float)cell);
            var around = new GeoBounds { minX = (float)(core.minX - margin * cell), minZ = (float)(core.minZ - margin * cell),
                maxX = (float)(core.minX + (resolution - 1 + margin) * cell), maxZ = (float)(core.minZ + (resolution - 1 + margin) * cell) };
            // every grid point from the tile's corner and the shared cell size, so neighbours sample the same places
            int n = resolution + 2 * margin;
            var field = new HeightField(around, n);
            if (heightAt != null)
                for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
                    field.heights[z, x] = heightAt((float)(core.minX + (x - margin) * cell), (float)(core.minZ + (z - margin) * cell));
            // the ground this export gives to the tile: all of it, or its region where exports meet
            bool shared = plan != null && plan.tiles.TryGetValue(index, out var builders) && builders.Count > 1;
            var reach = shared ? GeoTilePlan.Intersection(around, plan.region[part]) : around;
            if (reach == null) return null;
            Func<float, float, bool> owns = shared ? (Func<float, float, bool>)((x, z) => plan.OwnerOf(x, z) == part) : (x, z) => true;
            var area = new Rect(reach.minX, reach.minZ, reach.Width, reach.Depth);
            var e = new EnvironmentData { format = env.format, version = env.version, origin = env.origin, bounds = core, terrain = env.terrain,
                materials = env.materials, surround = around, outer = env.bounds };
            e.roads = Pick(env.roads, roadBox, area); e.waterways = Pick(env.waterways, waterwayBox, area); e.railways = Pick(env.railways, railBox, area);
            e.areas = Pick(env.areas, areaBox, area); e.barriers = Pick(env.barriers, barrierBox, area);
            e.hedges = Pick(env.hedges, hedgeBox, area); e.treeRows = Pick(env.treeRows, rowBox, area);
            var levels = new List<float>();
            for (int i = 0; i < env.water.Count; i++) if (waterBox[i].Overlaps(area)) { e.water.Add(env.water[i]); levels.Add(waterLevels[i]); }
            e.trees = env.trees.Where(t => t != null && around.Contains(t.x, t.z) && owns(t.x, t.z)).ToList();
            e.props = env.props.Where(p => p != null && around.Contains(p.x, p.z) && owns(p.x, p.z)).ToList();
            var pieces = new List<PathBuild>();
            for (int i = 0; i < paths.Count; i++) if (pathBox[i].Overlaps(area)) pieces.AddRange(GeoTileGeometry.ClipPath(paths[i], reach, settings.pathStep));
            // the rivers for the water: further out, but where exports meet still only on this export's region
            var wide = new GeoBounds { minX = around.minX - GeoTileGeometry.RiverReach, minZ = around.minZ - GeoTileGeometry.RiverReach, maxX = around.maxX + GeoTileGeometry.RiverReach, maxZ = around.maxZ + GeoTileGeometry.RiverReach };
            var riverReach = shared ? GeoTilePlan.Intersection(wide, plan.region[part]) : wide;
            var rivers = new List<PathBuild>();
            if (riverReach != null)
            {
                var riverArea = new Rect(riverReach.minX, riverReach.minZ, riverReach.Width, riverReach.Depth);
                for (int i = 0; i < paths.Count; i++) if (paths[i].river && pathBox[i].Overlaps(riverArea)) rivers.AddRange(GeoTileGeometry.ClipPath(paths[i], riverReach, settings.pathStep));
            }
            var nearby = new BuildingsData { format = buildings.format, version = buildings.version, origin = buildings.origin, area = new BuildingArea { bounds = core } };
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                if (cells.TryGetValue(new Vector2Int(index.x + dx, index.y + dz), out var list))
                    nearby.buildings.AddRange(list.Where(b => around.Contains(b.x, b.z) && owns(b.x, b.z)));
            return new GeoTileJob { env = e, buildings = nearby, field = field, margin = margin, paths = pieces, rivers = rivers, waterLevels = levels.ToArray(),
                core = core, index = index, size = tileSize, baseY = baseY, range = range, folder = folder, name = name, part = part };
        }

        // Fingerprint of what a tile is built from: the export files, the settings (libraries by asset), the tile and the grid.
        public string Hash(GeoBounds core, GenerationSettings settings)
        {
            var copy = JsonUtility.FromJson<GenerationSettings>(JsonUtility.ToJson(settings));
            // assets by their GUIDs (in the JSON they would be instance IDs, new in every editor session)
            string libraries = Guid(copy.ruinLibrary) + "|" + Guid(copy.homeLibrary) + "|" + Guid(copy.propCatalog) + "|" +
                               string.Join(",", (copy.treePrefabs ?? new GameObject[0]).Select(p => Guid(p)));
            copy.ruinLibrary = null; copy.homeLibrary = null; copy.propCatalog = null; copy.treePrefabs = null;
            copy.loadRadius = 0; // how far the tiles stream around the camera does not change what a tile is
            string text = string.Join("|", GeoTileGeometry.Version, files, GeoTileGeometry.TileSettingsJson(copy), libraries,
                core.minX.ToString("R"), core.minZ.ToString("R"), tileSize.ToString("R"), baseY.ToString("R"), range.ToString("R"));
            using (var md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        // the asset's GUID: the same in the editor and in a player (the registry's key starts with it)
#if UNITY_EDITOR
        static string Guid(Object asset) => asset ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) : "";
#else
        static string Guid(Object asset) { string key = GeoAssets.Key(asset); return key != null ? key.Substring(0, key.IndexOf(':') < 0 ? key.Length : key.IndexOf(':')) : ""; }
#endif

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

}
