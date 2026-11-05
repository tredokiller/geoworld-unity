using _Game.Scripts.Enums;
using _Game.Scripts.World;
using Enviro;
using GeoWorld;
using UnityEngine;

namespace _Game.Scripts.Services
{
    // Воздушная перспектива: дымка, которой у нас не было в ясную погоду (у «Ясно» туман Enviro нулевой, даль была чёткой и
    // «игровой»). В видео FPV над степью даль голубовато-серая: контраст заметно падает уже за 1–2 км, к 5–10 км земля почти
    // сливается с небом, горизонт растворяется в светлой полосе даже в солнечный день.
    // Это тонкий слой высотного тумана Enviro (толщина ~1.2 км над землёй под камерой): ~35 % дымки на 2 км, ~65 % на 5 км,
    // ~88 % на 10 км у земли, с высоты — меньше; цвет — небо в ту же сторону (голубая даль, светлый горизонт). Он
    // складывается со вторым слоем погоды (туман, дождь, облачность остаются своими; ползунок тумана тренировки дымку не
    // меняет), летом гуще, зимой воздух прозрачнее.
    // Enviro смешивает туман погоды в свои настройки в Update, а в шейдер отдаёт при отрисовке, поэтому: до Update Enviro
    // настройки возвращаются к значениям погоды, в LateUpdate к ним добавляется дымка — в ассет конфигурации (он в
    // редакторе живёт между запусками) дымка не попадает. Один на сцену (добавляет WeatherManager).
    [DefaultExecutionOrder(-500)]
    public class AerialHaze : MonoBehaviour
    {
        [Tooltip("Плотность дымки у земли (как fogDensity Enviro: ×0.01 на метр, по основанию 2).")]
        [SerializeField, Min(0f)] private float density = 0.031f;
        [Tooltip("Толщина слоя дымки над землёй, м: на этой высоте её в e раз меньше, чем у земли.")]
        [SerializeField, Min(100f)] private float scaleHeight = 1200f;
        [Tooltip("Сколько цвета дымки берётся от неба в ту же сторону (остальное — цвет тумана Enviro).")]
        [SerializeField, Range(0f, 1f)] private float skyBlend = 0.85f;

        private bool _applied;
        private float _density2;
        private float _falloff2;
        private float _height2;
        private float _colorBlend;

        public float Density
        {
            get => density;
            set => density = Mathf.Max(0f, value);
        }

        // до Update Enviro: настройки — снова погода (Enviro смешивает погоду от них, а не от дымки)
        private void Update() => Restore();

        private void LateUpdate()
        {
            EnviroFogModule fog = EnviroManager.instance != null ? EnviroManager.instance.Fog : null;

            if (fog == null || fog.Settings == null)
                return;

            Camera camera = GeoWorldCamera.Assigned != null && GeoWorldCamera.Assigned.isActiveAndEnabled
                ? GeoWorldCamera.Assigned
                : EnviroManager.instance.Camera != null ? EnviroManager.instance.Camera : Camera.main;

            if (camera == null)
                return;

            EnviroFogSettings settings = fog.Settings;
            _density2 = settings.fogDensity2;
            _falloff2 = settings.fogHeightFalloff2;
            _height2 = settings.fogHeight2;
            _colorBlend = settings.fogColorBlend;
            _applied = true;

            float ground = GroundUnder(camera.transform.position);
            // ползунок тумана тренировки (customFogDensityModifer) множит туман погоды, а не воздух: дымка от него не зависит
            float haze = density * SeasonScale() / Mathf.Max(fog.customFogDensityModifer, 0.05f);
            // спад по основанию 2: 2^(-f·h) = e^(-h/H)
            float hazeFalloff = 1f / (scaleHeight * 0.6931472f);

            // плотность второго слоя погоды у земли под камерой (шейдер Enviro: d·2^(-f·(h - высота слоя)))
            float weather = settings.fogDensity2 * Mathf.Pow(2f, -settings.fogHeightFalloff2 * (ground - (settings.fogHeight2 + settings.globalFogHeight)));
            float total = weather + haze;

            if (total <= 1e-6f)
                return;

            float hazeShare = haze / total;
            settings.fogDensity2 = total;
            settings.fogHeight2 = ground - settings.globalFogHeight;
            settings.fogHeightFalloff2 = Mathf.Lerp(Mathf.Max(settings.fogHeightFalloff2, 0.0001f), hazeFalloff, hazeShare);
            settings.fogColorBlend = Mathf.Lerp(settings.fogColorBlend, skyBlend, hazeShare);
        }

        private void OnDisable() => Restore();

        private void Restore()
        {
            if (!_applied)
                return;

            _applied = false;
            EnviroFogModule fog = EnviroManager.instance != null ? EnviroManager.instance.Fog : null;

            if (fog == null || fog.Settings == null)
                return;

            fog.Settings.fogDensity2 = _density2;
            fog.Settings.fogHeightFalloff2 = _falloff2;
            fog.Settings.fogHeight2 = _height2;
            fog.Settings.fogColorBlend = _colorBlend;
        }

        // летом воздух мутнее (пыль, испарение), зимой — прозрачнее
        private static float SeasonScale()
        {
            Seasons seasons = Seasons.Instance;

            return (seasons != null ? seasons.Current : Season.Summer) switch
            {
                Season.Winter => 0.6f,
                Season.Spring => 0.85f,
                Season.Autumn => 0.9f,
                _ => 1f
            };
        }

        private static float GroundUnder(Vector3 position)
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null)
                    continue;

                Vector3 origin = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;

                if (position.x >= origin.x && position.x <= origin.x + size.x && position.z >= origin.z && position.z <= origin.z + size.z)
                    return terrain.SampleHeight(position) + origin.y;
            }

            return position.y - 50f;
        }
    }
}
