using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // How a tiled world is cut (GeoTiles): the margin a tile is built with, the grouping of its terrains, and the pieces of
    // roads, rivers and meshes that belong to a tile.
    public static class GeoTileGeometry
    {
        public const int TerrainGroup = 4127;
        // Far more than any road grading or junction reaches, and room for the buildings next to a tile.
        public const float MarginMetres = 96;
        // Metres beyond the margin that a tile's water reads rivers from (GeoTileJob.rivers): a river area up to a kilometre wide
        // takes its level from its line, wherever the line is.
        public const float RiverReach = 600;
        // The settings as a tile's fingerprint takes them: without the overview (GenerationSettings' last field), which is not
        // what a tile is built from. The JSON is then the same as before the overview had settings, so no tile is rebuilt for it.
        public static string TileSettingsJson(GenerationSettings settings)
        {
            string json = JsonUtility.ToJson(settings);
            int cut = json.LastIndexOf(",\"overview\":", System.StringComparison.Ordinal);
            return cut >= 0 ? json.Substring(0, cut) + "}" : json;
        }

        // bump when the generator draws tiles differently, so that "Rebuild changed tiles" rebuilds them
        internal const string Version = "geo-tiles-19"; // 19: the terrain material has GPU instancing on (the terrain was not drawn in a build), crossings with shared vertices; 18: rivers and their water areas one surface on the terrain grid (GeoWater), no ribbons; 17: lake shores go back to the ground without a wall and never leave the water in the air; 16: lakes carved with a bank and drawn on the terrain grid, one ground for exports that meet; 15: one grid for every export, tiles where exports meet built from all of them; 13: ground roads one mesh per chunk and material; 12: crossings likewise; 11: Auto building mode merges in tiles; 10: homes made from a list when the tile loads

        // Does the road or river (with its width) reach onto the bounds?
        public static bool Touches(PathBuild path, GeoBounds b)
        {
            float pad = path.feature.width * 0.5f + 2;
            var s = path.samples;
            for (int i = 1; i < s.Count; i++)
                if (Mathf.Max(s[i - 1].x, s[i].x) + pad >= b.minX && Mathf.Min(s[i - 1].x, s[i].x) - pad <= b.maxX &&
                    Mathf.Max(s[i - 1].z, s[i].z) + pad >= b.minZ && Mathf.Min(s[i - 1].z, s[i].z) - pad <= b.maxZ) return true;
            return false;
        }

        // The pieces of a road or river inside the bounds, on the path's own samples (so that every tile sees the same
        // points and heights), each with its control points in that stretch.
        public static List<PathBuild> ClipPath(PathBuild path, GeoBounds box, float pathStep)
        {
            var result = new List<PathBuild>();
            var s = path.samples;
            if (s == null || s.Count < 2) return result;
            var controlAt = ControlSamples(path, pathStep);
            int segment = 0, segments = s.Count - 1;
            while (segment < segments)
            {
                while (segment < segments && !Overlaps(s[segment], s[segment + 1], box)) segment++;
                if (segment >= segments) break;
                int first = segment;
                while (segment < segments && Overlaps(s[segment], s[segment + 1], box)) segment++;
                int last = segment; // samples first..last
                var controls = new List<Vector3> { s[first] };
                if (controlAt != null) { for (int c = 0; c < controlAt.Length; c++) if (controlAt[c] > first && controlAt[c] < last) controls.Add(path.controls[c]); }
                else for (int i = first + 1; i < last; i++) controls.Add(s[i]);
                controls.Add(s[last]);
                result.Add(new PathBuild { feature = path.feature, river = path.river, controls = controls, samples = s.GetRange(first, last - first + 1),
                    widths = path.widths != null && path.widths.Count == s.Count ? path.widths.GetRange(first, last - first + 1) : null });
            }
            return result;
        }

        static bool Overlaps(Vector3 a, Vector3 b, GeoBounds box) =>
            Mathf.Max(a.x, b.x) >= box.minX && Mathf.Min(a.x, b.x) <= box.maxX && Mathf.Max(a.z, b.z) >= box.minZ && Mathf.Min(a.z, b.z) <= box.maxZ;

        // The sample index of each control point (GeoData.Resample puts every control point among the samples), or null when
        // the samples were not made from these controls.
        static int[] ControlSamples(PathBuild path, float pathStep)
        {
            var c = path.controls;
            if (c == null || c.Count < 2) return null;
            float step = Mathf.Max(0.5f, pathStep);
            var result = new int[c.Count];
            int at = 0;
            for (int i = 1; i < c.Count; i++) { at += Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(c[i - 1], c[i]) / step)); result[i] = at; }
            return at == path.samples.Count - 1 ? result : null;
        }

        // The triangles of a world-space mesh whose centre is on the bounds (west and south edges included, east and north
        // not, so that every triangle belongs to one tile). Returns the mesh itself when it lies on the bounds already.
        public static Mesh Clip(Mesh mesh, GeoBounds b)
        {
            var vertices = mesh.vertices;
            if (vertices.Length == 0) return mesh;
            var box = mesh.bounds;
            if (box.min.x >= b.minX && box.max.x < b.maxX && box.min.z >= b.minZ && box.max.z < b.maxZ) return mesh;
            var remap = new int[vertices.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var kept = new List<int>();
            var triangles = new List<int>[mesh.subMeshCount];
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var source = mesh.GetTriangles(sub); var list = triangles[sub] = new List<int>(source.Length);
                for (int t = 0; t + 2 < source.Length; t += 3)
                {
                    Vector3 p = vertices[source[t]], q = vertices[source[t + 1]], r = vertices[source[t + 2]];
                    float cx = (p.x + q.x + r.x) / 3, cz = (p.z + q.z + r.z) / 3;
                    if (cx < b.minX || cx >= b.maxX || cz < b.minZ || cz >= b.maxZ) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = source[t + k];
                        if (remap[v] < 0) { remap[v] = kept.Count; kept.Add(v); }
                        list.Add(remap[v]);
                    }
                }
            }
            var normals = mesh.normals; var tangents = mesh.tangents; var uv = mesh.uv; var uv2 = mesh.uv2; var colors = mesh.colors32;
            var result = new Mesh { name = mesh.name, indexFormat = kept.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            result.SetVertices(kept.Select(v => vertices[v]).ToList());
            if (normals.Length == vertices.Length) result.SetNormals(kept.Select(v => normals[v]).ToList());
            if (tangents.Length == vertices.Length) result.SetTangents(kept.Select(v => tangents[v]).ToList());
            if (uv.Length == vertices.Length) result.SetUVs(0, kept.Select(v => uv[v]).ToList());
            if (uv2.Length == vertices.Length) result.SetUVs(1, kept.Select(v => uv2[v]).ToList());
            if (colors.Length == vertices.Length) result.SetColors(kept.Select(v => colors[v]).ToList());
            result.subMeshCount = triangles.Length;
            for (int sub = 0; sub < triangles.Length; sub++) result.SetTriangles(triangles[sub], sub, false);
            result.RecalculateBounds();
            Object.DestroyImmediate(mesh);
            return result;
        }
    }
}
