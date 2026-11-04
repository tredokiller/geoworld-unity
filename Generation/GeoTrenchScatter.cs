using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Trenches of a finished battle, in the style the war dug them (not traced from imagery: no detector finds them
    // reliably at 0.5 m, and a live position must never be reproduced). Where the fighting was heavy (craters per hectare
    // around a tree line, the combat bake's), a tree line gets with a probability growing with that density (45 % + 5 % per crater
    // per hectare):
    //  - a fire trench along its edge on one side (the side the enemy was, from a hash), 1-3 m outside the trees (the tree
    //    line's own half-width: an OSM row's 4 m, a narrow wood's width from its outline, a ground-map strip's from its
    //    distance to open ground), zigzag:
    //    every 7-10 m its line steps 1.5-2.5 m to the other side of the run (a broken trace, as the Ukrainian MoD manual has
    //    it: straight runs of 20-30 m by hand, now mostly zigzag), 1 m wide and 1.6-2 m deep as dug;
    //  - dugouts every 35-60 m: a short pit of 3.5-5 m into the trees, wider and deeper (its log roof gone);
    //  - one or two communication trenches from the fire trench back through the trees and 40-150 m into the field behind,
    //    meandering.
    // Everything abandoned for one to three years (worn 0.6-1: slumped walls, half filled, grown over), kept off buildings,
    // water and roads (cut where it would cross them). A hash of the tree line decides all of it, so a new bake of the same
    // area gives the same trenches.
    public static class GeoTrenchScatter
    {
        const float MinLength = 60, Step = 2;

        public static List<TrenchFeature> Scatter(List<GeoTreeLine> treeLines, List<CraterFeature> craters, GeoCombatMask blocked, GeoCombatMask roads,
            GeoCombatSettings combat, List<string> notes)
        {
            var result = new List<TrenchFeature>();
            if (craters.Count == 0) return result;
            var grid = new GeoCraterGrid(craters, 25);
            int lines = 0, used = 0, fire = 0, dugouts = 0, links = 0;
            for (int li = 0; li < treeLines.Count; li++)
            {
                var line = treeLines[li]?.points;
                if (line == null || line.Count < 2) continue;
                float length = GeoTreeLines.Length(line);
                if (length < MinLength) continue;
                lines++;
                // how heavy the fighting was along it: craters within 60 m of its points, per hectare
                int seen = 0, samples = 0;
                for (float s = 0; s < length; s += 20, samples++)
                {
                    var p = At(line, s);
                    seen += grid.Count(p.x, p.y, 60);
                }
                float perHectare = (float)seen / Mathf.Max(1, samples) / (Mathf.PI * 60 * 60 / 10000f);
                if (perHectare < combat.trenchFromPerHectare) continue;
                float chance = Mathf.Clamp01((0.45f + 0.05f * perHectare) * combat.trenchShare);
                int seed = combat.seed * 7919 + li;
                if (GeoCraterScatter.Hash01(li, 0, 101, seed) >= chance) continue;
                used++;
                float side = GeoCraterScatter.Hash01(li, 0, 102, seed) < 0.5f ? -1 : 1;
                float offset = (side > 0 ? treeLines[li].left : treeLines[li].right) + 1 + 2 * GeoCraterScatter.Hash01(li, 0, 103, seed);
                float from = length * 0.3f * GeoCraterScatter.Hash01(li, 0, 104, seed), to = length * (0.7f + 0.3f * GeoCraterScatter.Hash01(li, 0, 105, seed));
                float age = 0.6f + 0.4f * GeoCraterScatter.Hash01(li, 0, 106, seed);
                // the fire trench: the offset line with a zigzag across it
                var run = new List<Vector2>();
                float stepAlong = 7 + 3 * GeoCraterScatter.Hash01(li, 0, 107, seed), zig = 1.5f + GeoCraterScatter.Hash01(li, 0, 108, seed);
                int k = 0;
                for (float s = from; s <= to; s += stepAlong, k++)
                {
                    var p = At(line, s); var n = Normal(line, s);
                    float across = side * offset + ((k & 1) == 0 ? -zig : zig) * 0.5f;
                    run.Add(p + n * across);
                }
                foreach (var piece in Clear(run, blocked, roads, GeoTrenchProfile.Reach(1.0f, age)))
                {
                    result.Add(Trench(piece, 1.0f, 1.6f + 0.4f * GeoCraterScatter.Hash01(li, k, 109, seed), age, GeoTrenchKind.Fire));
                    fire++;
                }
                // dugouts into the trees, and communication trenches back through them
                float next = from + 15 + 20 * GeoCraterScatter.Hash01(li, 1, 110, seed);
                int d = 0;
                while (next < to - 10)
                {
                    var p = At(line, next); var n = Normal(line, next);
                    var mouth = p + n * side * offset;
                    var end = mouth - n * side * (3.5f + 1.5f * GeoCraterScatter.Hash01(li, d, 111, seed));
                    // the whole pit, its end and its rounded ends clear of buildings, water and roads
                    bool clear = true;
                    foreach (float t in new[] { -0.35f, 0f, 0.25f, 0.5f, 0.75f, 1f, 1.25f })
                    {
                        var q = Vector2.LerpUnclamped(mouth, end, t);
                        clear &= !blocked.At(q.x, q.y) && !roads.At(q.x, q.y);
                    }
                    if (clear)
                    {
                        result.Add(Trench(new List<Vector2> { mouth, end }, 2.2f, 2.0f, age, GeoTrenchKind.Dugout));
                        dugouts++;
                    }
                    next += 35 + 25 * GeoCraterScatter.Hash01(li, d, 112, seed);
                    d++;
                }
                int count = (to - from) > 120 ? 1 + (GeoCraterScatter.Hash01(li, 2, 113, seed) < 0.4f ? 1 : 0) : 0;
                for (int c = 0; c < count; c++)
                {
                    float at = Mathf.Lerp(from, to, (c + 0.5f) / count + 0.15f * (GeoCraterScatter.Hash01(li, c, 114, seed) - 0.5f));
                    var p = At(line, at); var n = Normal(line, at);
                    var start = p + n * side * offset;
                    float back = 40 + 110 * GeoCraterScatter.Hash01(li, c, 115, seed);
                    var path = new List<Vector2> { start };
                    var dir = -n * side; float turn = 0;
                    for (float s = Step * 3; s <= back + offset * 2; s += Step * 3)
                    {
                        turn = Mathf.Clamp(turn + (GeoCraterScatter.Hash01(li, c * 1000 + (int)s, 116, seed) - 0.5f) * 0.5f, -0.6f, 0.6f);
                        dir = Rotate(dir, turn * 0.25f);
                        path.Add(path[path.Count - 1] + dir * Step * 3);
                    }
                    foreach (var piece in Clear(path, blocked, roads, GeoTrenchProfile.Reach(0.9f, age)))
                    {
                        result.Add(Trench(piece, 0.9f, 1.5f, age, GeoTrenchKind.Communication));
                        links++;
                    }
                }
            }
            notes.Add($"Trenches of the battle (in the style of, not traced): {used} of {lines} tree lines under heavy fire got {fire} fire trench runs, " +
                      $"{dugouts} dugouts and {links} communication trenches.");
            return result;
        }

        static TrenchFeature Trench(List<Vector2> points, float width, float depth, float age, int kind)
        {
            var flat = new float[points.Count * 2];
            for (int i = 0; i < points.Count; i++) { flat[2 * i] = points[i].x; flat[2 * i + 1] = points[i].y; }
            return new TrenchFeature { points = flat, width = width, depth = depth, age = age, kind = kind, source = GeoCraterSource.Scattered };
        }

        // The line cut where it would cross a building, water or a road (with its groove and spoil banks, reach either side
        // of it, off the carriageway); pieces shorter than 6 m are dropped.
        static IEnumerable<List<Vector2>> Clear(List<Vector2> line, GeoCombatMask blocked, GeoCombatMask roads, float reach)
        {
            bool Free(Vector2 p, Vector2 across) =>
                !blocked.At(p.x, p.y) && !roads.At(p.x, p.y) && !roads.At(p.x + across.x * reach, p.y + across.y * reach) &&
                !roads.At(p.x - across.x * reach, p.y - across.y * reach);
            var piece = new List<Vector2>();
            for (int i = 0; i < line.Count; i++)
            {
                var p = line[i];
                var d = (line[Mathf.Min(i + 1, line.Count - 1)] - line[Mathf.Max(i - 1, 0)]).normalized;
                var across = new Vector2(-d.y, d.x);
                bool free = Free(p, across), span = true;
                if (free && piece.Count > 0)
                {
                    // the span from the previous point too
                    var q = piece[piece.Count - 1];
                    for (float t = 0.25f; t < 1 && span; t += 0.25f) span = Free(Vector2.Lerp(q, p, t), across);
                }
                if (free && span) { piece.Add(p); continue; }
                if (Length(piece) >= 6) yield return piece;
                piece = new List<Vector2>();
                // a free point after a blocked span begins the next piece
                if (free) piece.Add(p);
            }
            if (Length(piece) >= 6) yield return piece;
        }

        static float Length(List<Vector2> line)
        {
            float length = 0;
            for (int i = 1; i < line.Count; i++) length += Vector2.Distance(line[i - 1], line[i]);
            return length;
        }

        static Vector2 At(List<Vector2> line, float s)
        {
            for (int i = 1; i < line.Count; i++)
            {
                var a = line[i - 1]; var b = line[i];
                float l = Vector2.Distance(a, b);
                if (s <= l || i == line.Count - 1) return Vector2.Lerp(a, b, l > 0 ? Mathf.Clamp01(s / l) : 0);
                s -= l;
            }
            return line[line.Count - 1];
        }

        // the left normal of the line at s
        static Vector2 Normal(List<Vector2> line, float s)
        {
            for (int i = 1; i < line.Count; i++)
            {
                var a = line[i - 1]; var b = line[i];
                float l = Vector2.Distance(a, b);
                if (s <= l || i == line.Count - 1)
                {
                    var d = l > 0 ? (b - a) / l : Vector2.right;
                    return new Vector2(-d.y, d.x);
                }
                s -= l;
            }
            return Vector2.up;
        }

        static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
        }
    }
}
