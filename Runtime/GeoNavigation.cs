using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    public enum GeoSurface { Ground, Building, Water, Bridge, Road }

    // What a part of a loaded tile is to a game's navigation: the game maps these to its own NavMesh areas (a NavMeshModifier, or
    // its NavMeshBuildSource markups) and agent types; Geo World builds no NavMesh. Walkable is the default meaning of the kind.
    [DisallowMultipleComponent]
    public sealed class GeoNavigationTag : MonoBehaviour
    {
        public GeoSurface surface;
        public bool walkable = true;
    }

    public static class GeoNavigation
    {
        public const string Group = "Navigation";
        const float Cell = 200;

        static void Mark(GameObject go, GeoSurface surface, bool walkable)
        {
            var tag = go.GetComponent<GeoNavigationTag>() ?? go.AddComponent<GeoNavigationTag>();
            tag.surface = surface; tag.walkable = walkable;
        }

        // Marks the terrain (ground), the water surfaces and the bridges of a tile.
        public static void Tag(Transform root)
        {
            foreach (var terrain in root.GetComponentsInChildren<Terrain>(true)) Mark(terrain.gameObject, GeoSurface.Ground, true);
            var water = root.Find(GeoWaterLook.Group);
            if (water) foreach (var r in water.GetComponentsInChildren<MeshRenderer>(true)) Mark(r.gameObject, GeoSurface.Water, false);
            foreach (var path in root.GetComponentsInChildren<GeoPath>(true))
                if (path.bridge && !path.river) foreach (var c in path.GetComponentsInChildren<MeshCollider>(true)) Mark(c.gameObject, GeoSurface.Bridge, true);
        }

        // Mesh colliders of the buildings' outlines, extruded from a metre under their ground to their height: one object per 200 m
        // cell under the tile's "Navigation" group, marked Building (not walkable). The meshes go into created.
        public static void BuildingColliders(Transform root, GeoTileNavigation navigation, List<Object> created, int layer = -1) =>
            AddBuildingColliders(root, BuildingMeshes(navigation, created), layer);

        // The meshes of BuildingColliders (main thread), one per cell with its name; Physics.BakeMesh can bake them on a worker
        // thread before AddBuildingColliders gives them to their colliders.
        public static List<(string name, Mesh mesh)> BuildingMeshes(GeoTileNavigation navigation, List<Object> created)
        {
            var result = new List<(string, Mesh)>();
            if (navigation?.buildings == null || navigation.buildings.Length == 0) return result;
            var cells = new SortedDictionary<long, List<GeoTileNavigation.Building>>();
            foreach (var b in navigation.buildings)
            {
                if (b.outline == null || b.outline.Length < 3 || !(b.height > 0)) continue;
                var c = Centre(b.outline);
                long key = ((long)Mathf.FloorToInt(c.x / Cell) << 32) ^ (uint)Mathf.FloorToInt(c.y / Cell);
                if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<GeoTileNavigation.Building>();
                list.Add(b);
            }
            foreach (var pair in cells)
            {
                var vertices = new List<Vector3>(); var triangles = new List<int>();
                foreach (var b in pair.Value) Prism(b, vertices, triangles);
                if (triangles.Count == 0) continue;
                var mesh = new Mesh { name = "Building outlines", indexFormat = vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
                created.Add(mesh);
                result.Add(("Buildings " + (int)(pair.Key >> 32) + "_" + (int)(pair.Key & 0xffffffff) + " (" + pair.Value.Count + ")", mesh));
            }
            return result;
        }

        public static void AddBuildingColliders(Transform root, List<(string name, Mesh mesh)> meshes, int layer = -1)
        {
            if (meshes == null || meshes.Count == 0) return;
            var group = new GameObject(Group).transform;
            group.SetParent(root, false);
            foreach (var (name, mesh) in meshes)
            {
                var go = new GameObject(name);
                go.transform.SetParent(group, false);
                go.isStatic = true;
                if (layer >= 0) go.layer = layer;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                Mark(go, GeoSurface.Building, false);
            }
        }

        static Vector2 Centre(Vector2[] outline)
        {
            var c = Vector2.zero;
            foreach (var p in outline) c += p;
            return c / outline.Length;
        }

        // walls facing out and a roof facing up (a ray does not hit the back of a face)
        static void Prism(GeoTileNavigation.Building b, List<Vector3> vertices, List<int> triangles)
        {
            var outline = GeoPolygon.Clean(b.outline);
            if (outline.Count < 3) return;
            float bottom = b.baseY - 1, top = b.baseY + b.height;
            int start = vertices.Count, n = outline.Count;
            foreach (var p in outline) { vertices.Add(new Vector3(p.x, bottom, p.y)); vertices.Add(new Vector3(p.x, top, p.y)); }
            // the outline's turn: outward is to the right of each edge going counter-clockwise (x east, z north), to the left else
            float turn = GeoPolygon.Area(outline) >= 0 ? 1 : -1;
            void Face(int a, int c, int d, Vector3 facing)
            {
                var normal = Vector3.Cross(vertices[c] - vertices[a], vertices[d] - vertices[a]);
                if (Vector3.Dot(normal, facing) >= 0) triangles.AddRange(new[] { a, c, d }); else triangles.AddRange(new[] { a, d, c });
            }
            for (int i = 0; i < n; i++)
            {
                int a = start + i * 2, c = start + (i + 1) % n * 2;
                Vector2 edge = outline[(i + 1) % n] - outline[i];
                var outward = new Vector3(edge.y, 0, -edge.x) * turn;
                Face(a, a + 1, c + 1, outward); Face(a, c + 1, c, outward);
            }
            var roof = GeoPolygon.Triangulate(outline);
            for (int i = 0; i + 2 < roof.Count; i += 3) Face(start + roof[i] * 2 + 1, start + roof[i + 1] * 2 + 1, start + roof[i + 2] * 2 + 1, Vector3.up);
        }
    }
}
