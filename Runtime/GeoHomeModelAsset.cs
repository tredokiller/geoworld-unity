using UnityEngine;

namespace GeoWorld
{
    // One intact-building model of the home kit (intact/models): a body, fixed roofs and six swappable roof variants, all as
    // meshes with two sub-meshes (0 = the baked body atlas, 1 = the roof surface with tiling texture). Authored with the long
    // side along X and the front (door, porch) towards +Z, in Unity's axes.
    [CreateAssetMenu(menuName = "Geo World/Home model")]
    public class GeoHomeModelAsset : ScriptableObject
    {
        public string id, label;
        [Tooltip("The building categories of the export this model stands for (house, apartments, commercial, public, garage ...).")]
        public string[] categories = new string[0];
        [Tooltip("OpenStreetMap building=* values it carries (school, kindergarten, retail, garages ...).")]
        public string[] tags = new string[0];
        [Tooltip("Used only on a building carrying one of the tags (a school, a kindergarten).")]
        public bool tagOnly;
        public int floors = 1;
        [Tooltip("Authored footprint in metres, long side along X.")]
        public float length = 10, width = 7;
        [Tooltip("Height of the wall tops, and of the ridge above them on the pitched roofs.")]
        public float eaveHeight = 2.8f, rise = 2.2f;
        public Mesh body, roofFixed;
        public string[] variantNames = new string[0];
        public Mesh[] variants = new Mesh[0];
        public string defaultVariant = "gabled_along", defaultRoofMaterial = "metal_tile", defaultRoofColor = "#6e4636";
        [Tooltip("The baked body atlas.")]
        public Material material;

        public Mesh Variant(string name)
        {
            for (int i = 0; i < variantNames.Length && i < variants.Length; i++) if (variantNames[i] == name) return variants[i];
            return null;
        }
    }
}
