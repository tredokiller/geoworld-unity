using System;
using System.Reflection;
using UnityEngine;

namespace GeoWorld
{
    // Nature Renderer's settings for a world seen from the air: adds its NatureRendererDefaultSettings and
    // NatureRendererGlobalSettings at runtime (found by name, by reflection) with values for a drone over streamed tiles.
    // Without them Nature Renderer takes its own defaults, made for a walking camera:
    //  - grass thins and goes from 5-13 m in front of the camera (density in distance 0.125 with falloff 0.08, 0.0075 of the
    //    screen height), 1/8 of it stays farther out and it ends at 150 m;
    //  - tree shadows are drawn to the whole tree distance (some 1.5 km) where URP shows 50 m of shadows;
    //  - grass cells load 1024 instances a frame: late at 400 km/h, whole squares in front of the nose.
    // Nature Renderer reads them every frame; nothing is rebuilt. Plants refine them with NatureRendererObjectSettings on their
    // prefabs. On the Geo Tiled World object (its Inspector adds it when Nature Renderer is in the project); a scene needs one
    // set of Nature Renderer settings, so a game that adds its own does not need this.
    [DisallowMultipleComponent]
    public class GeoNatureRendererSettings : MonoBehaviour
    {
        const string NatureAssembly = ", VisualDesignCafe.Rendering.Nature";
        const string DefaultSettingsTypeName = "VisualDesignCafe.Rendering.Nature.NatureRendererDefaultSettings" + NatureAssembly;
        const string GlobalSettingsTypeName = "VisualDesignCafe.Rendering.Nature.NatureRendererGlobalSettings" + NatureAssembly;
        // Metres the trees stop short of the load radius: a tree cell farther out may belong to a tile not loaded yet.
        public const float TreeMargin = 400;
        public const float DefaultTreeDistance = 1600;

        [Header("Grass and flowers")]
        [Tooltip("Metres the grass is drawn to (Nature Renderer's own: 150). Flowers and litter set their own on their prefabs.")]
        [Min(10)] public float detailRenderDistance = 150;
        [Tooltip("The share of the grass that stays far out (Nature Renderer's own: 0.125).")]
        [Range(0, 1)] public float detailDensityInDistance = 0.2f;
        [Tooltip("Where the grass starts and stops thinning, as the share of the screen height a plant takes (Nature Renderer's own: 0.08, 0.0075). A 0.7 m plant at 60 degrees starts thinning at some 30 m and the thinned ones go at some 95 m.")]
        public Vector2 detailDensityInDistanceFalloff = new Vector2(0.02f, 0.006f);
        [Tooltip("Metres of grass shadows (Nature Renderer's own: 50).")]
        [Min(0)] public float detailShadowDistance = 20;

        [Header("Trees")]
        [Tooltip("Take the tree distance from the tile streamer's load radius (less Tree Margin, 400 m): 1600 m for 2 km.")]
        public bool treeDistanceFromLoadRadius = true;
        [Tooltip("Metres the trees are drawn to when not taken from the load radius (0: the camera's far plane). One for every species and field of view, as long as the trees' last LOD does not cull them sooner (Geo World checks the tree prefabs when it generates).")]
        [Min(0)] public float treeRenderDistance = DefaultTreeDistance;
        [Tooltip("Metres of tree shadows (Nature Renderer's own: the whole tree distance).")]
        [Min(0)] public float treeShadowDistance = 70;

        [Header("Streaming")]
        [Tooltip("Grass instances Nature Renderer loads a frame (its own: 1024).")]
        [Min(1024)] public int streamingBudget = 16384;

        Component defaults, global;

        // The tree distance these settings give: from the streamer's load radius, or the set one.
        public float TreeDistance
        {
            get
            {
                if (!treeDistanceFromLoadRadius) return treeRenderDistance;
                var streamer = GetComponent<GeoTileStreamer>();
                if (!streamer) streamer = FindStreamer();
                return streamer ? Mathf.Max(500, streamer.loadRadius - TreeMargin) : treeRenderDistance;
            }
        }

        GeoTileStreamer FindStreamer()
        {
            var world = GetComponent<GeoTiledWorld>();
            foreach (var streamer in FindObjectsByType<GeoTileStreamer>(FindObjectsSortMode.None))
                if (streamer.world == world || !world) return streamer;
            return null;
        }

        void OnEnable() { if (Application.isPlaying) Apply(); }

        void OnValidate() { if (Application.isPlaying && isActiveAndEnabled) Apply(); }

        void OnDestroy()
        {
            if (defaults) Destroy(defaults);
            if (global) Destroy(global);
        }

        // Writes the settings into Nature Renderer's components (added on this object when missing); does nothing without
        // Nature Renderer.
        public void Apply()
        {
            if (!defaults) defaults = Add(DefaultSettingsTypeName);
            if (!global) global = Add(GlobalSettingsTypeName);
            if (defaults)
            {
                var type = defaults.GetType();
                Set(type, defaults, "DetailRenderDistance", detailRenderDistance);
                Set(type, defaults, "DetailDensityInDistance", detailDensityInDistance);
                Set(type, defaults, "DetailDensityInDistanceFalloff", detailDensityInDistanceFalloff);
                Set(type, defaults, "DetailShadowDistance", detailShadowDistance);
                Set(type, defaults, "TreeRenderDistance", TreeDistance);
                Set(type, defaults, "TreeShadowDistance", treeShadowDistance);
            }
            if (global)
            {
                var type = global.GetType();
                Set(type, global, "StreamingBudget", streamingBudget);
                Set(type, global, "ForceLoadNearbyCells", true);
            }
        }

        Component Add(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type == null) return null;
            var found = GetComponent(type);
            return found ? found : gameObject.AddComponent(type);
        }

        static void Set(Type type, object target, string name, object value)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite) property.SetValue(target, value);
            else Debug.LogWarning("Geo World: Nature Renderer has no " + type.Name + "." + name + " to set.");
        }
    }
}
