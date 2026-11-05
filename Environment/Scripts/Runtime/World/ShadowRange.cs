using GeoWorld;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Дальность теней солнца для дрона. В URP-ассете тени были до 50 м (3 каскада, затухание в последних 5 м), а Nature
    /// Renderer отсекал тени деревьев на 70 м: с 20–60 м высоты тени лежали кругом под дроном и обрывались, при снижении круг
    /// заметно «вырастал». Теперь:
    ///  - дальность — от высоты над землёй: 150 м у земли, +1 м на метр высоты, до 300 м (ступенями по 25 м, чтобы каскады
    ///    не «плавали»); 4 каскада (4 % / 12 % / 36 % дальности), карта 4096, затухают последние 35 % — край мягкий;
    ///  - тени деревьев Nature Renderer — дальше на длину тени дерева при этом солнце (25 м / tg высоты солнца, 15–100 м):
    ///    дерево за краем бросает тень внутрь, и она не появляется скачком;
    ///  - тени травы (20 м) гаснут плавно с 9 м — в проходе теней шейдера (NatureRendererFade.hlsl, _GrassShadowFade).
    /// Значения пишутся в ассет URP на время игры и возвращаются при выходе (в редакторе ассет иначе остался бы изменённым).
    /// Один на сцену (добавляет LocationsManager).
    /// </summary>
    public sealed class ShadowRange : MonoBehaviour
    {
        private const float CheckInterval = 0.25f;
        private const float Step = 25f;
        private const float Hysteresis = 10f;
        // высота дерева, по которой считается длина его тени
        private const float TreeHeight = 25f;

        [SerializeField, Min(20f)] private float nearDistance = 150f;
        [SerializeField, Min(20f)] private float farDistance = 300f;
        [Tooltip("На сколько метров дальности теней — каждый метр высоты над землёй.")]
        [SerializeField, Min(0f)] private float perMetreOfHeight = 1f;
        [SerializeField] private Vector3 cascadeSplits = new(0.04f, 0.12f, 0.36f);
        [Tooltip("Доля дальности, на которой тени затухают к краю.")]
        [SerializeField, Range(0.05f, 0.6f)] private float fadeShare = 0.35f;
        [SerializeField] private int shadowmapResolution = 4096;

        private UniversalRenderPipelineAsset _asset;
        private Saved _saved;
        private NatureRendererSettings _nature;
        private float _distance;
        private float _nextCheck;

        private struct Saved
        {
            public float Distance;
            public int Cascades;
            public Vector3 Split4;
            public float Border;
            public int Resolution;
        }

        public float Distance => _distance;

        // Дальность у земли из настроек графики (GraphicsSettingsService); с высотой — до вдвое дальше, как 150 → 300 м.
        public void SetNearDistance(float distance)
        {
            nearDistance = Mathf.Max(20f, distance);
            farDistance = nearDistance * 2f;
            _nextCheck = 0f;

            if (isActiveAndEnabled && _asset != null)
                SetDistance(nearDistance);
        }

        private void OnEnable()
        {
            _asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

            if (_asset == null)
            {
                enabled = false;
                return;
            }

            _saved = new Saved
            {
                Distance = _asset.shadowDistance,
                Cascades = _asset.shadowCascadeCount,
                Split4 = _asset.cascade4Split,
                Border = _asset.cascadeBorder,
                Resolution = _asset.mainLightShadowmapResolution
            };

            _asset.shadowCascadeCount = 4;
            _asset.cascade4Split = cascadeSplits;
            _asset.cascadeBorder = fadeShare;
            _asset.mainLightShadowmapResolution = shadowmapResolution;
            SetDistance(nearDistance);
        }

        private void OnDisable()
        {
            if (_asset == null)
                return;

            _asset.shadowDistance = _saved.Distance;
            _asset.shadowCascadeCount = _saved.Cascades;
            _asset.cascade4Split = _saved.Split4;
            _asset.cascadeBorder = _saved.Border;
            _asset.mainLightShadowmapResolution = _saved.Resolution;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextCheck)
                return;

            _nextCheck = Time.unscaledTime + CheckInterval;
            Camera camera = GeoWorldCamera.Current;

            if (camera == null)
                return;

            float wanted = Mathf.Clamp(nearDistance + HeightAboveGround(camera.transform.position) * perMetreOfHeight, nearDistance, farDistance);
            float stepped = Mathf.Clamp(Mathf.Round(wanted / Step) * Step, nearDistance, farDistance);

            // ступень меняется, только когда нужная дальность ушла за её край с запасом (не дёргается на границе)
            if (Mathf.Abs(wanted - _distance) > Step * 0.5f + Hysteresis)
                SetDistance(stepped);

            UpdateTreeShadows();
        }

        private void SetDistance(float distance)
        {
            _distance = distance;
            _asset.shadowDistance = distance;
            UpdateTreeShadows();
        }

        // Тени деревьев Nature Renderer режутся у него по корню дерева: дальше края URP на длину тени.
        private void UpdateTreeShadows()
        {
            if (_nature == null)
                _nature = GetComponent<NatureRendererSettings>();

            if (_nature == null)
                return;

            Light sun = RenderSettings.sun;
            float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg : 45f;
            float length = Mathf.Clamp(TreeHeight / Mathf.Tan(Mathf.Max(elevation, 5f) * Mathf.Deg2Rad), 15f, 100f);
            _nature.SetTreeShadowDistance(_distance + length);
        }

        // Высота точки над террейном под ней (0 — вне террейнов). Её же берёт CloudShadows.
        public static float HeightAboveGround(Vector3 position)
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null)
                    continue;

                Vector3 origin = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;

                if (position.x >= origin.x && position.x <= origin.x + size.x && position.z >= origin.z && position.z <= origin.z + size.z)
                    return Mathf.Max(0f, position.y - (terrain.SampleHeight(position) + origin.y));
            }

            return 0f;
        }
    }
}
