using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoWorld.Editor
{
    // A trench's cross-section as it is drawn in 3D (GeoTrenchMesh), d metres from its centreline, relative to the ground
    // before it was dug: a floor at the depth, walls up to the top edge (steep as dug; old, the wall slumps: its foot spreads
    // into the floor and its top edge rounds over), then the spoil thrown out on both sides: a low heap from the edge up to a
    // round crest and down to its toe, where it runs out into the ground level and flat (Profile), so the ground around it
    // carries on without a crease; past the toe an apron at the ground level over the terrain the cut under the slot may have
    // lowered, then the mesh turns down under the terrain. A height grid of about a metre cannot hold a slot 1.3-1.5 m across
    // with steep walls, so the terrain under the slot is cut below it (GeoCombatGround) and the mesh draws the trench, its
    // spoil and the apron with the terrain's own material (GeoTrenchGround). bank and spread are a trench's mean heap: the
    // mesh's is lower or higher, nearer or farther along the trench and on either side, never below the ground.
    public readonly struct GeoTrenchSection
    {
        public readonly float half, floorHalf, depth, bank, spread, crest, apron, lipEnd, tuckEnd, slump;

        // the crest's place, as a share of the way from the edge to the toe; the heap's height at the edge, as a share of
        // the crest's; the widest a heap spreads, as a share of the mean; the apron past the toe at least; the turn-down
        public const float CrestShare = 0.3f, LipShare = 0.45f, WidestSpread = 1.35f, ApronMin = 0.15f, TuckWidth = 0.3f;
        // how far the mesh's slot may stand off the centreline: its walls sway along the trench (Wobble) and their top edge
        // crumbles outward (LipCrumble); the cut under the slot reaches that much farther
        public const float Wobble = 0.08f, LipCrumble = 0.07f, Sway = Wobble + LipCrumble;

        public GeoTrenchSection(float width, float depth, float age, float lip)
        {
            age = Mathf.Clamp01(age);
            half = GeoTrenchProfile.HalfWidth(width, age);
            this.depth = GeoTrenchProfile.Depth(depth, age);
            // the walls' run: 6 cm per metre of depth as dug, 36 cm when they have slumped for years
            floorHalf = Mathf.Max(0.18f, half - this.depth * (0.06f + 0.3f * age));
            // the heap: about a fifth of the depth high at its crest (most of the earth is spread wide or carried off to hide
            // the line), its toe two metres past the edge; an old one worn lower and spread wider
            bank = Mathf.Clamp(0.21f * Mathf.Max(0, depth), 0.12f, 0.4f) * (1 - 0.4f * age);
            spread = 1.9f + 0.9f * age;
            crest = half + CrestShare * spread;
            apron = Mathf.Max(0, lip);
            lipEnd = half + Mathf.Max(spread * WidestSpread + ApronMin, apron);
            tuckEnd = lipEnd + TuckWidth;
            slump = age;
        }

        GeoTrenchSection(GeoTrenchSection s, float bankScale)
        {
            half = s.half; floorHalf = s.floorHalf; depth = s.depth; spread = s.spread; crest = s.crest; apron = s.apron;
            lipEnd = s.lipEnd; tuckEnd = s.tuckEnd; slump = s.slump;
            bank = s.bank * bankScale;
        }

        // the same section with its spoil scaled (the banks taper round a trench's end to EndBank of their height)
        public GeoTrenchSection Banks(float scale) => new GeoTrenchSection(this, scale);
        public const float EndBank = 0.15f;

        public static GeoTrenchSection Of(TrenchFeature t, float cell) => new GeoTrenchSection(t.width, t.depth, t.age, LipWidth(cell));

        // How far past the trench's edge its mesh reaches at least: the cut lowers the terrain nodes within a cell's
        // diagonal of the slot (Cap), so the terrain triangles they are corners of reach two diagonals past the edge; the
        // mesh lies over all of them.
        public static float LipWidth(float cell) => 2 * cell * 1.4143f + 0.1f;

        // How far from the centreline the terrain's grass is cleared (it would grow up through the slot's walls and the
        // spoil's thick inner part, GroundDressing); beyond it the grass comes up through the spoil's thin outer part.
        public float CutTo(float cell) => half + cell * 1.4143f + 0.2f;

        // The spoil's height share t of the way from the trench's edge (0) to its toe (1), crest: the crest's share of the
        // way. From LipShare at the edge it rises to the crest, its top round (no slope there), then falls to nothing at the
        // toe, flat there too (a biweight: no value and no slope at the toe), so it runs out into the ground without a crease.
        public static float Profile(float t, float crest)
        {
            if (t >= 1) return 0;
            if (t <= crest)
            {
                float u = 1 - Mathf.Max(0, t) / crest;
                return LipShare + (1 - LipShare) * (1 - u * u);
            }
            float v = (t - crest) / (1 - crest), w = 1 - v * v;
            return w * w;
        }

        // the mean heap at d (d >= half)
        public float Spoil(float d) => bank * Profile((d - half) / spread, CrestShare);

        // The lowest the mesh's surface stands at d, relative to the ground: the floor, the wall up to the ground level at
        // the edge, then the spoil and the apron, which stand above the ground (0). Never falls as d grows.
        public float Surface(float d)
        {
            if (d <= floorHalf) return -depth;
            if (d < half)
            {
                float t = (d - floorHalf) / (half - floorHalf);
                // an old wall's foot and top are rounded: a smoothstep instead of the straight face it was dug with
                t = Mathf.Lerp(t, t * t * (3 - 2 * t), slump);
                return Mathf.Lerp(-depth, 0, t);
            }
            return 0;
        }

        // The highest a terrain node d metres from the centreline may stand (relative to its ground) so that no terrain
        // triangle it is a corner of rises through the slot: the lowest surface within a cell's diagonal (and the slot's
        // Sway) of it (Surface never falls, so that is the nearest point's). Infinity when its triangles all lie past the
        // edge, under the spoil and the apron, which stand above the ground: the terrain keeps its height there.
        public float Cap(float d, float diagonal)
        {
            float a = Mathf.Max(0, d - diagonal - Sway);
            return a >= half ? float.PositiveInfinity : Surface(a);
        }
    }

    // Trenches as meshes on a tile, each centreline swept by its cross-section (GeoTrenchSection) a cross-section every
    // metre, each side on its own: at a corner the outer side turns round it (a fan of cross-sections), the inner side meets
    // the next segment's at the mitre (a point of it that would fold behind the corner stays at the mitre). A trench ends in
    // a rounded end wall with its banks fading round it, or, where it leaves another trench (a communication trench, a
    // dugout), it is cut at the other's floor and opens into it: the other gets cross-sections where its walls are crossed,
    // and its lip, banks and walls inside this one are left out. Each side's heap (Heap) swells and shrinks along the trench,
    // its toe nearer or farther, higher on one side (the parapet), with clods on it (Rough) that die out toward the toe. Sub-mesh
    // 0 is the ground (floor, spoil and apron: the terrain's material, texture coordinates the terrain's; UV1 x how much is the
    // trench's own soil — mud on the floor, y 1, spoil on the heaps, y 0, fading out to the terrain's look before the toe), 1
    // the walls (the wall atlas of Textures/Trenches: x along the wall a strip's 4 m, y up the wall within its strip of bare
    // soil, boards or wattle); with groundPasses > 1, sub-meshes 2.. are the ground's triangles again, for a terrain's further
    // passes (a terrain with more than four layers draws them four at a time, GeoTrenchGround).
    public static class GeoTrenchMesh
    {
        // The terrain under a trench's slot stands this far below it.
        public const float Clearance = 0.06f;
        // the spoil and the apron stand this far above the ground: the terrain under them keeps its height, and its triangles
        // stand off the ground the mesh is laid on (sampled bilinearly) by a centimetre or two where it bends
        public const float Lift = 0.03f;
        // the clods on fresh spoil (m, either way at the crest), on old
        const float FreshClods = 0.045f, OldClods = 0.02f;
        const float Step = 1f;
        const int CapSpokes = 10;
        const float FanStep = 12f * Mathf.Deg2Rad;
        // the wall atlas: strips 0 and 1 bare soil, 2 boards, 3 wattle, 4 round timber (a dugout's roof logs); each 4 m along
        public const int WallStrips = 5;
        const int BoardStrip = 2, LogStrip = 4;
        const float StripLength = 4f;

        public sealed class Chunk
        {
            public Vector2Int key;
            public Mesh mesh;
        }

        sealed class Line
        {
            public TrenchFeature feature;
            public GeoTrenchSection section;
            public List<Vector2> points = new List<Vector2>();
            public Rect bounds;
            public int seed;
            public bool openStart, openEnd;
            // an open end's stretch inside the other trench (arc before openFrom, after openTo): only its floor is drawn
            public float openFrom, openTo = float.PositiveInfinity;
            // arc positions where another trench's walls cross this one's (cross-sections there: crisp openings)
            public readonly List<float> extra = new List<float>();
            // arc positions where the walls must stand where the centreline says (junctions, open and closed ends): the sway
            // dies out toward them, so an opening meets the other trench's walls and an end its rounded cap
            public readonly List<float> calm = new List<float>();
            public float length;
            // how much of the banks is bare spoil (fresh 1, grown over less)
            public float soil;
            // which side's bank is the higher (the parapet), and by how much
            public float parapetSide, parapet;
        }

        // The other trenches near the one being swept. What of its lip, spoil and walls lies inside one of them (between that
        // one's walls' top edges) is cut away exactly along that wall line (Strip), so it opens into the other without shards.
        sealed class Others
        {
            public readonly List<Line> lines = new List<Line>();

            // the wall line of the other trench p is inside of: a point on it, and its normal pointing out of that trench
            public bool Inside(Vector2 p, out Vector2 onLine, out Vector2 outward)
            {
                onLine = outward = default;
                foreach (var other in lines)
                {
                    if (!other.bounds.Contains(p) || !Nearest(other, p, out var closest, out var along)) continue;
                    float d = Vector2.Distance(p, closest);
                    if (d >= other.section.half - 0.02f) continue;
                    outward = d > 1e-4f ? (p - closest) / d : Left(along);
                    onLine = closest + outward * other.section.half;
                    return true;
                }
                return false;
            }
        }

        sealed class Draft
        {
            public readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>(), soil = new List<Vector2>();
            // the ground's triangles, the turn-down's (ground too, drawn after the rest), the walls'
            public readonly List<int> ground = new List<int>(), tuck = new List<int>(), walls = new List<int>();
        }

        // The ground's normals from the faces round each point, whichever strip, row or trench they belong to (points closer
        // than 2 mm are one): the strips' own normals broke along their seams — the mitre of an inner corner, a fan's edges,
        // a clipped junction — and the surface shaded in pieces. The turn-down keeps the apron's normals (its top shows).
        static void SmoothGround(Draft draft)
        {
            var sums = new Dictionary<Vector3Int, Vector3>();
            Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.RoundToInt(p.x * 500), Mathf.RoundToInt(p.y * 500), Mathf.RoundToInt(p.z * 500));
            var t = draft.ground;
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                Vector3 a = draft.vertices[t[i]], b = draft.vertices[t[i + 1]], c = draft.vertices[t[i + 2]];
                var n = Vector3.Cross(b - a, c - a); // its length weighs it by area
                if (n.y <= 0) continue;
                for (int k = 0; k < 3; k++)
                {
                    var key = Key(draft.vertices[t[i + k]]);
                    sums[key] = sums.TryGetValue(key, out var sum) ? sum + n : n;
                }
            }
            foreach (int v in t)
                if (sums.TryGetValue(Key(draft.vertices[v]), out var sum) && sum.sqrMagnitude > 1e-12f) draft.normals[v] = sum.normalized;
        }

        // A grid of vertices (rows along the trench, columns across): positions, and normals from the grid turned to the side
        // the surface faces (the ground up, a wall toward the trench's middle), so neighbouring stretches share them. A normal
        // looks across only at the columns first..last of the strip it is drawn in (the floor's edge does not lean toward the
        // wall's top, the apron does not lean toward the turn-down under the terrain).
        sealed class Grid
        {
            public readonly int rows, columns;
            public readonly Vector3[] position, facing;
            public readonly Vector2[] uv, soil;
            public Grid(int rows, int columns)
            {
                this.rows = rows; this.columns = columns;
                position = new Vector3[rows * columns]; facing = new Vector3[rows * columns];
                uv = new Vector2[rows * columns]; soil = new Vector2[rows * columns];
            }
            public Vector3 Normal(int r, int c, int first, int last)
            {
                Vector3 along = position[Mathf.Min(r + 1, rows - 1) * columns + c] - position[Mathf.Max(r - 1, 0) * columns + c];
                Vector3 across = position[r * columns + Mathf.Min(c + 1, last)] - position[r * columns + Mathf.Max(c - 1, first)];
                var n = Vector3.Cross(across, along);
                var hint = facing[r * columns + c];
                if (n.sqrMagnitude < 1e-10f) return hint.sqrMagnitude > 0 ? hint.normalized : Vector3.up;
                n.Normalize();
                return Vector3.Dot(n, hint) < 0 ? -n : n;
            }
        }

        struct Corner
        {
            public bool real;
            public Vector2 at, bisector;   // bisector: toward the inner side
            public float tanHalf, mitre;
            public int inner;              // the side (+1 left, -1 right) on the inside of the turn
        }

        // a row of one side: where on the centreline, which way this side's offsets go, and the segment it lies on
        struct Row
        {
            public Vector2 at, normal;
            public float arc, along, scale;
            public int segment;
            public bool mitred;
        }

        // The meshes of the trenches whose stretches lie in area (a stretch by its middle, an end by its point), one per
        // chunk of chunkSize metres. ground: the height before the trenches were cut; terrain: after (the mesh's edge turns
        // under it). uvArea: the terrain the ground's texture coordinates refer to. cell: the height grid's spacing.
        // groundPasses: how many passes the terrain draws its layers in (four layers each); the ground's triangles are in
        // sub-mesh 0 and again in one more sub-mesh (from 2) for every further pass.
        public static List<Chunk> Build(IList<TrenchFeature> trenches, Func<float, float, float> ground, Func<float, float, float> terrain,
            Rect area, Rect uvArea, float cell, float chunkSize, int groundPasses = 1)
        {
            var lines = new List<Line>();
            if (trenches != null)
                foreach (var t in trenches)
                {
                    if (t?.points == null || t.points.Length < 4 || !(t.depth > 0)) continue;
                    var line = new Line { feature = t, section = GeoTrenchSection.Of(t, cell), points = Centreline(t) };
                    if (line.points.Count < 2) continue;
                    line.seed = Mathf.FloorToInt(line.points[0].x * 7.31f) * 73856093 ^ Mathf.FloorToInt(line.points[0].y * 5.17f) * 19349663;
                    line.soil = 1 - 0.45f * Mathf.Clamp01(t.age);
                    line.parapetSide = Hash(line.seed, 1, 3) < 0.5f ? 1 : -1;
                    line.parapet = 0.2f + 0.35f * Hash(line.seed, 2, 3);
                    lines.Add(line);
                }
            Junctions(lines);
            foreach (var line in lines)
            {
                line.length = Length(line.points);
                line.calm.Add(0); line.calm.Add(line.length);
                line.calm.AddRange(line.extra);
                if (line.openStart) line.calm.Add(line.openFrom);
                if (line.openEnd) line.calm.Add(line.openTo);
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var p in line.points) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y); }
                float r = line.section.tuckEnd + 1;
                line.bounds = Rect.MinMaxRect(minX - r, minZ - r, maxX + r, maxZ + r);
            }
            var drafts = new Dictionary<Vector2Int, Draft>();
            chunkSize = Mathf.Max(50, chunkSize);
            Draft DraftAt(Vector2 p)
            {
                var key = new Vector2Int(Mathf.FloorToInt(p.x / chunkSize), Mathf.FloorToInt(p.y / chunkSize));
                if (!drafts.TryGetValue(key, out var draft)) drafts[key] = draft = new Draft();
                return draft;
            }
            foreach (var line in lines)
            {
                var others = new Others();
                foreach (var other in lines) if (other != line && other.bounds.Overlaps(line.bounds)) others.lines.Add(other);
                Sweep(line, ground, terrain, area, uvArea, others.lines.Count > 0 ? others : null, DraftAt);
                if (line.feature.kind == GeoTrenchKind.Dugout) Roof(line, ground, area, uvArea, DraftAt);
            }
            var chunks = new List<Chunk>();
            var keys = new List<Vector2Int>(drafts.Keys);
            keys.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            foreach (var key in keys)
            {
                var draft = drafts[key];
                if (draft.ground.Count + draft.walls.Count == 0) continue;
                SmoothGround(draft);
                draft.ground.AddRange(draft.tuck);
                var mesh = new Mesh { name = "Trenches " + key.x + "_" + key.y, indexFormat = draft.vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(draft.vertices); mesh.SetNormals(draft.normals); mesh.SetUVs(0, draft.uvs); mesh.SetUVs(1, draft.soil);
                int passes = Mathf.Clamp(groundPasses, 1, 8);
                mesh.subMeshCount = passes + 1;
                mesh.SetTriangles(draft.ground, 0); mesh.SetTriangles(draft.walls, 1);
                for (int pass = 1; pass < passes; pass++) mesh.SetTriangles(draft.ground, pass + 1);
                mesh.RecalculateBounds(); mesh.RecalculateTangents();
                chunks.Add(new Chunk { key = key, mesh = mesh });
            }
            return chunks;
        }

        // A trench's centreline as its mesh and the cut under it follow it: its finite points at least 10 cm apart, without the
        // spikes that turn by more than SpikeTurn (such a point is dropped, its neighbours joined). The scatter's lines have a
        // few, arms of 2-3 m turning back on themselves (real zigzags turn by about a right angle), and a sharp inner corner's
        // mitre reaches far past such short arms: the spoil's columns met on the bisector up to 13 m out, long skewed
        // triangles across the field.
        public const float SpikeTurn = 110f;

        public static List<Vector2> Centreline(TrenchFeature t)
        {
            var points = new List<Vector2>();
            if (t?.points == null) return points;
            for (int i = 0; i + 1 < t.points.Length; i += 2)
            {
                var p = new Vector2(t.points[i], t.points[i + 1]);
                if (!GeoData.Finite(p.x) || !GeoData.Finite(p.y)) continue;
                if (points.Count == 0 || (p - points[points.Count - 1]).sqrMagnitude > 0.01f) points.Add(p);
            }
            float limit = Mathf.Cos(SpikeTurn * Mathf.Deg2Rad);
            for (int i = 1; i + 1 < points.Count;)
            {
                var u = (points[i] - points[i - 1]).normalized; var v = (points[i + 1] - points[i]).normalized;
                if (Vector2.Dot(u, v) < limit) { points.RemoveAt(i); i = Mathf.Max(1, i - 1); }
                else i++;
            }
            return points;
        }

        // A trench whose end lies inside another (a communication trench or a dugout dug out of a fire trench) is cut where
        // it reaches the other's floor and left open there; the other gets cross-sections where this one's walls cross it.
        // The cuts first (on the lines as they were), then the cross-sections on the lines as cut.
        static void Junctions(List<Line> lines)
        {
            var joins = new List<(Line branch, int end, Line main)>();
            var cuts = new List<(Line line, List<Vector2> points, int end)>();
            foreach (var m in lines)
                for (int end = 0; end < 2; end++)
                {
                    var point = end == 0 ? m.points[0] : m.points[m.points.Count - 1];
                    foreach (var l in lines)
                    {
                        if (l == m || Distance(l.points, point) >= l.section.half) continue;
                        var cut = CutOut(m.points, end, l);
                        if (cut != null) { cuts.Add((m, cut, end)); joins.Add((m, end, l)); }
                        break;
                    }
                }
            foreach (var (line, points, end) in cuts)
            {
                // both ends of one line cut: the second cut starts from the first's points
                if (end == 1 && line.openStart) points[0] = line.points[0];
                line.points = points;
                if (end == 0) line.openStart = true; else line.openEnd = true;
            }
            foreach (var (m, end, l) in joins)
            {
                // the branch's own cross-section where its centreline crosses the other's wall line (SweepSide draws only
                // floor before it, inside the other)
                float total = Length(m.points);
                for (float x = 0; x < Mathf.Min(6f, total); x += 0.02f)
                {
                    float arc = end == 0 ? x : total - x;
                    if (Distance(l.points, PointAt(m.points, arc)) < l.section.half) continue;
                    m.extra.Add(arc);
                    if (end == 0) m.openFrom = arc; else m.openTo = arc;
                    break;
                }
                var at = end == 0 ? m.points[0] : m.points[m.points.Count - 1];
                var into = (end == 0 ? m.points[1] - m.points[0] : m.points[m.points.Count - 2] - m.points[m.points.Count - 1]).normalized;
                var across = Left(into);
                foreach (float side in new[] { -1f, 0f, 1f })
                    l.extra.Add(Project(l.points, at + across * (m.section.half * side)));
            }
        }

        // The line with its end (0 start, 1 end) moved along it to where it leaves the other's floor, or null when it does
        // not leave it within 8 m or too little would remain.
        static List<Vector2> CutOut(List<Vector2> points, int end, Line other)
        {
            var path = new List<Vector2>(points);
            if (end == 1) path.Reverse();
            float walked = 0;
            for (int segment = 0; segment + 1 < path.Count && walked < 8f; segment++)
            {
                Vector2 a = path[segment], b = path[segment + 1];
                float length = Vector2.Distance(a, b);
                for (float x = 0; x < length; x += 0.05f)
                {
                    var p = Vector2.Lerp(a, b, x / length);
                    if (Distance(other.points, p) < other.section.floorHalf) continue;
                    var cut = new List<Vector2> { p };
                    for (int i = segment + 1; i < path.Count; i++) cut.Add(path[i]);
                    if (Length(cut) < 1f) return null;
                    if (end == 1) cut.Reverse();
                    return cut;
                }
                walked += length;
            }
            return null;
        }

        // the walls' rows: shares of the way up from the floor's edge to the top edge
        static readonly float[] WallRows = { 0f, 0.3f, 0.65f, 1f };
        // a heap's columns: shares of the way from the edge to the crest, then of the rest of the way from the crest to the
        // toe; after them the apron's and the turn-down's
        static readonly float[] ToCrest = { 0f, 0.45f, 1f }, ToToe = { 0.22f, 0.44f, 0.64f, 0.83f, 1f };
        const int HeapColumns = 8, ApronColumn = 8, TuckColumn = 9, BankColumns = 10;

        // One side's spoil at a point along the trench: its crest's height above the ground, how far past the edge its toe
        // lies, the crest's share of that way, and how big the clods on it are.
        struct Heap
        {
            public float height, width, crest, clods;
            public static Heap Lerp(Heap a, Heap b, float t) => new Heap
            {
                height = Mathf.Lerp(a.height, b.height, t), width = Mathf.Lerp(a.width, b.width, t),
                crest = Mathf.Lerp(a.crest, b.crest, t), clods = Mathf.Lerp(a.clods, b.clods, t)
            };
        }

        // The heap of one side arc metres along the trench: it swells and shrinks over 5-8 m and a little over 1.5-2 m, its
        // toe nearer or farther, its crest nearer the edge or farther out; the parapet side higher and wider. (Piles of a few
        // metres, the height doubling from one to the next, read as a row of loaves along the trench.)
        static Heap HeapAt(Line line, float arc, int side)
        {
            var s = line.section;
            bool parapet = side == line.parapetSide;
            float swell = 0.78f + 0.44f * Noise1(line.seed, arc / 6.3f + side * 17.3f, side);
            float lumps = 1 + 0.16f * (Noise1(line.seed + 7, arc / 1.7f + side * 5.1f, side + 4) - 0.5f);
            float reach = 0.86f + 0.28f * Noise1(line.seed + 3, arc / 5.1f + side * 3.7f, side + 8);
            float jitter = 0.22f * (Noise1(line.seed + 5, arc / 1.9f + side * 9.2f, side + 12) - 0.5f);
            float width = Mathf.Min(s.spread * (parapet ? 1.12f : 0.94f) * reach + jitter, s.spread * GeoTrenchSection.WidestSpread);
            return new Heap
            {
                height = s.bank * (parapet ? 1 + line.parapet : 0.85f) * swell * lumps,
                width = Mathf.Max(0.8f, width),
                crest = 0.26f + 0.12f * Noise1(line.seed + 11, arc / 4.4f + side * 2.3f, side + 16),
                clods = Mathf.Lerp(FreshClods, OldClods, Mathf.Clamp01(line.feature.age))
            };
        }

        // a heap's columns: their offsets past the edge and their shares of the way to the toe; the apron reaches past the
        // toe and past every terrain triangle the cut may have lowered (the section's apron)
        static void Columns(Heap heap, GeoTrenchSection s, float[] offset, float[] share)
        {
            int k = 0;
            foreach (float f in ToCrest) { share[k] = heap.crest * f; offset[k] = share[k] * heap.width; k++; }
            foreach (float f in ToToe) { share[k] = heap.crest + (1 - heap.crest) * f; offset[k] = share[k] * heap.width; k++; }
            share[ApronColumn] = share[TuckColumn] = 1;
            offset[ApronColumn] = Mathf.Max(heap.width + GeoTrenchSection.ApronMin, s.apron);
            offset[TuckColumn] = offset[ApronColumn] + GeoTrenchSection.TuckWidth;
        }

        // the spoil's height above the ground share t of the way from the edge to the toe; rough: the clods there (-1..1),
        // which die out with the heap toward the toe and never take it below the ground
        static float HeapHeight(Heap heap, float t, float rough)
        {
            float f = GeoTrenchSection.Profile(t, heap.crest);
            return Lift + f * (heap.height + Mathf.Min(heap.clods, 0.5f * heap.height) * rough);
        }

        // Clods on the spoil: noise placed in the world (-1..1), the same for every trench at a point, so heaps that run into
        // each other carry the same ones and an end's spokes meet its sides'.
        static float Rough(Vector2 p) =>
            0.62f * (2 * Noise2(p.x / 1.7f, p.y / 1.7f, 21) - 1) + 0.38f * (2 * Noise2(p.x / 0.75f + 3.1f, p.y / 0.75f + 7.7f, 22) - 1);

        static float Noise2(float x, float y, int channel)
        {
            int i = Mathf.FloorToInt(x), j = Mathf.FloorToInt(y);
            float fx = x - i, fy = y - j;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float Corner(int a, int b) => Hash(0x5A17, unchecked(a * 73856093 ^ b * 19349663), channel);
            return Mathf.Lerp(Mathf.Lerp(Corner(i, j), Corner(i + 1, j), fx), Mathf.Lerp(Corner(i, j + 1), Corner(i + 1, j + 1), fx), fy);
        }

        // a point height metres above the ground
        static Vector3 Raised(Vector2 p, float height, Func<float, float, float> ground) => new Vector3(p.x, ground(p.x, p.y) + height, p.y);

        // a wall's height above the ground share t of the way up from the floor's edge to the top edge, where it meets the
        // spoil (top: the spoil's height there); an old wall's foot and top rounded (a smoothstep, not the face it was dug)
        static float WallHeight(GeoTrenchSection s, float t, float top)
        {
            t = Mathf.Lerp(t, t * t * (3 - 2 * t), s.slump);
            return Mathf.Lerp(-s.depth, top, t);
        }

        // the turn-down: under the ground and under the terrain
        static Vector3 Tucked(Vector2 p, Func<float, float, float> ground, Func<float, float, float> terrain) =>
            new Vector3(p.x, Mathf.Min(ground(p.x, p.y) - 0.22f, terrain(p.x, p.y) - 0.08f), p.y);

        // a bank column's point: the spoil (its first column the wall's top), the apron or the turn-down
        static Vector3 BankPoint(int c, Vector2 p, Heap heap, float share, float top, Func<float, float, float> ground, Func<float, float, float> terrain)
        {
            if (c == TuckColumn) return Tucked(p, ground, terrain);
            if (c == ApronColumn) return Raised(p, Lift, ground);
            return Raised(p, c == 0 ? top : HeapHeight(heap, share, Rough(p)), ground);
        }

        static void Sweep(Line line, Func<float, float, float> ground, Func<float, float, float> terrain, Rect area, Rect uvArea,
            Others others, Func<Vector2, Draft> draftAt)
        {
            var s = line.section;
            var points = line.points;
            int count = points.Count;
            var arcAt = new float[count];
            for (int i = 1; i < count; i++) arcAt[i] = arcAt[i - 1] + Vector2.Distance(points[i - 1], points[i]);
            // the corners: which side is inside, the mitre there
            var corners = new Corner[count];
            for (int i = 1; i + 1 < count; i++)
            {
                var u = (points[i] - points[i - 1]).normalized; var v = (points[i + 1] - points[i]).normalized;
                float cross = u.x * v.y - u.y * v.x, dot = Mathf.Clamp(Vector2.Dot(u, v), -1, 1);
                if (Mathf.Abs(cross) < 1e-4f && dot > 0) continue;
                float halfTurn = 0.5f * Mathf.Acos(dot);
                int inner = cross > 0 ? 1 : -1;
                var bisector = (Left(u) + Left(v)) * inner;
                bisector = bisector.sqrMagnitude > 1e-8f ? bisector.normalized : Left(u) * inner;
                corners[i] = new Corner { real = true, at = points[i], bisector = bisector, tanHalf = Mathf.Tan(halfTurn), mitre = 1f / Mathf.Max(0.3f, Mathf.Cos(halfTurn)), inner = inner };
            }
            // stations along each segment: every Step at most, and where other trenches' walls cross it
            var stations = new List<(int segment, float along)>();
            for (int i = 0; i + 1 < count; i++)
            {
                float l = arcAt[i + 1] - arcAt[i];
                var marks = new List<float> { 0 };
                int n = Mathf.Max(1, Mathf.CeilToInt(l / Step));
                for (int k = 1; k < n; k++) marks.Add(l * k / n);
                foreach (float e in line.extra) if (e > arcAt[i] + 0.05f && e < arcAt[i + 1] - 0.05f) marks.Add(e - arcAt[i]);
                marks.Sort();
                float last = -1;
                foreach (float m in marks) { if (m - last < 0.05f) continue; stations.Add((i, m)); last = m; }
            }
            stations.Add((count - 2, arcAt[count - 1] - arcAt[count - 2]));

            // the walls sway a little along the trench (dug by hand or by a bucket, never ruled): a station's centre moves off
            // the line, both sides by the same, at a corner along the bisector of the two segments' normals
            Vector2 Swayed(int segment, float along, bool atCorner)
            {
                var a = points[segment];
                var dir = (points[segment + 1] - a).normalized;
                var off = atCorner ? (Left((a - points[segment - 1]).normalized) + Left(dir)).normalized : Left(dir);
                return a + dir * along + off * SwayAt(line, arcAt[segment] + along);
            }
            // how far along each side's wall its texture has run at the line's end (right side: [0], left: [1])
            var wallRun = new float[2];
            for (int side = 1; side >= -1; side -= 2)
            {
                // this side's rows: the stations, and a fan round every corner it is outside of
                var rows = new List<Row>();
                foreach (var (segment, along) in stations)
                {
                    var b = points[segment + 1];
                    var dir = (b - points[segment]).normalized;
                    var normal = Left(dir) * side;
                    bool atCorner = along < 1e-4f && segment > 0 && corners[segment].real;
                    var a = Swayed(segment, along, atCorner);
                    if (atCorner)
                    {
                        var corner = corners[segment];
                        var previous = Left((points[segment] - points[segment - 1]).normalized) * side;
                        if (corner.inner == side)
                            rows.Add(new Row { at = a, normal = corner.bisector, arc = arcAt[segment], along = 0, scale = corner.mitre, segment = segment, mitred = true });
                        else
                        {
                            // round the outside of the corner: from the last segment's normal to this one's
                            float turn = Vector2.SignedAngle(previous, normal) * Mathf.Deg2Rad;
                            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(turn) / FanStep));
                            for (int k = 0; k <= steps; k++)
                            {
                                float angle = turn * k / steps;
                                float c = Mathf.Cos(angle), sn = Mathf.Sin(angle);
                                var n = new Vector2(previous.x * c - previous.y * sn, previous.x * sn + previous.y * c);
                                rows.Add(new Row { at = a, normal = n, arc = arcAt[segment], along = 0, scale = 1, segment = segment });
                            }
                        }
                        continue;
                    }
                    rows.Add(new Row { at = a, normal = normal, arc = arcAt[segment] + along, along = along, scale = 1, segment = segment });
                }
                wallRun[side > 0 ? 1 : 0] = SweepSide(line, side, rows, corners, arcAt, ground, terrain, area, uvArea, others, draftAt);
            }
            // an end's wall carries on the texture of the side it starts from (spoke 0: the left side at the start, where both
            // sides' run 0, the right side at the end)
            if (!line.openStart && area.Contains(points[0]))
                End(line, points[0], (points[0] - points[1]).normalized, 0, true, 0, ground, terrain, others, uvArea, draftAt(points[0]));
            if (!line.openEnd && area.Contains(points[count - 1]))
                End(line, points[count - 1], (points[count - 1] - points[count - 2]).normalized, arcAt[count - 1], false, wallRun[0], ground, terrain, others, uvArea, draftAt(points[count - 1]));
        }

        static Vector2 Left(Vector2 d) => new Vector2(-d.y, d.x);

        // One side's grid over its rows: the floor half, the wall, the heap, the apron and the turn-down; quads between rows
        // into the drafts. Returns how far along the wall its texture has run at the last row.
        static float SweepSide(Line line, int side, List<Row> rows, Corner[] corners, float[] arcAt, Func<float, float, float> ground,
            Func<float, float, float> terrain, Rect area, Rect uvArea, Others others, Func<Vector2, Draft> draftAt)
        {
            var s = line.section;
            var floor = new Grid(rows.Count, 2);
            var wall = new Grid(rows.Count, WallRows.Length);
            var bank = new Grid(rows.Count, BankColumns);
            var offset = new float[BankColumns];
            var share = new float[BankColumns];
            // where a row's point at offset o stands: on its normal (a mitred row's: on the bisector, stretched by the mitre)
            Vector2 At(Row r, float o) => r.mitred ? r.at + r.normal * (o * r.scale) : r.at + r.normal * o;
            // the inner corner (its segment) behind which a row's point at offset o would fold, or -1: that corner's mitred row
            // gives the row its point there, to the bit — each row's own point on the bisector (its heap is its own) stood
            // apart from its neighbour's on the same line, and the quads between them stood up as slivers
            int Folds(Row r, float o)
            {
                if (r.mitred) return -1;
                var c0 = corners[r.segment];
                if (c0.real && c0.inner == side && r.along < o * c0.tanHalf) return r.segment;
                int next = r.segment + 1;
                if (next < corners.Length)
                {
                    var c1 = corners[next];
                    float toNext = arcAt[next] - arcAt[r.segment] - r.along;
                    if (c1.real && c1.inner == side && toNext < o * c1.tanHalf) return next;
                }
                return -1;
            }
            var mitredRow = new Dictionary<int, int>();
            for (int k = 0; k < rows.Count; k++) if (rows[k].mitred) mitredRow[rows[k].segment] = k;
            // a point of row k at column c taken from the mitred row of the corner it folds behind
            bool Take(Grid grid, int k, int c, float o)
            {
                int corner = Folds(rows[k], o);
                if (corner < 0 || !mitredRow.TryGetValue(corner, out int from)) return false;
                int i = k * grid.columns + c, j = from * grid.columns + c;
                grid.position[i] = grid.position[j]; grid.facing[i] = grid.facing[j]; grid.uv[i] = grid.uv[j]; grid.soil[i] = grid.soil[j];
                return true;
            }
            // the mitred rows first, then the others (which may take points from them)
            for (int pass = 0; pass < 2; pass++)
                for (int k = 0; k < rows.Count; k++)
                {
                    var r = rows[k];
                    if (r.mitred != (pass == 0)) continue;
                    var heap = HeapAt(line, r.arc, side);
                    Columns(heap, s, offset, share);
                    // the top edge crumbled outward and a little lower here; the wall's top is the heap's first column, to the bit
                    float lip = LipAt(line, r.arc, side);
                    offset[0] = lip;
                    float top = HeapHeight(heap, 0, Rough(At(r, s.half + lip))) - 0.8f * lip;
                    for (int c = 0; c < 2; c++)
                    {
                        float o = c == 0 ? 0 : s.floorHalf;
                        if (Take(floor, k, c, o)) continue;
                        var p = At(r, o);
                        floor.position[k * 2 + c] = Raised(p, -s.depth, ground);
                        floor.facing[k * 2 + c] = Vector3.up;
                        floor.uv[k * 2 + c] = TerrainUv(p, uvArea);
                        floor.soil[k * 2 + c] = new Vector2(1, 1);
                    }
                    for (int w = 0; w < WallRows.Length; w++)
                    {
                        float o = Mathf.Lerp(s.floorHalf, s.half + lip, WallRows[w]);
                        if (Take(wall, k, w, o)) continue;
                        var p = At(r, o);
                        wall.position[k * WallRows.Length + w] = Raised(p, WallHeight(s, WallRows[w], top), ground);
                        wall.facing[k * WallRows.Length + w] = new Vector3(r.at.x - p.x, 0.2f, r.at.y - p.y);
                        wall.uv[k * WallRows.Length + w] = new Vector2(r.arc, WallRows[w]);
                    }
                    for (int c = 0; c < BankColumns; c++)
                    {
                        if (Take(bank, k, c, s.half + offset[c])) continue;
                        var p = At(r, s.half + offset[c]);
                        int i = k * BankColumns + c;
                        bank.position[i] = BankPoint(c, p, heap, share[c], top, ground, terrain);
                        bank.facing[i] = Vector3.up;
                        bank.uv[i] = TerrainUv(p, uvArea);
                        bank.soil[i] = new Vector2(c < HeapColumns ? SoilShare(line, share[c], heap) : 0, 0);
                    }
                }
            // the wall's texture runs along this side's own length: round an outer corner's fan the wall is longer than the
            // centreline, at an inner mitre shorter (it ran along the centreline's arc, and a fan's rows, all at one arc,
            // streaked it round the corner)
            float run = 0;
            for (int k = 0; k < rows.Count; k++)
            {
                if (k > 0)
                {
                    Vector3 a = wall.position[(k - 1) * WallRows.Length + 1] + wall.position[(k - 1) * WallRows.Length + 2];
                    Vector3 b = wall.position[k * WallRows.Length + 1] + wall.position[k * WallRows.Length + 2];
                    run += 0.5f * new Vector2(b.x - a.x, b.z - a.z).magnitude;
                }
                for (int w = 0; w < WallRows.Length; w++) wall.uv[k * WallRows.Length + w].x = run;
            }
            // the left side's columns run to the left of the rows' direction (its quads face up unflipped), the right side's
            // to the right. Where the centreline is still inside another trench (a branch's open end, before its cross-section
            // on the other's wall line) only the floor is drawn: the other's walls are open there.
            bool flip = side < 0;
            var within = new bool[rows.Count];
            for (int k = 0; k < rows.Count; k++) within[k] = rows[k].arc < line.openFrom - 1e-3f || rows[k].arc > line.openTo + 1e-3f;
            for (int k = 0; k + 1 < rows.Count; k++)
            {
                var middle = (rows[k].at + rows[k + 1].at) * 0.5f;
                if (!area.Contains(middle)) continue;
                var draft = draftAt(middle);
                Strip(draft, draft.ground, floor, k, 0, 1, flip, null, -1);
                if (within[k] || within[k + 1]) continue;
                Strip(draft, draft.walls, wall, k, 0, WallRows.Length - 1, flip, others, WallStrip(line, (rows[k].arc + rows[k + 1].arc) * 0.5f));
                Strip(draft, draft.ground, bank, k, 0, ApronColumn, flip, others, -1);
                Strip(draft, draft.tuck, bank, k, ApronColumn, TuckColumn, flip, others, -1, ApronColumn, 0);
            }
            return run;
        }

        static Vector2 TerrainUv(Vector2 p, Rect uvArea) => new Vector2((p.x - uvArea.x) / uvArea.width, (p.y - uvArea.y) / uvArea.height);

        // How much of the spoil share t of the way to its toe shows as the trench's own soil (UV1.x): the inner slope and the
        // crest bare, fading out to the terrain's look well before the toe (where the heap runs out into the ground, both are
        // the terrain); more where the heap stands higher; less on an old trench.
        static float SoilShare(Line line, float t, Heap heap)
        {
            float fade = Mathf.Clamp01((t - 0.36f) / 0.5f);
            fade = 1 - fade * fade * (3 - 2 * fade);
            float high = Mathf.Clamp01(heap.height / (1.3f * Mathf.Max(0.01f, line.section.bank)));
            return Mathf.Clamp01(fade * line.soil * (0.7f + 0.3f * high));
        }

        // Which strip of the wall atlas a stretch of wall is (stretches of 5-11 m): a fire trench bare in most places, boards
        // in a quarter, wattle in some; a communication trench bare; a dugout's pit lined with boards or wattle.
        static int WallStrip(Line line, float arc)
        {
            float length = 5 + 6 * Hash(line.seed, 7, 1);
            int stretch = Mathf.FloorToInt(arc / length);
            float h = Hash(line.seed, stretch, 11);
            switch (line.feature.kind)
            {
                case GeoTrenchKind.Dugout: return h < 0.65f ? 2 : 3;
                case GeoTrenchKind.Communication: return h < 0.6f ? 0 : 1;
                default: return h < 0.38f ? 0 : h < 0.62f ? 1 : h < 0.86f ? 2 : 3;
            }
        }

        // how much a trench's centre sways off its line arc metres along it (±Wobble over 3-6 m), dying out toward its calm
        // places (junctions within 2.5 m, its ends within 1.5 m)
        static float SwayAt(Line line, float arc) =>
            GeoTrenchSection.Wobble * (2 * Noise1(line.seed + 17, arc / 4.6f, 31) - 1) * Calm(line, arc);

        // how far one side's top edge has crumbled outward there (0 on about half the length, up to LipCrumble)
        static float LipAt(Line line, float arc, int side) =>
            GeoTrenchSection.LipCrumble * Mathf.Max(0, 2 * Noise1(line.seed + 23, arc / 1.3f + side * 7.7f, 33 + side) - 1) * Calm(line, arc);

        static float Calm(Line line, float arc)
        {
            float calm = 1;
            foreach (float at in line.calm)
            {
                bool end = at <= 1e-3f || at >= line.length - 1e-3f;
                float d = Mathf.Abs(arc - at), reach = end ? 1.5f : 2.5f;
                float t = Mathf.Clamp01((d - 0.2f) / (reach - 0.2f));
                calm = Mathf.Min(calm, t * t * (3 - 2 * t));
            }
            return calm;
        }

        static float Noise1(int seed, float x, int channel)
        {
            int i = Mathf.FloorToInt(x);
            float f = x - i;
            f = f * f * (3 - 2 * f);
            return Mathf.Lerp(Hash(seed, i, channel), Hash(seed, i + 1, channel), f);
        }

        static float Hash(int seed, int i, int channel)
        {
            unchecked
            {
                uint h = (uint)seed * 2654435761u ^ (uint)i * 2246822519u ^ (uint)channel * 3266489917u;
                h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777215f;
            }
        }

        // Quads between rows k and k + 1 over the columns first..last of a grid, into the draft. Unflipped they face up where
        // the columns run to the left of the rows' direction; flip turns them over. What of a triangle lies inside another
        // trench is cut away (clip). strip >= 0: a wall, its texture coordinates (arc, share of the wall) mapped into that strip
        // of the atlas. normalOf >= 0: every vertex takes that column's normal (as the strip from normalFrom to it has it) —
        // the turn-down under the terrain shades as the apron above it, where its top shows.
        static void Strip(Draft draft, List<int> triangles, Grid grid, int k, int first, int last, bool flip, Others clip, int strip,
            int normalOf = -1, int normalFrom = 0)
        {
            int start = draft.vertices.Count;
            for (int r = k; r <= k + 1; r++)
                for (int c = first; c <= last; c++)
                {
                    int i = r * grid.columns + c;
                    draft.vertices.Add(grid.position[i]);
                    draft.normals.Add(normalOf >= 0 ? grid.Normal(r, normalOf, normalFrom, normalOf) : grid.Normal(r, c, first, last));
                    var uv = grid.uv[i];
                    if (strip >= 0) uv = new Vector2(uv.x / StripLength, (strip + 0.015f + 0.97f * uv.y) / WallStrips);
                    draft.uvs.Add(uv);
                    draft.soil.Add(grid.soil[i]);
                }
            int width = last - first + 1;
            Vector2 Flat(int i) => new Vector2(draft.vertices[i].x, draft.vertices[i].z);
            // a vertex on the edge i-j at t from i: position, normal and texture coordinates in between
            int Between(int i, int j, float t)
            {
                draft.vertices.Add(Vector3.Lerp(draft.vertices[i], draft.vertices[j], t));
                draft.normals.Add(Vector3.Lerp(draft.normals[i], draft.normals[j], t).normalized);
                draft.uvs.Add(Vector2.Lerp(draft.uvs[i], draft.uvs[j], t));
                draft.soil.Add(Vector2.Lerp(draft.soil[i], draft.soil[j], t));
                return draft.vertices.Count - 1;
            }
            void Add(int a, int b, int c)
            {
                if (clip == null) { triangles.Add(a); triangles.Add(b); triangles.Add(c); return; }
                bool ia = clip.Inside(Flat(a), out var pa, out var na), ib = clip.Inside(Flat(b), out var pb, out var nb), ic = clip.Inside(Flat(c), out var pc, out var nc);
                if (!ia && !ib && !ic) { triangles.Add(a); triangles.Add(b); triangles.Add(c); return; }
                if (ia && ib && ic) return;
                // keep the part outside the other trench: the triangle cut along the wall line of the first corner inside it
                var onLine = ia ? pa : ib ? pb : pc; var outward = ia ? na : ib ? nb : nc;
                float Side(int i) => Vector2.Dot(Flat(i) - onLine, outward);
                var corners = new[] { a, b, c };
                var kept = new List<int>(4);
                for (int i = 0; i < 3; i++)
                {
                    int here = corners[i], next = corners[(i + 1) % 3];
                    float sh = Side(here), sn = Side(next);
                    if (sh >= 0) kept.Add(here);
                    if ((sh >= 0) != (sn >= 0)) kept.Add(Between(here, next, sh / (sh - sn)));
                }
                for (int i = 1; i + 1 < kept.Count; i++) { triangles.Add(kept[0]); triangles.Add(kept[i]); triangles.Add(kept[i + 1]); }
            }
            for (int c = 0; c + 1 < width; c++)
            {
                int a = start + c, b = start + c + 1, d = start + width + c, e = start + width + c + 1;
                if (!flip) { Add(a, b, d); Add(b, e, d); }
                else { Add(a, d, b); Add(b, d, e); }
            }
        }

        // A rounded end: the half cross-section turned about the end point in CapSpokes steps from one side over the end to
        // the other, the heaps fading to EndBank of their height and three quarters of their spread at its tip (the spoil lies
        // along the trench, not round its end). Its first and last spokes are the sides' cross-sections at the end, to the bit.
        // run: how far the wall's texture has run on the side of spoke 0 (it carries on round the end from there).
        static void End(Line line, Vector2 point, Vector2 outward, float arc, bool start, float run, Func<float, float, float> ground, Func<float, float, float> terrain,
            Others others, Rect uvArea, Draft draft)
        {
            var s = line.section;
            float middle = 0.5f * (s.floorHalf + s.half);
            var left = new Vector2(outward.y, -outward.x);
            // spoke 0 is the trench's left side at its start, its right side at its end
            int first = start ? 1 : -1;
            Heap heapA = HeapAt(line, arc, first), heapB = HeapAt(line, arc, -first);
            // the ground's columns: the floor's middle and edge, then the heap's, the apron and the turn-down
            const int Floor = 2;
            var groundGrid = new Grid(CapSpokes + 1, Floor + BankColumns);
            var wallGrid = new Grid(CapSpokes + 1, WallRows.Length);
            var offset = new float[BankColumns];
            var share = new float[BankColumns];
            for (int m = 0; m <= CapSpokes; m++)
            {
                float angle = Mathf.PI * m / CapSpokes;
                var dir = left * Mathf.Cos(angle) + outward * Mathf.Sin(angle);
                float taper = Mathf.Lerp(GeoTrenchSection.EndBank, 1, Mathf.Pow(Mathf.Abs(Mathf.Cos(angle)), 0.8f));
                var heap = Heap.Lerp(heapA, heapB, (float)m / CapSpokes);
                heap.height *= taper;
                heap.width = Mathf.Max(0.8f, heap.width * Mathf.Lerp(0.75f, 1, taper));
                Columns(heap, s, offset, share);
                // the apron round the end is a polygon of spokes: out far enough that its sides, not only its corners, lie
                // over the terrain the cut lowered (the sides' spokes are the trench's sides, kept as they are)
                if (m > 0 && m < CapSpokes)
                {
                    float circumscribe = (s.half + offset[ApronColumn]) / Mathf.Cos(Mathf.PI / (2 * CapSpokes)) - s.half;
                    offset[TuckColumn] += circumscribe - offset[ApronColumn];
                    offset[ApronColumn] = circumscribe;
                }
                float top = HeapHeight(heap, 0, Rough(point + dir * s.half));
                int row = m * (Floor + BankColumns);
                for (int c = 0; c < Floor; c++)
                {
                    var p = point + dir * (c == 0 ? 0 : s.floorHalf);
                    groundGrid.position[row + c] = Raised(p, -s.depth, ground);
                    groundGrid.facing[row + c] = Vector3.up;
                    groundGrid.uv[row + c] = TerrainUv(p, uvArea);
                    groundGrid.soil[row + c] = new Vector2(1, 1);
                }
                for (int c = 0; c < BankColumns; c++)
                {
                    var p = point + dir * (s.half + offset[c]);
                    int i = row + Floor + c;
                    groundGrid.position[i] = BankPoint(c, p, heap, share[c], top, ground, terrain);
                    groundGrid.facing[i] = Vector3.up;
                    groundGrid.uv[i] = TerrainUv(p, uvArea);
                    groundGrid.soil[i] = new Vector2(c < HeapColumns ? SoilShare(line, share[c], heap) * taper : 0, 0);
                }
                for (int w = 0; w < WallRows.Length; w++)
                {
                    float d = Mathf.Lerp(s.floorHalf, s.half, WallRows[w]);
                    var p = point + dir * d;
                    int i = m * WallRows.Length + w;
                    wallGrid.position[i] = Raised(p, WallHeight(s, WallRows[w], top), ground);
                    wallGrid.facing[i] = new Vector3(point.x - p.x, 0.2f, point.y - p.y);
                    wallGrid.uv[i] = new Vector2(run + (start ? -1 : 1) * angle * middle, WallRows[w]);
                }
            }
            // the spokes turn anticlockwise seen from above, their columns run outward: every strip of an end is turned over
            int strip = WallStrip(line, arc);
            for (int m = 0; m < CapSpokes; m++)
            {
                Strip(draft, draft.ground, groundGrid, m, 0, Floor - 1, true, null, -1);
                Strip(draft, draft.ground, groundGrid, m, Floor, Floor + ApronColumn, true, others, -1);
                Strip(draft, draft.tuck, groundGrid, m, Floor + ApronColumn, Floor + TuckColumn, true, others, -1, Floor + ApronColumn, Floor);
                Strip(draft, draft.walls, wallGrid, m, 0, WallRows.Length - 1, true, others, strip);
            }
        }

        // A dugout's roof (GeoTrenchKind.Dugout): logs laid across its pit from wall to wall, from about a metre in from where
        // it opens into its trench (the way in) to its closed end, and a lid of soil heaped over them, sloping down past that
        // end. The battle over, most have fallen in: whole (30 %), a span of 1-2 m sagged into the pit (45 %: logs snapped in
        // the middle, their halves down at the break, some gone, no soil over them, the soil that fell in heaped on the floor),
        // or all but the end logs (25 %). The logs are the wall atlas's timber strip (sub-mesh 1, their snapped ends its
        // boards), the lid and the fallen soil the ground (sub-mesh 0, the trench's own soil).
        static void Roof(Line line, Func<float, float, float> ground, Rect area, Rect uvArea, Func<Vector2, Draft> draftAt)
        {
            var s = line.section;
            var points = line.points;
            float total = line.length;
            // the way in: at an open end (the start when the dugout stands on its own); the closed end gets the lid's slope
            float from = line.openStart ? line.openFrom + 1.1f : line.openEnd ? 0 : 1.1f;
            float to = line.openEnd ? line.openTo - 1.1f : total;
            bool closedEnd = !line.openEnd, closedStart = !line.openStart && line.openEnd;
            if (to - from < 1.2f) return;
            var middle = PointAt(points, 0.5f * (from + to));
            if (!area.Contains(middle)) return;
            var draft = draftAt(middle);
            float state = Hash(line.seed, 41, 7);
            float fallFrom = float.MaxValue, fallTo = float.MinValue;
            if (state >= 0.75f) { fallFrom = from + 0.5f; fallTo = to - 0.45f; }
            else if (state >= 0.3f)
            {
                float mid = Mathf.Lerp(from, to, 0.3f + 0.4f * Hash(line.seed, 42, 7)), span = 0.5f + 0.5f * Hash(line.seed, 43, 7);
                fallFrom = Mathf.Max(from + 0.35f, mid - span); fallTo = Mathf.Min(to - 0.35f, mid + span);
            }
            bool Fallen(float arc) => arc > fallFrom && arc < fallTo;
            const float Log = 0.105f, Seat = 0.03f;   // a log's mean radius; how far it sits into the ground at its ends
            // the logs: 18-24 cm thick, touching, from 30-40 cm past one wall's edge to past the other's
            int k = 0;
            for (float arc = from + 0.12f; arc < to - 0.08f; k++)
            {
                float r = Log + 0.03f * (Hash(line.seed, 100 + k, 8) - 0.5f);
                var p = PointAt(points, arc);
                var dir = TangentAt(points, arc);
                var axis = (Left(dir) + dir * 0.07f * (Hash(line.seed, 300 + k, 8) - 0.5f)).normalized;   // not quite square
                float reach = s.half + 0.3f + 0.1f * Hash(line.seed, 200 + k, 8);
                Vector2 pl = p - axis * reach, pr = p + axis * reach;
                var l = new Vector3(pl.x, ground(pl.x, pl.y) - Seat + r, pl.y);
                var rr = new Vector3(pr.x, ground(pr.x, pr.y) - Seat + r, pr.y);
                if (!Fallen(arc)) LogMesh(draft, l, rr, r, Hash(line.seed, 600 + k, 8), false, false);
                else if (Hash(line.seed, 400 + k, 8) > 0.3f)
                {
                    // snapped in the middle, the halves down at the break, not quite meeting
                    float sag = Mathf.Min(s.depth - 0.3f, 0.5f + 0.7f * Hash(line.seed, 500 + k, 8));
                    var d3 = new Vector3(dir.x, 0, dir.y);
                    var mid = (l + rr) * 0.5f + Vector3.down * sag;
                    LogMesh(draft, l, mid + d3 * 0.04f + Vector3.up * 0.05f * Hash(line.seed, 700 + k, 8), r, Hash(line.seed, 600 + k, 8), false, true);
                    LogMesh(draft, rr, mid - d3 * 0.04f, r, Hash(line.seed, 650 + k, 8), false, true);
                }
                arc += 2 * r + 0.015f + 0.03f * Hash(line.seed, 800 + k, 8);
            }
            // the lid: soil heaped over the whole stretches, each from the way in (or the fallen span) to the closed end (or it)
            if (fallFrom < fallTo)
            {
                if (fallFrom - from > 0.4f) Lid(line, ground, uvArea, draft, from, fallFrom, closedStart, false);
                if (to - fallTo > 0.4f) Lid(line, ground, uvArea, draft, fallTo, to, false, closedEnd);
                FallenSoil(line, ground, uvArea, draft, fallFrom, fallTo);
            }
            else Lid(line, ground, uvArea, draft, from, to, closedStart, closedEnd);
        }

        // A log from a to b: a six-sided cylinder in the timber strip (u along it, v round it); a snapped end (broken) gets a
        // cap in the boards (light split wood).
        static void LogMesh(Draft draft, Vector3 a, Vector3 b, float r, float phase, bool capA, bool capB)
        {
            const int Sides = 6;
            var axis = b - a;
            float length = axis.magnitude;
            if (length < 0.05f) return;
            axis /= length;
            var n1 = Vector3.Cross(axis, Vector3.up);
            if (n1.sqrMagnitude < 1e-4f) n1 = Vector3.Cross(axis, Vector3.right);
            n1.Normalize();
            var n2 = Vector3.Cross(axis, n1);
            int start = draft.vertices.Count;
            float turn = phase * Mathf.PI * 2;
            for (int i = 0; i <= Sides; i++)
            {
                float angle = turn + Mathf.PI * 2 * i / Sides;
                var o = n1 * Mathf.Cos(angle) + n2 * Mathf.Sin(angle);
                float v = (LogStrip + 0.015f + 0.97f * i / Sides) / WallStrips;
                draft.vertices.Add(a + o * r); draft.normals.Add(o); draft.uvs.Add(new Vector2(phase * 3, v)); draft.soil.Add(Vector2.zero);
                draft.vertices.Add(b + o * r); draft.normals.Add(o); draft.uvs.Add(new Vector2(phase * 3 + length / StripLength, v)); draft.soil.Add(Vector2.zero);
            }
            for (int i = 0; i < Sides; i++)
            {
                int a0 = start + 2 * i, b0 = a0 + 1, a1 = a0 + 2, b1 = a0 + 3;
                draft.walls.Add(a0); draft.walls.Add(a1); draft.walls.Add(b0);
                draft.walls.Add(a1); draft.walls.Add(b1); draft.walls.Add(b0);
            }
            void Cap(Vector3 at, Vector3 outward)
            {
                int centre = draft.vertices.Count;
                float vBoard = (BoardStrip + 0.4f) / WallStrips;
                draft.vertices.Add(at); draft.normals.Add(outward); draft.uvs.Add(new Vector2(0.5f, vBoard)); draft.soil.Add(Vector2.zero);
                for (int i = 0; i < Sides; i++)
                {
                    float angle = turn + Mathf.PI * 2 * i / Sides;
                    var o = n1 * Mathf.Cos(angle) + n2 * Mathf.Sin(angle);
                    draft.vertices.Add(at + o * r); draft.normals.Add(outward);
                    draft.uvs.Add(new Vector2(0.5f + 0.03f * Mathf.Cos(angle), vBoard + 0.01f * Mathf.Sin(angle))); draft.soil.Add(Vector2.zero);
                }
                bool forward = Vector3.Dot(outward, axis) > 0;
                for (int i = 0; i < Sides; i++)
                {
                    int p0 = centre + 1 + i, p1 = centre + 1 + (i + 1) % Sides;
                    draft.walls.Add(centre);
                    if (forward) { draft.walls.Add(p0); draft.walls.Add(p1); } else { draft.walls.Add(p1); draft.walls.Add(p0); }
                }
            }
            if (capA) Cap(a, -axis);
            if (capB) Cap(b, axis);
        }

        // The soil over a stretch of a dugout's roof (from..to along the line): a low mound over the logs whose edges dip under
        // the heaps beside the pit; a front face of bare soil (the wall atlas) where the stretch ends open, a slope down past
        // a closed end over the pit's rounded end.
        static void Lid(Line line, Func<float, float, float> ground, Rect uvArea, Draft draft, float from, float to, bool slopeAtStart, bool slopeAtEnd)
        {
            var s = line.section;
            const int Columns = 9;
            float width = s.half + 0.65f, top = 2 * 0.105f - 0.03f;   // the logs' top above the ground
            float soil = 0.24f;
            // rows along the stretch every half metre, past a closed end three more down the slope
            var arcs = new List<float>();
            int n = Mathf.Max(1, Mathf.CeilToInt((to - from) / 0.5f));
            for (int i = 0; i <= n; i++) arcs.Add(Mathf.Lerp(from, to, (float)i / n));
            var grid = new Grid(arcs.Count + (slopeAtStart ? 3 : 0) + (slopeAtEnd ? 3 : 0), Columns);
            int row = 0;
            // the lid's own mound covers the pit and the logs' ends; past the pit's edge it lies on the heap beside it (just over
            // it, in its colour) and its outer edge dips under it, so the two meet without a crease (as a mound of its own it
            // stood on the heaps like a plate); down the slope past a closed end it is its own mound sinking under the ground
            float inner = s.half + 0.3f;
            void Row(Vector2 at, Vector2 across, float arc, float fall)
            {
                var heapLeft = HeapAt(line, arc, 1); var heapRight = HeapAt(line, arc, -1);
                for (int c = 0; c < Columns; c++)
                {
                    float x = Mathf.Lerp(-width, width, (float)c / (Columns - 1)), ax = Mathf.Abs(x);
                    var p = at + across * x;
                    float own = top + soil * Mathf.Pow(Mathf.Cos(0.5f * Mathf.PI * Mathf.Min(1, ax / inner)), 2);
                    float h = own, share = line.soil * 0.85f;
                    if (fall <= 0 && ax > s.half)
                    {
                        var heap = x > 0 ? heapLeft : heapRight;
                        float t = (ax - s.half) / heap.width;
                        float under = HeapHeight(heap, t, Rough(p));
                        bool edge = c == 0 || c == Columns - 1;
                        h = edge ? under - 0.03f : Mathf.Max(own, under + 0.015f);
                        if (edge || own <= under + 0.015f) share = SoilShare(line, t, heap);
                    }
                    h = Mathf.Lerp(h, -0.12f, fall);
                    int i = row * Columns + c;
                    grid.position[i] = Raised(p, h, ground);
                    grid.facing[i] = Vector3.up;
                    grid.uv[i] = TerrainUv(p, uvArea);
                    grid.soil[i] = new Vector2(share * (1 - fall), 0);
                }
                row++;
            }
            var startDir = TangentAt(line.points, from); var endDir = TangentAt(line.points, to);
            if (slopeAtStart)
                for (int i = 3; i >= 1; i--) Row(PointAt(line.points, from) - startDir * (s.half * 0.45f * i), Left(startDir), from, i / 3f);
            foreach (float arc in arcs) Row(PointAt(line.points, arc), Left(TangentAt(line.points, arc)), arc, 0);
            if (slopeAtEnd)
                for (int i = 1; i <= 3; i++) Row(PointAt(line.points, to) + endDir * (s.half * 0.45f * i), Left(endDir), to, i / 3f);
            // the rows run along the line and the columns to its left (x grows leftward): the quads face up unflipped
            for (int k = 0; k + 1 < grid.rows; k++) Strip(draft, draft.ground, grid, k, 0, Columns - 1, false, null, -1);
            // the open faces: bare soil from the lid's edge down to the logs' underside, facing out of the lid
            if (!slopeAtStart) Face(draft, grid, 0, -startDir, ground);
            if (!slopeAtEnd) Face(draft, grid, grid.rows - 1, endDir, ground);
        }

        // a lid's open face at grid row k: from its surface down past the logs' underside, in the atlas's bare soil (strip 0)
        static void Face(Draft draft, Grid grid, int k, Vector2 outward, Func<float, float, float> ground)
        {
            int columns = grid.columns, start = draft.vertices.Count;
            var normal = new Vector3(outward.x, 0, outward.y);
            float u = 0;
            for (int c = 0; c < columns; c++)
            {
                var topPoint = grid.position[k * columns + c];
                var bottom = new Vector3(topPoint.x, Mathf.Min(topPoint.y - 0.05f, ground(topPoint.x, topPoint.z) - 0.1f), topPoint.z);
                if (c > 0) u += Vector3.Distance(topPoint, grid.position[k * columns + c - 1]);
                // the soil strip's middle (chernozem over loess), not its top with the turf's grass fringe
                draft.vertices.Add(bottom); draft.normals.Add(normal); draft.uvs.Add(new Vector2(u / StripLength, (0.015f + 0.97f * 0.3f) / WallStrips)); draft.soil.Add(Vector2.zero);
                draft.vertices.Add(topPoint); draft.normals.Add(normal); draft.uvs.Add(new Vector2(u / StripLength, (0.015f + 0.97f * 0.62f) / WallStrips)); draft.soil.Add(Vector2.zero);
            }
            for (int c = 0; c + 1 < columns; c++)
            {
                int b0 = start + 2 * c, t0 = b0 + 1, b1 = b0 + 2, t1 = b0 + 3;
                // facing outward: try one winding, turn it if its normal points in
                var n = Vector3.Cross(draft.vertices[b1] - draft.vertices[b0], draft.vertices[t0] - draft.vertices[b0]);
                if (Vector3.Dot(n, normal) >= 0) { draft.walls.Add(b0); draft.walls.Add(b1); draft.walls.Add(t0); draft.walls.Add(b1); draft.walls.Add(t1); draft.walls.Add(t0); }
                else { draft.walls.Add(b0); draft.walls.Add(t0); draft.walls.Add(b1); draft.walls.Add(b1); draft.walls.Add(t0); draft.walls.Add(t1); }
            }
        }

        // the soil that fell in with a roof's span, heaped on the pit's floor under it (fresh spoil)
        static void FallenSoil(Line line, Func<float, float, float> ground, Rect uvArea, Draft draft, float from, float to)
        {
            var s = line.section;
            const int Columns = 7;
            int n = Mathf.Max(2, Mathf.CeilToInt((to - from) / 0.4f));
            var grid = new Grid(n + 1, Columns);
            for (int i = 0; i <= n; i++)
            {
                float arc = Mathf.Lerp(from, to, (float)i / n), along = Mathf.Sin(Mathf.PI * i / n);
                var at = PointAt(line.points, arc); var across = Left(TangentAt(line.points, arc));
                for (int c = 0; c < Columns; c++)
                {
                    float x = Mathf.Lerp(-s.floorHalf, s.floorHalf, (float)c / (Columns - 1)) * 0.96f;
                    float rise = 0.45f * along * Mathf.Cos(0.5f * Mathf.PI * x / s.floorHalf) * (0.8f + 0.4f * Rough(at + across * x) * 0.5f);
                    var p = at + across * x;
                    int j = i * Columns + c;
                    grid.position[j] = Raised(p, -s.depth + Mathf.Max(0.02f, rise), ground);
                    grid.facing[j] = Vector3.up;
                    grid.uv[j] = TerrainUv(p, uvArea);
                    grid.soil[j] = new Vector2(1, 0.3f);
                }
            }
            for (int k = 0; k < n; k++) Strip(draft, draft.ground, grid, k, 0, Columns - 1, false, null, -1);
        }

        // the direction of a line at an arc position
        static Vector2 TangentAt(List<Vector2> line, float arc)
        {
            for (int i = 0; i + 1 < line.Count; i++)
            {
                float length = Vector2.Distance(line[i], line[i + 1]);
                if (arc <= length || i + 2 == line.Count) return (line[i + 1] - line[i]).normalized;
                arc -= length;
            }
            return Vector2.right;
        }

        // The point of a trench's centreline nearest p, and the direction of the line there; false when p lies past an end of
        // it that opens into another trench (an open end does not reach back across the other's floor).
        static bool Nearest(Line line, Vector2 p, out Vector2 closest, out Vector2 along)
        {
            var points = line.points;
            closest = points[0]; along = Vector2.right;
            float best = float.MaxValue; bool found = false;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                Vector2 a = points[i], ab = points[i + 1] - a;
                float t = Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f);
                if (t < 0 && i == 0 && line.openStart) continue;
                if (t > 1 && i + 2 == points.Count && line.openEnd) continue;
                var q = a + ab * Mathf.Clamp01(t);
                float d = (p - q).sqrMagnitude;
                if (d < best) { best = d; closest = q; along = ab.normalized; found = true; }
            }
            return found;
        }

        static float Distance(List<Vector2> line, Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                Vector2 a = line[i], ab = line[i + 1] - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        // the point at an arc position along a line
        static Vector2 PointAt(List<Vector2> line, float arc)
        {
            for (int i = 0; i + 1 < line.Count; i++)
            {
                float length = Vector2.Distance(line[i], line[i + 1]);
                if (arc <= length || i + 2 == line.Count) return Vector2.Lerp(line[i], line[i + 1], length > 0 ? Mathf.Clamp01(arc / length) : 0);
                arc -= length;
            }
            return line[line.Count - 1];
        }

        // the arc position on a line nearest a point
        static float Project(List<Vector2> line, Vector2 p)
        {
            float best = float.MaxValue, at = 0, arc = 0;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                Vector2 a = line[i], ab = line[i + 1] - a;
                float length = ab.magnitude;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
                float d = Vector2.Distance(p, a + ab * t);
                if (d < best) { best = d; at = arc + t * length; }
                arc += length;
            }
            return at;
        }

        static float Length(List<Vector2> line)
        {
            float length = 0;
            for (int i = 1; i < line.Count; i++) length += Vector2.Distance(line[i - 1], line[i]);
            return length;
        }
    }
}
