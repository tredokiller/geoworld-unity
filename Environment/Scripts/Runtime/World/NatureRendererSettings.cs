using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Настройки Nature Renderer для всей карты — его компоненты NatureRendererDefaultSettings и
    /// NatureRendererGlobalSettings (из DLL, через отражение). Без них DLL берёт заводские значения, а они не для дрона:
    ///  - трава начинала уменьшаться и пропадать уже в 5–13 м перед камерой (Density in Distance 0.125 со спадом
    ///    (0.08, 0.0075) в долях высоты кадра), вдали оставалась 1/8 травы, а на 150 м она обрывалась;
    ///  - тени деревьев строились на всю их дальность (~1.5 км), хотя URP показывает тени куда ближе (их дальность ведёт
    ///    ShadowRange: по высоте дрона и солнцу, SetTreeShadowDistance);
    ///  - ячейки травы догружались по 1024 экземпляра за кадр — на 400 км/ч поздно, целыми квадратами перед носом.
    /// Nature Renderer читает эти значения каждый кадр, пересборка ячеек не нужна. Мелкие и крупные растения уточняют
    /// их своими NatureRendererObjectSettings на префабах.
    /// </summary>
    public sealed class NatureRendererSettings : MonoBehaviour
    {
        private const string NatureAssembly = ", VisualDesignCafe.Rendering.Nature";
        private const string DefaultSettingsTypeName = "VisualDesignCafe.Rendering.Nature.NatureRendererDefaultSettings" + NatureAssembly;
        private const string GlobalSettingsTypeName = "VisualDesignCafe.Rendering.Nature.NatureRendererGlobalSettings" + NatureAssembly;
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public;

        [Header("Трава и цветы")]
        [Tooltip("Дальность травы, м (заводская 150; было 150 — с высоты луг кончался кругом). Цветам и листве свою задают их " +
                 "префабы, тростнику — 150 м.")]
        [SerializeField, Min(10f)] private float detailRenderDistance = 200f;
        [Tooltip("Доля травы, что остаётся вдали (заводская 0.125).")]
        [SerializeField, Range(0f, 1f)] private float detailDensityInDistance = 0.15f;
        [Tooltip("Где трава начинает и заканчивает редеть: доля высоты кадра, которую занимает куст (заводская 0.08, 0.0075). " +
                 "Куст 0.7 м при FOV 50 начинает редеть с ~37 м, прореженные уходят к ~165 м.")]
        [SerializeField] private Vector2 detailDensityInDistanceFalloff = new(0.02f, 0.0045f);
        [Tooltip("Тени травы, м (заводская 50). Nature Renderer режет их по корню; шейдер гасит их плавно с доли " +
                 "detailShadowFadeStart этой дальности (_GrassShadowFade), без кольца.")]
        [SerializeField, Min(0f)] private float detailShadowDistance = 20f;
        [SerializeField, Range(0f, 1f)] private float detailShadowFadeStart = 0.45f;

        [Header("Деревья")]
        [Tooltip("Дальность деревьев, м (0 — дальняя плоскость камеры). Одна для всех видов и любого FOV: пороги последнего " +
                 "LOD у деревьев опущены так, что раньше они не срабатывают. Тайлы грузятся до 2 км — деревья до их края " +
                 "(было 1600: лес обрывался в спутник на 1.2–1.6 км).")]
        [SerializeField, Min(0f)] private float treeRenderDistance = 2000f;
        [Tooltip("Тени деревьев, м (заводская — вся дальность деревьев).")]
        [SerializeField, Min(0f)] private float treeShadowDistance = 70f;

        [Header("Стриминг")]
        [Tooltip("Сколько экземпляров травы Nature Renderer догружает за кадр (заводская 1024).")]
        [SerializeField, Min(1024)] private int streamingBudget = 16384;

        private const string ObjectSettingsTypeName = "VisualDesignCafe.Rendering.Nature.NatureRendererObjectSettings" + NatureAssembly;
        // дальность травы, под которую настроены дальности растений на их префабах (цветы и стерня 100 м, тростник 150)
        private const float ReferenceDetailDistance = 200f;

        private static readonly int GrassShadowFadeId = Shader.PropertyToID("_GrassShadowFade");

        private Component _defaultSettings;
        private Component _globalSettings;
        // свои дальности префабов (NatureRendererObjectSettings) — как они в ассетах; на время игры меняются в доле от травы
        private readonly Dictionary<Object, float> _objectDistances = new();

        private void OnEnable() => Apply();

        // Дальности из настроек графики (GraphicsSettingsService): трава, деревья. Растения со своей дальностью на префабе
        // (цветы, сорняки, опад, ветки, камешки, карточки полей, тростник) с меньшей дальностью травы сжимаются в той же
        // доле, а дальше своей не растут: за ней они меньше пикселя, а их тысячи на гектар — на 1000 м карточки полей
        // (128 треугольников, 2.5–5.8 на м², без прореживания) шли до 500 м, тростник до 750 м: над лугом у горизонта
        // GPU 44 мс (21 FPS), без этого — 12 мс (54 FPS), как при 200 м. Дальняя доля основной травы (0.15) на цену почти
        // не влияет (0.015 давало те же 12 мс), поэтому трава вдали не прореживается сильнее.
        public void SetDistances(float detailDistance, float treeDistance)
        {
            detailRenderDistance = Mathf.Max(10f, detailDistance);
            treeRenderDistance = Mathf.Max(0f, treeDistance);
            Apply();
            ScaleObjectDistances(Mathf.Min(1f, detailRenderDistance / ReferenceDetailDistance));
        }


        // Дальность теней деревьев (ShadowRange: дальность теней URP + длина тени дерева); меньше 5 м разницы — не трогаем.
        public void SetTreeShadowDistance(float distance)
        {
            if (Mathf.Abs(distance - treeShadowDistance) < 5f)
                return;

            treeShadowDistance = distance;
            Apply();
        }

        private void OnValidate()
        {
            if (Application.isPlaying && isActiveAndEnabled)
                Apply();
        }

        private void OnDestroy()
        {
            Shader.SetGlobalVector(GrassShadowFadeId, Vector4.zero);
            // префабы — ассеты: в редакторе изменённая дальность осталась бы в них
            ScaleObjectDistances(1f);

            if (_defaultSettings != null)
                Destroy(_defaultSettings);

            if (_globalSettings != null)
                Destroy(_globalSettings);
        }

        private void Apply()
        {
            _defaultSettings ??= AddSettings(DefaultSettingsTypeName);
            _globalSettings ??= AddSettings(GlobalSettingsTypeName);
            Shader.SetGlobalVector(GrassShadowFadeId,
                new Vector4(detailShadowDistance * detailShadowFadeStart, detailShadowDistance * (1f - detailShadowFadeStart), 0f, 0f));

            if (_defaultSettings != null)
            {
                System.Type type = _defaultSettings.GetType();
                SetProperty(type, _defaultSettings, "DetailRenderDistance", detailRenderDistance);
                SetProperty(type, _defaultSettings, "DetailDensityInDistance", detailDensityInDistance);
                SetProperty(type, _defaultSettings, "DetailDensityInDistanceFalloff", detailDensityInDistanceFalloff);
                SetProperty(type, _defaultSettings, "DetailShadowDistance", detailShadowDistance);
                SetProperty(type, _defaultSettings, "TreeRenderDistance", treeRenderDistance);
                SetProperty(type, _defaultSettings, "TreeShadowDistance", treeShadowDistance);
            }

            if (_globalSettings != null)
            {
                System.Type type = _globalSettings.GetType();
                SetProperty(type, _globalSettings, "StreamingBudget", streamingBudget);
                SetProperty(type, _globalSettings, "ForceLoadNearbyCells", true);
            }
        }

        // Своя дальность каждого загруженного префаба с NatureRendererObjectSettings × scale (1 — как в ассете).
        private void ScaleObjectDistances(float scale)
        {
            System.Type type = System.Type.GetType(ObjectSettingsTypeName);
            PropertyInfo property = type?.GetProperty("RenderDistance", Members);

            if (property == null)
                return;

            foreach (Object settings in Resources.FindObjectsOfTypeAll(type))
            {
                if (!_objectDistances.TryGetValue(settings, out float original))
                {
                    var own = (float?)property.GetValue(settings);

                    if (!own.HasValue)
                        continue;

                    original = own.Value;
                    _objectDistances[settings] = original;
                }

                property.SetValue(settings, (float?)Mathf.Max(5f, original * scale));
            }
        }

        // Компонент Nature Renderer на этом объекте; null — Nature Renderer в проекте нет.
        private Component AddSettings(string typeName)
        {
            System.Type type = System.Type.GetType(typeName);

            if (type == null)
                return null;

            Component settings = GetComponent(type);
            return settings != null ? settings : gameObject.AddComponent(type);
        }

        private static void SetProperty(System.Type type, object target, string propertyName, object value)
        {
            PropertyInfo property = type.GetProperty(propertyName, Members);

            if (property != null && property.CanWrite)
                property.SetValue(target, value);
            else
                Debug.LogWarning($"[NatureRendererSettings] {type.Name}.{propertyName} not found");
        }
    }
}
