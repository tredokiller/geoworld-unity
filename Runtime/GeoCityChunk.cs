using System;
using UnityEngine;

namespace GeoWorld
{
    // One square of a merged city: the buildings of a chunk are baked into a single mesh, and this
    // component remembers which building is where and in what condition (a city has no object per house).
    public class GeoCityChunk : MonoBehaviour
    {
        [Serializable]
        public struct Entry
        {
            public string id, category, name;
            public Vector3 position;
            public GeoConditionState state;
            public string basis;
            // The ruin model it is drawn with (its ID), or empty for procedural geometry.
            public string ruin;
        }

        public Entry[] buildings = new Entry[0];

        // The building whose centre is nearest to a world position within `radius` metres, if any.
        public bool TryFind(Vector3 world, float radius, out Entry found)
        {
            found = default;
            float best = radius * radius;
            bool any = false;
            foreach (var entry in buildings)
            {
                float d = (new Vector2(entry.position.x - world.x, entry.position.z - world.z)).sqrMagnitude;
                if (d <= best) { best = d; found = entry; any = true; }
            }
            return any;
        }
    }
}
