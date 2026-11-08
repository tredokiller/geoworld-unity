using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // A power pole of a 0.4 kV line as the towns and villages of Eastern Ukraine have them: a vibrated reinforced-concrete
    // post (СВ-95: 9.5 m, about 1.9 m of it in the ground; 165 mm deep along the line, 224 mm across it at the ground and 165 mm
    // at the top, its edges bevelled), a crossarm of 63 × 5 steel angle on the side of it, held by a strap, and four pin
    // insulators (ТФ-20 porcelain: three phases and the neutral) on steel pins. GeoPowerLines hangs its wires from the
    // insulators' necks (WireTop).
    //
    // Drawn from one atlas (Textures/PowerPoles, Tools~/PowerPoles/power_pole_textures.py): the shaft unrolled into a strip
    // (weathered, aged, scarred by the war), the angle, steel, porcelain, the top and a fracture. Two levels of detail: near,
    // bevelled with lathed insulators (about 700 triangles, 900 broken); far, a tapered box with the crossarm and the
    // insulators as boxes (about 60). A broken pole (GeoPropDamage) is snapped low: its stump with rebar out of the break, and
    // the rest lying along +z, rolled onto the end of its crossarm.
    //
    // A merged world draws its poles as GPU instances (Plant: GeoInstancedMeshes, cells of 128 m, the near shape within
    // 100 m of a cell and the far one to 700 m), not copies of the mesh in the prop chunks; an editable one gives each its
    // own object (Attach).
    public static class GeoPowerPole
    {
        // the shaft
        public const float Height = 7.6f, Buried = 0.35f, Depth = 0.165f, FootWidth = 0.224f, TopWidth = 0.165f, Chamfer = 0.015f;
        // the crossarm: the top of the angle (its horizontal leg on top, reaching away from the pole's -z face), half its length
        public const float ArmTop = 7.32f, ArmHalf = 0.75f, ArmLeg = 0.063f, ArmThickness = 0.005f;
        // the pins stand on the middle of the horizontal leg; an insulator's foot 45 mm over the leg, the wire in its neck
        const float PinZ = -(Depth / 2 + ArmLeg / 2), PinRadius = 0.008f, InsulatorLift = 0.045f, Neck = 0.063f;
        static readonly float[] PinX = { -0.62f, -0.21f, 0.21f, 0.62f };
        // a ТФ-20: (radius, height over its foot), bottom up
        static readonly Vector2[] Insulator =
        {
            new Vector2(0.012f, 0f), new Vector2(0.034f, 0.010f), new Vector2(0.028f, 0.021f), new Vector2(0.020f, 0.033f),
            new Vector2(0.020f, 0.050f), new Vector2(0.025f, 0.056f), new Vector2(0.019f, Neck), new Vector2(0.024f, 0.071f),
            new Vector2(0.017f, 0.081f), new Vector2(0f, 0.085f),
        };
        const int Sides = 8;

        // levels of detail: the near shape within this distance of a cell's bounds, the far one beyond it, nothing past Far
        public const float Near = 100, Far = 700, Cell = 128;

        // the atlas
        public const string Folder = GeoPaths.Package + "/Textures/PowerPoles";
        public static readonly string[] Maps = { "albedo", "normal", "mask" };
        public static string MapPath(string map) => Folder + "/power_pole_" + map + ".png";
        // the shaft's strips: two looks of an intact pole and one scarred by shell fragments (damaged and broken poles)
        public const int Weathered = 0, Aged = 1, Scarred = 2;
        static Rect Strip(int strip) => new Rect(strip * 0.125f, 0, 0.125f, 1);
        static readonly Rect Arm = Inset(0.5f, 0.875f, 0.5f, 0.125f), Steel = Inset(0.5f, 0.75f, 0.5f, 0.125f),
            Porcelain = Inset(0.5f, 0.625f, 0.25f, 0.125f), Top = Inset(0.75f, 0.625f, 0.25f, 0.125f), Fracture = Inset(0.5f, 0.375f, 0.25f, 0.25f);
        static Rect Inset(float x, float y, float w, float h) { const float g = 6f / 2048; return new Rect(x + g, y + g, w - 2 * g, h - 2 * g); }

        // where each corner of the shaft's outline falls across its strip (the faces' share of the outline at mid-height; the
        // texture script draws them there): +z face, bevel, +x face, bevel, -z face (the crossarm's), bevel, -x face, bevel
        static readonly float[] Around = Outline();

        public static int Wires => PinX.Length;

        // The neck of insulator i, where its wire is tied, on an intact pole standing at the origin.
        public static Vector3 WireTop(int i) => new Vector3(PinX[i], ArmTop + InsulatorLift + Neck, PinZ);

        // Half the shaft's width across the line at a height (it tapers from the ground up, and on below it).
        public static float HalfWidth(float y) => 0.5f * Mathf.LerpUnclamped(FootWidth, TopWidth, y / Height);

        // ------------------------------------------------------------------ a pole of a placement

        // A standing pole is turned half round now and then, by its id: its crossarm on the other side and the other faces of
        // its texture to the street (the wires keep their order: GeoPowerLines).
        public static bool Flipped(PropPlacement p, int seed) => p.damage != GeoPropDamage.Broken && GeoPropPlanner.Sample(p.id, seed, 91) < 0.5f;

        // Its rotation: its yaw and lean (GeoPropDamage.Rotation), turned half round when Flipped.
        public static Quaternion Rotation(PropPlacement p, int seed) =>
            Flipped(p, seed) ? GeoPropDamage.Rotation(p) * Quaternion.Euler(0, 180, 0) : GeoPropDamage.Rotation(p);

        // Its shape: broken (snapped, one of four by GeoPropDamage.Variant), else intact, scarred where it was hit.
        public static string Key(PropPlacement p, int seed)
        {
            if (p.damage == GeoPropDamage.Broken) return "broken " + GeoPropDamage.Variant(p, seed).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            if (p.damage != GeoPropDamage.Intact) return "scarred";
            return GeoPropPlanner.Sample(p.id, seed, 92) < 0.5f ? "weathered" : "aged";
        }

        // Where the wires of a pole are tied, in the order of a pole that is not Flipped (null: gone). A broken pole's lie on
        // the ground by its top, torn off its insulators.
        public static Vector3[] WireTops(PropPlacement p, int seed)
        {
            var result = new Vector3[Wires];
            if (GeoPropDamage.Fallen(p))
            {
                var tops = BrokenWireTops(GeoPropDamage.Variant(p, seed));
                var turn = Quaternion.Euler(0, GeoData.Finite(p.yaw) ? p.yaw : 0, 0);
                for (int i = 0; i < result.Length; i++) result[i] = p.position + turn * tops[i];
                return result;
            }
            var rotation = Rotation(p, seed);
            bool flipped = Flipped(p, seed);
            float lift = GeoPropDamage.Lift(rotation, IntactBounds, Vector3.one, p.lean);
            for (int i = 0; i < result.Length; i++) result[i] = p.position + Vector3.up * lift + rotation * WireTop(flipped ? result.Length - 1 - i : i);
            return result;
        }

        static Bounds? intactBounds;
        static readonly object locker = new object();

        // The bounds of the intact pole's near shape (standing at the origin).
        public static Bounds IntactBounds
        {
            get
            {
                lock (locker) return intactBounds ??= Build(Weathered, false, 0, true).Bounds();
            }
        }

        static readonly Dictionary<float, Vector3[]> brokenTops = new Dictionary<float, Vector3[]>();

        // The insulators' necks of a broken pole's top as it lies, on the ground.
        public static Vector3[] BrokenWireTops(float variant)
        {
            lock (locker)
            {
                if (brokenTops.TryGetValue(variant, out var cached)) return cached;
                var lay = Lay(variant, true);
                var result = new Vector3[Wires];
                for (int i = 0; i < result.Length; i++) { result[i] = lay.MultiplyPoint3x4(WireTop(i)); result[i].y = 0.04f; }
                brokenTops[variant] = result;
                return result;
            }
        }

        // ------------------------------------------------------------------ meshes and drawing

        // The near and far meshes of one shape.
        public sealed class Shape { public Mesh near, far; }

        // The shapes and material of one build (meshes and the material saved once each).
        public sealed class Set
        {
            readonly Dictionary<string, Shape> shapes = new Dictionary<string, Shape>();
            readonly Action<Object> save;
            Material material;
            public Set(Action<Object> save) { this.save = save; }

            public Material Material => material ? material : material = MakeMaterial(save);

            public Shape Of(string key)
            {
                if (shapes.TryGetValue(key, out var shape)) return shape;
                bool broken = key.StartsWith("broken ", StringComparison.Ordinal);
                float variant = broken ? float.Parse(key.Substring(7), System.Globalization.CultureInfo.InvariantCulture) : 0;
                int strip = key == "aged" ? Aged : broken || key == "scarred" ? Scarred : Weathered;
                shape = new Shape
                {
                    near = Build(strip, broken, variant, true).Mesh("Power pole " + key),
                    far = Build(strip, broken, variant, false).Mesh("Power pole " + key + " (far)"),
                };
                save?.Invoke(shape.near); save?.Invoke(shape.far);
                shapes[key] = shape;
                return shape;
            }
        }

        // The poles of a merged world as GPU instances under parent: one GeoInstancedMeshes, a batch for each cell, shape and
        // level of detail. Returns how many were planted.
        public static int Plant(IList<PropPlacement> poles, Transform parent, Action<Object> save, int seed)
        {
            var set = new Set(save);
            var lists = new Dictionary<(Vector2Int cell, string key), List<Matrix4x4>>();
            int planted = 0;
            foreach (var p in poles)
            {
                if (!GeoData.Finite(p.position.x) || !GeoData.Finite(p.position.y) || !GeoData.Finite(p.position.z)) continue;
                var key = (new Vector2Int(Mathf.FloorToInt(p.position.x / Cell), Mathf.FloorToInt(p.position.z / Cell)), Key(p, seed));
                if (!lists.TryGetValue(key, out var list)) lists[key] = list = new List<Matrix4x4>();
                list.Add(Matrix(p, seed));
                planted++;
            }
            if (planted == 0) return 0;
            var go = new GameObject("Power poles (" + planted + " instances)");
            go.transform.SetParent(parent, false);
            var drawer = go.AddComponent<GeoInstancedMeshes>();
            drawer.count = planted;
            drawer.drawDistance = Far;
            var material = set.Material;
            var keys = new List<(Vector2Int cell, string key)>(lists.Keys);
            keys.Sort((a, b) => a.cell.y != b.cell.y ? a.cell.y.CompareTo(b.cell.y) : a.cell.x != b.cell.x ? a.cell.x.CompareTo(b.cell.x) : string.CompareOrdinal(a.key, b.key));
            foreach (var key in keys)
            {
                var shape = set.Of(key.key);
                var matrices = lists[key].ToArray();
                // both levels by the near shape's bounds, so exactly one of them is drawn at any distance
                var bounds = GeoInstancedMeshes.WorldBounds(shape.near.bounds, matrices);
                drawer.batches.Add(new GeoInstancedMeshes.Batch { mesh = shape.near, material = material, bounds = bounds, matrices = matrices, far = Near });
                drawer.batches.Add(new GeoInstancedMeshes.Batch { mesh = shape.far, material = material, bounds = bounds, matrices = matrices, near = Near });
            }
            return planted;
        }

        // A pole's placement in the world: its position (lifted when knocked flat), rotation and scale (its mapped height).
        public static Matrix4x4 Matrix(PropPlacement p, int seed)
        {
            var rotation = Rotation(p, seed);
            var scale = GeoPropBuilder.Scale(p, IntactBounds.size);
            float lift = GeoPropDamage.Lift(rotation, IntactBounds, scale, p.lean);
            return Matrix4x4.TRS(p.position + Vector3.up * lift, rotation, scale);
        }

        // An editable world's pole: its near mesh on the object, the far one on a child, an LODGroup between them.
        public static void Attach(GameObject go, PropPlacement p, Set set, int seed)
        {
            var shape = set.Of(Key(p, seed));
            var m = Matrix(p, seed);
            go.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
            go.transform.localScale = m.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = shape.near;
            var near = go.AddComponent<MeshRenderer>(); near.sharedMaterial = set.Material;
            var child = new GameObject("Far");
            child.transform.SetParent(go.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = shape.far;
            var far = child.AddComponent<MeshRenderer>(); far.sharedMaterial = set.Material;
            var lod = go.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0.07f, new Renderer[] { near }), new LOD(0.01f, new Renderer[] { far }) });
            lod.RecalculateBounds();
        }

        // The far shape as flat geometry for GeoPropBuilder.Drafts (its size and bounds; previews without the atlas).
        public static void Draft(MeshDraft draft, int damage, float variant)
        {
            var b = Build(Weathered, damage == GeoPropDamage.Broken, variant, false);
            int first = draft.vertices.Count;
            draft.vertices.AddRange(b.v);
            for (int i = 0; i < b.v.Count; i++) draft.uv.Add(b.uv[i]);
            foreach (int t in b.t) draft.triangles.Add(first + t);
        }

        // URP Lit with the atlas (R metallic, G occlusion, A smoothness in the mask), or plain concrete without it.
        public static Material MakeMaterial(Action<Object> save)
        {
            var shader = GeoAssets.Shader("Universal Render Pipeline/Lit") ?? GeoAssets.Shader("Standard");
            var material = new Material(shader) { name = "Power poles", enableInstancing = true };
            var albedo = GeoAssets.Load<Texture2D>(MapPath("albedo"));
            if (albedo)
            {
                material.SetTexture("_BaseMap", albedo); material.mainTexture = albedo; material.color = Color.white;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
                var normal = GeoAssets.Load<Texture2D>(MapPath("normal"));
                if (normal) { material.SetTexture("_BumpMap", normal); material.EnableKeyword("_NORMALMAP"); material.SetFloat("_BumpScale", 1f); }
                var mask = GeoAssets.Load<Texture2D>(MapPath("mask"));
                if (mask)
                {
                    material.SetTexture("_MetallicGlossMap", mask); material.EnableKeyword("_METALLICSPECGLOSSMAP");
                    material.SetFloat("_Metallic", 0); material.SetFloat("_Smoothness", 1); material.SetFloat("_SmoothnessTextureChannel", 0);
                    material.SetTexture("_OcclusionMap", mask); material.EnableKeyword("_OCCLUSIONMAP"); material.SetFloat("_OcclusionStrength", 1);
                }
            }
            else
            {
                material.color = GeoPropBuilder.Concrete;
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", GeoPropBuilder.Concrete);
                material.SetFloat("_Smoothness", 0.12f);
            }
            save?.Invoke(material);
            return material;
        }

        // ------------------------------------------------------------------ geometry

        sealed class Builder
        {
            public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
            public readonly List<Vector2> uv = new List<Vector2>();
            public readonly List<int> t = new List<int>();

            public int Vertex(Vector3 p, Vector3 normal, Vector2 u) { v.Add(p); n.Add(normal); uv.Add(u); return v.Count - 1; }

            // A flat polygon (3 or 4 corners, in order round it) facing `outward`, with its own vertices.
            public void Face(Vector3 outward, Vector3[] p, Vector2[] u)
            {
                var normal = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
                if (normal.sqrMagnitude < 1e-14f && p.Length > 3) normal = Vector3.Cross(p[2] - p[0], p[3] - p[0]);
                if (normal.sqrMagnitude < 1e-14f) return;
                normal.Normalize();
                bool flip = Vector3.Dot(normal, outward) < 0;
                if (flip) normal = -normal;
                int first = v.Count;
                for (int i = 0; i < p.Length; i++) Vertex(p[i], normal, u[i]);
                for (int i = 1; i + 1 < p.Length; i++)
                {
                    if (flip) { t.Add(first); t.Add(first + i + 1); t.Add(first + i); }
                    else { t.Add(first); t.Add(first + i); t.Add(first + i + 1); }
                }
            }

            public void Quad(Vector3 outward, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud) =>
                Face(outward, new[] { a, b, c, d }, new[] { ua, ub, uc, ud });

            public void Transform(Matrix4x4 m, int from)
            {
                for (int i = from; i < v.Count; i++) { v[i] = m.MultiplyPoint3x4(v[i]); n[i] = m.MultiplyVector(n[i]).normalized; }
            }

            public Bounds Bounds()
            {
                var b = new Bounds(v.Count > 0 ? v[0] : Vector3.zero, Vector3.zero);
                foreach (var p in v) b.Encapsulate(p);
                return b;
            }

            public Mesh Mesh(string name)
            {
                var mesh = new Mesh { name = name, indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv); mesh.SetTriangles(t, 0);
                mesh.RecalculateBounds(); mesh.RecalculateTangents();
                return mesh;
            }
        }

        static Vector2 In(Rect r, float s, float t) => new Vector2(r.x + r.width * Mathf.Clamp01(s), r.y + r.height * Mathf.Clamp01(t));

        // The share of the outline up to each corner (0..1, nine values: the last closes it), at mid-height.
        static float[] Outline()
        {
            var o = Corners(Height / 2, 0);
            var result = new float[9];
            float total = 0;
            for (int i = 0; i < 8; i++) total += Vector2.Distance(o[i], o[(i + 1) % 8]);
            float sum = 0;
            for (int i = 0; i < 8; i++) { result[i] = sum / total; sum += Vector2.Distance(o[i], o[(i + 1) % 8]); }
            result[8] = 1;
            return result;
        }

        // The eight corners of the bevelled section at height y (x, z), grown by `grow` all round: from the left end of the
        // +z face round by +x, -z and -x.
        static Vector2[] Corners(float y, float grow)
        {
            float hx = HalfWidth(y) + grow, hz = Depth / 2 + grow, c = Chamfer;
            return new[]
            {
                new Vector2(-(hx - c), hz), new Vector2(hx - c, hz), new Vector2(hx, hz - c), new Vector2(hx, -(hz - c)),
                new Vector2(hx - c, -hz), new Vector2(-(hx - c), -hz), new Vector2(-hx, -(hz - c)), new Vector2(-hx, hz - c),
            };
        }

        // A column of the outline: its corners (even, of `columns` 16) or the middles of its faces (odd); 8 columns: the corners.
        static Vector3 Column(int j, int columns, float y)
        {
            var o = Corners(y, 0);
            if (columns == 8) { var c = o[j % 8]; return new Vector3(c.x, y, c.y); }
            int k = (j % 16) / 2;
            var p = j % 2 == 0 ? o[k] : (o[k] + o[(k + 1) % 8]) / 2;
            return new Vector3(p.x, y, p.y);
        }

        static float ColumnU(int j, int columns)
        {
            if (columns == 8) return Around[j];
            int k = j / 2;
            return j % 2 == 0 ? Around[k] : (Around[k] + Around[k + 1]) / 2;
        }

        static float V(float y) => (y + Buried) / (Height + Buried);

        // The shaft, face by face, between per-column heights (columns: 8 corners, or 16 for a jagged break).
        static void Shaft(Builder b, int strip, int columns, Func<int, float> bottom, Func<int, float> top)
        {
            var r = Strip(strip);
            for (int j = 0; j < columns; j++)
            {
                int k = j + 1;
                float b0 = bottom(j % columns), b1 = bottom(k % columns), t0 = top(j % columns), t1 = top(k % columns);
                Vector3 p0 = Column(j, columns, b0), p1 = Column(k, columns, b1), p2 = Column(k, columns, t1), p3 = Column(j, columns, t0);
                var mid = (p0 + p1) / 2; var outward = new Vector3(mid.x, 0, mid.z);
                float u0 = ColumnU(j, columns), u1 = ColumnU(k, columns);
                b.Quad(outward, p0, p1, p2, p3, In(r, u0, V(b0)), In(r, u1, V(b1)), In(r, u1, V(t1)), In(r, u0, V(t0)));
            }
        }

        // A flat end of the shaft at y (up: its top).
        static void EndCap(Builder b, float y, bool up, int columns = 8)
        {
            var p = new Vector3[columns]; var u = new Vector2[columns];
            for (int j = 0; j < columns; j++) { p[j] = Column(j, columns, y); u[j] = In(Top, 0.5f + p[j].x / 0.32f, 0.5f + p[j].z / 0.32f); }
            b.Face(up ? Vector3.up : Vector3.down, p, u);
        }

        // A jagged break of the shaft: a fan from its middle (at `middle`) to the columns' ends (heights by `edge`); up: the
        // stump's top, else the underside of the piece above.
        static void Break(Builder b, Func<int, float> edge, float middle, bool up)
        {
            const int columns = 16;
            var centre = new Vector3(0, middle, 0);
            var uc = In(Fracture, 0.5f, 0.5f);
            for (int j = 0; j < columns; j++)
            {
                var p0 = Column(j, columns, edge(j)); var p1 = Column(j + 1, columns, edge((j + 1) % columns));
                b.Face(up ? Vector3.up : Vector3.down, new[] { centre, p0, p1 },
                    new[] { uc, In(Fracture, 0.5f + p0.x / 0.34f, 0.5f + p0.z / 0.34f), In(Fracture, 0.5f + p1.x / 0.34f, 0.5f + p1.z / 0.34f) });
            }
        }

        // Rebar out of a break: four bars near the corners, from inside the concrete out along `dir` (up or down) and bent.
        static void Rebar(Builder b, float y, float dir, System.Random random)
        {
            float hx = HalfWidth(y) - 0.035f, hz = Depth / 2 - 0.035f;
            foreach (var corner in new[] { new Vector2(-hx, -hz), new Vector2(hx, -hz), new Vector2(hx, hz), new Vector2(-hx, hz) })
            {
                if (random.NextDouble() < 0.2) continue; // one snapped off flush now and then
                float length = 0.1f + 0.22f * (float)random.NextDouble();
                var start = new Vector3(corner.x, y - dir * 0.04f, corner.y);
                var mid = new Vector3(corner.x, y + dir * length * 0.45f, corner.y);
                var bend = new Vector3(corner.x * (0.8f + 2.5f * (float)random.NextDouble()), dir * length * 0.6f, corner.y * (0.8f + 2.5f * (float)random.NextDouble()));
                Rod(b, start, mid, 0.0055f, 3, Steel);
                Rod(b, mid, mid + bend.normalized * length * 0.6f, 0.0055f, 3, Steel);
            }
        }

        // A thin prism from a to b (sides 3..6), its ends open.
        static void Rod(Builder b, Vector3 a, Vector3 c, float radius, int sides, Rect region)
        {
            var axis = (c - a).normalized;
            var side = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            var other = Vector3.Cross(axis, side);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2 / sides, a1 = (i + 1) * Mathf.PI * 2 / sides;
                var d0 = side * Mathf.Cos(a0) + other * Mathf.Sin(a0); var d1 = side * Mathf.Cos(a1) + other * Mathf.Sin(a1);
                float s0 = i / (float)sides, s1 = (i + 1) / (float)sides;
                b.Quad(d0 + d1, a + d0 * radius, a + d1 * radius, c + d1 * radius, c + d0 * radius,
                    In(region, s0, 0.1f), In(region, s1, 0.1f), In(region, s1, 0.9f), In(region, s0, 0.9f));
            }
        }

        // The crossarm: steel angle along x on the -z face, its horizontal leg on top.
        static void Crossarm(Builder b)
        {
            float zf = -Depth / 2, yt = ArmTop, L = ArmLeg, th = ArmThickness;
            // the outline round the angle (z, y) and how far round it each corner is (for the texture's v)
            var o = new[] { new Vector2(zf, yt), new Vector2(zf - L, yt), new Vector2(zf - L, yt - th), new Vector2(zf - th, yt - th), new Vector2(zf - th, yt - L), new Vector2(zf, yt - L) };
            var normals = new[] { Vector3.up, Vector3.back, Vector3.down, Vector3.back, Vector3.down };
            float total = 0; for (int i = 0; i < 5; i++) total += Vector2.Distance(o[i], o[i + 1]);
            float run = 0;
            for (int i = 0; i < 5; i++)
            {
                float next = run + Vector2.Distance(o[i], o[i + 1]);
                Vector3 a = new Vector3(-ArmHalf, o[i].y, o[i].x), c = new Vector3(-ArmHalf, o[i + 1].y, o[i + 1].x);
                Vector3 a2 = new Vector3(ArmHalf, o[i].y, o[i].x), c2 = new Vector3(ArmHalf, o[i + 1].y, o[i + 1].x);
                b.Quad(normals[i], a, a2, c2, c, In(Arm, 0, run / total), In(Arm, 1, run / total), In(Arm, 1, next / total), In(Arm, 0, next / total));
                run = next;
            }
            // the vertical leg's inner face, seen from the pole's side past the shaft (against it, facing away: hidden there)
            b.Quad(Vector3.forward, new Vector3(-ArmHalf, yt - L, zf), new Vector3(ArmHalf, yt - L, zf), new Vector3(ArmHalf, yt, zf), new Vector3(-ArmHalf, yt, zf),
                In(Arm, 0, 0.62f), In(Arm, 1, 0.62f), In(Arm, 1, 0.95f), In(Arm, 0, 0.95f));
            // the ends: the angle's section, as two rectangles
            foreach (float x in new[] { -ArmHalf, ArmHalf })
            {
                var outward = new Vector3(Mathf.Sign(x), 0, 0);
                Vector3 P(float z, float y) => new Vector3(x, y, z);
                Vector2 U(float z, float y) => In(Steel, 0.02f + (zf - z) * 0.2f, 0.2f + (yt - y) * 6f);
                b.Quad(outward, P(zf, yt), P(zf - L, yt), P(zf - L, yt - th), P(zf, yt - th), U(zf, yt), U(zf - L, yt), U(zf - L, yt - th), U(zf, yt - th));
                b.Quad(outward, P(zf, yt - th), P(zf - th, yt - th), P(zf - th, yt - L), P(zf, yt - L), U(zf, yt - th), U(zf - th, yt - th), U(zf - th, yt - L), U(zf, yt - L));
            }
        }

        // The strap round the pole that holds the crossarm, over the angle's vertical leg on the -z side.
        static void Strap(Builder b)
        {
            float y0 = ArmTop - 0.040f, y1 = ArmTop - 0.014f;
            Vector3 P(float y, int j)
            {
                var o = Corners(y, 0.004f)[j % 8];
                // the -z side passes over the vertical leg of the angle
                float z = o.y < -Depth / 2 + Chamfer + 0.0001f ? o.y - ArmThickness : o.y;
                return new Vector3(o.x, y, z);
            }
            for (int j = 0; j < 8; j++)
            {
                Vector3 a = P(y0, j), c = P(y0, j + 1), d = P(y1, j + 1), e = P(y1, j);
                var mid = (a + c) / 2;
                b.Quad(new Vector3(mid.x, 0, mid.z), a, c, d, e, In(Steel, j / 8f, 0.55f), In(Steel, (j + 1) / 8f, 0.55f), In(Steel, (j + 1) / 8f, 0.75f), In(Steel, j / 8f, 0.75f));
            }
        }

        // A pin with its nut on the crossarm and the insulator on it, at x.
        static void PinAndInsulator(Builder b, float x)
        {
            var foot = new Vector3(x, ArmTop, PinZ);
            Rod(b, foot, foot + Vector3.up * (InsulatorLift + 0.012f), PinRadius, 6, Steel);
            // the nut: a hexagon on the leg
            var nut = new Vector3[6];
            for (int i = 0; i < 6; i++) { float a = i * Mathf.PI / 3 + Mathf.PI / 6; nut[i] = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.0135f; }
            for (int i = 0; i < 6; i++)
            {
                Vector3 a = foot + nut[i], c = foot + nut[(i + 1) % 6];
                b.Quad(nut[i] + nut[(i + 1) % 6], a, c, c + Vector3.up * 0.01f, a + Vector3.up * 0.01f,
                    In(Steel, 0.6f + i * 0.02f, 0.2f), In(Steel, 0.62f + i * 0.02f, 0.2f), In(Steel, 0.62f + i * 0.02f, 0.3f), In(Steel, 0.6f + i * 0.02f, 0.3f));
            }
            var top = new Vector3[6]; var tu = new Vector2[6];
            for (int i = 0; i < 6; i++) { top[i] = foot + nut[i] + Vector3.up * 0.01f; tu[i] = In(Steel, 0.7f + nut[i].x * 2, 0.25f + nut[i].z * 2); }
            b.Face(Vector3.up, top, tu);
            Lathe(b, foot + Vector3.up * InsulatorLift);
        }

        // An insulator turned round its axis at `foot`: smooth, its profile Insulator.
        static void Lathe(Builder b, Vector3 foot)
        {
            int rings = Insulator.Length;
            var along = new float[rings];
            for (int i = 1; i < rings; i++) along[i] = along[i - 1] + Vector2.Distance(Insulator[i - 1], Insulator[i]);
            int first = b.v.Count;
            for (int i = 0; i < rings; i++)
            {
                // the profile's normal (outward, from the slope either side of the ring)
                var before = Insulator[Mathf.Max(0, i - 1)]; var after = Insulator[Mathf.Min(rings - 1, i + 1)];
                var slope = (after - before).normalized;
                var pn = new Vector2(slope.y, -slope.x); // (radial, up)
                for (int s = 0; s <= Sides; s++)
                {
                    float a = s * Mathf.PI * 2 / Sides;
                    var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    var p = foot + dir * Insulator[i].x + Vector3.up * Insulator[i].y;
                    var normal = (dir * pn.x + Vector3.up * pn.y).normalized;
                    b.Vertex(p, normal, In(Porcelain, s / (float)Sides, along[i] / along[rings - 1]));
                }
            }
            int row = Sides + 1;
            for (int i = 0; i + 1 < rings; i++)
                for (int s = 0; s < Sides; s++)
                {
                    int a = first + i * row + s, c = a + 1, d = a + row + 1, e = a + row;
                    // facing out: (a, e, d) and (a, d, c); none where a ring closes to the axis
                    if (Insulator[i + 1].x > 0) { b.t.Add(a); b.t.Add(e); b.t.Add(d); }
                    if (Insulator[i].x > 0) { b.t.Add(a); b.t.Add(d); b.t.Add(c); }
                }
        }

        // The near shape of the crossarm and what is on it.
        static void Head(Builder b, bool near)
        {
            if (near)
            {
                Crossarm(b);
                Strap(b);
                foreach (float x in PinX) PinAndInsulator(b, x);
                EndCap(b, Height, true);
                return;
            }
            // far: the crossarm a box, the insulators boxes
            float zf = -Depth / 2;
            Box(b, new Vector3(0, ArmTop - ArmLeg / 2, zf - ArmLeg / 2), new Vector3(ArmHalf * 2, ArmLeg, ArmLeg), Arm, false);
            foreach (float x in PinX)
                Box(b, new Vector3(x, ArmTop + 0.065f, PinZ), new Vector3(0.05f, 0.13f, 0.05f), Porcelain, false);
        }

        // A box about its centre, without its bottom unless asked.
        static void Box(Builder b, Vector3 centre, Vector3 size, Rect region, bool bottom)
        {
            var h = size / 2;
            Vector3 P(float x, float y, float z) => centre + new Vector3(x * h.x, y * h.y, z * h.z);
            Vector2 U(float s, float t) => In(region, s, t);
            b.Quad(Vector3.forward, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), U(0, 0), U(1, 0), U(1, 1), U(0, 1));
            b.Quad(Vector3.back, P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), U(0, 0), U(1, 0), U(1, 1), U(0, 1));
            b.Quad(Vector3.right, P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), U(0, 0), U(0.1f, 0), U(0.1f, 1), U(0, 1));
            b.Quad(Vector3.left, P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), U(0, 0), U(0.1f, 0), U(0.1f, 1), U(0, 1));
            b.Quad(Vector3.up, P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), U(0, 0.9f), U(1, 0.9f), U(1, 1), U(0, 1));
            if (bottom) b.Quad(Vector3.down, P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), U(0, 0), U(1, 0), U(1, 0.1f), U(0, 0.1f));
        }

        // The far shaft: four tapered faces between y0 and y1 (corners where the bevels' middles are).
        static void FarShaft(Builder b, int strip, float y0, float y1)
        {
            var r = Strip(strip);
            float[] u = { 0, (Around[1] + Around[2]) / 2, (Around[3] + Around[4]) / 2, (Around[5] + Around[6]) / 2, 1 };
            Vector3 P(int k, float y) { float hx = HalfWidth(y), hz = Depth / 2; var c = new[] { new Vector2(-hx, hz), new Vector2(hx, hz), new Vector2(hx, -hz), new Vector2(-hx, -hz) }[k % 4]; return new Vector3(c.x, y, c.y); }
            for (int k = 0; k < 4; k++)
            {
                Vector3 a = P(k, y0), c = P(k + 1, y0), d = P(k + 1, y1), e = P(k, y1);
                var mid = (a + c) / 2;
                b.Quad(new Vector3(mid.x, 0, mid.z), a, c, d, e, In(r, u[k], V(y0)), In(r, u[k + 1], V(y0)), In(r, u[k + 1], V(y1)), In(r, u[k], V(y1)));
            }
        }

        static void FarCap(Builder b, float y, bool up, Rect region)
        {
            float hx = HalfWidth(y), hz = Depth / 2;
            var p = new[] { new Vector3(-hx, y, hz), new Vector3(hx, y, hz), new Vector3(hx, y, -hz), new Vector3(-hx, y, -hz) };
            b.Face(up ? Vector3.up : Vector3.down, p, new[] { In(region, 0.3f, 0.3f), In(region, 0.7f, 0.3f), In(region, 0.7f, 0.7f), In(region, 0.3f, 0.7f) });
        }

        // How a broken pole breaks: the height of its stump (1.2..2.8 m), which way its top rolls onto the crossarm's end.
        static float StumpHeight(float variant) => 1.2f + variant * 1.6f;

        // The top of a broken pole laid down: from standing (its break at the stump's height) to lying along +z from just past
        // the stump, rolled about its axis onto the end of its crossarm, resting on the ground (its near or far shape).
        static Matrix4x4 Lay(float variant, bool near)
        {
            float stump = StumpHeight(variant);
            // 14°: the end of the crossarm and the shaft's edge both on the ground
            float roll = variant < 0.5f ? 14f : -14f;
            Matrix4x4 Laid(float pitch) => Matrix4x4.Translate(new Vector3(0, 0, 0.3f)) * Matrix4x4.Rotate(Quaternion.Euler(90 + pitch, 0, 0)) *
                Matrix4x4.Rotate(Quaternion.Euler(0, roll, 0)) * Matrix4x4.Translate(new Vector3(0, -stump, 0));
            var piece = new Builder();
            TopPiece(piece, Scarred, variant, near);
            // the shaft is thicker at the break than at the top: tipped down along its length until its head lies on the
            // ground too (on the end of the crossarm), not only the edge of the break
            var m = Laid(0);
            float atBreak = float.MaxValue, atHead = float.MaxValue;
            foreach (var p in piece.v)
            {
                float y = m.MultiplyPoint3x4(p).y;
                if (p.y < stump + 0.6f) atBreak = Mathf.Min(atBreak, y);
                else if (p.y > ArmTop - 0.4f) atHead = Mathf.Min(atHead, y);
            }
            if (atBreak < float.MaxValue && atHead < float.MaxValue) m = Laid(Mathf.Atan2(atHead - atBreak, ArmTop - stump) * Mathf.Rad2Deg);
            // resting on its lowest point
            float low = float.MaxValue;
            foreach (var p in piece.v) low = Mathf.Min(low, m.MultiplyPoint3x4(p).y);
            return Matrix4x4.Translate(Vector3.up * (-low - 0.01f)) * m;
        }

        static float Jag(System.Random random, float amount) => ((float)random.NextDouble() * 2 - 1) * amount;

        // The top of a broken pole as it stood: from its jagged break up, with its head.
        static void TopPiece(Builder b, int strip, float variant, bool near)
        {
            float stump = StumpHeight(variant);
            if (!near)
            {
                FarShaft(b, strip, stump, Height);
                FarCap(b, stump, false, Fracture);
                FarCap(b, Height, true, Top);
                Head(b, false);
                return;
            }
            var random = new System.Random(1000 + Mathf.RoundToInt(variant * 1000));
            var edge = new float[16];
            for (int j = 0; j < 16; j++) edge[j] = stump + Jag(random, 0.07f);
            Shaft(b, strip, 16, j => edge[j], j => Height);
            Break(b, j => edge[j], stump + Jag(random, 0.05f), false);
            Rebar(b, stump, -1, random);
            Head(b, true);
        }

        // A pole's shape at the origin: near or far, intact or broken (variant: GeoPropDamage.Variant), its shaft from `strip`.
        static Builder Build(int strip, bool broken, float variant, bool near)
        {
            var b = new Builder();
            if (!broken)
            {
                if (near) Shaft(b, strip, 8, j => -Buried, j => Height);
                else { FarShaft(b, strip, -Buried, Height); FarCap(b, Height, true, Top); }
                Head(b, near);
                return b;
            }
            float stump = StumpHeight(variant);
            // the stump, its break jagged and rebar out of it
            if (near)
            {
                var random = new System.Random(2000 + Mathf.RoundToInt(variant * 1000));
                var edge = new float[16];
                for (int j = 0; j < 16; j++) edge[j] = stump + Jag(random, 0.08f);
                Shaft(b, strip, 16, j => -Buried, j => edge[j]);
                Break(b, j => edge[j], stump + Jag(random, 0.06f), true);
                Rebar(b, stump, 1, random);
            }
            else { FarShaft(b, strip, -Buried, stump); FarCap(b, stump, true, Fracture); }
            // the top, laid down
            int from = b.v.Count;
            TopPiece(b, strip, variant, near);
            b.Transform(Lay(variant, near), from);
            return b;
        }
    }
}
