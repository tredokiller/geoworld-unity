#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif
using UnityEngine;

namespace _Game.Scripts.Locations.FlyPoints
{
    [CreateAssetMenu(menuName = "Configs/Locations/Fly Point Environment Profile", fileName = "FlyPointEnvironmentProfile", order = 1)]
    public class FlyPointEnvironmentProfile : ScriptableObject
    {
#if ODIN_INSPECTOR
        [field: Title("Ground")]
#else
        [field: Header("Ground")]
#endif
        [field: SerializeField] public TerrainLayer GrassLayer { private set; get; }
        [field: SerializeField] public TerrainLayer SoilLayer { private set; get; }
        [field: SerializeField] public TerrainLayer GravelLayer { private set; get; }
        [field: SerializeField, Range(0f, 1f)] public float SatelliteInfluence { private set; get; } = 0.4f;
        [field: SerializeField, Range(0f, 1f)] public float SoilCoverage { private set; get; } = 0.2f;
        // доля земли, где по спутнику голая земля (пахота, стерня)
        [field: SerializeField, Range(0f, 1f)] public float BareSoilCoverage { private set; get; } = 0.7f;
        [field: SerializeField, Min(1f)] public float SoilNoiseScale { private set; get; } = 40f;
        [field: SerializeField, Min(0f)] public float GravelRadius { private set; get; } = 7f;
        [field: SerializeField, Min(0f)] public float BoundaryNoise { private set; get; } = 25f;
        [field: SerializeField, Min(1f)] public float BoundaryBlendWidth { private set; get; } = 55f;

#if ODIN_INSPECTOR
        [field: Title("Relief")]
#else
        [field: Header("Relief")]
#endif
        [field: SerializeField, Min(0f)] public float ReliefAmplitude { private set; get; } = 0.25f;
        [field: SerializeField, Min(1f)] public float ReliefScale { private set; get; } = 18f;

#if ODIN_INSPECTOR
        [field: Title("Zones")]
#else
        [field: Header("Zones")]
#endif
        [field: SerializeField, Min(0f)] public float PadClearRadius { private set; get; } = 12f;
        [field: SerializeField, Min(0f)] public float InnerRadius { private set; get; } = 35f;
        [field: SerializeField, Min(1f)] public float EdgeBand { private set; get; } = 20f;

#if ODIN_INSPECTOR
        [field: Title("Patch terrain")]
#else
        [field: Header("Patch terrain")]
#endif
        [field: SerializeField, Range(129, 2049)] public int HeightmapResolution { private set; get; } = 513;
        [field: SerializeField, Range(128, 2048)] public int AlphamapResolution { private set; get; } = 1024;
        [field: SerializeField, Range(128, 2048)] public int DetailResolution { private set; get; } = 512;
        [field: SerializeField, Range(1f, 50f)] public float PixelError { private set; get; } = 5f;

#if ODIN_INSPECTOR
        [field: Title("Map")]
#else
        [field: Header("Map")]
#endif
        [field: SerializeField] public Sprite MarkerSprite { private set; get; }

#if ODIN_INSPECTOR
        [field: Title("Vegetation and props")]
#else
        [field: Header("Vegetation and props")]
#endif
        [field: SerializeField] public FlyPointDetailEntry[] Details { private set; get; }
        [field: SerializeField] public FlyPointScatterCategory[] Categories { private set; get; }
    }
}
