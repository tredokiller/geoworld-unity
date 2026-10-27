using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace GeoWorld.Editor
{
    // Photo-scanned road surfaces from ambientCG (CC0 1.0, https://ambientcg.com): a main road with white lines, and four
    // streets without lines from fairly sound to badly broken. Each picture is square and spans one road from edge to edge,
    // its lines running along v; it is laid along the road the same way as GeoMeshes.RoadUv does (v = metres / road width).
    // Files live in Assets/GeoWorld/Textures/Roads/<id>/<id>_1K-JPG_Color.jpg and _NormalGL.jpg.
    public static class GeoRoadPhotos
    {
        public const string Folder = GeoPaths.Package + "/Textures/Roads";
        public const string Marked = "Road001";                                          // white dashed centre, white edges, patches
        public static readonly string[] Plain = { "Road012B", "Road013B", "Road015B", "Road014B" }; // no lines; worn to broken

        public static string ColorPath(string id) => Folder + "/" + id + "/" + id + "_1K-JPG_Color.jpg";
        public static string NormalPath(string id) => Folder + "/" + id + "/" + id + "_1K-JPG_NormalGL.jpg";
        static bool Has(string id) => GeoPaths.Exists(ColorPath(id)) && GeoPaths.Exists(NormalPath(id));
        public static bool Available
        {
            get { if (!Has(Marked)) return false; foreach (var id in Plain) if (!Has(id)) return false; return true; }
        }

        // Main roads carry lines: in Ukraine trunk, primary, secondary and tertiary roads are marked; streets mostly are not.
        public static bool IsMarked(LineFeature road)
        {
            string type = road.type ?? "";
            return road.width >= 5 && (type == "motorway" || type == "trunk" || type == "primary" || type == "secondary" || type == "tertiary");
        }

        // The picture of a road: the marked one for main roads, else a plain one by its wear (with variety between roads).
        public static string For(LineFeature road) => IsMarked(road) ? Marked : Plain[GeoRoadTextures.VariantOf(road.id, road.wear)];
        public static string PlainFor(LineFeature road) => Plain[GeoRoadTextures.VariantOf(road.id, road.wear)];

#if UNITY_EDITOR
        // Normal maps must be imported as normal maps; the colour as sRGB, repeating along the road and not across it.
        public static void Configure()
        {
            Set(ColorPath(Marked), false); Set(NormalPath(Marked), true);
            foreach (var id in Plain) { Set(ColorPath(id), false); Set(NormalPath(id), true); }
        }

        static void Set(string path, bool normal)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (importer.textureType == type && importer.wrapModeU == TextureWrapMode.Clamp && importer.anisoLevel == 8) return;
            importer.textureType = type;
            importer.sRGBTexture = !normal;
            importer.mipmapEnabled = true;
            importer.wrapModeU = TextureWrapMode.Clamp; importer.wrapModeV = TextureWrapMode.Repeat;
            importer.anisoLevel = 8;
            importer.SaveAndReimport();
        }
#else
        public static void Configure() { }
#endif

    }
}
