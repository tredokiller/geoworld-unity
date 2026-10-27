using UnityEngine;

namespace GeoWorld
{
    // The water of a world is drawn with one material that all tiles share (Resources/GeoWorld/Water: the GeoWorld/Water shader,
    // coloured by the water's depth, with a soft shore, small waves and only a part of the sky's colour at grazing angles, no
    // mirror reflections). A tile made before it carries a URP Lit material of its own (smoothness 0.88) that reflects the
    // environment: under a bright sky a river turned white along its far stretches, and every tile held a copy of the material.
    // A tile that loads in Play mode takes the shared material (GeoWorldRoot), so worlds made earlier look right without being
    // generated again.
    public static class GeoWaterLook
    {
        public const string Resource = "GeoWorld/Water";
        public const string Group = "Water bodies";
        static Material shared;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() { shared = null; }

        // The shared water material (null if the package's resource is missing: the tile keeps its own).
        public static Material Material => shared ? shared : shared = Resources.Load<Material>(Resource);

        // Gives the water of a tile root (the meshes under its "Water bodies" group) the shared material. Returns how many renderers changed.
        public static int Apply(Transform root)
        {
            var material = Material;
            var group = root ? root.Find(Group) : null;
            if (!material || !group) return 0;
            int changed = 0;
            foreach (var renderer in group.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer.sharedMaterial == material) continue;
                renderer.sharedMaterial = material;
                changed++;
            }
            return changed;
        }
    }
}
