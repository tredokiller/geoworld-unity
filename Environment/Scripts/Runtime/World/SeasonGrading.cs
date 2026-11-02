using _Game.Scripts.Enums;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Цвет картинки по поре года — глобальный Volume URP поверх сцены (камера дрона рисует пост-обработку, HUD — нет):
    ///  - лето — чуть теплее и сочнее;
    ///  - осень — тёплая, золотистая, чуть приглушённая;
    ///  - зима — холоднее и бледнее, экспозиция ниже по снегу: камера, как настоящая с автоэкспозицией, не выжигает снег
    ///    в белое (тонмаппинга у камеры нет, яркий снег упирался в 1 и терял рельеф);
    ///  - весна — свежая зелень, чуть холоднее лета.
    /// Пора меняется сразу, без перехода (у каждой поры один вид). Один на сцену (на объекте WeatherManager).
    /// </summary>
    public sealed class SeasonGrading : MonoBehaviour
    {
        private const float Priority = 10f;

        private VolumeProfile _profile;
        private ColorAdjustments _colour;
        private WhiteBalance _whiteBalance;

        private void Awake()
        {
            GameObject host = new("Season grading");
            host.transform.SetParent(transform, false);
            Volume volume = host.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = Priority;

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "Season grading";
            _colour = _profile.Add<ColorAdjustments>(true);
            _whiteBalance = _profile.Add<WhiteBalance>(true);
            volume.sharedProfile = _profile;
        }

        private void OnEnable()
        {
            Seasons.Changed += Apply;
            Apply();
        }

        private void OnDisable() => Seasons.Changed -= Apply;

        private void OnDestroy()
        {
            if (_profile != null)
                Destroy(_profile);
        }

        private void Apply()
        {
            if (_colour == null)
                return;

            Seasons seasons = Seasons.Instance;
            Season season = seasons != null ? seasons.Current : Season.Summer;
            float snow = seasons != null ? seasons.SnowCover : 0f;

            (float exposure, float contrast, float saturation, float temperature, float tint) = season switch
            {
                Season.Autumn => (0f, 6f, -6f, 10f, 2f),
                Season.Winter => (-0.05f - 0.3f * snow, 6f, -14f, -10f, 0f),
                Season.Spring => (0f, 3f, 8f, -3f, -3f),
                _ => (0f, 4f, 4f, 3f, 0f)
            };

            _colour.postExposure.value = exposure;
            _colour.contrast.value = contrast;
            _colour.saturation.value = saturation;
            _whiteBalance.temperature.value = temperature;
            _whiteBalance.tint.value = tint;
        }
    }
}
