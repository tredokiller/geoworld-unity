using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Where a crater comes from: seen on the imagery (GeoCraterDetector), scattered where the fighting was (GeoCraterScatter:
    // the small pits no imagery resolves), or given by the export itself (the app, or drawn by hand).
    public static class GeoCraterSource
    {
        public const int Detected = 0, Scattered = 1, Given = 2;
    }

    // What a trench is: a fire trench (along a tree line, zigzag), a communication trench (back to cover), a dugout's pit
    // (its roof gone), an anti-tank ditch.
    // What a battlefield's tiles draw besides the ground (the settlements after the battle: GeoPropDamage, GeoPowerLines,
    // GeoHomeWear): part of the fingerprint of every tile and package with a combat bake, so a change of it rebuilds only
    // those (a world without a battlefield keeps its tiles). Bump it when they draw differently.
    public static class GeoWarLook
    {
        public const string Version = "war-look-1"; // 1: street props after the battle, power lines, worn homes
    }

    public static class GeoTrenchKind
    {
        public const int Fire = 0, Communication = 1, Dugout = 2, AntiTank = 3;
    }

    // The combat bake of an export (GeoCombatBake) as a file next to its environment.json, so the export as the app made it is
    // never rewritten: combat.json, with the craters, where they came from and the notes of the bake. ReadEnvironment adds its
    // craters to the export's own, and a tile's fingerprint includes the file, so a new bake rebuilds the tiles it changes.
    [Serializable]
    public sealed class GeoCombatFile
    {
        public const string FileName = "combat.json";
        public const string Format = "geo-combat";

        public string format = Format;
        public int version = 2;
        public string created;
        // the export it was baked from: a file next to another export (an area extracted again into the same folder) is not used
        public GeoOrigin origin;
        public GeoBounds bounds;
        // the imagery the craters were read from (tile server, zoom), with its attribution
        public string imagery;
        public List<string> notes = new List<string>();
        public List<CraterFeature> craters = new List<CraterFeature>();
        public List<TrenchFeature> trenches = new List<TrenchFeature>();
        // states estimated for the buildings the app has no evidence for (GeoBuildingEstimates), by building id
        public List<GeoBuildingEstimate> buildings = new List<GeoBuildingEstimate>();

        public static string PathFor(string environmentPath) =>
            string.IsNullOrEmpty(environmentPath) ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(environmentPath)) ?? "", FileName);

        // The file next to the export, or null when there is none (or it is not one).
        public static GeoCombatFile Read(string environmentPath)
        {
            string path = PathFor(environmentPath);
            if (path == null || !File.Exists(path)) return null;
            GeoCombatFile file;
            try { file = JsonConvert.DeserializeObject<GeoCombatFile>(File.ReadAllText(path)); }
            catch (Exception e) when (e is JsonException || e is IOException)
            {
                // a broken file (a bake cut short) is not used, and the next bake writes it anew
                Debug.LogWarning("Geo World: " + path + " cannot be read (" + e.Message + ") and is not used.");
                return null;
            }
            if (file == null || file.format != Format || file.version != 2) return null;
            file.craters = file.craters ?? new List<CraterFeature>();
            file.trenches = file.trenches ?? new List<TrenchFeature>();
            file.buildings = file.buildings ?? new List<GeoBuildingEstimate>();
            file.notes = file.notes ?? new List<string>();
            return file;
        }

        public void Write(string environmentPath)
        {
            // the same bake as the file there already: leave it (and so every tile's fingerprint, which takes its time stamp)
            var old = Read(environmentPath);
            created = null;
            if (old != null)
            {
                string stamp = old.created;
                old.created = null;
                if (JsonConvert.SerializeObject(old, Formatting.None) == JsonConvert.SerializeObject(this, Formatting.None)) { created = stamp; return; }
            }
            created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            File.WriteAllText(PathFor(environmentPath), JsonConvert.SerializeObject(this, Formatting.None));
        }

        // whether the file was baked from this export (its origin and bounds)
        public bool Matches(GeoOrigin otherOrigin, GeoBounds otherBounds)
        {
            if (origin == null || bounds == null || otherOrigin == null) return false;
            if (Math.Abs(origin.lat - otherOrigin.lat) > 1e-6 || Math.Abs(origin.lon - otherOrigin.lon) > 1e-6) return false;
            return otherBounds == null || Mathf.Abs(bounds.minX - otherBounds.minX) < 1 && Mathf.Abs(bounds.minZ - otherBounds.minZ) < 1 &&
                   Mathf.Abs(bounds.maxX - otherBounds.maxX) < 1 && Mathf.Abs(bounds.maxZ - otherBounds.maxZ) < 1;
        }

        // Adds the bake's craters to the export's own; a crater the export gives keeps its place, and the bake's on top of it
        // is left out.
        public static void AddTo(EnvironmentData env, string environmentPath)
        {
            var file = Read(environmentPath);
            if (file == null) return;
            if (!file.Matches(env.origin, env.bounds))
            {
                Debug.LogWarning("Geo World: " + PathFor(environmentPath) + " was baked from another export (origin or bounds differ) and is not used.");
                return;
            }
            env.battlefield = true;
            env.trenches.AddRange(file.trenches);
            if (file.craters.Count == 0) return;
            if (env.craters.Count == 0) { env.craters.AddRange(file.craters); return; }
            var given = new GeoCraterGrid(env.craters, 20);
            foreach (var crater in file.craters)
                if (!given.Overlaps(crater)) env.craters.Add(crater);
        }
    }

    // The bake's state for a building the app had no evidence for.
    [Serializable]
    public sealed class GeoBuildingEstimate
    {
        public string id, state;
        public float perHectare;
    }

    // Craters in cells for finding those near a point.
    public sealed class GeoCraterGrid
    {
        readonly Dictionary<long, List<CraterFeature>> cells = new Dictionary<long, List<CraterFeature>>();
        readonly float cell;
        float widest;

        public GeoCraterGrid(IEnumerable<CraterFeature> craters, float cell)
        {
            this.cell = cell;
            foreach (var c in craters) Add(c);
        }

        public void Add(CraterFeature c)
        {
            long key = Key(Mathf.FloorToInt(c.x / cell), Mathf.FloorToInt(c.z / cell));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<CraterFeature>();
            list.Add(c);
            widest = Mathf.Max(widest, c.diameter);
        }

        // how many craters have their centre within radius of (x, z)
        public int Count(float x, float z, float radius)
        {
            int n = 0, cx0 = Mathf.FloorToInt((x - radius) / cell), cx1 = Mathf.FloorToInt((x + radius) / cell);
            int cz0 = Mathf.FloorToInt((z - radius) / cell), cz1 = Mathf.FloorToInt((z + radius) / cell);
            for (int cz = cz0; cz <= cz1; cz++) for (int cx = cx0; cx <= cx1; cx++)
                if (cells.TryGetValue(Key(cx, cz), out var list))
                    foreach (var o in list)
                        if ((o.x - x) * (o.x - x) + (o.z - z) * (o.z - z) <= radius * radius) n++;
            return n;
        }

        // whether c's pit overlaps another's by more than share of the smaller radius
        public bool Overlaps(CraterFeature c, float share = 0.5f)
        {
            // as many cells around as the widest crater can reach (a given bomb crater may be wider than a cell)
            int cx = Mathf.FloorToInt(c.x / cell), cz = Mathf.FloorToInt(c.z / cell);
            int r = Mathf.Max(1, Mathf.CeilToInt((c.diameter + widest) * 0.5f / cell));
            for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++)
                if (cells.TryGetValue(Key(cx + dx, cz + dz), out var list))
                    foreach (var o in list)
                    {
                        float ra = c.diameter * 0.5f, rb = o.diameter * 0.5f;
                        float d = Mathf.Sqrt((c.x - o.x) * (c.x - o.x) + (c.z - o.z) * (c.z - o.z));
                        if (d < ra + rb - share * Mathf.Min(ra, rb) * 2) return true;
                    }
            return false;
        }

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }

    // The shape of a crater relative to the ground around it, as a function of the distance from its centre (s = r / R, R the
    // pit's radius): a paraboloid bowl down to the depth at the centre, a lip raised around its edge with its crest at 1.2 R,
    // and an apron of thrown-out soil thinning out to 2.2 R (after Glasstone and Dolan's crater: lip about a quarter of the
    // depth at 1.25 R, continuous ejecta to about 2.15 R; small shell craters keep a lower lip). Age wears it: the bowl fills
    // (to 0.55 of the depth when fully worn), the lip drops to 0.4 of its height and spreads. The generator carves it
    // (GeoCombatGround) and the environment layer draws the same shape in finer detail.
    public static class GeoCraterProfile
    {
        public const float LipCrest = 1.2f, Reach = 2.25f;

        public static float Depth(float depth, float age) => Mathf.Max(0, depth) * (1 - 0.45f * Mathf.Clamp01(age));
        public static float Lip(float depth, float age) => 0.18f * Mathf.Max(0, depth) * (1 - 0.6f * Mathf.Clamp01(age));

        // metres above (+) or below (-) the ground around the crater at s = r / R
        public static float Offset(CraterFeature c, float s) => Offset(c.depth, c.age, s);

        // the same for a crater of that depth and age (the environment layer's craters, and its shader: GroundCraters.hlsl)
        public static float Offset(float craterDepth, float craterAge, float s)
        {
            if (s >= Reach) return 0;
            float age = Mathf.Clamp01(craterAge);
            float depth = Depth(craterDepth, age), lip = Lip(craterDepth, age), width = 0.28f * (1 + 0.8f * age);
            float bowl = s < 1 ? -depth * (1 - s * s) : 0;
            float g = (s - LipCrest) / width;
            float crest = lip * Mathf.Exp(-g * g) * Smooth(0.7f, 1f, s);
            float apron = s > LipCrest ? 0.35f * lip * (1 - Smooth(LipCrest, Reach, s)) : 0.35f * lip * Smooth(0.9f, LipCrest, s);
            return bowl + Mathf.Max(crest, apron);
        }

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }
    }

    // The cross-section of a trench (d: metres from its centreline): a groove down to the depth, with a flat floor and steep
    // walls when fresh and a V of slumped walls when old (worn: up to half as wide again at the top, a third shallower), and
    // the spoil thrown up on both sides as low banks (parapet and parados) with their crest about a metre past the edge.
    public static class GeoTrenchProfile
    {
        public static float HalfWidth(float width, float age) => 0.5f * Mathf.Max(0.3f, width) * (1 + 0.5f * Mathf.Clamp01(age));
        public static float Depth(float depth, float age) => Mathf.Max(0, depth) * (1 - 0.35f * Mathf.Clamp01(age));
        public static float Reach(float width, float age) => HalfWidth(width, age) + 2.4f;

        public static float Offset(float depth, float width, float age, float d)
        {
            age = Mathf.Clamp01(age);
            float half = HalfWidth(width, age), floor = Depth(depth, age), bank = 0.25f * Mathf.Max(0, depth) * (1 - 0.3f * age);
            if (d >= half + 2.4f) return 0;
            float t = d / half;
            float groove = t < 1 ? -floor * (1 - Mathf.Pow(t, 2 + 2 * (1 - age))) : 0;
            float g = (d - half - 0.9f) / 0.75f;
            float spoil = bank * Mathf.Exp(-g * g) * Smooth(0.7f * half, half, d);
            return groove + spoil;
        }

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }
    }

    // Craters carved into a tile's height field (GeoWorldBuild, after the water and the roads). Each node takes the deepest bowl
    // that covers it and, outside the bowls, the highest lip or apron, relative to its own height before carving: a pure
    // function of its place and the world's crater list, so tiles meet without a step. Buildings keep their ground (a crater
    // under a footprint would sink its base: the ruins bring their own rubble), and water keeps its bed. A crater narrower than
    // about one and a half cells cannot be a bowl on the grid: it is left to the environment layer's drawing.
    public static class GeoCombatGround
    {
        // metres around footprints and water where craters fade out
        const float BuildingFade = 3, WaterFade = 4;

        // the area a crater shapes: its pit, lip and apron
        public static Rect Reach(CraterFeature c)
        {
            float r = (c != null ? Mathf.Max(0.1f, c.diameter) * 0.5f : 0) * GeoCraterProfile.Reach;
            return c == null ? Rect.zero : new Rect(c.x - r, c.z - r, 2 * r, 2 * r);
        }

        // the area a trench shapes: its line, groove and banks
        public static Rect Reach(TrenchFeature t)
        {
            if (t?.points == null || t.points.Length < 4) return Rect.zero;
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i + 1 < t.points.Length; i += 2)
            {
                minX = Mathf.Min(minX, t.points[i]); maxX = Mathf.Max(maxX, t.points[i]);
                minZ = Mathf.Min(minZ, t.points[i + 1]); maxZ = Mathf.Max(maxZ, t.points[i + 1]);
            }
            float r = GeoTrenchProfile.Reach(t.width, t.age);
            return Rect.MinMaxRect(minX - r, minZ - r, maxX + r, maxZ + r);
        }

        public static void Carve(HeightField field, IList<CraterFeature> craters, BuildingsData buildings, GeoWater water, List<string> notes) =>
            Carve(field, craters, null, buildings, water, notes);

        public static void Carve(HeightField field, IList<CraterFeature> craters, IList<TrenchFeature> trenches, BuildingsData buildings, GeoWater water, List<string> notes)
        {
            int n = field.resolution;
            float cellX = field.bounds.Width / (n - 1), cellZ = field.bounds.Depth / (n - 1), cell = Mathf.Max(cellX, cellZ);
            float minDiameter = 1.5f * cell;
            var low = new float[n, n];   // the deepest bowl at each node (<= 0)
            var high = new float[n, n];  // the highest lip or apron at each node (>= 0)
            // the highest a node under a trench's mesh may stand, relative to its ground (null: no trench; +infinity: none near)
            float[,] cap = null;
            int carved = 0, small = 0;
            int grooves = Grooves(field, trenches, low, high, ref cap);
            craters = craters ?? new CraterFeature[0];
            foreach (var c in craters)
            {
                if (c == null || !GeoData.Finite(c.x) || !GeoData.Finite(c.z) || !(c.diameter > 0) || !(c.depth > 0)) continue;
                if (c.diameter < minDiameter) { small++; continue; }
                float radius = c.diameter * 0.5f, reach = radius * GeoCraterProfile.Reach;
                int x0 = Mathf.Max(0, Mathf.FloorToInt((c.x - reach - field.bounds.minX) / cellX)), x1 = Mathf.Min(n - 1, Mathf.CeilToInt((c.x + reach - field.bounds.minX) / cellX));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((c.z - reach - field.bounds.minZ) / cellZ)), z1 = Mathf.Min(n - 1, Mathf.CeilToInt((c.z + reach - field.bounds.minZ) / cellZ));
                if (x0 > x1 || z0 > z1) continue;
                carved++;
                for (int z = z0; z <= z1; z++)
                {
                    float dz = field.Z(z) - c.z;
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = field.X(x) - c.x;
                        float offset = GeoCraterProfile.Offset(c, Mathf.Sqrt(dx * dx + dz * dz) / radius);
                        if (offset < low[z, x]) low[z, x] = offset;
                        else if (offset > high[z, x]) high[z, x] = offset;
                    }
                }
            }
            if (carved == 0 && grooves == 0)
            {
                if (small > 0)
                    notes.Add($"Craters: {small} too small for the height grid ({cell:F2} m), drawn only by the environment layer.");
                return;
            }
            var keep = Keep(field, buildings, water);
            // every node's change from the ground as it was (slopes read before anything is cut), then applied
            var offsets = new float[n, n];
            int changed = 0, held = 0;
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float bowl = low[z, x], rim = high[z, x];
                    float limit = cap != null ? cap[z, x] : float.PositiveInfinity;
                    if (bowl == 0 && rim == 0 && float.IsPositiveInfinity(limit)) continue;
                    // a lip inside another crater's bowl is cut away with the bowl (the later blast throws it out)
                    float offset = bowl + rim * Mathf.Exp(bowl / 0.15f);
                    offset *= 1 - keep[z, x];
                    if (!float.IsPositiveInfinity(limit))
                    {
                        // under a trench's mesh the terrain stays below it, by the clearance and by however much lower the
                        // ground gets within a cell (the mesh stands on the ground of its own place)
                        float under = limit - GeoTrenchMesh.Clearance - Fall(field, x, z);
                        if (offset > under) { offset = under; held++; }
                    }
                    offsets[z, x] = offset;
                }
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    if (offsets[z, x] == 0) continue;
                    field.heights[z, x] += offsets[z, x];
                    changed++;
                }
            if (carved > 0 || small > 0)
                notes.Add($"Craters: {carved} carved into the ground" + (grooves == 0 ? $" ({changed} height nodes at {cell:F2} m)" : "") +
                          (small > 0 ? $", {small} too small for the grid, drawn only by the environment layer" : "") + ".");
            if (grooves > 0)
                notes.Add($"Trenches: {grooves} cut under their slots ({held} height nodes held below them at {cell:F2} m).");
        }

        // how much lower than a node the ground gets at its neighbours (0 on a hilltop or flat ground)
        static float Fall(HeightField field, int x, int z)
        {
            int n = field.resolution;
            float h = field.heights[z, x], lowest = h;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, zz = z + dz;
                    if (xx < 0 || zz < 0 || xx >= n || zz >= n) continue;
                    lowest = Mathf.Min(lowest, field.heights[zz, xx]);
                }
            return h - lowest;
        }

        // The cap under the trenches' slots, segment by segment (GeoTrenchSection.Cap: every node within a cell's diagonal of
        // a slot below it, so no terrain triangle rises through it; round a trench's end the same, the end wall being round).
        // The terrain is only cut, and only there: the trench, its spoil and the apron over the cut are the mesh's
        // (GeoTrenchMesh), and past the slot the terrain keeps its height under them. Returns how many trenches touch the field.
        static int Grooves(HeightField field, IList<TrenchFeature> trenches, float[,] low, float[,] high, ref float[,] cap)
        {
            if (trenches == null) return 0;
            int n = field.resolution, grooves = 0;
            float cellX = field.bounds.Width / (n - 1), cellZ = field.bounds.Depth / (n - 1), cell = Mathf.Max(cellX, cellZ);
            float diagonal = Mathf.Sqrt(cellX * cellX + cellZ * cellZ);
            foreach (var t in trenches)
            {
                if (t?.points == null || t.points.Length < 4 || !(t.depth > 0)) continue;
                var section = GeoTrenchSection.Of(t, cell);
                float reach = section.half + diagonal + GeoTrenchSection.Sway;
                bool touched = false;
                // the centreline the mesh follows (GeoTrenchMesh.Centreline: no spikes)
                var points = GeoTrenchMesh.Centreline(t);
                for (int k = 0; k + 1 < points.Count; k++)
                {
                    var a = points[k]; var b = points[k + 1];
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - reach - field.bounds.minX) / cellX)), x1 = Mathf.Min(n - 1, Mathf.CeilToInt((Mathf.Max(a.x, b.x) + reach - field.bounds.minX) / cellX));
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, b.y) - reach - field.bounds.minZ) / cellZ)), z1 = Mathf.Min(n - 1, Mathf.CeilToInt((Mathf.Max(a.y, b.y) + reach - field.bounds.minZ) / cellZ));
                    if (x0 > x1 || z0 > z1) continue;
                    touched = true;
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float d = GeoData.SegmentDistance(new Vector2(field.X(x), field.Z(z)), a, b, out _);
                            if (d >= reach) continue;
                            float limit = section.Cap(d, diagonal);
                            if (float.IsPositiveInfinity(limit)) continue;
                            if (cap == null)
                            {
                                cap = new float[n, n];
                                for (int zz = 0; zz < n; zz++) for (int xx = 0; xx < n; xx++) cap[zz, xx] = float.PositiveInfinity;
                            }
                            if (limit < cap[z, x]) cap[z, x] = limit;
                        }
                }
                if (touched) grooves++;
            }
            return grooves;
        }

        // The distance of every node to water (<= 0 in it), exact enough up to WaterFade. GeoWater measures it around rivers
        // and their areas only (a lake's nodes are just marked as inside), so a chamfer pass over the grid from every wet node
        // adds the ground around lakes: craters used to stop at a lake's outline in a step, their bowl cut off by the shore.
        static float[,] WaterDistance(GeoWater water, int n, float cellX, float cellZ)
        {
            var d = new float[n, n];
            float far = WaterFade * 2, diagonal = Mathf.Sqrt(cellX * cellX + cellZ * cellZ);
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    d[z, x] = Mathf.Min(water.Distance(x, z), far);
            // forward: the left neighbour and the row below; backward: the right neighbour and the row above (from wet nodes
            // the distance starts at 0, so a lake's own negative values stay)
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    float v = d[z, x];
                    if (v <= 0) continue;
                    if (x > 0) v = Mathf.Min(v, Mathf.Max(0, d[z, x - 1]) + cellX);
                    if (z > 0)
                    {
                        v = Mathf.Min(v, Mathf.Max(0, d[z - 1, x]) + cellZ);
                        if (x > 0) v = Mathf.Min(v, Mathf.Max(0, d[z - 1, x - 1]) + diagonal);
                        if (x < n - 1) v = Mathf.Min(v, Mathf.Max(0, d[z - 1, x + 1]) + diagonal);
                    }
                    d[z, x] = v;
                }
            for (int z = n - 1; z >= 0; z--)
                for (int x = n - 1; x >= 0; x--)
                {
                    float v = d[z, x];
                    if (v <= 0) continue;
                    if (x < n - 1) v = Mathf.Min(v, Mathf.Max(0, d[z, x + 1]) + cellX);
                    if (z < n - 1)
                    {
                        v = Mathf.Min(v, Mathf.Max(0, d[z + 1, x]) + cellZ);
                        if (x < n - 1) v = Mathf.Min(v, Mathf.Max(0, d[z + 1, x + 1]) + diagonal);
                        if (x > 0) v = Mathf.Min(v, Mathf.Max(0, d[z + 1, x - 1]) + diagonal);
                    }
                    d[z, x] = v;
                }
            return d;
        }

        // per node, how much of its ground is kept as it was: 1 on a building's footprint and in water, fading out around them
        static float[,] Keep(HeightField field, BuildingsData buildings, GeoWater water)
        {
            int n = field.resolution;
            float cellX = field.bounds.Width / (n - 1), cellZ = field.bounds.Depth / (n - 1);
            var keep = new float[n, n];
            if (water != null)
            {
                var near = WaterDistance(water, n, cellX, cellZ);
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        float d = near[z, x];
                        if (d < WaterFade) keep[z, x] = d <= 0 ? 1 : 1 - d / WaterFade;
                    }
            }
            if (buildings?.buildings == null) return keep;
            var outline = new List<Vector2>();
            foreach (var b in buildings.buildings)
            {
                if (b?.footprint == null || b.footprint.Length < 6 || !GeoData.Finite(b.x) || !GeoData.Finite(b.z)) continue;
                outline.Clear();
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                for (int i = 0; i + 1 < b.footprint.Length; i += 2)
                {
                    var p = new Vector2(b.x + b.footprint[i], b.z + b.footprint[i + 1]);
                    outline.Add(p);
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
                }
                int x0 = Mathf.Max(0, Mathf.FloorToInt((minX - BuildingFade - field.bounds.minX) / cellX)), x1 = Mathf.Min(n - 1, Mathf.CeilToInt((maxX + BuildingFade - field.bounds.minX) / cellX));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((minZ - BuildingFade - field.bounds.minZ) / cellZ)), z1 = Mathf.Min(n - 1, Mathf.CeilToInt((maxZ + BuildingFade - field.bounds.minZ) / cellZ));
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = new Vector2(field.X(x), field.Z(z));
                        float d = Inside(outline, p) ? 0 : EdgeDistance(outline, p);
                        if (d >= BuildingFade) continue;
                        keep[z, x] = Mathf.Max(keep[z, x], 1 - d / BuildingFade);
                    }
            }
            return keep;
        }

        static bool Inside(List<Vector2> ring, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                if ((ring[i].y > p.y) != (ring[j].y > p.y) && p.x < (ring[j].x - ring[i].x) * (p.y - ring[i].y) / (ring[j].y - ring[i].y) + ring[i].x)
                    inside = !inside;
            return inside;
        }

        static float EdgeDistance(List<Vector2> ring, Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                best = Mathf.Min(best, GeoData.SegmentDistance(p, ring[j], ring[i], out _));
            return best;
        }
    }
}
