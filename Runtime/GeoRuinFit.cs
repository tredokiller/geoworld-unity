using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoWorld
{
    // Fits a ruin model onto a real footprint. The model is cut at its joints along its length, its depth and its
    // height; whole bays, boxes and storeys are repeated or left out until it is about as large as the building, and it is
    // stretched the rest of the way. The walls are fitted to the building's eaves and what stands above them (the roof,
    // or what is left of it) to the building's roof rise. Low yard and rubble far outside the footprint is left out, and
    // what lies on the ground follows the terrain. Everything is deterministic: the same building fits the same way.
    public static class GeoRuinFit
    {
        // Above this, debris outside the footprint is not "low" and is never trimmed (a lean-to's wall, a leaning fin).
        const float LowHeight = 1.5f;
        // How far the trimmed edge of a yard sinks, so that it ends under the terrain.
        const float EdgeSink = 0.15f;
        // Height over which ground-level parts follow the terrain; higher parts stay level.
        const float DrapeBand = 1.5f;
        // A piece of the mesh smaller than this (metres, across) is loose debris: trimmed whole or not at all.
        const float LoosePiece = 4f;
        const int MaxSegments = 512, MaxTiles = 24;

        // ------------------------------------------------------------------ vertex data

        // A mesh's vertex data as lists that can be cut and moved.
        public sealed class Shape
        {
            public readonly List<Vector3> positions, normals;
            public readonly List<Vector4> tangents;
            public readonly List<Vector2> uvs;
            public readonly List<int> triangles;

            public Shape(int vertices = 0, int indices = 0)
            {
                positions = new List<Vector3>(vertices); normals = new List<Vector3>(vertices);
                tangents = new List<Vector4>(vertices); uvs = new List<Vector2>(vertices); triangles = new List<int>(indices);
            }

            public int Add(Vector3 p, Vector3 n, Vector4 t, Vector2 uv)
            {
                positions.Add(p); normals.Add(n); tangents.Add(t); uvs.Add(uv);
                return positions.Count - 1;
            }

            public static Shape Of(Mesh mesh)
            {
                var shape = new Shape(mesh.vertexCount);
                mesh.GetVertices(shape.positions);
                mesh.GetNormals(shape.normals);
                mesh.GetTangents(shape.tangents);
                mesh.GetUVs(0, shape.uvs);
                int count = shape.positions.Count;
                if (shape.normals.Count != count) { shape.normals.Clear(); for (int i = 0; i < count; i++) shape.normals.Add(Vector3.up); }
                if (shape.tangents.Count != count) { shape.tangents.Clear(); for (int i = 0; i < count; i++) shape.tangents.Add(new Vector4(1, 0, 0, 1)); }
                if (shape.uvs.Count != count) { shape.uvs.Clear(); for (int i = 0; i < count; i++) shape.uvs.Add(Vector2.zero); }
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (mesh.GetTopology(s) == MeshTopology.Triangles) shape.triangles.AddRange(mesh.GetTriangles(s));
                return shape;
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name, indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(positions);
                mesh.SetNormals(normals);
                mesh.SetTangents(tangents);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        // Source models are read once per generation (a city fits the same twenty models thousands of times).
        static readonly Dictionary<Mesh, Shape> sources = new Dictionary<Mesh, Shape>();

        public static Shape Source(Mesh mesh)
        {
            if (!mesh) throw new ArgumentException("The ruin model has no mesh. Import the ruin models again.");
            if (sources.TryGetValue(mesh, out var shape) && shape.positions.Count == mesh.vertexCount) return shape;
            if (!mesh.isReadable) throw new ArgumentException("The ruin model's mesh " + mesh.name + " is not readable. Import the ruin models again.");
            return sources[mesh] = Shape.Of(mesh);
        }

        // Forget the source models read so far (after an import, or at the start of a generation).
        public static void ClearCache() => sources.Clear();

        // ------------------------------------------------------------------ planning one axis

        // One piece of the model along an axis: the source range [from, to] moved by offset; a mirrored piece is turned
        // end for end within its range first (every other copy of a model repeated whole, so neighbours meet end wall to
        // end wall like semi-detached houses, not as one picture printed over and over). A face lying exactly in a
        // piece's end plane belongs to it when that end is closed: a joint's plane to the piece above it, the footprint's
        // own ends (an end wall's outer face) to the segment there and never to the yard beyond.
        public struct Piece { public float from, to, offset; public bool mirror, openFrom, closedTo; }

        public sealed class AxisPlan
        {
            public readonly List<Piece> pieces = new List<Piece>();
            // Segment indices in output order (a repeated bay appears several times, a left-out one not at all).
            public readonly List<int> sequence = new List<int>();
            // Every segment between the footprint's start and end; uses[i] is how often segment i appears.
            public int[] uses;
            public string roles;
            // The footprint length after cutting, before stretching.
            public float length;
            public int tiles = 1;
            public int Added { get { int n = 0; foreach (int u in uses) n += Mathf.Max(0, u - 1); return n * tiles + (tiles - 1) * uses.Length; } }
            public int LeftOut { get { int n = 0; foreach (int u in uses) if (u == 0) n++; return n; } }
            public int FeaturesLeftOut { get { int n = 0; for (int i = 0; i < uses.Length; i++) if (uses[i] == 0 && roles[i] != 'R') n++; return n; } }
        }

        // The segment boundaries of an axis (footprint start, joints, footprint end) and one role letter per segment.
        // Joints that are out of order or outside the footprint make the whole axis one stretched segment.
        public static List<float> Segments(GeoRuinAxis axis, float start, float end, out string roles)
        {
            var bounds = new List<float> { start };
            roles = "F";
            if (axis == null || axis.joints == null || axis.joints.Length == 0) { bounds.Add(end); return bounds; }
            string letters = (axis.segments ?? "").ToUpperInvariant();
            bool valid = letters.Length == axis.joints.Length + 1;
            float previous = start;
            foreach (float joint in axis.joints)
            {
                if (!(joint > previous + 0.05f) || !(joint < end - 0.05f)) { valid = false; break; }
                previous = joint;
            }
            foreach (char letter in letters) if (letter != 'C' && letter != 'F' && letter != 'R') valid = false;
            if (!valid) { bounds.Add(end); return bounds; }
            bounds.AddRange(axis.joints);
            bounds.Add(end);
            roles = letters;
            return bounds;
        }

        public static bool IsValid(GeoRuinAxis axis, float start, float end)
        {
            Segments(axis, start, end, out string roles);
            return axis == null || axis.joints == null || axis.joints.Length == 0 || roles.Length == axis.joints.Length + 1;
        }

        // Plans one axis: which segments, how often, and where each piece goes. centre puts the middle of the result at
        // 0 (length and depth); otherwise it starts where the footprint starts (height: the ground stays at 0).
        // keepEnds keeps what lies beyond the footprint's end a separate piece, so a cut runs along that plane.
        // Features are left out only while the rest would still be squeezed by more than dropFeatures (squeezed storeys
        // show more than a squeezed facade, so height drops them sooner).
        public static AxisPlan Plan(GeoRuinAxis axis, float start, float end, float target, bool centre = true, bool keepEnds = false, float dropFeatures = 1.35f, bool tiles = true)
        {
            var plan = new AxisPlan();
            var bounds = Segments(axis, start, end, out string roles);
            int count = bounds.Count - 1;
            plan.roles = roles;
            plan.uses = new int[count];
            for (int i = 0; i < count; i++) plan.uses[i] = 1;
            var uses = plan.uses;
            bool open = axis != null && axis.openEnds;
            float middle = (start + end) * 0.5f;
            float Length(int i) => bounds[i + 1] - bounds[i];
            float FromMiddle(int i) => Mathf.Abs((bounds[i] + bounds[i + 1]) * 0.5f - middle);
            int Kept() { int n = 0; foreach (int u in uses) if (u > 0) n++; return n; }
            int Total() { int n = 0; foreach (int u in uses) n += u; return n; }
            float length = end - start;

            if (target > 0 && float.IsFinite(target) && Mathf.Abs(target - length) > 0.001f)
            {
                if (target > length)
                {
                    if (roles.IndexOf('R') >= 0)
                        while (Total() < MaxSegments)
                        {
                            // repeat the bay used least, the middle one first among equals
                            int best = -1;
                            for (int i = 0; i < count; i++)
                                if (roles[i] == 'R' && (best < 0 || uses[i] < uses[best] || (uses[i] == uses[best] && FromMiddle(i) < FromMiddle(best) - 0.001f))) best = i;
                            if (Mathf.Abs(length + Length(best) - target) >= Mathf.Abs(length - target)) break;
                            uses[best]++; length += Length(best);
                        }
                    else if (tiles && target > length * 1.5f)
                    {
                        // nothing repeats: a much longer building is a row of the whole model
                        plan.tiles = Mathf.Clamp(Mathf.RoundToInt(target / length), 1, MaxTiles);
                        length *= plan.tiles;
                    }
                }
                else
                {
                    if (open && count > 1)
                    {
                        // every joint is a wall: the run of boxes nearest the building's length is a closed model of its own
                        // (the one nearer the middle among equals)
                        int from = 0, to = count - 1; float bestError = float.MaxValue, bestMiddle = float.MaxValue;
                        for (int i = 0; i < count; i++)
                            for (int j = i; j < count; j++)
                            {
                                float run = bounds[j + 1] - bounds[i];
                                float error = Mathf.Abs(Mathf.Log(target / run)), off = Mathf.Abs((bounds[i] + bounds[j + 1]) * 0.5f - middle);
                                if (error < bestError - 0.001f || (error < bestError + 0.001f && off < bestMiddle - 0.001f)) { bestError = error; bestMiddle = off; from = i; to = j; }
                            }
                        for (int i = 0; i < count; i++) uses[i] = i >= from && i <= to ? 1 : 0;
                        length = bounds[to + 1] - bounds[from];
                    }
                    while (Kept() > 1)
                    {
                        // leave out the bay used most, the outermost first among equals
                        int best = -1;
                        for (int i = 0; i < count; i++)
                            if (roles[i] == 'R' && uses[i] > 0 && (best < 0 || uses[i] > uses[best] || (uses[i] == uses[best] && FromMiddle(i) > FromMiddle(best) + 0.001f))) best = i;
                        if (best < 0 || Mathf.Abs(length - Length(best) - target) >= Mathf.Abs(length - target)) break;
                        uses[best]--; length -= Length(best);
                    }
                    // a much smaller building: features go too, the outermost first (a model with open ends may also lose
                    // its end boxes), and something always remains
                    while (length > target * dropFeatures && Kept() > 1)
                    {
                        int best = -1;
                        for (int i = 0; i < count; i++)
                            if (uses[i] > 0 && (roles[i] == 'F' || (open && roles[i] == 'R')) && (best < 0 || FromMiddle(i) > FromMiddle(best) + 0.001f)) best = i;
                        if (best < 0 || Mathf.Abs(length - Length(best) - target) >= Mathf.Abs(length - target)) break;
                        uses[best]--; length -= Length(best);
                    }
                }
            }

            for (int tile = 0; tile < plan.tiles; tile++)
                for (int i = 0; i < count; i++)
                    for (int k = 0; k < uses[i]; k++) plan.sequence.Add(i);
            plan.length = 0;
            foreach (int i in plan.sequence) plan.length += Length(i);

            // pieces: what lies before the footprint, the segments in order (every other copy of a model repeated whole
            // turned end for end), what lies after it
            float cursor = centre ? -plan.length * 0.5f : start;
            var pieces = new List<Piece> { new Piece { from = float.NegativeInfinity, to = start, offset = cursor - start } };
            if (plan.tiles > 1)
                for (int tile = 0; tile < plan.tiles; tile++)
                {
                    pieces.Add(new Piece { from = start, to = end, offset = cursor - start, mirror = tile % 2 == 1, closedTo = true });
                    cursor += end - start;
                }
            else
                foreach (int i in plan.sequence)
                {
                    pieces.Add(new Piece { from = bounds[i], to = bounds[i + 1], offset = cursor - bounds[i], closedTo = i == count - 1 });
                    cursor += Length(i);
                }
            pieces.Add(new Piece { from = end, to = float.PositiveInfinity, offset = cursor - end, openFrom = true, closedTo = true });
            // Pieces that follow each other in the model as well join up, so a cut is made only where something was
            // repeated or left out. A cropped model starts or ends at a joint, and the yard that lay beyond the footprint
            // is put against its new end.
            foreach (var piece in pieces)
            {
                if (plan.pieces.Count > 0)
                {
                    var previous = plan.pieces[plan.pieces.Count - 1];
                    bool split = keepEnds && float.IsPositiveInfinity(piece.to);
                    if (!split && !previous.mirror && !piece.mirror && previous.to == piece.from && Mathf.Abs(previous.offset - piece.offset) < 0.0001f)
                    {
                        previous.to = piece.to; previous.closedTo = piece.closedTo;
                        plan.pieces[plan.pieces.Count - 1] = previous;
                        continue;
                    }
                }
                plan.pieces.Add(piece);
            }
            return plan;
        }

        // ------------------------------------------------------------------ cutting

        struct Corner
        {
            public Vector3 p, n; public Vector4 t; public Vector2 uv;
            public static Corner Lerp(Corner a, Corner b, float s)
            {
                var t = Vector3.Lerp(a.t, b.t, s);
                return new Corner
                {
                    p = Vector3.Lerp(a.p, b.p, s),
                    n = Vector3.Lerp(a.n, b.n, s).normalized,
                    t = new Vector4(t.x, t.y, t.z, 0).normalized + new Vector4(0, 0, 0, a.t.w),
                    uv = Vector2.Lerp(a.uv, b.uv, s)
                };
            }
        }

        // The shape put together from pieces of the source along one axis (0 x, 1 y, 2 z). Triangles that cross a cut
        // are clipped to it; a face lying in a cut plane belongs to one piece only (see Piece).
        public static Shape Cut(Shape source, int axis, List<Piece> pieces)
        {
            if (pieces.Count == 1 && float.IsNegativeInfinity(pieces[0].from) && float.IsPositiveInfinity(pieces[0].to) && pieces[0].offset == 0 && !pieces[0].mirror) return source;
            var result = new Shape(source.positions.Count * 2, source.triangles.Count * 2);
            var remap = new int[source.positions.Count];
            var polygon = new List<Corner>(8); var clipped = new List<Corner>(8);
            foreach (var piece in pieces)
            {
                for (int i = 0; i < remap.Length; i++) remap[i] = -1;
                var place = new Placement { axis = axis, offset = piece.offset, mirror = piece.mirror, sum = piece.from + piece.to };
                for (int t = 0; t < source.triangles.Count; t += 3)
                {
                    int a = source.triangles[t], b = source.triangles[t + 1], c = source.triangles[t + 2];
                    // a piece turned end for end is a mirror image: its triangles turn round
                    if (piece.mirror) { int swap = b; b = c; c = swap; }
                    float ca = source.positions[a][axis], cb = source.positions[b][axis], cc = source.positions[c][axis];
                    float min = Mathf.Min(ca, Mathf.Min(cb, cc)), max = Mathf.Max(ca, Mathf.Max(cb, cc));
                    bool whole;
                    if (max - min < 0.00001f)
                    {
                        // a face lying in the axis' plane belongs to the piece whose closed end it lies on
                        float at = (ca + cb + cc) / 3f;
                        bool fromSide = piece.openFrom ? at > piece.from + 0.0001f : at >= piece.from - 0.0001f;
                        bool toSide = piece.closedTo ? at <= piece.to + 0.0001f : at < piece.to - 0.0001f;
                        if (!fromSide || !toSide) continue;
                        whole = true;
                    }
                    else
                    {
                        if (max <= piece.from || min >= piece.to) continue;
                        whole = min >= piece.from && max <= piece.to;
                    }
                    if (whole)
                    {
                        result.triangles.Add(Keep(source, result, remap, a, place));
                        result.triangles.Add(Keep(source, result, remap, b, place));
                        result.triangles.Add(Keep(source, result, remap, c, place));
                        continue;
                    }
                    polygon.Clear();
                    polygon.Add(CornerOf(source, a)); polygon.Add(CornerOf(source, b)); polygon.Add(CornerOf(source, c));
                    if (min < piece.from) { Clip(polygon, clipped, axis, piece.from, true); Swap(ref polygon, ref clipped); }
                    if (max > piece.to) { Clip(polygon, clipped, axis, piece.to, false); Swap(ref polygon, ref clipped); }
                    if (polygon.Count < 3) continue;
                    int first = -1;
                    for (int k = 0; k < polygon.Count; k++)
                    {
                        var corner = place.Apply(polygon[k]);
                        int index = result.Add(corner.p, corner.n, corner.t, corner.uv);
                        if (first < 0) first = index;
                        if (k >= 2)
                        {
                            var p0 = result.positions[first]; var p1 = result.positions[index - 1]; var p2 = result.positions[index];
                            if (Vector3.Cross(p1 - p0, p2 - p0).sqrMagnitude < 1e-12f) continue;
                            result.triangles.Add(first); result.triangles.Add(index - 1); result.triangles.Add(index);
                        }
                    }
                }
            }
            return result;
        }

        static void Swap(ref List<Corner> a, ref List<Corner> b) { var t = a; a = b; b = t; }

        static Corner CornerOf(Shape s, int i) => new Corner { p = s.positions[i], n = s.normals[i], t = s.tangents[i], uv = s.uvs[i] };

        static int Keep(Shape source, Shape result, int[] remap, int i, Placement place)
        {
            if (remap[i] < 0)
            {
                var corner = place.Apply(CornerOf(source, i));
                remap[i] = result.Add(corner.p, corner.n, corner.t, corner.uv);
            }
            return remap[i];
        }

        // Where a piece goes along the cut axis: moved by offset, and turned end for end within its range first when
        // mirrored (sum is its start plus its end).
        struct Placement
        {
            public int axis; public float offset, sum; public bool mirror;
            public Corner Apply(Corner c)
            {
                if (mirror)
                {
                    c.p[axis] = sum - c.p[axis];
                    c.n[axis] = -c.n[axis];
                    var t = c.t; t[axis] = -t[axis]; t.w = -t.w; c.t = t;
                }
                c.p[axis] += offset;
                return c;
            }
        }

        // Sutherland-Hodgman against one plane: keeps the side above (or below) it.
        static void Clip(List<Corner> polygon, List<Corner> result, int axis, float plane, bool keepAbove)
        {
            result.Clear();
            for (int i = 0; i < polygon.Count; i++)
            {
                var current = polygon[i]; var next = polygon[(i + 1) % polygon.Count];
                float dc = (current.p[axis] - plane) * (keepAbove ? 1 : -1), dn = (next.p[axis] - plane) * (keepAbove ? 1 : -1);
                if (dc >= 0) result.Add(current);
                if ((dc >= 0) != (dn >= 0)) result.Add(Corner.Lerp(current, next, dc / (dc - dn)));
            }
        }

        // ------------------------------------------------------------------ the footprint's frame

        // The smallest rectangle around an outline: its centre, the unit direction of its longer side, and its sides.
        public struct Frame
        {
            public Vector2 centre, axis;
            public float length, width;
            // Share of the rectangle the outline fills: 1 for a rectangle, about 0.75 for a fat L.
            public float fill;
            public Vector2 Across => new Vector2(-axis.y, axis.x);
        }

        public static Frame FrameOf(IList<Vector2> outline)
        {
            var hull = Hull(outline);
            var best = new Frame { axis = Vector2.right, length = 0, width = 0 };
            float bestArea = float.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                var edge = hull[(i + 1) % hull.Count] - hull[i];
                if (edge.sqrMagnitude < 1e-8f) continue;
                var u = edge.normalized; var v = new Vector2(-u.y, u.x);
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                foreach (var p in hull)
                {
                    float du = Vector2.Dot(p, u), dv = Vector2.Dot(p, v);
                    minU = Mathf.Min(minU, du); maxU = Mathf.Max(maxU, du); minV = Mathf.Min(minV, dv); maxV = Mathf.Max(maxV, dv);
                }
                float area = (maxU - minU) * (maxV - minV);
                if (area >= bestArea - 1e-6f) continue;
                bestArea = area;
                var centre = u * ((minU + maxU) * 0.5f) + v * ((minV + maxV) * 0.5f);
                if (maxU - minU >= maxV - minV) best = new Frame { centre = centre, axis = u, length = maxU - minU, width = maxV - minV };
                else best = new Frame { centre = centre, axis = v, length = maxV - minV, width = maxU - minU };
            }
            // one direction for a given axis, so a rebuild never turns the model round
            if (best.axis.x < -1e-6f || (Mathf.Abs(best.axis.x) <= 1e-6f && best.axis.y < 0)) best.axis = -best.axis;
            best.fill = bestArea > 1e-6f && bestArea < float.MaxValue ? Mathf.Clamp01(Mathf.Abs(GeoPolygon.Area(outline)) / bestArea) : 0;
            return best;
        }

        static List<Vector2> Hull(IList<Vector2> points)
        {
            var sorted = new List<Vector2>(points);
            sorted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var hull = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int k = 0; k < sorted.Count; k++)
                {
                    var p = sorted[pass == 0 ? k : sorted.Count - 1 - k];
                    while (hull.Count >= start + 2 && GeoPolygon.Cross(hull[hull.Count - 1] - hull[hull.Count - 2], p - hull[hull.Count - 2]) <= 0) hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

        // ------------------------------------------------------------------ fitting a model

        // What a building asks of its ruin, in the building's local space.
        public sealed class Target
        {
            public Frame frame;
            // The building's total height, its walls (eaves) and its roof rise (0 for a flat roof).
            public float height, eaves, roofRise;
            public bool mirror, turn;
            // Yard and debris kept around the footprint, at most: this, or more for a big building (a fifth of its mean
            // side: a block's debris fans need more room than a house's).
            public float apron = 4;
            // The apron of a fitted model: its own, grown or shrunk with the plan, within the limit above.
            public float Apron(GeoRuinModel model, Fit fit) =>
                Mathf.Max(0, Mathf.Min(model.apron * Mathf.Sqrt(fit.stretch.x * fit.stretch.z), Mathf.Max(apron, 0.2f * Mathf.Sqrt(frame.length * frame.width))));
            // Terrain height relative to the building's base at a local point (x, z), or null on flat ground.
            public Func<Vector2, float> ground;
        }

        // How a model was fitted: bays and storeys added or left out, and the stretch that remained. length, width and
        // height are the plans along the model's X, Z and Y, and stretch is in the model's axes too. swapped: the model is
        // longer in Z than in X (a theatre deeper than wide), so its Z runs along the footprint's long side.
        public struct Fit
        {
            public AxisPlan length, width, height;
            public Vector3 stretch;
            public float roofStretch;
            public bool swapped;
            public override string ToString()
            {
                string Axis(string name, AxisPlan plan) => plan.Added > 0 ? name + " +" + plan.Added : plan.LeftOut > 0 ? name + " -" + plan.LeftOut : null;
                var changes = new List<string>();
                foreach (var change in new[] { Axis("bays", length), Axis("depth", width), Axis("storeys", height) }) if (change != null) changes.Add(change);
                return (changes.Count == 0 ? "as modelled" : string.Join(", ", changes)) +
                       $"; stretched {stretch.x:F2} x {stretch.z:F2} x {stretch.y:F2} (length x depth x walls), roof {roofStretch:F2}";
            }
        }

        // The heights the model's walls and roof are fitted to. Both pitched: walls to the eaves, the roof to the rise.
        // A flat-roofed building's walls are its whole height: a pitched model's roof (or what is left of it) then keeps
        // the walls' proportions, and a flat model keeps its parapets and roof rooms as they are.
        public static void Heights(GeoRuinModel model, Target target, out float eaves, out float roofRise)
        {
            bool modelPitched = model.roofRise > 0.3f, buildingPitched = target.roofRise > 0.3f;
            if (modelPitched && buildingPitched) { eaves = target.eaves; roofRise = target.roofRise; }
            else if (modelPitched) { eaves = target.height; roofRise = model.roofRise * target.height / Mathf.Max(0.5f, model.eaves); }
            else { eaves = target.height; roofRise = 0; }
            eaves = Mathf.Max(0.5f, eaves);
        }

        // Plans all three axes without touching the mesh (to compare models before choosing one).
        public static Fit Measure(GeoRuinModel model, Target target)
        {
            // the model's long side goes along the footprint's long side
            bool swapped = model.LongAlongZ;
            float toX = swapped ? target.frame.width : target.frame.length, toZ = swapped ? target.frame.length : target.frame.width;
            // whole copies only in a row along the footprint, never one behind the other, and never of a one-of-a-kind
            // building (two churches back to back)
            bool tiles = !model.keepProportions;
            var fit = new Fit
            {
                swapped = swapped,
                length = Plan(model.length, model.footprintMin.x, model.footprintMax.x, toX, tiles: tiles && !swapped),
                width = Plan(model.width, model.footprintMin.y, model.footprintMax.y, toZ, tiles: tiles && swapped)
            };
            float sx = toX / Mathf.Max(0.01f, fit.length.length), sz = toZ / Mathf.Max(0.01f, fit.width.length);
            float eaves, rise;
            if (model.keepProportions)
            {
                // a dome or a headframe grows with the plan, whatever the data says the height is
                float scale = Mathf.Sqrt(sx * sz);
                eaves = model.eaves * scale; rise = model.roofRise * scale;
            }
            else Heights(model, target, out eaves, out rise);
            fit.height = Plan(model.height, 0, model.eaves, eaves, false, true, 1.15f);
            fit.stretch = new Vector3(sx, eaves / Mathf.Max(0.01f, fit.height.length), sz);
            fit.roofStretch = model.keepProportions ? fit.stretch.y : model.roofRise > 0.3f ? Mathf.Clamp(rise / model.roofRise, 0.4f, 2.5f) : 1f;
            return fit;
        }

        // The model fitted to the target, in the building's local space.
        public static Mesh Build(GeoRuinModel model, Target target, out Fit fit)
        {
            if (!model || !model.Usable) throw new ArgumentException("The ruin model is incomplete. Import the ruin models again.");
            if (!(target.frame.length > 0.1f) || !(target.frame.width > 0.1f)) throw new ArgumentException("The footprint is too small for a ruin model.");
            fit = Measure(model, target);
            // a merged city's bake asks for the reduced copy (GeoModelDetail); it has the source's own vertices
            var shape = Source(GeoModelDetail.For(model.mesh));
            shape = Cut(shape, 0, fit.length.pieces);
            shape = Cut(shape, 2, fit.width.pieces);
            shape = Cut(shape, 1, fit.height.pieces);

            float sx = fit.stretch.x, sz = fit.stretch.z, sWall = fit.stretch.y, sRoof = fit.roofStretch;
            float top = fit.height.length; // the eaves after storeys were added or left out
            // the footprint's half sides along the model's X and Z
            float halfX = (fit.swapped ? target.frame.width : target.frame.length) * 0.5f, halfZ = (fit.swapped ? target.frame.length : target.frame.width) * 0.5f;
            float margin = target.Apron(model, fit);
            int count = shape.positions.Count;

            // the roof is what lies above the eaves; the cut along that plane gives every triangle one side
            var roof = new bool[count];
            for (int t = 0; t < shape.triangles.Count; t += 3)
            {
                int a = shape.triangles[t], b = shape.triangles[t + 1], c = shape.triangles[t + 2];
                if ((shape.positions[a].y + shape.positions[b].y + shape.positions[c].y) / 3f > top) roof[a] = roof[b] = roof[c] = true;
            }

            var frame = target.frame; var along = frame.axis; var across = frame.Across;
            var outside = new float[count];
            var planar = new Vector2[count];
            var result = new Shape(count, shape.triangles.Count);
            for (int i = 0; i < count; i++)
            {
                var p = shape.positions[i]; var n = shape.normals[i]; var t = shape.tangents[i];
                float sy = roof[i] ? sRoof : sWall;
                float y = roof[i] ? top * sWall + (p.y - top) * sRoof : p.y * sWall;
                float x = p.x * sx, z = p.z * sz;
                var normal = new Vector3(n.x / sx, n.y / sy, n.z / sz).normalized;
                var tangent = new Vector3(t.x * sx, t.y * sy, t.z * sz).normalized;
                float w = t.w;
                // yard and rubble beyond the footprint sink a little towards where they are trimmed
                float dx = Mathf.Max(0, Mathf.Abs(x) - halfX), dz = Mathf.Max(0, Mathf.Abs(z) - halfZ);
                outside[i] = Mathf.Sqrt(dx * dx + dz * dz);
                planar[i] = new Vector2(x, z);
                if (y < LowHeight && outside[i] > 0 && margin > 0) y -= EdgeSink * Mathf.SmoothStep(0, 1, outside[i] / margin);
                if (target.mirror) { x = -x; normal.x = -normal.x; tangent.x = -tangent.x; w = -w; }
                if (target.turn) { x = -x; z = -z; normal.x = -normal.x; normal.z = -normal.z; tangent.x = -tangent.x; tangent.z = -tangent.z; }
                // model X along the footprint's long side and Z across it, or (swapped) Z along and -X across: a turn,
                // never a mirror
                float u = fit.swapped ? z : x, v = fit.swapped ? -x : z;
                float nu = fit.swapped ? normal.z : normal.x, nv = fit.swapped ? -normal.x : normal.z;
                float tu = fit.swapped ? tangent.z : tangent.x, tv = fit.swapped ? -tangent.x : tangent.z;
                var flat = frame.centre + along * u + across * v;
                var local = new Vector3(flat.x, y, flat.y);
                if (target.ground != null)
                {
                    float g = target.ground(flat);
                    if (float.IsFinite(g)) local.y += g * (1 - Mathf.SmoothStep(0, 1, Mathf.Clamp01(y / DrapeBand)));
                }
                result.Add(local,
                    new Vector3(along.x * nu + across.x * nv, normal.y, along.y * nu + across.y * nv),
                    new Vector4(along.x * tu + across.x * tv, tangent.y, along.y * tu + across.y * tv, w),
                    shape.uvs[i]);
            }

            // What lies beyond the apron is left out. A loose piece (a chunk, a door leaf, a slab) goes or stays whole, by
            // where its middle lies, so nothing is left floating; the yard, mounds and walls are trimmed triangle by
            // triangle, and only where they are low (a lean-to's wall or a leaning fin stays).
            var piece = PiecesOf(shape, out int pieces);
            var lowest = new Vector2[pieces]; var highest = new Vector2[pieces];
            var farOut = new float[pieces]; var members = new int[pieces];
            for (int k = 0; k < pieces; k++) { lowest[k] = Vector2.one * float.MaxValue; highest[k] = Vector2.one * float.MinValue; }
            for (int i = 0; i < count; i++)
            {
                int k = piece[i];
                lowest[k] = Vector2.Min(lowest[k], planar[i]); highest[k] = Vector2.Max(highest[k], planar[i]);
                farOut[k] += outside[i]; members[k]++;
            }
            var used = new int[count];
            for (int i = 0; i < count; i++) used[i] = -1;
            var kept = new Shape(count, shape.triangles.Count);
            for (int t = 0; t < shape.triangles.Count; t += 3)
            {
                int a = shape.triangles[t], b = shape.triangles[t + 1], c = shape.triangles[t + 2];
                int k = piece[a];
                var size = highest[k] - lowest[k];
                if (Mathf.Max(size.x, size.y) < LoosePiece)
                {
                    if (farOut[k] / Mathf.Max(1, members[k]) > margin) continue;
                }
                else
                {
                    bool far = outside[a] > margin && outside[b] > margin && outside[c] > margin;
                    bool low = shape.positions[a].y * sWall < LowHeight && shape.positions[b].y * sWall < LowHeight && shape.positions[c].y * sWall < LowHeight;
                    if (far && low) continue;
                }
                if (target.mirror) { int swap = b; b = c; c = swap; }
                foreach (int v in new[] { a, b, c })
                {
                    if (used[v] < 0) used[v] = kept.Add(result.positions[v], result.normals[v], result.tangents[v], result.uvs[v]);
                    kept.triangles.Add(used[v]);
                }
            }
            if (kept.triangles.Count == 0) throw new ArgumentException("The ruin model left nothing after fitting.");
            return kept.ToMesh("Ruin " + model.id);
        }

        // Which piece of the shape each vertex belongs to: triangles that touch, also where a box's faces have vertices of
        // their own at the same corner.
        static int[] PiecesOf(Shape shape, out int count)
        {
            int n = shape.positions.Count;
            var welded = new Dictionary<Vector3Int, int>(n);
            var parent = new List<int>(n);
            var weld = new int[n];
            for (int i = 0; i < n; i++)
            {
                var p = shape.positions[i];
                var key = new Vector3Int(Mathf.RoundToInt(p.x * 500), Mathf.RoundToInt(p.y * 500), Mathf.RoundToInt(p.z * 500));
                if (!welded.TryGetValue(key, out int w)) { w = parent.Count; parent.Add(w); welded[key] = w; }
                weld[i] = w;
            }
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int t = 0; t < shape.triangles.Count; t += 3)
            {
                int a = Find(weld[shape.triangles[t]]);
                int b = Find(weld[shape.triangles[t + 1]]); if (b != a) parent[b] = a;
                int c = Find(weld[shape.triangles[t + 2]]); if (c != a) parent[c] = a;
            }
            var number = new Dictionary<int, int>();
            var result = new int[n];
            for (int i = 0; i < n; i++)
            {
                int root = Find(weld[i]);
                if (!number.TryGetValue(root, out int k)) { k = number.Count; number[root] = k; }
                result[i] = k;
            }
            count = number.Count;
            return result;
        }
    }
}
