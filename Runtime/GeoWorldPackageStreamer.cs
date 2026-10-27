using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace GeoWorld
{
    // Loads the tiles of a world package around a target while playing (the nearest first, a few at a time, each spread over
    // frames) and unloads those that are left behind: the package counterpart of GeoTileStreamer.
    public sealed class GeoWorldPackageStreamer : MonoBehaviour
    {
        [Tooltip("What the tiles follow; empty: the main camera.")]
        public Transform target;
        [Min(100)] public float loadRadius = 2000;
        [Tooltip("At least the load radius and GeoTileStreamer.UnloadMargin more.")]
        [Min(100)] public float unloadRadius = 2600;
        [Range(1, 8)] public int loadsAtOnce = 1;
        public GeoTileLoadOptions options = new GeoTileLoadOptions();

        public GeoWorldPackage Package { get; private set; }
        readonly Dictionary<Vector2Int, GeoWorldTileHandle> loaded = new Dictionary<Vector2Int, GeoWorldTileHandle>();
        readonly HashSet<Vector2Int> loading = new HashSet<Vector2Int>();
        readonly HashSet<Vector2Int> failed = new HashSet<Vector2Int>();
        CancellationTokenSource lifetime;
        Transform root;

        public IReadOnlyDictionary<Vector2Int, GeoWorldTileHandle> Loaded => loaded;
        public bool Busy => loading.Count > 0;
        // seconds each tile took to load, in the order they came
        public readonly List<(Vector2Int tile, double seconds)> loadTimes = new List<(Vector2Int, double)>();

        // Shows a package (the one before is unloaded): its tiles stream around the target from now on.
        public void Open(GeoWorldPackage package)
        {
            Close();
            Package = package;
            lifetime = new CancellationTokenSource();
            root = new GameObject("Geo World package - " + package.Manifest.name).transform;
            root.SetParent(transform, false);
        }

        public void Close()
        {
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            foreach (var h in loaded.Values) h.Unload();
            loaded.Clear(); loading.Clear(); failed.Clear();
            if (root) Destroy(root.gameObject);
            Package = null;
        }

        void OnDestroy() => Close();

        Vector3 Where => target ? target.position : Camera.main ? Camera.main.transform.position : Vector3.zero;

        void Update()
        {
            if (Package == null) return;
            var at = Where;
            float unload = Mathf.Max(unloadRadius, loadRadius + 100);
            var gone = new List<Vector2Int>();
            foreach (var pair in loaded)
            {
                var t = Package.Tile(pair.Key);
                if (Distance(t, at) > unload) gone.Add(pair.Key);
            }
            foreach (var index in gone) { loaded[index].Unload(); loaded.Remove(index); }
            if (loading.Count >= loadsAtOnce) return;
            foreach (var t in Package.TilesAround(at, loadRadius))
            {
                var index = t.Index;
                if (loaded.ContainsKey(index) || loading.Contains(index) || failed.Contains(index)) continue;
                Load(index).Forget();
                if (loading.Count >= loadsAtOnce) break;
            }
        }

        async UniTaskVoid Load(Vector2Int index)
        {
            loading.Add(index);
            var package = Package; var token = lifetime.Token;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var handle = await package.LoadTileAsync(index, root, options, token);
                if (token.IsCancellationRequested || package != Package) { handle.Unload(); return; }
                loaded[index] = handle;
                loadTimes.Add((index, clock.Elapsed.TotalSeconds));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { failed.Add(index); Debug.LogException(e); }
            finally { loading.Remove(index); }
        }

        // Waits until every tile within radius of a point is loaded.
        public async UniTask LoadAroundAsync(Vector3 point, float radius, CancellationToken cancellation = default)
        {
            while (Package != null)
            {
                bool all = true;
                foreach (var t in Package.TilesAround(point, radius)) if (!loaded.ContainsKey(t.Index) && !failed.Contains(t.Index)) { all = false; break; }
                if (all) return;
                await UniTask.Yield(cancellation);
            }
        }

        static float Distance(GeoWorldManifest.Tile t, Vector3 p)
        {
            if (t == null) return float.MaxValue;
            float dx = Mathf.Max(t.minX - p.x, 0, p.x - t.maxX), dz = Mathf.Max(t.minZ - p.z, 0, p.z - t.maxZ);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
