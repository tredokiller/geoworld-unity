using UnityEngine;

namespace GeoWorld.Editor
{
    // The shape of the ground next to water, from the distance to the water's edge. Lakes and rivers share it, so that water
    // and land meet the same way everywhere:
    //
    //  - the water's edge is where the ground is at the water level, so the line of the water is the outline of the water;
    //  - inside, the bed falls from the water level to the full depth over a bank of a few metres;
    //  - outside, the ground never rises faster than a gentle slope from the water and never lies lower than a shoulder just
    //    above it, so that a surface that reaches a cell or two beyond the outline has ground over its edge, and the ground then
    //    goes back to its own height, over a distance that grows with the difference (no wall, whatever the height of the bank).
    //
    // Everything is a function of the distance to the water and of the ground's own height, so tiles that shape the same
    // water on their own agree at their common edge.
    public static class GeoShore
    {
        // Slope of the ground outside the water (metres up for each metre away from it).
        public const float OutsideSlope = 0.5f;
        // Metres that low land is raised above the water just outside its edge.
        public const float Shoulder = 0.25f;
        // Metres, at most, that the ground takes to go back to its own height: 10 cells and 2.5 m for each metre that it differs.
        public const float MaxFade = 100;

        // Metres the bed takes to reach its full depth: a bank of about one to three, 4 m at least.
        public static float Bank(float depth) => Mathf.Clamp(depth * 3, 4, 14);

        // How far from the water's edge the ground is brought to the gentle slope; beyond it the ground goes back to its own height.
        public static float Cut(float depth, float cell) => Bank(depth) * OutsideSlope * 2 + cell;

        // Beyond this distance from the edge nothing changes.
        public static float Reach(float depth, float cell) => Cut(depth, cell) + MaxFade;

        static float Smooth(float t) => t * t * (3 - 2 * t);
        // Steepest at 0 and level at 1: the shoulder leaves the water's level at an angle, so that the ground crosses the water
        // along a line. With a smooth start the ground lay within a few centimetres of the water for a metre or more on each side
        // of the edge, a band where the depth buffer cannot tell water from ground and the shore flickers from afar.
        static float Ease(float t) => t * (2 - t);
        // The bed's fall: half of each, so that it leaves the water at an angle too but is nowhere steeper than a third more than
        // the smooth fall (no step of a cell is a wall).
        static float Fall(float t) => 0.5f * (Ease(t) + Smooth(t));

        // The height of the ground at a point with the signed distance s from the water's edge (negative inside the water,
        // positive outside), where the water's level is `level` and the ground's own height is `natural`.
        public static float Shape(float natural, float level, float s, float depth, float cell)
        {
            if (s <= 0)
            {
                float bank = Bank(depth), d = -s;
                float bed = Mathf.Lerp(level, level - depth, d >= bank ? 1 : Fall(d / bank));
                return Mathf.Min(natural, bed);
            }
            float cut = Cut(depth, cell);
            if (s >= cut + MaxFade) return natural;
            // no higher than a gentle bank rising from the water, and no lower than a shoulder just above it
            float shoulder = Shoulder * Ease(Mathf.Min(1, s / (1.5f * cell)));
            float shaped = Mathf.Max(Mathf.Min(natural, level + s * OutsideSlope), level + shoulder);
            // and back to the ground's own height beyond that, over a distance that grows with the difference
            float fade = Mathf.Clamp(10f * cell + 2.5f * Mathf.Abs(natural - shaped), 20f, MaxFade);
            return Mathf.Lerp(shaped, natural, s <= cut ? 0 : Smooth(Mathf.Clamp01((s - cut) / fade)));
        }
    }
}
