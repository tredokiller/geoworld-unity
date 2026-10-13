using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The roads of an export in a coarse grid, to find the street nearest a building quickly (a city has tens of
    // thousands of road segments): a ruin model turns its front, with the door and porch, towards it.
    public sealed class GeoStreets
    {
        const float Cell = 64;
        readonly Dictionary<Vector2Int, List<Vector4>> cells = new Dictionary<Vector2Int, List<Vector4>>();

        public GeoStreets(IEnumerable<LineFeature> roads)
        {
            if (roads == null) return;
            foreach (var road in roads)
            {
                if (road == null || road.tunnel || road.bridge) continue;
                var points = GeoData.Points(road.points);
                for (int i = 1; i < points.Count; i++)
                {
                    var a = new Vector2(points[i - 1].x, points[i - 1].z); var b = new Vector2(points[i].x, points[i].z);
                    int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) / Cell), x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) / Cell);
                    int z0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) / Cell), z1 = Mathf.FloorToInt(Mathf.Max(a.y, b.y) / Cell);
                    if ((x1 - x0 + 1) * (z1 - z0 + 1) > 4096) continue; // a broken segment kilometres long
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            var key = new Vector2Int(x, z);
                            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Vector4>();
                            list.Add(new Vector4(a.x, a.y, b.x, b.y));
                        }
                }
            }
        }

        public bool Empty => cells.Count == 0;

        // The unit direction from a point (world X/Z) to the nearest road within reach, if there is one.
        public Vector2? Toward(Vector2 point, float reach = 60)
        {
            if (cells.Count == 0) return null;
            int r = Mathf.CeilToInt(reach / Cell);
            var home = new Vector2Int(Mathf.FloorToInt(point.x / Cell), Mathf.FloorToInt(point.y / Cell));
            float best = reach; Vector2? toward = null;
            for (int x = home.x - r; x <= home.x + r; x++)
                for (int z = home.y - r; z <= home.y + r; z++)
                {
                    if (!cells.TryGetValue(new Vector2Int(x, z), out var list)) continue;
                    foreach (var s in list)
                    {
                        var a = new Vector2(s.x, s.y); var b = new Vector2(s.z, s.w);
                        float d = GeoData.SegmentDistance(point, a, b, out float t);
                        if (d >= best || d < 0.01f) continue;
                        best = d;
                        toward = (Vector2.Lerp(a, b, t) - point) / d;
                    }
                }
            return toward;
        }
    }
}
