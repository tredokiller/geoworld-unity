using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // The destroyed-building models of the ruins pipeline (ruins/models next to this Unity project: one folder per model
    // with a GLB, its baked albedo and normal map, and INDEX.json listing them) brought into the project as
    // Assets/GeoWorld/Ruins/<ID>/: the mesh read from the GLB in Unity's axes, the two textures, a URP material, a prefab
    // for placing it by hand, and a Geo Ruin Model asset that says how the model is fitted to real footprints; plus the
    // Ruin library the generator chooses from. The GLB's mesh is used rather than the FBX: the FBX carries both
    // textures inside it again, which Unity does not use.
    // Importing again refreshes meshes and textures; fitting values edited on a model asset are kept (its context menu
    // resets them).
    public static class GeoRuinImporter
    {
        // the library ships in the package (imported here while the package is developed)
        public const string Folder = GeoPaths.Package + "/Ruins";

        // ruins/models of the Building Extractor repository: the folder chosen on this computer (remembered for every
        // project that uses the package), else the one beside this Unity project
        public const string SourceKey = "GeoWorld.RuinModels";
        public static string DefaultSource
        {
            get
            {
                string chosen = EditorPrefs.GetString(SourceKey, "");
                if (!string.IsNullOrEmpty(chosen) && File.Exists(Path.Combine(chosen, "INDEX.json"))) return chosen;
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "ruins", "models"));
            }
        }

        [MenuItem("Tools/Geo World/Import ruin models")]
        public static void ImportMenu()
        {
            if (GeoDefaults.RefuseImportHere("ruin models")) return;
            string source = DefaultSource;
            if (!File.Exists(Path.Combine(source, "INDEX.json")))
            {
                source = EditorUtility.OpenFolderPanel("ruins/models of the Building Extractor repository (INDEX.json and a folder per model)", "", "");
                if (string.IsNullOrEmpty(source)) return;
                if (!File.Exists(Path.Combine(source, "INDEX.json"))) { EditorUtility.DisplayDialog("Import ruin models", "No INDEX.json in " + source + ".", "OK"); return; }
                EditorPrefs.SetString(SourceKey, source);
            }
            try
            {
                var library = Import(source, (label, f) => EditorUtility.DisplayProgressBar("Importing ruin models", label, f));
                Selection.activeObject = library;
                Debug.Log("[GeoWorld] " + library.models.Count + " ruin models imported from " + source + " into " + Folder, library);
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // The library of this project, or null before the models were imported.
        public static GeoRuinLibrary Library => AssetDatabase.LoadAssetAtPath<GeoRuinLibrary>(GeoRuinLibrary.DefaultPath);

        // resetFitting writes the default fitting values into existing model assets too (edits are lost).
        public static GeoRuinLibrary Import(string source, Action<string, float> progress = null, bool resetFitting = false)
        {
            string indexPath = Path.Combine(source, "INDEX.json");
            if (!File.Exists(indexPath)) throw new FileNotFoundException("INDEX.json is missing in " + source, indexPath);
            var index = JArray.Parse(File.ReadAllText(indexPath));
            Directory.CreateDirectory(Folder);
            var entries = new List<JObject>();
            var textures = new List<string>();
            // first the textures, so that one refresh imports them all
            for (int i = 0; i < index.Count; i++)
            {
                if (!(index[i] is JObject entry)) continue;
                string id = (string)entry["id"], folder = (string)entry["folder"], name = (string)entry["name"];
                if (!SafeName(id) || string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name)) continue;
                string from = Path.Combine(source, folder, name);
                if (!File.Exists(from + ".glb") || !File.Exists(from + "_albedo.png") || !File.Exists(from + "_normal.png"))
                {
                    Debug.LogWarning("[GeoWorld] ruin model " + id + " skipped: its GLB or textures are missing in " + Path.Combine(source, folder));
                    continue;
                }
                progress?.Invoke("Copying " + id, 0.3f * i / index.Count);
                Directory.CreateDirectory(Folder + "/" + id);
                string albedo = Folder + "/" + id + "/" + GeoTextureFiles.AlbedoName(id), normal = Folder + "/" + id + "/" + GeoTextureFiles.NormalName(id);
                if (GeoTextureFiles.Stale(from + "_albedo.png", albedo)) GeoTextureFiles.Write(from + "_albedo.png", albedo, false);
                if (GeoTextureFiles.Stale(from + "_normal.png", normal)) GeoTextureFiles.Write(from + "_normal.png", normal, true);
                textures.Add(albedo); textures.Add(normal);
                entries.Add(entry);
            }
            AssetDatabase.Refresh();
            foreach (var path in textures) GeoTextureFiles.Configure(path, true);

            var models = new List<GeoRuinModel>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                string id = (string)entry["id"];
                progress?.Invoke("Building " + id, 0.3f + 0.65f * i / entries.Count);
                string from = Path.Combine(source, (string)entry["folder"], (string)entry["name"]);
                models.Add(Model(id, entry, Roster(source, (string)entry["folder"]), ReadGlb(from + ".glb"), resetFitting));
            }

            var library = Library;
            if (!library)
            {
                library = ScriptableObject.CreateInstance<GeoRuinLibrary>();
                AssetDatabase.CreateAsset(library, GeoRuinLibrary.DefaultPath);
            }
            // every model asset in the folder, also ones made by hand
            foreach (var guid in AssetDatabase.FindAssets("t:GeoRuinModel", new[] { Folder }))
            {
                var model = AssetDatabase.LoadAssetAtPath<GeoRuinModel>(AssetDatabase.GUIDToAssetPath(guid));
                if (model && !models.Contains(model)) models.Add(model);
            }
            models.Sort((a, b) => string.CompareOrdinal(a.id ?? a.name, b.id ?? b.name));
            library.models = models;
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            GeoRuinFit.ClearCache();
            return library;
        }

        // The model's design spec (ruins/roster/<folder>.json, beside the models), or null.
        static JObject Roster(string source, string folder)
        {
            string path = Path.Combine(source, "..", "roster", folder + ".json");
            try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
            catch (Exception e) when (e is IOException || e is Newtonsoft.Json.JsonException) { return null; }
        }

        static bool SafeName(string id) => !string.IsNullOrEmpty(id) && id.Length <= 40 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains("..");

        // The model asset of one ruin: mesh (a sub-asset), material and prefab next to it. Fitting values are written
        // only when the asset is new.
        static GeoRuinModel Model(string id, JObject entry, JObject roster, Mesh mesh, bool resetFitting)
        {
            string folder = Folder + "/" + id, path = folder + "/" + id + ".asset";
            var model = AssetDatabase.LoadAssetAtPath<GeoRuinModel>(path);
            bool created = !model;
            if (created)
            {
                model = ScriptableObject.CreateInstance<GeoRuinModel>();
                model.name = id;
                AssetDatabase.CreateAsset(model, path);
            }
            mesh.name = id + " mesh";
            if (model.mesh && AssetDatabase.GetAssetPath(model.mesh) == path)
            {
                // the same mesh object, so everything that references it sees the new data
                var existing = model.mesh;
                existing.Clear();
                existing.indexFormat = mesh.indexFormat;
                existing.SetVertices(mesh.vertices); existing.SetNormals(mesh.normals); existing.SetTangents(mesh.tangents);
                existing.SetUVs(0, mesh.uv); existing.SetTriangles(mesh.triangles, 0);
                existing.RecalculateBounds();
                Object.DestroyImmediate(mesh);
                EditorUtility.SetDirty(existing);
            }
            else
            {
                AssetDatabase.AddObjectToAsset(mesh, model);
                model.mesh = mesh;
            }
            model.id = id;
            model.title = (string)entry["title"] ?? id;
            model.material = MaterialFor(id, folder);
            if (created || resetFitting) GeoRuinDefaults.Apply(model, entry, roster);
            EditorUtility.SetDirty(model);
            AssetDatabase.SaveAssets();
            Prefab(model, folder);
            return model;
        }

        static Material MaterialFor(string id, string folder)
        {
            string path = folder + "/" + id + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + GeoTextureFiles.AlbedoName(id));
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + id + "_normal.png");
            if (material)
            {
                if (material.HasProperty("_BaseMap") && !material.GetTexture("_BaseMap")) material.SetTexture("_BaseMap", albedo);
                if (material.HasProperty("_BumpMap") && !material.GetTexture("_BumpMap")) { material.SetTexture("_BumpMap", normal); material.EnableKeyword("_NORMALMAP"); }
                return material;
            }
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            material = new Material(shader) { name = id, enableInstancing = true };
            material.mainTexture = albedo;
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", albedo);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            material.color = Color.white;
            if (material.HasProperty("_BumpMap")) { material.SetTexture("_BumpMap", normal); material.SetFloat("_BumpScale", 1f); material.EnableKeyword("_NORMALMAP"); }
            // the bake is rough plaster, brick, concrete and soot (roughness 0.92), nothing metallic
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.08f);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.08f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
            // the models are made double-sided (thin sheets, shards, hanging slabs) and a cut piece shows its inside
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)CullMode.Off);
            material.doubleSidedGI = true;
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static void Prefab(GeoRuinModel model, string folder)
        {
            string path = folder + "/" + model.id + ".prefab";
            if (File.Exists(path)) return;
            var go = new GameObject(model.id);
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = model.mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = model.material;
                PrefabUtility.SaveAsPrefabAsset(go, path);
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ------------------------------------------------------------------ GLB

        // The mesh of a binary glTF file, in Unity's axes: glTF is right-handed, so X is mirrored, triangles are turned
        // round and texture V runs up. Every triangle primitive of every mesh goes into one mesh (node transforms are
        // not applied: the ruin pipeline writes one mesh at the origin). Tangents are computed for the normal map.
        public static Mesh ReadGlb(string path)
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
            if (accessors == null || views == null || !(gltf["meshes"] is JArray meshes)) throw new InvalidDataException(file + " has no mesh.");

            int Start(JObject accessor, out int stride, int elementSize)
            {
                var view = (JObject)views[(int)accessor["bufferView"]];
                if (((int?)view["buffer"] ?? 0) != 0) throw new InvalidDataException(file + " refers to an external buffer.");
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
                    for (int c = 0; c < components; c++) result[i * components + c] = BitConverter.ToSingle(bytes, start + i * stride + c * 4);
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

            var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>(); var triangles = new List<int>();
            foreach (var meshToken in meshes)
                foreach (var primitiveToken in (JArray)meshToken["primitives"] ?? new JArray())
                {
                    var primitive = (JObject)primitiveToken;
                    if (((int?)primitive["mode"] ?? 4) != 4) continue; // triangles only
                    var attributes = (JObject)primitive["attributes"];
                    if (attributes?["POSITION"] == null) continue;
                    var p = Floats(attributes["POSITION"], 3);
                    var n = attributes["NORMAL"] != null ? Floats(attributes["NORMAL"], 3) : null;
                    var t = attributes["TEXCOORD_0"] != null ? Floats(attributes["TEXCOORD_0"], 2) : null;
                    int first = positions.Count, count = p.Length / 3;
                    for (int i = 0; i < count; i++)
                    {
                        positions.Add(new Vector3(-p[i * 3], p[i * 3 + 1], p[i * 3 + 2]));
                        normals.Add(n != null && n.Length >= (i + 1) * 3 ? new Vector3(-n[i * 3], n[i * 3 + 1], n[i * 3 + 2]) : Vector3.up);
                        uvs.Add(t != null && t.Length >= (i + 1) * 2 ? new Vector2(t[i * 2], 1 - t[i * 2 + 1]) : Vector2.zero);
                    }
                    int[] indices;
                    if (primitive["indices"] != null) indices = Indices(primitive["indices"]);
                    else { indices = new int[count]; for (int i = 0; i < count; i++) indices[i] = i; }
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        if (indices[i] >= count || indices[i + 1] >= count || indices[i + 2] >= count) throw new InvalidDataException(file + ": an index points past the vertices.");
                        triangles.Add(first + indices[i]); triangles.Add(first + indices[i + 2]); triangles.Add(first + indices[i + 1]);
                    }
                }
            if (triangles.Count == 0) throw new InvalidDataException(file + " has no triangles.");
            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path), indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        [MenuItem("CONTEXT/GeoRuinModel/Reset fitting values to the importer's defaults")]
        static void ResetDefaults(MenuCommand command)
        {
            var model = (GeoRuinModel)command.context;
            Undo.RecordObject(model, "Reset ruin model");
            JObject entry = null;
            string indexPath = Path.Combine(DefaultSource, "INDEX.json");
            if (File.Exists(indexPath))
                foreach (var token in JArray.Parse(File.ReadAllText(indexPath)))
                    if (token is JObject e && (string)e["id"] == model.id) entry = e;
            GeoRuinDefaults.Apply(model, entry ?? new JObject { ["id"] = model.id }, entry != null ? Roster(DefaultSource, (string)entry["folder"]) : null);
            EditorUtility.SetDirty(model);
        }
    }
}
