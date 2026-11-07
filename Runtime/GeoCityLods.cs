using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    // Distance detail for a merged city (GeoCityBuilder's chunks), made when a tile is loaded from a package. A chunk copies a
    // ruin or home model (two to three thousand triangles) for every building drawn with one, and had no LOD: every triangle was
    // drawn however far the chunk was. Bakhmut's battlefield held 34 M triangles in its chunks; seen along the ground from 2 km
    // the frame drew 45 M, 20 M of them buildings, twice with a depth-normals prepass. Each chunk gets coarser copies made on a
    // worker thread by vertex clustering (one vertex for every cell of Cells metres, at the mean of the vertices in it, with the
    // attributes of the first; triangles that collapse or repeat are dropped) and an LODGroup that switches to them by the
    // chunk's height on screen (Heights; a 220 m chunk at a 60° field of view: about 520 m and 1.3 km). The edge-collapse
    // simplifier (GeoMeshSimplifier) keeps outlines better, but takes seconds for a chunk; clustering takes milliseconds, and
    // what it loses is smaller than a pixel at those distances. Only the library models' sub-meshes (ModelSubMesh and on) are
    // clustered: the generated buildings (sub-meshes 0 and 1) take their colours from a palette by texture coordinate, which a
    // merged vertex smears across the palette as stripes of other colours, and they are a few percent of a chunk's triangles.
    // Nothing is stored in the package, so worlds made before this get it too.
    public static class GeoCityLods
    {
        public const int ModelSubMesh = 2;
        public static readonly float[] Cells = { 1f, 3f };
        // screen height below which each level gives way to the next; the last level is never culled
        public static readonly float[] Heights = { 0.3f, 0.12f };
        // a level that keeps more of the triangles than this is not worth its memory and draw
        const float MaxShare = 0.8f;

        // The chunks of a tile that have a mesh to reduce, and their meshes (main thread).
        static List<(MeshRenderer renderer, Mesh mesh)> Chunks(GameObject root)
        {
            var chunks = new List<(MeshRenderer, Mesh)>();
            if (!root) return chunks;
            foreach (var chunk in root.GetComponentsInChildren<GeoCityChunk>(true))
            {
                if (chunk.GetComponent<LODGroup>() || !chunk.TryGetComponent(out MeshRenderer renderer) || !chunk.TryGetComponent(out MeshFilter filter)) continue;
                var mesh = filter.sharedMesh;
                if (mesh && mesh.isReadable && mesh.subMeshCount > ModelSubMesh) chunks.Add((renderer, mesh));
            }
            return chunks;
        }

        // Gives the chunks under root their levels, the clustering on a worker thread, spending at most budgetMs of the main
        // thread a frame on making the objects. The meshes go into created, which unloading the tile destroys.
        public static async UniTask AddAsync(GameObject root, List<Object> created, float budgetMs, CancellationToken cancellation)
        {
            var chunks = Chunks(root);
            if (chunks.Count == 0) return;
            var source = Mesh.AcquireReadOnlyMeshData(chunks.ConvertAll(c => c.mesh));
            var target = Mesh.AllocateWritableMeshData(chunks.Count * Cells.Length);
            bool applied = false;
            try
            {
                // not cancelled half-way: the worker reads the mesh data, which is disposed after it
                var kept = await UniTask.RunOnThreadPool(() => Build(source, target, cancellation));
                cancellation.ThrowIfCancellationRequested();
                var meshes = Apply(target, chunks.Count, created);
                applied = true;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < chunks.Count; i++)
                {
                    Attach(chunks[i].renderer, chunks[i].mesh, meshes, kept, i);
                    if (clock.Elapsed.TotalMilliseconds < budgetMs) continue;
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellation);
                    clock.Restart();
                }
            }
            finally
            {
                source.Dispose();
                if (!applied) target.Dispose();
            }
        }

        // The same at once (LoadTile).
        public static void Add(GameObject root, List<Object> created)
        {
            var chunks = Chunks(root);
            if (chunks.Count == 0) return;
            var source = Mesh.AcquireReadOnlyMeshData(chunks.ConvertAll(c => c.mesh));
            var target = Mesh.AllocateWritableMeshData(chunks.Count * Cells.Length);
            try
            {
                var kept = Build(source, target, CancellationToken.None);
                var meshes = Apply(target, chunks.Count, created);
                for (int i = 0; i < chunks.Count; i++) Attach(chunks[i].renderer, chunks[i].mesh, meshes, kept, i);
            }
            finally { source.Dispose(); }
        }

        // Fills target[chunk * Cells.Length + level]; kept says which levels are worth drawing.
        static bool[] Build(Mesh.MeshDataArray source, Mesh.MeshDataArray target, CancellationToken cancellation)
        {
            var kept = new bool[target.Length];
            for (int i = 0; i < source.Length && !cancellation.IsCancellationRequested; i++)
            {
                var mesh = source[i];
                var positions = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var submeshes = new int[mesh.subMeshCount][];
                try
                {
                    mesh.GetVertices(positions);
                    long triangles = 0;
                    for (int s = 0; s < submeshes.Length; s++)
                    {
                        var indices = new NativeArray<int>(mesh.GetSubMesh(s).indexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                        try { mesh.GetIndices(indices, s); submeshes[s] = indices.ToArray(); }
                        finally { indices.Dispose(); }
                        triangles += submeshes[s].Length / 3;
                    }
                    // each level kept only where it has at most MaxShare of the triangles of the one drawn before it
                    long limit = (long)(triangles * MaxShare);
                    for (int level = 0; level < Cells.Length; level++)
                    {
                        int at = i * Cells.Length + level;
                        long left = Cluster(mesh, positions, submeshes, Cells[level], target[at]);
                        kept[at] = left > 0 && left <= limit;
                        if (kept[at]) limit = (long)(left * MaxShare);
                    }
                }
                finally { positions.Dispose(); }
            }
            return kept;
        }

        // One clustered copy of source into target; returns its triangle count.
        static long Cluster(Mesh.MeshData source, NativeArray<Vector3> positions, int[][] submeshes, float cell, Mesh.MeshData target)
        {
            int count = positions.Length;
            Vector3 min = positions.Length > 0 ? positions[0] : Vector3.zero;
            for (int v = 1; v < count; v++) min = Vector3.Min(min, positions[v]);
            float inverse = 1f / cell;
            var clusters = new Dictionary<long, int>(count / 4);
            var sums = new List<Vector3>(count / 4);
            var members = new List<int>(count / 4);
            var first = new List<int>(count / 4);
            var remap = new int[count];
            var stamp = new int[count];
            var lists = new List<int>[submeshes.Length];
            var seen = new HashSet<long>();
            long triangles = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                var indices = submeshes[s];
                foreach (int v in indices)
                {
                    if (stamp[v] == s + 1) continue;
                    stamp[v] = s + 1;
                    long key;
                    if (s < ModelSubMesh) key = (1L << 62) | (long)v; // kept as it is
                    else
                    {
                        var p = (positions[v] - min) * inverse;
                        key = (long)p.x | ((long)p.y << 16) | ((long)p.z << 32) | ((long)s << 48);
                    }
                    if (!clusters.TryGetValue(key, out int id))
                    {
                        id = sums.Count; clusters[key] = id;
                        sums.Add(Vector3.zero); members.Add(0); first.Add(v);
                    }
                    sums[id] += positions[v]; members[id]++;
                    remap[v] = id;
                }
                var list = new List<int>(s < ModelSubMesh ? indices.Length : indices.Length / 3);
                seen.Clear();
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    int a = remap[indices[t]], b = remap[indices[t + 1]], c = remap[indices[t + 2]];
                    if (a == b || b == c || a == c) continue;
                    // the same triangle facing the same way, from its smallest corner: the other side of a thin wall stays
                    long key = a < b && a < c ? Key(a, b, c) : b < c ? Key(b, c, a) : Key(c, a, b);
                    if (!seen.Add(key)) continue;
                    list.Add(a); list.Add(b); list.Add(c);
                }
                lists[s] = list;
                triangles += list.Count / 3;
            }

            // the vertices: the first member's bytes in every stream (normal, tangent, texture coordinates as they were), at the
            // members' mean position where positions are three floats
            int vertices = sums.Count;
            var layout = new List<VertexAttributeDescriptor>();
            for (var attribute = VertexAttribute.Position; attribute <= VertexAttribute.BlendIndices; attribute++)
                if (source.HasVertexAttribute(attribute))
                    layout.Add(new VertexAttributeDescriptor(attribute, source.GetVertexAttributeFormat(attribute), source.GetVertexAttributeDimension(attribute), source.GetVertexAttributeStream(attribute)));
            target.SetVertexBufferParams(vertices, layout.ToArray());
            for (int stream = 0; stream < 4; stream++)
                if (target.GetVertexBufferStride(stream) != source.GetVertexBufferStride(stream)) return 0; // a layout the bytes cannot be copied into
            for (int stream = 0; stream < 4; stream++)
            {
                int stride = source.GetVertexBufferStride(stream);
                if (stride == 0) continue;
                var from = source.GetVertexData<byte>(stream);
                var to = target.GetVertexData<byte>(stream);
                for (int i = 0; i < vertices; i++) NativeArray<byte>.Copy(from, first[i] * stride, to, i * stride, stride);
            }
            if (source.GetVertexAttributeFormat(VertexAttribute.Position) == VertexAttributeFormat.Float32 && source.GetVertexAttributeDimension(VertexAttribute.Position) == 3)
            {
                int stream = source.GetVertexAttributeStream(VertexAttribute.Position);
                int stride = source.GetVertexBufferStride(stream) / 4, offset = source.GetVertexAttributeOffset(VertexAttribute.Position) / 4;
                var floats = target.GetVertexData<float>(stream);
                for (int i = 0; i < vertices; i++)
                {
                    var p = sums[i] / members[i];
                    int at = i * stride + offset;
                    floats[at] = p.x; floats[at + 1] = p.y; floats[at + 2] = p.z;
                }
            }

            int total = 0;
            foreach (var list in lists) total += list.Count;
            bool wide = vertices > ushort.MaxValue;
            target.SetIndexBufferParams(total, wide ? IndexFormat.UInt32 : IndexFormat.UInt16);
            if (wide) { var data = target.GetIndexData<int>(); int at = 0; foreach (var list in lists) foreach (int i in list) data[at++] = i; }
            else { var data = target.GetIndexData<ushort>(); int at = 0; foreach (var list in lists) foreach (int i in list) data[at++] = (ushort)i; }
            target.subMeshCount = lists.Length;
            const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            for (int s = 0, start = 0; s < lists.Length; start += lists[s].Count, s++)
                target.SetSubMesh(s, new SubMeshDescriptor(start, lists[s].Count), flags);
            return triangles;
        }

        static long Key(int a, int b, int c) => ((long)a << 42) | ((long)b << 21) | (long)c;

        static Mesh[] Apply(Mesh.MeshDataArray target, int chunks, List<Object> created)
        {
            var meshes = new Mesh[target.Length];
            for (int i = 0; i < meshes.Length; i++) { meshes[i] = new Mesh(); created.Add(meshes[i]); }
            Mesh.ApplyAndDisposeWritableMeshData(target, meshes, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            return meshes;
        }

        // The chunk's LODGroup: its own renderer, then a child object for every level worth drawing; a level not kept is destroyed.
        // A level shows below its own Heights entry, so the one before it gives way there.
        static void Attach(MeshRenderer renderer, Mesh source, Mesh[] meshes, bool[] kept, int chunk)
        {
            var levels = new List<LOD> { new LOD(0, new Renderer[] { renderer }) };
            for (int level = 0; level < Cells.Length; level++)
            {
                var mesh = meshes[chunk * Cells.Length + level];
                if (!renderer || !kept[chunk * Cells.Length + level])
                {
                    if (Application.isPlaying) Object.Destroy(mesh); else Object.DestroyImmediate(mesh);
                    continue;
                }
                var before = levels[levels.Count - 1];
                before.screenRelativeTransitionHeight = Heights[level];
                levels[levels.Count - 1] = before;
                mesh.name = $"{source.name} LOD{levels.Count}";
                mesh.bounds = source.bounds;
                var go = new GameObject($"LOD{levels.Count}") { layer = renderer.gameObject.layer };
                go.transform.SetParent(renderer.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var copy = go.AddComponent<MeshRenderer>();
                copy.sharedMaterials = renderer.sharedMaterials;
                copy.shadowCastingMode = renderer.shadowCastingMode;
                copy.receiveShadows = renderer.receiveShadows;
                copy.staticShadowCaster = renderer.staticShadowCaster;
                copy.renderingLayerMask = renderer.renderingLayerMask;
                copy.lightProbeUsage = renderer.lightProbeUsage;
                copy.reflectionProbeUsage = renderer.reflectionProbeUsage;
                copy.motionVectorGenerationMode = renderer.motionVectorGenerationMode;
                levels.Add(new LOD(0, new Renderer[] { copy }));
            }
            if (levels.Count == 1) return;
            var group = renderer.gameObject.AddComponent<LODGroup>();
            group.SetLODs(levels.ToArray());
            group.RecalculateBounds();
        }
    }
}
