using Enviro;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Общие запросы к окружению для систем мира (GeoWorld): сила эффекта погоды Enviro, свет, которым освещена пыль или
    /// листья в воздухе, и попадание точки на террейн. Были внутри DroneRotorWash (игра); вынесены, чтобы лужи, листья и
    /// вода в пакете не зависели от дрона.
    /// </summary>
    public static class EnvironmentQueries
    {
        // доля солнца, которую пыль рассеивает во все стороны (вдобавок к прямому свету)
        private const float DustSunScatter = 0.25f;

        private static readonly Vector3[] Up = { Vector3.up };
        private static readonly Color[] Ambient = new Color[1];

        // Сила эффекта погоды Enviro по имени (Rain, Snow…): 0..1, 0 без Enviro.
        public static float EffectIntensity(string effectName)
        {
            EnviroManager manager = EnviroManager.instance;

            if (manager == null || manager.Effects == null)
                return 0f;

            foreach (EnviroEffectTypes type in manager.Effects.Settings.effectTypes)
            {
                if (type != null && type.name == effectName)
                    return Mathf.Clamp01(type.emissionRate);
            }

            return 0f;
        }

        // Солнце — как на земле (интенсивность × высота солнца), плюс свет, который пыль рассеивает в любую сторону: с
        // половиной солнца и почти нулевым ambientProbe (Enviro) утром множитель был ~0.18, и пыль выходила почти чёрной.
        public static Color DustLight()
        {
            RenderSettings.ambientProbe.Evaluate(Up, Ambient);
            Color light = Ambient[0];
            EnviroManager manager = EnviroManager.instance;
            Light sun = manager != null && manager.Objects != null && manager.Objects.directionalLight != null
                ? manager.Objects.directionalLight
                : RenderSettings.sun;

            if (sun != null && sun.isActiveAndEnabled)
            {
                float elevation = Mathf.Clamp01(-sun.transform.forward.y);
                float scatter = DustSunScatter * Mathf.Clamp01(elevation * 4f);
                light += sun.color * (sun.intensity * (elevation + scatter));
            }

            return new Color(Mathf.Min(light.r, 1.3f), Mathf.Min(light.g, 1.3f), Mathf.Min(light.b, 1.3f), 1f);
        }

        // Точка над террейном (по его прямоугольнику в плане).
        public static bool TerrainContains(Terrain terrain, Vector3 position)
        {
            if (terrain == null || terrain.terrainData == null)
                return false;

            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            return position.x >= origin.x && position.x <= origin.x + size.x &&
                   position.z >= origin.z && position.z <= origin.z + size.z;
        }
    }
}
