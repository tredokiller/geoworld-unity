using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // A state for the buildings the app gives no evidence for, where the fighting was (the combat bake). The app's radar
    // estimate never says "intact" and leaves a building unknown below its first line, and nobody reports every house of a
    // razed town: Unity drew every one of them whole (in Bakhmut's centre 1 678 of 2 763, in Klishchiivka 410 of 574).
    // Here each such building takes a state from the craters around it (within 150 m, per hectare) or from the share of
    // buildings around it the app shows damaged (within ~175 m, doubled), whichever says heavier: from one crater per hectare,
    // where 55 % stay intact, 30 % are damaged, 10 % heavily damaged and 5 % destroyed, to 15 craters and more (or half the
    // buildings around damaged), where 4 % stay intact, 22 % are damaged, 36 % heavily damaged and 38 % destroyed; which one
    // from a hash of its id, so a new bake gives the same. Marked estimated, low confidence, with the density in its note: the app's evidence always wins, and a
    // building with any is left as the app has it.
    public static class GeoBuildingEstimates
    {
        const float Radius = 150;

        public static List<GeoBuildingEstimate> Estimate(BuildingsData buildings, List<CraterFeature> craters, GeoCombatSettings combat, List<string> notes)
        {
            var result = new List<GeoBuildingEstimate>();
            if (buildings?.buildings == null || craters.Count == 0) return result;
            var grid = new GeoCraterGrid(craters, 50);
            float area = Mathf.PI * Radius * Radius / 10000f;
            // the app's evidence around: the share of all buildings within the radius it gives as damaged or worse (the radar
            // ranks a district; a centre where two in five buildings show damage on radar was fought over street by street)
            var evidence = new Dictionary<long, (int all, int hit)>();
            foreach (var b in buildings.buildings)
            {
                if (b == null || !GeoData.Finite(b.x) || !GeoData.Finite(b.z)) continue;
                long key = ((long)Mathf.FloorToInt(b.x / 50) << 32) ^ (uint)Mathf.FloorToInt(b.z / 50);
                evidence.TryGetValue(key, out var e);
                var st = b.condition != null ? b.condition.State : GeoConditionState.Unknown;
                evidence[key] = (e.all + 1, e.hit + (st >= GeoConditionState.Damaged ? 1 : 0));
            }
            var counts = new int[5];
            int unknown = 0;
            foreach (var b in buildings.buildings)
            {
                if (b == null || string.IsNullOrEmpty(b.id) || !GeoData.Finite(b.x) || !GeoData.Finite(b.z)) continue;
                if (b.condition != null && b.condition.State != GeoConditionState.Unknown) continue;
                unknown++;
                float perHectare = grid.Count(b.x, b.z, Radius) / area;
                if (perHectare < 1) continue;
                int all = 0, hit = 0, cx = Mathf.FloorToInt(b.x / 50), cz = Mathf.FloorToInt(b.z / 50);
                for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++)
                    if (evidence.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out var e)) { all += e.all; hit += e.hit; }
                float shown = all >= 8 ? hit / (float)all : 0;
                float t = Mathf.Max(Mathf.Clamp01((perHectare - 1) / 14), Mathf.Clamp01(shown * 2));
                float intact = Mathf.Lerp(0.55f, 0.04f, t), damaged = Mathf.Lerp(0.30f, 0.22f, t), heavy = Mathf.Lerp(0.10f, 0.36f, t);
                float h = Hash(b.id, combat.seed);
                var state = h < intact ? GeoConditionState.Intact : h < intact + damaged ? GeoConditionState.Damaged
                    : h < intact + damaged + heavy ? GeoConditionState.HeavilyDamaged : GeoConditionState.Destroyed;
                counts[(int)state]++;
                result.Add(new GeoBuildingEstimate { id = b.id, state = GeoBuildingCondition.Name(state), perHectare = perHectare });
            }
            notes.Add($"Buildings without evidence given a state from the craters around them (estimated): {result.Count} of {unknown} " +
                      $"({counts[1]} intact, {counts[2]} damaged, {counts[3]} heavily damaged, {counts[4]} destroyed).");
            return result;
        }

        // Adds the bake's estimates (combat.json next to the export) to the buildings without evidence.
        public static void ApplyTo(BuildingsData data, string buildingsPath)
        {
            // next to buildings.json (a world package's export, where it lies in the environment folder), else in the
            // environment folder beside it (the app's own layout: <name>.json and <name>_env/)
            var file = GeoCombatFile.Read(buildingsPath);
            if (file == null && !string.IsNullOrEmpty(buildingsPath))
                file = GeoCombatFile.Read(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(buildingsPath)) ?? "",
                    System.IO.Path.GetFileNameWithoutExtension(buildingsPath) + "_env", "environment.json"));
            if (file == null || file.buildings.Count == 0 || !file.Matches(data.origin, null)) return;
            var byId = new Dictionary<string, GeoBuildingEstimate>();
            foreach (var e in file.buildings) if (e?.id != null) byId[e.id] = e;
            foreach (var b in data.buildings)
            {
                if (b?.id == null || b.condition != null && b.condition.State != GeoConditionState.Unknown) continue;
                if (!byId.TryGetValue(b.id, out var e)) continue;
                b.condition = new GeoBuildingCondition
                {
                    state = e.state, basis = "estimated", confidence = "low", asOf = file.created,
                    note = $"GeoWorld combat bake: {e.perHectare:F0} craters per hectare within {Radius:F0} m",
                    sources = new List<string> { "GeoWorld combat bake (crater density)" }
                };
            }
        }

        // 0..1 from the id and the seed (FNV-1a, the same everywhere)
        static float Hash(string id, int seed)
        {
            uint h = 2166136261u ^ (uint)seed;
            foreach (char c in id) { h ^= c; h *= 16777619u; }
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
