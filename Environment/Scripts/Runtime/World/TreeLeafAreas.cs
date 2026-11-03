using System;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Контуры лесов одного мира с типом листвы (OSM leaf_type из выгрузок GeoWorld, areas[].leafType: пакет его в тайлы не
    /// переносит) — по ним TileTreeSpecies выбирает лес лиственный, хвойный или смешанный. Лежит в папке мира и висит на его
    /// WorldMapData; запекает TreeLeafAreasBaker. Виды деревьев и смеси по месту — общие для всех карт, в TreeSpeciesProfile.
    /// </summary>
    public sealed class TreeLeafAreas : ScriptableObject
    {
        public TreeSpeciesProfile.LeafArea[] areas = Array.Empty<TreeSpeciesProfile.LeafArea>();
    }
}
