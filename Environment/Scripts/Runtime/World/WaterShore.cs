using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Мокрый берег у воды. С высоты урез реки читался резкой линией: вода, и сразу сухая земля того же цвета, что и луг
    /// выше. У настоящей воды полоса берега на полметра над урезом мокрая — темнее и блестит, у самой воды грязь. Здесь вода
    /// тайлов (WaterSurfaces) рисуется сверху в окне windowSize вокруг камеры (Hidden/Game/Water Shore: высота глади), и для
    /// каждой точки земли считается ближняя вода в пределах reach — её высота и расстояние до неё (_WaterShore,
    /// _WaterShoreRect). Террейн (GroundWetness.hlsl, WaterShoreWet) мочит землю ниже ~0.3–0.7 м над этой гладью. Карта
    /// перерисовывается, когда камера уходит от середины окна на recentreDistance или вода тайлов меняется. Один на сцену
    /// (добавляет WeatherManager).
    /// </summary>
    public class WaterShore : MonoBehaviour
    {
        private const string MaterialResource = "Materials/WaterShore";
        private static readonly int ShoreId = Shader.PropertyToID("_WaterShore");
        private static readonly int RectId = Shader.PropertyToID("_WaterShoreRect");
        private static readonly int DrawId = Shader.PropertyToID("_ShoreDraw");
        private static readonly int ReachId = Shader.PropertyToID("_ShoreReach");

        [Tooltip("Сторона окна карты берега вокруг камеры, м (дальше полосы в метр всё равно не видно).")]
        [SerializeField] private float windowSize = 512f;
        [Tooltip("Текселей по стороне окна.")]
        [SerializeField] private int resolution = 1024;
        [Tooltip("Докуда от воды искать её по горизонтали, м (дальше берег сухой).")]
        [SerializeField] private float reach = 8f;
        [Tooltip("На сколько камера уходит от середины окна, прежде чем карту перерисовать, м.")]
        [SerializeField] private float recentreDistance = 64f;

        private readonly List<Renderer> _renderers = new();
        private RenderTexture _height;
        private RenderTexture _rows;
        private RenderTexture _shore;
        private Material _material;
        private CommandBuffer _commands;
        private Vector2 _origin;
        private int _version = -1;
        private bool _drawn;

        private void OnDisable()
        {
            Shader.SetGlobalVector(RectId, Vector4.zero);
            Release(ref _height);
            Release(ref _rows);
            Release(ref _shore);
            _commands?.Release();
            _commands = null;

            if (_material != null)
                Destroy(_material);

            _material = null;
            _drawn = false;
        }

        private void LateUpdate()
        {
            Camera camera = Camera.main;

            if (camera == null || !EnsureResources())
                return;

            WaterSurfaces.Update();
            Vector2 eye = new(camera.transform.position.x, camera.transform.position.z);
            Vector2 centre = _origin + Vector2.one * (windowSize * 0.5f);

            if (_drawn && WaterSurfaces.Version == _version && (eye - centre).sqrMagnitude < recentreDistance * recentreDistance)
                return;

            Draw(eye);
        }

        private void Draw(Vector2 eye)
        {
            float texel = windowSize / resolution;
            // начало окна — по сетке текселей: когда окно переезжает, берег не дрожит
            _origin = new Vector2(Mathf.Floor((eye.x - windowSize * 0.5f) / texel) * texel, Mathf.Floor((eye.y - windowSize * 0.5f) / texel) * texel);
            WaterSurfaces.CollectRenderers(new Rect(_origin - Vector2.one * reach, Vector2.one * (windowSize + 2f * reach)), _renderers);
            _version = WaterSurfaces.Version;
            _drawn = true;

            _commands.Clear();
            _commands.SetRenderTarget(_height);
            _commands.ClearRenderTarget(false, true, new Color(-10000f, 1000f, 0f, 0f));
            _commands.SetGlobalVector(DrawId, new Vector4(_origin.x, _origin.y, 1f / windowSize, SystemInfo.graphicsUVStartsAtTop ? -1f : 1f));

            foreach (Renderer renderer in _renderers)
            {
                if (renderer != null)
                    _commands.DrawRenderer(renderer, _material, 0, 0);
            }

            Graphics.ExecuteCommandBuffer(_commands);
            _material.SetVector(ReachId, new Vector4(Mathf.Ceil(reach / texel), texel, 0f, 0f));
            Graphics.Blit(_height, _rows, _material, 1);
            Graphics.Blit(_rows, _shore, _material, 2);

            Shader.SetGlobalTexture(ShoreId, _shore);
            Shader.SetGlobalVector(RectId, new Vector4(_origin.x, _origin.y, 1f / windowSize, 1f));
        }

        private bool EnsureResources()
        {
            if (_material == null)
            {
                Material source = Resources.Load<Material>(MaterialResource);
                Shader shader = source != null ? source.shader : Shader.Find("Hidden/Game/Water Shore");

                if (shader == null || !shader.isSupported)
                    return false;

                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            _commands ??= new CommandBuffer { name = "Water shore" };
            _height = Ensure(_height, "Water Shore Height", FilterMode.Point);
            _rows = Ensure(_rows, "Water Shore Rows", FilterMode.Point);
            _shore = Ensure(_shore, "Water Shore", FilterMode.Bilinear);
            return true;
        }

        private RenderTexture Ensure(RenderTexture texture, string name, FilterMode filter)
        {
            if (texture != null && texture.width == resolution && texture.IsCreated())
                return texture;

            Release(ref texture);
            texture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false
            };
            texture.Create();
            _drawn = false;
            return texture;
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture == null)
                return;

            texture.Release();
            Destroy(texture);
            texture = null;
        }
    }
}
