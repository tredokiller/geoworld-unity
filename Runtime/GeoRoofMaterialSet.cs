using System;
using UnityEngine;

namespace GeoWorld
{
    // Roof materials of the intact-building kit, one per kind. Each is a URP/Lit (or Standard) material with the shared TILING roof
    // texture (1 UV unit = 2.1 m, repeat) as base map and normal map; the colour is applied per building by GeoHomeModel.
    // The extractor's roof.material values map to a kind through Aliases below.
    [CreateAssetMenu(menuName = "Geo World/Roof material set")]
    public class GeoRoofMaterialSet : ScriptableObject
    {
        [Serializable] public class Entry { public string kind; public Material material; }

        public Entry[] entries = new Entry[0];

        // roof.material (OpenStreetMap roof:material) -> kind of the kit
        public static string KindFor(string appMaterial)
        {
            if (string.IsNullOrEmpty(appMaterial)) return null;
            switch (appMaterial.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_'))
            {
                case "metal": case "metal_tile": case "metal_tiles": case "tile_metal": return "metal_tile";
                case "metal_sheet": case "corrugated_iron": case "steel": case "iron": case "profiled_sheet": case "zinc": case "tin": case "copper": return "metal_sheet";
                case "asbestos_cement": case "asbestos": case "eternit": case "fibre_cement": case "concrete_sheet": return "asbestos_cement";
                case "clay_tiles": case "roof_tiles": case "tiles": case "tile": case "ceramic": case "terracotta": return "clay_tiles";
                case "slate": case "stone": return "slate";
                case "bitumen": case "tar_paper": case "roofing_felt": case "felt": case "concrete": case "membrane": case "glass": case "plastic": return "bitumen";
                case "shingles": case "asphalt_shingles": case "bitumen_shingles": return "shingles";
                case "thatch": case "straw": case "reed": return "thatch";
                default: return null;
            }
        }

        public Material For(string appMaterial)
        {
            string kind = KindFor(appMaterial);
            if (kind == null) return null;
            foreach (var e in entries) if (e != null && e.kind == kind) return e.material;
            return null;
        }
    }
}
