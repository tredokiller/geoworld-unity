using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GeoWorld
{
    // XYZ (Web Mercator) map tiles kept on disk, one file a tile: <root>/<hash of the tile URL>/<z>/<x>/<y>.img (".none" for a tile the
    // server does not have). The satellite imagery of a generation (GeoSatellite), the background around packages (GeoWorldBackground)
    // and the elevation under it share it, so a tile is downloaded once whoever asks for it: a zone generated next to another takes
    // the tiles they share from here.
    public static class GeoTileCache
    {
        static string root;

        // The editor keeps tiles in the project's Library, a player in its own folder (GeoWorldGenerator.Home/cache/satellite).
        public static string Root
        {
            get => root ??= Application.isEditor ? Path.Combine("Library", "GeoWorld", "SatelliteTiles")
                : Path.Combine(Application.persistentDataPath, "GeoWorld", "cache", "satellite");
            set => root = value;
        }

        public static string Folder(string template)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(template));
                return Path.Combine(Root, BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant());
            }
        }

        public static string File(string template, int z, int x, int y) => Path.Combine(Folder(template), z.ToString(), x.ToString(), y + ".img");

        public static string Url(string template, int z, int x, int y)
        {
            int max = 1 << z;
            return template.Replace("{z}", z.ToString()).Replace("{x}", (((x % max) + max) % max).ToString()).Replace("{y}", y.ToString());
        }

        static readonly HttpClient client = CreateClient();

        static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GeoWorld-Unity/1.0");
            return http;
        }

        // Fetches a tile: its bytes, or null where the server has none (404). Throws on other failures. Replaceable (tests).
        public static Func<string, CancellationToken, Task<byte[]>> Fetch = async (url, cancellation) =>
        {
            using (var response = await client.GetAsync(url, cancellation).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 404) return null;
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        };

        public readonly struct Tile : IEquatable<Tile>
        {
            public readonly int z, x, y;
            public Tile(int z, int x, int y) { this.z = z; this.x = x; this.y = y; }
            public bool Equals(Tile other) => z == other.z && x == other.x && y == other.y;
            public override bool Equals(object obj) => obj is Tile t && Equals(t);
            public override int GetHashCode() => (z * 73856093) ^ (x * 19349663) ^ (y * 83492791);
            public override string ToString() => z + "/" + x + "/" + y;
        }

        // The tiles' bytes (null where there is none), from the disk or the server (six at a time, three tries each). progress: tiles
        // done of all; fetched counts the tiles that came from the server. Runs off the main thread.
        public static async Task<(byte[][] tiles, int fetched)> GetAsync(string template, IList<Tile> tiles, Action<int, int> progress, CancellationToken cancellation)
        {
            var result = new byte[tiles.Count][];
            int done = 0, downloaded = 0;
            using (var gate = new SemaphoreSlim(6))
            {
                var tasks = new List<Task>(tiles.Count);
                for (int k = 0; k < tiles.Count; k++)
                {
                    int index = k; var t = tiles[k];
                    string file = File(template, t.z, t.x, t.y), none = Path.ChangeExtension(file, ".none");
                    if (System.IO.File.Exists(file)) { result[index] = System.IO.File.ReadAllBytes(file); progress?.Invoke(Interlocked.Increment(ref done), tiles.Count); continue; }
                    if (System.IO.File.Exists(none) || t.y < 0 || t.y >= 1 << t.z) { progress?.Invoke(Interlocked.Increment(ref done), tiles.Count); continue; }
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancellation).ConfigureAwait(false);
                        try
                        {
                            for (int attempt = 0; attempt < 3; attempt++)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                try
                                {
                                    var bytes = await Fetch(Url(template, t.z, t.x, t.y), cancellation).ConfigureAwait(false);
                                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                                    if (bytes == null) { System.IO.File.WriteAllBytes(none, new byte[0]); break; }
                                    // written whole, then moved: another process reading the cache never sees half a tile
                                    string part = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".part";
                                    System.IO.File.WriteAllBytes(part, bytes);
                                    if (System.IO.File.Exists(file)) System.IO.File.Delete(part); else System.IO.File.Move(part, file);
                                    result[index] = bytes; Interlocked.Increment(ref downloaded);
                                    break;
                                }
                                catch (HttpRequestException) when (attempt < 2) { }
                                catch (TaskCanceledException) when (!cancellation.IsCancellationRequested && attempt < 2) { }
                            }
                        }
                        catch (Exception e) when (!(e is OperationCanceledException)) { Debug.LogWarning("Geo World: map tile " + t.z + "/" + t.x + "/" + t.y + " not downloaded: " + e.Message); }
                        finally { gate.Release(); progress?.Invoke(Interlocked.Increment(ref done), tiles.Count); }
                    }, cancellation));
                }
                // (not back on the caller's thread: a caller may wait for the result there)
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            return (result, downloaded);
        }
    }

}
