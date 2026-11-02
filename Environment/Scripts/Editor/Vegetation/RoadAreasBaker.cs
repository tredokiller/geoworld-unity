using System.Collections.Generic;
using System.IO;
using System.Linq;
using _Game.Scripts.World;
using GeoWorld;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>
    /// Дороги (RoadAreas): оси OSM highway и railway из выгрузок GeoWorld (environment.json частей мира, roads[] и
    /// railways[]) с шириной экстрактора, покрытием, разметкой и износом. Мосты и тоннели пропускаются — под мостом трава
    /// растёт, над тоннелем тоже. Части мира перекрываются, дорога на стыке приходит из обеих — одинаковые (id и точки)
    /// берутся один раз, а куски одной линии, обрезанные по границам частей, сшиваются; концам дорог ставится вид — тупик,
    /// продолжение, примыкание (RoadJunctions). Потом оси подтягиваются к дороге на снимке тайлов (RoadSatelliteSnap), а
    /// асфальту в городе (вокруг застройка карты земель и мощёные тротуары или разметка) ставится бордюр. Без выгрузок на
    /// этой машине (пути в сцене мира — локальные) прежние дороги остаются.
    /// </summary>
    public static class RoadAreasBaker
    {
        private const string AssetPath = "Packages/com.tredokiller.geoworld/Environment/Resources/" + RoadAreas.ResourcePath + ".asset";
        private const string WorldScene = "Assets/GeoWorld/Tiled/kharkiv_east/kharkiv_east.unity";
        // ширина балласта, если у рельсов её нет
        private const float DefaultRailWidth = 4f;

        [MenuItem("Tools/Environment/Bake Road Areas")]
        public static void BakeMenu() => Debug.Log("[RoadAreasBaker] " + Bake());

        public static string Bake(bool snap = true)
        {
            GeoTiledWorld world = Object.FindFirstObjectByType<GeoTiledWorld>(FindObjectsInactive.Include);
            UnityEngine.SceneManagement.Scene opened = default;

            if (world == null && File.Exists(WorldScene))
            {
                opened = EditorSceneManager.OpenScene(WorldScene, OpenSceneMode.Additive);

                foreach (GameObject root in opened.GetRootGameObjects())
                {
                    world = root.GetComponentInChildren<GeoTiledWorld>(true);

                    if (world != null)
                        break;
                }
            }

            try
            {
                return world != null ? Bake(world, snap) : "no GeoTiledWorld found";
            }
            finally
            {
                if (opened.IsValid())
                    EditorSceneManager.CloseScene(opened, true);
            }
        }

        private static string Bake(GeoTiledWorld world, bool snap)
        {
            List<RoadAreas.Road> roads = new();
            List<string> ids = new();
            List<Rect> coverage = new();
            HashSet<string> seen = new();
            int files = 0;
            int skipped = 0;

            foreach (string path in world.parts.Select(part => part.environmentPath).Where(path => !string.IsNullOrEmpty(path)))
            {
                if (!File.Exists(path))
                    continue;

                files++;
                JObject root = JObject.Parse(File.ReadAllText(path));

                if (root["bounds"] is JObject bounds)
                {
                    coverage.Add(Rect.MinMaxRect((float)bounds["minX"], (float)bounds["minZ"], (float)bounds["maxX"], (float)bounds["maxZ"]));
                }

                foreach (JToken road in root["roads"] ?? new JArray())
                {
                    if (!Add(road, (float?)road["width"] ?? 3f, KindOf((string)road["material"]), IsMarked((string)road["type"])))
                        skipped++;
                }

                foreach (JToken rail in root["railways"] ?? new JArray())
                {
                    if (!Add(rail, (float?)rail["width"] ?? DefaultRailWidth, RoadAreas.Kind.Rail, false))
                        skipped++;
                }
            }

            RoadAreas asset = AssetDatabase.LoadAssetAtPath<RoadAreas>(AssetPath);

            if (files == 0)
                return $"no GeoWorld environment export found, {asset?.roads.Length ?? 0} roads kept";

            int stitched = RoadJunctions.Stitch(roads, ids);
            RoadJunctions.Links links = RoadJunctions.Detect(roads, out string ends);
            // сначала оси и ширина по снимку: по ширине город отличается от села
            string snapped = snap ? RoadSatelliteSnap.Snap(roads, world, links) : "not snapped";
            int curbs = MarkCurbs(roads, world.GetComponentInChildren<GeoLandCover>(true) ?? Object.FindFirstObjectByType<GeoLandCover>(FindObjectsInactive.Include));
            bool created = asset == null;

            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
                asset = ScriptableObject.CreateInstance<RoadAreas>();
            }

            asset.roads = roads.ToArray();
            asset.coverage = coverage.ToArray();

            if (created)
                AssetDatabase.CreateAsset(asset, AssetPath);
            else
                EditorUtility.SetDirty(asset);

            AssetDatabase.SaveAssetIfDirty(asset);
            return $"{roads.Count} roads from {files} exports ({skipped} bridges, tunnels and repeats skipped, {stitched} pieces stitched), " +
                   $"{roads.Sum(road => road.points.Length)} points, {curbs} with curbs; {ends}; {snapped}";

            bool Add(JToken feature, float width, RoadAreas.Kind kind, bool marked)
            {
                if ((bool?)feature["bridge"] == true || (bool?)feature["tunnel"] == true || ((int?)feature["layer"] ?? 0) != 0)
                    return false;

                float[] flat = feature["points"]?.Values<float>().ToArray() ?? System.Array.Empty<float>();

                if (flat.Length < 4)
                    return false;

                string key = (string)feature["id"] + "|" + flat.Length + "|" + flat[0] + "|" + flat[1];

                if (!seen.Add(key))
                    return false;

                Vector2[] points = new Vector2[flat.Length / 2];

                for (int i = 0; i < points.Length; i++)
                    points[i] = new Vector2(flat[i * 2], flat[i * 2 + 1]);

                float half = Mathf.Max(0.5f, width) * 0.5f;
                Vector2 min = points.Aggregate(Vector2.positiveInfinity, Vector2.Min) - Vector2.one * half;
                Vector2 max = points.Aggregate(Vector2.negativeInfinity, Vector2.Max) + Vector2.one * half;
                ids.Add((string)feature["id"]);
                roads.Add(new RoadAreas.Road
                {
                    width = Mathf.Max(0.5f, width), bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y), points = points,
                    kind = kind, marked = marked, wear = Mathf.Clamp01((float?)feature["wear"] ?? 0.5f)
                });
                return true;
            }
        }

        // Бордюр — у асфальта в городе: вдоль дороги (каждые 10 м, на оси и в 4 м за кромками) застройка карты земель (Urban,
        // Building, Asphalt) не меньше половины, и дорога размечена, широкая по снимку (городская улица от ~7 м, сельская —
        // 4–6) или рядом мощёное (тротуары, площади — Paving). В селе застройка тоже Urban — там обочина.
        private static int MarkCurbs(List<RoadAreas.Road> roads, GeoLandCover cover)
        {
            if (cover == null)
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

                            if (!cover.Sample(at.x, at.y, out GeoLandClass land))
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
        private static RoadAreas.Kind KindOf(string material) => material switch
        {
            "asphalt" => RoadAreas.Kind.Asphalt,
            "paving" => RoadAreas.Kind.Paving,
            "gravel" => RoadAreas.Kind.Gravel,
            "path" => RoadAreas.Kind.Path,
            _ => RoadAreas.Kind.Dirt
        };

        // Разметка — у магистралей и дорог областного и районного значения (как GeoRoadPhotos.IsMarked пакета).
        private static bool IsMarked(string type)
        {
            string highway = (type ?? "").Replace("_link", "");
            return highway is "motorway" or "trunk" or "primary" or "secondary" or "tertiary";
        }
    }
}
