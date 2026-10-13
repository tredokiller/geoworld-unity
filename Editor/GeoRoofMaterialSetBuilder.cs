using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Tools > Geo World > Create roof material set: select the folder holding <kind>_albedo.png / <kind>_normal.png
    // (the kit's "roofs" folder) and this creates one material per kind plus a GeoRoofMaterialSet asset next to them.
    public static class GeoRoofMaterialSetBuilder
    {
        static readonly string[] Kinds = { "metal_tile", "metal_sheet", "asbestos_cement", "clay_tiles", "slate", "bitumen", "shingles", "thatch" };

        [MenuItem("Tools/Geo World/Create roof material set")]
        public static void Create()
        {
            string folder = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                EditorUtility.DisplayDialog("Roof material set", "Select the folder with the roof textures first (<kind>_albedo.png, <kind>_normal.png).", "OK");
                return;
            }
            Selection.activeObject = CreateIn(folder);
        }

        // Makes (or updates) the materials and the set in a folder holding <kind>_albedo.png / <kind>_normal.png.
        public static GeoRoofMaterialSet CreateIn(string folder)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var set = AssetDatabase.LoadAssetAtPath<GeoRoofMaterialSet>(folder + "/RoofMaterialSet.asset");
            bool create = !set;
            if (create) set = ScriptableObject.CreateInstance<GeoRoofMaterialSet>();
            var list = new System.Collections.Generic.List<GeoRoofMaterialSet.Entry>();
            foreach (var kind in Kinds)
            {
                var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + kind + "_albedo.png");
                var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + kind + "_normal.png");
                if (albedo == null) continue;
                if (normal != null)
                {
                    var imp = AssetImporter.GetAtPath(folder + "/" + kind + "_normal.png") as TextureImporter;
                    if (imp != null && imp.textureType != TextureImporterType.NormalMap) { imp.textureType = TextureImporterType.NormalMap; imp.SaveAndReimport(); }
                }
                string matPath = folder + "/Roof_" + kind + ".mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                bool createMaterial = !m;
                if (createMaterial) m = new Material(shader) { name = "Roof_" + kind };
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", albedo); else m.mainTexture = albedo;
                if (normal != null && m.HasProperty("_BumpMap")) { m.SetTexture("_BumpMap", normal); m.EnableKeyword("_NORMALMAP"); }
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", kind.StartsWith("metal") ? 0.45f : 0.2f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                if (createMaterial) AssetDatabase.CreateAsset(m, matPath); else EditorUtility.SetDirty(m);
                list.Add(new GeoRoofMaterialSet.Entry { kind = kind, material = m });
            }
            set.entries = list.ToArray();
            if (create) AssetDatabase.CreateAsset(set, folder + "/RoofMaterialSet.asset"); else EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            Debug.Log("Roof material set with " + list.Count + " materials in " + folder);
            return set;
        }
    }
}
