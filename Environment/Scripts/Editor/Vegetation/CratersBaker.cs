using System.Collections.Generic;
using System.IO;
using System.Linq;
using _Game.Scripts.World;
using GeoWorld;
using GeoWorld.Editor;
using UnityEditor;
using UnityEngine;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>
    /// Воронки мира (CraterField) из его выгрузок: свои воронки выгрузки и бой рядом с ней (combat.json, GeoCombatBake) — тот
    /// же список, которым генератор врезал их в землю тайлов. Где выгрузки перекрываются, воронка берётся из той, чья это
    /// земля по сетке тайлов мира (GeoTilePlan.OwnerOf — так её выбирает и тайл, GeoTileSource); окопы — из всех, как у
    /// тайлов. Пишет в папку мира (WorldMapBaker: &lt;папка мира&gt;/Environment/Craters, на его WorldMapData); мир без
    /// воронок их не получает (ассет не создаётся). Без выгрузок на этой машине прежние воронки остаются.
    /// </summary>
    public static class CratersBaker
    {
        private const string AssetName = "Craters";

        [MenuItem("Tools/Environment/Bake Craters")]
        public static void BakeMenu()
        {
            GeoTiledWorld world = WorldMapBaker.FindWorld();
            Debug.Log("[CratersBaker] " + (world != null ? Bake(world) : WorldMapBaker.NoWorld));
        }

        public static string Bake(GeoTiledWorld world)
        {
            List<CraterField.Crater> craters = new();
            List<CraterField.Trench> trenches = new();
            List<EnvironmentData> exports = new();
            int missing = 0;

            foreach (string path in WorldMapBaker.EnvironmentPaths(world))
            {
                if (File.Exists(path))
                    exports.Add(GeoData.ReadEnvironment(path));
                else
                    missing++;
            }

            CraterField asset = WorldMapBaker.Asset<CraterField>(world, AssetName, false);
            int files = exports.Count;

            if (files == 0)
                return $"no GeoWorld environment export found, {asset?.craters.Length ?? 0} craters kept";

            GeoTilePlan plan = files > 1 ? GeoTilePlan.Make(exports.Select(e => e.bounds).ToList(), world.tileSize) : null;

            for (int i = 0; i < files; i++)
            {
                foreach (TrenchFeature trench in exports[i].trenches)
                    trenches.Add(CraterField.From(trench));

                foreach (CraterFeature crater in exports[i].craters)
                {
                    if (plan == null || plan.OwnerOf(crater.x, crater.z) == i)
                        craters.Add(CraterField.From(crater));
                }
            }

            if (craters.Count == 0 && trenches.Count == 0 && asset == null)
                return "none in the exports";

            asset = WorldMapBaker.Asset<CraterField>(world, AssetName, true);
            asset.craters = craters.ToArray();
            asset.trenches = trenches.ToArray();
            asset.report = $"{craters.Count} craters and {trenches.Count} trenches from {files} exports" +
                           (missing > 0 ? $" ({missing} more not on this machine)" : "");
            asset.ResetIndex();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            WorldMapBaker.Attach(world, data => data.craters = asset);
            return asset.report;
        }
    }
}
