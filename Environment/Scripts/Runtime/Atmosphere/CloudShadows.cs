using Enviro;
using UnityEngine;

namespace _Game.Scripts.Services
{
    // Тени облаков на земле — одна из самых узнаваемых примет настоящего неба с высоты: медленные пятна тени ползут по полям
    // и лесам. У Enviro они есть (луч от точки сцены к солнцу сквозь облака в их проходе, кадр темнеет по пройденной
    // плотности — на земле, деревьях, траве и воде одинаково), но в конфигурации выключены. Здесь они включаются на время
    // игры с силой intensity и возвращаются при выходе: модуль облаков — часть ассета конфигурации, который в редакторе
    // живёт между запусками. Enviro кладёт тень в экранном проходе из уменьшенного буфера облаков: из облака и над ним
    // земля сквозь облака темнела пятнами со ступенчатым краем (при силе 2 — до чёрного), поэтому с высоты тени гаснут:
    // у земли (полёт FPV) они полные, к fadeHeight.y над землёй их нет. Один на сцену (добавляет WeatherManager).
    public class CloudShadows : MonoBehaviour
    {
        [Tooltip("Сила тени облака (Enviro: кадр × (1 − плотность × сила), не меньше 0). С 1 земля под плотным облаком " +
                 "~50 % света (прямое солнце закрыто, небо светит); с 2 плотная середина облака давала чёрное.")]
        [SerializeField, Range(0f, 3f)] private float intensity = 1f;
        [Tooltip("Тени гаснут, пока камера поднимается от первой до второй высоты над землёй, м (слой облаков Enviro задан " +
                 "от 0 до 10 км, видимая кромка — внутри него, так что по её высоте не отмерить).")]
        [SerializeField] private Vector2 fadeHeight = new(150f, 450f);

        private EnviroVolumetricCloudsModule _clouds;
        private bool _savedShadows;
        private float _savedIntensity;

        private void LateUpdate()
        {
            EnviroVolumetricCloudsModule clouds = EnviroManager.instance != null ? EnviroManager.instance.VolumetricClouds : null;

            if (clouds == null || clouds.settingsGlobal == null)
                return;

            if (clouds != _clouds)
            {
                Restore();
                _clouds = clouds;
                _savedShadows = clouds.settingsGlobal.cloudShadows;
                _savedIntensity = clouds.settingsGlobal.cloudShadowsIntensity;
            }

            float applied = intensity * NearGround();
            clouds.settingsGlobal.cloudShadows = applied > 0.001f;
            clouds.settingsGlobal.cloudShadowsIntensity = applied;
        }

        // 1 — камера у земли, 0 — высоко.
        private float NearGround()
        {
            Camera camera = Camera.main;

            if (camera == null)
                return 1f;

            float height = _Game.Scripts.World.ShadowRange.HeightAboveGround(camera.transform.position);
            return 1f - Mathf.InverseLerp(fadeHeight.x, fadeHeight.y, height);
        }

        private void OnDisable() => Restore();

        private void Restore()
        {
            if (_clouds == null || _clouds.settingsGlobal == null)
                return;

            _clouds.settingsGlobal.cloudShadows = _savedShadows;
            _clouds.settingsGlobal.cloudShadowsIntensity = _savedIntensity;
            _clouds = null;
        }
    }
}
