using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // Procedural war damage, drawn from a building's condition. Everything here is deterministic: the
    // same building (by its source ID) always breaks the same way, so a rebuild changes nothing.
    //   damaged          the building as it was, soot on walls and roof, most windows broken
    //   heavily damaged  no roof, walls broken down to a ragged edge, rubble inside
    //   destroyed        low wall stumps with a few standing fragments, a mound of rubble
    public static class GeoBuildingDamage
    {
        public static GeoConditionState StateOf(GeoBuilding building) =>
            building != null && building.condition != null ? building.condition.State : GeoConditionState.Unknown;

        // ---------------------------------------------------------------- deterministic randomness

        public static uint Seed(string text)
        {
            uint hash = 2166136261u; // FNV-1a
            if (!string.IsNullOrEmpty(text))
                foreach (char ch in text) { hash ^= ch; hash *= 16777619u; }
            return hash;
        }

        static uint Mix(uint seed, int a, int b)
        {
            uint h = seed ^ 0x9E3779B9u;
            h = (h ^ (uint)a) * 0x85EBCA6Bu; h ^= h >> 13;
            h = (h ^ (uint)b) * 0xC2B2AE35u; h ^= h >> 16;
            h *= 0x27D4EB2Fu; h ^= h >> 15;
            return h;
        }

        public static float Hash01(uint seed, int a, int b = 0) => (Mix(seed, a, b) & 0xFFFFFF) / 16777216f;

        // Smooth value noise in [0, 1] on an integer lattice.
        static float Noise(uint seed, float x, float y)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float a = Hash01(seed, ix, iy), b = Hash01(seed, ix + 1, iy), c = Hash01(seed, ix, iy + 1), d = Hash01(seed, ix + 1, iy + 1);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        // ---------------------------------------------------------------- colour

        static readonly Color Soot = new Color(0.12f, 0.11f, 0.10f);
        static readonly Color Dust = new Color(0.34f, 0.32f, 0.29f);
        static readonly Color Charcoal = new Color(0.07f, 0.065f, 0.06f);

        // Whether a patch of the roof of a damaged building is scorched. About half of the damaged buildings have
        // a scorched part, and the scorch spreads over a few metres, so it follows the roof and not single triangles.
        public static bool Scorched(uint seed, Vector2 point)
        {
            if (Hash01(seed, 41) > 0.5f) return false;
            return Noise(seed, point.x * 0.2f + 31f, point.y * 0.2f + 17f) > 0.46f;
        }

        // The colour of a part of a building in this condition, from the colour it would have undamaged.
        public static Color Tint(GeoConditionState state, string part, Color color, uint seed)
        {
            if (state < GeoConditionState.Damaged) return color;
            float variation = 0.88f + 0.24f * Hash01(seed, 17);
            switch (part)
            {
                case "Broken windows":
                    return new Color(0.03f, 0.03f, 0.035f);
                case "Roof":
                    return Color.Lerp(color, Soot, state == GeoConditionState.Damaged ? 0.42f : 0.6f) * variation;
                case "Burnt roof":
                    return Color.Lerp(color, Charcoal, 0.88f) * variation;
                case "Rubble":
                    return Color.Lerp(color, Dust, 0.7f) * variation;
                case "Foundation":
                    return color * 0.82f;
                case "Windows":
                case "Window sills":
                case "Entrance":
                    return color;
                default: // walls and anything else of the building body
                    // what still stands of a ruin is dusty and scorched in patches, not black
                    if (state == GeoConditionState.Destroyed) return Color.Lerp(Color.Lerp(color, Dust, 0.45f), Soot, 0.25f) * variation;
                    // a damaged building keeps its walls, sooty; a heavily damaged one's broken walls are dusty and scorched in
                    // patches like a ruin's (their shades darken the upper parts and the insides): the style's colour taken
                    // 45 % toward soot made them near black
                    if (state == GeoConditionState.HeavilyDamaged) return Color.Lerp(Color.Lerp(color, Dust, 0.35f), Soot, 0.2f) * variation;
                    return Color.Lerp(color, Soot, 0.3f) * variation;
            }
        }

        public static Color Tint(GeoBuilding building, string part, Color color) =>
            Tint(StateOf(building), part, color, Seed(building.sourceId ?? building.name));

        // ---------------------------------------------------------------- geometry

        // Heavily damaged and destroyed buildings do not have the usual walls and roof: they have what is left.
        public static GeoBuildingOutput BuildBroken(GeoBuildingContext c, float foundationDepth, GeoConditionState state)
        {
            var building = c.building;
            var output = new GeoBuildingOutput();
            try
            {
                bool destroyed = state == GeoConditionState.Destroyed;
                uint seed = Seed(building.sourceId ?? building.name);
                foundationDepth = Mathf.Max(0.2f, foundationDepth);
                var foundation = Foundation(c, foundationDepth);

                // wall remnants: every edge in short runs up to the broken top (BrokenTop: one profile around the whole
                // outline, so neighbouring runs meet), with the wall's thickness inward from the outline (the facade's window
                // openings stay in front of it), soot toward the top and inside, plaster gone and brick bare in patches
                var walls = new GeoBuildingMesh();
                float height = c.eaves;
                float cap = destroyed ? height * 0.8f : height; // nothing stands higher than the building did
                var starts = new float[c.outline.Count + 1];
                var corners = new List<float>();
                for (int i = 0; i < c.outline.Count; i++)
                {
                    corners.Add(starts[i]);
                    starts[i + 1] = starts[i] + Vector2.Distance(c.outline[i], c.outline[(i + 1) % c.outline.Count]);
                }
                var broken = new BrokenTop(seed, starts[c.outline.Count], height, Mathf.Clamp(building.floors, 1, 100), destroyed, corners);
                // the outline's winding: its outward side
                float outward = GeoPolygon.Area(c.outline) >= 0 ? 1 : -1;
                float thickness = c.eaves > 9 ? 0.3f : 0.38f;
                for (int i = 0; i < c.outline.Count; i++)
                {
                    Vector2 a = c.outline[i], b = c.outline[(i + 1) % c.outline.Count];
                    float length = starts[i + 1] - starts[i];
                    int runs = Mathf.Max(1, Mathf.RoundToInt(length / 1.5f));
                    var tops = new float[runs + 1];
                    for (int k = 0; k <= runs; k++) tops[k] = Mathf.Min(broken.At(starts[i] + length * k / runs), cap);
                    Vector2 along = (b - a) / Mathf.Max(length, 0.001f);
                    Vector2 inward = -new Vector2(along.y, -along.x) * outward * thickness;
                    for (int k = 0; k < runs; k++)
                    {
                        float top = tops[k], topEnd = tops[k + 1];
                        if (Mathf.Max(top, topEnd) < 0.12f) continue;
                        top = Mathf.Max(top, 0.05f); topEnd = Mathf.Max(topEnd, 0.05f);
                        float s = starts[i] + length * (k + 0.5f) / runs;
                        Vector2 o0 = Vector2.Lerp(a, b, (float)k / runs), o1 = Vector2.Lerp(a, b, (float)(k + 1) / runs);
                        Vector2 i0 = o0 + inward, i1 = o1 + inward;
                        // the wall's shade by stretches of it, not by run: plaster partly gone (lighter), bare brick (warmer),
                        // weathered (darker) over a few metres each
                        float tone = 0.88f + 0.2f * Noise(seed, s / 9f, 41.3f), bare = Noise(seed, s / 7f, 23.7f);
                        Color skin = Color.Lerp(Color.white, bare > 0.5f ? new Color(1.08f, 0.86f, 0.74f) : new Color(1.1f, 1.08f, 1.04f),
                            Mathf.Clamp01(Mathf.Abs(bare - 0.5f) * 4 - 0.6f)) * tone;
                        skin.a = 1;
                        // outer face: as it was below, sootier toward the top (fire and smoke went up and out of the openings)
                        walls.shade = skin;
                        walls.Wall(o0, o1, 0, top, topEnd, Darker(skin, 0.7f));
                        // inner face: burnt out
                        walls.shade = Darker(skin, 0.68f);
                        walls.Wall(i1, i0, 0, topEnd, top);
                        // the broken top: the core of the wall, bare brick or concrete, dusty. The outline is counter-clockwise
                        // (GeoPolygon.Clean), so this winding faces up and the end caps below face out of the wall's end; the
                        // other sides are inside the wall
                        walls.shade = new Color(0.78f, 0.7f, 0.64f) * tone;
                        walls.Triangle(GeoBuildingMesh.V(o0, top), GeoBuildingMesh.V(i0, top), GeoBuildingMesh.V(i1, topEnd));
                        walls.Triangle(GeoBuildingMesh.V(o0, top), GeoBuildingMesh.V(i1, topEnd), GeoBuildingMesh.V(o1, topEnd));
                        // the run's ends only where the wall ends: at the corners and beside a gap (inside a wall they are hidden)
                        if (k == 0 || Mathf.Max(tops[k - 1], tops[k]) < 0.12f) walls.Wall(i0, o0, 0, top, top);
                        if (k == runs - 1 || k + 2 <= runs && Mathf.Max(tops[k + 1], tops[k + 2]) < 0.12f) walls.Wall(o1, i1, 0, topEnd, topEnd);
                        walls.shade = Color.white;
                    }
                }

                var rubble = Rubble(c, seed, destroyed);
                // dust over what is left: the part colour comes from the style, a dark one lifts a little toward concrete dust
                // (GeoCityBuilder multiplies it by the shades)
                output.Add("Foundation", foundation.ToMesh("Footprint foundation"), c.style.foundation, true);
                output.Add("Walls", walls.ToMesh("Wall remnants"), building.wallMaterial ? building.wallMaterial : c.style.walls, true);
                output.Add("Rubble", rubble.ToMesh("Rubble"), c.style.foundation, true);
                if (!destroyed)
                    GeoBuildingGeometry.Facade(c, output, 1f, (edge, at) => Mathf.Min(broken.At(starts[edge] + at), cap), seed);
                return output;
            }
            catch { output.Dispose(); throw; }
        }

        static Color Darker(Color c, float f) => new Color(c.r * f, c.g * f, c.b * f, 1);

        // What still stands of the walls, as a height at s metres around the outline from its first corner. Heavily damaged: the
        // roof gone and the walls mostly up to the top floors, with one to three bays fallen in (6-18 m across, down to a
        // storey or two) and a top that breaks along the storeys; destroyed: stumps of a storey or less, with one to three
        // fragments still standing (a corner more often than not: two walls hold each other up), 3-9 m across and up to 0.8
        // of the walls. One profile around the whole outline, with ragged sides of 2-4 m and an edge ragged by about a metre:
        // a height drawn for every 2.4 m run on its own read as a comb of organ pipes.
        sealed class BrokenTop
        {
            readonly uint seed;
            readonly bool destroyed;
            readonly float perimeter, height, storey;
            readonly List<Vector3> features = new List<Vector3>(); // centre (s), half-width, share of the walls

            public BrokenTop(uint seed, float perimeter, float height, int floors, bool destroyed, List<float> corners)
            {
                this.seed = seed; this.destroyed = destroyed; this.height = height;
                this.perimeter = Mathf.Max(1, perimeter);
                storey = height / Mathf.Max(1, floors);
                int count = 1 + Mathf.FloorToInt(Hash01(seed, 71) * Mathf.Clamp(perimeter / (destroyed ? 40f : 50f), 1, 3));
                for (int k = 0; k < count; k++)
                {
                    float at = destroyed && corners.Count > 0 && Hash01(seed, 73, k) < 0.6f
                        ? corners[Mathf.FloorToInt(Hash01(seed, 74, k) * corners.Count) % corners.Count]
                        : Hash01(seed, 75, k) * this.perimeter;
                    float half = destroyed ? Mathf.Lerp(1.5f, 4.5f, Hash01(seed, 76, k)) : Mathf.Lerp(3f, 9f, Hash01(seed, 77, k));
                    float share = destroyed ? Mathf.Lerp(0.35f, 0.8f, Hash01(seed, 78, k)) : Mathf.Lerp(0f, 0.35f, Hash01(seed, 79, k));
                    features.Add(new Vector3(at, half, share));
                }
            }

            public float At(float s)
            {
                // the line it breaks along: mostly up to the top floors (heavily damaged) or a storey of stumps at most
                float wave = Noise(seed, s / 14f, 3.3f);
                float share = destroyed ? 0.04f + 0.16f * wave * wave : 0.72f + 0.28f * wave;
                foreach (var f in features)
                {
                    float d = Mathf.Abs(Wrap(s - f.x));
                    float t = Mathf.Clamp01((d - f.y) / (2f + 2f * Noise(seed, s / 3f, f.x * 0.37f)));
                    float inside = 1 - t * t * (3 - 2 * t);
                    share = destroyed ? Mathf.Max(share, Mathf.Lerp(share, f.z, inside)) : Mathf.Min(share, Mathf.Lerp(share, f.z, inside));
                }
                float h = share * height;
                // a storey that came down to its slab in stretches: the top steps along the floors
                if (h > storey)
                {
                    float floor = Mathf.Floor(h / storey) * storey;
                    h = floor + (h - floor) * Noise(seed, s / 5f, 17.3f);
                }
                h += (Noise(seed, s / 2.6f, 9.1f) - 0.5f) * Mathf.Min(1.2f, storey * 0.4f);
                return Mathf.Clamp(h, 0, height);
            }

            float Wrap(float d)
            {
                d %= perimeter;
                if (d > perimeter * 0.5f) d -= perimeter;
                if (d < -perimeter * 0.5f) d += perimeter;
                return d;
            }
        }

        static GeoBuildingMesh Foundation(GeoBuildingContext c, float depth)
        {
            var foundation = new GeoBuildingMesh();
            for (int i = 0; i < c.outline.Count; i++)
                foundation.Wall(c.outline[i], c.outline[(i + 1) % c.outline.Count], -depth, 0, 0);
            for (int i = 0; i < c.triangles.Count; i += 3)
            {
                var a = c.outline[c.triangles[i]]; var b = c.outline[c.triangles[i + 1]]; var d = c.outline[c.triangles[i + 2]];
                foundation.Triangle(GeoBuildingMesh.V(a, 0), GeoBuildingMesh.V(d, 0), GeoBuildingMesh.V(b, 0));
                foundation.Triangle(GeoBuildingMesh.V(a, -depth), GeoBuildingMesh.V(b, -depth), GeoBuildingMesh.V(d, -depth));
            }
            return foundation;
        }

        // A mound of rubble over the footprint: highest in the middle, level with the ground at the edge.
        static GeoBuildingMesh Rubble(GeoBuildingContext c, uint seed, bool destroyed)
        {
            var mesh = new GeoBuildingMesh();
            float area = Mathf.Abs(GeoPolygon.Area(c.outline));
            float perimeter = 0;
            for (int i = 0; i < c.outline.Count; i++) perimeter += Vector2.Distance(c.outline[i], c.outline[(i + 1) % c.outline.Count]);
            float inradius = perimeter > 0.01f ? 2 * area / perimeter : 1;
            float peak = destroyed ? Mathf.Clamp(0.45f + Mathf.Sqrt(area) * 0.07f, 0.6f, 2.4f) : Mathf.Clamp(0.25f + Mathf.Sqrt(area) * 0.03f, 0.3f, 1.2f);
            float rise = Mathf.Clamp(inradius * 0.7f, 0.8f, 3f);
            float Height(Vector2 p)
            {
                float edge = Mathf.SmoothStep(0, 1, DistanceToOutline(c.outline, p) / rise);
                // lumps of broken slabs and brick: a coarse swell and a fine jumble on top
                float lumps = 0.4f + 0.75f * Noise(seed, p.x * 0.55f, p.y * 0.55f) + 0.35f * (Noise(seed, p.x * 2.1f + 13f, p.y * 2.1f + 5f) - 0.5f);
                return Mathf.Max(0.02f, peak * edge * lumps);
            }
            for (int t = 0; t < c.triangles.Count; t += 3)
            {
                var a = c.outline[c.triangles[t]]; var b = c.outline[c.triangles[t + 1]]; var d = c.outline[c.triangles[t + 2]];
                float longest = Mathf.Max(Vector2.Distance(a, b), Mathf.Max(Vector2.Distance(b, d), Vector2.Distance(d, a)));
                int levels = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(Mathf.Max(longest / 2f, 1f), 2f)), 0, 4);
                Split(a, b, d, levels, (p0, p1, p2) =>
                {
                    // broken brick, concrete and plaster in patches: lighter and darker, warmer where brick is
                    Vector2 m = (p0 + p1 + p2) / 3;
                    float n = Noise(seed, m.x * 0.9f + 7f, m.y * 0.9f + 3f), roll = Hash01(seed, Mathf.FloorToInt(m.x * 2), Mathf.FloorToInt(m.y * 2) + 500);
                    // the mound under the dust is darker and browner than the clean foundation it is drawn with
                    float v = 0.55f + 0.45f * n;
                    mesh.shade = roll < 0.22f ? new Color(v * 1.05f, v * 0.8f, v * 0.66f) : new Color(v * 0.92f, v * 0.88f, v * 0.82f);
                    mesh.Triangle(GeoBuildingMesh.V(p0, Height(p0)), GeoBuildingMesh.V(p2, Height(p2)), GeoBuildingMesh.V(p1, Height(p1)));
                });
                mesh.shade = Color.white;
            }
            return mesh;
        }

        static void Split(Vector2 a, Vector2 b, Vector2 c, int levels, Action<Vector2, Vector2, Vector2> emit)
        {
            if (levels <= 0) { emit(a, b, c); return; }
            Vector2 ab = (a + b) * 0.5f, bc = (b + c) * 0.5f, ca = (c + a) * 0.5f;
            Split(a, ab, ca, levels - 1, emit); Split(ab, b, bc, levels - 1, emit);
            Split(ca, bc, c, levels - 1, emit); Split(ab, bc, ca, levels - 1, emit);
        }

        static float DistanceToOutline(List<Vector2> outline, Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < outline.Count; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % outline.Count], ab = b - a;
                float t = ab.sqrMagnitude < 1e-6f ? 0 : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }
    }
}
