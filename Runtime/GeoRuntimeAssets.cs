using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    // What a player needs to generate and to draw worlds without the editor: the package's library (home and ruin models,
    // the reduced models, tree prefabs, road and ground pictures, building styles), the shaders, and template materials
    // that keep the shader variants of the generated materials in a build. A world package refers to these by key (the
    // asset's GUID and local file id, the same in the editor and in the player) instead of carrying them.
    // Made in the editor: Tools > Geo World > Runtime generation > Prepare runtime assets (GeoRuntimeSetup), into
    // Assets/GeoWorld/Resources/GeoWorldRuntimeAssets.asset, which a player finds in Resources (or set GeoAssets.Registry).
    [PreferBinarySerialization]
    public class GeoRuntimeAssets : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string key;  // "<guid>:<local file id>"
            public string path; // the asset path it was found at in the editor
            public Object asset;
        }

        [Tooltip("Version of the Geo World package these assets were gathered with.")]
        public string packageVersion;
        public List<Entry> entries = new List<Entry>();
        public List<Shader> shaders = new List<Shader>();
        [Tooltip("Copies of the materials a generation makes (one per shader and keyword set): a build keeps the shader variants of the materials it holds, and strips the rest.")]
        public List<Material> templates = new List<Material>();
        public GeoHomeLibrary homeLibrary;
        public GeoRuinLibrary ruinLibrary;
        public GeoReducedModels reducedModels;

        [NonSerialized] Dictionary<string, Object> byKey;
        [NonSerialized] Dictionary<Object, string> keyOf;
        [NonSerialized] Dictionary<string, List<Object>> byPath;
        [NonSerialized] Dictionary<string, Shader> shaderByName;

        void OnEnable() => byKey = null;

        void Index()
        {
            if (byKey != null) return;
            byKey = new Dictionary<string, Object>(entries.Count);
            keyOf = new Dictionary<Object, string>(entries.Count);
            byPath = new Dictionary<string, List<Object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                if (!e.asset || string.IsNullOrEmpty(e.key)) continue;
                byKey[e.key] = e.asset;
                if (!keyOf.ContainsKey(e.asset)) keyOf[e.asset] = e.key;
                if (string.IsNullOrEmpty(e.path)) continue;
                if (!byPath.TryGetValue(e.path, out var list)) byPath[e.path] = list = new List<Object>();
                list.Add(e.asset);
            }
            shaderByName = new Dictionary<string, Shader>();
            foreach (var s in shaders) if (s && !shaderByName.ContainsKey(s.name)) shaderByName[s.name] = s;
            foreach (var m in templates) if (m && m.shader && !shaderByName.ContainsKey(m.shader.name)) shaderByName[m.shader.name] = m.shader;
        }

        public Object Find(string key)
        {
            Index();
            return key != null && byKey.TryGetValue(key, out var found) ? found : null;
        }

        public string KeyOf(Object asset)
        {
            Index();
            return asset && keyOf.TryGetValue(asset, out var key) ? key : null;
        }

        // The first object of type T at an asset path (the main asset is listed first).
        public T Load<T>(string path) where T : Object
        {
            Index();
            if (path == null || !byPath.TryGetValue(path, out var list)) return null;
            foreach (var o in list) if (o is T t) return t;
            // a prefab is listed by its GameObject; a component asked for comes from it
            if (typeof(Component).IsAssignableFrom(typeof(T)))
                foreach (var o in list) if (o is GameObject go && go.GetComponent(typeof(T)) is T c) return c;
            return null;
        }

        public bool Has(string path)
        {
            Index();
            return path != null && byPath.ContainsKey(path);
        }

        public Shader Shader(string name)
        {
            Index();
            return name != null && shaderByName.TryGetValue(name, out var s) ? s : null;
        }

        public int Count => entries.Count;
    }

    // Assets by their path (as the editor's AssetDatabase has them) or by key, in the editor and in a player alike: in the
    // editor through the AssetDatabase, in a player from the registry (GeoRuntimeAssets).
    public static class GeoAssets
    {
        public const string ResourceName = "GeoWorldRuntimeAssets";
        static GeoRuntimeAssets registry;
        static bool looked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() { if (!explicitRegistry) { registry = null; looked = false; } }
        static bool explicitRegistry;

        // The registry a player uses: Resources/GeoWorldRuntimeAssets unless the game sets one.
        public static GeoRuntimeAssets Registry
        {
            get
            {
                if (!registry && !looked) { looked = true; registry = Resources.Load<GeoRuntimeAssets>(ResourceName); }
                return registry;
            }
            set { registry = value; looked = true; explicitRegistry = value; }
        }

        // The editor reads assets through the AssetDatabase unless this is on (tests of what a player sees).
        public static bool ForceRegistry;
        public static bool UseRegistry => !Application.isEditor || ForceRegistry;

        public static T Load<T>(string path) where T : Object
        {
#if UNITY_EDITOR
            if (!UseRegistry) return UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
#endif
            var r = Registry;
            return r ? r.Load<T>(path) : null;
        }

        public static bool Exists(string path)
        {
            if (!UseRegistry) return System.IO.File.Exists(GeoPaths.Disk(path));
            var r = Registry;
            return r && r.Has(path);
        }

        // A text file of the package (a version stamp).
        public static string ReadText(string path)
        {
            if (!UseRegistry) return System.IO.File.ReadAllText(GeoPaths.Disk(path));
            var text = Load<TextAsset>(path);
            return text ? text.text : null;
        }

        // A shader a build keeps (the registry's, else Shader.Find): Shader.Find alone finds a shader in a player only when
        // something in the build uses it.
        public static Shader Shader(string name)
        {
            var r = UseRegistry ? Registry : null;
            var found = r ? r.Shader(name) : null;
            return found ? found : UnityEngine.Shader.Find(name);
        }

        public static GeoReducedModels ReducedModels => Registry ? Registry.reducedModels : null;
        public static GeoHomeLibrary HomeLibrary => Registry ? Registry.homeLibrary : null;
        public static GeoRuinLibrary RuinLibrary => Registry ? Registry.ruinLibrary : null;

        // The key of a library asset (null for an object made by a generation, which a package carries itself).
        public static string Key(Object asset)
        {
            if (!asset) return null;
#if UNITY_EDITOR
            if (!UseRegistry)
            {
                if (!UnityEditor.EditorUtility.IsPersistent(asset)) return null;
                return UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id) ? guid + ":" + id : null;
            }
#endif
            var r = Registry;
            return r ? r.KeyOf(asset) : null;
        }

        public static Object Resolve(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
#if UNITY_EDITOR
            if (!UseRegistry)
            {
                int colon = key.IndexOf(':');
                if (colon < 0 || !long.TryParse(key.Substring(colon + 1), out long id)) return null;
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(key.Substring(0, colon));
                if (string.IsNullOrEmpty(path)) return null;
                foreach (var o in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path))
                    if (o && UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out _, out long local) && local == id) return o;
                return null;
            }
#endif
            var r = Registry;
            return r ? r.Find(key) : null;
        }
    }
}
