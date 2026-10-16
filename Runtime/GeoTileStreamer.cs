using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GeoWorld
{
    // Keeps the tiles of a tiled world loaded around a point (the camera) while playing: tiles within Load Radius are
    // loaded (nearest first, a few at a time, in the background) and tiles beyond Unload Radius are unloaded, so only
    // a few square kilometres are in memory and drawn however large the world is. A loaded tile is switched on (its
    // objects set up, which takes a frame) one at a time, Frames Between Tiles apart, so two tiles never land in the same
    // frame. A player build needs the tile scenes in the Build Settings (the tiled world's Inspector adds them); in the
    // editor they load without.
    [DisallowMultipleComponent]
    public class GeoTileStreamer : MonoBehaviour
    {
        public GeoTiledWorld world;
        [Tooltip("What the tiles follow; empty: the main camera.")]
        public Transform target;
        [Tooltip("Tiles nearer than this (metres from the tile's edge) are loaded.")]
        [Min(100)] public float loadRadius = 2000;
        [Tooltip("Tiles further than this are unloaded (more than Load Radius, so a tile at the edge does not load and unload over and over).")]
        [Min(100)] public float unloadRadius = 2600;
        [Range(1, 8)] public int loadsAtOnce = 2;
        [Tooltip("Frames between switching on two loaded tiles: a tile's objects are set up in the frame it is switched on, so two in one frame would cost twice.")]
        [Range(0, 30)] public int framesBetweenTiles = 3;

        readonly Dictionary<string, AsyncOperation> loading = new Dictionary<string, AsyncOperation>();
        readonly Dictionary<string, AsyncOperation> unloading = new Dictionary<string, AsyncOperation>();
        readonly Dictionary<string, Rect> areas = new Dictionary<string, Rect>();
        float next;
        int lastSwitchedOn = -1000;
        Vector3 at;

        // Tiles still loading or unloading, or wanted and not loaded yet.
        public bool Busy { get; private set; } = true;
        public int LoadedTiles { get; private set; }

        void Update()
        {
            if (!world) return;
            SwitchOn();
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.2f;
            var follow = target ? target : Camera.main ? Camera.main.transform : null;
            if (!follow) return;
            at = follow.position;
            Finish(loading, true); Finish(unloading, false);
            var wanted = new List<GeoTiledWorld.Tile>();
            int loaded = 0;
            foreach (var tile in world.tiles)
            {
                if (string.IsNullOrEmpty(tile.scenePath)) continue;
                float distance = Distance(tile.area, at);
                var scene = SceneManager.GetSceneByPath(tile.scenePath);
                bool isLoaded = scene.IsValid() && scene.isLoaded;
                if (isLoaded) loaded++;
                if (isLoaded && distance > unloadRadius && !unloading.ContainsKey(tile.scenePath))
                    unloading[tile.scenePath] = SceneManager.UnloadSceneAsync(scene);
                else if (!isLoaded && distance <= loadRadius && !loading.ContainsKey(tile.scenePath)) wanted.Add(tile);
            }
            wanted.Sort((a, b) => Distance(a.area, at).CompareTo(Distance(b.area, at)));
            foreach (var tile in wanted)
            {
                if (loading.Count >= loadsAtOnce) break;
                var operation = Load(tile.scenePath);
                if (operation == null) continue;
                operation.allowSceneActivation = false; // SwitchOn lets it in
                loading[tile.scenePath] = operation;
                areas[tile.scenePath] = tile.area;
            }
            LoadedTiles = loaded;
            Busy = loading.Count > 0 || unloading.Count > 0 || wanted.Count > 0;
        }

        // The nearest tile that is read and waiting is switched on, at most one every Frames Between Tiles frames.
        void SwitchOn()
        {
            if (Time.frameCount - lastSwitchedOn < framesBetweenTiles) return;
            string nearest = null; float distance = float.MaxValue;
            foreach (var pair in loading)
            {
                var operation = pair.Value;
                if (operation == null || operation.allowSceneActivation || operation.progress < 0.9f) continue;
                float d = areas.TryGetValue(pair.Key, out var area) ? Distance(area, at) : 0;
                if (d < distance) { distance = d; nearest = pair.Key; }
            }
            if (nearest == null) return;
            loading[nearest].allowSceneActivation = true;
            lastSwitchedOn = Time.frameCount;
        }

        static void Finish(Dictionary<string, AsyncOperation> operations, bool loaded)
        {
            var done = new List<string>();
            foreach (var pair in operations) if (pair.Value == null || pair.Value.isDone) done.Add(pair.Key);
            foreach (var key in done) operations.Remove(key);
            // neighbouring terrains stitch their edges once they are both there
            if (done.Count > 0 && loaded) Terrain.SetConnectivityDirty();
        }

        static AsyncOperation Load(string path)
        {
#if UNITY_EDITOR
            return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
#else
            return SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
#endif
        }

        public static float Distance(Rect area, Vector3 point)
        {
            float dx = Mathf.Max(area.xMin - point.x, 0, point.x - area.xMax), dz = Mathf.Max(area.yMin - point.z, 0, point.z - area.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
