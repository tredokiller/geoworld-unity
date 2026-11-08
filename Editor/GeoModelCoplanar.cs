using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Coplanar faces of the library models, separated in the assets (GeoCoplanarFaces does the work): a window frame or a
    // patch flush with its wall, a slab laid twice, a home's gable end in the plane of the body's wall. The kits had about
    // 12 000 such pairs. The importers run it on every model they import; fitted buildings get the same at generation, where
    // parts meet and squashing brings faces together.
    public static class GeoModelCoplanar
    {
        public const float Gap = GeoCoplanarFaces.Gap;

#if GEOWORLD_DEVELOPMENT // only where the package is developed (its project defines this)
        [MenuItem("Tools/Geo World/Developer/Separate coplanar faces of library models")]
#endif
        static void SeparateMenu() => Debug.Log(SeparateLibraries(GeoRuinImporter.Library, GeoHomeImporter.Library));

        // Every ruin and home of the libraries as it is drawn (a home: its body and fixed roofs with each roof variant, the
        // body never moved for a variant); the meshes are saved. Returns a report, a line per model that changed.
        public static string SeparateLibraries(GeoRuinLibrary ruins, GeoHomeLibrary homes)
        {
            var report = new StringBuilder();
            int models = 0, patches = 0, faces = 0;
            if (ruins)
                foreach (var model in ruins.models)
                {
                    if (!model || !model.mesh) continue;
                    var r = Repeat(() => Separate(new[] { model.mesh }, new[] { true }, new[] { GeoCoplanarFaces.TwoSided(model.material) }));
                    Note(report, "ruin " + model.id, r, ref models, ref patches, ref faces);
                }
            if (homes)
                foreach (var model in homes.models)
                {
                    if (!model) continue;
                    var r = Repeat(() => SeparateHome(model));
                    Note(report, "home " + model.id, r, ref models, ref patches, ref faces);
                }
            AssetDatabase.SaveAssets();
            report.Insert(0, $"Coplanar faces: {models} models changed, {patches} patches moved {Gap * 100:0.#} cm forward, {faces} repeated faces removed.\n");
            return report.ToString();
        }

        // Three patches in one plane: the two smaller both move in front of the largest, and still fight; the next pass
        // moves the smaller of them again.
        public static (int moved, int removed) Repeat(System.Func<(int moved, int removed)> pass)
        {
            (int moved, int removed) total = (0, 0);
            for (int i = 0; i < 4; i++)
            {
                var r = pass();
                total.moved += r.moved; total.removed += r.removed;
                if (r.moved == 0 && r.removed == 0) break;
            }
            return total;
        }

        // A home's body with its fixed roofs, then each roof variant over them (only the variant moves).
        public static (int moved, int removed) SeparateHome(GeoHomeModelAsset model)
        {
            var total = Separate(new[] { model.body, model.roofFixed }, new[] { true, true });
            if (model.variants != null)
                foreach (var variant in model.variants)
                {
                    if (!variant) continue;
                    var r = Separate(new[] { model.body, model.roofFixed, variant }, new[] { false, false, true });
                    total.moved += r.moved; total.removed += r.removed;
                }
            return total;
        }

        static void Note(StringBuilder report, string name, (int moved, int removed) r, ref int models, ref int patches, ref int faces)
        {
            if (r.moved == 0 && r.removed == 0) return;
            models++; patches += r.moved; faces += r.removed;
            report.Append(name).Append(": ").Append(r.moved).Append(" patches moved, ").Append(r.removed).Append(" faces removed\n");
        }

        // The meshes are drawn together in one space (GeoCoplanarFaces). Only those marked movable change (and are marked dirty);
        // their further texture coordinate channels and colours are kept in step with the moved vertices. twoSided marks the
        // meshes drawn with a double-sided material (none when it is null).
        public static (int moved, int removed) Separate(Mesh[] meshes, bool[] movable, bool[] twoSided = null)
        {
            var parts = new GeoCoplanarFaces.Part[meshes.Length];
            var extras = new List<Vector4>[meshes.Length][];
            var extraSize = new int[meshes.Length][];
            var colors = new List<Color32>[meshes.Length];
            var before = new (int vertices, int indices)[meshes.Length];
            for (int m = 0; m < meshes.Length; m++)
            {
                var mesh = meshes[m];
                if (!mesh) continue;
                var part = new GeoCoplanarFaces.Part { positions = new List<Vector3>(), normals = new List<Vector3>(), tangents = new List<Vector4>(), uvs = new List<Vector2>(),
                                                       triangles = new List<int>[mesh.subMeshCount], movable = movable[m], twoSided = twoSided != null && twoSided[m] };
                mesh.GetVertices(part.positions); mesh.GetNormals(part.normals); mesh.GetTangents(part.tangents); mesh.GetUVs(0, part.uvs);
                int indices = 0;
                for (int s = 0; s < mesh.subMeshCount; s++) { part.triangles[s] = new List<int>(); mesh.GetTriangles(part.triangles[s], s); indices += part.triangles[s].Count; }
                var lists = extras[m] = new List<Vector4>[4];
                var sizes = extraSize[m] = new int[4];
                for (int u = 1; u < 4; u++)
                {
                    lists[u] = new List<Vector4>(); mesh.GetUVs(u, lists[u]);
                    sizes[u] = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0 + u) ? mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0 + u) : 0;
                }
                var colour = colors[m] = new List<Color32>(); mesh.GetColors(colour);
                part.copyExtra = source =>
                {
                    for (int u = 1; u < 4; u++) if (lists[u].Count > source) lists[u].Add(lists[u][source]);
                    if (colour.Count > source) colour.Add(colour[source]);
                };
                parts[m] = part;
                before[m] = (part.positions.Count, indices);
            }
            var result = GeoCoplanarFaces.Separate(parts);
            for (int m = 0; m < meshes.Length; m++)
            {
                var part = parts[m];
                if (part == null || !part.movable) continue;
                int indices = 0;
                foreach (var list in part.triangles) indices += list.Count;
                if (part.positions.Count == before[m].vertices && indices == before[m].indices) continue;
                var mesh = meshes[m];
                if (part.positions.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(part.positions);
                if (part.normals.Count > 0) mesh.SetNormals(part.normals);
                if (part.tangents.Count > 0) mesh.SetTangents(part.tangents);
                if (part.uvs.Count > 0) mesh.SetUVs(0, part.uvs);
                for (int u = 1; u < 4; u++) if (extras[m][u].Count > 0) SetUVs(mesh, u, extras[m][u], extraSize[m][u]);
                if (colors[m].Count > 0) mesh.SetColors(colors[m]);
                for (int s = 0; s < part.triangles.Length; s++) mesh.SetTriangles(part.triangles[s], s, false);
                mesh.RecalculateBounds();
                if (EditorUtility.IsPersistent(mesh)) EditorUtility.SetDirty(mesh);
            }
            return result;
        }

        // a channel keeps its size: a two-component one set as four would double its memory
        static void SetUVs(Mesh mesh, int channel, List<Vector4> uvs, int size)
        {
            if (size == 2) { var list = new List<Vector2>(uvs.Count); foreach (var uv in uvs) list.Add(uv); mesh.SetUVs(channel, list); }
            else if (size == 3) { var list = new List<Vector3>(uvs.Count); foreach (var uv in uvs) list.Add(uv); mesh.SetUVs(channel, list); }
            else mesh.SetUVs(channel, uvs);
        }
    }
}
