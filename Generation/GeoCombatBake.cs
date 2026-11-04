using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace GeoWorld.Editor
{
    // What the combat bake does (GeoCombatBake): find the craters on the imagery, and scatter the small pits it cannot show
    // where the fighting was.
    [Serializable]
    public sealed class GeoCombatSettings
    {
        [Tooltip("Find craters on the satellite imagery (GeoCraterDetector): pits from about 2 m across, bomb craters, water in them. " +
                 "Check that the imagery's licence allows reading data out of it.")]
        public bool detect = true;
        [Tooltip("Scatter the small pits no imagery resolves (mortars, drones, most shells: 1-3 m) where the fighting was: where the imagery " +
                 "shows craters and where buildings are damaged, denser the more of both.")]
        public bool scatter = true;
        [Tooltip("How heavy the fighting was, 0-2: scales the scattered pits.")]
        [Range(0, 2)] public float intensity = 1;
        [Tooltip("Scattered pits per hectare anywhere in the area, before the imagery and the damage add theirs.")]
        [Range(0, 20)] public float basePerHectare = 0.5f;
        [Tooltip("Scattered pits per hectare at most (the most shelled fields have tens per hectare).")]
        [Range(1, 200)] public float maxPerHectare = 60;
        [Tooltip("Share of scattered pits that look fresh (bright soil, sharp rim); the rest are worn and grown over. A finished battle: few.")]
        [Range(0, 1)] public float freshShare = 0.1f;
        [Tooltip("Dig the trenches of the battle in the style the war dug them along tree lines under heavy fire (GeoTrenchScatter): " +
                 "fire trenches, dugouts and communication trenches, abandoned for years. Never traced from imagery.")]
        public bool trenches = true;
        [Tooltip("Craters per hectare around a tree line from which it may have been a position.")]
        [Range(0.5f, 50)] public float trenchFromPerHectare = 2;
        [Tooltip("Scales the chance that a tree line under fire has trenches (0-2).")]
        [Range(0, 2)] public float trenchShare = 1;
        [Tooltip("Give the buildings the app has no evidence for a state from the craters around them (estimated, low confidence): in a " +
                 "razed town no house stands whole only because nobody reported it. Buildings with evidence keep theirs.")]
        public bool estimateBuildings = true;
        public int seed = 1;
        public GeoCraterDetector.Settings detector = new GeoCraterDetector.Settings();
    }

    // The combat bake of an export: the craters of the area, read once for all its tiles before any tile is built (a tile's
    // own imagery covers only its core and comes after its ground is shaped), and written next to the export (GeoCombatFile),
    // where every tile's build finds them (GeoCombatGround) and the environment layer reads them too. Craters seen on the
    // imagery come in pieces of about a kilometre with an overlap, so one on a seam is found once; the scattered ones come
    // from a hash of their place, so a new bake of the same area gives the same pits.
    public static class GeoCombatBake
    {
        const float Piece = 1000, Overlap = 100, TexelMetres = 0.4883f;

        public static GeoCombatFile Run(string environmentPath, string buildingsPath, GeoCombatSettings combat, GenerationSettings settings, List<string> notes,
            Action<string, float> progress, CancellationToken cancellation = default)
        {
            combat = combat ?? new GeoCombatSettings();
            var env = GeoData.ReadEnvironment(environmentPath, false);
            var buildings = string.IsNullOrEmpty(buildingsPath) ? new BuildingsData() : GeoData.ReadBuildings(buildingsPath, env.origin, false);
            var file = new GeoCombatFile { origin = env.origin, bounds = env.bounds };
            var b = env.bounds;
            var blocked = new GeoCombatMask(b, 1);
            blocked.Buildings(buildings, 1.5f);
            blocked.Water(env);
            var detected = new List<CraterFeature>();
            if (combat.detect && settings.satelliteImagery)
            {
                int px0 = Mathf.FloorToInt(b.minX / Piece), px1 = Mathf.CeilToInt(b.maxX / Piece) - 1;
                int pz0 = Mathf.FloorToInt(b.minZ / Piece), pz1 = Mathf.CeilToInt(b.maxZ / Piece) - 1;
                int pieces = (px1 - px0 + 1) * (pz1 - pz0 + 1), done = 0, zoom = 0;
                string template = null;
                var grid = new GeoCraterGrid(new CraterFeature[0], 25);
                for (int pz = pz0; pz <= pz1; pz++)
                    for (int px = px0; px <= px1; px++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var core = Rect.MinMaxRect(Mathf.Max(b.minX, px * Piece), Mathf.Max(b.minZ, pz * Piece), Mathf.Min(b.maxX, (px + 1) * Piece), Mathf.Min(b.maxZ, (pz + 1) * Piece));
                        if (core.width <= 1 || core.height <= 1) continue;
                        var area = Rect.MinMaxRect(Mathf.Max(b.minX, core.xMin - Overlap), Mathf.Max(b.minZ, core.yMin - Overlap), Mathf.Min(b.maxX, core.xMax + Overlap), Mathf.Min(b.maxZ, core.yMax + Overlap));
                        float side = Mathf.Max(area.width, area.height);
                        area = new Rect(area.xMin, area.yMin, side, side); // the imagery is square
                        int resolution = Mathf.CeilToInt(side / TexelMetres);
                        progress?.Invoke($"Craters on the imagery: piece {done + 1} / {pieces}", done / (float)pieces);
                        Color32[] pixels;
                        try
                        {
                            pixels = GeoSatellite.Pixels(env.origin, new GeoBounds { minX = area.xMin, minZ = area.yMin, maxX = area.xMax, maxZ = area.yMax }, settings,
                                resolution, null, null, out zoom, out _, out _, out _, out template);
                        }
                        catch (Exception e) when (!(e is OperationCanceledException))
                        {
                            notes.Add("Craters: no imagery for a piece of the area (" + e.Message + ").");
                            done++;
                            continue;
                        }
                        var exclude = blocked.Pixels(area, resolution);
                        foreach (var d in GeoCraterDetector.Detect(pixels, resolution, resolution, area, exclude, combat.detector))
                        {
                            if (!core.Contains(new Vector2(d.x, d.z))) continue;
                            var crater = Detected(d);
                            if (grid.Overlaps(crater, 0.6f)) continue;
                            grid.Add(crater);
                            detected.Add(crater);
                        }
                        done++;
                    }
                if (template != null)
                    file.imagery = $"{new Uri(template.Replace("{", "").Replace("}", "")).Host} zoom {zoom}; {GeoSatellite.Attribution(template)}";
                file.notes.Add($"Craters found on the imagery: {detected.Count} ({Count(detected, 7)} of them 7 m across or more).");
            }
            var scattered = combat.scatter ? GeoCraterScatter.Scatter(env, buildings, detected, blocked, combat, file.notes) : new List<CraterFeature>();
            file.craters.AddRange(detected);
            file.craters.AddRange(scattered);
            if (combat.trenches)
            {
                var roads = new GeoCombatMask(b, 1);
                roads.Roads(env);
                var ground = GeoGroundMap.Read(new[] { environmentPath }, Piece, file.notes);
                var treeLines = GeoTreeLines.Find(env, ground, file.notes);
                file.trenches.AddRange(GeoTrenchScatter.Scatter(treeLines, file.craters, blocked, roads, combat, file.notes));
            }
            if (combat.estimateBuildings)
                file.buildings.AddRange(GeoBuildingEstimates.Estimate(buildings, file.craters, combat, file.notes));
            file.notes.Add("The craters' places come from the imagery and a hash of their place; their depths and shapes are modelled from their size, " +
                           "not measured.");
            file.Write(environmentPath);
            notes.AddRange(file.notes);
            return file;
        }

        static int Count(List<CraterFeature> craters, float diameter) { int n = 0; foreach (var c in craters) if (c.diameter >= diameter) n++; return n; }

        // A crater from a detection: its pit, deeper for a bomb's (a third of the width) than a shell's (about a quarter);
        // fresh ones bright and sharp, the others worn by an amount from a hash of the place.
        static CraterFeature Detected(GeoCraterDetector.Detection d)
        {
            float diameter = Mathf.Clamp(d.pitDiameter, 0.8f, 30);
            float h = GeoCraterScatter.Hash01(Mathf.RoundToInt(d.x * 10), Mathf.RoundToInt(d.z * 10), 7, 0);
            return new CraterFeature
            {
                x = d.x, z = d.z, diameter = diameter,
                depth = Mathf.Min(6, diameter * (diameter >= 7 ? 0.33f : 0.27f)),
                age = d.fresh ? 0.15f : 0.55f + 0.4f * h,
                water = d.water, confidence = Mathf.Clamp01(d.confidence), source = GeoCraterSource.Detected
            };
        }
    }

    // The small pits where the fighting was: per 50 m cell a number from the craters the imagery shows near it (each visible
    // crater stands for about two pits too small to see) and the share of damaged buildings around it, scaled by the fighting's
    // intensity, then placed half in a cluster (a salvo's dispersion: an ellipse of about 18 x 8 m along a direction shared
    // by a 200 m district) and half anywhere in the cell. Sizes: most 1-1.8 m (mortars, drones, small shells), a third 1.8-3 m
    // (122-155 mm), a tenth 3-5 m; depth a fifth to a third of the width (measured shell craters: 0.17-0.32). Never on a
    // footprint or in water; on a road three times less often.
    public static class GeoCraterScatter
    {
        const float Cell = 50, Hectare = 10000;

        public static List<CraterFeature> Scatter(EnvironmentData env, BuildingsData buildings, List<CraterFeature> detected, GeoCombatMask blocked, GeoCombatSettings combat,
            List<string> notes)
        {
            var result = new List<CraterFeature>();
            var b = env.bounds;
            int nx = Mathf.Max(1, Mathf.CeilToInt(b.Width / Cell)), nz = Mathf.Max(1, Mathf.CeilToInt(b.Depth / Cell));
            // visible craters and damaged / all buildings per cell, summed over 3 x 3 (visible) and 5 x 5 cells (damage)
            var seen = new float[nz, nx];
            foreach (var c in detected) { var (i, j) = CellOf(b, c.x, c.z, nx, nz); seen[j, i]++; }
            var damaged = new float[nz, nx]; var standing = new float[nz, nx];
            foreach (var bd in buildings?.buildings ?? new List<BuildingFeature>())
            {
                if (bd == null || !GeoData.Finite(bd.x) || !GeoData.Finite(bd.z)) continue;
                var (i, j) = CellOf(b, bd.x, bd.z, nx, nz);
                standing[j, i]++;
                string state = bd.condition?.state;
                if (state == "damaged" || state == "heavily_damaged" || state == "destroyed") damaged[j, i]++;
            }
            var roads = new GeoCombatMask(b, 1);
            roads.Roads(env);
            float total = 0;
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    float visible = Sum(seen, i, j, 1, nx, nz) / (9 * Cell * Cell / Hectare);
                    float all = Sum(standing, i, j, 2, nx, nz), hit = Sum(damaged, i, j, 2, nx, nz);
                    float damage = all >= 3 ? hit / all : 0;
                    float perHectare = Mathf.Min(combat.maxPerHectare, combat.intensity * (combat.basePerHectare + 2 * visible + 30 * damage));
                    // fighting is patchy: some cells got far more than their neighbours
                    float expected = perHectare * Cell * Cell / Hectare * (0.5f + Hash01(i, j, 1, combat.seed));
                    int count = Mathf.FloorToInt(expected) + (Hash01(i, j, 2, combat.seed) < expected - Mathf.Floor(expected) ? 1 : 0);
                    if (count == 0) continue;
                    float x0 = b.minX + i * Cell, z0 = b.minZ + j * Cell;
                    // the cell's salvo: its centre, and the direction of the district it lies in
                    float cx = x0 + Cell * (0.2f + 0.6f * Hash01(i, j, 3, combat.seed)), cz = z0 + Cell * (0.2f + 0.6f * Hash01(i, j, 4, combat.seed));
                    float angle = Hash01(Mathf.FloorToInt(i * Cell / 200), Mathf.FloorToInt(j * Cell / 200), 5, combat.seed) * Mathf.PI;
                    Vector2 along = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), across = new Vector2(-along.y, along.x);
                    for (int k = 0; k < count; k++)
                    {
                        float x, z;
                        int s = k * 16;
                        if (Hash01(i, j, 10 + s, combat.seed) < 0.5f)
                        {
                            float u = Gaussian(i, j, 11 + s, combat.seed) * 9, v = Gaussian(i, j, 12 + s, combat.seed) * 4;
                            x = cx + along.x * u + across.x * v; z = cz + along.y * u + across.y * v;
                        }
                        else
                        {
                            x = x0 + Cell * Hash01(i, j, 13 + s, combat.seed); z = z0 + Cell * Hash01(i, j, 14 + s, combat.seed);
                        }
                        if (!b.Contains(x, z) || blocked.At(x, z)) continue;
                        if (roads.At(x, z) && Hash01(i, j, 15 + s, combat.seed) > 0.33f) continue;
                        float size = Hash01(i, j, 16 + s, combat.seed), d;
                        if (size < 0.55f) d = Mathf.Lerp(1.0f, 1.8f, size / 0.55f);
                        else if (size < 0.9f) d = Mathf.Lerp(1.8f, 3.0f, (size - 0.55f) / 0.35f);
                        else d = Mathf.Lerp(3.0f, 5.0f, (size - 0.9f) / 0.1f);
                        bool fresh = Hash01(i, j, 17 + s, combat.seed) < combat.freshShare;
                        result.Add(new CraterFeature
                        {
                            x = x, z = z, diameter = d, depth = d * (0.2f + 0.12f * Hash01(i, j, 18 + s, combat.seed)),
                            age = fresh ? 0.1f + 0.2f * Hash01(i, j, 19 + s, combat.seed) : 0.5f + 0.5f * Hash01(i, j, 20 + s, combat.seed),
                            water = d >= 1.8f && Hash01(i, j, 21 + s, combat.seed) < 0.1f,
                            confidence = 1, source = GeoCraterSource.Scattered
                        });
                    }
                    total += perHectare;
                }
            float area = b.Width * b.Depth / Hectare;
            notes.Add($"Craters scattered where the fighting was: {result.Count} small pits ({result.Count / Mathf.Max(0.01f, area):F1} per hectare over {area:F0} ha, " +
                      $"intensity {combat.intensity:F2}).");
            return result;
        }

        static (int, int) CellOf(GeoBounds b, float x, float z, int nx, int nz) =>
            (Mathf.Clamp(Mathf.FloorToInt((x - b.minX) / Cell), 0, nx - 1), Mathf.Clamp(Mathf.FloorToInt((z - b.minZ) / Cell), 0, nz - 1));

        static float Sum(float[,] a, int i, int j, int r, int nx, int nz)
        {
            float s = 0;
            for (int dj = -r; dj <= r; dj++) for (int di = -r; di <= r; di++)
            {
                int x = i + di, z = j + dj;
                if (x >= 0 && z >= 0 && x < nx && z < nz) s += a[z, x];
            }
            return s;
        }

        // a standard normal from two hashes (Box-Muller)
        static float Gaussian(int i, int j, int k, int seed)
        {
            float u = Mathf.Max(1e-6f, Hash01(i, j, k, seed)), v = Hash01(i, j, k + 1000, seed);
            return Mathf.Sqrt(-2 * Mathf.Log(u)) * Mathf.Cos(2 * Mathf.PI * v);
        }

        // 0..1 from four integers (a 32-bit integer hash: the same on every machine)
        public static float Hash01(int a, int b, int c, int seed)
        {
            uint h = (uint)seed * 0x9E3779B9u;
            h ^= (uint)a * 0x85EBCA6Bu; h = (h << 13) | (h >> 19); h *= 5; h += 0xE6546B64u;
            h ^= (uint)b * 0xC2B2AE35u; h = (h << 13) | (h >> 19); h *= 5; h += 0xE6546B64u;
            h ^= (uint)c * 0x27D4EB2Fu; h = (h << 13) | (h >> 19); h *= 5; h += 0xE6546B64u;
            h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    // Where craters never go (buildings and water) or go less often (roads), on a grid of metres over the export.
    public sealed class GeoCombatMask
    {
        readonly GeoBounds bounds;
        readonly float cell;
        readonly int nx, nz;
        readonly bool[] mask;

        public GeoCombatMask(GeoBounds bounds, float cell)
        {
            this.bounds = bounds; this.cell = cell;
            nx = Mathf.Max(1, Mathf.CeilToInt(bounds.Width / cell)); nz = Mathf.Max(1, Mathf.CeilToInt(bounds.Depth / cell));
            mask = new bool[nx * nz];
        }

        public bool At(float x, float z)
        {
            int i = Mathf.FloorToInt((x - bounds.minX) / cell), j = Mathf.FloorToInt((z - bounds.minZ) / cell);
            return i >= 0 && j >= 0 && i < nx && j < nz && mask[j * nx + i];
        }

        // the mask over area at resolution x resolution pixels (row 0 = south), for the detector
        public bool[] Pixels(Rect area, int resolution)
        {
            var result = new bool[resolution * resolution];
            float step = area.width / resolution;
            for (int v = 0; v < resolution; v++)
            {
                float z = area.yMin + (v + 0.5f) * step;
                for (int u = 0; u < resolution; u++) result[v * resolution + u] = At(area.xMin + (u + 0.5f) * step, z);
            }
            return result;
        }

        public void Buildings(BuildingsData buildings, float grow)
        {
            var ring = new List<Vector2>();
            foreach (var b in buildings?.buildings ?? new List<BuildingFeature>())
            {
                if (b?.footprint == null || b.footprint.Length < 6 || !GeoData.Finite(b.x) || !GeoData.Finite(b.z)) continue;
                ring.Clear();
                for (int i = 0; i + 1 < b.footprint.Length; i += 2) ring.Add(new Vector2(b.x + b.footprint[i], b.z + b.footprint[i + 1]));
                Fill(ring, grow);
            }
        }

        public void Water(EnvironmentData env)
        {
            var ring = new List<Vector2>();
            foreach (var w in env.water)
            {
                if (w?.rings == null) continue;
                foreach (var flat in w.rings)
                {
                    if (flat == null || flat.Length < 6) continue;
                    ring.Clear();
                    for (int i = 0; i + 1 < flat.Length; i += 2) ring.Add(new Vector2(flat[i], flat[i + 1]));
                    Fill(ring, 1);
                    break; // the outer ring: islands in a lake stay water for the craters' sake
                }
            }
            foreach (var line in env.waterways) Line(line?.points, Mathf.Max(1, (line?.width ?? 2) * 0.5f + 1));
        }

        public void Roads(EnvironmentData env)
        {
            foreach (var road in env.roads) if (road != null && !road.bridge && !road.tunnel) Line(road.points, Mathf.Max(1.5f, road.width * 0.5f));
        }

        void Line(float[] points, float half)
        {
            if (points == null || points.Length < 4) return;
            for (int k = 0; k + 3 < points.Length; k += 2)
            {
                var a = new Vector2(points[k], points[k + 1]); var c = new Vector2(points[k + 2], points[k + 3]);
                int i0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, c.x) - half - bounds.minX) / cell)), i1 = Mathf.Min(nx - 1, Mathf.CeilToInt((Mathf.Max(a.x, c.x) + half - bounds.minX) / cell));
                int j0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, c.y) - half - bounds.minZ) / cell)), j1 = Mathf.Min(nz - 1, Mathf.CeilToInt((Mathf.Max(a.y, c.y) + half - bounds.minZ) / cell));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                        if (GeoData.SegmentDistance(new Vector2(bounds.minX + (i + 0.5f) * cell, bounds.minZ + (j + 0.5f) * cell), a, c, out _) <= half) mask[j * nx + i] = true;
            }
        }

        // the polygon (a scanline fill: per row the spans between its edges' crossings) and grow metres around its edges
        void Fill(List<Vector2> ring, float grow)
        {
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var p in ring) { minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y); }
            int j0 = Mathf.Max(0, Mathf.FloorToInt((minZ - bounds.minZ) / cell)), j1 = Mathf.Min(nz - 1, Mathf.CeilToInt((maxZ - bounds.minZ) / cell));
            var crossings = new List<float>();
            for (int j = j0; j <= j1; j++)
            {
                float z = bounds.minZ + (j + 0.5f) * cell;
                crossings.Clear();
                for (int a = 0, c = ring.Count - 1; a < ring.Count; c = a++)
                    if ((ring[a].y > z) != (ring[c].y > z))
                        crossings.Add((ring[c].x - ring[a].x) * (z - ring[a].y) / (ring[c].y - ring[a].y) + ring[a].x);
                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    int i0 = Mathf.Max(0, Mathf.CeilToInt((crossings[k] - bounds.minX) / cell - 0.5f));
                    int i1 = Mathf.Min(nx - 1, Mathf.FloorToInt((crossings[k + 1] - bounds.minX) / cell - 0.5f));
                    for (int i = i0; i <= i1; i++) mask[j * nx + i] = true;
                }
            }
            if (grow <= 0) return;
            var flat = new float[(ring.Count + 1) * 2];
            for (int k = 0; k <= ring.Count; k++) { var p = ring[k % ring.Count]; flat[2 * k] = p.x; flat[2 * k + 1] = p.y; }
            Line(flat, grow);
        }
    }
}
