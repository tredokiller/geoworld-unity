using System;
using UnityEngine;

namespace GeoWorld
{
    [Serializable]
    public struct GeoTreeInstance
    {
        [SerializeField] private Vector3 _position;
        [SerializeField] private float _widthScale;
        [SerializeField] private float _heightScale;
        [SerializeField] private float _rotation;
        [SerializeField] private Color32 _color;
        [SerializeField] private Color32 _lightmapColor;
        [SerializeField] private int _prototypeIndex;

        public static GeoTreeInstance From(TreeInstance tree)
        {
            return new GeoTreeInstance
            {
                _position = tree.position,
                _widthScale = tree.widthScale,
                _heightScale = tree.heightScale,
                _rotation = tree.rotation,
                _color = tree.color,
                _lightmapColor = tree.lightmapColor,
                _prototypeIndex = tree.prototypeIndex
            };
        }

        public TreeInstance ToTreeInstance()
        {
            return new TreeInstance
            {
                position = _position,
                widthScale = _widthScale,
                heightScale = _heightScale,
                rotation = _rotation,
                color = _color,
                lightmapColor = _lightmapColor,
                prototypeIndex = _prototypeIndex
            };
        }
    }
}
