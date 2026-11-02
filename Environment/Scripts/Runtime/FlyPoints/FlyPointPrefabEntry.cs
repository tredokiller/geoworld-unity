using System;
using Sirenix.OdinInspector;
using UnityEngine;

namespace _Game.Scripts.Locations.FlyPoints
{
    [Serializable]
    public class FlyPointPrefabEntry
    {
        [field: SerializeField, AssetsOnly] public GameObject Prefab { private set; get; }
        [field: SerializeField, Min(0f)] public float Weight { private set; get; } = 1f;
        [field: SerializeField, MinMaxSlider(0.1f, 3f, true)] public Vector2 ScaleRange { private set; get; } = new(0.8f, 1.2f);
        [field: SerializeField, Range(0f, 1f)] public float AlignToNormal { private set; get; }
        [field: SerializeField, Range(0f, 0.6f)] public float SinkFraction { private set; get; }
        [field: SerializeField, Range(0f, 30f)] public float RandomTilt { private set; get; }
    }
}
