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

        [Header("Компенсация прореживания")]
        [Tooltip("Оставленные прореживанием растения растут так, чтобы покрытие земли было прежним (Nature Renderer " +
                 "Instancing.compute, GameKeptScale): вдали меньше экземпляров, но луг не редеет и у пятен полей нет дыр. " +
                 "0 — как у Nature Renderer.")]
        [SerializeField, Range(0f, 1f)] private float densityCompensation = 1f;
        [Tooltip("Наибольший рост оставленного растения по ширине.")]
        [SerializeField, Range(1f, 4f)] private float compensationMaxScale = 3f;
        [Tooltip("Высота растёт как ширина в этой степени: вдали не вырастают высокие пучки, ширина растёт сильнее.")]
        [SerializeField, Range(0f, 1f)] private float compensationHeightExponent = 0.5f;
        [Tooltip("Растения со своей дальностью на префабе (цветы, поля, тростник, опад…) с дальностью травы больше 200 м " +
                 "растут дальше своей в той же доле. Выключено — дальше своей не растут (как было: с ползунком росла " +
                 "только основная трава).")]
        [SerializeField] private bool growObjectDistances = true;
        [Tooltip("Дальше своей растут только растения не тяжелее этого (треугольников на экземпляр). Тяжёлые — дёрн, " +
                 "ветошь, газон, дерновины из настоящих травинок (860–1550), камешки, ветки, сухостой — остаются на своей " +
                 "дальности: на 1000 м с ними луг стоил +9 мс GPU (8.5 из них — эти модели), на 250–500 м они меньше пары " +
                 "пикселей. Лёгкие (основная трава, цветы, тростник, карточки полей, опад) до 500–750 м — ~1.5–2 мс.")]
        [SerializeField, Min(0)] private int growTriangleLimit = 200;

        [Header("Деревья")]
        [Tooltip("Дальность деревьев, м (0 — дальняя плоскость камеры). Одна для всех видов и любого FOV: пороги последнего " +
                 "LOD у деревьев опущены так, что раньше они не срабатывают. Тайлы грузятся до 2 км — деревья до их края " +
                 "(было 1600: лес обрывался в спутник на 1.2–1.6 км).")]
        [SerializeField, Min(0f)] private float treeRenderDistance = 2000f;
        [Tooltip("Тени деревьев, м (заводская — вся дальность деревьев).")]
        [SerializeField, Min(0f)] private float treeShadowDistance = 70f;
        [Tooltip("Плавная смена LOD деревьев и растений с LODGroup: в полосе перед порогом (эта доля его дальности) " +
                 "экземпляр рисуется обоими LOD, и дизеринг экрана перетекает от одного к другому (Nature Renderer " +
                 "Instancing.compute, GameLodFade; NatureRendererLodFade.hlsl). 0 — скачком, как у Nature Renderer.")]
        [SerializeField, Range(0f, 0.5f)] private float lodFadeBand = 0.15f;

        [Header("Стриминг")]
        [Tooltip("Сколько экземпляров травы Nature Renderer догружает за кадр (заводская 1024).")]
        [SerializeField, Min(1024)] private int streamingBudget = 16384;

        private const string ObjectSettingsTypeName = "VisualDesignCafe.Rendering.Nature.NatureRendererObjectSettings" + NatureAssembly;
        // дальность травы, под которую настроены дальности растений на их префабах (цветы и стерня 100 м, тростник 150)
        private const float ReferenceDetailDistance = 200f;

        private static readonly int GrassShadowFadeId = Shader.PropertyToID("_GrassShadowFade");
        private static readonly int DensityCompensationId = Shader.PropertyToID("_GameDensityCompensation");
        private static readonly int LodFadeId = Shader.PropertyToID("_GameLodFade");

        private Component _defaultSettings;
        private Component _globalSettings;
        // свои дальности префабов (NatureRendererObjectSettings) — как они в ассетах; на время игры меняются в доле от травы
        private readonly Dictionary<Object, float> _objectDistances = new();
        private readonly Dictionary<Object, int> _triangles = new();

        private void OnEnable() => Apply();

        // Дальности из настроек графики (GraphicsSettingsService): трава, деревья. Растения со своей дальностью на префабе
        // (цветы, сорняки, опад, ветки, камешки, карточки полей, тростник) меняют её в той же доле, что трава, — и с
        // большей дальностью травы растут дальше своей. Раньше дальше своей они не росли: на 1000 м карточки полей
        // (128 треугольников, 2.5–5.8 на м², без прореживания) шли до 500 м, тростник до 750 м — GPU 44 мс над лугом у
        // горизонта, — и с ползунком дальше 200 м росла только основная трава. Теперь цену держит прореживание вдали с
        // компенсацией размера (densityCompensation): экземпляров вдали мало, покрытие то же; у карточек полей оно включено.
        public void SetDistances(float detailDistance, float treeDistance)
        {
            detailRenderDistance = Mathf.Max(10f, detailDistance);
            treeRenderDistance = Mathf.Max(0f, treeDistance);
            Apply();
            float scale = detailRenderDistance / ReferenceDetailDistance;
            ScaleObjectDistances(growObjectDistances ? scale : Mathf.Min(1f, scale));
        }

        // Треугольников у экземпляра растения (LOD0 — его Nature Renderer и рисует у деталей), по компоненту настроек.
        private int TrianglesOf(Object settings)
        {
            if (_triangles.TryGetValue(settings, out int cached))
                return cached;

            int triangles = 0;

            if (settings is Component component)
            {
                LODGroup lodGroup = component.GetComponentInChildren<LODGroup>(true);
                Renderer[] renderers = lodGroup != null && lodGroup.lodCount > 0
                    ? lodGroup.GetLODs()[0].renderers
                    : component.GetComponentsInChildren<Renderer>(true);

                foreach (Renderer renderer in renderers)
                {
                    MeshFilter filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
                    Mesh mesh = filter != null ? filter.sharedMesh : null;

                    if (mesh == null)
                        continue;

                    for (int i = 0; i < mesh.subMeshCount; i++)
                        triangles += (int)(mesh.GetIndexCount(i) / 3);
                }
            }

            _triangles[settings] = triangles;
            return triangles;
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
            Shader.SetGlobalVector(DensityCompensationId, Vector4.zero);
            Shader.SetGlobalVector(LodFadeId, Vector4.zero);
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
            Shader.SetGlobalVector(DensityCompensationId,
                new Vector4(densityCompensation, compensationMaxScale, compensationHeightExponent, 0f));
            Shader.SetGlobalVector(LodFadeId, new Vector4(lodFadeBand, 0f, 0f, 0f));

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

                // тяжёлые растения дальше своей дальности не растут (growTriangleLimit)
                float growth = scale > 1f && TrianglesOf(settings) > growTriangleLimit ? 1f : scale;
                property.SetValue(settings, (float?)Mathf.Max(5f, original * growth));
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
