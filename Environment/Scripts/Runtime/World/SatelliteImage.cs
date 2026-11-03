using System;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Снимок тайла GeoWorld для подтяжки дорог и рядков полей (RoadSatelliteSnap, FieldRowsSatellite): текстура — слой
    /// террейна, растянутый на весь тайл (самая крупная), пиксели — через GPU (снимок тайла сжат, Crunch или DXT1, и не
    /// читается с CPU): в sRGB (как его видит глаз — дороги) или в линейном цвете (яркость для рядков). Reader держит
    /// буферы чтения, чтобы не создавать их на каждый тайл.
    /// </summary>
    public static class SatelliteImage
    {
        /// <summary>Снимок тайла: самая крупная текстура его слоёв, не меньше 1024; null — снимка нет.</summary>
        public static Texture Of(TerrainData data)
        {
            Texture best = null;

            if (data == null)
                return null;

            foreach (TerrainLayer layer in data.terrainLayers)
            {
                if (layer != null && layer.diffuseTexture != null && (best == null || layer.diffuseTexture.width > best.width))
                    best = layer.diffuseTexture;
            }

            return best != null && best.width >= 1024 ? best : null;
        }

        public sealed class Reader : IDisposable
        {
            private readonly int _size;
            private readonly bool _linear;
            private RenderTexture _target;
            private Texture2D _read;

            public Reader(int size, bool linear)
            {
                _size = size;
                _linear = linear;
            }

            /// <summary>Пиксели текстуры в size² (строки с юга, как у текстуры).</summary>
            public Color32[] Read(Texture source)
            {
                _target ??= new RenderTexture(_size, _size, 0, RenderTextureFormat.ARGB32, _linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                // своя, не ассет: UnloadUnusedAssetsImmediate между тайлами в редакторе иначе её уничтожает
                _read ??= new Texture2D(_size, _size, TextureFormat.RGBA32, false, _linear) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                RenderTexture previous = RenderTexture.active;
                Graphics.Blit(source, _target);
                RenderTexture.active = _target;
                _read.ReadPixels(new Rect(0, 0, _size, _size), 0, 0);
                _read.Apply(false);
                RenderTexture.active = previous;
                return _read.GetPixels32();
            }

            public void Dispose()
            {
                if (_target != null)
                {
                    _target.Release();
                    UnityEngine.Object.DestroyImmediate(_target);
                }

                if (_read != null)
                    UnityEngine.Object.DestroyImmediate(_read);

                _target = null;
                _read = null;
            }
        }
    }
}
