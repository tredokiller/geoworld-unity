using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The intact-building models (intact/models: a folder per model with a GLB, baked albedo and normal map and _meta.json,
    // plus INDEX.json) brought into Assets/GeoWorld/Homes/<ID>/ : one mesh per GLB node (Body, RoofFixed, Roof_<variant>) in
    // Unity's axes with two sub-meshes (body atlas, roof surface), the two textures, a URP material and a Geo Home Model asset,
    // plus Assets/GeoWorld/Homes/Home library.asset with the roof material set (Assets/GeoWorld/Models/Roofs).
    [InitializeOnLoad]
    public static class GeoHomeImporter
    {
        public const string Folder = GeoPaths.Project + "/Homes";
        public const string RoofTextures = GeoPaths.Package + "/Models/Roofs";

        static readonly Dictionary<string, Material> tinted = new Dictionary<string, Material>();

        static GeoHomeImporter()
        {
            GeoHomes.RoofMaterialProvider = TintedRoof;
        }

        // intact/models of the Building Extractor repository: the folder chosen on this computer (remembered for every
        // project that uses the package), else searched for in the project's parents and their sub-folders (the project
        // may sit beside the extractor)
        public const string SourceKey = "GeoWorld.HomeModels";
        public static string DefaultSource
        {
            get
            {
                string chosen = EditorPrefs.GetString(SourceKey, "");
                if (!string.IsNullOrEmpty(chosen) && File.Exists(Path.Combine(chosen, "INDEX.json"))) return chosen;
                var dir = new DirectoryInfo(Application.dataPath);
                for (int up = 0; up < 5 && dir != null; up++, dir = dir.Parent)
                {
                    string direct = Path.Combine(dir.FullName, "intact", "models");
                    if (File.Exists(Path.Combine(direct, "INDEX.json"))) return direct;
                    foreach (var child in SafeDirectories(dir))
                    {
                        string nested = Path.Combine(child, "intact", "models");
                        if (File.Exists(Path.Combine(nested, "INDEX.json"))) return nested;
                    }
                }
                return null;
            }
        }

        static IEnumerable<string> SafeDirectories(DirectoryInfo dir)
        {
            try { return Directory.GetDirectories(dir.FullName); } catch { return new string[0]; }
        }

        public static GeoHomeLibrary Library => AssetDatabase.LoadAssetAtPath<GeoHomeLibrary>(GeoHomeLibrary.DefaultPath);

        [MenuItem("Tools/Geo World/Import home models")]
        public static void ImportMenu()
        {
            string source = DefaultSource;
            if (source == null)
            {
                source = EditorUtility.OpenFolderPanel("intact/models of the Building Extractor repository (with INDEX.json)", "", "");
                if (string.IsNullOrEmpty(source)) return;
                if (!File.Exists(Path.Combine(source, "INDEX.json"))) { EditorUtility.DisplayDialog("Import home models", "No INDEX.json in " + source + ".", "OK"); return; }
                EditorPrefs.SetString(SourceKey, source);
            }
            try { var library = Import(source, (text, f) => EditorUtility.DisplayProgressBar("Importing home models", text, f)); Selection.activeObject = library; }
            finally { EditorUtility.ClearProgressBar(); }
        }

        public static GeoHomeLibrary Import(string source, Action<string, float> progress = null)
        {
            string indexPath = Path.Combine(source, "INDEX.json");
            if (!File.Exists(indexPath)) throw new FileNotFoundException("No INDEX.json in " + source);
            var index = JArray.Parse(File.ReadAllText(indexPath, Encoding.UTF8));
            EnsureFolder(Folder);
            var models = new List<GeoHomeModelAsset>();
            int n = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (JObject entry in index)
                {
                    string id = (string)entry["id"], folder = (string)entry["folder"];
                    progress?.Invoke(id, n++ / (float)Math.Max(1, index.Count));
                    if (!SafeName(id) || !SafeName(folder)) continue;
                    string from = Path.Combine(source, folder, folder);
                    if (!File.Exists(from + ".glb") || !File.Exists(from + "_albedo.png") || !File.Exists(from + "_normal.png") || !File.Exists(from + "_meta.json"))
                    {
                        Debug.LogWarning("[GeoWorld] home model " + id + " skipped: its GLB, textures or meta are missing in " + Path.Combine(source, folder));
                        continue;
                    }
                    models.Add(Model(id, entry, JObject.Parse(File.ReadAllText(from + "_meta.json", Encoding.UTF8)), from));
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh();
            // the assets exist now: connect them (textures need importing before the materials can use them)
            var library = Library;
            if (!library) { library = ScriptableObject.CreateInstance<GeoHomeLibrary>(); AssetDatabase.CreateAsset(library, GeoHomeLibrary.DefaultPath); }
            foreach (var model in models)
            {
                string dir = Folder + "/" + model.id;
                model.material = MaterialFor(model.id, dir);
                EditorUtility.SetDirty(model);
            }
            library.models = models;
            // the package's roof material set; it is only made where the package can be written (while developing it)
            library.roofMaterials = AssetDatabase.LoadAssetAtPath<GeoRoofMaterialSet>(RoofTextures + "/RoofMaterialSet.asset");
            if (!library.roofMaterials) library.roofMaterials = GeoRoofMaterialSetBuilder.CreateIn(RoofTextures);
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            return library;
        }

        // Unity -batchmode -projectPath ... -executeMethod GeoWorld.Editor.GeoHomeImporter.BatchImport : imports the models and
        // reports what the library holds (a quick check of the import without building a city).
        public static void BatchImport()
        {
            try
            {
                string source = DefaultSource;
                if (source == null) throw new FileNotFoundException("intact/models not found near the project");
                var library = Import(source);
                int bad = 0;
                foreach (var m in library.models)
                {
                    bool ok = m && m.body && m.material && m.variants.Length > 0 && Array.TrueForAll(m.variants, v => v);
                    if (!ok) { bad++; Debug.LogError("[GeoHomes] incomplete model " + (m ? m.id : "null")); }
                }
                Debug.Log("[GeoHomes] imported " + library.models.Count + " home models, " + bad + " incomplete, roof materials: " + (library.roofMaterials ? library.roofMaterials.entries.Length : 0));
                EditorApplication.Exit(bad == 0 ? 0 : 1);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }

        static bool SafeName(string s) => !string.IsNullOrEmpty(s) && s.Length <= 90 && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !s.Contains("..");

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // what a model stands for: app categories and OpenStreetMap tags
        static void Roles(string id, string modelCategory, out string[] categories, out string[] tags, out bool tagOnly)
        {
            tags = new string[0]; tagOnly = false;
            switch (id)
            {
                case "N27": categories = new[] { "commercial" }; tags = new[] { "retail", "shop", "kiosk", "supermarket" }; return;
                case "N28": categories = new[] { "public" }; tags = new[] { "school" }; tagOnly = true; return;
                case "N29": categories = new[] { "public" }; tags = new[] { "kindergarten" }; tagOnly = true; return;
                case "N30": categories = new[] { "garage" }; tags = new[] { "garage", "garages", "carport" }; return;
                // a long row of household buildings: the long narrow outlines the app guesses to be houses are mostly these
                case "N34": categories = new[] { "shed", "garage", "house" }; tags = new[] { "shed", "barn", "farm_auxiliary" }; return;
            }
            switch (modelCategory)
            {
                case "apartment": categories = new[] { "apartments" }; break;
                case "commercial": categories = new[] { "commercial" }; break;
                case "public": categories = new[] { "public" }; break;
                case "outbuilding": categories = new[] { "garage", "shed" }; break;
                case "industrial": categories = new[] { "industrial" }; tags = new[] { "industrial", "warehouse", "manufacture", "service" }; break;
                default: categories = new[] { "house" }; break;   // village-house, wooden-house, cottage, attic-house, town-house, semi-detached
            }
        }

        static GeoHomeModelAsset Model(string id, JObject entry, JObject meta, string from)
        {
            string folder = Folder + "/" + id;
            EnsureFolder(folder);
            Copy(from + "_albedo.png", folder + "/" + id + "_albedo.png");
            Copy(from + "_normal.png", folder + "/" + id + "_normal.png");
            var parts = ReadParts(from + ".glb");
            string path = folder + "/" + id + ".asset";
            var model = AssetDatabase.LoadAssetAtPath<GeoHomeModelAsset>(path);
            bool create = !model;
            if (create) model = ScriptableObject.CreateInstance<GeoHomeModelAsset>();
            var roof = (JObject)meta["roof"];
            model.id = id; model.label = (string)entry["name"];
            Roles(id, (string)entry["category"], out model.categories, out model.tags, out model.tagOnly);
            model.floors = Math.Max(1, (int?)entry["floors"] ?? 1);
            model.length = (float?)roof["length"] ?? 10; model.width = (float?)roof["width"] ?? 7;
            model.eaveHeight = (float?)roof["eaveZ"] ?? 2.8f; model.rise = (float?)roof["rise"] ?? 2.2f;
            model.defaultVariant = (string)meta["defaultVariant"] ?? "gabled_along";
            model.defaultRoofMaterial = (string)meta["defaultRoofMaterial"] ?? "metal_tile";
            model.defaultRoofColor = (string)meta["defaultRoofColor"] ?? "#6e4636";
            model.body = Save(parts, "Body", id, folder);
            model.roofFixed = Save(parts, "RoofFixed", id, folder);
            var names = new List<string>(); var meshes = new List<Mesh>();
            foreach (var pair in parts)
                if (pair.Key.StartsWith("Roof_", StringComparison.Ordinal)) { names.Add(pair.Key.Substring(5)); meshes.Add(Save(parts, pair.Key, id, folder)); }
            model.variantNames = names.ToArray(); model.variants = meshes.ToArray();
            if (create) AssetDatabase.CreateAsset(model, path); else EditorUtility.SetDirty(model);
            return model;
        }

        static Mesh Save(Dictionary<string, Mesh> parts, string part, string id, string folder)
        {
            if (!parts.TryGetValue(part, out var mesh)) return null;
            string path = folder + "/" + id + "_" + part + ".asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path)) AssetDatabase.DeleteAsset(path);
            mesh.name = id + "_" + part;
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;   // inside an asset-editing batch the asset cannot be loaded back yet
        }

        static void Copy(string from, string to)
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", to));
            if (File.Exists(full) && new FileInfo(full).Length == new FileInfo(from).Length) return;
            File.Copy(from, full, true);
            AssetDatabase.ImportAsset(to, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(to) as TextureImporter;
            if (importer == null) return;
            bool normal = to.EndsWith("_normal.png", StringComparison.Ordinal);
            importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = !normal;
            importer.mipmapEnabled = true; importer.anisoLevel = 4; importer.maxTextureSize = 4096;
            importer.SaveAndReimport();
        }

        static Material MaterialFor(string id, string folder)
        {
            string path = folder + "/" + id + ".mat";
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = !material;
            if (create) material = new Material(shader) { name = id };
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + id + "_albedo.png");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + id + "_normal.png");
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", albedo); else material.mainTexture = albedo;
            if (normal && material.HasProperty("_BumpMap")) { material.SetTexture("_BumpMap", normal); material.EnableKeyword("_NORMALMAP"); }
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.12f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            if (create) AssetDatabase.CreateAsset(material, path); else EditorUtility.SetDirty(material);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        // ---------------------------------------------------------------- roof tint

        // One material per roof kind and colour, saved as an asset so merged chunks and scenes keep it.
        static Material TintedRoof(Material source, Color color)
        {
            string key = AssetDatabase.GetAssetPath(source) + "#" + ColorUtility.ToHtmlStringRGB(color);
            if (tinted.TryGetValue(key, out var found) && found) return found;
            string dir = Folder + "/Roofs";
            EnsureFolder(dir);
            string path = dir + "/" + source.name + "_" + ColorUtility.ToHtmlStringRGB(color) + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!material)
            {
                material = new Material(source) { name = source.name + "_" + ColorUtility.ToHtmlStringRGB(color) };
                // the roof texture is a neutral grey of about 0.8: lift the tint so the result matches the colour asked for
                var tint = new Color(Mathf.Clamp01(color.r * 1.2f), Mathf.Clamp01(color.g * 1.2f), Mathf.Clamp01(color.b * 1.2f), 1f);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
                if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
                AssetDatabase.CreateAsset(material, path);
            }
            tinted[key] = material;
            return material;
        }

        // ---------------------------------------------------------------- GLB parts

        // The meshes of a binary glTF by node name, in Unity's axes (X mirrored, triangles turned, V up). Each mesh has two
        // sub-meshes: 0 for primitives of the "_body" material (baked atlas), 1 for the rest (the roof). Node transforms are
        // not applied: the home pipeline writes every node at the origin. Tangents are computed.
        public static Dictionary<string, Mesh> ReadParts(string path)
        {
            var bytes = File.ReadAllBytes(path);
            string file = Path.GetFileName(path);
            if (bytes.Length < 28 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67u) throw new InvalidDataException(file + " is not a binary glTF (GLB) file.");
            int jsonLength = (int)BitConverter.ToUInt32(bytes, 12);
            if (BitConverter.ToUInt32(bytes, 16) != 0x4E4F534Au || 20 + jsonLength > bytes.Length) throw new InvalidDataException(file + ": the first chunk is not JSON.");
            var gltf = JObject.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength));
            int chunk = 20 + jsonLength;
            if (chunk + 8 > bytes.Length || BitConverter.ToUInt32(bytes, chunk + 4) != 0x004E4942u) throw new InvalidDataException(file + " has no binary chunk.");
            int binary = chunk + 8, binaryLength = (int)BitConverter.ToUInt32(bytes, chunk);
            var accessors = (JArray)gltf["accessors"]; var views = (JArray)gltf["bufferViews"];
            var meshes = gltf["meshes"] as JArray; var nodes = gltf["nodes"] as JArray; var materials = gltf["materials"] as JArray;
            if (accessors == null || views == null || meshes == null || nodes == null) throw new InvalidDataException(file + " has no mesh nodes.");

            int Start(JObject accessor, out int stride, int elementSize)
            {
                var view = (JObject)views[(int)accessor["bufferView"]];
                stride = (int?)view["byteStride"] ?? 0;
                if (stride == 0) stride = elementSize;
                int start = binary + ((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
                int count = (int)accessor["count"];
                if (start < binary || count < 0 || start + (long)stride * Math.Max(0, count - 1) + elementSize > binary + binaryLength)
                    throw new InvalidDataException(file + ": an accessor reaches beyond the binary chunk.");
                return start;
            }
            float[] Floats(JToken reference, int components)
            {
                var accessor = (JObject)accessors[(int)reference];
                if ((int)accessor["componentType"] != 5126) throw new InvalidDataException(file + ": vertex data must be 32-bit floats.");
                int count = (int)accessor["count"], start = Start(accessor, out int stride, components * 4);
                var result = new float[count * components];
                for (int i = 0; i < count; i++)
                    for (int k = 0; k < components; k++) result[i * components + k] = BitConverter.ToSingle(bytes, start + i * stride + k * 4);
                return result;
            }
            int[] Indices(JToken reference)
            {
                var accessor = (JObject)accessors[(int)reference];
                int type = (int)accessor["componentType"], size = type == 5121 ? 1 : type == 5123 ? 2 : type == 5125 ? 4 : 0;
                if (size == 0) throw new InvalidDataException(file + ": unsupported index type " + type + ".");
                int count = (int)accessor["count"], start = Start(accessor, out int stride, size);
                var result = new int[count];
                for (int i = 0; i < count; i++)
                {
                    int at = start + i * stride;
                    result[i] = size == 1 ? bytes[at] : size == 2 ? BitConverter.ToUInt16(bytes, at) : (int)BitConverter.ToUInt32(bytes, at);
                }
                return result;
            }

            var result_ = new Dictionary<string, Mesh>();
            foreach (JObject node in nodes)
            {
                if (node["mesh"] == null) continue;
                string name = StripSuffix((string)node["name"] ?? "");
                if (name.Length == 0) continue;
                var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
                var sub = new List<int>[] { new List<int>(), new List<int>() };
                foreach (var primitiveToken in (JArray)meshes[(int)node["mesh"]]["primitives"] ?? new JArray())
                {
                    var primitive = (JObject)primitiveToken;
                    if (((int?)primitive["mode"] ?? 4) != 4) continue;
                    var attributes = (JObject)primitive["attributes"];
                    if (attributes?["POSITION"] == null) continue;
                    string material = primitive["material"] != null && materials != null ? (string)materials[(int)primitive["material"]]["name"] ?? "" : "";
                    var list = sub[material.EndsWith("_body", StringComparison.Ordinal) ? 0 : 1];
                    var p = Floats(attributes["POSITION"], 3);
                    var nm = attributes["NORMAL"] != null ? Floats(attributes["NORMAL"], 3) : null;
                    var t = attributes["TEXCOORD_0"] != null ? Floats(attributes["TEXCOORD_0"], 2) : null;
                    int first = positions.Count, count = p.Length / 3;
                    for (int i = 0; i < count; i++)
                    {
                        positions.Add(new Vector3(-p[i * 3], p[i * 3 + 1], p[i * 3 + 2]));
                        normals.Add(nm != null && nm.Length >= (i + 1) * 3 ? new Vector3(-nm[i * 3], nm[i * 3 + 1], nm[i * 3 + 2]) : Vector3.up);
                        uvs.Add(t != null && t.Length >= (i + 1) * 2 ? new Vector2(t[i * 2], 1 - t[i * 2 + 1]) : Vector2.zero);
                    }
                    int[] indices;
                    if (primitive["indices"] != null) indices = Indices(primitive["indices"]);
                    else { indices = new int[count]; for (int i = 0; i < count; i++) indices[i] = i; }
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        if (indices[i] >= count || indices[i + 1] >= count || indices[i + 2] >= count) throw new InvalidDataException(file + ": an index points past the vertices.");
                        list.Add(first + indices[i]); list.Add(first + indices[i + 2]); list.Add(first + indices[i + 1]);
                    }
                }
                if (sub[0].Count + sub[1].Count == 0) continue;
                var mesh = new Mesh { name = name, indexFormat = positions.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                mesh.SetVertices(positions); mesh.SetNormals(normals); mesh.SetUVs(0, uvs);
                mesh.subMeshCount = 2;
                mesh.SetTriangles(sub[0], 0); mesh.SetTriangles(sub[1], 1);
                mesh.RecalculateTangents(); mesh.RecalculateBounds();
                result_[name] = mesh;
            }
            return result_;
        }

        // "Body.001" -> "Body"
        static string StripSuffix(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1 && int.TryParse(name.Substring(dot + 1), out _) ? name.Substring(0, dot) : name;
        }
    }
}
