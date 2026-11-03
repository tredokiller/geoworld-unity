using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace GeoWorld
{
    // A world package's land cover: the export's ground map on the tiles' grid (GeoGroundMap.Cells), as GeoLandCoverBaker bakes it
    // for a tiled world in the editor, in one file next to world.json (landcover.bin, the extra "land cover"). Opening the package
    // (GeoWorldPackage.Open) puts it on the host as a GeoLandCover, so a game asks what the ground is (GeoLandCover.TrySample) in a
    // package the same way as in a tiled world, and the trees take their heights from it (GeoTreeHeights).
    //   "GLC1", then a deflate stream: resolution, the class names (GeoLandCover.Names: a cell's value is the index of its name),
    //   the tiles (index, area, resolution² bytes, rows from the south).
    public static class GeoPackageLandCover
    {
        public const string Name = "land cover";
        public const string FileName = "landcover.bin";
        const string Magic = "GLC1";

        public struct Tile
        {
            public Vector2Int index;
            public Rect area;
            public byte[] cells;
        }

        public static byte[] Write(int resolution, string[] classes, IList<Tile> tiles)
        {
            using var stream = new MemoryStream();
            stream.Write(Encoding.ASCII.GetBytes(Magic), 0, Magic.Length);
            using (var deflate = new DeflateStream(stream, System.IO.Compression.CompressionLevel.Optimal, true))
            using (var writer = new BinaryWriter(deflate, Encoding.UTF8, true))
            {
                writer.Write(resolution);
                writer.Write(classes.Length);
                foreach (string name in classes) writer.Write(name ?? "");
                writer.Write(tiles.Count);
                foreach (var tile in tiles)
                {
                    writer.Write(tile.index.x); writer.Write(tile.index.y);
                    writer.Write(tile.area.x); writer.Write(tile.area.y); writer.Write(tile.area.width); writer.Write(tile.area.height);
                    writer.Write(tile.cells);
                }
            }
            return stream.ToArray();
        }

        // The tiles' land cover as data the runtime asks (made in memory, not assets).
        public static List<GeoLandCoverData> Read(byte[] bytes, out int resolution)
        {
            if (bytes == null || bytes.Length < Magic.Length || Encoding.ASCII.GetString(bytes, 0, Magic.Length) != Magic)
                throw new InvalidDataException("Not a Geo World package land cover (" + FileName + ").");
            var result = new List<GeoLandCoverData>();
            using var stream = new MemoryStream(bytes, Magic.Length, bytes.Length - Magic.Length);
            using var deflate = new DeflateStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(deflate, Encoding.UTF8);
            resolution = reader.ReadInt32();
            var classes = new string[reader.ReadInt32()];
            for (int i = 0; i < classes.Length; i++) classes[i] = reader.ReadString();
            int count = reader.ReadInt32();
            for (int t = 0; t < count; t++)
            {
                var index = new Vector2Int(reader.ReadInt32(), reader.ReadInt32());
                var area = new Rect(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                byte[] cells = reader.ReadBytes(resolution * resolution);
                var data = ScriptableObject.CreateInstance<GeoLandCoverData>();
                data.name = "Land cover " + index.x + "_" + index.y;
                data.hideFlags = HideFlags.DontSave;
                data.Set(index, area, resolution, cells, classes);
                result.Add(data);
            }
            return result;
        }

        // The package's land cover on host (a GeoLandCover with the tiles' data); null when the package has none.
        public static GeoLandCover Attach(GeoWorldPackage package, GameObject host)
        {
            byte[] bytes = package.ReadExtra(Name);
            if (bytes == null) return null;
            var tiles = Read(bytes, out int resolution);
            var cover = host.GetComponent<GeoLandCover>();
            if (!cover) cover = host.AddComponent<GeoLandCover>();
            cover.origin = Vector2.zero;
            cover.tileSize = package.Manifest.tileSize;
            cover.cellSize = resolution > 0 ? package.Manifest.tileSize / resolution : 10;
            cover.tiles = tiles;
            cover.report = $"{tiles.Count} tiles of {resolution} x {resolution} cells from the package";
            return cover;
        }

        // What Attach made, taken off host again.
        public static void Detach(GameObject host)
        {
            var cover = host ? host.GetComponent<GeoLandCover>() : null;
            if (!cover) return;
            foreach (var data in cover.tiles) if (data) UnityEngine.Object.Destroy(data);
            UnityEngine.Object.Destroy(cover);
        }
    }
}
