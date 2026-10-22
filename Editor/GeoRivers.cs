using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // What a river is built with, beyond its line: its width and its middle where the export's ground map shows the water, and a
    // water level that does not wobble with the noise of the elevation data.
    public static class GeoRivers
    {
        // How far from the line the water of the map may start, and how wide a stretch of it can be a river (metres).
        public const float Search = 12, Reach = 90;
        // The width and the shift are the median over this many metres, so that one section (a bridge, a tree) does not change them.
        const float MedianMetres = 24;
        // A river does not widen in a step: at most this many metres of width for each metre along it (a pool or a mouth on the
        // map, or a stretch it shows badly, would otherwise be a bulge with square ends).
        const float Grow = 0.5f;

        // Widens the river to the water of the map and moves its line to the middle of that water: samples (and the widths) only,
        // the controls stay as the export has them. `height` is the ground; the level of the water is the ground under the line
        // minus 0.35 m. Returns whether the map said anything about this river.
        public static bool Fit(PathBuild path, GeoWaterMask mask, Func<float, float, float> height, float step)
        {
            var s = path.samples; int n = s.Count;
            if (mask == null || n < 2) return false;
            float own = path.feature.width;
            var width = new float[n]; var shift = new float[n]; var ok = new bool[n]; var normal = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 a = s[Mathf.Max(0, i - 1)], b = s[Mathf.Min(n - 1, i + 1)];
                var t = new Vector2(b.x - a.x, b.z - a.z);
                if (t.sqrMagnitude < 1e-6f) continue;
                t.Normalize(); normal[i] = new Vector2(-t.y, t.x);
                if (mask.Across(new Vector2(s[i].x, s[i].z), normal[i], Search, Reach, out float lo, out float hi))
                { width[i] = hi - lo; shift[i] = (hi + lo) * 0.5f; ok[i] = true; }
            }
            int half = Mathf.Max(2, Mathf.RoundToInt(MedianMetres / Mathf.Max(0.5f, step) / 2));
            var widths = new List<float>(n); var seen = false;
            var spread = new List<float>(); var moves = new List<float>();
            var moved = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                spread.Clear(); moves.Clear();
                for (int j = Mathf.Max(0, i - half); j <= Mathf.Min(n - 1, i + half); j++) if (ok[j]) { spread.Add(width[j]); moves.Add(shift[j]); }
                if (spread.Count > half)
                {
                    spread.Sort(); moves.Sort();
                    widths.Add(Mathf.Min(Reach * 2, Mathf.Max(own, spread[spread.Count / 2])));
                    float m = Mathf.Clamp(moves[moves.Count / 2], -Search, Search);
                    moved[i] = new Vector3(normal[i].x * m, 0, normal[i].y * m);
                    seen = true;
                }
                else widths.Add(own);
            }
            if (!seen) return false;
            for (int i = 1; i < n; i++) widths[i] = Mathf.Min(widths[i], widths[i - 1] + Grow * step);
            for (int i = n - 2; i >= 0; i--) widths[i] = Mathf.Min(widths[i], widths[i + 1] + Grow * step);
            for (int i = 0; i < n; i++)
            {
                var p = s[i] + moved[i];
                p.y = height(p.x, p.z) - 0.35f;
                s[i] = p;
            }
            path.widths = widths;
            float widest = own;
            foreach (float w in widths) widest = Mathf.Max(widest, w);
            path.feature.width = widest; // the box round the river (which tiles it touches) is made from it
            return true;
        }

        // The level of the water along a river without the wobble of the elevation data: the mean over `window` metres (shorter
        // at the ends). The ground is carved down to this level, so that the water is never buried by noise.
        public static void SmoothLevels(List<Vector3> samples, float step, float window)
        {
            int n = samples.Count, k = Mathf.Max(1, Mathf.RoundToInt(window / Mathf.Max(0.5f, step) / 2));
            if (n < 3) return;
            var y = new float[n];
            for (int i = 0; i < n; i++)
            {
                int reach = Mathf.Min(k, Mathf.Min(i, n - 1 - i)); // symmetric, so a slope stays a slope
                float sum = 0;
                for (int j = i - reach; j <= i + reach; j++) sum += samples[j].y;
                y[i] = sum / (2 * reach + 1);
            }
            for (int i = 0; i < n; i++) { var p = samples[i]; p.y = y[i]; samples[i] = p; }
        }
    }
}
