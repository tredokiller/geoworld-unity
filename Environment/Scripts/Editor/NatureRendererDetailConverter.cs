#if UNITY_EDITOR

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace _Game.Scripts.Editor
{
    /// <summary>
    /// Копии деталей NatureManufacture для Nature Renderer: префаб копируется в Environment/Vegetation/Prefabs, его материалы —
    /// в Environment/Vegetation/Materials как «копия__материал_NR» с патченым шейдером (Foliage/Cross/Bark (Nature Renderer)),
    /// гладкостью 0.2 (без светлого ободка от неба) и дальним краем под управлением Nature Renderer
    /// (_ScaleFade, NatureRendererFade.hlsl). Мелочь (цветы) получает NatureRendererObjectSettings: свою дальность и без
    /// теней. Существующие копии и материалы не перезаписываются — их подстраивали руками.
    /// </summary>
    public static class NatureRendererDetailConverter
    {
        public const string PrefabFolder = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Prefabs";
        public const string MaterialFolder = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Materials";
        private const string PatchedShaderFolder = "Packages/com.tredokiller.geoworld/Environment/Shaders/NatureRendererTrees";
        private const string ObjectSettingsTypeName =
            "VisualDesignCafe.Rendering.Nature.NatureRendererObjectSettings, VisualDesignCafe.Rendering.Nature";
        private const float DetailSmoothness = 0.2f;
        // _ScaleFade.x для рендереров без Nature Renderer: им дальний край материала не нужен
        private const float NoFadeStart = 100000f;

        /// <summary>
        /// Класс растения: длина растворения у дальнего края (м); мелочи — своя дальность (м, 0 — общая из
        /// NatureRendererSettings) и без теней.
        /// </summary>
        public readonly struct DetailClass
        {
            public readonly float FadeLength;
            public readonly float RenderDistance;
            public readonly bool Shadows;

            public DetailClass(float fadeLength, float renderDistance = 0f, bool shadows = true)
            {
                FadeLength = fadeLength;
                RenderDistance = renderDistance;
                Shadows = shadows;
            }

            // трава растворяется на последних 60 м своей дальности (200 м): с высоты край луга не читается кругом
            public static DetailClass Grass => new(60f);
            public static DetailClass Flower => new(20f, 100f, shadows: false);
        }

        [MenuItem("Tools/Environment/Convert Selected Details For Nature Renderer")]
        private static void ConvertSelectedGrass() => ConvertSelected(DetailClass.Grass);

        [MenuItem("Tools/Environment/Convert Selected Flowers For Nature Renderer")]
        private static void ConvertSelectedFlowers() => ConvertSelected(DetailClass.Flower);

        private static void ConvertSelected(DetailClass detailClass)
        {
            StringBuilder report = new();

            foreach (GameObject selected in Selection.GetFiltered<GameObject>(SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(selected);
                GameObject copy = Convert(path, null, detailClass, report);
                report.AppendLine($"{selected.name} -> {(copy != null ? AssetDatabase.GetAssetPath(copy) : "пропущен")}");
            }

            Debug.Log($"[NatureRendererDetailConverter]\n{report}");
        }

        /// <summary>
        /// Копия префаба для Nature Renderer. targetName — имя копии (null — как у исходного); материалы называются по
        /// копии, поэтому у одной травы могут быть разные копии со своим цветом (камыш и посевы из meadow_01).
        /// Возвращает копию: существующую — как есть, если не replace. С replace содержимое заменяется: GUID остаётся, а
        /// корень получает новый fileID — ссылки на прежний префаб (записи профиля) пропадают, их надо выставить заново.
        /// null — префаба нет или в нём остался материал без версии для Nature Renderer: он не нарисовал бы весь слой.
        /// </summary>
        public static GameObject Convert(string sourcePath, string targetName, DetailClass detailClass, StringBuilder report = null,
            bool replace = false)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);

            if (source == null)
            {
                report?.AppendLine($"  нет префаба {sourcePath}");
                return null;
            }

            targetName ??= source.name;
            string targetPath = $"{PrefabFolder}/{targetName}.prefab";
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(targetPath);

            if (existing != null && !replace)
                return existing;

            Dictionary<string, Shader> patchedShaders = LoadPatchedShaders();
            GameObject contents = PrefabUtility.LoadPrefabContents(sourcePath);

            try
            {
                foreach (Renderer renderer in contents.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] materials = renderer.sharedMaterials;

                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] == null)
                            continue;

                        materials[i] = ConvertMaterial(materials[i], targetName, detailClass, patchedShaders, report);

                        // Nature Renderer отказывается от всего прототипа, если хоть один материал не для него
                        if (materials[i] == null)
                        {
                            Debug.LogError($"[NatureRendererDetailConverter] {source.name}: {renderer.name} has a material " +
                                           "without a Nature Renderer shader, the copy is not saved");
                            return null;
                        }
                    }

                    renderer.sharedMaterials = materials;
                }

                if (detailClass.RenderDistance > 0f || !detailClass.Shadows)
                    AddObjectSettings(contents, detailClass);

                PrefabUtility.SaveAsPrefabAsset(contents, targetPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(targetPath);
        }

        private static Material ConvertMaterial(Material source, string targetName, DetailClass detailClass,
            Dictionary<string, Shader> patchedShaders, StringBuilder report)
        {
            if (!patchedShaders.TryGetValue(KeyOf(source.shader.name), out Shader patched))
            {
                report?.AppendLine($"  материал {source.name}: нет патченого шейдера для '{source.shader.name}'");
                return null;
            }

            string path = $"{MaterialFolder}/{targetName}__{source.name}_NR.mat";
            Material converted = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (converted != null)
                return converted;

            AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), path);
            converted = AssetDatabase.LoadAssetAtPath<Material>(path);
            converted.shader = patched;
            // без него сборка вырезает вариант с процедурным инстансингом Nature Renderer
            converted.enableInstancing = true;

            if (converted.HasProperty("_SmoothnessRemapMax"))
                converted.SetFloat("_SmoothnessRemapMax", DetailSmoothness);

            if (converted.HasProperty("_ScaleFade") && converted.HasProperty("_NatureRendererDistanceControl"))
            {
                converted.SetVector("_ScaleFade", new Vector4(NoFadeStart, detailClass.FadeLength, 0f, 0f));
                converted.SetFloat("_NatureRendererDistanceControl", 1f);
                // растворение вдали в шейдере есть только с этим ключевым словом
                converted.SetFloat("_DISTANCEBLEND", 1f);
                converted.EnableKeyword("_DISTANCEBLEND_ON");
            }

            EditorUtility.SetDirty(converted);
            AssetDatabase.SaveAssetIfDirty(converted);
            return converted;
        }

        // Nature Renderer ищет эти настройки только на корне префаба детали и только при загрузке тайла.
        private static void AddObjectSettings(GameObject root, DetailClass detailClass)
        {
            System.Type type = System.Type.GetType(ObjectSettingsTypeName);

            if (type == null)
                return;

            Component settings = root.GetComponent(type) ?? root.AddComponent(type);
            SerializedObject serialized = new(settings);

            if (detailClass.RenderDistance > 0f)
            {
                serialized.FindProperty("_overrideRenderDistance").boolValue = true;
                serialized.FindProperty("_renderDistance").floatValue = detailClass.RenderDistance;
            }

            if (!detailClass.Shadows)
            {
                serialized.FindProperty("_overrideRenderShadows").boolValue = true;
                serialized.FindProperty("_renderShadows").boolValue = false;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Dictionary<string, Shader> LoadPatchedShaders()
        {
            Dictionary<string, Shader> result = new();

            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { PatchedShaderFolder }))
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));

                if (shader != null && shader.name.EndsWith("(Nature Renderer)"))
                    result[KeyOf(shader.name)] = shader;
            }

            return result;
        }

        // Полное имя без «(Nature Renderer)»: чужой шейдер с тем же последним словом не должен попасть в NM_Foliage.
        private static string KeyOf(string shaderName) => shaderName.Replace(" (Nature Renderer)", string.Empty);
    }
}

#endif
