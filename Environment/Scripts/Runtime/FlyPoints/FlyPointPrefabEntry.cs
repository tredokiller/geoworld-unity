using System;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif
using UnityEngine;

namespace _Game.Scripts.Locations.FlyPoints
{
    [Serializable]
    public class FlyPointPrefabEntry
    {
#if ODIN_INSPECTOR
        [field: AssetsOnly]
#endif
        [field: SerializeField] public GameObject Prefab { private set; get; }
        [field: SerializeField, Min(0f)] public float Weight { private set; get; } = 1f;
#if ODIN_INSPECTOR
        [field: MinMaxSlider(0.1f, 3f, true)]
#endif
        [field: SerializeField] public Vector2 ScaleRange { private set; get; } = new(0.8f, 1.2f);
        [field: SerializeField, Range(0f, 1f)] public float AlignToNormal { private set; get; }
        [field: SerializeField, Range(0f, 0.6f)] public float SinkFraction { private set; get; }
        [field: SerializeField, Range(0f, 30f)] public float RandomTilt { private set; get; }
    }
}
