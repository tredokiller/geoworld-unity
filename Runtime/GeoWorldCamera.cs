using UnityEngine;

namespace GeoWorld
{
    // The camera the world is seen through, for everything of Geo World that follows a camera (the environment layer, and the
    // streamers without a target of their own). A game that renders the world through a camera that is not Camera.main (an
    // FPV camera rendering into a texture, one of several drones) gives it with Use; without one, or while it is switched
    // off or destroyed, it is Camera.main as before.
    public static class GeoWorldCamera
    {
        static Camera assigned;

        public static Camera Current => assigned && assigned.isActiveAndEnabled ? assigned : Camera.main;

        // The camera given with Use, whether it is switched on or not (null: none).
        public static Camera Assigned => assigned ? assigned : null;

        public static void Use(Camera camera) { assigned = camera; }

        // Only the camera that is assigned: the next one is often given before the last one lets go.
        public static void Release(Camera camera)
        {
            if (assigned == camera) assigned = null;
        }

        // A Play mode entered without a domain reload does not keep the last one's camera.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() { assigned = null; }
    }
}
