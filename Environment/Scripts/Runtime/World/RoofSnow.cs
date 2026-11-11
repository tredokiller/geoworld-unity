using System.Collections.Generic;
using GeoWorld;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Снег на крышах зимой. Снег рисует шейдер GeoWorld/Home (HomeSnow: на всём, что смотрит вверх, по _SnowCover —
    /// при полном покрове крыши белые до ~55°, при меньшем снег лежит пятнами, тонкий просвечивает), но им рисуются
    /// только дома после боя; библиотечные дома, кварталы города (GeoCityChunk) и руины — материалами URP Lit, которые
    /// запечены в тайлы. Пока лежит снег (Seasons.SnowCover > 0), этот компонент ставит их материалам шейдер GeoWorld/Home
    /// (свойства у него — URP Lit, без износа он рисует так же), а без снега и при выходе возвращает URP Lit с прежними
    /// ключевыми словами: в редакторе это ассеты, и в них ничего не остаётся. Тайлы, загруженные позже, — по sceneLoaded.
    /// Один на сцену (на объекте WeatherManager, как Seasons).
    /// </summary>
    public sealed class RoofSnow : MonoBehaviour
    {
        private const string LitShader = "Universal Render Pipeline/Lit";
        private const string HomeShader = "GeoWorld/Home";
        private static readonly int ParallaxId = Shader.PropertyToID("_Parallax");

        // ключевые слова URP Lit, которых нет у GeoWorld/Home (или чьи свойства он читает иначе: _ClearCoatMask — крыша
        // для износа, _Parallax — износ): такие материалы не трогаем
        private static readonly string[] Unsupported = { "_PARALLAXMAP", "_CLEARCOAT", "_CLEARCOATMAP" };

        private struct Original
        {
            public Shader Shader;
            public string[] Keywords;
            public int RenderQueue;
            public float Parallax;
        }

        private readonly Dictionary<Material, Original> _swapped = new();
        private readonly List<Renderer> _renderers = new();
        private Shader _home;
        private bool _active;
        private bool _scanPending;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            Seasons.Changing += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Seasons.Changing -= Refresh;
            RestoreAll();
            _active = false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => _scanPending = _active;

        private void LateUpdate()
        {
            // пора могла смениться без Changing (Apply с тем же значением не шлёт события) — дёшево сверить
            bool want = WantSnow();

            if (want != _active)
                Refresh();
            else if (_scanPending)
            {
                _scanPending = false;
                Scan();
            }
        }

        private static bool WantSnow() => Seasons.Instance != null && Seasons.Instance.SnowCover > 0.001f;

        private void Refresh()
        {
            _active = WantSnow();
            _scanPending = false;

            if (_active)
                Scan();
            else
                RestoreAll();
        }

        // Материалы зданий всех загруженных тайлов: кварталы (с их уровнями детализации), библиотечные дома (и те, что
        // ещё не созданы, — по набору GeoHomeSet), отдельные здания.
        private void Scan()
        {
            if (_home == null)
                _home = GeoRuntimeAssets.Shader(HomeShader);

            if (_home == null)
            {
                Debug.LogWarning("[RoofSnow] Shader " + HomeShader + " not found: no snow on roofs.");
                _active = false;
                return;
            }

            foreach (GeoCityChunk chunk in FindObjectsByType<GeoCityChunk>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                SwapRenderers(chunk.gameObject);

            foreach (GeoBuilding building in FindObjectsByType<GeoBuilding>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                SwapRenderers(building.gameObject);

            foreach (GeoHomeInstances homes in FindObjectsByType<GeoHomeInstances>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                SwapRenderers(homes.gameObject);

                if (homes.set == null)
                    continue;

                foreach (GeoHomeSet.Home home in homes.set.homes)
                {
                    if (home?.parts == null)
                        continue;

                    foreach (GeoHomeSet.Part part in home.parts)
                    {
                        if (part.materials == null)
                            continue;

                        foreach (Material material in part.materials)
                            Swap(material);
                    }
                }
            }
        }

        private void SwapRenderers(GameObject root)
        {
            root.GetComponentsInChildren(true, _renderers);

            foreach (Renderer renderer in _renderers)
            {
                foreach (Material material in renderer.sharedMaterials)
                    Swap(material);
            }

            _renderers.Clear();
        }

        private void Swap(Material material)
        {
            if (material == null || _swapped.ContainsKey(material) || material.shader == null || material.shader.name != LitShader)
                return;

            foreach (string keyword in Unsupported)
            {
                if (material.IsKeywordEnabled(keyword))
                    return;
            }

            var original = new Original
            {
                Shader = material.shader,
                Keywords = material.shaderKeywords,
                RenderQueue = material.renderQueue,
                Parallax = material.GetFloat(ParallaxId)
            };

            material.shader = _home;
            material.shaderKeywords = original.Keywords;
            material.renderQueue = original.RenderQueue;
            // без износа и не крыша для износа (URP Lit по умолчанию даёт _Parallax 0.005 — ниже порога износа 0.02)
            material.SetFloat(ParallaxId, 0f);
            _swapped[material] = original;
        }

        private void RestoreAll()
        {
            foreach (KeyValuePair<Material, Original> pair in _swapped)
            {
                Material material = pair.Key;

                if (material == null)
                    continue;

                material.shader = pair.Value.Shader;
                material.shaderKeywords = pair.Value.Keywords;
                material.renderQueue = pair.Value.RenderQueue;
                material.SetFloat(ParallaxId, pair.Value.Parallax);
            }

            _swapped.Clear();
        }
    }
}
