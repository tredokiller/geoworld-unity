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
    /// Если рисунок слабый (связность ниже MinCoherence), остаётся длинная сторона. Без выгрузок на этой машине (пути в сцене
    /// мира — локальные) прежние поля остаются.
    /// </summary>
    public static class FieldAreasBaker
    {
        private const string AssetPath = "Packages/com.tredokiller.geoworld/Environment/Resources/" + FieldAreas.ResourcePath + ".asset";
        private const string WorldScene = "Assets/GeoWorld/Tiled/kharkiv_east/kharkiv_east.unity";
        private const int SatelliteSize = 1024;
        private const int SampleStep = 3;
        private const float EdgeMargin = 12f;
        private const float MinCoherence = 0.08f;
        private const int MinSamples = 400;

        [MenuItem("Tools/Environment/Bake Field Areas")]
        public static void BakeMenu() => Debug.Log("[FieldAreasBaker] " + Bake());

        public static string Bake()
        {
            List<FieldAreas.Field> fields = new();
            int files = 0;
            List<GeoTiledWorld.Tile> tiles = new();

            foreach (string path in EnvironmentPaths(tiles))
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

            FieldAreas asset = AssetDatabase.LoadAssetAtPath<FieldAreas>(AssetPath);

            if (files == 0)
                return $"no GeoWorld environment export found, {asset?.fields.Length ?? 0} fields kept";

            int fromSatellite = OrientBySatellite(fields, tiles);

            bool created = asset == null;

            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
                asset = ScriptableObject.CreateInstance<FieldAreas>();
            }

            asset.fields = fields.ToArray();

            if (created)
                AssetDatabase.CreateAsset(asset, AssetPath);
            else
                EditorUtility.SetDirty(asset);

            AssetDatabase.SaveAssetIfDirty(asset);
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

        // Направление рядков по снимку: тензор единичных градиентов яркости в поле. Возвращает, у скольких полей оно взято со
        // снимка.
        private static int OrientBySatellite(List<FieldAreas.Field> fields, List<GeoTiledWorld.Tile> tiles)
        {
            int n = fields.Count;
            double[] sxx = new double[n], sxy = new double[n], syy = new double[n];
            int[] counts = new int[n];
            RenderTexture rt = RenderTexture.GetTemporary(SatelliteSize, SatelliteSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            // своя, не ассет: UnloadUnusedAssetsImmediate между тайлами иначе её уничтожает
            Texture2D read = new(SatelliteSize, SatelliteSize, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            float[] lum = new float[SatelliteSize * SatelliteSize];

            try
            {
                for (int t = 0; t < tiles.Count; t++)
                {
                    GeoTiledWorld.Tile tile = tiles[t];
                    List<int> inside = new();

                    for (int f = 0; f < n; f++)
                    {
                        if (fields[f].bounds.Overlaps(tile.area))
                            inside.Add(f);
                    }

                    if (inside.Count == 0 || string.IsNullOrEmpty(tile.assetPath))
                        continue;

                    if (EditorUtility.DisplayCancelableProgressBar("Bake Field Areas", "Rows from the satellite: " + tile.index, t / (float)tiles.Count))
                        break;

                    TerrainData data = AssetDatabase.LoadAssetAtPath<TerrainData>(tile.assetPath);
                    Texture satellite = data != null ? Satellite(data) : null;

                    if (satellite == null)
                        continue;

                    RenderTexture previous = RenderTexture.active;
                    Graphics.Blit(satellite, rt);
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, SatelliteSize, SatelliteSize), 0, 0);
                    read.Apply(false);
                    RenderTexture.active = previous;
                    Color32[] pixels = read.GetPixels32();

                    for (int i = 0; i < lum.Length; i++)
                        lum[i] = (pixels[i].r * 0.299f + pixels[i].g * 0.587f + pixels[i].b * 0.114f) / 255f;

                    float cellX = tile.area.width / SatelliteSize;
                    float cellZ = tile.area.height / SatelliteSize;

                    foreach (int f in inside)
                    {
                        FieldAreas.Field field = fields[f];

                        for (int j = 1; j < SatelliteSize - 1; j += SampleStep)
                        {
                            float z = tile.area.yMin + (j + 0.5f) * cellZ;

                            if (z < field.bounds.yMin + EdgeMargin || z > field.bounds.yMax - EdgeMargin)
                                continue;

                            for (int i = 1; i < SatelliteSize - 1; i += SampleStep)
                            {
                                float x = tile.area.xMin + (i + 0.5f) * cellX;

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
                                sxx[f] += gx * gx;
                                sxy[f] += gx * gy;
                                syy[f] += gy * gy;
                                counts[f]++;
                            }
                        }
                    }

                    if (t % 20 == 0)
                        EditorUtility.UnloadUnusedAssetsImmediate();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(read);
            }

            int oriented = 0;

            for (int f = 0; f < n; f++)
            {
                if (counts[f] < MinSamples)
                    continue;

                double a = sxx[f] / counts[f], b = sxy[f] / counts[f], d = syy[f] / counts[f];
                double coherence = System.Math.Sqrt((a - d) * (a - d) + 4.0 * b * b) / System.Math.Max(a + d, 1e-6);

                if (coherence < MinCoherence)
                    continue;

                // главный градиент — под углом phi от востока к северу; линии поперёк него: азимут = −phi
                double phi = 0.5 * System.Math.Atan2(2.0 * b, a - d);
                fields[f].rotation = Mathf.Repeat((float)(-phi * Mathf.Rad2Deg), 180f);
                oriented++;
            }

            return oriented;
        }

        // Снимок тайла — слой террейна, растянутый на весь тайл (самая крупная текстура).
        internal static Texture Satellite(TerrainData data)
        {
            Texture best = null;

            foreach (TerrainLayer layer in data.terrainLayers)
            {
                if (layer != null && layer.diffuseTexture != null && (best == null || layer.diffuseTexture.width > best.width))
                    best = layer.diffuseTexture;
            }

            return best != null && best.width >= 1024 ? best : null;
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

        // Пути environment.json частей мира и его тайлы — из его GeoTiledWorld (сцена мира открывается в фоне и закрывается).
        internal static IEnumerable<string> EnvironmentPaths(List<GeoTiledWorld.Tile> tiles)
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

            List<string> paths = world != null ? world.parts.Select(p => p.environmentPath).Where(p => !string.IsNullOrEmpty(p)).ToList() : new List<string>();

            if (world != null)
                tiles.AddRange(world.tiles.Where(tile => !tile.empty));

            if (opened.IsValid())
                EditorSceneManager.CloseScene(opened, true);

            return paths;
        }
    }
}
