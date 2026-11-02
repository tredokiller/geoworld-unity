using System;
using Sirenix.OdinInspector;
using UnityEngine;

namespace _Game.Scripts.Locations.FlyPoints
{
    [Serializable]
    public class FlyPointScatterCategory
    {
        [field: SerializeField] public string Name { private set; get; } = "Rocks";
        [field: SerializeField] public bool AsTerrainTrees { private set; get; }
        [field: SerializeField] public bool NearTreeGroups { private set; get; }
        [field: SerializeField, MinMaxSlider(0f, 400f, true)] public Vector2 RadiusRange { private set; get; } = new(20f, 190f);
        [field: SerializeField, Min(0f)] public float ClustersPerHectare { private set; get; } = 1f;
        [field: SerializeField, MinMaxSlider(1, 30, true)] public Vector2Int ClusterSize { private set; get; } = new(2, 5);
        [field: SerializeField, Min(0.5f)] public float ClusterRadius { private set; get; } = 5f;
        [field: SerializeField, Range(0.1f, 2f)] public float Spacing { private set; get; } = 0.8f;
        // только на вытоптанной площадке и тропинке (камешки)
        [field: SerializeField] public bool OnWornGround { private set; get; }
        // вдоль тропинки, по её краям
        [field: SerializeField] public bool AlongPath { private set; get; }
        // без коллайдеров: мелочь, о которую дрон не бьётся
        [field: SerializeField] public bool StripColliders { private set; get; }
        // мелочь, которую зимой заносит снегом: при покрове снега её столько меньше, при 100 % не видно совсем
        [field: SerializeField] public bool UnderSnow { private set; get; }
        // множитель количества там, где деревьев вокруг нет (x, поле) и где вокруг лес (y)
        [field: SerializeField] public Vector2 TreeCoverScale { private set; get; } = Vector2.one;
        // множитель на голой земле по спутнику (пахота, стерня)
        [field: SerializeField, Min(0f)] public float BareGroundScale { private set; get; } = 1f;
        [field: SerializeField] public FlyPointPrefabEntry[] Prefabs { private set; get; }
    }
}
