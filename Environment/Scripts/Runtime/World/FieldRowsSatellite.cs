using System.Collections.Generic;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Направление рядков полей (FieldAreas) по самому снимку поля: длинная сторона поля (rotation экстрактора) часто не та —
    /// на kharkiv_east колеи на снимке шли поперёк неё, и рисунок шейдера ложился клеткой поверх настоящих. Снимок читается
    /// по 1 м на пиксель (SatelliteSize на тайл, линейный цвет), в поле (дальше EdgeMargin от края — лесополосы и дороги не
    /// в счёт) копится тензор градиентов яркости (каждый градиент — единичный, чтобы редкие резкие края не перевешивали);
    /// линии идут поперёк главного градиента. Если рисунок слабый (связность ниже MinCoherence) или отсчётов мало, остаётся
    /// длинная сторона. По шагам (Begin, AddTile на каждый тайл, Finish), как RoadSatelliteSnap: в редакторе снимки — из
    /// ассетов тайлов (FieldAreasBaker), в плеере — пока генератор строит тайл пакета мира (WorldMapPackage).
    /// </summary>
    public static class FieldRowsSatellite
    {
        public const int SatelliteSize = 1024;
        private const int SampleStep = 3;
        private const float EdgeMargin = 12f;
        private const float MinCoherence = 0.08f;
        private const int MinSamples = 400;

        public sealed class Job
        {
            internal List<FieldAreas.Field> Fields;
            internal double[] Sxx, Sxy, Syy;
            internal int[] Counts;
            internal float[] Luminance;

            /// <summary>Есть ли поле на тайле (его снимок нужен AddTile).</summary>
            public bool Wants(Rect area)
            {
                foreach (FieldAreas.Field field in Fields)
                    if (field.bounds.Overlaps(area))
                        return true;

                return false;
            }
        }

        public static Job Begin(List<FieldAreas.Field> fields)
        {
            int n = fields.Count;
            return new Job { Fields = fields, Sxx = new double[n], Sxy = new double[n], Syy = new double[n], Counts = new int[n] };
        }

        /// <summary>Снимок тайла (area — тайл в метрах; pixels — SatelliteSize² в линейном цвете, строки с юга).</summary>
        public static void AddTile(Job job, Rect area, Color32[] pixels)
        {
            if (pixels == null || pixels.Length != SatelliteSize * SatelliteSize)
                return;

            List<int> inside = new();

            for (int f = 0; f < job.Fields.Count; f++)
                if (job.Fields[f].bounds.Overlaps(area))
                    inside.Add(f);

            if (inside.Count == 0)
                return;

            float[] lum = job.Luminance ??= new float[SatelliteSize * SatelliteSize];

            for (int i = 0; i < lum.Length; i++)
                lum[i] = (pixels[i].r * 0.299f + pixels[i].g * 0.587f + pixels[i].b * 0.114f) / 255f;

            float cellX = area.width / SatelliteSize;
            float cellZ = area.height / SatelliteSize;

            foreach (int f in inside)
            {
                FieldAreas.Field field = job.Fields[f];

                for (int j = 1; j < SatelliteSize - 1; j += SampleStep)
                {
                    float z = area.yMin + (j + 0.5f) * cellZ;

                    if (z < field.bounds.yMin + EdgeMargin || z > field.bounds.yMax - EdgeMargin)
                        continue;

                    for (int i = 1; i < SatelliteSize - 1; i += SampleStep)
                    {
                        float x = area.xMin + (i + 0.5f) * cellX;

                        if (x < field.bounds.xMin + EdgeMargin || x > field.bounds.xMax - EdgeMargin)
                            continue;

                        if (!InsideField(field, x, z) || NearEdge(field, x, z, EdgeMargin))
                            continue;

                        // Собель по яркости, 1 м на пиксель; x — на восток, y — на север
                        int c = j * SatelliteSize + i;
                        float gx = lum[c + 1 - SatelliteSize] + 2f * lum[c + 1] + lum[c + 1 + SatelliteSize] -
                                   lum[c - 1 - SatelliteSize] - 2f * lum[c - 1] - lum[c - 1 + SatelliteSize];
                        float gy = lum[c + SatelliteSize - 1] + 2f * lum[c + SatelliteSize] + lum[c + SatelliteSize + 1] -
                                   lum[c - SatelliteSize - 1] - 2f * lum[c - SatelliteSize] - lum[c - SatelliteSize + 1];
                        float length = Mathf.Sqrt(gx * gx + gy * gy);

                        if (length < 0.004f)
                            continue;

                        gx /= length;
                        gy /= length;
                        job.Sxx[f] += gx * gx;
                        job.Sxy[f] += gx * gy;
                        job.Syy[f] += gy * gy;
                        job.Counts[f]++;
                    }
                }
            }
        }

        /// <summary>Рядки полей с ясным рисунком — по снимку; возвращает, у скольких.</summary>
        public static int Finish(Job job)
        {
            int oriented = 0;
            job.Luminance = null;

            for (int f = 0; f < job.Fields.Count; f++)
            {
                if (job.Counts[f] < MinSamples)
                    continue;

                double a = job.Sxx[f] / job.Counts[f], b = job.Sxy[f] / job.Counts[f], d = job.Syy[f] / job.Counts[f];
                double coherence = System.Math.Sqrt((a - d) * (a - d) + 4.0 * b * b) / System.Math.Max(a + d, 1e-6);

                if (coherence < MinCoherence)
                    continue;

                // главный градиент — под углом phi от востока к северу; линии поперёк него: азимут = −phi
                double phi = 0.5 * System.Math.Atan2(2.0 * b, a - d);
                job.Fields[f].rotation = Mathf.Repeat((float)(-phi * Mathf.Rad2Deg), 180f);
                oriented++;
            }

            return oriented;
        }

        private static bool InsideField(FieldAreas.Field field, float x, float z)
        {
            if (!Inside(field.outer, 0, field.outer.Length, x, z))
                return false;

            for (int h = 0; h < field.holeStarts.Length; h++)
            {
                int start = field.holeStarts[h];
                int end = h + 1 < field.holeStarts.Length ? field.holeStarts[h + 1] : field.holes.Length;

                if (Inside(field.holes, start, end, x, z))
                    return false;
            }

            return true;
        }

        private static bool NearEdge(FieldAreas.Field field, float x, float z, float margin)
        {
            if (NearRing(field.outer, 0, field.outer.Length, x, z, margin))
                return true;

            for (int h = 0; h < field.holeStarts.Length; h++)
            {
                int start = field.holeStarts[h];
                int end = h + 1 < field.holeStarts.Length ? field.holeStarts[h + 1] : field.holes.Length;

                if (NearRing(field.holes, start, end, x, z, margin))
                    return true;
            }

            return false;
        }

        private static bool NearRing(Vector2[] ring, int start, int end, float x, float z, float margin)
        {
            Vector2 point = new(x, z);

            for (int a = start, b = end - 1; a < end; b = a++)
            {
                Vector2 p = ring[a], q = ring[b];

                if (x < Mathf.Min(p.x, q.x) - margin || x > Mathf.Max(p.x, q.x) + margin ||
                    z < Mathf.Min(p.y, q.y) - margin || z > Mathf.Max(p.y, q.y) + margin)
                    continue;

                Vector2 edge = q - p;
                float k = Mathf.Clamp01(Vector2.Dot(point - p, edge) / Mathf.Max(edge.sqrMagnitude, 1e-6f));

                if ((p + edge * k - point).sqrMagnitude < margin * margin)
                    return true;
            }

            return false;
        }

        private static bool Inside(Vector2[] ring, int start, int end, float x, float z)
        {
            bool inside = false;

            for (int a = start, b = end - 1; a < end; b = a++)
            {
                Vector2 p = ring[a], q = ring[b];

                if ((p.y > z) != (q.y > z) && x < (q.x - p.x) * (z - p.y) / (q.y - p.y) + p.x)
                    inside = !inside;
            }

            return inside;
        }
    }
}
