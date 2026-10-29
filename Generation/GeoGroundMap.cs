using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // The ground maps of a world's exports (materials.png: a material id a cell of a few metres, rows from the north), read
    // once, and what they say the ground is anywhere: where exports overlap, the export that builds the ground at the point
    // (GeoTilePlan.OwnerOf) says it, else any export whose map has the point. A tile's land cover (GeoLandCoverData) takes the
    // most common class of Samples x Samples points in each of its cells, so a track 4 m wide across a field of 10 m cells
    // leaves the field a field, and a road that fills most of a cell keeps the cell.
    public sealed class GeoGroundMap
    {
        public const int Samples = 4;

        sealed class Map
        {
            public MaterialMapInfo info;
            public byte[] ids;             // the map's ids, rows from the south (as the texture's)
            public GeoLandClass[] classes; // id -> class
        }

        readonly GeoTilePlan plan;
        readonly Map[] maps; // by export, in the plan's order; null: the export has no ground map

        public bool Any => Array.Exists(maps, m => m != null);

        // infos and ids by export, in the plan's order (ids: a byte a cell, rows from the south; null: the export has no map).
        public GeoGroundMap(GeoTilePlan plan, IList<MaterialMapInfo> infos, IList<byte[]> ids, List<string> notes = null)
        {
            this.plan = plan;
            maps = new Map[infos.Count];
            var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                if (info == null || ids[i] == null || info.width < 1 || info.height < 1 || info.cellSize <= 0 || ids[i].Length != info.width * info.height) continue;
                var classes = new GeoLandClass[256];
                // an export without its legend has the app's own table (materials.py), whose ids are GeoLandClass's
                if (info.classes == null || info.classes.Count == 0)
                    for (int c = 0; c < GeoLandCover.ClassCount; c++) classes[c] = (GeoLandClass)c;
                else
                    foreach (var c in info.classes)
                    {
                        if (c == null || c.id < 0 || c.id > 255) continue;
                        classes[c.id] = GeoLandCover.ClassOf(c.name);
                        if (classes[c.id] == GeoLandClass.None && !string.IsNullOrEmpty(c.name) && !string.Equals(c.name, "none", StringComparison.OrdinalIgnoreCase)) unknown.Add(c.name);
                    }
                maps[i] = new Map { info = info, ids = ids[i], classes = classes };
            }
            if (unknown.Count > 0) notes?.Add("Land cover: classes of the ground map that the runtime does not know are left out (None): " + string.Join(", ", unknown) + ".");
        }

        // The ground maps of a world's exports (their environment.json, in the world's order) on tiles of tileSize metres.
        public static GeoGroundMap Read(IList<string> environmentPaths, float tileSize, List<string> notes)
        {
            var bounds = new List<GeoBounds>();
            var infos = new List<MaterialMapInfo>();
            var ids = new List<byte[]>();
            foreach (var path in environmentPaths)
            {
                EnvironmentData env;
                try { env = GeoData.ReadEnvironment(path); }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
                {
                    notes?.Add("Land cover: " + path + " is left out (" + e.Message + ").");
                    continue;
                }
                bounds.Add(env.bounds); infos.Add(env.materials); ids.Add(ReadIds(env, path, notes));
            }
            if (bounds.Count == 0) throw new InvalidDataException("none of the world's exports (environment.json) could be read");
            return new GeoGroundMap(GeoTilePlan.Make(bounds, tileSize), infos, ids, notes);
        }

        // The material ids of an export's ground map, rows from the south; null without one.
        static byte[] ReadIds(EnvironmentData env, string environmentPath, List<string> notes)
        {
            var info = env.materials;
            string name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(environmentPath)));
            if (info == null || string.IsNullOrEmpty(info.file)) { notes?.Add("Land cover: " + name + " has no ground map (materials.png)."); return null; }
            if (info.width < 1 || info.height < 1 || (long)info.width * info.height > 17000000 || info.cellSize <= 0)
            { notes?.Add("Land cover: the ground map of " + name + " has invalid metadata."); return null; }
            var map = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!map.LoadImage(File.ReadAllBytes(GeoData.PackageFile(environmentPath, info.file))) || map.width != info.width || map.height != info.height)
                { notes?.Add("Land cover: the ground map of " + name + " could not be read or does not match environment.json."); return null; }
                var pixels = map.GetPixels32();
                var ids = new byte[pixels.Length];
                for (int i = 0; i < pixels.Length; i++) ids[i] = pixels[i].r;
                return ids;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                notes?.Add("Land cover: the ground map of " + name + " is left out (" + e.Message + ").");
                return null;
            }
            finally { Object.DestroyImmediate(map); }
        }

        // What the ground is at a point.
        public GeoLandClass At(float x, float z)
        {
            int owner = plan.OwnerOf(x, z);
            if (owner >= 0 && owner < maps.Length && Class(maps[owner], x, z, out var value)) return value;
            for (int i = 0; i < maps.Length; i++) if (i != owner && Class(maps[i], x, z, out value)) return value;
            return GeoLandClass.None;
        }

        static bool Class(Map map, float x, float z, out GeoLandClass value)
        {
            value = GeoLandClass.None;
            if (map == null) return false;
            var info = map.info;
            int column = Mathf.FloorToInt((x - info.minX) / info.cellSize), rowNorth = Mathf.FloorToInt((info.maxZ - z) / info.cellSize);
            if (column < 0 || column >= info.width || rowNorth < 0 || rowNorth >= info.height) return false;
            value = map.classes[map.ids[(info.height - 1 - rowNorth) * info.width + column]];
            return value != GeoLandClass.None;
        }

        // A tile's cells (resolution along a side, rows from the south) as GeoLandClass values: the most common class among the
        // points of each cell, None only where none of them has one; a tie goes to the lower value.
        public byte[] Cells(Rect area, int resolution)
        {
            var cells = new byte[resolution * resolution];
            var counts = new int[GeoLandCover.ClassCount];
            float width = area.width / resolution, depth = area.height / resolution;
            for (int row = 0; row < resolution; row++)
                for (int column = 0; column < resolution; column++)
                {
                    Array.Clear(counts, 0, counts.Length);
                    for (int b = 0; b < Samples; b++)
                        for (int a = 0; a < Samples; a++)
                            counts[(int)At(area.xMin + (column + (a + 0.5f) / Samples) * width, area.yMin + (row + (b + 0.5f) / Samples) * depth)]++;
                    int best = 0;
                    for (int c = 1; c < counts.Length; c++) if (counts[c] > (best == 0 ? 0 : counts[best])) best = c;
                    cells[row * resolution + column] = (byte)best;
                }
            return cells;
        }
    }
}
