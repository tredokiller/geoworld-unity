using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // Faces drawn together that lie in one plane, face the same way and overlap fight for the depth buffer: the two textures
    // flicker through each other as the camera turns. A library model has them where a detail is flush with its wall (fixed
    // in the assets, GeoModelCoplanar); a fitted building gets more where its parts meet — a long house covered by two to four
    // home models, each with its roof's overhang lying in the plane of the next one's roof, or a ruin's repeated bays and its
    // L, T and U parts — and where a model is squashed to the footprint, which brings faces a few centimetres apart together.
    // Separate moves the smaller of two overlapping coplanar patches (faces joined by an edge in that plane) Gap forward along
    // its normal, so it is drawn over the other everywhere, moves a face folded over its own patch alone, and drops a face that
    // repeats another exactly. Patches are placed from the largest down, each Gap in front of every overlapping face already
    // placed: a stack of three or four (a ruin's bay repeated over its neighbours at a seam) is laid out in one pass. A moved
    // patch gets its own vertices: its neighbours out of the plane keep their place, and the step at its edge (a few
    // centimetres, like a frame standing out of its wall or one roof sheet over another) is not seen.
    // Faces closer than Tolerance count as one plane.
    // A double-sided material (the ruins: thin sheets, shards, hanging slabs) draws a face from behind too, so two faces in one
    // plane facing opposite ways fight as well: a roof slab whose top and underside were laid on the same plane flickers grey
    // and dark as the camera turns. The smaller of the two moves Gap along its own normal (the slab gets that thickness), and
    // each side then shows its own face.
    // Candidate pairs: faces in one bin of normals and one slab of planes, swept along an axis; a bin is also read with its
    // neighbours (two faces a degree apart, as a fitted model's stretch tilts them, often fall into neighbouring bins) and, for
    // double-sided parts, with the bins facing the other way.
    public static class GeoCoplanarFaces
    {
        public const float Gap = 0.03f;
        public const float Tolerance = 0.015f;
        // faces that only touch at an edge or a corner do not overlap: each is shrunk this far towards its centre first
        const float Shrink = 0.03f;
        const float MinArea = 0.002f;
        const float SameNormal = 0.9995f;
        // bins of normals: a step of 1/Bins on each component, wider than the angle of SameNormal (1.8 degrees), so the faces
        // of a pair are in one bin or in neighbouring ones
        const int Bins = 20;
        // slabs of planes: faces closer than Tolerance are in one slab or in the next
        const float Slab = Tolerance * 2;
        const int Passes = 8;

        // One mesh of what is drawn together, as lists the caller owns: Separate appends the moved copies of vertices to them
        // and rewrites the triangle lists. Normals, tangents and texture coordinates may be empty; copyExtra keeps any further
        // per-vertex channel of the caller in step (called with the source index for each appended copy).
        public sealed class Part
        {
            public List<Vector3> positions, normals;
            public List<Vector4> tangents;
            public List<Vector2> uvs;
            public List<int>[] triangles;
            public bool movable = true;
            // drawn from behind too (TwoSided): its faces also fight faces in their plane that face the other way
            public bool twoSided;
            public Action<int> copyExtra;
        }

        public static bool TwoSided(Material material) => material && material.HasProperty("_Cull") && material.GetFloat("_Cull") == 0;

        struct Face
        {
            public int part, sub, first;
            public Vector3 a, b, c, normal, min, max;
            public float plane, area;
            // the face shrunk in its plane (Shrink), and laid flat along the axis its normal is nearest (any axis but one in
            // its plane keeps what overlaps), with its box; its extent along the axis its bin is swept on
            public Vector3 shrunkA, shrunkB, shrunkC;
            public int axis;
            public Vector2 flatA, flatB, flatC, flatMin, flatMax;
            public float sweepMin, sweepMax;
        }

        // Up to Passes passes: a patch moved in front of another can land on a third that was already Gap in front of it, which
        // the next pass finds. Returns the patches moved and the faces removed in all.
        public static (int moved, int removed) Separate(IList<Part> parts)
        {
            (int moved, int removed) total = (0, 0);
            for (int i = 0; i < Passes; i++)
            {
                var r = Pass(parts);
                total.moved += r.moved; total.removed += r.removed;
                if (r.moved == 0 && r.removed == 0) break;
            }
            return total;
        }

        static (int moved, int removed) Pass(IList<Part> parts)
        {
            int capacity = 0;
            foreach (var part in parts)
                if (part?.positions != null && part.triangles != null)
                    foreach (var list in part.triangles)
                        if (list != null) capacity += list.Count / 3;
            if (capacity < 2) return (0, 0);

            // corners numbered by position (to a tenth of a millimetre) across the parts
            // (packed in one number while within a hundred metres of the origin, as a building's own space is)
            var cornerOf = new Dictionary<(long, long, long), int>();
            var packedCornerOf = new Dictionary<long, int>();
            var vertexCorner = new int[parts.Count][];
            var faces = new Face[capacity];
            int count = 0;
            for (int p = 0; p < parts.Count; p++)
            {
                var part = parts[p];
                if (part?.positions == null || part.triangles == null) continue;
                var corners = vertexCorner[p] = new int[part.positions.Count];
                for (int v = 0; v < corners.Length; v++)
                {
                    var position = part.positions[v];
                    long x = (long)Mathf.Round(position.x * 10000), y = (long)Mathf.Round(position.y * 10000), z = (long)Mathf.Round(position.z * 10000);
                    int id;
                    if (Math.Abs(x) < 1L << 20 && Math.Abs(y) < 1L << 20 && Math.Abs(z) < 1L << 20)
                    {
                        long packed = (x + (1L << 20)) << 42 | (y + (1L << 20)) << 21 | (z + (1L << 20));
                        if (!packedCornerOf.TryGetValue(packed, out id)) packedCornerOf[packed] = id = packedCornerOf.Count + cornerOf.Count;
                    }
                    else if (!cornerOf.TryGetValue((x, y, z), out id)) cornerOf[(x, y, z)] = id = packedCornerOf.Count + cornerOf.Count;
                    corners[v] = id;
                }
                for (int s = 0; s < part.triangles.Length; s++)
                {
                    var list = part.triangles[s];
                    if (list == null) continue;
                    for (int i = 0; i + 2 < list.Count; i += 3)
                    {
                        Vector3 a = part.positions[list[i]], b = part.positions[list[i + 1]], c = part.positions[list[i + 2]];
                        var cross = Vector3.Cross(b - a, c - a);
                        float area = cross.magnitude * 0.5f;
                        if (area < MinArea) continue;
                        var normal = cross / (area * 2);
                        faces[count++] = new Face { part = p, sub = s, first = i, a = a, b = b, c = c, normal = normal, plane = Vector3.Dot(normal, a), area = area,
                                                    min = Vector3.Min(a, Vector3.Min(b, c)), max = Vector3.Max(a, Vector3.Max(b, c)) };
                    }
                }
            }
            if (count < 2) return (0, 0);

            // patches: faces of one part in one plane that share an edge (both of its corners), found through a chain of the
            // faces on each edge
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            var edgeHead = new Dictionary<long, int>(count * 2);
            var edgeNext = new int[count * 3];
            for (int f = 0; f < count; f++)
            {
                var corners = vertexCorner[faces[f].part];
                var list = parts[faces[f].part].triangles[faces[f].sub];
                int c0 = corners[list[faces[f].first]], c1 = corners[list[faces[f].first + 1]], c2 = corners[list[faces[f].first + 2]];
                for (int e = 0; e < 3; e++)
                {
                    int from = e == 0 ? c0 : e == 1 ? c1 : c2, to = e == 0 ? c1 : e == 1 ? c2 : c0;
                    long key = ((long)faces[f].part << 48) | ((long)Math.Min(from, to) << 24) | (long)Math.Max(from, to);
                    int slot = f * 3 + e;
                    if (edgeHead.TryGetValue(key, out int first))
                    {
                        for (int other = first; other >= 0; other = edgeNext[other])
                            if (Coplanar(in faces[f], in faces[other / 3], 0.0005f)) parent[Find(f)] = Find(other / 3);
                        edgeNext[slot] = first;
                    }
                    else edgeNext[slot] = -1;
                    edgeHead[key] = slot;
                }
            }

            // each face laid flat once (Overlap), in its bin of normals and its slab of planes (along its own normal), and in sweep
            // order within them (a building's floors and facades hold hundreds of faces in one plane: a pair is tested only where
            // their extents along the sweep axis meet)
            bool anyTwoSided = false;
            foreach (var part in parts) anyTwoSided |= part != null && part.twoSided;
            var order = new int[count];
            var keys = new long[count];
            for (int f = 0; f < count; f++)
            {
                ref var face = ref faces[f];
                var n = face.normal;
                var centre = (face.a + face.b + face.c) / 3;
                face.shrunkA = Toward(face.a, centre); face.shrunkB = Toward(face.b, centre); face.shrunkC = Toward(face.c, centre);
                face.axis = Mathf.Abs(n.x) >= Mathf.Abs(n.y) ? (Mathf.Abs(n.x) >= Mathf.Abs(n.z) ? 0 : 2) : (Mathf.Abs(n.y) >= Mathf.Abs(n.z) ? 1 : 2);
                Flatten(in face, face.axis, out face.flatA, out face.flatB, out face.flatC, out face.flatMin, out face.flatMax);
                int bin = BinKey(Mathf.RoundToInt(n.x * Bins), Mathf.RoundToInt(n.y * Bins), Mathf.RoundToInt(n.z * Bins));
                long slab = Mathf.FloorToInt(face.plane / Slab);
                keys[f] = ((long)bin << 40) | ((slab + (1L << 39)) & ((1L << 40) - 1));
                bool alongZ = AlongZ(bin);
                face.sweepMin = alongZ ? face.min.z : face.min.x;
                face.sweepMax = alongZ ? face.max.z : face.max.x;
                order[f] = f;
            }
            Array.Sort(keys, order);
            var mins = new float[count];
            for (int i = 0; i < count; i++) mins[i] = faces[order[i]].sweepMin;
            // runs: the faces of one bin and slab; the runs of a bin follow each other by slab
            var runStarts = new List<int>();
            for (int start = 0, end; start < count; start = end)
            {
                for (end = start + 1; end < count && keys[end] == keys[start]; end++) { }
                if (end - start > 1) Array.Sort(mins, order, start, end - start);
                runStarts.Add(start);
            }
            int runs = runStarts.Count;
            runStarts.Add(count);
            int RunBin(int r) => (int)(keys[runStarts[r]] >> 40);
            long RunSlab(int r) => (keys[runStarts[r]] & ((1L << 40) - 1)) - (1L << 39);
            var binRuns = new Dictionary<int, (int first, int end)>();
            for (int r = 0, e; r < runs; r = e)
            {
                for (e = r + 1; e < runs && RunBin(e) == RunBin(r); e++) { }
                binRuns[RunBin(r)] = (r, e);
            }

            var removed = new bool[count];
            int removedCount = 0;
            // faces that overlap another face of their own patch (a fold, a quad laid twice over a shared edge) move alone
            var facePush = new Dictionary<int, float>();
            // overlapping faces of different patches
            var conflicts = new List<(int, int)>();
            void Test(int p0, int q0)
            {
                int p = Math.Min(p0, q0), q = Math.Max(p0, q0);
                if (removed[p] || removed[q]) return;
                ref var fp = ref faces[p];
                ref var fq = ref faces[q];
                float facing = Vector3.Dot(fp.normal, fq.normal);
                bool opposite = facing < 0;
                if (Mathf.Abs(facing) < SameNormal || Mathf.Abs(opposite ? fp.plane + fq.plane : fp.plane - fq.plane) > Tolerance) return;
                if (opposite && !parts[fp.part].twoSided && !parts[fq.part].twoSided) return;
                if (!BoxesMeet(in fp, in fq) || !Overlap(in fp, in fq)) return;
                // faces facing opposite ways are never in one patch, and both are kept: each is the side seen from its way
                if (opposite)
                {
                    conflicts.Add((p, q));
                    return;
                }
                bool mp = parts[fp.part].movable, mq = parts[fq.part].movable;
                // an exact repeat shares every edge with the face it repeats, so it is in the same patch
                if (Same(in fp, in fq))
                {
                    if (mq) { removed[q] = true; removedCount++; }
                    else if (mp) { removed[p] = true; removedCount++; }
                    return;
                }
                if (Find(p) != Find(q))
                {
                    conflicts.Add((p, q));
                    return;
                }
                if (!mp) return;
                int alone = fp.area <= fq.area ? p : q, under = alone == p ? q : p;
                float lift = faces[under].plane + Gap - faces[alone].plane;
                if (lift > 0) facePush[alone] = Mathf.Max(facePush.TryGetValue(alone, out float l) ? l : 0, lift);
            }
            // within a run, then the run with the next slab of its bin
            for (int r = 0; r < runs; r++)
            {
                for (int a = runStarts[r], last = runStarts[r + 1]; a < last; a++)
                {
                    float reach = faces[order[a]].sweepMax + Tolerance;
                    for (int b = a + 1; b < last && mins[b] <= reach; b++) Test(order[a], order[b]);
                }
                if (r + 1 < runs && RunBin(r + 1) == RunBin(r) && RunSlab(r + 1) == RunSlab(r) + 1) Across(r, r + 1);
            }
            // a bin with each neighbouring bin, and (double-sided) with each bin around the opposite one: once a pair of bins
            foreach (var entry in binRuns)
            {
                int bin = entry.Key;
                int kx = (bin >> 12) - 32, ky = ((bin >> 6) & 63) - 32, kz = (bin & 63) - 32;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            int near = BinKey(kx + dx, ky + dy, kz + dz);
                            if (near > bin && binRuns.TryGetValue(near, out var runsNear)) Slabs(entry.Value, runsNear, false);
                            if (!anyTwoSided) continue;
                            int facing = BinKey(-kx + dx, -ky + dy, -kz + dz);
                            if (facing > bin && binRuns.TryGetValue(facing, out var runsFacing)) Slabs(entry.Value, runsFacing, true);
                        }
            }

            // The runs of two bins whose planes can be within Tolerance: the same slab or a next one, or for bins facing opposite
            // ways (a plane along the other normal is negated) slabs -s-2 to -s.
            void Slabs((int first, int end) a, (int first, int end) b, bool opposite)
            {
                int j = b.first;
                if (!opposite)
                    for (int i = a.first; i < a.end; i++)
                    {
                        long s = RunSlab(i);
                        while (j < b.end && RunSlab(j) < s - 1) j++;
                        for (int k = j; k < b.end && RunSlab(k) <= s + 1; k++) Across(i, k);
                    }
                else
                    for (int i = a.end - 1; i >= a.first; i--)
                    {
                        long s = RunSlab(i);
                        while (j < b.end && RunSlab(j) < -s - 2) j++;
                        for (int k = j; k < b.end && RunSlab(k) <= -s; k++) Across(i, k);
                    }
            }

            // Two runs, each in sweep order, merged as they are read; runs swept along different axes are tested pair by pair.
            void Across(int r1, int r2)
            {
                int aStart = runStarts[r1], aEnd = runStarts[r1 + 1], bStart = runStarts[r2], bEnd = runStarts[r2 + 1];
                if (AlongZ(RunBin(r1)) != AlongZ(RunBin(r2)))
                {
                    for (int i = aStart; i < aEnd; i++)
                        for (int k = bStart; k < bEnd; k++) Test(order[i], order[k]);
                    return;
                }
                for (int i = aStart, j = bStart; i < aEnd && j < bEnd;)
                {
                    if (mins[i] <= mins[j])
                    {
                        float reach = faces[order[i]].sweepMax + Tolerance;
                        for (int k = j; k < bEnd && mins[k] <= reach; k++) Test(order[i], order[k]);
                        i++;
                    }
                    else
                    {
                        float reach = faces[order[j]].sweepMax + Tolerance;
                        for (int k = i; k < aEnd && mins[k] <= reach; k++) Test(order[j], order[k]);
                        j++;
                    }
                }
            }

            // the patches in conflict, placed in turn: those that cannot move, then the largest first; each moves along its normal
            // until its faces are Gap in front of the overlapping faces of the patches placed before it (a plane along the other
            // normal is negated for a patch facing the other way)
            var push = new Dictionary<int, float>();
            if (conflicts.Count > 0)
            {
                var patchArea = new float[count];
                for (int f = 0; f < count; f++) patchArea[Find(f)] += faces[f].area;
                var ofPatch = new Dictionary<int, List<int>>();
                for (int i = 0; i < conflicts.Count; i++)
                {
                    var (p, q) = conflicts[i];
                    foreach (int root in new[] { Find(p), Find(q) })
                    {
                        if (!ofPatch.TryGetValue(root, out var list)) ofPatch[root] = list = new List<int>();
                        list.Add(i);
                    }
                }
                var turn = new List<int>(ofPatch.Keys);
                turn.Sort((x, y) =>
                {
                    bool mx = parts[faces[x].part].movable, my = parts[faces[y].part].movable;
                    return mx != my ? (mx ? 1 : -1) : patchArea[y].CompareTo(patchArea[x]);
                });
                var placed = new Dictionary<int, float>();
                foreach (int patch in turn)
                {
                    float shift = 0;
                    if (parts[faces[patch].part].movable)
                        foreach (int i in ofPatch[patch])
                        {
                            var (p, q) = conflicts[i];
                            int mine = Find(p) == patch ? p : q, theirs = mine == p ? q : p;
                            if (!placed.TryGetValue(Find(theirs), out float theirShift)) continue; // smaller: it moves in front of this one
                            float theirPlane = faces[theirs].plane + theirShift;
                            if (Vector3.Dot(faces[mine].normal, faces[theirs].normal) < 0) theirPlane = -theirPlane;
                            shift = Mathf.Max(shift, theirPlane + Gap - faces[mine].plane);
                        }
                    placed[patch] = shift;
                    if (shift > 0) push[patch] = shift;
                }
            }
            if (push.Count == 0 && facePush.Count == 0 && removedCount == 0) return (0, 0);

            // a moved patch gets its own copies of its vertices, shifted along its normal; a face moved alone its own too
            var moved = new HashSet<int>();
            var dropped = new HashSet<(int, int, int)>();
            var copies = new Dictionary<(int, int, int), int>();
            for (int f = 0; f < count; f++)
            {
                ref var face = ref faces[f];
                var part = parts[face.part];
                if (!part.movable) continue;
                if (removed[f]) { dropped.Add((face.part, face.sub, face.first)); continue; }
                // a face moved alone goes its lift in front of where its patch goes
                int key = facePush.TryGetValue(f, out float shift) ? -1 - f : Find(f);
                if (key < 0 && push.TryGetValue(Find(f), out float patchShift)) shift += patchShift;
                if (key >= 0 && !push.TryGetValue(key, out shift)) continue;
                var list = part.triangles[face.sub];
                for (int k = 0; k < 3; k++)
                {
                    int source = list[face.first + k];
                    if (!copies.TryGetValue((face.part, key, source), out int copy))
                    {
                        copy = part.positions.Count;
                        part.positions.Add(part.positions[source] + face.normal * shift);
                        if (part.normals != null && part.normals.Count > source) part.normals.Add(part.normals[source]);
                        if (part.tangents != null && part.tangents.Count > source) part.tangents.Add(part.tangents[source]);
                        if (part.uvs != null && part.uvs.Count > source) part.uvs.Add(part.uvs[source]);
                        part.copyExtra?.Invoke(source);
                        copies[(face.part, key, source)] = copy;
                    }
                    list[face.first + k] = copy;
                }
                moved.Add(key);
            }
            if (dropped.Count > 0)
                for (int p = 0; p < parts.Count; p++)
                {
                    var part = parts[p];
                    if (part?.triangles == null || !part.movable) continue;
                    for (int s = 0; s < part.triangles.Length; s++)
                    {
                        var list = part.triangles[s];
                        if (list == null) continue;
                        int write = 0;
                        for (int i = 0; i + 2 < list.Count; i += 3)
                        {
                            if (dropped.Contains((p, s, i))) continue;
                            list[write++] = list[i]; list[write++] = list[i + 1]; list[write++] = list[i + 2];
                        }
                        list.RemoveRange(write, list.Count - write);
                    }
                }
            return (moved.Count, removedCount);
        }

        static int BinKey(int x, int y, int z) => ((x + 32) << 12) | ((y + 32) << 6) | (z + 32);

        // a bin facing along x is swept along z, any other along x
        static bool AlongZ(int bin) => Math.Abs((bin >> 12) - 32) * 10 > Bins * 7;

        static bool BoxesMeet(in Face p, in Face q) =>
            p.min.x <= q.max.x + Tolerance && q.min.x <= p.max.x + Tolerance &&
            p.min.y <= q.max.y + Tolerance && q.min.y <= p.max.y + Tolerance &&
            p.min.z <= q.max.z + Tolerance && q.min.z <= p.max.z + Tolerance;

        static bool Coplanar(in Face p, in Face q, float tolerance) =>
            Vector3.Dot(p.normal, q.normal) >= SameNormal && Mathf.Abs(p.plane - q.plane) <= tolerance;

        static bool Same(in Face p, in Face q)
        {
            const float e = 1e-8f;
            Vector3 qa = q.a, qb = q.b, qc = q.c;
            bool Has(Vector3 v) => (qa - v).sqrMagnitude < e || (qb - v).sqrMagnitude < e || (qc - v).sqrMagnitude < e;
            return Has(p.a) && Has(p.b) && Has(p.c);
        }

        // The two faces, shrunk by Shrink, cross in p's plane: q is laid on it along p's normal, both are laid flat along p's
        // axis, their boxes meet, and no edge of either separates them. (Laid flat along the axis without the first step, two
        // sloping faces a centimetre apart slide past each other by most of that.)
        static bool Overlap(in Face p, in Face q)
        {
            var n = p.normal;
            var qa = Drop(q.shrunkA - n * (Vector3.Dot(n, q.shrunkA) - p.plane), p.axis);
            var qb = Drop(q.shrunkB - n * (Vector3.Dot(n, q.shrunkB) - p.plane), p.axis);
            var qc = Drop(q.shrunkC - n * (Vector3.Dot(n, q.shrunkC) - p.plane), p.axis);
            Vector2 qMin = Vector2.Min(qa, Vector2.Min(qb, qc)), qMax = Vector2.Max(qa, Vector2.Max(qb, qc));
            if (p.flatMax.x <= qMin.x || qMax.x <= p.flatMin.x || p.flatMax.y <= qMin.y || qMax.y <= p.flatMin.y) return false;
            return !Separated(p.flatA, p.flatB, p.flatC, qa, qb, qc, p.flatA, p.flatB, p.flatC) &&
                   !Separated(p.flatA, p.flatB, p.flatC, qa, qb, qc, qa, qb, qc);
        }

        // laid flat by dropping an axis: an affine map of the face's plane, so what overlaps there overlaps here
        static void Flatten(in Face face, int axis, out Vector2 a, out Vector2 b, out Vector2 c, out Vector2 min, out Vector2 max)
        {
            a = Drop(face.shrunkA, axis); b = Drop(face.shrunkB, axis); c = Drop(face.shrunkC, axis);
            min = Vector2.Min(a, Vector2.Min(b, c));
            max = Vector2.Max(a, Vector2.Max(b, c));
        }

        static Vector2 Drop(Vector3 v, int axis) => axis == 0 ? new Vector2(v.y, v.z) : axis == 1 ? new Vector2(v.x, v.z) : new Vector2(v.x, v.y);

        static Vector3 Toward(Vector3 v, Vector3 centre)
        {
            var d = centre - v;
            float length = d.magnitude;
            return length > 0 ? v + d * (Mathf.Min(Shrink, length * 0.5f) / length) : v;
        }

        static bool Separated(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 e0, Vector2 e1, Vector2 e2)
        {
            return Apart(p0, p1, p2, q0, q1, q2, e1 - e0) || Apart(p0, p1, p2, q0, q1, q2, e2 - e1) || Apart(p0, p1, p2, q0, q1, q2, e0 - e2);
        }

        static bool Apart(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 q0, Vector2 q1, Vector2 q2, Vector2 edge)
        {
            var axis = new Vector2(-edge.y, edge.x);
            float a = Vector2.Dot(p0, axis), b = Vector2.Dot(p1, axis), c = Vector2.Dot(p2, axis);
            float d = Vector2.Dot(q0, axis), e = Vector2.Dot(q1, axis), g = Vector2.Dot(q2, axis);
            float minP = Mathf.Min(a, Mathf.Min(b, c)), maxP = Mathf.Max(a, Mathf.Max(b, c));
            float minQ = Mathf.Min(d, Mathf.Min(e, g)), maxQ = Mathf.Max(d, Mathf.Max(e, g));
            return maxP <= minQ || maxQ <= minP;
        }
    }
}
