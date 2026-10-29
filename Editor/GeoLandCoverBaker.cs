using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Bakes a tiled world's land cover (GeoLandCover on the world's object, a GeoLandCoverData a tile in "<world folder>/Land
    // cover") from its exports' ground maps (GeoGroundMap), on the tiles' grid. GeoTiles.Generate bakes it after building
    // tiles; the Geo Tiled World's Inspector has Bake land cover for a world made earlier. No tile is rebuilt or opened (the
    // land cover is the export's, not the tile's), and the asset of a tile whose land cover is the same is left as it is.
    public static class GeoLandCoverBaker
    {
        // About the ground map's own detail: WorldCover is 10 m (OpenStreetMap's shapes are painted on it a few metres a cell).
        public const float CellMetres = 10;

        public static string Folder(GeoTiledWorld world) => (string.IsNullOrEmpty(world.folder) ? GeoPaths.Project : world.folder.TrimEnd('/')) + "/Land cover";
        public static string AssetPath(GeoTiledWorld world, Vector2Int index) => Folder(world) + "/Tile " + index.x + "_" + index.y + ".asset";
        // Cells along a tile's side: 100 of 10 m on tiles of 1 km.
        public static int Resolution(float tileSize) => Mathf.Clamp(Mathf.RoundToInt(tileSize / CellMetres), 1, 1024);

        public static GeoLandCover Bake(GeoTiledWorld world, List<string> notes = null)
        {
            if (!world) throw new ArgumentNullException(nameof(world));
            if (world.tiles.Count == 0 || world.tileSize <= 0) throw new InvalidOperationException(world.name + " has no tiles to bake the land cover of.");
            notes ??= new List<string>();
            try
            {
                Bar("Reading the exports' ground maps", 0);
                var map = GeoGroundMap.Read(Exports(world), world.tileSize, notes);
                if (!map.Any)
                {
                    notes.Add("Land cover: not made, the world's exports have no ground map (materials.png).");
                    return world.GetComponent<GeoLandCover>();
                }
                int resolution = Resolution(world.tileSize);
                string folder = EnsureFolder(Folder(world));
                var classes = (string[])GeoLandCover.Names.Clone();
                var counts = new long[GeoLandCover.ClassCount];
                var paths = new HashSet<string>();
                var changed = new List<GeoLandCoverData>();
                int written = 0;
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int t = 0; t < world.tiles.Count; t++)
                    {
                        var tile = world.tiles[t];
                        Bar($"Tile {tile.index.x}, {tile.index.y} ({t + 1} / {world.tiles.Count})", 0.05f + 0.9f * t / world.tiles.Count);
                        var area = tile.area.width > 0 && tile.area.height > 0 ? tile.area : Area(world, tile.index);
                        var cells = map.Cells(area, resolution);
                        foreach (byte value in cells) counts[value]++;
                        string path = AssetPath(world, tile.index);
                        paths.Add(path);
                        var data = AssetDatabase.LoadAssetAtPath<GeoLandCoverData>(path);
                        if (data && Same(data, tile.index, area, resolution, cells, classes)) continue;
                        if (data)
                        {
                            data.Set(tile.index, area, resolution, cells, classes);
                            EditorUtility.SetDirty(data);
                            changed.Add(data);
                        }
                        else
                        {
                            data = ScriptableObject.CreateInstance<GeoLandCoverData>();
                            data.Set(tile.index, area, resolution, cells, classes);
                            AssetDatabase.CreateAsset(data, path);
                        }
                        written++;
                    }
                }
                finally { AssetDatabase.StopAssetEditing(); }
                // its own assets only: after Play mode a project's assets can be changed in memory (a sky asset keeps the
                // weather it was given while playing), and AssetDatabase.SaveAssets would write them all to disk
                foreach (var data in changed) AssetDatabase.SaveAssetIfDirty(data);
                int removed = RemoveOthers(folder, paths);
                var tiles = world.tiles.Select(t => AssetDatabase.LoadAssetAtPath<GeoLandCoverData>(AssetPath(world, t.index))).Where(d => d).ToList();
                var cover = Attach(world, tiles, resolution, Summary(tiles.Count, resolution, world.tileSize / resolution, counts));
                notes.Add("Land cover: " + cover.report + (written == 1 ? " 1 tile written" : written > 0 ? $" {written} tiles written" : " No tile changed") +
                          (removed > 0 ? $", the land cover of {removed} tiles no longer in the world removed." : "."));
                return cover;
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // The exports the world is made of, in its order (the earlier owns the ground where they overlap).
        static List<string> Exports(GeoTiledWorld world)
        {
            var paths = new List<string>();
            if (world.parts != null)
                foreach (var part in world.parts) if (part != null && !string.IsNullOrEmpty(part.environmentPath)) paths.Add(part.environmentPath);
            if (paths.Count == 0 && !string.IsNullOrEmpty(world.environmentPath)) paths.Add(world.environmentPath);
            return paths;
        }

        static Rect Area(GeoTiledWorld world, Vector2Int index) =>
            new Rect(world.origin.x + index.x * world.tileSize, world.origin.y + index.y * world.tileSize, world.tileSize, world.tileSize);

        static bool Same(GeoLandCoverData data, Vector2Int index, Rect area, int resolution, byte[] cells, string[] classes) =>
            data.tile == index && data.area == area && data.resolution == resolution && data.cells != null && data.cells.SequenceEqual(cells) &&
            data.classes != null && data.classes.SequenceEqual(classes);

        // "400 tiles of 100 x 100 cells of 10 m (3.8 MB): cropland 36 %, forest 28 %, ..." (the classes of 1 % or more)
        static string Summary(int tiles, int resolution, float cell, long[] counts)
        {
            long total = counts.Skip(1).Sum();
            var shares = Enumerable.Range(1, counts.Length - 1).Where(c => total > 0 && counts[c] * 100 >= total).OrderByDescending(c => counts[c])
                .Select(c => $"{GeoLandCover.Names[c].Replace('_', ' ')} {100.0 * counts[c] / total:0} %");
            double bytes = (double)tiles * resolution * resolution;
            string size = bytes >= 1048576 ? $"{bytes / 1048576:0.#} MB" : $"{bytes / 1024:0.#} KB";
            return $"{tiles} tiles of {resolution} x {resolution} cells of {cell:0.#} m ({size}): " + string.Join(", ", shares) + ".";
        }

        // Assets of tiles the world no longer has.
        static int RemoveOthers(string folder, HashSet<string> keep)
        {
            int removed = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(GeoLandCoverData), new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (keep.Contains(path) || Path.GetDirectoryName(path)?.Replace('\\', '/') != folder) continue;
                if (AssetDatabase.DeleteAsset(path)) removed++;
            }
            return removed;
        }

        static string EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return folder;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent)) throw new InvalidOperationException("Cannot make the folder " + folder + ".");
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            return folder;
        }

        // The world's object answers what the ground is.
        static GeoLandCover Attach(GeoTiledWorld world, List<GeoLandCoverData> tiles, int resolution, string report)
        {
            var cover = world.GetComponent<GeoLandCover>();
            if (!cover) cover = Undo.AddComponent<GeoLandCover>(world.gameObject);
            else Undo.RecordObject(cover, "Geo World land cover");
            cover.origin = world.origin; cover.tileSize = world.tileSize; cover.cellSize = world.tileSize / resolution;
            cover.tiles = tiles; cover.report = report;
            EditorUtility.SetDirty(cover);
            if (world.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            return cover;
        }

        static void Bar(string label, float value)
        {
            if (EditorUtility.DisplayCancelableProgressBar("Geo World land cover", label, value))
                throw new OperationCanceledException("The land cover was cancelled.");
        }
    }
}
