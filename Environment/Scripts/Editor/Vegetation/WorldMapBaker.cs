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
    /// поставлен из git, пакет только для чтения. Вешает WorldMapData на объект мира — и на копии этого мира в открытых
    /// сценах (в игре объект мира лежит и в её сцене, не только в сцене мира). Запекается после генерации тайлов (событие
    /// GeoTiles.AfterBake: если тайлы строились или данных у мира нет), кнопкой в инспекторе WorldMapData и
    /// Tools/Environment/Bake World Map Data. Без выгрузок на этой машине (пути в мире — локальные) прежние данные остаются.
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
            Debug.Log("[WorldMapBaker] " + (world != null ? Bake(world) : NoWorld));
        }

        public const string NoWorld = "no Geo Tiled World in the open scenes: open the world's scene (or a scene with its object)";

        public static string Folder(GeoTiledWorld world) =>
            (string.IsNullOrEmpty(world.folder) ? GeoPaths.Project : world.folder.TrimEnd('/')) + "/Environment";

        /// <summary>Дороги, поля и контуры лесов мира — всё, что есть в его выгрузках.</summary>
        public static string Bake(GeoTiledWorld world, bool snap = true)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            string report = "Roads: " + RoadAreasBaker.Bake(world, snap) + "\nFields: " + FieldAreasBaker.Bake(world) +
                            "\nLeaf types: " + TreeLeafAreasBaker.BakeLeafAreas(world);
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

                if (copy.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(copy.gameObject.scene);
            }
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
}
