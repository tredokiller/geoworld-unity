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
                    return Color.Lerp(color, Soot, state == GeoConditionState.Damaged ? 0.3f : 0.45f) * variation;
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

                // wall remnants: every edge in short runs, each as high as it still stands
                var walls = new GeoBuildingMesh();
                var tops = new float[c.outline.Count][];
                var lengths = new float[c.outline.Count];
                float height = c.eaves;
                for (int i = 0; i < c.outline.Count; i++)
                {
                    Vector2 a = c.outline[i], b = c.outline[(i + 1) % c.outline.Count];
                    float length = Vector2.Distance(a, b);
                    lengths[i] = length;
                    int runs = Mathf.Max(1, Mathf.RoundToInt(length / 2.4f));
                    tops[i] = new float[runs];
                    for (int k = 0; k < runs; k++)
                    {
                        float fraction = RemainingFraction(seed, i, k, destroyed);
                        float cap = destroyed ? height * 0.8f : height; // nothing stands higher than the building did
                        tops[i][k] = Mathf.Min(height * fraction, cap);
                        float t0 = (float)k / runs, t1 = (float)(k + 1) / runs;
                        float top = tops[i][k];
                        if (top < 0.12f) continue;
                        // a slanted top breaks the straight line of a run
                        float topEnd = Mathf.Clamp(top * (0.78f + 0.4f * Hash01(seed, i * 31 + k, 5)), 0.1f, cap);
                        Vector2 p0 = Vector2.Lerp(a, b, t0), p1 = Vector2.Lerp(a, b, t1);
                        walls.Wall(p0, p1, 0, top, topEnd);
                        walls.Wall(p1, p0, 0, topEnd, top); // the inner face
                    }
                }

                var rubble = Rubble(c, seed, destroyed);
                output.Add("Foundation", foundation.ToMesh("Footprint foundation"), c.style.foundation, true);
                output.Add("Walls", walls.ToMesh("Wall remnants"), building.wallMaterial ? building.wallMaterial : c.style.walls, true);
                output.Add("Rubble", rubble.ToMesh("Rubble"), c.style.foundation, true);
                if (!destroyed)
                    GeoBuildingGeometry.Facade(c, output, 1f, (edge, at) => tops[edge][Mathf.Clamp(Mathf.FloorToInt(at / Mathf.Max(0.01f, lengths[edge]) * tops[edge].Length), 0, tops[edge].Length - 1)], seed);
                return output;
            }
            catch { output.Dispose(); throw; }
        }

        // How much of a wall run is still standing, 0..1.
        static float RemainingFraction(uint seed, int edge, int run, bool destroyed)
        {
            float n = Noise(seed, edge * 3.7f, run * 0.9f);
            float roll = Hash01(seed, edge * 97 + run, 3);
            if (destroyed)
            {
                if (roll < 0.14f) return Mathf.Lerp(0.35f, 0.8f, Hash01(seed, edge * 53 + run, 4)); // a fragment still standing
                return Mathf.Lerp(0.0f, 0.2f, n * n);
            }
            if (roll < 0.2f) return Mathf.Lerp(0.15f, 0.4f, Hash01(seed, edge * 53 + run, 4)); // a run that came down
            return Mathf.Lerp(0.45f, 1.0f, n);
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
                return Mathf.Max(0.02f, peak * edge * (0.4f + 0.95f * Noise(seed, p.x * 0.55f, p.y * 0.55f)));
            }
            for (int t = 0; t < c.triangles.Count; t += 3)
            {
                var a = c.outline[c.triangles[t]]; var b = c.outline[c.triangles[t + 1]]; var d = c.outline[c.triangles[t + 2]];
                float longest = Mathf.Max(Vector2.Distance(a, b), Mathf.Max(Vector2.Distance(b, d), Vector2.Distance(d, a)));
                int levels = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(Mathf.Max(longest / 2f, 1f), 2f)), 0, 3);
                Split(a, b, d, levels, (p0, p1, p2) =>
                    mesh.Triangle(GeoBuildingMesh.V(p0, Height(p0)), GeoBuildingMesh.V(p2, Height(p2)), GeoBuildingMesh.V(p1, Height(p1))));
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
