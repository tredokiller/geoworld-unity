using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The trees of a merged world as GPU instances. Baked into chunks, every tree is its own copy of the mesh (about a
    // kilobyte; a forest tile holds tens of thousands). Unity's terrain trees store a tree in 40 bytes but draw each one
    // with a call of its own, which cost a town more than all its homes; so a tree is a matrix in a GeoInstancedMeshes
    // batch instead (per 500 m cell, part and green), and a tile's trees are a few dozen instanced calls. The model is
    // the low-poly tree of the baked chunks in four greens (Models/Trees, also as prefabs for painting terrain trees).
    public static class GeoTerrainTrees
    {
        public const string Folder = GeoPaths.Package + "/Models/Trees";
        public const int Tones = 4;
        // the model is 7 m tall and 4 m across
        const float ModelHeight = 7, ModelWidth = 4;
        static GameObject[] cached;

        public static GameObject[] Prefabs()
        {
            if (cached != null && System.Array.TrueForAll(cached, p => p)) return cached;
            var prefabs = new GameObject[Tones];
            for (int i = 0; i < Tones; i++) prefabs[i] = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(i));
            if (System.Array.TrueForAll(prefabs, p => p)) return cached = prefabs;
            Directory.CreateDirectory(Folder); AssetDatabase.Refresh();
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(Folder + "/Geo tree.asset");
            if (!mesh) { mesh = Model(); AssetDatabase.CreateAsset(mesh, Folder + "/Geo tree.asset"); }
            var bark = MakeMaterial(Folder + "/Geo tree bark.mat", GeoPropBuilder.Wood);
            for (int i = 0; i < Tones; i++)
            {
                if (prefabs[i]) continue;
                // the greens the baked trees take (GeoPropBaker), from the darkest to the lightest
                float tone = 0.8f + 0.4f * (i + 0.5f) / Tones;
                var leaf = GeoPropBuilder.Leaf;
                var leaves = MakeMaterial(Folder + "/Geo tree leaves " + i + ".mat", new Color(leaf.r * tone, leaf.g * (0.9f + 0.2f * tone), leaf.b * tone));
                var go = new GameObject("Geo tree " + i);
                try
                {
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = new[] { bark, leaves };
                    var lod = go.AddComponent<LODGroup>();
                    lod.SetLODs(new[] { new LOD(0.006f, new Renderer[] { renderer }) });
                    prefabs[i] = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath(i));
                }
                finally { Object.DestroyImmediate(go); }
            }
            AssetDatabase.SaveAssets();
            return cached = prefabs;
        }

        static string PrefabPath(int tone) => Folder + "/Geo tree " + tone + ".prefab";

        static Material MakeMaterial(string path, Color color)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material) return material;
            material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { enableInstancing = true, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.05f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // the baked chunks' tree, with the trunk and the crown as two sub-meshes
        static Mesh Model()
        {
            var tree = GeoPropBaker.MakeTree();
            var trunk = new List<int>(); var crown = new List<int>();
            for (int i = 0; i < tree.triangles.Length; i += 3)
                (tree.trunk[tree.triangles[i]] ? trunk : crown).AddRange(new[] { tree.triangles[i], tree.triangles[i + 1], tree.triangles[i + 2] });
            var mesh = new Mesh { name = "Geo tree" };
            mesh.SetVertices(tree.vertices); mesh.SetNormals(tree.normals);
            mesh.subMeshCount = 2; mesh.SetTriangles(trunk, 0); mesh.SetTriangles(crown, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        const float Cell = 500;

        // The trees as instanced batches under parent (none when the list is empty). Returns how many were planted.
        public static int Plant(Transform parent, IList<PropPlacement> trees, int seed)
        {
            if (trees.Count == 0) return 0;
            var prefabs = Prefabs();
            var mesh = prefabs[0].GetComponent<MeshFilter>().sharedMesh;
            var materials = new Material[Tones][];
            for (int i = 0; i < Tones; i++) materials[i] = prefabs[i].GetComponent<MeshRenderer>().sharedMaterials;
            var lists = new Dictionary<(Vector2Int cell, int tone), List<Matrix4x4>>();
            int planted = 0;
            foreach (var p in trees)
            {
                if (!GeoData.Finite(p.position.x) || !GeoData.Finite(p.position.y) || !GeoData.Finite(p.position.z)) continue;
                float width = p.width > 0 && GeoData.Finite(p.width) ? p.width : ModelWidth, height = p.height > 0 && GeoData.Finite(p.height) ? p.height : ModelHeight;
                int tone = Mathf.Clamp((int)(GeoPropPlanner.Sample(p.id, seed, 7) * Tones), 0, Tones - 1);
                var key = (new Vector2Int(Mathf.FloorToInt(p.position.x / Cell), Mathf.FloorToInt(p.position.z / Cell)), tone);
                if (!lists.TryGetValue(key, out var list)) lists[key] = list = new List<Matrix4x4>();
                list.Add(Matrix4x4.TRS(p.position, Quaternion.Euler(0, GeoData.Finite(p.yaw) ? p.yaw : 0, 0), new Vector3(width / ModelWidth, height / ModelHeight, width / ModelWidth)));
                planted++;
            }
            var go = new GameObject("Trees (" + planted + " instances)");
            go.transform.SetParent(parent, false);
            var drawer = go.AddComponent<GeoInstancedMeshes>();
            drawer.count = planted;
            var keys = new List<(Vector2Int cell, int tone)>(lists.Keys);
            keys.Sort((a, b) => a.cell.y != b.cell.y ? a.cell.y.CompareTo(b.cell.y) : a.cell.x != b.cell.x ? a.cell.x.CompareTo(b.cell.x) : a.tone.CompareTo(b.tone));
            foreach (var key in keys)
            {
                var matrices = lists[key].ToArray();
                var bounds = GeoInstancedMeshes.WorldBounds(mesh.bounds, matrices);
                for (int s = 0; s < mesh.subMeshCount; s++)
                    drawer.batches.Add(new GeoInstancedMeshes.Batch { mesh = mesh, submesh = s, material = materials[key.tone][s], bounds = bounds, matrices = matrices });
            }
            return planted;
        }

        // A terrain's trees from before they were instances (they would be drawn twice).
        public static void ClearTerrainTrees(Terrain terrain)
        {
            if (!terrain || terrain.terrainData.treeInstanceCount == 0) return;
            terrain.terrainData.SetTreeInstances(new TreeInstance[0], false);
            EditorUtility.SetDirty(terrain.terrainData);
        }
    }
}
