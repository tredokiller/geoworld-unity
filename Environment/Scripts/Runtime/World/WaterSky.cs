using Enviro;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Живое небо в отражении воды. Небо в воде там, куда луч отражения экрана (WaterScreenReflection) не дотянулся, — из
    /// пробы отражений, а у сцены это Default-Skybox-Cubemap: чистое голубое небо, раз запечённое Unity, без облаков, погоды и
    /// времени суток (глобальная проба Enviro в конфигурации выключена). Под кучевыми облаками река оставалась ровной синей
    /// лентой. Здесь своя проба Enviro (его отрисовка: небо и облака, сцена не рисуется — cullingMask 0) раз в
    /// refreshSeconds рисует кубмапу по грани за кадр и отдаёт её шейдерам воды (_WaterSkyCube, _WaterSkyOn — WaterCore.hlsl);
    /// сам ReflectionProbe выключен, так что террейн, деревья и трава отражают прежнее небо (живое небо в их бликах меняло
    /// бы вид всего мира). Модули Enviro не трогает. Один на сцену (добавляет WeatherManager).
    /// </summary>
    public class WaterSky : MonoBehaviour
    {
        private static readonly int CubeId = Shader.PropertyToID("_WaterSkyCube");
        private static readonly int OnId = Shader.PropertyToID("_WaterSkyOn");

        [Tooltip("Сторона грани кубмапы неба, px (мипы — размытое небо в ряби).")]
        [SerializeField] private int resolution = 128;
        [Tooltip("Как часто перерисовывать небо, с (облака плывут, погода меняется); рисуется по грани за кадр.")]
        [SerializeField] private float refreshSeconds = 2f;

        private GameObject _probeObject;
        private ReflectionProbe _probe;
        private EnviroReflectionProbe _enviroProbe;
        private float _nextRefresh;

        private void OnEnable() => Shader.SetGlobalFloat(OnId, 0f);

        private void OnDisable()
        {
            Shader.SetGlobalFloat(OnId, 0f);

            if (_probeObject != null)
                Destroy(_probeObject);

            _probeObject = null;
            _probe = null;
            _enviroProbe = null;
        }

        private void LateUpdate()
        {
            if (EnviroManager.instance == null)
                return;

            if (_enviroProbe == null && !CreateProbe())
                return;

            Camera camera = Camera.main;

            if (camera != null)
                _probeObject.transform.position = camera.transform.position;

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + refreshSeconds;
                _enviroProbe.RefreshReflection(true);
            }

            Texture cube = _probe.customBakedTexture;
            Shader.SetGlobalFloat(OnId, cube != null ? 1f : 0f);

            if (cube != null)
                Shader.SetGlobalTexture(CubeId, cube);
        }

        // Проба создаётся выключенной, чтобы EnviroReflectionProbe.OnEnable увидел standalone и customRendering и не включил
        // ReflectionProbe (включённый, он отражался бы во всём мире).
        private bool CreateProbe()
        {
            _probeObject = new GameObject("Water Sky Probe") { hideFlags = HideFlags.DontSave };
            _probeObject.SetActive(false);
            _probeObject.transform.SetParent(transform, false);

            _probe = _probeObject.AddComponent<ReflectionProbe>();
            _probe.enabled = false;
            _probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;
            _probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            _probe.resolution = resolution;
            _probe.hdr = true;
            _probe.cullingMask = 0;
            _probe.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.Skybox;
            _probe.nearClipPlane = 0.3f;
            _probe.farClipPlane = 100000f;

            _enviroProbe = _probeObject.AddComponent<EnviroReflectionProbe>();
            _enviroProbe.standalone = true;
            _enviroProbe.customRendering = true;
            _enviroProbe.updateReflectionOnGameTime = false;
            _enviroProbe.useTimeSlicing = true;
            _probeObject.SetActive(true);
            _nextRefresh = 0f;
            return true;
        }
    }
}
