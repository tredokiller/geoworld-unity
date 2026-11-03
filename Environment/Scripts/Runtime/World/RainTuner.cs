using Enviro;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Дождь Enviro — один конус частиц радиусом 5 м прямо над камерой: дальше нескольких метров дождя не видно, а всплески
    /// капель на земле выглядят белыми точками. Здесь, без правки ассетов Enviro, ближний слой расширяется и становится
    /// гуще, всплески выключаются, добавляется редкий дальний слой, а в полёте капли летят навстречу камере (скорость — по
    /// её движению, сглаженно; телепорт не в счёт). Enviro выставляет эмиссию в своём Update, частицы считаются после всех
    /// Update — поэтому порядок здесь позже Enviro. Ставит его WorldEnvironment.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class RainTuner : MonoBehaviour
    {
        private const string RainEffectName = "Rain";
        // быстрее этого камера не летает: прыжок — телепорт
        private const float MaxCameraSpeed = 150f;
        private const float VelocitySmoothing = 10f;

        [Header("Near rain")]
        [SerializeField, Min(1f)] private float nearRadius = 22f;
        [Tooltip("Во сколько раз больше капель, чем у Enviro (радиус больше — капель нужно больше).")]
        [SerializeField, Min(0f)] private float nearDensity = 4f;

        [Header("Far rain")]
        [SerializeField, Min(1f)] private float farRadius = 150f;
        [SerializeField, Min(1f)] private float farHeight = 70f;
        [SerializeField, Min(0.1f)] private float farLifetime = 2.8f;
        [Tooltip("Капель в секунду при сильном дожде.")]
        [SerializeField, Min(0f)] private float farEmission = 7000f;
        [SerializeField, Min(0f)] private float farSizeScale = 2.5f;
        [SerializeField, Range(0f, 1f)] private float farAlpha = 0.55f;

        [Tooltip("Насколько далеко вперёд по курсу (в секундах полёта) сдвигается область, где рождаются капли.")]
        [SerializeField, Range(0f, 1f)] private float leadTime = 0.5f;

        private ParticleSystem _near;
        private ParticleSystem _far;
        private Camera _camera;
        private Vector3 _cameraPosition;
        private Vector3 _velocity;

        private void Update()
        {
            Vector3 velocity = CameraVelocity();
            EnviroManager manager = EnviroManager.instance;
            EnviroEffectTypes rain = FindRain(manager);

            if (rain == null || rain.mySystem == null)
                return;

            if (rain.mySystem != _near)
                Setup(rain.mySystem);

            float intensity = rain.emissionRate * manager.Effects.Settings.particeEmissionRateModifier;
            SetEmission(_near, rain.maxEmission * intensity * nearDensity);
            SetEmission(_far, farEmission * intensity);
            FollowCamera(_near, velocity);
            FollowCamera(_far, velocity);
        }

        private void OnDestroy()
        {
            if (_far != null)
                Destroy(_far.gameObject);
        }

        private void Setup(ParticleSystem near)
        {
            if (_far != null)
                Destroy(_far.gameObject);

            _near = near;
            ParticleSystem.ShapeModule shape = near.shape;
            shape.radius = nearRadius;
            ParticleSystem.CollisionModule collision = near.collision;
            collision.enabled = false;
            ParticleSystem.SubEmittersModule subEmitters = near.subEmitters;
            subEmitters.enabled = false;

            foreach (ParticleSystem splash in near.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (splash == near)
                    continue;

                splash.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                splash.gameObject.SetActive(false);
            }

            GameObject farObject = Instantiate(near.gameObject, near.transform.parent, false);
            farObject.name = near.gameObject.name + " (far)";

            foreach (Transform child in farObject.transform)
                Destroy(child.gameObject);

            Vector3 position = near.transform.localPosition;
            farObject.transform.localPosition = new Vector3(position.x, farHeight, position.z);
            _far = farObject.GetComponent<ParticleSystem>();

            ParticleSystem.MainModule main = _far.main;
            main.startLifetime = farLifetime;
            main.startSizeMultiplier *= farSizeScale;
            Color colour = main.startColor.color;
            colour.a *= farAlpha;
            main.startColor = colour;
            ParticleSystem.ShapeModule farShape = _far.shape;
            farShape.radius = farRadius;
            _far.Play();
        }

        private static void SetEmission(ParticleSystem system, float rate)
        {
            if (system == null)
                return;

            ParticleSystem.EmissionModule emission = system.emission;
            ParticleSystem.MinMaxCurve curve = emission.rateOverTime;
            curve.constantMax = rate;
            emission.rateOverTime = curve;

            if (rate > 0f && !system.isPlaying)
                system.Play();
        }

        // Система дождя едет за камерой (в своих координатах): в полёте капли получают встречную скорость камеры, а область
        // рождения сдвигается вперёд, чтобы впереди дождь был, а не оставался позади.
        private void FollowCamera(ParticleSystem system, Vector3 velocity)
        {
            if (system == null)
                return;

            ParticleSystem.VelocityOverLifetimeModule overLifetime = system.velocityOverLifetime;
            overLifetime.enabled = true;
            overLifetime.space = ParticleSystemSimulationSpace.World;
            overLifetime.x = -velocity.x;
            overLifetime.y = -velocity.y;
            overLifetime.z = -velocity.z;

            float lifetime = system.main.startLifetime.constantMax;
            ParticleSystem.ShapeModule shape = system.shape;
            shape.position = system.transform.InverseTransformVector(velocity * (lifetime * leadTime));
        }

        // Скорость главной камеры по её перемещению за кадр, сглаженно; смена камеры и телепорт — с нуля.
        private Vector3 CameraVelocity()
        {
            Camera camera = Camera.main;
            float deltaTime = Time.deltaTime;

            if (camera == null || deltaTime <= 0f)
                return _velocity;

            Vector3 position = camera.transform.position;

            if (camera != _camera)
            {
                _camera = camera;
                _cameraPosition = position;
                _velocity = Vector3.zero;
                return _velocity;
            }

            Vector3 velocity = (position - _cameraPosition) / deltaTime;
            _cameraPosition = position;

            if (velocity.sqrMagnitude > MaxCameraSpeed * MaxCameraSpeed)
                velocity = Vector3.zero;

            _velocity = Vector3.Lerp(_velocity, velocity, 1f - Mathf.Exp(-VelocitySmoothing * deltaTime));
            return _velocity;
        }

        private static EnviroEffectTypes FindRain(EnviroManager manager)
        {
            if (manager == null || manager.Effects == null)
                return null;

            foreach (EnviroEffectTypes type in manager.Effects.Settings.effectTypes)
                if (type != null && type.name == RainEffectName)
                    return type;

            return null;
        }
    }
}
