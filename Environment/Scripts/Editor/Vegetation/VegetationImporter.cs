using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace _Game.Scripts.Editor.Vegetation
{
    /// <summary>
    /// Переносит в проект растительность, сгенерированную в Blender (Tools~/TreeGen пакета): у каждого ассета в папке выдачи —
    /// manifest.json, FBX уровней детализации и PNG текстур. Кладёт их в Environment/Vegetation/Generated/&lt;group&gt;,
    /// настраивает импорт (карточки — альфа с сохранением покрытия в мипах, нормали, маски линейно), делает материалы копиями
    /// материалов Nature Renderer, что уже стоят на деревьях и траве (бук: кора, листва, крест; короткая трава) — с ними
    /// приходят пора года, ветер, угасание вдали, поток винтов и лужи, — и собирает префаб: дерево — LODGroup с коллайдером
    /// ствола (как у бука), деталь (трава, ветки, камни) — один меш с одним материалом (Nature Renderer берёт у деталей только
    /// LOD0; части меша с разными материалами — отдельными префабами), проп (упавшая ветка, камень у точки взлёта) — LODGroup
    /// с коллайдером по мешу.
    /// </summary>
    public static class VegetationImporter
    {
        public const string Root = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Generated";
        private const string BarkReference = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Materials/M_beech_bark_02.mat";
        private const string LeavesReference = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Materials/M_beech_leaves.mat";
        private const string CrossReference = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Materials/M_beech_Tree_01_Cross.mat";
        private const string GrassReference = "Packages/com.tredokiller.geoworld/Environment/Vegetation/Materials/prefab_Unity_Terrain_grass_02_1__M_grass_beech_forest_02_NR.mat";

        [Serializable]
        public class Manifest
        {
            public string name;
            // tree | detail | prop (ветка, камень у точки взлёта: GameObject с коллайдером по мешу)
            public string type = "tree";
            // подпапка в Generated (вид: Pine, Oak, Grass, Ground)
            public string group = "Misc";
            public float height;
            // коллайдер ствола дерева: радиус и высота (0 — высота дерева)
            public float trunkRadius = 0.2f;
            public float colliderHeight;
            // хвойное: хвоя круглый год (_Evergreen у листвы и креста); у группы Pine — всегда
            public bool evergreen;
            public LodEntry[] lods = Array.Empty<LodEntry>();
            public MaterialEntry[] materials = Array.Empty<MaterialEntry>();
            // деталь: свои дальность, тени, редение вдали (NatureRendererObjectSettings на корне префаба)
            public NatureRendererEntry nr;
        }

        [Serializable]
        public class NatureRendererEntry
        {
            // 0 — как в глобальных настройках
            public float renderDistance;
            public bool shadows = true;
            // < 0 — как в глобальных
            public float densityInDistance = -1f;
            public float[] densityFalloff;
        }

        [Serializable]
        public class LodEntry
        {
            public string fbx;
            // доля высоты экрана, ниже которой уровень сменяется следующим
            public float screen = 0.5f;
            // материалы подмешей по порядку (id из materials)
            public string[] submeshes = Array.Empty<string>();
            public bool castShadows = true;
        }

        [Serializable]
        public class MaterialEntry
        {
            public string id;
            // bark | leaves | cross | grass
            public string role;
            public string albedo;
            public string normal;
            public string mask;
            // отрицательное — как у образца
            public float alphaCutoff = -1f;
            public float smoothnessMax = -1f;
            public float aoMax = -1f;
            public float normalScale = -1f;
            public float puddleSink = -1f;
            public float sizeVariation = -1f;
            public float stiffness = -1f;
            public float drag = -1f;
            public float shiverDrag = -1f;
            public float rotorWashClear = -1f;
            public float cullFarStart = -1f;
            public float scaleFadeLength = -1f;
            public float alphaDrag = -1f;
            public float specular = -1f;
            public float shiverDirectionality = -1f;
            public float colorNoiseSpread = -1f;
            public float aoMin = -1f;
            public float smoothnessMin = -1f;
            public float initialBend = -1f;
            public float heightDrag = -1f;
            public float invertBackfaceNormal = -1f;
            // 1 — пора года не сушит (всходы озимых зелёные осенью и под снегом)
            public float seasonKeep = -1f;
            // 1 — угасание вдали по Nature Renderer (_NatureRendererDistanceControl, у листвы и травы — с растворением)
            public float distanceControl = -1f;
            // 0 — без подкраски ветром (у образца травы она есть: цвет темнеет и теплеет, мерцая с порывами — стерня рыжела)
            public float windColorMix = -1f;
            public float[] scaleFade;
            public float[] healthyColor;
            public float[] dryColor;
            public float[] tiling;
        }

        [MenuItem("Tools/Environment/Import Generated Vegetation")]
        private static void ImportMenu()
        {
            string folder = EditorUtility.OpenFolderPanel("Folder with generated vegetation (manifest.json in subfolders)", "", "");

            if (!string.IsNullOrEmpty(folder))
                Debug.Log("[VegetationImporter] " + ImportAll(folder));
        }

        /// <summary>Все ассеты в подпапках folder (и сама folder), где есть manifest.json.</summary>
        public static string ImportAll(string folder)
        {
            StringBuilder report = new();
            List<string> manifests = new();

            if (File.Exists(Path.Combine(folder, "manifest.json")))
                manifests.Add(Path.Combine(folder, "manifest.json"));

            foreach (string dir in Directory.GetDirectories(folder))
            {
                string path = Path.Combine(dir, "manifest.json");

                if (File.Exists(path))
                    manifests.Add(path);
            }

            Dictionary<string, float> atlasCutoffs = AtlasCutoffs(manifests, report);

            foreach (string path in manifests)
            {
                try
                {
                    report.AppendLine(Import(path, atlasCutoffs));
                }
                catch (Exception e)
                {
                    report.AppendLine($"FAILED {path}: {e.Message}");
                    Debug.LogException(e);
                }
            }

            return report.ToString();
        }

        // Атлас, общий для нескольких ассетов группы (field_cards_*, grass_atlas_*), — один файл, и порог для сохранения
        // покрытия в его мипах один: наименьший из материалов на нём (иначе каждый импорт перезаписывал его своим).
        private static Dictionary<string, float> AtlasCutoffs(List<string> manifests, StringBuilder report)
        {
            Dictionary<string, float> result = new();

            foreach (string path in manifests)
            {
                Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(path));

                foreach (MaterialEntry m in manifest.materials)
                {
                    if (m.role == "bark" || string.IsNullOrEmpty(m.albedo))
                        continue;

                    string key = manifest.group + "/" + m.albedo;
                    float cutoff = Cutoff(m);

                    if (result.TryGetValue(key, out float other))
                    {
                        if (!Mathf.Approximately(other, cutoff))
                            report.AppendLine($"note: {key} is shared by cutoffs {other} and {cutoff}; its mips keep coverage at {Mathf.Min(other, cutoff)}");

                        cutoff = Mathf.Min(other, cutoff);
                    }

                    result[key] = cutoff;
                }
            }

            return result;
        }

        // Порог материала: из манифеста, а без него — как у материала-образца (его и получит копия).
        private static float Cutoff(MaterialEntry m)
        {
            if (m.alphaCutoff >= 0f)
                return m.alphaCutoff;

            Material reference = AssetDatabase.LoadAssetAtPath<Material>(ReferenceFor(m.role));
            return reference != null && reference.HasProperty("_AlphaCutoff") ? reference.GetFloat("_AlphaCutoff") : 0.5f;
        }

        private static string ReferenceFor(string role) => role switch
        {
            "bark" => BarkReference,
            "cross" => CrossReference,
            "grass" => GrassReference,
            _ => LeavesReference
        };

        public static string Import(string manifestPath) => Import(manifestPath, null);

        private static string Import(string manifestPath, Dictionary<string, float> atlasCutoffs)
        {
            string source = Path.GetDirectoryName(manifestPath);
            Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            string dest = Root + "/" + manifest.group;
            Directory.CreateDirectory(dest);

            // файлы: FBX и текстуры (общие текстуры вида — одним файлом на группу)
            HashSet<string> files = new();

            foreach (LodEntry lod in manifest.lods)
                files.Add(lod.fbx);

            foreach (MaterialEntry m in manifest.materials)
            {
                foreach (string t in new[] { m.albedo, m.normal, m.mask })
                {
                    if (!string.IsNullOrEmpty(t))
                        files.Add(t);
                }
            }

            foreach (string file in files)
                File.Copy(Path.Combine(source, file), Path.Combine(dest, file), true);

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            foreach (MaterialEntry m in manifest.materials)
            {
                bool card = m.role != "bark";
                float cutoff = atlasCutoffs != null && atlasCutoffs.TryGetValue(manifest.group + "/" + m.albedo, out float shared)
                    ? shared
                    : Cutoff(m);
                ConfigureTexture(dest, m.albedo, TextureImporterType.Default, true, card, cutoff);
                ConfigureTexture(dest, m.normal, TextureImporterType.NormalMap, false, false, cutoff);
                ConfigureTexture(dest, m.mask, TextureImporterType.Default, false, false, cutoff);
            }

            foreach (LodEntry lod in manifest.lods)
                ConfigureModel(dest + "/" + lod.fbx);

            Dictionary<string, Material> materials = new();

            bool evergreen = manifest.evergreen || manifest.group == "Pine";

            foreach (MaterialEntry m in manifest.materials)
                materials[m.id] = BuildMaterial(dest, manifest.group, m, evergreen);

            string prefab = BuildPrefab(dest, manifest, materials);
            return $"{manifest.name}: {manifest.lods.Length} LOD, {materials.Count} materials -> {prefab}";
        }

        private static void ConfigureTexture(string dest, string file, TextureImporterType type, bool sRGB, bool alphaCard, float cutoff)
        {
            if (string.IsNullOrEmpty(file))
                return;

            string path = dest + "/" + file;

            if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
                return;

            importer.textureType = type;
            importer.sRGBTexture = sRGB;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = alphaCard;
            importer.mipmapEnabled = true;
            // карточки листвы и хвои не редеют вдали: мипы сохраняют долю непрозрачного при пороге отсечения
            importer.mipMapsPreserveCoverage = alphaCard;
            importer.alphaTestReferenceValue = cutoff;
            importer.maxTextureSize = 2048;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.wrapMode = alphaCard ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            importer.anisoLevel = 4;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        private static void ConfigureModel(string path)
        {
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
                return;

            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.isReadable = true;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.importBlendShapes = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importAnimation = false;
            importer.addCollider = false;
            importer.weldVertices = true;
            importer.SaveAndReimport();
        }

        private static Material BuildMaterial(string dest, string group, MaterialEntry entry, bool evergreen)
        {
            string path = $"{dest}/M_{group}_{entry.id}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            string reference = ReferenceFor(entry.role);

            if (material == null)
            {
                material = new Material(AssetDatabase.LoadAssetAtPath<Material>(reference));
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.CopyPropertiesFromMaterial(AssetDatabase.LoadAssetAtPath<Material>(reference));
            }

            Texture2D albedo = Load(dest, entry.albedo);
            Texture2D normal = Load(dest, entry.normal);
            Texture2D mask = Load(dest, entry.mask);

            if (entry.role == "bark")
            {
                material.SetTexture("_TrunkBaseColorMap", albedo);
                material.SetTexture("_TrunkNormalMap", normal);
                material.SetTexture("_TrunkMaskMap", mask);
                material.SetColor("_TrunkBaseColor", Color.white);

                if (entry.tiling is { Length: 2 })
                    material.SetVector("_TrunkTilingOffset", new Vector4(entry.tiling[0], entry.tiling[1], 0f, 0f));

                if (entry.normalScale >= 0f)
                    material.SetFloat("_TrunkNormalScale", entry.normalScale);

                if (entry.smoothnessMax >= 0f)
                    material.SetFloat("_TrunkSmoothnessRemapMax", entry.smoothnessMax);

                if (entry.aoMax >= 0f)
                    material.SetFloat("_TrunkAORemapMax", entry.aoMax);

                // у копии коры бука остаётся её второй слой (своя кора по маске и UV3, которых в наших мешах нет):
                // второй слой — те же текстуры, тогда маска ни на что не влияет
                material.SetTexture("_BarkBaseColorMap", albedo);
                material.SetTexture("_BarkNormalMap", normal);
                material.SetTexture("_BarkMaskMap", mask);
                material.SetColor("_BarkBaseColor", Color.white);
                material.SetVector("_BarkTilingOffset", material.GetVector("_TrunkTilingOffset"));
                Set(material, "_BarkUseUV3", 0f);
                Set(material, "_TrunkMetallic", 0f);
                Set(material, "_BarkMetallic", 0f);
                Set(material, "_BarkNormalScale", entry.normalScale);
                Set(material, "_BarkSmoothnessRemapMax", entry.smoothnessMax);
                Set(material, "_InitialBend", entry.initialBend);
                Set(material, "_HeightDrag", entry.heightDrag);
                Set(material, "_Stiffness", entry.stiffness);
                Set(material, "_Drag", entry.drag);
            }
            else
            {
                material.SetTexture("_BaseColorMap", albedo);
                material.SetTexture("_NormalMap", normal);

                if (entry.role != "cross" || mask != null)
                    material.SetTexture("_MaskMap", mask);

                Set(material, "_AlphaCutoff", entry.alphaCutoff);
                Set(material, "_SmoothnessRemapMax", entry.smoothnessMax);
                Set(material, "_AORemapMax", entry.aoMax);
                Set(material, "_NormalScale", entry.normalScale);
                Set(material, "_PuddleSink", entry.puddleSink);
                Set(material, "_SizeVariation", entry.sizeVariation);
                Set(material, "_Stiffness", entry.stiffness);
                Set(material, "_Drag", entry.drag);
                Set(material, "_ShiverDrag", entry.shiverDrag);
                Set(material, "_RotorWashClear", entry.rotorWashClear);
                Set(material, "_CullFarStart", entry.cullFarStart);
                Set(material, "_AlphaDrag", entry.alphaDrag);
                Set(material, "_Specular", entry.specular);
                Set(material, "_ShiverDirectionality", entry.shiverDirectionality);
                Set(material, "_ColorNoiseSpread", entry.colorNoiseSpread);
                Set(material, "_AORemapMin", entry.aoMin);
                Set(material, "_SmoothnessRemapMin", entry.smoothnessMin);
                Set(material, "_InitialBend", entry.initialBend);
                Set(material, "_HeightDrag", entry.heightDrag);
                Set(material, "_InvertBackfaceNormal", entry.invertBackfaceNormal);
                Set(material, "_SeasonKeep", entry.seasonKeep);

                if (entry.scaleFade is { Length: 2 } && material.HasProperty("_ScaleFade"))
                    material.SetVector("_ScaleFade", new Vector4(entry.scaleFade[0], entry.scaleFade[1], 0f, 0f));

                if (entry.distanceControl >= 0f && material.HasProperty("_NatureRendererDistanceControl"))
                {
                    bool on = entry.distanceControl > 0.5f;
                    material.SetFloat("_NatureRendererDistanceControl", on ? 1f : 0f);

                    if (entry.role != "cross" && material.HasProperty("_DISTANCEBLEND"))
                    {
                        material.SetFloat("_DISTANCEBLEND", on ? 1f : 0f);

                        if (on)
                            material.EnableKeyword("_DISTANCEBLEND_ON");
                        else
                            material.DisableKeyword("_DISTANCEBLEND_ON");
                    }
                }

                if (material.HasProperty("_Evergreen"))
                    material.SetFloat("_Evergreen", evergreen && entry.role != "grass" ? 1f : 0f);

                if (entry.windColorMix >= 0f && material.HasProperty("_WINDCOLORMIX"))
                {
                    bool on = entry.windColorMix > 0.5f;
                    material.SetFloat("_WINDCOLORMIX", on ? 1f : 0f);
                    material.SetFloat("_WindColorMix", on ? 1f : 0f);

                    if (on)
                        material.EnableKeyword("_WINDCOLORMIX_ON");
                    else
                        material.DisableKeyword("_WINDCOLORMIX_ON");
                }

                if (entry.scaleFadeLength >= 0f && material.HasProperty("_ScaleFade"))
                {
                    Vector4 fade = material.GetVector("_ScaleFade");
                    material.SetVector("_ScaleFade", new Vector4(fade.x, entry.scaleFadeLength, fade.z, fade.w));
                }

                if (entry.healthyColor is { Length: >= 3 })
                    material.SetColor("_HealthyColor", new Color(entry.healthyColor[0], entry.healthyColor[1], entry.healthyColor[2], 1f));

                if (entry.dryColor is { Length: >= 3 })
                    material.SetColor("_DryColor", new Color(entry.dryColor[0], entry.dryColor[1], entry.dryColor[2], 1f));
            }

            material.enableInstancing = true;
            material.name = Path.GetFileNameWithoutExtension(path);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        private static void Set(Material material, string property, float value)
        {
            if (value >= 0f && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static Texture2D Load(string dest, string file) =>
            string.IsNullOrEmpty(file) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(dest + "/" + file);

        private static Mesh LoadMesh(string path)
        {
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset is Mesh mesh)
                    return mesh;
            }

            throw new InvalidDataException("no mesh in " + path);
        }

        private static string BuildPrefab(string dest, Manifest manifest, Dictionary<string, Material> materials)
        {
            string path = $"{dest}/prefab_{manifest.name}.prefab";

            if (manifest.type == "detail")
                return BuildDetailPrefabs(dest, path, manifest, materials);

            GameObject root = new(manifest.name);

            try
            {
                LODGroup group = root.AddComponent<LODGroup>();
                LOD[] lods = new LOD[manifest.lods.Length];

                for (int i = 0; i < manifest.lods.Length; i++)
                {
                    LodEntry lod = manifest.lods[i];
                    GameObject child = new($"{manifest.name}_LOD{i}");
                    child.transform.SetParent(root.transform, false);
                    child.AddComponent<MeshFilter>().sharedMesh = LoadMesh(dest + "/" + lod.fbx);
                    MeshRenderer renderer = child.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = Pick(lod, materials);
                    renderer.shadowCastingMode = lod.castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
                    lods[i] = new LOD(lod.screen, new Renderer[] { renderer });
                }

                // ветка или камень: столкновение по форме (статичный, без Rigidbody — невыпуклый можно)
                if (manifest.type == "prop")
                    root.AddComponent<MeshCollider>().sharedMesh = LoadMesh(dest + "/" + manifest.lods[0].fbx);
                // ствол — для столкновений дрона: у деревьев террейна Unity берёт коллайдер с корня префаба
                else if (manifest.trunkRadius > 0f)
                {
                    CapsuleCollider capsule = root.AddComponent<CapsuleCollider>();
                    float height = manifest.colliderHeight > 0f ? manifest.colliderHeight : manifest.height;
                    capsule.radius = manifest.trunkRadius;
                    capsule.height = height;
                    capsule.direction = 1;
                    capsule.center = new Vector3(0f, height * 0.5f, 0f);
                }

                group.SetLODs(lods);
                group.fadeMode = LODFadeMode.CrossFade;
                group.animateCrossFading = true;
                group.RecalculateBounds();

                // дальний крест не гаснет ближе 1.6 км и при масштабе 0.5 (GeoNatureRendererTrees.CheckLods: порог
                // последнего уровня <= размер / 1847.5 при масштабе 1, с запасом на 0.5 — размер / 3700)
                // (только деревья: камень в 0.3 м с таким порогом не гас бы никогда)
                float limit = group.size / 3700f;

                if (manifest.type == "tree" && lods[^1].screenRelativeTransitionHeight > limit)
                {
                    lods[^1].screenRelativeTransitionHeight = limit;
                    group.SetLODs(lods);
                }

                GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            return path;
        }

        // Деталь террейна — один меш с одним материалом: с несколькими DetailPrototype.Validate её отвергает («A detail
        // prototype must have exactly one material»), и ни Unity, ни Nature Renderer её не рисуют вовсе, молча (стерня из
        // карточек и соломы так и не появилась на поле). Поэтому меш из нескольких частей делится: часть — свой меш-ассет и
        // свой префаб, первая — prefab_<name>, остальные — prefab_<name>_part2… (в профиль идут отдельными записями).
        private static string BuildDetailPrefabs(string dest, string path, Manifest manifest, Dictionary<string, Material> materials)
        {
            LodEntry lod = manifest.lods[0];
            Mesh mesh = LoadMesh(dest + "/" + lod.fbx);
            Material[] picked = Pick(lod, materials);

            if (picked.Length <= 1)
            {
                SaveDetailPrefab(path, manifest.name, mesh, picked.Length == 1 ? picked[0] : null, lod, manifest.nr);
                return path;
            }

            List<string> paths = new();

            for (int i = 0; i < picked.Length; i++)
            {
                string part = i == 0 ? path : $"{dest}/prefab_{manifest.name}_part{i + 1}.prefab";
                Mesh partMesh = SaveSubmesh(mesh, i, $"{dest}/{manifest.name}_part{i + 1}.asset");
                SaveDetailPrefab(part, Path.GetFileNameWithoutExtension(part).Substring("prefab_".Length), partMesh, picked[i], lod, manifest.nr);
                paths.Add(part);
            }

            return string.Join(", ", paths);
        }

        private static void SaveDetailPrefab(string path, string name, Mesh mesh, Material material, LodEntry lod, NatureRendererEntry nr)
        {
            GameObject root = new(name);

            try
            {
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer renderer = root.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { material };
                renderer.shadowCastingMode = lod.castShadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                AddNatureRendererSettings(root, nr);
                GameObjectUtility.SetStaticEditorFlags(root, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // Часть (подмеш) меша — отдельным мешем-ассетом: только её вершины, индексы заново. Ассет перезаписывается на месте,
        // чтобы префабы не теряли ссылку при повторном импорте, — данными прямо в тот же меш: EditorUtility.CopySerialized
        // меняет его сериализованные данные, но меш на видеокарте остаётся прежним до перезапуска редактора (старая стерня
        // без нового веса ветра так и не рисовалась).
        private static Mesh SaveSubmesh(Mesh source, int submesh, string path)
        {
            int[] triangles = source.GetTriangles(submesh);
            Dictionary<int, int> remap = new();
            List<int> order = new();
            int[] local = new int[triangles.Length];

            for (int i = 0; i < triangles.Length; i++)
            {
                if (!remap.TryGetValue(triangles[i], out int index))
                {
                    index = order.Count;
                    remap[triangles[i]] = index;
                    order.Add(triangles[i]);
                }

                local[i] = index;
            }

            Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            Mesh part = existing != null ? existing : new Mesh();
            part.Clear();
            part.name = Path.GetFileNameWithoutExtension(path);
            part.SetVertices(Select(source.vertices, order));

            if (source.normals.Length > 0)
                part.SetNormals(Select(source.normals, order));

            if (source.tangents.Length > 0)
                part.SetTangents(Select(source.tangents, order));

            // форматы — как у исходного (цвет 8 бит, UV по два числа). Альфа цвета — вес ветра (_AlphaDrag), и ветер NM с
            // малым весом (сопротивление × альфа) теряет треугольники: карточки стерни (альфа до 0.11, низ 0) не рисовались
            // вовсе; с 40/255 видно всё, а лишний вес на жёсткой стерне не заметен
            if (source.colors32.Length > 0)
            {
                Color32[] colours = Select(source.colors32, order);

                for (int i = 0; i < colours.Length; i++)
                    colours[i].a = (byte)Mathf.Max(colours[i].a, 40);

                part.SetColors(colours);
            }

            for (int channel = 0; channel < 4; channel++)
            {
                UnityEngine.Rendering.VertexAttribute attribute = UnityEngine.Rendering.VertexAttribute.TexCoord0 + channel;

                if (!source.HasVertexAttribute(attribute))
                    continue;

                if (source.GetVertexAttributeDimension(attribute) == 2)
                {
                    List<Vector2> uvs = new();
                    source.GetUVs(channel, uvs);
                    part.SetUVs(channel, Select(uvs.ToArray(), order));
                }
                else
                {
                    List<Vector4> uvs = new();
                    source.GetUVs(channel, uvs);
                    part.SetUVs(channel, Select(uvs.ToArray(), order));
                }
            }

            part.SetTriangles(local, 0);
            part.RecalculateBounds();

            if (existing == null)
            {
                AssetDatabase.CreateAsset(part, path);
                return part;
            }

            EditorUtility.SetDirty(part);
            AssetDatabase.SaveAssetIfDirty(part);
            return part;
        }

        private static T[] Select<T>(T[] values, List<int> order)
        {
            T[] result = new T[order.Count];

            for (int i = 0; i < result.Length; i++)
                result[i] = values[order[i]];

            return result;
        }

        // Nature Renderer ищет эти настройки только на корне префаба детали (NatureRendererDetailConverter.AddObjectSettings).
        private static void AddNatureRendererSettings(GameObject root, NatureRendererEntry nr)
        {
            if (nr == null)
                return;

            System.Type type = System.Type.GetType("VisualDesignCafe.Rendering.Nature.NatureRendererObjectSettings, VisualDesignCafe.Rendering.Nature");

            if (type == null)
                return;

            Component settings = root.GetComponent(type) ?? root.AddComponent(type);
            SerializedObject serialized = new(settings);

            if (nr.renderDistance > 0f)
            {
                serialized.FindProperty("_overrideRenderDistance").boolValue = true;
                serialized.FindProperty("_renderDistance").floatValue = nr.renderDistance;
            }

            serialized.FindProperty("_overrideRenderShadows").boolValue = true;
            serialized.FindProperty("_renderShadows").boolValue = nr.shadows;

            if (nr.densityInDistance >= 0f && serialized.FindProperty("_overrideDensityInDistance") != null)
            {
                serialized.FindProperty("_overrideDensityInDistance").boolValue = true;
                serialized.FindProperty("_densityInDistance").floatValue = nr.densityInDistance;
            }

            if (nr.densityFalloff is { Length: 2 } && serialized.FindProperty("_overrideDensityInDistanceFalloff") != null)
            {
                serialized.FindProperty("_overrideDensityInDistanceFalloff").boolValue = true;
                SerializedProperty falloff = serialized.FindProperty("_densityInDistanceFalloff");

                if (falloff != null && falloff.propertyType == SerializedPropertyType.Vector2)
                    falloff.vector2Value = new Vector2(nr.densityFalloff[0], nr.densityFalloff[1]);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Material[] Pick(LodEntry lod, Dictionary<string, Material> materials)
        {
            Material[] result = new Material[lod.submeshes.Length];

            for (int i = 0; i < result.Length; i++)
                result[i] = materials.TryGetValue(lod.submeshes[i], out Material m) ? m : null;

            return result;
        }
    }
}
