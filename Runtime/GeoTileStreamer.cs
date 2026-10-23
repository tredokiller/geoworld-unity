using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace GeoWorld
{
    [DisallowMultipleComponent]
    public class GeoTileStreamer : MonoBehaviour
    {
        public GeoTiledWorld world;
        [Tooltip("What the tiles follow; empty: the main camera.")]
        public Transform target;
        [Min(100)] public float loadRadius = 2000;
        [Min(100)] public float unloadRadius = 2600;
        [Range(1, 8)] public int loadsAtOnce = 2;
        [Range(0, 30)] public int framesBetweenTiles = 3;

        sealed class Loaded
        {
            public Scene scene;
            public AsyncOperationHandle<SceneInstance> handle;
            public bool addressable;
        }

        readonly Dictionary<string, Loaded> loaded = new Dictionary<string, Loaded>();
        readonly HashSet<string> loading = new HashSet<string>();
        readonly HashSet<string> unloading = new HashSet<string>();
        readonly Dictionary<string, float> retryAfter = new Dictionary<string, float>();
        CancellationTokenSource lifetime;
        int lastSwitchedOn = -1000;
        float next;
        public bool Busy { get; private set; } = true;
        public int LoadedTiles => loaded.Count;
        // Is the tile's scene loaded and switched on (scenePath of GeoTiledWorld.Tile)?
        public bool IsLoaded(string scenePath) => !string.IsNullOrEmpty(scenePath) && loaded.ContainsKey(scenePath);

        void OnEnable() { lifetime = new CancellationTokenSource(); next = 0; }

        void Update()
        {
            if (!world || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.2f;
            var follow = target ? target : Camera.main ? Camera.main.transform : null;
            if (!follow) return;
            var wanted = new List<GeoTiledWorld.Tile>();
            foreach (var tile in world.tiles)
            {
                string key = tile.scenePath;
                if (string.IsNullOrEmpty(key)) continue;
                float distance = Distance(tile.area, follow.position);
                // Adopt tiles opened in the editor, but never load them twice.
                if (!loaded.ContainsKey(key) && !loading.Contains(key) && !unloading.Contains(key))
                {
                    var scene = SceneManager.GetSceneByPath(key);
                    if (scene.IsValid() && scene.isLoaded) loaded[key] = new Loaded { scene = scene };
                }
                if (loaded.TryGetValue(key, out var existing))
                {
                    if (distance > Mathf.Max(loadRadius, unloadRadius) && !unloading.Contains(key))
                    {
                        loaded.Remove(key);
                        UnloadAsync(key, existing).Forget();
                    }
                }
                else if (distance <= loadRadius && !loading.Contains(key) && !unloading.Contains(key))
                    wanted.Add(tile);
            }
            wanted.Sort((a, b) => Distance(a.area, follow.position).CompareTo(Distance(b.area, follow.position)));
            foreach (var tile in wanted)
            {
                if (loading.Count >= Mathf.Max(1, loadsAtOnce)) break;
                if (retryAfter.TryGetValue(tile.scenePath, out float retry) && Time.unscaledTime < retry) continue;
                LoadAsync(tile, world.addressableScenes, lifetime.Token).Forget();
            }
            Busy = loading.Count > 0 || unloading.Count > 0 || wanted.Count > 0;
        }

        async UniTask ActivationSlot(CancellationToken token)
        {
            while (!token.IsCancellationRequested && Time.frameCount - lastSwitchedOn < Mathf.Max(1, framesBetweenTiles))
                await UniTask.Yield();
            lastSwitchedOn = Time.frameCount;
        }

        async UniTask LoadAsync(GeoTiledWorld.Tile tile, bool addressable, CancellationToken token)
        {
            string key = tile.scenePath;
            loading.Add(key);
            var item = new Loaded { addressable = addressable };
            try
            {
                // Native scene operations cannot be cancelled. Always finish activation before cleanup,
                // otherwise a scene waiting at 90% blocks Unity's entire async operation queue.
                if (addressable)
                {
                    if (string.IsNullOrEmpty(tile.sceneAddress))
                        throw new InvalidOperationException("Rebuild this world's tiles to register Addressables: " + key);
                    item.handle = Addressables.LoadSceneAsync(tile.sceneAddress, LoadSceneMode.Additive, false);
                    while (!item.handle.IsDone) await UniTask.Yield();
                    if (item.handle.Status != AsyncOperationStatus.Succeeded)
                        throw item.handle.OperationException ?? new InvalidOperationException("Cannot load " + tile.sceneAddress);
                    item.scene = item.handle.Result.Scene;
                    await ActivationSlot(token);
                    await item.handle.Result.ActivateAsync();
                }
                else
                {
#if UNITY_EDITOR
                    var operation = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(key, new LoadSceneParameters(LoadSceneMode.Additive));
#else
                    var operation = SceneManager.LoadSceneAsync(key, LoadSceneMode.Additive);
#endif
                    if (operation == null) throw new InvalidOperationException("Cannot load tile scene " + key);
                    operation.allowSceneActivation = false;
                    while (operation.progress < 0.9f && !operation.isDone) await UniTask.Yield();
                    await ActivationSlot(token);
                    operation.allowSceneActivation = true;
                    await operation;
                    item.scene = SceneManager.GetSceneByPath(key);
                }
                if (token.IsCancellationRequested) await UnloadAsync(key, item);
                else
                {
                    loaded[key] = item;
                    retryAfter.Remove(key);
                    Terrain.SetConnectivityDirty();
                }
            }
            catch (Exception exception)
            {
                if (item.addressable && item.handle.IsValid())
                {
                    if (item.handle.Status == AsyncOperationStatus.Succeeded)
                        await UnloadAsync(key, item);
                    else Addressables.Release(item.handle);
                }
                retryAfter[key] = Time.unscaledTime + 5;
                Debug.LogException(exception, this);
            }
            finally { loading.Remove(key); }
        }

        async UniTask UnloadAsync(string key, Loaded item)
        {
            unloading.Add(key);
            try
            {
                if (item.addressable && item.handle.IsValid())
                {
                    var operation = Addressables.UnloadSceneAsync(item.handle, autoReleaseHandle: false);
                    try
                    {
                        while (!operation.IsDone) await UniTask.Yield();
                        if (operation.Status != AsyncOperationStatus.Succeeded)
                            throw operation.OperationException ?? new InvalidOperationException("Cannot unload " + key);
                    }
                    finally { if (operation.IsValid()) Addressables.Release(operation); }
                }
                else if (item.scene.IsValid() && item.scene.isLoaded)
                {
                    var operation = SceneManager.UnloadSceneAsync(item.scene);
                    if (operation != null) await operation;
                }
                Terrain.SetConnectivityDirty();
            }
            catch (Exception exception) { Debug.LogException(exception, this); }
            finally { unloading.Remove(key); }
        }

        void OnDisable()
        {
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            foreach (var pair in loaded) UnloadAsync(pair.Key, pair.Value).Forget();
            loaded.Clear();
            Busy = loading.Count > 0 || unloading.Count > 0;
        }

        public static float Distance(Rect area, Vector3 point)
        {
            float dx = Mathf.Max(area.xMin - point.x, 0, point.x - area.xMax);
            float dz = Mathf.Max(area.yMin - point.z, 0, point.z - area.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
