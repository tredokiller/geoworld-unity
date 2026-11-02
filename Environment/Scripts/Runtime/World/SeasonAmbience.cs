using _Game.Scripts.Enums;
using Enviro;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Звуки природы по поре года поверх фона Enviro. У Enviro два фоновых звука: «Day» (в проекте — ветер) и «Night»
    /// (ночные сверчки); громкость им каждый кадр ставит Enviro по погоде и времени суток, а здесь после него она
    /// умножается по поре: ветер осенью и зимой сильнее, сверчков летом больше всего, зимой нет. Днём поют птицы — свой
    /// источник (звук птиц Enviro, Resources/Audio/Seasons/Birds): весной больше всего, летом чуть меньше, осенью мало,
    /// зимой нет; в дождь и снег тише. Птицы — с той же общей громкостью фона Enviro и в той же группе микшера, что его
    /// фон, — баланс с остальными звуками не сбивается. Один на сцену (на объекте WeatherManager).
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public sealed class SeasonAmbience : MonoBehaviour
    {
        private const string BirdsPath = "Audio/Seasons/Birds";
        // фоновые звуки Enviro (EnviroAudioClip.name)
        private const string WindClipName = "Day";
        private const string NightClipName = "Night";
        private const string RainEffectName = "Rain";
        private const string SnowEffectName = "Snow";

        private AudioSource _birds;

        private void Awake()
        {
            AudioClip clip = Resources.Load<AudioClip>(BirdsPath);

            if (clip == null)
            {
                Debug.LogWarning($"[SeasonAmbience] Birds clip not found in Resources ({BirdsPath})");
                return;
            }

            _birds = gameObject.AddComponent<AudioSource>();
            _birds.clip = clip;
            _birds.loop = true;
            _birds.playOnAwake = false;
            _birds.spatialBlend = 0f;
            _birds.volume = 0f;
        }

        // после Enviro (он ставит громкость в Update): своё умножение не копится — Enviro каждый кадр считает заново
        private void LateUpdate()
        {
            EnviroManager enviro = EnviroManager.instance;

            if (enviro == null || enviro.Audio == null || enviro.Audio.Settings == null)
            {
                SetBirds(0f);
                return;
            }

            Seasons seasons = Seasons.Instance;
            Season season = seasons != null ? seasons.Current : Season.Summer;
            float daylight = 0f;

            foreach (EnviroAudioClip clip in enviro.Audio.Settings.ambientClips)
            {
                if (clip == null || clip.myAudioSource == null)
                    continue;

                if (clip.name == WindClipName)
                {
                    clip.myAudioSource.volume *= WindScale(season);
                    daylight = Mathf.Clamp01(clip.volumeCurve.Evaluate(enviro.solarTime));

                    if (_birds != null && _birds.outputAudioMixerGroup == null)
                        _birds.outputAudioMixerGroup = clip.audioMixerGroup;
                }
                else if (clip.name == NightClipName)
                {
                    clip.myAudioSource.volume *= CricketScale(season);
                }
            }

            float precipitation = Mathf.Max(EffectIntensity(enviro, RainEffectName), EffectIntensity(enviro, SnowEffectName));
            SetBirds(enviro.Audio.Settings.ambientMasterVolume * BirdScale(season) * daylight * (1f - 0.85f * precipitation));
        }

        private void OnDisable() => SetBirds(0f);

        private void SetBirds(float volume)
        {
            if (_birds == null)
                return;

            _birds.volume = volume;

            if (volume > 0.0001f && !_birds.isPlaying)
                _birds.Play();
            else if (volume <= 0.0001f && _birds.isPlaying)
                _birds.Pause();
        }

        private static float WindScale(Season season) => season switch
        {
            Season.Autumn => 1.25f,
            Season.Winter => 1.4f,
            Season.Summer => 0.85f,
            _ => 1f
        };

        private static float CricketScale(Season season) => season switch
        {
            Season.Spring => 0.35f,
            Season.Autumn => 0.3f,
            Season.Winter => 0f,
            _ => 1f
        };

        private static float BirdScale(Season season) => season switch
        {
            Season.Spring => 1f,
            Season.Summer => 0.7f,
            Season.Autumn => 0.2f,
            _ => 0f
        };

        private static float EffectIntensity(EnviroManager enviro, string effectName)
        {
            if (enviro.Effects == null)
                return 0f;

            foreach (EnviroEffectTypes type in enviro.Effects.Settings.effectTypes)
                if (type != null && type.name == effectName)
                    return Mathf.Clamp01(type.emissionRate);

            return 0f;
        }
    }
}
