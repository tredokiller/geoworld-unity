using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Makes and keeps the reduced copies of the library models (GeoReducedModels). A home has about a thousand triangles
    // and a ruin about three thousand; a merged city copies one for every building, so they are most of a city's mesh
    // data. GeoMeshSimplifier collapses edges onto the mesh's own vertices, keeping outlines, hard edges, texture seams
    // and sub-meshes, down to Ratio of the triangles. (Unity's own mesh LOD generator crashes the editor on some of these
    // models, in MeshLod::BuildSlidingIndexDataFromClusterData, so it is not used.) Keeping only source vertices matters
    // to the fitting: a ruin is cut at the source's joints, and a home's foot (y = 0) is pulled down to the foundation,
    // so the reduced copy has exactly the same corners, eaves and foot as the source.
    public static class GeoModelReduction
    {
        public const string AssetPath = GeoPaths.Project + "/Models/Reduced models.asset";
        public const float Ratio = 0.5f;

#if GEOWORLD_DEVELOPMENT // only where the package is developed (its project defines this)
        [MenuItem("Tools/Geo World/Developer/Reduce library models again")]
#endif
        static void ReduceAgainMenu()
        {
            var set = AssetDatabase.LoadAssetAtPath<GeoReducedModels>(AssetPath);
            if (set) { foreach (var entry in set.entries) if (entry.reduced) AssetDatabase.RemoveObjectFromAsset(entry.reduced); set.entries.Clear(); EditorUtility.SetDirty(set); }
            var result = Ensure(GeoRuinImporter.Library, GeoHomeImporter.Library);
            Debug.Log(Summary(result), result);
        }

        public static string Summary(GeoReducedModels set)
        {
            long before = 0, after = 0;
            foreach (var e in set.entries) if (e.source && e.reduced) { before += Triangles(e.source); after += Triangles(e.reduced); }
            return $"{set.entries.Count} library meshes reduced: {before} triangles to {after} ({(before > 0 ? 100f * after / before : 0):F0}%).";
        }

        // The reduced set for these libraries, made for every source mesh that is new or changed since it was reduced.
        public static GeoReducedModels Ensure(GeoRuinLibrary ruins, GeoHomeLibrary homes)
        {
            var sources = new List<Mesh>();
            if (ruins) foreach (var model in ruins.models) if (model && model.mesh) sources.Add(model.mesh);
            if (homes)
                foreach (var model in homes.models)
                {
                    if (!model) continue;
                    if (model.body) sources.Add(model.body);
                    if (model.roofFixed) sources.Add(model.roofFixed);
                    if (model.variants != null) foreach (var variant in model.variants) if (variant) sources.Add(variant);
                }
            var set = AssetDatabase.LoadAssetAtPath<GeoReducedModels>(AssetPath);
            if (!set)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath)); AssetDatabase.Refresh();
                set = ScriptableObject.CreateInstance<GeoReducedModels>();
                AssetDatabase.CreateAsset(set, AssetPath);
            }
            var known = new Dictionary<Mesh, int>();
            for (int i = 0; i < set.entries.Count; i++) if (set.entries[i].source) known[set.entries[i].source] = i;
            bool changed = false;
            foreach (var source in sources.Distinct())
            {
                string fingerprint = Fingerprint(source);
                if (known.TryGetValue(source, out int at) && set.entries[at].fingerprint == fingerprint && set.entries[at].reduced) continue;
                var reduced = Reduce(source, Ratio);
                if (!reduced) continue; // nothing to gain: the full model is drawn
                reduced.name = source.name + " (reduced)";
                AssetDatabase.AddObjectToAsset(reduced, set);
                var entry = new GeoReducedModels.Entry { source = source, reduced = reduced, fingerprint = fingerprint };
                if (known.TryGetValue(source, out at))
                {
                    if (set.entries[at].reduced) { AssetDatabase.RemoveObjectFromAsset(set.entries[at].reduced); Object.DestroyImmediate(set.entries[at].reduced, true); }
                    set.entries[at] = entry;
                }
                else { known[source] = set.entries.Count; set.entries.Add(entry); }
                changed = true;
            }
            // entries whose source model is gone
            for (int i = set.entries.Count - 1; i >= 0; i--)
                if (!set.entries[i].source)
                {
                    if (set.entries[i].reduced) { AssetDatabase.RemoveObjectFromAsset(set.entries[i].reduced); Object.DestroyImmediate(set.entries[i].reduced, true); }
                    set.entries.RemoveAt(i); changed = true;
                }
            if (changed) { EditorUtility.SetDirty(set); AssetDatabase.SaveAssets(); }
            return set;
        }

        static string Fingerprint(Mesh mesh)
        {
            long indices = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) indices += (long)mesh.GetIndexCount(s);
            var b = mesh.bounds;
            return mesh.vertexCount + "/" + mesh.subMeshCount + "/" + indices + "/" + b.center.ToString("F3") + b.size.ToString("F3");
        }

        public static long Triangles(Mesh mesh)
        {
            long count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) count += (long)mesh.GetIndexCount(s) / 3;
            return count;
        }

        // A copy of the mesh with at most `ratio` of its triangles, on a subset of the source's vertices, moving the
        // surface by at most a hundredth of the model's size (10 cm on a 10 m house); null when nothing could go.
        public static Mesh Reduce(Mesh source, float ratio)
        {
            var reduced = GeoMeshSimplifier.Simplify(source, ratio, 0.01f * source.bounds.size.magnitude);
            if (reduced && Triangles(reduced) >= Triangles(source)) { Object.DestroyImmediate(reduced); return null; }
            return reduced;
        }
    }
}
