using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoWorld.Editor
{
    // Simplifies a mesh by collapsing edges onto the mesh's own vertices (Garland & Heckbert's quadric error metric,
    // with the vertex rules of meshoptimizer), in managed code. Vertices that share a position are one point of the
    // surface; the edges between them are classified as
    //   border  (one triangle: an open edge, a window's reveal ending in the air),
    //   seam    (two triangles that use different vertices there, or different sub-meshes: a texture seam, a hard
    //            edge between two flat faces, a material boundary),
    //   inner   (two triangles that share the vertices), or non-manifold (more).
    // A point with only inner edges may move to any neighbour; a point on exactly two border (or two seam) edges may
    // only slide along them, so outlines, hard edges and texture seams keep their course; every other point (corners,
    // where seams meet, non-manifold places) stays. A collapse never turns a triangle over, never joins two surfaces
    // that only touch, and moves every corner of the point to the vertex on the same side of a seam, so texture
    // coordinates, normals and materials stay as they were. It stops at the target triangle count or when the next
    // collapse would move the surface by more than the error limit.
    public static class GeoMeshSimplifier
    {
        enum Kind { Inner, Border, Seam, Locked }
        // degrees an outline may turn at a point that is removed from it
        const float StraightLine = 15;

        sealed class Quadric
        {
            public double a2, ab, ac, ad, b2, bc, bd, c2, cd, d2, weight;
            public void Add(Vector3 n, double d, double w)
            {
                a2 += w * n.x * n.x; ab += w * n.x * n.y; ac += w * n.x * n.z; ad += w * n.x * d;
                b2 += w * n.y * n.y; bc += w * n.y * n.z; bd += w * n.y * d;
                c2 += w * n.z * n.z; cd += w * n.z * d; d2 += w * d * d; weight += w;
            }
            public void Add(Quadric q)
            {
                a2 += q.a2; ab += q.ab; ac += q.ac; ad += q.ad; b2 += q.b2; bc += q.bc; bd += q.bd; c2 += q.c2; cd += q.cd; d2 += q.d2; weight += q.weight;
            }
            // mean squared distance of p from the planes
            public double Error(Vector3 p)
            {
                double x = p.x, y = p.y, z = p.z;
                double e = a2 * x * x + 2 * ab * x * y + 2 * ac * x * z + 2 * ad * x + b2 * y * y + 2 * bc * y * z + 2 * bd * y + c2 * z * z + 2 * cd * z + d2;
                return weight > 0 ? System.Math.Max(0, e) / weight : 0;
            }
        }

        // Triangle lists per sub-mesh, over the given vertices, with at most `ratio` of the triangles (or as close as the
        // error limit, metres, allows). Returns null when nothing could be collapsed.
        public static List<int>[] Simplify(Vector3[] vertices, List<int>[] submeshes, float ratio, float maxError)
        {
            // points: vertices with the same position
            var point = new int[vertices.Length];
            var points = new Dictionary<Vector3, int>();
            var position = new List<Vector3>();
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!points.TryGetValue(vertices[i], out int p)) { p = position.Count; points[vertices[i]] = p; position.Add(vertices[i]); }
                point[i] = p;
            }
            int count = 0;
            foreach (var list in submeshes) count += list.Count / 3;
            var corner = new int[count * 3]; var sub = new int[count]; var alive = new bool[count];
            int t0 = 0;
            for (int s = 0; s < submeshes.Length; s++)
                for (int i = 0; i + 2 < submeshes[s].Count; i += 3, t0++)
                {
                    corner[t0 * 3] = submeshes[s][i]; corner[t0 * 3 + 1] = submeshes[s][i + 1]; corner[t0 * 3 + 2] = submeshes[s][i + 2];
                    sub[t0] = s;
                    alive[t0] = point[corner[t0 * 3]] != point[corner[t0 * 3 + 1]] && point[corner[t0 * 3 + 1]] != point[corner[t0 * 3 + 2]] && point[corner[t0 * 3]] != point[corner[t0 * 3 + 2]];
                }
            var around = new List<int>[position.Count];
            for (int p = 0; p < around.Length; p++) around[p] = new List<int>();
            int living = 0;
            for (int t = 0; t < count; t++) if (alive[t]) { living++; for (int k = 0; k < 3; k++) around[point[corner[t * 3 + k]]].Add(t); }
            int target = Mathf.Max(1, Mathf.CeilToInt(living * ratio));

            // quadrics: the planes of the triangles (by area), and planes standing on border and seam edges, which
            // keep outlines in place
            var quadric = new Quadric[position.Count];
            for (int p = 0; p < quadric.Length; p++) quadric[p] = new Quadric();
            for (int t = 0; t < count; t++)
            {
                if (!alive[t]) continue;
                Vector3 a = position[point[corner[t * 3]]], b = position[point[corner[t * 3 + 1]]], c = position[point[corner[t * 3 + 2]]];
                var cross = Vector3.Cross(b - a, c - a);
                float area = cross.magnitude * 0.5f;
                if (area < 1e-12f) continue;
                var n = cross.normalized;
                for (int k = 0; k < 3; k++) quadric[point[corner[t * 3 + k]]].Add(n, -Vector3.Dot(n, a), area);
            }
            var edges = Classify(corner, sub, alive, point, count, out var kinds, out var line, around, position.Count);
            foreach (var pair in edges)
            {
                var e = pair.Value;
                if (e.kind == Kind.Inner) continue;
                int t = e.first;
                Vector3 pa = position[pair.Key.x], pb = position[pair.Key.y];
                Vector3 a = position[point[corner[t * 3]]], b = position[point[corner[t * 3 + 1]]], c = position[point[corner[t * 3 + 2]]];
                var face = Vector3.Cross(b - a, c - a).normalized;
                var side = Vector3.Cross(pb - pa, face);
                float length = side.magnitude;
                if (length < 1e-6f) continue;
                side /= length;
                double weight = length * length * (e.kind == Kind.Border ? 4 : 2);
                quadric[pair.Key.x].Add(side, -Vector3.Dot(side, pa), weight);
                quadric[pair.Key.y].Add(side, -Vector3.Dot(side, pa), weight);
            }

            double limit = (double)maxError * maxError;
            var touched = new bool[position.Count];
            var candidates = new List<(double cost, int u, int v)>();
            bool any = false;
            for (int pass = 0; pass < 64 && living > target; pass++)
            {
                if (pass > 0) edges = Classify(corner, sub, alive, point, count, out kinds, out line, around, position.Count);
                candidates.Clear();
                foreach (var pair in edges)
                {
                    var e = pair.Value;
                    if (e.kind == Kind.Locked) continue;
                    foreach (var (u, v) in new[] { (pair.Key.x, pair.Key.y), (pair.Key.y, pair.Key.x) })
                    {
                        var ku = kinds[u];
                        if (ku == Kind.Locked) continue;
                        if (ku == Kind.Border && e.kind != Kind.Border) continue;
                        if (ku == Kind.Seam && e.kind != Kind.Seam) continue;
                        // a point of an outline or hard edge goes only where the line runs on almost straight
                        if (ku != Kind.Inner)
                        {
                            int w = line[u].x == v ? line[u].y : line[u].x;
                            if (w < 0) continue;
                            if (Vector3.Angle(position[u] - position[v], position[w] - position[u]) > StraightLine) continue;
                        }
                        double cost = quadric[u].Error(position[v]);
                        if (cost <= limit) candidates.Add((cost, u, v));
                    }
                }
                if (candidates.Count == 0) break;
                candidates.Sort((x, y) => x.cost.CompareTo(y.cost));
                System.Array.Clear(touched, 0, touched.Length);
                int collapsed = 0;
                foreach (var (cost, u, v) in candidates)
                {
                    if (living <= target) break;
                    if (touched[u] || touched[v]) continue;
                    if (!Collapse(u, v, corner, alive, point, around, position, ref living)) continue;
                    quadric[v].Add(quadric[u]);
                    collapsed++; any = true;
                    touched[u] = touched[v] = true;
                    foreach (int t in around[v]) if (alive[t]) for (int k = 0; k < 3; k++) touched[point[corner[t * 3 + k]]] = true;
                }
                if (collapsed == 0) break;
            }
            if (!any) return null;
            var result = new List<int>[submeshes.Length];
            for (int s = 0; s < result.Length; s++) result[s] = new List<int>();
            for (int t = 0; t < count; t++)
                if (alive[t]) { var list = result[sub[t]]; list.Add(corner[t * 3]); list.Add(corner[t * 3 + 1]); list.Add(corner[t * 3 + 2]); }
            return result;
        }

        struct Edge { public Kind kind; public int first, second, count; }

        static Dictionary<Vector2Int, Edge> Classify(int[] corner, int[] sub, bool[] alive, int[] point, int count, out Kind[] kinds, out Vector2Int[] line, List<int>[] around, int points)
        {
            var edges = new Dictionary<Vector2Int, Edge>();
            for (int t = 0; t < count; t++)
            {
                if (!alive[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = point[corner[t * 3 + k]], b = point[corner[t * 3 + (k + 1) % 3]];
                    var key = a < b ? new Vector2Int(a, b) : new Vector2Int(b, a);
                    edges.TryGetValue(key, out var e);
                    if (e.count == 0) e.first = t; else if (e.count == 1) e.second = t;
                    e.count++;
                    edges[key] = e;
                }
            }
            var border = new int[points]; var seam = new int[points]; var edgeCount = new int[points]; var locked = new bool[points];
            // the two neighbours of a point along its border or seam
            line = new Vector2Int[points];
            for (int p = 0; p < points; p++) line[p] = new Vector2Int(-1, -1);
            var keys = new List<Vector2Int>(edges.Keys);
            foreach (var key in keys)
            {
                var e = edges[key];
                if (e.count == 1) e.kind = Kind.Border;
                else if (e.count > 2) e.kind = Kind.Locked;
                else
                {
                    bool same = sub[e.first] == sub[e.second] &&
                                VertexAt(corner, point, e.first, key.x) == VertexAt(corner, point, e.second, key.x) &&
                                VertexAt(corner, point, e.first, key.y) == VertexAt(corner, point, e.second, key.y);
                    e.kind = same ? Kind.Inner : Kind.Seam;
                }
                edges[key] = e;
                foreach (int p in new[] { key.x, key.y })
                {
                    edgeCount[p]++;
                    if (e.kind == Kind.Border || e.kind == Kind.Seam)
                    {
                        int other = p == key.x ? key.y : key.x;
                        if (line[p].x < 0) line[p].x = other; else line[p].y = other;
                    }
                    if (e.kind == Kind.Border) border[p]++;
                    else if (e.kind == Kind.Seam) seam[p]++;
                    else if (e.kind == Kind.Locked) locked[p] = true;
                }
            }
            kinds = new Kind[points];
            for (int p = 0; p < points; p++)
            {
                int triangles = 0;
                foreach (int t in around[p]) if (alive[t]) triangles++;
                if (triangles == 0 || locked[p]) kinds[p] = Kind.Locked;
                else if (border[p] == 0 && seam[p] == 0) kinds[p] = edgeCount[p] == triangles ? Kind.Inner : Kind.Locked;   // one closed fan
                else if (border[p] == 2 && seam[p] == 0) kinds[p] = edgeCount[p] == triangles + 1 ? Kind.Border : Kind.Locked; // one open fan
                else if (seam[p] == 2 && border[p] == 0) kinds[p] = edgeCount[p] == triangles ? Kind.Seam : Kind.Locked;
                else kinds[p] = Kind.Locked;
            }
            return edges;
        }

        static int VertexAt(int[] corner, int[] point, int t, int p)
        {
            for (int k = 0; k < 3; k++) if (point[corner[t * 3 + k]] == p) return corner[t * 3 + k];
            return -1;
        }

        // Moves point u onto point v, if that keeps the surface sound.
        static bool Collapse(int u, int v, int[] corner, bool[] alive, int[] point, List<int>[] around, List<Vector3> position, ref int living)
        {
            // every vertex of u goes to the vertex of v on the same side, learnt from the triangles on the edge
            var map = new Dictionary<int, int>();
            var shared = new List<int>();
            var thirds = new HashSet<int>();
            foreach (int t in around[u])
            {
                if (!alive[t]) continue;
                int iu = -1, iv = -1, third = -1;
                for (int k = 0; k < 3; k++)
                {
                    int c = corner[t * 3 + k], p = point[c];
                    if (p == u) iu = c; else if (p == v) iv = c; else third = p;
                }
                if (iv < 0) continue;
                if (map.TryGetValue(iu, out int known) && known != iv) return false;
                map[iu] = iv; shared.Add(t); thirds.Add(third);
            }
            if (shared.Count == 0) return false;
            // link condition: u and v have no neighbours in common but the corners of the triangles on their edge
            var neighbours = new HashSet<int>();
            foreach (int t in around[v]) if (alive[t]) for (int k = 0; k < 3; k++) { int p = point[corner[t * 3 + k]]; if (p != v) neighbours.Add(p); }
            foreach (int t in around[u])
            {
                if (!alive[t]) continue;
                for (int k = 0; k < 3; k++) { int p = point[corner[t * 3 + k]]; if (p != u && p != v && neighbours.Contains(p) && !thirds.Contains(p)) return false; }
            }
            // no triangle turns over or becomes a sliver
            Vector3 target = position[v];
            foreach (int t in around[u])
            {
                if (!alive[t] || shared.Contains(t)) continue;
                int iu = -1;
                var p = new Vector3[3]; var q = new Vector3[3];
                for (int k = 0; k < 3; k++)
                {
                    int c = corner[t * 3 + k];
                    p[k] = position[point[c]];
                    q[k] = point[c] == u ? target : p[k];
                    if (point[c] == u) iu = c;
                }
                if (!map.ContainsKey(iu)) return false;
                var before = Vector3.Cross(p[1] - p[0], p[2] - p[0]); var after = Vector3.Cross(q[1] - q[0], q[2] - q[0]);
                if (after.sqrMagnitude < 1e-14f || Vector3.Dot(before.normalized, after.normalized) < 0.2f) return false;
            }
            foreach (int t in shared) { alive[t] = false; living--; }
            foreach (int t in around[u])
            {
                if (!alive[t]) continue;
                for (int k = 0; k < 3; k++) { int c = corner[t * 3 + k]; if (point[c] == u) corner[t * 3 + k] = map[c]; }
                around[v].Add(t);
            }
            around[u].Clear();
            return true;
        }

        // A copy of a mesh with the simplified triangles and only the vertices they use.
        public static Mesh Simplify(Mesh mesh, float ratio, float maxError)
        {
            var vertices = mesh.vertices;
            var submeshes = new List<int>[mesh.subMeshCount];
            for (int s = 0; s < submeshes.Length; s++) submeshes[s] = new List<int>(mesh.GetTriangles(s));
            var triangles = Simplify(vertices, submeshes, ratio, maxError);
            if (triangles == null) return null;
            var normals = mesh.normals; var tangents = mesh.tangents; var uvs = mesh.uv;
            var remap = new Dictionary<int, int>(); var used = new List<int>();
            foreach (var list in triangles)
                for (int i = 0; i < list.Count; i++)
                {
                    if (!remap.TryGetValue(list[i], out int at)) { at = used.Count; remap[list[i]] = at; used.Add(list[i]); }
                    list[i] = at;
                }
            var result = new Mesh { name = mesh.name, indexFormat = used.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            var v = new List<Vector3>(used.Count); foreach (int i in used) v.Add(vertices[i]);
            result.SetVertices(v);
            if (normals.Length == vertices.Length) { var n = new List<Vector3>(used.Count); foreach (int i in used) n.Add(normals[i]); result.SetNormals(n); }
            if (tangents.Length == vertices.Length) { var t = new List<Vector4>(used.Count); foreach (int i in used) t.Add(tangents[i]); result.SetTangents(t); }
            if (uvs.Length == vertices.Length) { var u = new List<Vector2>(used.Count); foreach (int i in used) u.Add(uvs[i]); result.SetUVs(0, u); }
            result.subMeshCount = triangles.Length;
            for (int s = 0; s < triangles.Length; s++) result.SetTriangles(triangles[s], s, false);
            result.RecalculateBounds();
            return result;
        }
    }
}
