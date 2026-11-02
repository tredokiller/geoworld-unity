using System.Collections.Generic;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Разведение крупных деталей (FlyPointDetailEntry.KeepApart: пни, брёвна, штабеля, кучи хвороста). Они ложились
    /// «под деревьями» — плотностью, которая выше всего у самого ствола, — и каждая запись сама по себе: пень вставал
    /// вплотную к стволу или к другому пню, бревно — поперёк дороги. Здесь каждая занимает круг по ящику своего префаба
    /// (Radius) и не встаёт в ствол дерева, в другую такую же и на дорогу (с зазором RoadGap от края полотна). Детали
    /// ложатся по ячейкам, а место в ячейке выбирает Unity (TerrainData.ComputeDetailInstanceTransforms, по нему ставит и
    /// Nature Renderer), поэтому у каждой запас на полдиагонали ячейки (slop). Кто остаётся — по убыванию Priority (хеш
    /// места: на том же месте — то же). Resolve — своя математика, годится для фонового потока.
    /// </summary>
    public static class DetailSpacing
    {
        // ствол дерева тайла с корневыми лапами
        public const float TrunkRadius = 0.6f;
        // зазор от края полотна дороги
        public const float RoadGap = 1f;
        private const float BucketSize = 8f;

        private static readonly Dictionary<GameObject, float> Radii = new();

        public struct Candidate
        {
            public Vector2 Position;
            public float Radius;
            public float Priority;
            public int Layer;
            public int Cell;
        }

        /// <summary>Сколько префаб занимает на земле: дальний угол ящика LOD0 от корня по xz, м. На главном потоке.</summary>
        public static float Radius(GameObject prefab)
        {
            if (prefab == null)
                return 0f;

            if (Radii.TryGetValue(prefab, out float known))
                return known;

            LODGroup lod = prefab.GetComponentInChildren<LODGroup>();
            Renderer[] renderers = lod != null && lod.lodCount > 0 ? lod.GetLODs()[0].renderers : prefab.GetComponentsInChildren<Renderer>();
            Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
            float radiusSqr = 0f;

            foreach (Renderer renderer in renderers)
            {
                MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;

                if (filter == null || filter.sharedMesh == null)
                    continue;

                Matrix4x4 matrix = toRoot * renderer.transform.localToWorldMatrix;
                Bounds b = filter.sharedMesh.bounds;

                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1));
                    Vector3 point = matrix.MultiplyPoint3x4(corner);
                    radiusSqr = Mathf.Max(radiusSqr, point.x * point.x + point.z * point.z);
                }
            }

            float radius = Mathf.Sqrt(radiusSqr) * Mathf.Abs(prefab.transform.localScale.x);
            Radii[prefab] = radius;
            return radius;
        }

        /// <summary>
        /// Кандидаты, что никому не мешают, по убыванию Priority. obstacles — круги (x, z мира и радиус в z вектора): стволы
        /// деревьев, камни и кусты точки взлёта; roads — отрезки дорог (null — дорог нет); slop — полдиагонали ячейки.
        /// </summary>
        public static List<Candidate> Resolve(List<Candidate> candidates, float slop, List<Vector3> obstacles, RoadAreas.Segment[] roads)
        {
            List<Candidate> accepted = new(candidates.Count);

            if (candidates.Count == 0)
                return accepted;

            candidates.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            Dictionary<long, List<Vector3>> obstacleBuckets = new();
            float obstacleReach = 0f;

            if (obstacles != null)
            {
                foreach (Vector3 obstacle in obstacles)
                {
                    Add(obstacleBuckets, obstacle);
                    obstacleReach = Mathf.Max(obstacleReach, obstacle.z);
                }
            }

            Dictionary<long, List<Vector3>> placed = new();
            float placedReach = 0f;

            foreach (Candidate candidate in candidates)
            {
                if (roads != null && RoadAreas.EdgeDistance(roads, candidate.Position) < candidate.Radius + slop + RoadGap)
                    continue;

                if (Hits(obstacleBuckets, candidate.Position, candidate.Radius + slop, obstacleReach) ||
                    Hits(placed, candidate.Position, candidate.Radius + slop * 2f, placedReach))
                    continue;

                accepted.Add(candidate);
                Add(placed, new Vector3(candidate.Position.x, candidate.Position.y, candidate.Radius));
                placedReach = Mathf.Max(placedReach, candidate.Radius);
            }

            return accepted;
        }

        // Есть ли круг ближе к point, чем radius плюс его собственный радиус; reach — наибольший радиус в buckets.
        private static bool Hits(Dictionary<long, List<Vector3>> buckets, Vector2 point, float radius, float reach)
        {
            float range = radius + reach;
            int x0 = Mathf.FloorToInt((point.x - range) / BucketSize);
            int x1 = Mathf.FloorToInt((point.x + range) / BucketSize);
            int z0 = Mathf.FloorToInt((point.y - range) / BucketSize);
            int z1 = Mathf.FloorToInt((point.y + range) / BucketSize);

            for (int bz = z0; bz <= z1; bz++)
            {
                for (int bx = x0; bx <= x1; bx++)
                {
                    if (!buckets.TryGetValue(Key(bx, bz), out List<Vector3> circles))
                        continue;

                    foreach (Vector3 circle in circles)
                    {
                        float limit = radius + circle.z;
                        float dx = circle.x - point.x;
                        float dz = circle.y - point.y;

                        if (dx * dx + dz * dz < limit * limit)
                            return true;
                    }
                }
            }

            return false;
        }

        private static void Add(Dictionary<long, List<Vector3>> buckets, Vector3 circle)
        {
            long key = Key(Mathf.FloorToInt(circle.x / BucketSize), Mathf.FloorToInt(circle.y / BucketSize));

            if (!buckets.TryGetValue(key, out List<Vector3> list))
            {
                list = new List<Vector3>();
                buckets[key] = list;
            }

            list.Add(circle);
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
