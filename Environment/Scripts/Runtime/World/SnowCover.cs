using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Покров снега на земле (0..1) на CPU — SnowCoverAt из Snow.hlsl, бит в бит по шуму (градиентный шум на Jenkins-хеше
    /// Random.hlsl Core RP), без мелкого шума края, который шейдер добавляет у самой камеры. По нему зимой трава, цветы,
    /// опад и мелочь точки взлёта остаются только там, где шейдер оставил землю голой: раньше их было равномерно меньше
    /// на (1 − покров), и на сугробах лежали рыжие пятна листвы и торчала трава.
    /// </summary>
    public static class SnowCover
    {
        // земля, сквозь которую снег не тает раньше (светлее 0.2 по SeasonLuminance)
        public const float NeutralGround = 0.2f;

        // куда дует ветер зимы (SNOW_WIND)
        private static readonly Vector2 Wind = new(0.8f, -0.6f);

        // normal — нормаль рельефа, groundLuminance — SeasonLuminance земли под снегом (линейный цвет), footprint — сколько
        // метров земли в ячейке, по которой решают.
        public static float At(float x, float z, Vector3 normal, float groundLuminance, float snowCover, float footprint)
        {
            if (snowCover <= 0f)
                return 0f;

            float flat = SmoothStep(0.55f, 0.85f, normal.y);

            if (snowCover >= 0.999f)
                return flat;

            float warpX = Noise(x / 47f, z / 47f, 211u) * 18f;
            float warpZ = Noise(x / 47f + 31.7f, z / 47f + 31.7f, 212u) * 18f;
            float px = x + warpX;
            float pz = z + warpZ;
            float driftX = (px * Wind.x + pz * Wind.y) / 1.7f;
            float driftZ = px * -Wind.y + pz * Wind.x;
            // шейдер добавляет мелкие пятна в 400 м от камеры (1 − d/400); трава видна до ~150 м, там их ~0.7
            float field = 0.5f + Noise(driftX / 31f, driftZ / 31f, 213u) * 0.7f +
                          Noise(driftX / 11f + 5.3f, driftZ / 11f + 5.3f, 214u) * 0.35f +
                          Noise(x / 3.9f, z / 3.9f, 215u) * 0.15f * 0.7f;
            float melt = Mathf.Clamp01((1f - snowCover) * 2.5f);
            field -= (Mathf.Max(0f, -normal.z) * 1.6f + (1f - SmoothStep(0.03f, 0.2f, groundLuminance)) * 0.12f) * melt;
            float threshold = 1f - snowCover * 1.1f;
            float edge = Mathf.Max(footprint * 0.04f, 0.015f);
            float band = SmoothStep(threshold - 0.09f - edge, threshold + 0.03f + edge, field);
            return Mathf.Clamp01(band * 1.5f - 0.5f) * flat;
        }

        // SnowNoise: ≈ −0.5..0.5
        private static float Noise(float px, float pz, uint salt)
        {
            float ix = Mathf.Floor(px);
            float iz = Mathf.Floor(pz);
            float fx = px - ix;
            float fz = pz - iz;
            float ux = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
            float uz = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);
            int cx = (int)ix;
            int cz = (int)iz;
            float a = Gradient(cx, cz, salt, fx, fz);
            float b = Gradient(cx + 1, cz, salt, fx - 1f, fz);
            float d = Gradient(cx, cz + 1, salt, fx, fz - 1f);
            float e = Gradient(cx + 1, cz + 1, salt, fx - 1f, fz - 1f);
            return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(d, e, ux), uz);
        }

        // SnowGradient, скалярно с (dx, dz)
        private static float Gradient(int x, int z, uint salt, float dx, float dz)
        {
            uint hash = Jenkins(unchecked((uint)x) ^ Jenkins(unchecked((uint)z) ^ Jenkins(salt)));
            float angle = (hash >> 8) * (6.2831853f / 16777216f);
            return Mathf.Cos(angle) * dx + Mathf.Sin(angle) * dz;
        }

        private static uint Jenkins(uint x)
        {
            unchecked
            {
                x += x << 10;
                x ^= x >> 6;
                x += x << 3;
                x ^= x >> 11;
                x += x << 15;
            }

            return x;
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
