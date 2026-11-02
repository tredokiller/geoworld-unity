using System;
using _Game.Scripts.Enums;
using Sirenix.OdinInspector;
using UnityEngine;

namespace _Game.Scripts.Locations.FlyPoints
{
    [Serializable]
    public class FlyPointDetailEntry
    {
        [field: SerializeField, AssetsOnly] public GameObject Prototype { private set; get; }
        [field: SerializeField, Min(0f)] public float DensityPerSquareMeter { private set; get; } = 1f;
        [field: SerializeField, Range(0f, 1f)] public float PatchThreshold { private set; get; }
        [field: SerializeField, Min(1f)] public float PatchScale { private set; get; } = 12f;
        [field: SerializeField, Range(0f, 1f)] public float SoilDensity { private set; get; } = 0.25f;
        [field: SerializeField, Min(0f)] public float MinRadius { private set; get; } = 2f;
        [field: SerializeField, MinMaxSlider(0.1f, 3f, true)] public Vector2 WidthRange { private set; get; } = new(0.8f, 1.2f);
        [field: SerializeField, MinMaxSlider(0.1f, 3f, true)] public Vector2 HeightRange { private set; get; } = new(0.8f, 1.2f);
        [field: SerializeField] public Color HealthyColor { private set; get; } = Color.white;
        [field: SerializeField] public Color DryColor { private set; get; } = new(0.85f, 0.8f, 0.6f);
        [field: SerializeField, Min(0f)] public float NoiseSpread { private set; get; } = 0.3f;
        // 0 — до края зоны; иначе плотность сходит на нет к этому радиусу (густой подсев только у площадки)
        [field: SerializeField, Min(0f)] public float MaxRadius { private set; get; }
        // кочки: плотность собирается в куртины размером ClumpScale метров
        [field: SerializeField, Min(0.5f)] public float ClumpScale { private set; get; } = 3f;
        [field: SerializeField, Range(0f, 1f)] public float ClumpStrength { private set; get; }
        // сколько остаётся на вытоптанной площадке и тропинке (0 — ничего)
        [field: SerializeField, Range(0f, 1f)] public float TrampleTolerance { private set; get; } = 0.2f;
        // только под деревьями (опавшая листва)
        [field: SerializeField] public bool UnderTrees { private set; get; }
        // множитель плотности там, где деревьев вокруг нет (x, поле) и где вокруг лес (y)
        [field: SerializeField] public Vector2 TreeCoverScale { private set; get; } = Vector2.one;
        // множитель на голой земле по спутнику (пахота, стерня)
        [field: SerializeField, Min(0f)] public float BareGroundScale { private set; get; } = 1f;
        // множитель по тому, что спутник показывает в ячейке: зелень, жёлтое (спелое, стерня, сухая трава), тёмная пахота,
        // голая земля (SatelliteStates); все нули — прежний BareGroundScale, заданные — вместо него
        [field: SerializeField] public FlyPointSatelliteScales SatelliteScales { private set; get; }

        public bool UsesSatelliteScales => !SatelliteScales.IsUnset;
        // множитель по земле карты GeoWorld: луг, поле и его край, лес, болото, посёлок, грунтовка, голая земля
        [field: SerializeField] public FlyPointLandScales LandScales { private set; get; } = FlyPointLandScales.Everywhere;
        // множитель по поре года (Seasons); зимой его гасит снег, кроме тех, что торчат из сугроба
        [field: SerializeField] public FlyPointSeasonScales SeasonScales { private set; get; } = FlyPointSeasonScales.Default;
        [field: SerializeField] public bool AboveSnow { private set; get; }
        // насколько экземпляр ложится по склону (DetailPrototype.alignToGround, %; Nature Renderer его учитывает): опавшие
        // листья, ветки и камешки лежат на земле — 100, трава растёт вверх — 0. Лежащее по склону не кладётся круче
        // MaxGroundSlope: на обрывах листья вставали почти вертикально (и в натуре там не держатся)
        [field: SerializeField, Range(0f, 100f)] public float AlignToGround { private set; get; }
        // пни, брёвна, штабеля, кучи хвороста: занимают место по своему префабу — не в стволах деревьев, не друг в друге
        // (по всем таким записям) и не на дорогах (DetailSpacing); в ячейке — не больше одного
        [field: SerializeField] public bool KeepApart { private set; get; }

        // значение для DetailPrototype.alignToGround. Nature Renderer (TerrainGrassSpawner.Spawn) берёт его долей 0–1 и
        // смешивает поворот «только по Y» с поворотом «по нормали» без ограничения: при 100 поворот на склоне
        // экстраполировался в 100 раз, и кучи листьев вставали вертикально или вверх ногами (на ровном оба совпадают)
        public float PrototypeAlignToGround => Mathf.Clamp01(AlignToGround / 100f);

        public const float MaxGroundSlope = 28f;

        // Множитель плотности лежащего по склону по крутизне (градусы): полный до MaxGroundSlope − 6°, к MaxGroundSlope — 0.
        public static float GroundSlopeScale(float steepness) =>
            1f - Mathf.Clamp01((steepness - (MaxGroundSlope - 6f)) / 6f);

        public FlyPointLandScales EffectiveLandScales => LandScales.IsUnset ? FlyPointLandScales.Everywhere : LandScales;

        // Плотность в эту пору (без снега: его покров гасит запись поклеточно, SnowCover).
        public float SeasonScale(Season season) => (SeasonScales.IsUnset ? FlyPointSeasonScales.Default : SeasonScales).Of(season);

        // Прячется ли под снегом в эту пору: зимой всё, кроме того, что торчит из сугроба.
        public bool HiddenBySnow(Season season) => season == Season.Winter && !AboveSnow;
    }
}
