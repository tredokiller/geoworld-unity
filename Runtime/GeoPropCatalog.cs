using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    [Serializable]
    public class GeoPropPrefab
    {
        [Tooltip("tree, hedge, fence, wall, gate, street_lamp, bench, waste_basket, bus_stop, shelter, bicycle_parking, bollard, power_pole, traffic_signals, post_box or waste_disposal")]
        public string kind = "tree";
        [Tooltip("Use a prefab with its pivot on the ground. Forward is local +Z.")]
        public GameObject prefab;
        [Min(0.01f)] public float scale = 1;
        public float yawOffset;
        [Min(0.1f)] public float clearanceRadius = 1;
        [Tooltip("For fence/wall/hedge segments: model length along local +Z, before scale. Segments stretch to follow the mapped line.")]
        [Min(0.1f)] public float segmentLength = 2;
    }

    [CreateAssetMenu(menuName="Geo World/Prop catalog", fileName="Prop catalog")]
    public class GeoPropCatalog : ScriptableObject
    {
        public List<GeoPropPrefab> prefabs = new List<GeoPropPrefab>();
        public GeoPropPrefab Find(string kind) => prefabs.Find(p => p != null && p.kind == kind && p.prefab);
    }
}
