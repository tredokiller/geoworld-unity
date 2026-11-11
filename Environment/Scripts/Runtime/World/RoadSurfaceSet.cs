using System;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Фактуры полотна дорог (RoadMap → GroundRoads.hlsl), которым нужен свой массив: асфальт — полосы поперёк дороги
    /// GeoWorld (GeoRoadTextures: u — ширина stripWidthMetres, v — длина stripLengthMetres, тайлится вдоль дороги; от почти
    /// нового к разбитому — колеи, заплаты с тёмными швами, трещины, сетка трещин, выбоины, пыль с обочин; без разметки).
    /// Текстуры лежат в пакете; набор их держит, чтобы они попали в бандлы мира (Addressables, GeoAddressableAssets). Строит
    /// Tools/Environment/Build Road Surface Set (RoadSurfaceSetBuilder).
    /// </summary>
    public sealed class RoadSurfaceSet : ScriptableObject
    {

        public Texture2D[] asphaltAlbedo = Array.Empty<Texture2D>();
        public Texture2D[] asphaltNormal = Array.Empty<Texture2D>();
        [Tooltip("Ширина полосы асфальта в фактуре (u = 0..1), м.")]
        public float stripWidthMetres = 6f;
        [Tooltip("Длина фактуры вдоль дороги (v = 0..1), м.")]
        public float stripLengthMetres = 12f;
    }
}
