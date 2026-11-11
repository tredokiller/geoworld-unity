using System;
using System.Collections.Generic;
using GeoWorld;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Набор фото-текстур земли для фактуры вблизи камеры (GroundDetail, GroundDetail.hlsl): слои (альбедо, нормали, маска —
    /// R высота, G AO, B гладкость; все одного размера и сжатия — из них собираются массивы текстур) и какие слои у каждого
    /// класса земли GeoWorld. Собирает его Tools/Environment/Build Ground Detail Set (GroundDetailSetBuilder) из
    /// Environment/Textures/Ground пакета; лежит в его Environment/Profiles/GroundDetailSet (Addressables, GeoAddressableAssets).
    /// </summary>
    [CreateAssetMenu(menuName = "Configs/World/Ground Detail Set", fileName = "GroundDetailSet")]
    public sealed class GroundDetailSet : ScriptableObject
    {
        [Serializable]
        public sealed class Layer
        {
            public string name;
            public Texture2D albedo;
            public Texture2D normal;
            public Texture2D mask;
            [Tooltip("Размер одного повтора текстуры в мире, м.")]
            [Min(0.1f)] public float tileMetres = 3f;
            [Tooltip("Насколько рисунок текстуры виден поверх цвета спутника.")]
            [Range(0f, 1.5f)] public float contrast = 1f;
            [Range(0f, 2f)] public float normalStrength = 1f;
            [Range(0f, 1f)] public float occlusion = 0.5f;
            [Tooltip("Средний цвет альбедо (линейный): рисунок — отношение к нему, поэтому средний цвет земли остаётся спутниковым.")]
            public Color average = Color.gray;
        }

        [Serializable]
        public sealed class ClassLayers
        {
            public string landClass;
            [Tooltip("Слой, где спутник зелёный (трава, всходы, листва).")]
            public int green;
            [Tooltip("Слой, где спутник бурый и тёмный (пашня, грязь, асфальт).")]
            public int bareDark;
            [Tooltip("Слой, где спутник бурый и светлый (стерня, сухое поле, песок, плитка).")]
            public int bareLight;
        }

        [Tooltip("С какой дистанции от камеры фактура гаснет и где её уже нет, м.")]
        public float fadeStart = 160f;
        public float fadeEnd = 280f;
        public List<Layer> layers = new();
        [Tooltip("По значению GeoLandClass.")]
        public ClassLayers[] classes = new ClassLayers[GeoLandCover.ClassCount];
    }
}
