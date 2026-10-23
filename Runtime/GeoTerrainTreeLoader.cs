using System;
using System.Threading;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace GeoWorld
{
    // Owns the trees of one loaded tile. Does not retain a global Resources cache.
    [DefaultExecutionOrder(-11000), DisallowMultipleComponent, RequireComponent(typeof(Terrain))]
    public sealed class GeoTerrainTreeLoader : MonoBehaviour
    {
        public string resourceKey;
        [Min(1)] public int batchSize = 2048;
        [Min(1)] public int framesBetweenTerrains = 2;
        public bool Ready { get; private set; }
        Terrain terrain;
        TerrainData source, runtime;
        TerrainCollider terrainCollider;
        GeoInstancedMeshes drawer;
        CancellationTokenSource lifetime;
        // The trees are in the terrain. They stay while the tile is only switched off (a Nature Renderer that drew them keeps
        // references to the terrain data, and draws nothing if the data is destroyed under it) and go when the tile is destroyed.
        bool restored;
        sealed class Request
        {
            public ResourceRequest operation;
            public int users;
        }
        // Share only requests currently being applied, including a disable/re-enable during loading.
        static readonly Dictionary<string, Request> requests = new Dictionary<string, Request>();
        static int lastAppliedFrame = -1000;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() { requests.Clear(); lastAppliedFrame = -1000; }

        void OnEnable()
        {
            if (!Application.isPlaying) return;
            // switched on again: its trees are still in
            if (restored) { Ready = true; return; }
            lifetime = new CancellationTokenSource();
            RestoreAsync(lifetime.Token).Forget();
        }

        async UniTaskVoid RestoreAsync(CancellationToken token)
        {
            try
            {
                terrain = GetComponent<Terrain>();
                if (string.IsNullOrEmpty(resourceKey)) { restored = Ready = true; return; }
                string key = resourceKey;
                if (!requests.TryGetValue(key, out var request))
                    requests[key] = request = new Request { operation = Resources.LoadAsync<GeoTreeData>(key) };
                request.users++;
                try
                {
                    // Finish the request even when disabled, so its asset can be released.
                    await request.operation;
                    var data = request.operation.asset as GeoTreeData;
                    if (!data) throw new InvalidOperationException("Missing Geo World tree data: " + key);
                    token.ThrowIfCancellationRequested();
                    var trees = new TreeInstance[data.TreeCount];
                    for (int i = 0; i < trees.Length; i++)
                    {
                        trees[i] = data.GetTreeInstance(i);
                        if ((i + 1) % Mathf.Max(1, batchSize) == 0)
                            await UniTask.Yield(PlayerLoopTiming.Update, token);
                    }
                    while (Time.frameCount - lastAppliedFrame < Mathf.Max(1, framesBetweenTerrains))
                        await UniTask.Yield(PlayerLoopTiming.Update, token);
                    token.ThrowIfCancellationRequested();
                    lastAppliedFrame = Time.frameCount;
                    if (trees.Length > 0)
                    {
                        source = terrain.terrainData;
                        runtime = Instantiate(source);
                        runtime.SetTreeInstances(trees, false);
                        terrain.terrainData = runtime;
                        terrainCollider = GetComponent<TerrainCollider>();
                        if (terrainCollider && terrainCollider.terrainData == source) terrainCollider.terrainData = runtime;
                        terrain.Flush();
                    }
                    if (data.batches.Length > 0)
                    {
                        var go = new GameObject("Runtime trees");
                        go.transform.SetParent(transform, false);
                        drawer = go.AddComponent<GeoInstancedMeshes>();
                        drawer.batches.AddRange(data.batches);
                        drawer.count = data.instanceCount;
                    }
                    restored = Ready = true;
                    // a Nature Renderer that is already running keeps the terrain data it started with
                    GetComponent<GeoNatureRendererStart>()?.TreesChanged();
                }
                finally
                {
                    if (--request.users == 0)
                    {
                        requests.Remove(key);
                        if (request.operation.asset) Resources.UnloadAsset(request.operation.asset);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                // the tile goes on without the trees it could not have: Nature Renderer, which waits for them, starts all the same
                // (it would otherwise never start on this terrain), and the loader does not try again until the tile loads anew
                if (this) restored = Ready = true;
            }
        }

        void OnDisable()
        {
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            Ready = false;
        }

        void OnDestroy()
        {
            restored = false;
            if (drawer) Destroy(drawer.gameObject);
            if (runtime)
            {
                if (terrain) terrain.terrainData = source;
                if (terrainCollider && terrainCollider.terrainData == runtime) terrainCollider.terrainData = source;
                Destroy(runtime);
            }
            runtime = null;
        }
    }
}
