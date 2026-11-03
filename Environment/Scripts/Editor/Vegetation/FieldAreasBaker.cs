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
    /// Поля для рядков и колей (FieldAreas): контуры OSM landuse=farmland из выгрузок GeoWorld (environment.json частей мира,
    /// areas[]). Направление рядков — по самому снимку поля: длинная сторона поля (rotation экстрактора) часто не та — на
    /// kharkiv_east колеи на снимке шли поперёк неё, и рисунок шейдера ложился клеткой поверх настоящих. Снимок тайлов
    /// читается по 1 м на пиксель, в поле (дальше 12 м от края — лесополосы и дороги не в счёт) копится тензор градиентов
    /// (каждый градиент — единичный, чтобы редкие резкие края не перевешивали); линии идут поперёк главного градиента.
    /// Если рисунок слабый (связность ниже MinCoherence), остаётся длинная сторона. Пишет в папку мира (WorldMapBaker:
    /// &lt;папка мира&gt;/Environment/Field areas, на его WorldMapData). Без выгрузок на этой машине (пути в мире — локальные)
    /// прежние поля остаются.
    /// </summary>
    public static class FieldAreasBaker
    {
        private const string AssetName = "Field areas";

        [MenuItem("Tools/Environment/Bake Field Areas")]
        public static void BakeMenu()
        {
            GeoTiledWorld world = WorldMapBaker.FindWorld();
            Debug.Log("[FieldAreasBaker] " + (world != null ? Bake(world) : WorldMapBaker.NoWorld));
        }

        public static string Bake(GeoTiledWorld world)
        {
            List<FieldAreas.Field> fields = new();
            int files = 0;
            List<GeoTiledWorld.Tile> tiles = world.tiles.Where(tile => !tile.empty).ToList();

            foreach (string path in WorldMapBaker.EnvironmentPaths(world))
            {
                if (!File.Exists(path))
                    continue;

                files++;
                JObject root = JObject.Parse(File.ReadAllText(path));

                foreach (JToken area in root["areas"] ?? new JArray())
                {
                    string tag = (string)area["tag"] ?? "";
                    string material = (string)area["material"] ?? "";

                    if (tag != "landuse=farmland" && material != "cropland")
                        continue;

                    if (area["rings"] is not JArray rings || rings.Count == 0)
                        continue;

                    FieldAreas.Field field = new()
                    {
                        rotation = Mathf.Repeat((float?)area["rotation"] ?? 0f, 180f),
                        outer = Ring(rings[0])
                    };

                    if (field.outer.Length < 3)
                        continue;

                    List<Vector2> holes = new();
                    List<int> starts = new();

                    for (int r = 1; r < rings.Count; r++)
                    {
                        starts.Add(holes.Count);
                        holes.AddRange(Ring(rings[r]));
                    }

                    field.holes = holes.ToArray();
                    field.holeStarts = starts.ToArray();
                    Vector2 min = field.outer.Aggregate(Vector2.positiveInfinity, Vector2.Min);
                    Vector2 max = field.outer.Aggregate(Vector2.negativeInfinity, Vector2.Max);
                    field.bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                    fields.Add(field);
                }
            }

            FieldAreas asset = WorldMapBaker.Asset<FieldAreas>(world, AssetName, false);

            if (files == 0)
                return $"no GeoWorld environment export found, {asset?.fields.Length ?? 0} fields kept";

            int fromSatellite = OrientBySatellite(fields, tiles);

            asset = WorldMapBaker.Asset<FieldAreas>(world, AssetName, true);
            asset.fields = fields.ToArray();
            asset.ResetIndex();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            WorldMapBaker.Attach(world, data => data.fields = asset);
            return $"{fields.Count} fields from {files} exports, rows from the satellite on {fromSatellite} (the rest along the long side)";
        }

        private static Vector2[] Ring(JToken ring)
        {
            float[] flat = ring.Values<float>().ToArray();
            Vector2[] points = new Vector2[flat.Length / 2];

            for (int i = 0; i < points.Length; i++)
                points[i] = new Vector2(flat[i * 2], flat[i * 2 + 1]);

            return points;
        }

        // Направление рядков по снимку (FieldRowsSatellite) по снимкам тайлов мира из их ассетов. Возвращает, у скольких полей
        // оно взято со снимка.
        private static int OrientBySatellite(List<FieldAreas.Field> fields, List<GeoTiledWorld.Tile> tiles)
        {
            FieldRowsSatellite.Job job = FieldRowsSatellite.Begin(fields);

            using (SatelliteImage.Reader reader = new(FieldRowsSatellite.SatelliteSize, true))
            {
                try
                {
                    for (int t = 0; t < tiles.Count; t++)
                    {
                        GeoTiledWorld.Tile tile = tiles[t];

                        if (string.IsNullOrEmpty(tile.assetPath) || !job.Wants(tile.area))
                            continue;

                        if (EditorUtility.DisplayCancelableProgressBar("Bake Field Areas", "Rows from the satellite: " + tile.index, t / (float)tiles.Count))
                            break;

                        Texture satellite = SatelliteImage.Of(AssetDatabase.LoadAssetAtPath<TerrainData>(tile.assetPath));

                        if (satellite == null)
                            continue;

                        FieldRowsSatellite.AddTile(job, tile.area, reader.Read(satellite));

                        if (t % 20 == 0)
                            EditorUtility.UnloadUnusedAssetsImmediate();
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            return FieldRowsSatellite.Finish(job);
        }
    }
}