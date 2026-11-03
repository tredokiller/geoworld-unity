using System;
using System.Collections.Generic;
using System.Linq;
using GeoWorld;
using GeoWorld.Editor;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Данные карты мира (дороги, поля, контуры лесов с типом листвы) из прочитанной выгрузки GeoWorld (EnvironmentData) —
    /// без редактора: для пакета мира, который генерирует плеер (WorldMapPackage). Как запекатели редактора (RoadAreasBaker,
    /// FieldAreasBaker, TreeLeafAreasBaker); шаг со снимком (RoadSatelliteSnap, FieldRowsSatellite) — у того, кто снимки
    /// читает (WorldMapPackage: пока генератор строит тайлы). Стыки дорог (RoadJunctions) и бордюры (MarkCurbs — по карте
    /// земель) — те же. Вид покрытия и разметку берёт и редактор.
    /// </summary>
    public static class WorldMapExport
    {
        // ширина балласта, если у рельсов её нет; ширина дороги, если её нет
        private const float DefaultRailWidth = 4f;
        private const float DefaultRoadWidth = 3f;

        /// <summary>Дороги и рельсы выгрузок со стыками; coverage — их границы (внутри дороги известны осями).</summary>
        public static List<RoadAreas.Road> Roads(IEnumerable<EnvironmentData> exports, List<Rect> coverage, out RoadJunctions.Links links, out string summary)
        {
            List<RoadAreas.Road> roads = new();
            List<string> ids = new();
            HashSet<string> seen = new();
            int skipped = 0;

            foreach (EnvironmentData env in exports)
            {
                if (env == null)
                    continue;

                if (env.bounds != null)
                    coverage.Add(Rect.MinMaxRect(env.bounds.minX, env.bounds.minZ, env.bounds.maxX, env.bounds.maxZ));

                foreach (LineFeature road in env.roads ?? new List<LineFeature>())
                    if (!Add(road, road.width > 0 ? road.width : DefaultRoadWidth, KindOf(road.material), IsMarked(road.type)))
                        skipped++;

                foreach (LineFeature rail in env.railways ?? new List<LineFeature>())
                    if (!Add(rail, rail.width > 0 ? rail.width : DefaultRailWidth, RoadAreas.Kind.Rail, false))
                        skipped++;
            }

            int stitched = RoadJunctions.Stitch(roads, ids);
            links = RoadJunctions.Detect(roads, out string ends);
            summary = $"{roads.Count} roads ({skipped} bridges, tunnels and repeats skipped, {stitched} pieces stitched); {ends}";
            return roads;

            bool Add(LineFeature feature, float width, RoadAreas.Kind kind, bool marked)
            {
                if (feature == null || feature.bridge || feature.tunnel || feature.layer != 0)
                    return false;

                float[] flat = feature.points ?? Array.Empty<float>();

                if (flat.Length < 4)
                    return false;

                string key = feature.id + "|" + flat.Length + "|" + flat[0] + "|" + flat[1];

                if (!seen.Add(key))
                    return false;

                Vector2[] points = new Vector2[flat.Length / 2];

                for (int i = 0; i < points.Length; i++)
                    points[i] = new Vector2(flat[i * 2], flat[i * 2 + 1]);

                float half = Mathf.Max(0.5f, width) * 0.5f;
                Vector2 min = points.Aggregate(Vector2.positiveInfinity, Vector2.Min) - Vector2.one * half;
                Vector2 max = points.Aggregate(Vector2.negativeInfinity, Vector2.Max) + Vector2.one * half;
                ids.Add(feature.id);
                roads.Add(new RoadAreas.Road
                {
                    width = Mathf.Max(0.5f, width), bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y), points = points,
                    // износ: 0 у экстрактора — «не знаю»
                    kind = kind, marked = marked, wear = Mathf.Clamp01(feature.wear > 0f ? feature.wear : 0.5f)
                });
                return true;
            }
        }

        /// <summary>Поля (OSM landuse=farmland или материал cropland): контуры и рядки вдоль длинной стороны.</summary>
        public static List<FieldAreas.Field> Fields(IEnumerable<EnvironmentData> exports)
        {
            List<FieldAreas.Field> fields = new();

            foreach (EnvironmentData env in exports)
            {
                foreach (AreaFeature area in env?.areas ?? new List<AreaFeature>())
                {
                    if (area == null || area.tag != "landuse=farmland" && area.material != "cropland" || area.rings == null || area.rings.Length == 0)
                        continue;

                    FieldAreas.Field field = new() { rotation = Mathf.Repeat(area.rotation, 180f), outer = Ring(area.rings[0]) };

                    if (field.outer.Length < 3)
                        continue;

                    Holes(area.rings, out field.holes, out field.holeStarts);
                    field.bounds = Bounds(field.outer);
                    fields.Add(field);
                }
            }

            return fields;
        }

        /// <summary>Контуры лесов с типом листвы (OSM leaf_type: 1 — лиственный, 2 — хвойный, 3 — смешанный).</summary>
        public static List<TreeSpeciesProfile.LeafArea> LeafAreas(IEnumerable<EnvironmentData> exports)
        {
            List<TreeSpeciesProfile.LeafArea> areas = new();

            foreach (EnvironmentData env in exports)
            {
                foreach (AreaFeature area in env?.areas ?? new List<AreaFeature>())
                {
                    string leaf = area?.leafType ?? "";
                    byte type = leaf == "broadleaved" ? (byte)1 : leaf == "needleleaved" ? (byte)2 : leaf == "mixed" ? (byte)3 : (byte)0;

                    if (type == 0 || area.rings == null || area.rings.Length == 0)
                        continue;

                    TreeSpeciesProfile.LeafArea result = new() { type = type, outer = Ring(area.rings[0]) };

                    if (result.outer.Length < 3)
                        continue;

                    Holes(area.rings, out result.holes, out result.holeStarts);
                    result.bounds = Bounds(result.outer);
                    areas.Add(result);
                }
            }

            return areas;
        }

        // Бордюр — у асфальта в городе: вдоль дороги (каждые 10 м, на оси и в 4 м за кромками) застройка карты земель (Urban,
        // Building, Asphalt) не меньше половины, и дорога размечена, широкая (городская улица от ~7 м, сельская — 4–6) или
        // рядом мощёное (тротуары, площади — Paving). В селе застройка тоже Urban — там обочина. landAt — класс земли в
        // точке, None — неизвестно.
        public static int MarkCurbs(List<RoadAreas.Road> roads, Func<float, float, GeoLandClass> landAt)
        {
            if (landAt == null)
                return 0;

            int marked = 0;

            foreach (RoadAreas.Road road in roads)
            {
                if (road.kind != RoadAreas.Kind.Asphalt)
                    continue;

                int samples = 0, urban = 0, paving = 0;
                float half = road.width * 0.5f;

                for (int i = 1; i < road.points.Length; i++)
                {
                    Vector2 a = road.points[i - 1];
                    Vector2 b = road.points[i];
                    float length = Vector2.Distance(a, b);

                    if (length < 0.01f)
                        continue;

                    Vector2 direction = (b - a) / length;
                    Vector2 normal = new(-direction.y, direction.x);

                    for (float s = 0f; s < length; s += 10f)
                    {
                        Vector2 point = a + direction * s;

                        foreach (float side in new[] { 0f, half + 4f, -half - 4f })
                        {
                            Vector2 at = point + normal * side;
                            GeoLandClass land = landAt(at.x, at.y);

                            if (land == GeoLandClass.None)
                                continue;

                            samples++;

                            if (land is GeoLandClass.Urban or GeoLandClass.Building or GeoLandClass.Asphalt)
                                urban++;

                            if (land == GeoLandClass.Paving)
                                paving++;
                        }
                    }
                }

                if (samples == 0)
                    continue;

                float urbanShare = (urban + paving) / (float)samples;
                road.curb = urbanShare >= 0.5f && (paving / (float)samples >= 0.1f || road.marked || road.width >= 6.8f);

                if (road.curb)
                    marked++;
            }

            return marked;
        }

        // Материал экстрактора (materials.py: asphalt, paving, gravel, dirt_road, path) → покрытие.
        public static RoadAreas.Kind KindOf(string material) => material switch
        {
            "asphalt" => RoadAreas.Kind.Asphalt,
            "paving" => RoadAreas.Kind.Paving,
            "gravel" => RoadAreas.Kind.Gravel,
            "path" => RoadAreas.Kind.Path,
            _ => RoadAreas.Kind.Dirt
        };

        // Разметка — у магистралей и дорог областного и районного значения (как GeoRoadPhotos.IsMarked пакета).
        public static bool IsMarked(string type)
        {
            string highway = (type ?? "").Replace("_link", "");
            return highway is "motorway" or "trunk" or "primary" or "secondary" or "tertiary";
        }

        private static Vector2[] Ring(float[] flat)
        {
            if (flat == null)
                return Array.Empty<Vector2>();

            Vector2[] points = new Vector2[flat.Length / 2];

            for (int i = 0; i < points.Length; i++)
                points[i] = new Vector2(flat[i * 2], flat[i * 2 + 1]);

            return points;
        }

        private static void Holes(float[][] rings, out Vector2[] holes, out int[] holeStarts)
        {
            List<Vector2> points = new();
            List<int> starts = new();

            for (int r = 1; r < rings.Length; r++)
            {
                starts.Add(points.Count);
                points.AddRange(Ring(rings[r]));
            }

            holes = points.ToArray();
            holeStarts = starts.ToArray();
        }

        private static Rect Bounds(Vector2[] ring)
        {
            Vector2 min = ring.Aggregate(Vector2.positiveInfinity, Vector2.Min);
            Vector2 max = ring.Aggregate(Vector2.negativeInfinity, Vector2.Max);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
    }
}
