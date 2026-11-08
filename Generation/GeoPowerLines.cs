using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Power lines of a battlefield's settlements, generated and labelled so: the export has none (OpenStreetMap maps few of
    // the 0.4 kV lines that run along every street of houses in a Ukrainian town or village), and after the battle a street
    // reads by its wires — slack between leaning poles, snapped and hanging down, a pole broken off with its wires on the
    // ground.
    //
    // Poles: concrete (GeoPowerPole: an СВ-95 with a steel crossarm and four pin insulators), 30–38 m apart on one side of a
    // residential, living, unclassified, tertiary, secondary or primary street (the side by a hash of the road), 1.8 m past
    // its edge, nudged along the street off whatever stands there; only where a house stands within 35 m (flats have their
    // cables underground). They are power_pole placements (source generated), so the war leans, snaps or takes them like the
    // other props (GeoPropDamage). The wires are hung after that from what is left of the poles (Build): four per span (three
    // phases and the neutral) from the necks of the crossarm's insulators, sagging 0.5–0.9 m, slacker the more the war (up to
    // ~3 m, each wire its own); one that reaches a broken pole falls from the standing pole to the ground and lies on to the
    // broken pole's top, torn off its insulators, one that reaches a pole that is gone ends on the ground part of the way;
    // where there was fighting single wires are snapped and hang down from both poles. A span belongs to the tile of its
    // first pole; its other pole may stand in the next tile (the planner's `beyond`: the same plan and fate in both tiles).
    // Drawn by the GeoWorld/Wire shader (a ribbon facing the camera, at least 1.5 pixels wide and as faint as the wire is
    // thin, so far wires fade instead of flickering), in chunks like the baked props.
    public static class GeoPowerLines
    {
        public const string Marker = "/power/", ShaderName = "GeoWorld/Wire";
        const float Spacing = 30, SpacingRange = 8, Edge = 1.8f, HouseReach = 35, LongestSpan = 50, Cell = 32;
        static readonly HashSet<string> Streets = new HashSet<string> { "residential", "living_street", "unclassified", "tertiary", "secondary", "primary" };

        sealed class Pole { public PropPlacement placement; public bool standing, inside; public string road; public int number; }

        sealed class Chunk
        {
            public readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> triangles = new List<int>();
            public int spans;
        }

        // ------------------------------------------------------------------ poles

        // The poles the streets of houses would have, in road order (id: the road's id, Marker, the number along the road),
        // on the ground plane (y 0); around: where to look (a tile with its margin).
        public static List<PropPlacement> Candidates(EnvironmentData env, BuildingsData buildings, int seed, GeoBounds around)
        {
            var result = new List<PropPlacement>();
            if (env?.roads == null || around == null) return result;
            var houses = new Dictionary<Vector2Int, List<Vector2>>();
            if (buildings?.buildings != null)
                foreach (var b in buildings.buildings)
                {
                    if (b == null || b.category != "house" || !GeoData.Finite(b.x) || !GeoData.Finite(b.z)) continue;
                    var key = Key(b.x, b.z);
                    if (!houses.TryGetValue(key, out var list)) houses[key] = list = new List<Vector2>();
                    list.Add(new Vector2(b.x, b.z));
                }
            if (houses.Count == 0) return result;
            foreach (var road in env.roads.Where(r => r != null && !r.bridge && !r.tunnel && r.layer == 0 && r.type != null && Streets.Contains(r.type))
                         .OrderBy(r => r.id, StringComparer.Ordinal))
            {
                var points = GeoData.Points(road.points);
                if (points.Count < 2) continue;
                float side = GeoPropPlanner.Sample(road.id, seed, 71) < 0.5f ? 1 : -1;
                float spacing = Spacing + SpacingRange * GeoPropPlanner.Sample(road.id, seed, 72);
                float offset = (GeoData.Finite(road.width) && road.width > 0 ? Mathf.Min(road.width, 30) : 5) / 2 + Edge;
                float total = 0, next = spacing * (0.2f + 0.6f * GeoPropPlanner.Sample(road.id, seed, 73));
                int number = 0;
                for (int i = 1; i < points.Count; i++)
                {
                    Vector3 a = points[i - 1], b = points[i];
                    float length = Vector3.Distance(a, b);
                    if (length < 0.01f) continue;
                    var tangent = (b - a) / length;
                    var normal = new Vector3(-tangent.z, 0, tangent.x);
                    while (next < total + length)
                    {
                        var centre = Vector3.Lerp(a, b, (next - total) / length);
                        string id = road.id + Marker + number++;
                        next += spacing + (GeoPropPlanner.Sample(id, seed, 74) - 0.5f) * 4;
                        var at = centre + normal * (offset * side);
                        if (!around.Contains(at.x, at.z) || !NearHouse(houses, new Vector2(at.x, at.z))) continue;
                        result.Add(new PropPlacement
                        {
                            id = id, kind = "power_pole", source = "generated", evidence = road.id, rotationSource = "line",
                            position = new Vector3(at.x, 0, at.z), yaw = Mathf.Atan2(tangent.x, tangent.z) * Mathf.Rad2Deg
                        });
                    }
                    total += length;
                }
            }
            return result;
        }

        // Moves a pole along its street off whatever stands where it would be (0, ±2, ±4 m); false when nowhere is free.
        public static bool Nudge(PropPlacement pole, Func<Vector3, bool> free)
        {
            float a = pole.yaw * Mathf.Deg2Rad;
            var along = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
            var start = pole.position;
            foreach (float d in new[] { 0f, 2f, -2f, 4f, -4f })
            {
                var p = start + along * d;
                if (!free(p)) continue;
                pole.position = p;
                return true;
            }
            return false;
        }

        static bool NearHouse(Dictionary<Vector2Int, List<Vector2>> houses, Vector2 p)
        {
            var key = Key(p.x, p.y);
            int reach = Mathf.CeilToInt(HouseReach / Cell);
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                    if (houses.TryGetValue(new Vector2Int(key.x + dx, key.y + dz), out var list))
                        foreach (var h in list)
                            if ((h - p).sqrMagnitude < HouseReach * HouseReach) return true;
            return false;
        }

        static Vector2Int Key(float x, float z) => new Vector2Int(Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell));

        // ------------------------------------------------------------------ wires

        // The wires of this tile's spans, from the poles as the war left them: plan (standing), gone (taken out by
        // GeoPropDamage), beyond (the next tiles' poles a span reaches, with their fate) and beyondGone. One object per
        // chunk under group. Returns the spans made.
        public static int Build(List<PropPlacement> plan, List<PropPlacement> gone, List<PropPlacement> beyond, List<PropPlacement> beyondGone,
            GeoPropDamage.War war, HeightField field, Transform group, float chunkSize, Action<Object> save, int seed, List<string> notes)
        {
            var poles = new Dictionary<string, Pole>();
            void Take(List<PropPlacement> list, bool standing, bool inside)
            {
                if (list == null) return;
                foreach (var p in list)
                {
                    if (p == null || p.kind != "power_pole" || p.id == null) continue;
                    int at = p.id.LastIndexOf(Marker, StringComparison.Ordinal);
                    if (at < 0 || !int.TryParse(p.id.Substring(at + Marker.Length), out int number)) continue;
                    if (field != null) p.position.y = field.Sample(p.position.x, p.position.z);
                    poles[p.id] = new Pole { placement = p, standing = standing, inside = inside, road = p.id.Substring(0, at), number = number };
                }
            }
            Take(plan, true, true); Take(gone, false, true); Take(beyond, true, false); Take(beyondGone, false, false);
            if (!poles.Values.Any(p => p.inside)) return 0;
            var shader = GeoAssets.Shader(ShaderName);
            if (!shader) { notes?.Add("Power lines: the " + ShaderName + " shader is missing, the poles stand without wires."); return 0; }
            var material = new Material(shader) { name = "Power line wires", enableInstancing = true };
            save?.Invoke(material);
            Func<float, float, float> ground = (x, z) => field != null ? field.Sample(x, z) : 0;

            float size = Mathf.Max(50, chunkSize);
            var chunks = new Dictionary<Vector2Int, Chunk>();
            int spans = 0, snapped = 0, down = 0, standing = 0;
            foreach (var a in poles.Values.Where(p => p.inside).OrderBy(p => p.road, StringComparer.Ordinal).ThenBy(p => p.number))
            {
                if (a.standing) standing++;
                if (!poles.TryGetValue(a.road + Marker + (a.number + 1), out var b)) continue;
                var pa = a.placement.position;
                float span = Horizontal(b.placement.position - pa);
                if (span > LongestSpan || span < 1) continue;
                var key = new Vector2Int(Mathf.FloorToInt(pa.x / size), Mathf.FloorToInt(pa.z / size));
                if (!chunks.TryGetValue(key, out var chunk)) chunks[key] = chunk = new Chunk();
                foreach (var wire in Span(a, b, span, war, ground, seed, ref snapped, ref down)) AddWire(chunk, wire);
                chunk.spans++;
                spans++;
            }
            foreach (var pair in chunks)
            {
                var c = pair.Value;
                if (c.vertices.Count == 0) continue;
                string name = "Wires " + pair.Key.x + "_" + pair.Key.y;
                var mesh = new Mesh { name = name, indexFormat = c.vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(c.vertices);
                mesh.SetNormals(c.normals);
                mesh.SetUVs(0, c.uvs);
                mesh.SetTriangles(c.triangles, 0);
                mesh.RecalculateBounds();
                // the shader widens far wires to a pixel and more
                var bounds = mesh.bounds; bounds.Expand(2); mesh.bounds = bounds;
                save?.Invoke(mesh);
                var go = new GameObject(name + " (" + c.spans + " spans)");
                go.transform.SetParent(group, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                // gone by about 400 m: the shader has faded them out there
                var lod = go.AddComponent<LODGroup>();
                lod.SetLODs(new[] { new LOD(0.025f, new Renderer[] { renderer }) });
                // measured from the wires (the object stands at the tile root's origin), as if they were 20 m across
                lod.localReferencePoint = go.transform.InverseTransformPoint(mesh.bounds.center);
                lod.size = 20;
            }
            if (spans > 0)
                notes?.Add($"Power lines (generated: the export has none): {standing} poles along streets of houses, {spans} spans of wires" +
                           (snapped + down > 0 ? $"; after the battle {snapped} wires snapped, {down} spans down with a broken or missing pole." : "."));
            return spans;
        }

        // The wires of one span, one from each insulator (GeoPowerPole.Wires), as their poles stand.
        static List<List<Vector3>> Span(Pole a, Pole b, float span, GeoPropDamage.War war, Func<float, float, float> ground, int seed, ref int snapped, ref int down)
        {
            var wires = new List<List<Vector3>>();
            var ta = Attach(a, seed);
            var tb = Attach(b, seed);
            bool fa = ta != null && Lying(a.placement), fb = tb != null && Lying(b.placement);
            string id = a.placement.id + ">" + b.number;
            var mid = (a.placement.position + b.placement.position) / 2;
            float blast = 0, level = war != null && !war.None ? war.At(new Vector2(mid.x, mid.z), out blast, out _) : 0;
            float slack = 0.5f + 0.4f * GeoPropPlanner.Sample(id, seed, 81);
            bool spanDown = false;
            for (int i = 0; i < GeoPowerPole.Wires; i++)
            {
                var wire = new List<Vector3>();
                float r = GeoPropPlanner.Sample(id + "/" + i, seed, 82), r2 = GeoPropPlanner.Sample(id + "/" + i, seed, 83);
                if (ta != null && tb != null && !fa && !fb)
                {
                    // both standing: slacker the more the war, each wire its own; snapped now and then where it was
                    float sag = slack + level * (0.8f + 1.4f * r2 * r2);
                    if (r < 0.28f * level + 0.5f * blast)
                    {
                        float cut = 0.2f + 0.6f * r2;
                        Fall(wire, ta[i], Toward(ta[i], tb[i]), cut * span * 1.02f + 0.5f * sag, ground, null, 0.35f, r);
                        wires.Add(wire);
                        wire = new List<Vector3>();
                        Fall(wire, tb[i], Toward(tb[i], ta[i]), (1 - cut) * span * 1.02f + 0.5f * sag, ground, null, 0.35f, r2);
                        snapped++;
                    }
                    else Hang(wire, ta[i], tb[i], sag, ground);
                }
                else if (ta != null && tb != null)
                {
                    // a pole down with its top: from the standing one to the ground and on to the fallen top, or along the ground
                    if (!fa) Fall(wire, ta[i], Toward(ta[i], tb[i]), span * 1.02f, ground, tb[i], 0.3f, r);
                    else if (!fb) Fall(wire, tb[i], Toward(tb[i], ta[i]), span * 1.02f, ground, ta[i], 0.3f, r);
                    else Lie(wire, ta[i], tb[i], ground, 0.4f, r);
                    spanDown = true;
                }
                else if (ta != null || tb != null)
                {
                    // the other pole is gone: what is left of the wire, on the ground toward where it stood (some taken away)
                    spanDown = true;
                    if (r < 0.3f) continue;
                    var from = ta ?? tb;
                    var to = ta != null ? b.placement.position : a.placement.position;
                    Fall(wire, from[i], Toward(from[i], to), (0.35f + 0.45f * r2) * span, ground, null, 0.4f, r);
                }
                wires.Add(wire);
            }
            if (spanDown) down++;
            return wires;
        }

        // Where the wires are tied on a pole as it stands, leans or lies (null: gone): the necks of its insulators, the same
        // wire on the same side of every pole of the line (GeoPowerPole.WireTops).
        static Vector3[] Attach(Pole pole, int seed) => pole.standing ? GeoPowerPole.WireTops(pole.placement, seed) : null;

        // A pole down on the ground: snapped with its top lying, or (a prefab) knocked flat.
        static bool Lying(PropPlacement p) => GeoPropDamage.Fallen(p) || p.lean > 45;

        // A span between two standing poles: a parabola under the chord, `sag` at its middle, never into the ground.
        static void Hang(List<Vector3> wire, Vector3 a, Vector3 b, float sag, Func<float, float, float> ground)
        {
            int n = Mathf.Clamp(Mathf.CeilToInt(Horizontal(b - a) / 2f), 8, 26);
            for (int k = 0; k <= n; k++)
            {
                float t = k / (float)n;
                var p = Vector3.Lerp(a, b, t) + Vector3.down * (4 * sag * t * (1 - t));
                p.y = Mathf.Max(p.y, ground(p.x, p.z) + 0.05f);
                wire.Add(p);
            }
        }

        // A wire hanging from `from` with `length` of it free, toward `toward`: down to the ground (leaving the insulator
        // steeply, meeting the ground flat), then lying on along the ground, snaking a little, to `end` when given. Too
        // short to reach the ground, it dangles.
        static void Fall(List<Vector3> wire, Vector3 from, Vector2 toward, float length, Func<float, float, float> ground, Vector3? end, float wiggle, float phase)
        {
            var d = new Vector3(toward.x, 0, toward.y);
            var side = new Vector3(-d.z, 0, d.x);
            float h = Mathf.Max(0.05f, from.y - ground(from.x, from.z));
            wire.Add(from);
            if (length <= h * 1.05f)
            {
                for (int k = 1; k <= 6; k++)
                {
                    float u = k / 6f;
                    var p = from + d * (0.25f * length * u * (1 - 0.5f * u)) + Vector3.down * (0.95f * length * u);
                    p.y = Mathf.Max(p.y, ground(p.x, p.z) + 0.03f);
                    wire.Add(p);
                }
                return;
            }
            float reach = Mathf.Clamp(0.35f * (length - h) + 0.3f * h, 0.5f, 0.8f * length);
            if (end.HasValue) reach = Mathf.Min(reach, 0.9f * Horizontal(end.Value - from));
            reach = Mathf.Max(reach, 0.3f);
            float touch = ground(from.x + d.x * reach, from.z + d.z * reach) + 0.03f;
            float used = 0;
            var last = from;
            for (int k = 1; k <= 10; k++)
            {
                float u = k / 10f, w = 1 - (1 - u) * (1 - u);
                var p = from + d * (reach * u);
                p.y = Mathf.Max(Mathf.Lerp(from.y, touch, w), ground(p.x, p.z) + 0.03f);
                used += Vector3.Distance(last, p);
                last = p;
                wire.Add(p);
            }
            if (end.HasValue)
            {
                var e = end.Value;
                var start = last;
                float run = Horizontal(e - start);
                int m = Mathf.Max(1, Mathf.CeilToInt(run / 1.2f));
                var across = new Vector3(-(e.z - start.z), 0, e.x - start.x);
                across = across.sqrMagnitude > 1e-6f ? across.normalized : side;
                for (int k = 1; k <= m; k++)
                {
                    float u = k / (float)m;
                    var p = Vector3.Lerp(start, e, u) + across * (wiggle * Mathf.Sin(u * Mathf.PI) * Mathf.Sin(u * 6.3f + phase * 6.28f));
                    p.y = k == m ? e.y : ground(p.x, p.z) + 0.03f;
                    wire.Add(p);
                }
                return;
            }
            float rest = length - used;
            int count = Mathf.Clamp(Mathf.CeilToInt(rest / 1.2f), 0, 40);
            for (int k = 1; k <= count; k++)
            {
                float s = rest * k / count;
                var p = last + d * s + side * (wiggle * Mathf.Sin(s * 0.8f + phase * 6.28f) * Mathf.Min(1, s / 2));
                p.y = ground(p.x, p.z) + 0.03f;
                wire.Add(p);
            }
        }

        // A wire lying along the ground from a to b, snaking a little.
        static void Lie(List<Vector3> wire, Vector3 a, Vector3 b, Func<float, float, float> ground, float wiggle, float phase)
        {
            int m = Mathf.Clamp(Mathf.CeilToInt(Horizontal(b - a) / 1.2f), 2, 50);
            var across = new Vector3(-(b.z - a.z), 0, b.x - a.x);
            across = across.sqrMagnitude > 1e-6f ? across.normalized : Vector3.right;
            for (int k = 0; k <= m; k++)
            {
                float u = k / (float)m;
                var p = Vector3.Lerp(a, b, u) + across * (wiggle * Mathf.Sin(u * Mathf.PI) * Mathf.Sin(u * 9 + phase * 6.28f));
                p.y = k == 0 ? a.y : k == m ? b.y : ground(p.x, p.z) + 0.03f;
                wire.Add(p);
            }
        }

        // A wire's ribbon: a pair of vertices at each point (sides -1 and +1 in uv.x), the wire's direction as their normal.
        static void AddWire(Chunk c, List<Vector3> raw)
        {
            var points = new List<Vector3>(raw.Count);
            foreach (var p in raw)
                if (GeoData.Finite(p.x) && GeoData.Finite(p.y) && GeoData.Finite(p.z) && (points.Count == 0 || (p - points[points.Count - 1]).sqrMagnitude > 1e-6f))
                    points.Add(p);
            if (points.Count < 2) return;
            for (int i = 0; i < points.Count; i++)
            {
                var tangent = points[Mathf.Min(i + 1, points.Count - 1)] - points[Mathf.Max(i - 1, 0)];
                tangent = tangent.sqrMagnitude > 1e-8f ? tangent.normalized : Vector3.forward;
                int first = c.vertices.Count;
                c.vertices.Add(points[i]); c.vertices.Add(points[i]);
                c.normals.Add(tangent); c.normals.Add(tangent);
                c.uvs.Add(new Vector2(-1, 0)); c.uvs.Add(new Vector2(1, 0));
                if (i == 0) continue;
                int a = first - 2;
                c.triangles.Add(a); c.triangles.Add(a + 1); c.triangles.Add(first + 1);
                c.triangles.Add(a); c.triangles.Add(first + 1); c.triangles.Add(first);
            }
        }

        static Vector2 Toward(Vector3 from, Vector3 to)
        {
            var d = new Vector2(to.x - from.x, to.z - from.z);
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.up;
        }

        static float Horizontal(Vector3 v) => new Vector2(v.x, v.z).magnitude;
    }
}
