using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // Faces of a library model that lie in one plane, face the same way and overlap fight for the depth buffer: the two
    // textures flicker through each other as the camera moves (a window frame or a patch flush with its wall, a slab laid
    // twice, a home's gable end in the plane of the body's wall). The kits are full of them: most ruins and homes had a
    // hundred or more such pairs. Separate moves the smaller of two overlapping coplanar patches (faces joined by an edge in
    // that plane; a detail that only touches its wall at a corner is a patch of its own) Gap forward along its normal, so it
    // is drawn over the other everywhere, and drops a face that repeats another exactly. A moved patch gets its own vertices:
    // its neighbours out of the plane keep their place, and the step it leaves at its edge (a few centimetres, like a frame
    // standing out of its wall) is not seen. Faces closer than Tolerance count as one plane: decals a few millimetres off
    // their wall still flicker at a few hundred metres in a 24-bit depth buffer.
    public static class GeoModelCoplanar
    {
        public const float Gap = 0.03f;
        public const float Tolerance = 0.015f;
        // faces that only touch at an edge or a corner do not overlap: each is shrunk this far towards its centre first
        const float Shrink = 0.03f;
        const float MinArea = 0.002f;
        const float SameNormal = 0.9995f;

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
                    var r = Repeat(() => Separate(new[] { model.mesh }, new[] { true }));
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

        struct Face
        {
            public int mesh, sub, first;
            public Vector3 a, b, c, normal;
            public float plane, area;
        }

        // The meshes are drawn together in one space. Only those marked movable change (and are marked dirty).
        public static (int moved, int removed) Separate(Mesh[] meshes, bool[] movable)
        {
            var faces = new List<Face>();
            var indices = new List<int>[meshes.Length][];
            var vertices = new List<Vector3>[meshes.Length];
            for (int m = 0; m < meshes.Length; m++)
            {
                var mesh = meshes[m];
                if (!mesh) continue;
                vertices[m] = new List<Vector3>(); mesh.GetVertices(vertices[m]);
                indices[m] = new List<int>[mesh.subMeshCount];
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    indices[m][s] = new List<int>(); mesh.GetTriangles(indices[m][s], s);
                    var list = indices[m][s];
                    for (int i = 0; i + 2 < list.Count; i += 3)
                    {
                        Vector3 a = vertices[m][list[i]], b = vertices[m][list[i + 1]], c = vertices[m][list[i + 2]];
                        var cross = Vector3.Cross(b - a, c - a);
                        float area = cross.magnitude * 0.5f;
                        if (area < MinArea) continue;
                        var normal = cross / (area * 2);
                        faces.Add(new Face { mesh = m, sub = s, first = i, a = a, b = b, c = c, normal = normal, plane = Vector3.Dot(normal, a), area = area });
                    }
                }
            }

            // patches: faces of one mesh in one plane that share an edge (both of its corners by position)
            var parent = new int[faces.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            (long, long, long) Corner(Vector3 v) => ((long)Mathf.Round(v.x * 10000), (long)Mathf.Round(v.y * 10000), (long)Mathf.Round(v.z * 10000));
            var byEdge = new Dictionary<(int, (long, long, long), (long, long, long)), List<int>>();
            for (int f = 0; f < faces.Count; f++)
            {
                var corners = new[] { Corner(faces[f].a), Corner(faces[f].b), Corner(faces[f].c) };
                for (int e = 0; e < 3; e++)
                {
                    var from = corners[e];
                    var to = corners[(e + 1) % 3];
                    var key = from.CompareTo(to) <= 0 ? (faces[f].mesh, from, to) : (faces[f].mesh, to, from);
                    if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = new List<int>();
                    foreach (int other in list)
                        if (Coplanar(faces[f], faces[other], 0.0005f)) parent[Find(f)] = Find(other);
                    list.Add(f);
                }
            }

            // overlapping coplanar faces of different patches
            var buckets = new Dictionary<(int, int, int, int), List<int>>();
            for (int f = 0; f < faces.Count; f++)
            {
                var key = NormalKey(faces[f].normal, Mathf.FloorToInt(faces[f].plane / (Tolerance * 2)));
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                list.Add(f);
            }
            var removed = new HashSet<int>();
            var push = new Dictionary<int, float>();
            // faces that overlap another face of their own patch (a fold, a quad laid twice over a shared edge) move alone
            var facePush = new Dictionary<int, float>();
            var patchArea = new Dictionary<int, float>();
            var planeSum = new Dictionary<int, float>();
            for (int f = 0; f < faces.Count; f++)
            {
                int root = Find(f);
                patchArea[root] = (patchArea.TryGetValue(root, out float x) ? x : 0) + faces[f].area;
                planeSum[root] = (planeSum.TryGetValue(root, out float y) ? y : 0) + faces[f].plane * faces[f].area;
            }
            var pairs = new HashSet<(int, int)>();
            foreach (var entry in buckets)
            {
                var (nx, ny, nz, slot) = entry.Key;
                for (int near = slot - 1; near <= slot + 1; near++)
                {
                    if (!buckets.TryGetValue((nx, ny, nz, near), out var others)) continue;
                    foreach (int p in entry.Value)
                        foreach (int q in others)
                        {
                            if (q <= p || removed.Contains(p) || removed.Contains(q)) continue;
                            if (!Coplanar(faces[p], faces[q], Tolerance) || !Overlap(faces[p], faces[q])) continue;
                            // an exact repeat shares every edge with the face it repeats, so it is in the same patch
                            if (Same(faces[p], faces[q]))
                            {
                                if (movable[faces[q].mesh]) removed.Add(q);
                                else if (movable[faces[p].mesh]) removed.Add(p);
                                continue;
                            }
                            int rp = Find(p), rq = Find(q);
                            if (rp != rq)
                            {
                                pairs.Add(rp < rq ? (rp, rq) : (rq, rp));
                                continue;
                            }
                            if (!movable[faces[p].mesh]) continue;
                            int alone = faces[p].area <= faces[q].area ? p : q, under = alone == p ? q : p;
                            float lift = faces[under].plane + Gap - faces[alone].plane;
                            if (lift > 0) facePush[alone] = Mathf.Max(facePush.TryGetValue(alone, out float l) ? l : 0, lift);
                        }
                }
            }
            foreach (var (p, q) in pairs)
            {
                bool mp = movable[faces[p].mesh], mq = movable[faces[q].mesh];
                if (!mp && !mq) continue;
                int mover = mp && mq ? (patchArea[p] <= patchArea[q] ? p : q) : mp ? p : q, other = mover == p ? q : p;
                float shift = planeSum[other] / patchArea[other] + Gap - planeSum[mover] / patchArea[mover];
                if (shift > 0) push[mover] = Mathf.Max(push.TryGetValue(mover, out float s) ? s : 0, shift);
            }
            if (push.Count == 0 && facePush.Count == 0 && removed.Count == 0) return (0, 0);

            // a moved patch gets its own copies of its vertices, shifted along its normal
            var moved = new HashSet<int>();
            for (int m = 0; m < meshes.Length; m++)
            {
                if (!meshes[m] || !movable[m]) continue;
                var mesh = meshes[m];
                var normals = new List<Vector3>(); mesh.GetNormals(normals);
                var tangents = new List<Vector4>(); mesh.GetTangents(tangents);
                var colors = new List<Color32>(); mesh.GetColors(colors);
                var uvs = new List<Vector4>[4];
                var uvSize = new int[4];
                for (int u = 0; u < 4; u++)
                {
                    uvs[u] = new List<Vector4>(); mesh.GetUVs(u, uvs[u]);
                    uvSize[u] = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0 + u) ? mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0 + u) : 0;
                }
                var copies = new Dictionary<(int, int), int>();
                bool changed = false;
                var dropped = new HashSet<(int, int)>();
                for (int f = 0; f < faces.Count; f++)
                {
                    if (faces[f].mesh != m) continue;
                    if (removed.Contains(f)) { dropped.Add((faces[f].sub, faces[f].first)); changed = true; continue; }
                    // a face moved alone gets copies of its own (key below zero), a patch shares them
                    int root = facePush.TryGetValue(f, out float shift) ? -1 - f : Find(f);
                    if (root >= 0 && !push.TryGetValue(root, out shift)) continue;
                    var list = indices[m][faces[f].sub];
                    for (int k = 0; k < 3; k++)
                    {
                        int source = list[faces[f].first + k];
                        if (!copies.TryGetValue((root, source), out int copy))
                        {
                            copy = vertices[m].Count;
                            vertices[m].Add(vertices[m][source] + faces[f].normal * shift);
                            if (normals.Count > 0) normals.Add(normals[source]);
                            if (tangents.Count > 0) tangents.Add(tangents[source]);
                            if (colors.Count > 0) colors.Add(colors[source]);
                            for (int u = 0; u < 4; u++) if (uvs[u].Count > 0) uvs[u].Add(uvs[u][source]);
                            copies[(root, source)] = copy;
                        }
                        list[faces[f].first + k] = copy;
                    }
                    moved.Add(root); changed = true;
                }
                if (!changed) continue;
                var bounds = mesh.bounds;
                mesh.indexFormat = vertices[m].Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : mesh.indexFormat;
                mesh.SetVertices(vertices[m]);
                if (normals.Count > 0) mesh.SetNormals(normals);
                if (tangents.Count > 0) mesh.SetTangents(tangents);
                if (colors.Count > 0) mesh.SetColors(colors);
                for (int u = 0; u < 4; u++) if (uvs[u].Count > 0) SetUVs(mesh, u, uvs[u], uvSize[u]);
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var list = indices[m][s];
                    var kept = new List<int>(list.Count);
                    for (int i = 0; i + 2 < list.Count; i += 3)
                        if (!dropped.Contains((s, i))) { kept.Add(list[i]); kept.Add(list[i + 1]); kept.Add(list[i + 2]); }
                    mesh.SetTriangles(kept, s, false);
                }
                mesh.RecalculateBounds();
                if (EditorUtility.IsPersistent(mesh)) EditorUtility.SetDirty(mesh);
            }
            return (moved.Count, removed.Count);
        }

        // a channel keeps its size: a two-component one set as four would double its memory
        static void SetUVs(Mesh mesh, int channel, List<Vector4> uvs, int size)
        {
            if (size == 2) { var list = new List<Vector2>(uvs.Count); foreach (var uv in uvs) list.Add(uv); mesh.SetUVs(channel, list); }
            else if (size == 3) { var list = new List<Vector3>(uvs.Count); foreach (var uv in uvs) list.Add(uv); mesh.SetUVs(channel, list); }
            else mesh.SetUVs(channel, uvs);
        }

        static (int, int, int, int) NormalKey(Vector3 n, int slot) => (Mathf.RoundToInt(n.x * 40), Mathf.RoundToInt(n.y * 40), Mathf.RoundToInt(n.z * 40), slot);

        static bool Coplanar(Face p, Face q, float tolerance) =>
            Vector3.Dot(p.normal, q.normal) >= SameNormal && Mathf.Abs(p.plane - q.plane) <= tolerance;

        static bool Same(Face p, Face q)
        {
            const float e = 1e-8f;
            bool Has(Vector3 v) => (q.a - v).sqrMagnitude < e || (q.b - v).sqrMagnitude < e || (q.c - v).sqrMagnitude < e;
            return Has(p.a) && Has(p.b) && Has(p.c);
        }

        // The two faces, laid in p's plane and shrunk by Shrink, cross (separating axis test on their edges).
        static bool Overlap(Face p, Face q)
        {
            var n = p.normal;
            var ax = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var ay = Vector3.Cross(n, ax);
            var P = Flat(p, ax, ay);
            var Q = Flat(q, ax, ay);
            return !Separated(P, Q, P) && !Separated(P, Q, Q);
        }

        static Vector2[] Flat(Face f, Vector3 ax, Vector3 ay)
        {
            var points = new[] { f.a, f.b, f.c };
            var flat = new Vector2[3];
            var centre = Vector2.zero;
            for (int i = 0; i < 3; i++) { flat[i] = new Vector2(Vector3.Dot(points[i], ax), Vector3.Dot(points[i], ay)); centre += flat[i] / 3; }
            for (int i = 0; i < 3; i++) { var d = centre - flat[i]; flat[i] += d.normalized * Mathf.Min(Shrink, d.magnitude * 0.5f); }
            return flat;
        }

        static bool Separated(Vector2[] p, Vector2[] q, Vector2[] edges)
        {
            for (int i = 0; i < 3; i++)
            {
                var edge = edges[(i + 1) % 3] - edges[i];
                var axis = new Vector2(-edge.y, edge.x);
                float minP = float.MaxValue, maxP = float.MinValue, minQ = float.MaxValue, maxQ = float.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    float sp = Vector2.Dot(p[k], axis), sq = Vector2.Dot(q[k], axis);
                    minP = Mathf.Min(minP, sp); maxP = Mathf.Max(maxP, sp); minQ = Mathf.Min(minQ, sq); maxQ = Mathf.Max(maxQ, sq);
                }
                if (maxP <= minQ || maxQ <= minP) return true;
            }
            return false;
        }
    }
}
