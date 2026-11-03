using System;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Какие деревья где растут на тайлах GeoWorld (TileTreeSpecies): виды с вариантами префабов и смесь видов по месту —
    /// лес хвойный (OSM leaf_type=needleleaved или песок вокруг: сосновые боры на песчаных террасах Северского Донца), лес
    /// лиственный (дубравы с ясенем, клёном, липой), лесополосы, посёлки, берега. Общий для всех карт, лежит в
    /// Resources/World/TreeSpeciesProfile; контуры лесов с типом листвы — свои у каждого мира (TreeLeafAreas на его
    /// WorldMapData).
    /// </summary>
    [CreateAssetMenu(menuName = "Configs/World/Tree Species Profile", fileName = "TreeSpeciesProfile")]
    public sealed class TreeSpeciesProfile : ScriptableObject
    {
        [Serializable]
        public sealed class Species
        {
            public string name;
            public GameObject[] variants = Array.Empty<GameObject>();
            [Tooltip("Своя высота у вариантов (снаги — обломанные стволы): высота дерева тайла не в счёт, масштаб 0.85–1.15. " +
                     "Иначе вариант — ближайший по высоте, и снаг в 7 м на месте 20-метрового дерева вырос бы вдвое.")]
            public bool ownHeight;
            [Tooltip("Часть дерева лежит на земле (бурелом: вершина с кроной рядом с пнём): дерево поворачивается так, чтобы " +
                     "она не легла на дорогу, а где так не выходит — на его месте стоячий вид.")]
            public bool lying;
        }

        /// <summary>Смесь видов: доля каждого (по индексу в species); нули — вида здесь нет.</summary>
        [Serializable]
        public sealed class Mix
        {
            public float[] weights = Array.Empty<float>();
        }

        [Serializable]
        public sealed class LeafArea
        {
            // 1 — лиственный, 2 — хвойный, 3 — смешанный
            public byte type;
            public Rect bounds;
            // внешний контур и дыры (xz мира), дыры подряд, начало каждой — в holeStarts
            public Vector2[] outer = Array.Empty<Vector2>();
            public Vector2[] holes = Array.Empty<Vector2>();
            public int[] holeStarts = Array.Empty<int>();
        }

        public Species[] species = Array.Empty<Species>();
        [Header("Смеси по месту")]
        public Mix forestConifer = new();
        public Mix forestMixed = new();
        public Mix forestBroadleaf = new();
        [Tooltip("Лес без типа листвы, но на песке (доля песка вокруг): сосна с примесью.")]
        public Mix forestSandy = new();
        public Mix rows = new();
        public Mix settlement = new();
        [Tooltip("У воды и на болоте.")]
        public Mix wet = new();
        [Tooltip("Сады: пусто — как поставил тайл.")]
        public Mix orchard = new();
        [Header("Признаки места")]
        [Tooltip("Доля песка (GeoWorld Sand) в радиусе SandRadius, с которой лес без типа листвы — сосновый.")]
        [Range(0f, 1f)] public float sandShare = 0.08f;
        public float sandRadius = 120f;
        [Tooltip("Доля воды и болота в радиусе WetRadius, с которой дерево — прибрежное.")]
        [Range(0f, 1f)] public float wetShare = 0.2f;
        public float wetRadius = 30f;
    }
}
