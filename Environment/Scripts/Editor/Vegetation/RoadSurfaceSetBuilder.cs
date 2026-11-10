using System.IO;
using _Game.Scripts.World;
using GeoWorld;
using GeoWorld.Editor;
using UnityEditor;
using UnityEngine;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>
    /// Набор фактур полотна дорог (RoadSurfaceSet): асфальт — полосы GeoRoadTextures пакета GeoWorld (их генерирует сам пакет,
    /// GeoRoadTextures.EnsureGenerated).
    /// </summary>
    public static class RoadSurfaceSetBuilder
    {
        private const string AssetPath = GeoAddressableAssets.RoadSurfaceSet;

        [MenuItem("Tools/Environment/Build Road Surface Set")]
        public static void BuildMenu() => Debug.Log("[RoadSurfaceSetBuilder] " + Build());

        public static string Build()
        {
            GeoRoadTextures.EnsureGenerated();
            var albedo = new Texture2D[GeoRoadTextures.Variants];
            var normal = new Texture2D[GeoRoadTextures.Variants];

            for (int i = 0; i < GeoRoadTextures.Variants; i++)
            {
                albedo[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(GeoRoadTextures.DiffusePath(i));
                normal[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(GeoRoadTextures.NormalPath(i));

                if (albedo[i] == null || normal[i] == null)
                    return "missing " + GeoRoadTextures.DiffusePath(i) + " or its normal map";
            }

            RoadSurfaceSet set = AssetDatabase.LoadAssetAtPath<RoadSurfaceSet>(AssetPath);
            bool created = set == null;

            if (created)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
                set = ScriptableObject.CreateInstance<RoadSurfaceSet>();
            }

            set.asphaltAlbedo = albedo;
            set.asphaltNormal = normal;
            set.stripWidthMetres = GeoRoadTextures.WidthMetres;
            set.stripLengthMetres = GeoRoadTextures.LengthMetres;

            if (created)
                AssetDatabase.CreateAsset(set, AssetPath);
            else
                EditorUtility.SetDirty(set);

            AssetDatabase.SaveAssetIfDirty(set);
            return $"{albedo.Length} asphalt strips ({albedo[0].width}×{albedo[0].height}, {albedo[0].graphicsFormat}) in {AssetPath}";
        }
    }
}
