using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // Simplified copies of the library models (ruins and homes): about half the triangles of each source mesh, from
    // GeoMeshSimplifier (Editor), which keeps the source's own vertices, its outlines, texture seams and
    // sub-meshes. Made once by GeoModelReduction (Tools > Geo World) and kept here as sub-assets.
    public class GeoReducedModels : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public Mesh source, reduced;
            [Tooltip("The source mesh as it was reduced (vertex and index counts, bounds): a changed source is reduced again.")]
            public string fingerprint;
        }

        public List<Entry> entries = new List<Entry>();
    }

    // Which version of a library model the buildings being drawn now use. A merged city (thousands of buildings seen
    // from the street or further) bakes its chunks with the reduced models; an editable building always shows the full
    // model. Nothing is in use outside a bake.
    public static class GeoModelDetail
    {
        static Dictionary<Mesh, Mesh> reduced;

        // models: the reduced set to draw with; null: the full models.
        public static void Use(GeoReducedModels models)
        {
            if (!models) { reduced = null; return; }
            reduced = new Dictionary<Mesh, Mesh>();
            foreach (var entry in models.entries) if (entry.source && entry.reduced) reduced[entry.source] = entry.reduced;
        }

        public static bool Reduced => reduced != null;

        // The mesh to draw for a source mesh of the library.
        public static Mesh For(Mesh source) => source && reduced != null && reduced.TryGetValue(source, out var mesh) ? mesh : source;

        // What the set saves (the generator's report), the same in the editor and in a player.
        public static string Summary(GeoReducedModels set)
        {
            long before = 0, after = 0;
            foreach (var e in set.entries) if (e.source && e.reduced) { before += Triangles(e.source); after += Triangles(e.reduced); }
            return $"{set.entries.Count} library meshes reduced: {before} triangles to {after} ({(before > 0 ? 100f * after / before : 0):F0}%).";
        }

        public static long Triangles(Mesh mesh)
        {
            long count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) count += (long)mesh.GetIndexCount(s) / 3;
            return count;
        }
    }
}
