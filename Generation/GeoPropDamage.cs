using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Street props after the battle. Where the combat bake (or the export) put craters, and around damaged buildings, lamps,
    // poles, signs and signals lean or are snapped, fences lean, lie flat or have gaps, walls are chipped, breached or down
    // to rubble, bus shelters have lost their panels, benches and bins are knocked over, and some props are gone. Anywhere
    // else the plan stays exactly as it was. A finished battle: nothing burns or smokes.
    //
    // The war at a prop is the stronger of the crater density around it (craters within 56 m per hectare: 1 none, 15 full,
    // smoothstep — as WarMap of the environment layer, which strips the trees) and of the nearest damaged building (destroyed
    // 0.95 at its wall, heavily damaged 0.7, damaged 0.35, nothing 30 m out). A crater close by (its radius × 3 + 4 m) is a
    // blast on top of it: it takes what stood in it and throws the rest away from it. The fate is a hash of the prop's id
    // with the seed, so it is the same in every tile (a tile has the craters and buildings of its 96 m margin) and on every
    // rebuild. Fences and walls where there was fighting are cut into their sections (2.5 m fence spans, 4 m wall panels);
    // neighbouring sections mostly share a fate (in 7.5 m runs), so a fence lies flat or has a gap over a few spans, not
    // span by span, and a run falls to one side. Props placed from photos keep their look (the photo shows it); trees and
    // hedges are the environment layer's.
    public static class GeoPropDamage
    {
        public const int Intact = 0, Damaged = 1, Broken = 2, Rubble = 3;

        const float DensityRadius = 56, RuinReach = 30, Cell = 32;
        const float FenceSection = 2.5f, WallSection = 4, Run = 7.5f;

        sealed class Ruin { public Vector2[] outline; public Rect bounds; public float peak; }

        // The fighting around: craters and damaged buildings in 32 m cells.
        public sealed class War
        {
            readonly Dictionary<Vector2Int, List<CraterFeature>> craters = new Dictionary<Vector2Int, List<CraterFeature>>();
            readonly Dictionary<Vector2Int, List<Ruin>> ruins = new Dictionary<Vector2Int, List<Ruin>>();
            public bool None => craters.Count == 0 && ruins.Count == 0;

            // conditions: the buildings' states count (false: the world draws every building intact, applyCondition off)
            public War(EnvironmentData env, BuildingsData buildings, bool conditions = true)
            {
                if (env?.craters != null)
                    foreach (var c in env.craters)
                        if (c != null && GeoData.Finite(c.x) && GeoData.Finite(c.z)) Add(craters, Key(c.x, c.z), c);
                if (conditions && buildings?.buildings != null)
                    foreach (var b in buildings.buildings)
                    {
                        var ruin = MakeRuin(b);
                        if (ruin == null) continue;
                        for (int z = Floor(ruin.bounds.yMin - RuinReach); z <= Floor(ruin.bounds.yMax + RuinReach); z++)
                            for (int x = Floor(ruin.bounds.xMin - RuinReach); x <= Floor(ruin.bounds.xMax + RuinReach); x++)
                                Add(ruins, new Vector2Int(x, z), ruin);
                    }
            }

            // The war at p (0..1), and the blast of the nearest crater close by (0..1) with the way away from it.
            public float At(Vector2 p, out float blast, out Vector2 away)
            {
                blast = 0; away = Vector2.zero;
                int count = 0, reach = Mathf.CeilToInt(DensityRadius / Cell);
                var key = Key(p.x, p.y);
                for (int dz = -reach; dz <= reach; dz++)
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        if (!craters.TryGetValue(new Vector2Int(key.x + dx, key.y + dz), out var list)) continue;
                        foreach (var c in list)
                        {
                            var d = p - new Vector2(c.x, c.z);
                            float distance = d.magnitude;
                            if (distance < DensityRadius) count++;
                            float diameter = GeoData.Finite(c.diameter) && c.diameter > 0 ? Mathf.Min(c.diameter, 40) : 6;
                            float radius = diameter * 1.5f + 4;
                            if (distance >= radius) continue;
                            float b = 1 - distance / radius;
                            if (b > blast) { blast = b; away = distance > 0.01f ? d / distance : Vector2.zero; }
                        }
                    }
                float perHectare = count / (Mathf.PI * DensityRadius * DensityRadius / 10000f);
                float t = Mathf.Clamp01((perHectare - 1) / 14f);
                float war = t * t * (3 - 2 * t);
                if (ruins.TryGetValue(key, out var near))
                    foreach (var ruin in near)
                    {
                        float v = ruin.peak * (1 - Distance(ruin.outline, p) / RuinReach);
                        if (v > war) war = v;
                    }
                return war;
            }

            static Ruin MakeRuin(BuildingFeature b)
            {
                if (b?.condition == null || !GeoData.Finite(b.x) || !GeoData.Finite(b.z)) return null;
                var state = b.condition.State;
                if (state < GeoConditionState.Damaged) return null;
                var points = GeoData.Points(b.footprint);
                Vector2[] outline;
                if (points.Count >= 3) outline = points.Select(q => new Vector2(q.x + b.x, q.z + b.z)).ToArray();
                else
                {
                    var turn = Quaternion.Euler(0, GeoData.Finite(b.rotation) ? b.rotation : 0, 0);
                    float w = (GeoData.Finite(b.width) && b.width > 0 ? b.width : 10) / 2, l = (GeoData.Finite(b.length) && b.length > 0 ? Mathf.Min(b.length, 500) : 10) / 2;
                    outline = new[] { new Vector3(-w, 0, -l), new Vector3(w, 0, -l), new Vector3(w, 0, l), new Vector3(-w, 0, l) }
                        .Select(v => { var r = turn * v; return new Vector2(r.x + b.x, r.z + b.z); }).ToArray();
                }
                var bounds = Rect.MinMaxRect(outline.Min(v => v.x), outline.Min(v => v.y), outline.Max(v => v.x), outline.Max(v => v.y));
                float peak = state >= GeoConditionState.Destroyed ? 0.95f : state >= GeoConditionState.HeavilyDamaged ? 0.7f : 0.35f;
                return new Ruin { outline = outline, bounds = bounds, peak = peak };
            }
        }

        // Applies the war to the plan (removes the props that are gone, cuts fences and walls into sections where there
        // was fighting) and returns how many props it changed. Props drawn by a prefab of the prop catalog only lean or
        // lie (a prefab has no broken model), the preview and baked models take broken shapes. gone: receives the props
        // taken out (the power lines hang their wires toward a pole that is gone).
        public static int Apply(List<PropPlacement> plan, EnvironmentData env, BuildingsData buildings, GenerationSettings settings, List<string> notes,
            List<PropPlacement> gone = null)
        {
            if (plan == null || plan.Count == 0) return 0;
            var war = new War(env, buildings, settings == null || settings.applyCondition);
            if (war.None) return 0;
            return Apply(plan, war, settings, notes, gone);
        }

        public static int Apply(List<PropPlacement> plan, War war, GenerationSettings settings, List<string> notes, List<PropPlacement> gone = null)
        {
            if (plan == null || plan.Count == 0 || war == null || war.None) return 0;
            int seed = settings != null ? settings.decorationSeed : 137;
            var result = new List<PropPlacement>(plan.Count);
            int touched = 0, damaged = 0, broken = 0, removed = 0, total = 0;
            foreach (var p in plan)
            {
                if (!Damageable(p)) { result.Add(p); continue; }
                float level = war.At(GeoPropPlanner.XZ(p.position), out float blast, out Vector2 away);
                if (level <= 0.01f && blast <= 0.01f) { result.Add(p); continue; }
                bool prefab = settings != null && settings.propCatalog && settings.propCatalog.Find(p.kind) != null;
                IEnumerable<PropPlacement> parts = p.segment && (p.kind == "fence" || p.kind == "guard_rail") ? Sections(p, FenceSection)
                    : p.segment && p.kind == "wall" ? Sections(p, WallSection) : new[] { p };
                foreach (var part in parts)
                {
                    total++;
                    if (part != p) level = war.At(GeoPropPlanner.XZ(part.position), out blast, out away);
                    if (!Decide(part, level, blast, away, seed, prefab)) { gone?.Add(part); removed++; touched++; continue; }
                    result.Add(part);
                    if (part.damage == Intact) continue;
                    touched++;
                    if (part.damage == Damaged) damaged++; else broken++;
                }
            }
            plan.Clear();
            plan.AddRange(result);
            if (touched > 0)
                notes?.Add($"Street props after the battle: {damaged} damaged (leaning, panels gone), {broken} broken (snapped, knocked over, flattened, breached), " +
                           $"{removed} gone, of {total} where there was fighting (craters and damaged buildings around them).");
            return touched;
        }

        static bool Damageable(PropPlacement p) =>
            p != null && p.kind != "tree" && p.kind != "hedge" && p.kind != "entrance" && p.source != "manual_photo";

        // A fence or wall piece as its sections, each with its own id.
        static IEnumerable<PropPlacement> Sections(PropPlacement p, float section)
        {
            float length = GeoData.Finite(p.length) && p.length > 0 ? p.length : 1;
            int count = Mathf.Max(1, Mathf.RoundToInt(length / section));
            if (count == 1) { yield return p; yield break; }
            float yaw = (GeoData.Finite(p.yaw) ? p.yaw : 0) * Mathf.Deg2Rad;
            var along = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
            float piece = length / count;
            for (int k = 0; k < count; k++)
                yield return new PropPlacement
                {
                    id = p.id + "/" + k, kind = p.kind, source = p.source, evidence = p.evidence, rotationSource = p.rotationSource,
                    photoReference = p.photoReference, position = p.position + along * ((k + 0.5f) * piece - length / 2), yaw = p.yaw,
                    height = p.height, width = p.width, length = piece, segment = true
                };
        }

        // What each kind does: more or less often gone, broken or damaged than the rest.
        static void Factors(string kind, out float gone, out float broken, out float damaged)
        {
            switch (kind)
            {
                case "street_lamp": gone = 0.5f; broken = 1; damaged = 1.4f; break;
                case "power_pole": gone = 0.35f; broken = 0.9f; damaged = 1.6f; break;
                // the glass goes first: a shelter keeps its frame long after its panels
                case "shelter": gone = 0.3f; broken = 1; damaged = 2.6f; break;
                case "bus_stop": case "post_box": case "traffic_signals": gone = 0.8f; broken = 1.1f; damaged = 1.2f; break;
                case "bench": case "waste_disposal": gone = 1.3f; broken = 1.3f; damaged = 0.8f; break;
                case "wall": gone = 0.6f; broken = 1; damaged = 1.2f; break;
                case "guard_rail": gone = 0.6f; broken = 0.8f; damaged = 1.3f; break;
                case "gate": gone = 0.6f; broken = 1.2f; damaged = 1; break;
                default: gone = 1; broken = 1; damaged = 1; break;
            }
        }

        // Decides the fate of one prop or section; false when it is gone.
        static bool Decide(PropPlacement p, float war, float blast, Vector2 away, int seed, bool prefab)
        {
            Factors(p.kind, out float fg, out float fb, out float fd);
            float gone = (0.16f * war + 0.65f * blast * blast) * fg;
            float broken = (0.2f * war + 0.3f * blast) * fb;
            float damaged = (0.32f * war + 0.3f * blast) * fd;
            float sum = gone + broken + damaged;
            if (sum > 0.95f) { float k = 0.95f / sum; gone *= k; broken *= k; damaged *= k; }

            // sections of one fence share their fate in runs: the same draw for most of a 7.5 m run
            string run = null;
            float r = GeoPropPlanner.Sample(p.id, seed, 41);
            if (p.segment)
            {
                run = "war/" + p.evidence + "/" + Mathf.FloorToInt(p.position.x / Run) + "/" + Mathf.FloorToInt(p.position.z / Run);
                if (GeoPropPlanner.Sample(p.id, seed, 42) < 0.7f) r = GeoPropPlanner.Sample(run, seed, 43);
            }
            if (r < gone)
            {
                // a wall leaves its rubble (a prefab wall has no rubble shape: it goes)
                if (p.kind != "wall" || prefab) return false;
                p.damage = Rubble;
                return true;
            }
            if (r >= gone + broken + damaged) return true;
            p.damage = r < gone + broken ? Broken : Damaged;

            float h1 = GeoPropPlanner.Sample(p.id, seed, 44), h2 = GeoPropPlanner.Sample(p.id, seed, 45);
            float yaw = GeoData.Finite(p.yaw) ? p.yaw : 0;
            // where it falls: away from the blast, or as it happened
            float toward = blast > 0.03f && away.sqrMagnitude > 0.5f ? Mathf.Atan2(away.x, away.y) * Mathf.Rad2Deg + (h2 - 0.5f) * 60 : h2 * 360;
            bool broke = p.damage == Broken;
            switch (p.kind)
            {
                case "fence": case "guard_rail":
                {
                    // across the line, the run to one side
                    float side = run != null ? (GeoPropPlanner.Sample(run, seed, 46) < 0.5f ? 1 : -1) : 1;
                    if (blast > 0.03f && away.sqrMagnitude > 0.5f)
                    {
                        float a = yaw * Mathf.Deg2Rad;
                        side = Vector2.Dot(away, new Vector2(Mathf.Cos(a), -Mathf.Sin(a))) >= 0 ? 1 : -1;
                    }
                    p.leanToward = yaw + 90 * side;
                    p.lean = broke ? 76 + h1 * 11 : p.kind == "fence" ? 12 + h1 * 26 : 10 + h1 * 20;
                    break;
                }
                case "wall":
                    // a panel knocked flat now and then; otherwise chipped or breached (the shape is the baker's); a prefab
                    // wall has no broken shapes: knocked flat or tilted
                    if ((broke && h1 < 0.3f) || (broke && prefab)) { p.leanToward = yaw + (h2 < 0.5f ? 90 : -90); p.lean = 82 + h2 * 6; }
                    else if (prefab) { p.leanToward = yaw + (h2 < 0.5f ? 90 : -90); p.lean = 4 + h1 * 8; }
                    break;
                case "street_lamp": case "power_pole":
                    if (broke && !prefab) { p.yaw = toward; p.lean = 0; }
                    else { p.leanToward = toward; p.lean = broke ? 78 + h1 * 8 : 6 + h1 * 16; }
                    break;
                case "shelter":
                    // panels gone or the roof down (the baker's shapes); a prefab only leans a little
                    if (prefab) { p.leanToward = toward; p.lean = broke ? 12 + h1 * 10 : 2 + h1 * 3; }
                    break;
                case "gate":
                    if (!broke || prefab) { p.leanToward = toward; p.lean = broke ? 80 + h1 * 6 : 10 + h1 * 20; }
                    break;
                case "bench":
                    // on its back or on its face; a damaged one has lost its backrest and sagged
                    if (broke) { p.leanToward = yaw + (h1 < 0.6f ? 180 : 0); p.lean = 92 + h2 * 8; }
                    else { p.leanToward = toward; p.lean = 3 + h1 * 7; }
                    break;
                case "waste_disposal":
                    if (broke) { p.leanToward = toward; p.lean = 88 + h1 * 4; }
                    else { p.yaw = yaw + (h1 < 0.5f ? 1 : -1) * (20 + h2 * 50); p.leanToward = toward; p.lean = 3 + h1 * 5; }
                    break;
                default:
                    // signs, signals, bollards, stands: bent at the foot, or lying
                    p.leanToward = toward;
                    p.lean = broke ? 74 + h1 * 12 : 6 + h1 * 19;
                    break;
            }
            return true;
        }

        // The prop's rotation: its yaw, then its lean toward leanToward (degrees from the vertical, about its foot).
        public static Quaternion Rotation(PropPlacement p)
        {
            var yaw = Quaternion.Euler(0, GeoData.Finite(p.yaw) ? p.yaw : 0, 0);
            if (!GeoData.Finite(p.lean) || p.lean <= 0 || !GeoData.Finite(p.leanToward)) return yaw;
            float a = p.leanToward * Mathf.Deg2Rad;
            var toward = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            return Quaternion.AngleAxis(p.lean, Vector3.Cross(Vector3.up, toward)) * yaw;
        }

        // How far a prop lying on its side must be lifted so that it lies on the ground instead of in it (local bounds of
        // its model, scaled, turned by rotation). Standing and leaning props keep their foot in the ground.
        public static float Lift(Quaternion rotation, Bounds local, Vector3 scale, float lean)
        {
            if (!(lean > 45)) return 0;
            float low = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(i % 2 == 0 ? local.min.x : local.max.x, (i / 2) % 2 == 0 ? local.min.y : local.max.y, i / 4 == 0 ? local.min.z : local.max.z);
                low = Mathf.Min(low, (rotation * Vector3.Scale(corner, scale)).y);
            }
            return Mathf.Max(0, -low - 0.03f);
        }

        // One of four shapes of a broken prop, by its id (0.125 .. 0.875).
        public static float Variant(PropPlacement p, int seed) => p.damage == Intact ? 0 : (Mathf.Floor(GeoPropPlanner.Sample(p.id, seed, 47) * 4) + 0.5f) / 4;

        // The model a prop is drawn with: its kind, and its damage and shape where they change the model.
        public static string ModelKey(PropPlacement p, int seed) => p.damage == Intact ? p.kind : p.kind + "|" + p.damage + "|" + Variant(p, seed).ToString("F3");

        // A broken lamp or pole lies along its local z: its length is its height.
        public static bool Fallen(PropPlacement p) => p.damage == Broken && !(p.lean > 0) && (p.kind == "street_lamp" || p.kind == "power_pole");

        static int Floor(float v) => Mathf.FloorToInt(v / Cell);
        static Vector2Int Key(float x, float z) => new Vector2Int(Floor(x), Floor(z));

        static void Add<T>(Dictionary<Vector2Int, List<T>> cells, Vector2Int key, T item)
        {
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<T>();
            list.Add(item);
        }

        // From p to the outline (0 inside).
        static float Distance(Vector2[] outline, Vector2 p)
        {
            bool inside = false;
            float best = float.MaxValue;
            for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            {
                Vector2 a = outline[j], b = outline[i];
                if ((b.y > p.y) != (a.y > p.y) && p.x < (a.x - b.x) * (p.y - b.y) / (a.y - b.y) + b.x) inside = !inside;
                var ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, (p - (a + ab * t)).sqrMagnitude);
            }
            return inside ? 0 : Mathf.Sqrt(best);
        }
    }
}
