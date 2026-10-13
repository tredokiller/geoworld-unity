using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    public enum GroundSurface { Asphalt, Gravel, Dirt, Paving }

    [DisallowMultipleComponent]
    public class GeoPath : MonoBehaviour
    {
        [HideInInspector] public string sourceId;
        [HideInInspector] public bool river;
        [Min(0.5f)] public float width = 6;
        public GroundSurface surface;
        public bool bridge;
        [HideInInspector] public int layer;
        [Tooltip("World-space X/Z in metres. Height is regenerated from the terrain.")]
        public List<Vector3> points = new List<Vector3>();

        void OnDrawGizmosSelected()
        {
            Gizmos.color = river ? Color.cyan : new Color(1, 0.75f, 0.2f);
            for (int i = 1; i < points.Count; i++) Gizmos.DrawLine(points[i - 1], points[i]);
        }
    }
}
