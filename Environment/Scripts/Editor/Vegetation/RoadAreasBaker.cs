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
    /// асфальту в городе (вокруг застройка карты земель и мощёные тротуары или разметка) ставится бордюр. Пишет в папку мира
    /// (WorldMapBaker: &lt;папка мира&gt;/Environment/Road areas, на его WorldMapData). Без выгрузок на этой машине (пути в
    /// мире — локальные) прежние дороги остаются.
    /// </summary>
    public static class RoadAreasBaker
    {
        private const string AssetName = "Road areas";
        // ширина балласта, если у рельсов её нет
        private const float DefaultRailWidth = 4f;

        [MenuItem("Tools/Environment/Bake Road Areas")]
        public static void BakeMenu()
        {
            GeoTiledWorld world = WorldMapBaker.FindWorld();
            Debug.Log("[RoadAreasBaker] " + (world != null ? Bake(world) : WorldMapBaker.NoWorld));
        }

        public static string Bake(GeoTiledWorld world, bool snap = true)
        {
            List<RoadAreas.Road> roads = new();
            List<string> ids = new();
            List<Rect> coverage = new();
            HashSet<string> seen = new();
            int files = 0;
            int skipped = 0;

            foreach (string path in WorldMapBaker.EnvironmentPaths(world))
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
                    if (!Add(road, (float?)road["width"] ?? 3f, WorldMapExport.KindOf((string)road["material"]), WorldMapExport.IsMarked((string)road["type"])))
                        skipped++;
                }

                foreach (JToken rail in root["railways"] ?? new JArray())
                {
                    if (!Add(rail, (float?)rail["width"] ?? DefaultRailWidth, RoadAreas.Kind.Rail, false))
                        skipped++;
                }
            }

            RoadAreas asset = WorldMapBaker.Asset<RoadAreas>(world, AssetName, false);

            if (files == 0)
                return $"no GeoWorld environment export found, {asset?.roads.Length ?? 0} roads kept";

            int stitched = RoadJunctions.Stitch(roads, ids);
            RoadJunctions.Links links = RoadJunctions.Detect(roads, out string ends);
            // сначала оси и ширина по снимку: по ширине город отличается от села
            string snapped = snap ? SnapToSatellite(roads, world, links) : "not snapped";
            GeoLandCover cover = world.GetComponentInChildren<GeoLandCover>(true) ?? Object.FindFirstObjectByType<GeoLandCover>(FindObjectsInactive.Include);
            int curbs = WorldMapExport.MarkCurbs(roads, cover != null ? (x, z) => cover.Sample(x, z, out GeoLandClass land) ? land : GeoLandClass.None : null);
            asset = WorldMapBaker.Asset<RoadAreas>(world, AssetName, true);
            asset.roads = roads.ToArray();
            asset.coverage = coverage.ToArray();
            asset.ResetIndex();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            WorldMapBaker.Attach(world, data => data.roads = asset);
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
        // Подтяжка осей к снимку (RoadSatelliteSnap) по снимкам тайлов мира из их ассетов.
        private static string SnapToSatellite(List<RoadAreas.Road> roads, GeoTiledWorld world, RoadJunctions.Links links)
        {
            RoadSatelliteSnap.Job job = RoadSatelliteSnap.Begin(roads, world.origin, world.tileSize);
            int wanted = Mathf.Max(1, world.tiles.Count(tile => job.Wants(tile.index)));
            int done = 0;

            using (SatelliteImage.Reader reader = new(RoadSatelliteSnap.SatelliteSize, false))
            {
                try
                {
                    foreach (GeoTiledWorld.Tile tile in world.tiles)
                    {
                        if (tile.empty || string.IsNullOrEmpty(tile.assetPath) || !job.Wants(tile.index))
                            continue;

                        if (EditorUtility.DisplayCancelableProgressBar("Bake Road Areas", "Roads on the satellite: " + tile.index, done / (float)wanted))
                            break;

                        done++;
                        Texture satellite = SatelliteImage.Of(AssetDatabase.LoadAssetAtPath<TerrainData>(tile.assetPath));

                        if (satellite == null)
                            continue;

                        RoadSatelliteSnap.AddTile(job, tile.index, tile.area, reader.Read(satellite));

                        if (done % 20 == 0)
                            EditorUtility.UnloadUnusedAssetsImmediate();
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            return RoadSatelliteSnap.Finish(job, links);
        }
    }
}
