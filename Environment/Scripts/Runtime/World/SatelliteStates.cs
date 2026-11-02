using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Что на земле по спутнику тайла: доли зелени, жёлтого (спелый хлеб, стерня, сухая трава), тёмной земли (пахота) и
    /// голой земли (рыже-бурый/серый сухой грунт, песок, мел), в сумме 1 и плавно. Спутник тайла — один летний снимок
    /// (2048² на 1 км, sRGB), читается копией 512² (байты sRGB); классы — в линейном цвете, в нём и калибровка по 24 тайлам:
    /// зелёность (g − max(r, b)) / g — лес 0.58, луг 0.27, пашня 0.11 (p10 −0.35), голая земля −0.21; яркость — пахота
    /// ~0.05, посевы 0.088, спелое и стерня от 0.13, песок 0.45. Только массивы и своя математика: зовётся из фонового потока.
    /// </summary>
    public static class SatelliteStates
    {
        // без спутника — зелень: как прежний цвет по умолчанию (SampleSatellite), который «голым» не был
        public static readonly Vector4 NoSatellite = new(1f, 0f, 0f, 0f);

        private const float GreenLow = 0.06f;
        private const float GreenHigh = 0.20f;
        private const float DarkLuminance = 0.075f;
        private const float LightLuminance = 0.115f;
        private const float VeryLightLow = 0.28f;
        private const float VeryLightHigh = 0.42f;
        // спелое и стерня светлые (от ~0.13); серо-хаки той же окраски темнее — сухая пашня с сорняками, не солома
        private const float StrawLuminanceLow = 0.11f;
        private const float StrawLuminanceHigh = 0.145f;
        // оттенок (g − b) / (r − b): солома и спелый хлеб ~0.6–0.85, рыже-бурый грунт ~0.3–0.47
        private const float RedHueLow = 0.48f;
        private const float RedHueHigh = 0.60f;
        // насыщенность (max − min) / max: ниже — серое (грунт, пыль), оттенку не верим
        private const float GreyChroma = 0.15f;
        private const float ColourChroma = 0.30f;
        // тёмное и синеватое — тень деревьев и облаков или кромка воды, а не пахота
        private const float ColdShade = -0.1f;
        private const float WarmShade = 0.1f;

        // пахота — грунт, а не песок, мел или бетон (они ярче)
        private const float SoilLuminanceLow = 0.20f;
        private const float SoilLuminanceHigh = 0.30f;

        private static readonly float[] Linear = BuildLinear();

        // Доли в каждом пикселе копии спутника (size²), сглажено 1-2-1. null — спутника нет.
        public static Vector4[] Classify(Color32[] pixels, int size) => Classify(pixels, size, out _);

        // arable — доля пахоты в пикселе (0..1): тёмная земля и голый грунт (не песок и не мел). Луг и голая земля карты с
        // такой долей растут как поле: WorldCover зовёт «лугом» вспаханные поля. Жёлтое сюда не входит: на летнем снимке
        // ровно-жёлтые — сухие поляны в бору и степь у реки, а не стерня (с ним стерня вырастала на лесной поляне).
        public static Vector4[] Classify(Color32[] pixels, int size, out float[] arable)
        {
            arable = null;

            if (pixels == null || pixels.Length < size * size)
                return null;

            int count = size * size;
            Vector4[] states = new Vector4[count];
            arable = new float[count];

            for (int i = 0; i < count; i++)
            {
                Vector4 s = Of(pixels[i], out _, out float luminance);
                states[i] = s;
                arable[i] = Mathf.Clamp01(s.z + s.w * (1f - SmoothStep(SoilLuminanceLow, SoilLuminanceHigh, luminance)));
            }

            Blur(states, size);
            Blur(arable, size);
            return states;
        }

        public static Vector4 Of(Color32 pixel) => Of(pixel, out _, out _);

        public static Vector4 Of(Color32 pixel, out float greenness, out float luminance)
        {
            float r = Linear[pixel.r];
            float g = Linear[pixel.g];
            float b = Linear[pixel.b];
            float max = Mathf.Max(r, Mathf.Max(g, b));
            float min = Mathf.Min(r, Mathf.Min(g, b));
            greenness = (g - Mathf.Max(r, b)) / Mathf.Max(g, 0.02f);
            luminance = 0.2126f * r + 0.7152f * g + 0.0722f * b;

            float green = SmoothStep(GreenLow, GreenHigh, greenness);
            float rest = 1f - green;
            float light = SmoothStep(DarkLuminance, LightLuminance, luminance);
            float warm = SmoothStep(ColdShade, WarmShade, (r - b) / Mathf.Max(max, 0.0001f));
            float yellowShare = r > b
                ? SmoothStep(RedHueLow, RedHueHigh, (g - b) / (r - b)) *
                  SmoothStep(GreyChroma, ColourChroma, (max - min) / Mathf.Max(max, 0.0001f))
                : 0f;
            yellowShare *= SmoothStep(StrawLuminanceLow, StrawLuminanceHigh, luminance) *
                           (1f - SmoothStep(VeryLightLow, VeryLightHigh, luminance));

            float darkPart = rest * (1f - light);
            float dark = darkPart * warm;
            float lightPart = rest * light;
            float yellow = lightPart * yellowShare;
            // тень — к зелени: под ней на снимке чаще трава
            return new Vector4(green + darkPart - dark, yellow, dark, lightPart - yellow);
        }

        // Билинейно, как GroundDressing.SampleSatellite: u, v — 0..1 по тайлу.
        public static Vector4 Sample(Vector4[] states, int size, float u, float v)
        {
            if (states == null)
                return NoSatellite;

            float fx = Mathf.Clamp(u * size - 0.5f, 0f, size - 1.001f);
            float fy = Mathf.Clamp(v * size - 0.5f, 0f, size - 1.001f);
            int x0 = (int)fx;
            int y0 = (int)fy;
            float tx = fx - x0;
            float ty = fy - y0;
            int i = y0 * size + x0;
            Vector4 south = Vector4.LerpUnclamped(states[i], states[i + 1], tx);
            Vector4 north = Vector4.LerpUnclamped(states[i + size], states[i + size + 1], tx);
            return Vector4.LerpUnclamped(south, north, ty);
        }

        public static float Sample(float[] values, int size, float u, float v)
        {
            if (values == null)
                return 0f;

            float fx = Mathf.Clamp(u * size - 0.5f, 0f, size - 1.001f);
            float fy = Mathf.Clamp(v * size - 0.5f, 0f, size - 1.001f);
            int x0 = (int)fx;
            int y0 = (int)fy;
            int i = y0 * size + x0;
            float tx = fx - x0;
            float ty = fy - y0;
            return Mathf.Lerp(Mathf.Lerp(values[i], values[i + 1], tx), Mathf.Lerp(values[i + size], values[i + size + 1], tx), ty);
        }

        private static void Blur(float[] values, int size)
        {
            float[] rows = new float[values.Length];

            for (int y = 0; y < size; y++)
            {
                int row = y * size;

                for (int x = 0; x < size; x++)
                    rows[row + x] = (values[row + Mathf.Max(x - 1, 0)] + 2f * values[row + x] + values[row + Mathf.Min(x + 1, size - 1)]) * 0.25f;
            }

            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                int below = Mathf.Max(y - 1, 0) * size;
                int above = Mathf.Min(y + 1, size - 1) * size;

                for (int x = 0; x < size; x++)
                    values[row + x] = (rows[below + x] + 2f * rows[row + x] + rows[above + x]) * 0.25f;
            }
        }

        private static void Blur(Vector4[] states, int size)
        {
            Vector4[] rows = new Vector4[states.Length];

            for (int y = 0; y < size; y++)
            {
                int row = y * size;

                for (int x = 0; x < size; x++)
                    rows[row + x] = (states[row + Mathf.Max(x - 1, 0)] + 2f * states[row + x] +
                                     states[row + Mathf.Min(x + 1, size - 1)]) * 0.25f;
            }

            for (int y = 0; y < size; y++)
            {
                int row = y * size;
                int below = Mathf.Max(y - 1, 0) * size;
                int above = Mathf.Min(y + 1, size - 1) * size;

                for (int x = 0; x < size; x++)
                    states[row + x] = (rows[below + x] + 2f * rows[row + x] + rows[above + x]) * 0.25f;
            }
        }

        private static float[] BuildLinear()
        {
            float[] table = new float[256];

            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                table[i] = (float)(c <= 0.04045 ? c / 12.92 : System.Math.Pow((c + 0.055) / 1.055, 2.4));
            }

            return table;
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }
    }
}
