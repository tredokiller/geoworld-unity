using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // A merged city bakes its trees, fences, walls and street props like its buildings: one object per tree would
    // stall the editor (a city has tens of thousands), a chunk of a few hundred metres does not. Colours go through
    // the same palette texture as the buildings, so a whole city is a few hundred objects and one material.
    // Props with a prefab in the prop catalog, and props placed from photo references, stay objects of their own.
    public static class GeoPropBaker
    {
        public static bool Bakes(PropPlacement placement, GenerationSettings settings) =>
            placement.source != "manual_photo" && (!settings.propCatalog || settings.propCatalog.Find(placement.kind) == null);

        sealed class Chunk
        {
            public readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> triangles = new List<int>();
            public int props, trees;
        }

        sealed class Model { public Dictionary<Color, MeshDraft> drafts; public Vector3 size; }

        // A low-poly tree, 7 m tall and 4 m across, as one smooth-shaded mesh: a trunk and a crown of two cones.
        internal sealed class TreeModel { public Vector3[] vertices, normals; public bool[] trunk; public int[] triangles; }

        // Returns the number of chunks made. `group` receives one child per chunk.
        public static int Build(IEnumerable<PropPlacement> placements, Transform group, float chunkSize, Material palette, Action<Object> save, int seed)
        {
            float size = Mathf.Max(50, chunkSize);
            var chunks = new Dictionary<Vector2Int, Chunk>();
            var models = new Dictionary<string, Model>();
            TreeModel tree = null;
            foreach (var p in placements)
            {
                if (!GeoData.Finite(p.position.x) || !GeoData.Finite(p.position.y) || !GeoData.Finite(p.position.z)) continue;
                var key = new Vector2Int(Mathf.FloorToInt(p.position.x / size), Mathf.FloorToInt(p.position.z / size));
                if (!chunks.TryGetValue(key, out var chunk)) chunks[key] = chunk = new Chunk();
                var rotation = Quaternion.Euler(0, GeoData.Finite(p.yaw) ? p.yaw : 0, 0);
                if (p.kind == "tree") { tree = tree ?? MakeTree(); AppendTree(chunk, tree, p, rotation, seed); chunk.trees++; }
                else if (p.segment) AppendDrafts(chunk, Segment(p), Matrix4x4.TRS(p.position, rotation, Vector3.one));
                else
                {
                    if (!models.TryGetValue(p.kind, out var model)) models[p.kind] = model = MakeModel(p.kind);
                    float sx = p.width > 0 && GeoData.Finite(p.width) ? p.width / Mathf.Max(model.size.x, 0.01f) : 1;
                    float sy = p.height > 0 && GeoData.Finite(p.height) ? p.height / Mathf.Max(model.size.y, 0.01f) : 1;
                    AppendDrafts(chunk, model.drafts, Matrix4x4.TRS(p.position, rotation, new Vector3(sx, sy, 1)));
                }
                chunk.props++;
            }
            foreach (var pair in chunks)
            {
                var chunk = pair.Value;
                if (chunk.vertices.Count == 0) continue;
                string name = "Props " + pair.Key.x + "_" + pair.Key.y;
                var mesh = new Mesh { name = name, indexFormat = chunk.vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(chunk.vertices);
                mesh.SetNormals(chunk.normals);
                mesh.SetUVs(0, chunk.uvs);
                mesh.SetTriangles(chunk.triangles, 0);
                mesh.RecalculateBounds();
                mesh = GeoMeshCompression.Compact(mesh);
                save(mesh);
                var go = new GameObject(name + " (" + chunk.props + " props, " + chunk.trees + " trees)");
                go.transform.SetParent(group, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = palette;
                // fences, walls and street props are small however big the chunk is: drawn to about a kilometre
                var lod = go.AddComponent<LODGroup>();
                lod.SetLODs(new[] { new LOD(0.02f, new Renderer[] { renderer }) });
                lod.size = 20;
            }
            return chunks.Count;
        }

        // The palette material of the building chunks of this world, or a new one when there are none.
        public static Material PaletteOf(Transform world, Action<Object> save)
        {
            if (world)
                foreach (var chunk in world.GetComponentsInChildren<GeoCityChunk>(true))
                {
                    var renderer = chunk.GetComponent<MeshRenderer>();
                    if (renderer && renderer.sharedMaterial) return renderer.sharedMaterial;
                }
            return GeoCityBuilder.PaletteMaterial(save);
        }

        // ------------------------------------------------------------------ models

        static Model MakeModel(string kind)
        {
            var drafts = GeoPropBuilder.Drafts(kind);
            Vector3 min = Vector3.one * float.MaxValue, max = Vector3.one * float.MinValue;
            foreach (var draft in drafts.Values)
                foreach (var v in draft.vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            return new Model { drafts = drafts, size = drafts.Count == 0 ? Vector3.one : max - min };
        }

        // A fence, wall or hedge piece in real dimensions: z is its length, so a long piece is not a stretched model.
        static Dictionary<Color, MeshDraft> Segment(PropPlacement p)
        {
            var drafts = new Dictionary<Color, MeshDraft>();
            MeshDraft Draft(Color c) { if (!drafts.TryGetValue(c, out var d)) drafts[c] = d = new MeshDraft(); return d; }
            float w = Mathf.Max(0.02f, GeoData.Finite(p.width) ? p.width : 0.1f);
            float h = Mathf.Max(0.1f, GeoData.Finite(p.height) ? p.height : 1f);
            float l = Mathf.Max(0.1f, GeoData.Finite(p.length) ? p.length : 1f);
            switch (p.kind)
            {
                case "wall": GeoPropBuilder.AddBox(Draft(GeoPropBuilder.Concrete), 0, h / 2, 0, w, h, l); break;
                case "hedge": GeoPropBuilder.AddBox(Draft(GeoPropBuilder.Leaf), 0, h / 2, 0, w, h, l); break;
                default: // fence and guard rail: a post at each end and rails between
                    var metal = Draft(GeoPropBuilder.Metal);
                    float post = Mathf.Min(0.12f, l / 2);
                    foreach (float z in new[] { -(l - post) / 2, (l - post) / 2 }) GeoPropBuilder.AddBox(metal, 0, h / 2, z, post, h, post);
                    if (p.kind == "guard_rail") GeoPropBuilder.AddBox(metal, 0, h * 0.7f, 0, 0.1f, h * 0.35f, l);
                    else
                    {
                        GeoPropBuilder.AddBox(metal, 0, h * 0.88f, 0, 0.05f, 0.08f, l);
                        GeoPropBuilder.AddBox(metal, 0, h * 0.35f, 0, 0.05f, 0.08f, l);
                    }
                    break;
            }
            return drafts;
        }

        internal static TreeModel MakeTree()
        {
            var vs = new List<Vector3>(); var trunk = new List<bool>(); var ts = new List<int>();
            var axis = new List<Vector3>(); // a point inside the part each vertex belongs to: faces are turned to look away from it
            int Add(Vector3 v, bool isTrunk, Vector3 inside) { vs.Add(v); trunk.Add(isTrunk); axis.Add(inside); return vs.Count - 1; }
            void Tri(int a, int b, int c)
            {
                var n = Vector3.Cross(vs[b] - vs[a], vs[c] - vs[a]);
                var centre = (vs[a] + vs[b] + vs[c]) / 3f;
                if (Vector3.Dot(n, centre - axis[a]) < 0) { int t = b; b = c; c = t; }
                ts.Add(a); ts.Add(b); ts.Add(c);
            }
            Vector3 Ring(float angle, float radius, float y) => new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);

            // trunk: four sides, shared corners, no caps (the crown hides the top)
            var trunkInside = new Vector3(0, 1.6f, 0);
            int[] low = new int[4], high = new int[4];
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI / 2 + Mathf.PI / 4;
                low[i] = Add(Ring(a, 0.24f, 0), true, trunkInside);
                high[i] = Add(Ring(a, 0.17f, 3.6f), true, trunkInside);
            }
            for (int i = 0; i < 4; i++) { int j = (i + 1) % 4; Tri(low[i], high[i], high[j]); Tri(low[i], high[j], low[j]); }

            // crown: a wide ring, a narrower ring higher up, and the tip
            const int sides = 7;
            var crownInside = new Vector3(0, 4.8f, 0);
            int[] ringLow = new int[sides], ringHigh = new int[sides], floor = new int[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = i * 2 * Mathf.PI / sides;
                ringLow[i] = Add(Ring(a, 2.0f, 3.0f), false, crownInside);
                ringHigh[i] = Add(Ring(a + Mathf.PI / sides, 1.55f, 5.1f), false, crownInside);
            }
            int tip = Add(new Vector3(0, 7.0f, 0), false, crownInside);
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                Tri(ringLow[i], ringHigh[i], ringLow[j]);
                Tri(ringHigh[i], ringHigh[j], ringLow[j]);
                Tri(ringHigh[i], tip, ringHigh[j]);
            }
            // the underside has its own vertices, so it shades flat
            for (int i = 0; i < sides; i++) floor[i] = Add(Ring(i * 2 * Mathf.PI / sides, 2.0f, 3.0f), false, crownInside);
            int middle = Add(new Vector3(0, 2.8f, 0), false, crownInside);
            for (int i = 0; i < sides; i++) Tri(floor[i], middle, floor[(i + 1) % sides]);

            // smooth normals for the shared vertices: the sum of the face normals around each
            var normals = new Vector3[vs.Count];
            for (int i = 0; i < ts.Count; i += 3)
            {
                var n = Vector3.Cross(vs[ts[i + 1]] - vs[ts[i]], vs[ts[i + 2]] - vs[ts[i]]);
                for (int k = 0; k < 3; k++) normals[ts[i + k]] += n;
            }
            for (int i = 0; i < normals.Length; i++) normals[i] = normals[i].sqrMagnitude > 1e-12f ? normals[i].normalized : Vector3.up;
            return new TreeModel { vertices = vs.ToArray(), normals = normals, trunk = trunk.ToArray(), triangles = ts.ToArray() };
        }

        // ------------------------------------------------------------------ baking into a chunk

        static Vector2 Uv(Color colour) => GeoCityBuilder.PaletteUv(colour);

        static void AppendTree(Chunk chunk, TreeModel tree, PropPlacement p, Quaternion rotation, int seed)
        {
            float width = p.width > 0 && GeoData.Finite(p.width) ? p.width : 4, height = p.height > 0 && GeoData.Finite(p.height) ? p.height : 7;
            var m = Matrix4x4.TRS(p.position, rotation, new Vector3(width / 4, height / 7, width / 4));
            // every tree is a slightly different green
            float tone = 0.8f + 0.4f * GeoPropPlanner.Sample(p.id, seed, 7);
            var leaf = new Color(GeoPropBuilder.Leaf.r * tone, GeoPropBuilder.Leaf.g * (0.9f + 0.2f * tone), GeoPropBuilder.Leaf.b * tone);
            Vector2 uvLeaf = Uv(leaf), uvWood = Uv(GeoPropBuilder.Wood);
            int first = chunk.vertices.Count;
            for (int i = 0; i < tree.vertices.Length; i++)
            {
                chunk.vertices.Add(m.MultiplyPoint3x4(tree.vertices[i]));
                chunk.normals.Add(m.MultiplyVector(tree.normals[i]).normalized);
                chunk.uvs.Add(tree.trunk[i] ? uvWood : uvLeaf);
            }
            for (int i = 0; i < tree.triangles.Length; i++) chunk.triangles.Add(first + tree.triangles[i]);
        }

        // Flat-shaded drafts (boxes and the like): a draft's vertices belong to one flat face each, so the normal of
        // the face a triangle lies in is the normal of its vertices, and the draft can be copied as it is.
        static void AppendDrafts(Chunk chunk, Dictionary<Color, MeshDraft> drafts, Matrix4x4 m)
        {
            foreach (var pair in drafts)
            {
                var uv = Uv(pair.Key);
                var v = pair.Value.vertices; var t = pair.Value.triangles;
                if (v.Count == 0) continue;
                var world = new Vector3[v.Count]; var normals = new Vector3[v.Count];
                for (int i = 0; i < v.Count; i++) world[i] = m.MultiplyPoint3x4(v[i]);
                for (int i = 0; i < t.Count; i += 3)
                {
                    var n = Vector3.Cross(world[t[i + 1]] - world[t[i]], world[t[i + 2]] - world[t[i]]);
                    if (n.sqrMagnitude < 1e-12f) continue;
                    n.Normalize();
                    normals[t[i]] = n; normals[t[i + 1]] = n; normals[t[i + 2]] = n;
                }
                int first = chunk.vertices.Count;
                for (int i = 0; i < v.Count; i++)
                {
                    chunk.vertices.Add(world[i]);
                    chunk.normals.Add(normals[i].sqrMagnitude > 0.5f ? normals[i] : Vector3.up);
                    chunk.uvs.Add(uv);
                }
                for (int i = 0; i < t.Count; i++) chunk.triangles.Add(first + t[i]);
            }
        }
    }
}
