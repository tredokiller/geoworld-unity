using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Satellite imagery for the terrain: XYZ (Web Mercator) tiles from a tile server, downloaded once into
    // Library/GeoWorld/SatelliteTiles and resampled into one texture that covers the world's bounds.
    public static class GeoSatellite
    {
        public const string EsriWorldImagery = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}?blankTile=false";
        // a whole town in one texture (7.5 km at zoom 17 is about 1 600 tiles); the mosaic holds 64 K pixels a tile while it is built
        public const int MaxTiles = 2048;
        const double Circumference = 40075016.68557849;
        static readonly HttpClient client = CreateClient();

        static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GeoWorld-Unity/1.0");
            return http;
        }

        public static string Attribution(string template)
        {
            if (string.IsNullOrEmpty(template)) return "";
            if (template.Contains("arcgisonline.com") || template.Contains("arcgis.com")) return "Imagery © Esri, Maxar, Earthstar Geographics";
            try { return "Imagery: " + new Uri(template.Replace("{", "").Replace("}", "")).Host; } catch (UriFormatException) { return "Imagery: " + template; }
        }

        // Global Web Mercator pixel of a point at a zoom (tiles of tileSize pixels, y down from the north).
        public static void Pixel(double lat, double lon, int zoom, int tileSize, out double px, out double py)
        {
            double world = tileSize * Math.Pow(2, zoom);
            double s = Math.Sin(Math.Max(-85.05112878, Math.Min(85.05112878, lat)) * Math.PI / 180);
            px = (lon + 180) / 360 * world;
            py = (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * world;
        }

        public static double MetresPerPixel(double lat, int zoom, int tileSize = 256) =>
            Circumference * Math.Cos(lat * Math.PI / 180) / (tileSize * Math.Pow(2, zoom));

        // Tile range covering the bounds at a zoom.
        public static RectInt Tiles(GeoEnu enu, GeoBounds b, int zoom)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var corner in new[] { new Vector2(b.minX, b.minZ), new Vector2(b.maxX, b.minZ), new Vector2(b.minX, b.maxZ), new Vector2(b.maxX, b.maxZ) })
            {
                enu.Inverse(corner.x, corner.y, out double lat, out double lon);
                Pixel(lat, lon, zoom, 1, out double tx, out double ty);
                minX = Math.Min(minX, tx); minY = Math.Min(minY, ty); maxX = Math.Max(maxX, tx); maxY = Math.Max(maxY, ty);
            }
            int x0 = (int)Math.Floor(minX), y0 = (int)Math.Floor(minY);
            return new RectInt(x0, y0, (int)Math.Floor(maxX) - x0 + 1, (int)Math.Floor(maxY) - y0 + 1);
        }

        // Texels along a side when the settings say Auto (0): the power of two nearest to 0.4 m a texel, the detail of the
        // imagery (zoom 18 is 0.4 m a pixel across Ukraine), from 1024 to 8192: up to 600 m 1024, a 1 km tile 2048,
        // 1.3 to 2.4 km 4096, anything larger 8192.
        public const double AutoTexelMetres = 0.4;
        public static int AutoResolution(GeoBounds b) =>
            Mathf.Clamp(Mathf.ClosestPowerOfTwo((int)Math.Ceiling(Math.Max(b.Width, b.Depth) / AutoTexelMetres)), 1024, 8192);

        // The coarsest zoom that is at least as sharp as the texture (metres per texel), no deeper than maxZoom,
        // and coarser while the bounds would need more than maxTiles tiles.
        public static int Zoom(GeoEnu enu, GeoBounds b, double lat, int resolution, int maxZoom, int maxTiles = MaxTiles)
        {
            double texel = Math.Max(b.Width, b.Depth) / resolution;
            int zoom = Mathf.Clamp((int)Math.Ceiling(Math.Log(MetresPerPixel(lat, 0) / texel, 2)), 1, Mathf.Clamp(maxZoom, 1, 22));
            while (zoom > 1) { var r = Tiles(enu, b, zoom); if ((long)r.width * r.height <= maxTiles) break; zoom--; }
            return zoom;
        }

        // A square texture (resolution x resolution, mipmapped, DXT1 or crunched DXT1: Compress) whose UV (0,0)-(1,1) spans the bounds
        // (row 0 = south edge). Throws when no tile could be read.
        public static Texture2D Build(GeoOrigin origin, GeoBounds b, GenerationSettings settings, List<string> notes, Action<string, float> progress)
        {
            if (origin == null) throw new InvalidDataException("the export has no geographic origin");
            string template = string.IsNullOrWhiteSpace(settings.satelliteTiles) ? EsriWorldImagery : settings.satelliteTiles.Trim();
            if (!template.Contains("{z}") || !template.Contains("{x}") || !template.Contains("{y}"))
                throw new ArgumentException("the tile URL needs {z}, {x} and {y}");
            int resolution = settings.satelliteResolution > 0 ? settings.satelliteResolution : AutoResolution(b);
            var enu = new GeoEnu(origin.lat, origin.lon);
            int zoom = Zoom(enu, b, origin.lat, resolution, settings.satelliteMaxZoom), wanted = zoom;
            RectInt range; byte[][] images; int failed;
            // Servers answer with 404 or with one "no imagery here" picture where a zoom is not covered:
            // step out until nearly every tile is real imagery.
            while (true)
            {
                range = Tiles(enu, b, zoom);
                images = Download(template, zoom, range, progress, out failed);
                failed += DropPlaceholders(images);
                if (failed * 20 <= images.Length || zoom <= Math.Max(1, wanted - 6)) break;
                zoom--;
            }
            if (zoom < wanted) notes?.Add($"Satellite imagery: zoom {wanted} is not covered here, zoom {zoom} is used.");

            progress?.Invoke("Decoding satellite tiles", 0.8f);
            int tileSize = 0;
            var decoded = new Color32[range.width * range.height][];
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i] == null) continue;
                    if (!texture.LoadImage(images[i]) || texture.width != texture.height || (tileSize != 0 && texture.width != tileSize)) { failed++; continue; }
                    tileSize = texture.width; decoded[i] = texture.GetPixels32();
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            if (tileSize == 0) throw new IOException("no satellite tile could be downloaded or decoded from " + new Uri(template.Replace("{", "").Replace("}", "")).Host);

            // One mosaic, rows from the north like the tile grid (Unity images are stored bottom row first).
            int mw = range.width * tileSize, mh = range.height * tileSize;
            var mosaic = new Color32[(long)mw * mh];
            var missing = new Color32(110, 110, 100, 255);
            for (int ty = 0; ty < range.height; ty++) for (int tx = 0; tx < range.width; tx++)
            {
                var tile = decoded[ty * range.width + tx];
                for (int r = 0; r < tileSize; r++)
                {
                    long row = (long)(ty * tileSize + r) * mw + tx * tileSize;
                    if (tile == null) { for (int c = 0; c < tileSize; c++) mosaic[row + c] = missing; continue; }
                    Array.Copy(tile, (tileSize - 1 - r) * tileSize, mosaic, row, tileSize);
                }
            }
            decoded = null;

            progress?.Invoke("Draping satellite imagery over the terrain", 0.88f);
            // Exact projection on a coarse grid; the mapping is smooth, so texels in between are interpolated.
            const int step = 32;
            int cells = resolution / step + 1;
            var gx = new double[cells, cells]; var gy = new double[cells, cells];
            double ox = range.x * (double)tileSize, oy = range.y * (double)tileSize;
            for (int j = 0; j < cells; j++) for (int i = 0; i < cells; i++)
            {
                double x = b.minX + (double)i * step / resolution * b.Width, z = b.minZ + (double)j * step / resolution * b.Depth;
                enu.Inverse(x, z, out double lat, out double lon);
                Pixel(lat, lon, zoom, tileSize, out double px, out double py);
                gx[j, i] = px - ox; gy[j, i] = py - oy;
            }
            var pixels = new Color32[resolution * resolution];
            Parallel.For(0, resolution, v =>
            {
                double fv = (v + 0.5) / step; int j = Math.Min((int)fv, cells - 2); double tv = fv - j;
                for (int u = 0; u < resolution; u++)
                {
                    double fu = (u + 0.5) / step; int i = Math.Min((int)fu, cells - 2); double tu = fu - i;
                    double px = Lerp(Lerp(gx[j, i], gx[j, i + 1], tu), Lerp(gx[j + 1, i], gx[j + 1, i + 1], tu), tv) - 0.5;
                    double py = Lerp(Lerp(gy[j, i], gy[j, i + 1], tu), Lerp(gy[j + 1, i], gy[j + 1, i + 1], tu), tv) - 0.5;
                    pixels[v * resolution + u] = Sample(mosaic, mw, mh, px, py);
                }
            });
            mosaic = null;

            var result = new Texture2D(resolution, resolution, TextureFormat.RGBA32, true, false)
                { name = "Satellite imagery", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            result.SetPixels32(pixels); result.Apply(true);
            Compress(result, pixels, settings, notes);
            double metres = MetresPerPixel(origin.lat, zoom, tileSize);
            notes?.Add($"Terrain draped with satellite imagery: {range.width * range.height} tiles at zoom {zoom} ({metres:F2} m per pixel) into {resolution} x {resolution} " +
                $"({Math.Max(b.Width, b.Depth) / resolution:F2} m per texel). {Attribution(template)}." +
                (failed > 0 ? $" {failed} tiles were missing and are grey." : "") + " Tiles are cached in Library/GeoWorld/SatelliteTiles.");
            return result;
        }

        // The texture (mipmapped RGBA32 made from pixels) in the form the settings ask for: DXT1 at its best encoding (the same size
        // as at the normal one, with fewer wrong colours in each block), or Crunched: DXT1 with Crunch compression on top, which
        // takes a quarter of the bytes or less in the world's assets and in a build and becomes the same DXT1 when Unity loads it
        // (on the loading thread). The satellite textures are most of a tiled world's size: a 1 km tile's 2048 texture is
        // 2.7 MB as DXT1. Where this editor cannot crunch, the texture is DXT1 and the report says so.
        // (The editor's compressor: GeoEditorGeneration; a player's: GeoMemoryTarget.)
        public static void Compress(Texture2D texture, Color32[] pixels, GenerationSettings settings, List<string> notes) =>
            GeoGeneration.Target.CompressSatellite(texture, pixels, settings, notes);

        // Tiles whose bytes repeat across the area are the server's placeholder ("map data not yet available"),
        // not imagery: they are dropped (left grey). Returns how many.
        public static int DropPlaceholders(byte[][] images)
        {
            var groups = new Dictionary<string, List<int>>();
            using (var md5 = MD5.Create())
                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i] == null) continue;
                    string key = Convert.ToBase64String(md5.ComputeHash(images[i]));
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<int>();
                    list.Add(i);
                }
            int dropped = 0;
            foreach (var list in groups.Values)
                if (list.Count >= 3 && list.Count * 20 >= images.Length)
                    foreach (int i in list) { images[i] = null; dropped++; }
            return dropped;
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        static Color32 Sample(Color32[] image, int w, int h, double x, double y)
        {
            x = Math.Max(0, Math.Min(w - 1.001, x)); y = Math.Max(0, Math.Min(h - 1.001, y));
            int ix = (int)x, iy = (int)y; float fx = (float)(x - ix), fy = (float)(y - iy);
            long i = (long)iy * w + ix;
            Color32 a = image[i], c = image[i + 1], d = image[i + w], e = image[i + w + 1];
            float w0 = (1 - fx) * (1 - fy), w1 = fx * (1 - fy), w2 = (1 - fx) * fy, w3 = fx * fy;
            return new Color32((byte)(a.r * w0 + c.r * w1 + d.r * w2 + e.r * w3 + 0.5f), (byte)(a.g * w0 + c.g * w1 + d.g * w2 + e.g * w3 + 0.5f),
                (byte)(a.b * w0 + c.b * w1 + d.b * w2 + e.b * w3 + 0.5f), 255);
        }

        // Where downloaded tiles are kept: the project's Library in the editor; a player sets a folder of its own (GeoWorldGenerator).
        public static string CacheRoot = Path.Combine("Library", "GeoWorld", "SatelliteTiles");

        public static string CacheFolder(string template)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(template));
                return Path.Combine(CacheRoot, BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant());
            }
        }

        // Tile bytes in row order of the range (null where a tile could not be had), from the cache or the server.
        static byte[][] Download(string template, int zoom, RectInt range, Action<string, float> progress, out int failed)
        {
            string cache = CacheFolder(template);
            int count = range.width * range.height, done = 0, errors = 0, max = 1 << zoom;
            var result = new byte[count][];
            string lastError = null;
            using (var cancel = new CancellationTokenSource())
            using (var gate = new SemaphoreSlim(6))
            {
                var tasks = new List<Task>();
                for (int k = 0; k < count; k++)
                {
                    int index = k, x = range.x + k % range.width, y = range.y + k / range.width;
                    string file = Path.Combine(cache, zoom.ToString(), x.ToString(), y + ".img");
                    if (File.Exists(file)) { result[index] = File.ReadAllBytes(file); Interlocked.Increment(ref done); continue; }
                    if (File.Exists(Path.ChangeExtension(file, ".none"))) { Interlocked.Increment(ref errors); Interlocked.Increment(ref done); continue; }
                    if (y < 0 || y >= max) { Interlocked.Increment(ref errors); Interlocked.Increment(ref done); continue; }
                    int wx = ((x % max) + max) % max;
                    string url = template.Replace("{z}", zoom.ToString()).Replace("{x}", wx.ToString()).Replace("{y}", y.ToString());
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancel.Token);
                        try
                        {
                            for (int attempt = 0; attempt < 3 && !cancel.IsCancellationRequested; attempt++)
                            {
                                try
                                {
                                    using (var response = await client.GetAsync(url, cancel.Token))
                                    {
                                        if (!response.IsSuccessStatusCode) { lastError = (int)response.StatusCode + " " + response.ReasonPhrase; if ((int)response.StatusCode == 404) { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllBytes(Path.ChangeExtension(file, ".none"), new byte[0]); break; } continue; }
                                        var bytes = await response.Content.ReadAsByteArrayAsync();
                                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                                        File.WriteAllBytes(file, bytes);
                                        result[index] = bytes; return;
                                    }
                                }
                                catch (HttpRequestException e) { lastError = e.Message; }
                                catch (TaskCanceledException) when (!cancel.IsCancellationRequested) { lastError = "timed out"; }
                            }
                            Interlocked.Increment(ref errors);
                        }
                        finally { gate.Release(); Interlocked.Increment(ref done); }
                    }, cancel.Token));
                }
                var all = Task.WhenAll(tasks);
                try
                {
                    while (!all.IsCompleted)
                    {
                        progress?.Invoke($"Downloading satellite tiles ({Volatile.Read(ref done)} / {count}, zoom {zoom})", 0.8f * Volatile.Read(ref done) / count);
                        ((IAsyncResult)all).AsyncWaitHandle.WaitOne(100);
                    }
                }
                catch (OperationCanceledException) { cancel.Cancel(); ((IAsyncResult)all).AsyncWaitHandle.WaitOne(5000); throw; }
                if (all.IsFaulted && all.Exception != null) throw all.Exception.GetBaseException();
            }
            failed = errors;
            if (errors > 0 && errors == count && lastError != null) Debug.LogWarning($"Geo World: no satellite tiles at zoom {zoom} ({lastError}).");
            return result;
        }
    }
}
