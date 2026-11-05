using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using _Game.Scripts.World;
using GeoWorld;
using GeoWorld.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>
    /// Данные карты мира для слоя окружения (WorldMapData): дороги (RoadAreasBaker), поля (FieldAreasBaker) и контуры лесов с
    /// типом листвы (TreeLeafAreasBaker) из выгрузок мира (environment.json его частей). Пишет их в папку мира
    /// (&lt;папка мира&gt;/Environment: Road areas, Field areas, Tree leaf areas) — никогда в пакет: в проекте, где он
    /// поставлен из git, пакет только для чтения. Вешает WorldMapData и WorldEnvironment (весь слой окружения) на объект мира — и на копии этого мира в открытых
    /// сценах (в игре объект мира лежит и в её сцене, не только в сцене мира). Запекается после генерации тайлов (событие
    /// GeoTiles.AfterBake: если тайлы строились или данных у мира нет), кнопкой в инспекторе WorldMapData и
    /// Tools/Environment/Bake World Map Data. Нет хоть одной выгрузки на этой машине (пути в мире — локальные) — бейк
    /// останавливается с ошибкой (RequireExports) и прежние данные не трогает: молча оставленные старые данные не видно.
    /// </summary>
    [InitializeOnLoad]
    public static class WorldMapBaker
    {
        static WorldMapBaker()
        {
            GeoTiles.AfterBake -= AfterTiles;
            GeoTiles.AfterBake += AfterTiles;
        }

        [MenuItem("Tools/Environment/Bake World Map Data")]
        public static void BakeMenu()
        {
            GeoTiledWorld world = FindWorld();
            Log("WorldMapBaker", () => world != null ? Bake(world) : NoWorld);
        }

        /// <summary>Отчёт бейка в консоль; бейк, остановленный без выгрузок (RequireExports), — ошибкой.</summary>
        public static void Log(string tag, Func<string> bake, Object context = null)
        {
            try
            {
                Debug.Log($"[{tag}] {bake()}", context);
            }
            catch (MissingExportException e)
            {
                Debug.LogError($"[{tag}] {e.Message}", context);
            }
        }

        public const string NoWorld = "no Geo Tiled World in the open scenes: open the world's scene (or a scene with its object)";

        public static string Folder(GeoTiledWorld world) =>
            (string.IsNullOrEmpty(world.folder) ? GeoPaths.Project : world.folder.TrimEnd('/')) + "/Environment";

        /// <summary>Дороги, поля, контуры лесов и воронки мира — всё, что есть в его выгрузках.</summary>
        public static string Bake(GeoTiledWorld world, bool snap = true)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            string report = "Roads: " + RoadAreasBaker.Bake(world, snap) + "\nFields: " + FieldAreasBaker.Bake(world) +
                            "\nLeaf types: " + TreeLeafAreasBaker.BakeLeafAreas(world) + "\nCraters: " + CratersBaker.Bake(world);
            Attach(world, data => data.report = report);
            return report;
        }

        // После генерации тайлов: заново, если тайлы строились, или впервые, если у мира данных ещё нет.
        private static string AfterTiles(GeoTiledWorld world, bool built)
        {
            WorldMapData data = world.GetComponent<WorldMapData>();

            if (!built && data != null && data.roads != null)
                return null;

            return "World map data (environment): " + Bake(world).Replace("\n", "; ");
        }

        /// <summary>Мир в открытых сценах (первый), в том числе выключенный.</summary>
        public static GeoTiledWorld FindWorld()
        {
            if (Selection.activeGameObject != null && Selection.activeGameObject.GetComponentInParent<GeoTiledWorld>() is { } selected)
                return selected;

            return Object.FindFirstObjectByType<GeoTiledWorld>(FindObjectsInactive.Include);
        }

        /// <summary>Ассет данных мира в его папке; null — его нет и create false.</summary>
        public static T Asset<T>(GeoTiledWorld world, string name, bool create) where T : ScriptableObject
        {
            string path = Folder(world) + "/" + name + ".asset";
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);

            if (asset != null || !create)
                return asset;

            EnsureFolder(Folder(world));
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        /// <summary>
        /// Ставит данные на WorldMapData мира и на копии этого мира в открытых сценах (тот же folder), добавляя компонент,
        /// где его нет; сцены помечаются изменёнными (сохраняет их тот, кто запекал).
        /// </summary>
        public static void Attach(GeoTiledWorld world, Action<WorldMapData> set)
        {
            IEnumerable<GeoTiledWorld> copies = Object.FindObjectsByType<GeoTiledWorld>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(other => other == world || !string.IsNullOrEmpty(world.folder) && other.folder == world.folder);

            foreach (GeoTiledWorld copy in copies.Append(world).Distinct())
            {
                WorldMapData data = copy.GetComponent<WorldMapData>();

                if (data == null)
                    data = Undo.AddComponent<WorldMapData>(copy.gameObject);
                else
                    Undo.RecordObject(data, "World map data");

                set(data);
                EditorUtility.SetDirty(data);

                // и сам слой окружения: мир с данными карты выглядит как в игре и без её кода
                WorldEnvironment environment = copy.GetComponent<WorldEnvironment>();

                if (environment == null)
                    environment = Undo.AddComponent<WorldEnvironment>(copy.gameObject);
                else
                    Undo.RecordObject(environment, "World environment");

                environment.SetDefaults();
                EditorUtility.SetDirty(environment);

                if (copy.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(copy.gameObject.scene);
            }
        }

        /// <summary>
        /// Пути environment.json частей мира, если все они есть на этой машине; иначе MissingExportException с теми, которых
        /// нет. Без части выгрузок бейк записал бы данные без её дорог, полей и лесов, а без всех — оставил бы старые.
        /// </summary>
        public static List<string> RequireExports(GeoTiledWorld world)
        {
            List<string> paths = EnvironmentPaths(world);

            if (paths.Count == 0)
                throw new MissingExportException($"{world.name} has no environment export path: nothing baked, the world's map data is unchanged.");

            List<string> missing = paths.Where(path => !File.Exists(path)).ToList();

            if (missing.Count > 0)
                throw new MissingExportException($"{world.name}: environment export not found on this machine: {string.Join(", ", missing)}. " +
                                                 "Nothing baked, the world's map data is unchanged. Point the world's parts at their exports or extract the area again.");

            return paths;
        }

        /// <summary>Пути environment.json частей мира, в его порядке.</summary>
        public static List<string> EnvironmentPaths(GeoTiledWorld world)
        {
            List<string> paths = world.parts != null
                ? world.parts.Where(part => part != null && !string.IsNullOrEmpty(part.environmentPath)).Select(part => part.environmentPath).ToList()
                : new List<string>();

            if (paths.Count == 0 && !string.IsNullOrEmpty(world.environmentPath))
                paths.Add(world.environmentPath);

            return paths;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');

            if (string.IsNullOrEmpty(parent))
                throw new InvalidOperationException("Cannot make the folder " + folder + ".");

            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }

    /// <summary>Бейк данных карты остановлен: у мира нет выгрузок на этой машине (WorldMapBaker.RequireExports).</summary>
    public sealed class MissingExportException : InvalidOperationException
    {
        public MissingExportException(string message) : base(message)
        {
        }
    }
}
