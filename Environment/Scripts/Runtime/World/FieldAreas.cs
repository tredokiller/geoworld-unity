using System;
using System.Collections.Generic;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Поля (OSM landuse=farmland) из выгрузок GeoWorld — контуры и направление рядков (rotation экстрактора: длинная сторона
    /// поля, как обычно идут рядки и колеи техники). Пакет их в тайлы не переносит, поэтому их запекает FieldAreasBaker
    /// (Tools/Environment/Bake Field Areas, WorldMapBaker) в папку мира — ассет висит на его WorldMapData. По ним карта земли (GroundWetness, A канал) даёт
    /// шейдеру террейна направление рядков, технологических колей и борозд в каждой точке поля (FieldRows.hlsl).
    /// </summary>
    public sealed class FieldAreas : ScriptableObject
    {
        private const float BucketSize = 500f;

        [Serializable]
        public sealed class Field
        {
            // направление рядков — азимут длинной стороны поля, градусы по часовой от севера (+Z), 0..180 (rotation экстрактора)
            public float rotation;
            public Rect bounds;
            public Vector2[] outer = Array.Empty<Vector2>();
            public Vector2[] holes = Array.Empty<Vector2>();
            public int[] holeStarts = Array.Empty<int>();
        }

        public Field[] fields = Array.Empty<Field>();

        [NonSerialized] private Dictionary<long, List<int>> _buckets;

        /// <summary>Поля загруженного мира (WorldMapData); null — у него их нет.</summary>
        public static FieldAreas Instance => WorldMapData.Fields;

        // Запекание переписало поля: корзины поиска — заново.
        public void ResetIndex() => _buckets = null;

        /// <summary>Направление рядков поля в точке (градусы 0..180) или -1, если точка не в поле.</summary>
        public float RotationAt(float x, float z)
        {
            _buckets ??= BuildBuckets();

            if (!_buckets.TryGetValue(Key(Mathf.FloorToInt(x / BucketSize), Mathf.FloorToInt(z / BucketSize)), out List<int> list))
                return -1f;

            Vector2 point = new(x, z);

            foreach (int index in list)
            {
                Field field = fields[index];

                if (!field.bounds.Contains(point) || !Inside(field.outer, 0, field.outer.Length, x, z))
                    continue;

                bool inHole = false;

                for (int h = 0; h < field.holeStarts.Length && !inHole; h++)
                {
                    int start = field.holeStarts[h];
                    int end = h + 1 < field.holeStarts.Length ? field.holeStarts[h + 1] : field.holes.Length;
                    inHole = Inside(field.holes, start, end, x, z);
                }

                if (!inHole)
                    return field.rotation;
            }

            return -1f;
        }

        private Dictionary<long, List<int>> BuildBuckets()
        {
            Dictionary<long, List<int>> buckets = new();

            for (int i = 0; i < fields.Length; i++)
            {
                Rect b = fields[i].bounds;

                for (int bx = Mathf.FloorToInt(b.xMin / BucketSize); bx <= Mathf.FloorToInt(b.xMax / BucketSize); bx++)
                {
                    for (int bz = Mathf.FloorToInt(b.yMin / BucketSize); bz <= Mathf.FloorToInt(b.yMax / BucketSize); bz++)
                    {
                        long key = Key(bx, bz);

                        if (!buckets.TryGetValue(key, out List<int> list))
                            buckets[key] = list = new List<int>();

                        list.Add(i);
                    }
                }
            }

            return buckets;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static bool Inside(Vector2[] ring, int start, int end, float x, float z)
        {
            bool inside = false;

            for (int a = start, b = end - 1; a < end; b = a++)
            {
                Vector2 p = ring[a], q = ring[b];

                if ((p.y > z) != (q.y > z) && x < (q.x - p.x) * (z - p.y) / (q.y - p.y) + p.x)
                    inside = !inside;
            }

            return inside;
        }
    }
}
