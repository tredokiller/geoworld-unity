using UnityEngine;

namespace GeoWorld
{
    // Starts the Nature Renderer of a tile's terrain a little after the tile loads, one terrain at a time. Nature Renderer
    // sets itself up on its first LateUpdate (30-50 ms a terrain), which landed in the frame the tile was switched on,
    // on top of setting up the tile's objects. In Play mode this switches it off before that and back on two frames
    // later, at least Frames Apart frames after the last one; outside Play mode it does nothing. Geo World adds it to a
    // terrain whose trees Nature Renderer draws (and finds Nature Renderer by name: it does not depend on it).
    [DefaultExecutionOrder(-10000), DisallowMultipleComponent]
    public class GeoNatureRendererStart : MonoBehaviour
    {
        public static int FramesApart = 8;
        static int lastStart = -1000;

        Behaviour natureRenderer;
        int loadedFrame;

        void Awake()
        {
            if (!Application.isPlaying) return;
            foreach (var behaviour in GetComponents<Behaviour>())
                if (behaviour && behaviour.GetType().Name == "NatureRenderer") natureRenderer = behaviour;
            if (!natureRenderer || !natureRenderer.enabled) { enabled = false; return; }
            natureRenderer.enabled = false;
            loadedFrame = Time.frameCount;
            enabled = true; // Update starts it again
        }

        void Update()
        {
            int frame = Time.frameCount;
            if (frame - loadedFrame < 2 || frame - lastStart < FramesApart) return;
            lastStart = frame;
            if (natureRenderer) natureRenderer.enabled = true;
            enabled = false;
        }
    }
}
