using System.Collections.Generic;
using _Game.Scripts.World;
using GeoWorld;
using UnityEditor;
using UnityEngine;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>
    /// Подтягивает оси дорог (RoadAreas) к дороге на снимке тайлов. Оси OSM местами лежат в 1–3 м от неё — полотно
    /// (GroundRoads.hlsl) и вырезанная трава шли рядом с дорогой на снимке. Каждые Spacing м вдоль дороги пробуются сдвиги
    /// поперёк от −MaxOffset до +MaxOffset: насколько полоса ширины дороги «дорожнее» полос по бокам (асфальт и плитка —
    /// серые, ненасыщенные, не зелёные; грунт и гравий — голые, светлее), снимок — 2048 px на тайл (~0.5 м). Свидетельства
    /// копятся в окне ±Window отсчётов вдоль дороги; где их мало (лес, тень, дорога снимка не видна) — ось OSM. Сдвиги
    /// сглаживаются вдоль дороги. Стыки (RoadJunctions) не разъезжаются: за EndTaper м до конца, примкнувшего к другой
    /// дороге, сдвиг переходит в сдвиг той дороги в точке примыкания (конец остаётся на её новой оси), концы, сходящиеся в
    /// одной точке, сдвигаются на средний сдвиг (или со сквозной дорогой через эту точку), тупик — на свой. У асфальта и
    /// плитки ширина — по краям серой полосы снимка (медиана по дороге, от 0.75 до 1.5 ширины OSM).
    /// </summary>
    public static class RoadSatelliteSnap
    {
        private const float Spacing = 8f;
        private const float MaxOffset = 4f;
        private const float OffsetStep = 0.5f;
        private const int Window = 2;
        // насколько лучший сдвиг лучше среднего: у асфальта серая полоса видна чётко, у грунта — слабее, ему строже
        private const float MinConfidence = 0.05f;
        private const float MinConfidenceBare = 0.08f;
        private const float EndTaper = 12f;
        private const int SatelliteSize = 2048;
        private const float Simplify = 0.2f;

        private sealed class Sample
        {
            public Vector2 Point;
            public Vector2 Normal;
            public float Along;
            public float[] Scores;
            public float Width = -1f;
        }

        public static string Snap(List<RoadAreas.Road> roads, GeoTiledWorld world, RoadJunctions.Links links)
        {
            int steps = Mathf.RoundToInt(MaxOffset * 2 / OffsetStep) + 1;
            var samples = new List<Sample>[roads.Count];
            var byTile = new Dictionary<Vector2Int, List<(int road, Sample sample)>>();

            for (int r = 0; r < roads.Count; r++)
            {
                if (roads[r].kind == RoadAreas.Kind.Rail)
                    continue;

                samples[r] = Resample(roads[r].points);

                foreach (Sample sample in samples[r])
                {
                    var key = new Vector2Int(Mathf.FloorToInt((sample.Point.x - world.origin.x) / world.tileSize),
                        Mathf.FloorToInt((sample.Point.y - world.origin.y) / world.tileSize));

                    if (!byTile.TryGetValue(key, out var list))
                        byTile[key] = list = new List<(int, Sample)>();

                    list.Add((r, sample));
                }
            }

            RenderTexture rt = RenderTexture.GetTemporary(SatelliteSize, SatelliteSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D read = new(SatelliteSize, SatelliteSize, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            float[] paved = new float[SatelliteSize * SatelliteSize];
            float[] bare = new float[SatelliteSize * SatelliteSize];
            int done = 0;

            try
            {
                foreach (GeoTiledWorld.Tile tile in world.tiles)
                {
                    if (tile.empty || string.IsNullOrEmpty(tile.assetPath) || !byTile.TryGetValue(tile.index, out var list))
                        continue;

                    if (EditorUtility.DisplayCancelableProgressBar("Bake Road Areas", "Roads on the satellite: " + tile.index, done / (float)byTile.Count))
                        break;

                    done++;
                    TerrainData data = AssetDatabase.LoadAssetAtPath<TerrainData>(tile.assetPath);
                    Texture satellite = data != null ? FieldAreasBaker.Satellite(data) : null;

                    if (satellite == null)
                        continue;

                    RenderTexture previous = RenderTexture.active;
                    Graphics.Blit(satellite, rt);
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, SatelliteSize, SatelliteSize), 0, 0);
                    read.Apply(false);
                    RenderTexture.active = previous;
                    Classify(read.GetPixels32(), paved, bare);

                    foreach ((int road, Sample sample) in list)
                        Score(sample, roads[road], IsPaved(roads[road].kind) ? paved : bare, tile.area, steps);

                    if (done % 20 == 0)
                        EditorUtility.UnloadUnusedAssetsImmediate();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(read);
            }

            int snapped = 0;
            int widened = 0;
            float[][] offsets = new float[roads.Count][];
            float[] totals = new float[roads.Count];

            for (int r = 0; r < roads.Count; r++)
            {
                if (samples[r] == null || samples[r].Count < 2)
                    continue;

                totals[r] = samples[r][samples[r].Count - 1].Along;
                offsets[r] = Offsets(samples[r], steps, IsPaved(roads[r].kind) ? MinConfidence : MinConfidenceBare, out int accepted);

                if (accepted > 0)
                    snapped++;
                else
                    offsets[r] = null;
            }

            // куда сдвинуть концы: примыкание — со сдвигом той дороги в точке (он сам зависит от её концов — несколько
            // проходов), общая точка концов — средний их сдвиг или сдвиг сквозной дороги, тупик — свой
            var targetStart = new Vector2?[roads.Count];
            var targetEnd = new Vector2?[roads.Count];
            var nodeSum = new Dictionary<int, Vector2>();
            var nodeCount = new Dictionary<int, int>();

            for (int r = 0; r < roads.Count; r++)
            {
                for (int e = 0; e < 2; e++)
                {
                    RoadJunctions.Link link = e == 0 ? links.Start[r] : links.End[r];

                    if (link.Type != RoadAreas.End.Joined || link.Other >= 0)
                        continue;

                    Vector2 own = Own(samples[r], offsets[r], e == 0 ? 0f : totals[r]);
                    nodeSum[link.Node] = (nodeSum.TryGetValue(link.Node, out Vector2 sum) ? sum : Vector2.zero) + own;
                    nodeCount[link.Node] = (nodeCount.TryGetValue(link.Node, out int count) ? count : 0) + 1;
                }
            }

            for (int pass = 0; pass < 3; pass++)
            {
                var nextStart = new Vector2?[roads.Count];
                var nextEnd = new Vector2?[roads.Count];

                for (int r = 0; r < roads.Count; r++)
                {
                    for (int e = 0; e < 2; e++)
                    {
                        RoadJunctions.Link link = e == 0 ? links.Start[r] : links.End[r];
                        Vector2? target = null;

                        if (link.Other >= 0 && (link.Type == RoadAreas.End.Attached || link.Type == RoadAreas.End.Joined))
                            target = Final(samples[link.Other], offsets[link.Other], totals[link.Other], targetStart[link.Other], targetEnd[link.Other], link.OtherAlong);
                        else if (link.Type == RoadAreas.End.Joined && nodeCount.TryGetValue(link.Node, out int count))
                            target = nodeSum[link.Node] / count;

                        if (e == 0)
                            nextStart[r] = target;
                        else
                            nextEnd[r] = target;
                    }
                }

                targetStart = nextStart;
                targetEnd = nextEnd;
            }

            double shift = 0;
            int shifts = 0;
            int moved = 0;

            for (int r = 0; r < roads.Count; r++)
            {
                if (samples[r] == null || samples[r].Count < 2)
                    continue;

                var points = new List<Vector2>(samples[r].Count);
                float largest = 0f;

                for (int i = 0; i < samples[r].Count; i++)
                {
                    Vector2 displacement = Final(samples[r], offsets[r], totals[r], targetStart[r], targetEnd[r], samples[r][i].Along);
                    points.Add(samples[r][i].Point + displacement);
                    largest = Mathf.Max(largest, displacement.magnitude);

                    if (offsets[r] != null)
                    {
                        shift += displacement.magnitude;
                        shifts++;
                    }
                }

                if (largest < 0.01f)
                    continue;

                if (offsets[r] == null)
                    moved++;

                roads[r].points = SimplifyLine(points, Simplify).ToArray();

                if (offsets[r] != null && IsPaved(roads[r].kind) && Width(samples[r], roads[r].width, out float width))
                {
                    roads[r].width = width;
                    widened++;
                }

                RoadJunctions.RecomputeBounds(roads[r]);
            }

            return $"snapped {snapped} of {roads.Count} roads to the satellite (mean shift {(shifts > 0 ? shift / shifts : 0):F2} m), " +
                   $"{moved} more moved only at the ends with the roads they meet, paved width from the satellite on {widened}";
        }

        // Свой сдвиг дороги (по снимку) в метрах вдоль неё: поперёк, между отсчётами линейно; без подтяжки — ноль.
        private static Vector2 Own(List<Sample> samples, float[] offsets, float along)
        {
            if (samples == null || offsets == null || samples.Count < 2)
                return Vector2.zero;

            int i = 1;

            while (i < samples.Count - 1 && samples[i].Along < along)
                i++;

            Sample a = samples[i - 1], b = samples[i];
            float t = Mathf.Clamp01((along - a.Along) / Mathf.Max(b.Along - a.Along, 1e-4f));
            Vector2 normal = Vector2.Lerp(a.Normal, b.Normal, t).normalized;
            return normal * Mathf.Lerp(offsets[i - 1], offsets[i], t);
        }

        // Итоговый сдвиг: свой, а за EndTaper м до конца со стыком — к сдвигу стыка (null — конец свой).
        private static Vector2 Final(List<Sample> samples, float[] offsets, float total, Vector2? start, Vector2? end, float along)
        {
            Vector2 own = Own(samples, offsets, along);

            if (samples == null || samples.Count < 2)
                return own;

            float taper = Mathf.Max(Mathf.Min(EndTaper, total * 0.5f), 1e-3f);
            Vector2 result = own;

            if (start.HasValue)
                result += (start.Value - own) * (1f - Smooth(0f, 1f, along / taper));

            if (end.HasValue)
                result += (end.Value - own) * (1f - Smooth(0f, 1f, (total - along) / taper));

            return result;
        }

        private static bool IsPaved(RoadAreas.Kind kind) => kind is RoadAreas.Kind.Asphalt or RoadAreas.Kind.Paving;

        // Отсчёты каждые Spacing м вдоль ломаной (с концами), поперёк — нормаль налево по ходу.
        private static List<Sample> Resample(Vector2[] points)
        {
            var result = new List<Sample>();
            float total = 0f;

            for (int i = 1; i < points.Length; i++)
                total += Vector2.Distance(points[i - 1], points[i]);

            if (total < 1f)
                return result;

            int count = Mathf.Max(2, Mathf.CeilToInt(total / Spacing) + 1);
            float step = total / (count - 1);
            int segment = 1;
            float segmentStart = 0f;

            for (int k = 0; k < count; k++)
            {
                float s = Mathf.Min(k * step, total);

                while (segment < points.Length - 1 && segmentStart + Vector2.Distance(points[segment - 1], points[segment]) < s)
                {
                    segmentStart += Vector2.Distance(points[segment - 1], points[segment]);
                    segment++;
                }

                Vector2 a = points[segment - 1];
                Vector2 b = points[segment];
                float length = Mathf.Max(Vector2.Distance(a, b), 1e-4f);
                Vector2 direction = (b - a) / length;
                Vector2 point = a + direction * Mathf.Clamp(s - segmentStart, 0f, length);
                result.Add(new Sample { Point = point, Normal = new Vector2(-direction.y, direction.x), Along = s });
            }

            return result;
        }

        // «Дорожность» пикселя снимка: paved — серое, ненасыщенное, не зелёное, не слишком тёмное и не белое; bare — голое и
        // светлое (полевая дорога на снимке — светлая полоса без зелени).
        private static void Classify(Color32[] pixels, float[] paved, float[] bare)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                float r = pixels[i].r / 255f, g = pixels[i].g / 255f, b = pixels[i].b / 255f;
                float max = Mathf.Max(r, Mathf.Max(g, b));
                float min = Mathf.Min(r, Mathf.Min(g, b));
                float lum = r * 0.299f + g * 0.587f + b * 0.114f;
                float green = Mathf.Max(g - Mathf.Max(r, b), 0f);
                // асфальт на снимке — от тёмно-серого до выгоревшего светло-серо-бежевого (~150, 140, 125)
                float grey = Mathf.Clamp01(1f - (max - min) * 3f);
                paved[i] = grey * Mathf.Clamp01(1f - green * 8f) * Smooth(0.12f, 0.25f, lum) * (1f - Smooth(0.7f, 0.85f, lum));
                bare[i] = Mathf.Clamp01(1f - green * 6f) * (0.35f + 0.65f * lum);
            }
        }

        // smoothstep шейдеров (Mathf.SmoothStep — другое: сглаженная интерполяция между первыми двумя)
        private static float Smooth(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static float Lookup(float[] map, Rect area, Vector2 point)
        {
            int x = Mathf.Clamp((int)((point.x - area.xMin) / area.width * SatelliteSize), 0, SatelliteSize - 1);
            int y = Mathf.Clamp((int)((point.y - area.yMin) / area.height * SatelliteSize), 0, SatelliteSize - 1);
            return map[y * SatelliteSize + x];
        }

        // Для каждого сдвига: средняя «дорожность» полосы ширины дороги минус большей из двух полос по бокам (0.75–3 м за
        // краем): дорога «дорожнее» обеих сторон. По среднему двух сторон ось съезжала на край поля (пашня с одной стороны,
        // трава с другой) — там, где грунтовку на снимке не видно.
        private static void Score(Sample sample, RoadAreas.Road road, float[] map, Rect area, int steps)
        {
            float half = road.width * 0.5f;
            sample.Scores = new float[steps];

            for (int k = 0; k < steps; k++)
            {
                float offset = -MaxOffset + k * OffsetStep;
                float inside = 0f, sideA = 0f, sideB = 0f;
                int insideCount = 0, sideCount = 0;

                for (float x = -half; x <= half + 1e-3f; x += 0.5f)
                {
                    inside += Lookup(map, area, sample.Point + sample.Normal * (offset + x));
                    insideCount++;
                }

                for (float x = half + 0.75f; x <= half + 3f + 1e-3f; x += 0.5f)
                {
                    sideA += Lookup(map, area, sample.Point + sample.Normal * (offset + x));
                    sideB += Lookup(map, area, sample.Point + sample.Normal * (offset - x));
                    sideCount++;
                }

                sideCount = Mathf.Max(sideCount, 1);
                sample.Scores[k] = inside / Mathf.Max(insideCount, 1) - Mathf.Max(sideA, sideB) / sideCount;
            }

            if (!IsPaved(road.kind))
                return;

            // ширина серой полосы снимка вокруг оси: до половины её «дорожности» над фоном
            float centre = Lookup(map, area, sample.Point);
            float background = 0f;

            for (float x = half + 3f; x <= half + 5f; x += 0.5f)
                background += (Lookup(map, area, sample.Point + sample.Normal * x) + Lookup(map, area, sample.Point - sample.Normal * x)) * 0.5f;

            background /= 5f;
            float level = background + (centre - background) * 0.5f;

            if (centre - background < 0.15f)
                return;

            float left = 0f, right = 0f;

            while (left < half * 1.6f && Lookup(map, area, sample.Point + sample.Normal * (left + 0.5f)) > level)
                left += 0.5f;

            while (right < half * 1.6f && Lookup(map, area, sample.Point - sample.Normal * (right + 0.5f)) > level)
                right += 0.5f;

            sample.Width = left + right + 0.5f;
        }

        // Сдвиг в каждом отсчёте: лучший по сумме оценок окна ±Window отсчётов, если он заметно лучше среднего; пропуски —
        // по соседям; медиана и сглаживание вдоль дороги.
        private static float[] Offsets(List<Sample> samples, int steps, float minConfidence, out int accepted)
        {
            int n = samples.Count;
            float[] offsets = new float[n];
            bool[] has = new bool[n];
            accepted = 0;

            for (int i = 0; i < n; i++)
            {
                float best = float.NegativeInfinity, sum = 0f;
                int bestK = -1;

                for (int k = 0; k < steps; k++)
                {
                    float score = 0f;
                    int counted = 0;

                    for (int j = Mathf.Max(0, i - Window); j <= Mathf.Min(n - 1, i + Window); j++)
                    {
                        if (samples[j].Scores == null)
                            continue;

                        score += samples[j].Scores[k];
                        counted++;
                    }

                    if (counted == 0)
                        break;

                    score /= counted;
                    sum += score;

                    if (score > best)
                    {
                        best = score;
                        bestK = k;
                    }
                }

                if (bestK < 0 || best <= 0f || best - sum / steps < minConfidence)
                    continue;

                offsets[i] = -MaxOffset + bestK * OffsetStep;
                has[i] = true;
                accepted++;
            }

            if (accepted == 0)
                return offsets;

            // пропуски — линейно между соседями с оценкой, за крайними — их значение
            for (int i = 0; i < n; i++)
            {
                if (has[i])
                    continue;

                int before = i - 1, after = i + 1;

                while (before >= 0 && !has[before])
                    before--;

                while (after < n && !has[after])
                    after++;

                offsets[i] = before < 0 ? offsets[after] : after >= n ? offsets[before] :
                    Mathf.Lerp(offsets[before], offsets[after], (i - before) / (float)(after - before));
            }

            float[] median = new float[n];
            float[] window = new float[5];

            for (int i = 0; i < n; i++)
            {
                int count = 0;

                for (int j = Mathf.Max(0, i - 2); j <= Mathf.Min(n - 1, i + 2); j++)
                    window[count++] = offsets[j];

                System.Array.Sort(window, 0, count);
                median[i] = window[count / 2];
            }

            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                int count = 0;

                for (int j = Mathf.Max(0, i - 2); j <= Mathf.Min(n - 1, i + 2); j++)
                {
                    sum += median[j];
                    count++;
                }

                offsets[i] = Mathf.Clamp(sum / count, -MaxOffset, MaxOffset);
            }

            return offsets;
        }

        private static bool Width(List<Sample> samples, float osmWidth, out float width)
        {
            var widths = new List<float>();

            foreach (Sample sample in samples)
            {
                if (sample.Width > 0f)
                    widths.Add(sample.Width);
            }

            width = osmWidth;

            if (widths.Count < 5 || widths.Count < samples.Count / 3)
                return false;

            widths.Sort();
            width = Mathf.Clamp(widths[widths.Count / 2], osmWidth * 0.75f, osmWidth * 1.5f);
            return Mathf.Abs(width - osmWidth) > 0.25f;
        }

        // Упрощение ломаной (Дуглас — Пекер): точек меньше, отклонение не больше tolerance.
        private static List<Vector2> SimplifyLine(List<Vector2> points, float tolerance)
        {
            if (points.Count < 3)
                return points;

            var keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, points.Count - 1));

            while (stack.Count > 0)
            {
                (int first, int last) = stack.Pop();
                float worst = 0f;
                int index = -1;
                Vector2 a = points[first], b = points[last];
                Vector2 ab = b - a;
                float length2 = Mathf.Max(ab.sqrMagnitude, 1e-6f);

                for (int i = first + 1; i < last; i++)
                {
                    float t = Mathf.Clamp01(Vector2.Dot(points[i] - a, ab) / length2);
                    float distance = Vector2.Distance(points[i], a + ab * t);

                    if (distance > worst)
                    {
                        worst = distance;
                        index = i;
                    }
                }

                if (index < 0 || worst <= tolerance)
                    continue;

                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }

            var result = new List<Vector2>();

            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i])
                    result.Add(points[i]);
            }

            return result;
        }
    }
}
