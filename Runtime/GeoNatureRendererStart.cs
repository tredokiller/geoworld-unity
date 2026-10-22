using System;
using UnityEngine;

namespace GeoWorld
{
    // Starts the Nature Renderer of a tile's terrain a little after the tile loads, one terrain at a time. Nature Renderer
    // sets itself up on its first LateUpdate (30-50 ms a terrain), which landed in the frame the tile was switched on,
    // on top of setting up the tile's objects. In Play mode this switches it off before that and back on once the tile's
    // trees are in (GeoTerrainTreeLoader), two frames later at the earliest and at least Frames Apart frames after the last
    // one; outside Play mode it does nothing. It does this every time the tile is switched on, not only when it loads: a tile
    // switched off and on again gets its trees back through the loader, and a Nature Renderer that started on the terrain
    // before they were in would keep the empty terrain data it saw and draw no trees at all. Geo World adds it to a terrain
    // whose trees Nature Renderer draws (and finds Nature Renderer by name: it does not depend on it).
    [DefaultExecutionOrder(-10000), DisallowMultipleComponent]
    public class GeoNatureRendererStart : MonoBehaviour
    {
        public static int FramesApart = 8;
        static int lastStart = -1000;

        Behaviour natureRenderer;
        // Nature Renderer was on in the scene: only then is its start Geo World's to delay
        bool managed, waiting;
        int loadedFrame;
        GeoTerrainTreeLoader treeLoader;

        // Frame numbers start again with every Play mode: without this, a Play mode entered without a domain reload would wait
        // for the frame count to catch up with the last one's.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() { lastStart = -1000; }

        void Awake()
        {
            if (!Application.isPlaying) return;
            foreach (var behaviour in GetComponents<Behaviour>())
                if (behaviour && behaviour.GetType().Name == "NatureRenderer") natureRenderer = behaviour;
            managed = natureRenderer && natureRenderer.enabled;
            treeLoader = GetComponent<GeoTerrainTreeLoader>();
            if (managed) natureRenderer.enabled = false; // before its own Awake
        }

        // Every time the tile is switched on: Nature Renderer waits again.
        void OnEnable()
        {
            if (!Application.isPlaying || !managed || !natureRenderer) return;
            natureRenderer.enabled = false;
            loadedFrame = Time.frameCount;
            waiting = true;
        }

        void OnDisable() { waiting = false; }

        void Update()
        {
            if (!waiting) return;
            if (!natureRenderer) { waiting = false; return; }
            if (treeLoader && !treeLoader.Ready) return;
            int frame = Time.frameCount;
            if (frame - loadedFrame < 2 || frame - lastStart < FramesApart) return;
            lastStart = frame;
            waiting = false;
            // Nature Renderer keeps the terrain data it started with: it starts on the data the terrain has now
            Bind();
            natureRenderer.enabled = true;
        }

        // The tile's trees came in (GeoTerrainTreeLoader). Where Nature Renderer is already running (something else switched it
        // on before they were), it starts again on the terrain's new data, which it would not look at by itself.
        public void TreesChanged()
        {
            if (!managed || !natureRenderer || !natureRenderer.enabled) return;
            var type = natureRenderer.GetType();
            if (!(type.GetProperty("IsInitialized")?.GetValue(natureRenderer) is true)) return;
            Bind();
            type.GetMethod("Restart", Type.EmptyTypes)?.Invoke(natureRenderer, null);
        }

        void Bind()
        {
            var terrain = GetComponent<Terrain>();
            if (!terrain) return;
            var type = natureRenderer.GetType();
            type.GetProperty("Terrain")?.SetValue(natureRenderer, terrain);
            type.GetProperty("TerrainData")?.SetValue(natureRenderer, terrain.terrainData);
        }
    }
}
