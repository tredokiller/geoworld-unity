using System.Collections.Generic;
using GeoWorld;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Разрушенные здания загруженного мира (война): каждое повреждённое здание тайлов — контур, состояние, высота, род
    /// (дом и сарай, многоэтажка и общественное, промышленное и склад). У пакета мира — из навигации его тайла
    /// (GeoTileNavigation: контур в координатах мира пакета, к ним — положение корня тайла), у мира из редактора — из
    /// GeoCityChunk его сцен тайлов (только центры: радиус — по виду). Тайл выгрузился — его руины уходят. По ним слой
    /// окружения посекает деревья (WarMap), пылит землю вокруг руин (RuinMap) и рассыпает обломки (GroundDressing).
    /// Version меняется с каждым тайлом; потребители зовут Prune, чтобы выбросить ушедшие.
    /// </summary>
    public static class RuinField
    {
        public struct Ruin
        {
            /// <summary>Центр и охват контура (мир x, z).</summary>
            public Vector2 centre;
            public float radius, height;
            public GeoConditionState state;
            /// <summary>0 — дом и сарай, 1 — многоэтажка и общественное, 2 — промышленное, склад, гараж.</summary>
            public byte family;
            /// <summary>Контур в мире (x, z); null — у слитого города из редактора (только центр).</summary>
            public Vector2[] outline;
            public Rect bounds;
        }

        private static readonly Dictionary<object, Ruin[]> Sources = new Dictionary<object, Ruin[]>();

        public static int Version { get; private set; }

        public static int Count
        {
            get
            {
                int n = 0;
                foreach (Ruin[] list in Sources.Values) n += list.Length;
                return n;
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            Sources.Clear();
            Version++;
            GeoWorldPackage.TileLoaded -= OnTileLoaded;
            GeoWorldPackage.TileLoaded += OnTileLoaded;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private static void OnTileLoaded(GeoWorldPackage package, GeoWorldTileHandle handle)
        {
            if (!Application.isPlaying || handle?.Navigation?.buildings == null || !handle.Root)
                return;

            Vector3 origin = handle.Root.transform.position;
            var list = new List<Ruin>();

            foreach (GeoTileNavigation.Building b in handle.Navigation.buildings)
            {
                if (b.state < GeoConditionState.Damaged || b.outline == null || b.outline.Length < 3)
                    continue;

                var outline = new Vector2[b.outline.Length];
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

                for (int i = 0; i < outline.Length; i++)
                {
                    Vector2 p = b.outline[i] + new Vector2(origin.x, origin.z);
                    outline[i] = p;
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
                }

                var bounds = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
                float radius = 0f;

                foreach (Vector2 p in outline)
                    radius = Mathf.Max(radius, Vector2.Distance(p, bounds.center));

                list.Add(new Ruin
                {
                    centre = bounds.center, radius = radius, height = Mathf.Max(2.5f, b.height), state = b.state,
                    family = Family(b.category), outline = outline, bounds = bounds
                });
            }

            Sources[handle] = list.ToArray();
            Version++;
        }

        // мир из редактора: слитый город сцены тайла (GeoCityChunk) знает центры зданий и их состояние
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Application.isPlaying || !scene.IsValid())
                return;

            var list = new List<Ruin>();

            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (GeoCityChunk chunk in root.GetComponentsInChildren<GeoCityChunk>(true))
            foreach (GeoCityChunk.Entry e in chunk.buildings)
            {
                if (e.state < GeoConditionState.Damaged)
                    continue;

                var c = new Vector2(e.position.x, e.position.z);
                byte family = Family(e.category);
                float radius = family == 0 ? 6f : 12f;
                list.Add(new Ruin
                {
                    centre = c, radius = radius, height = family == 1 ? 15f : family == 2 ? 8f : 5f, state = e.state, family = family,
                    outline = null, bounds = new Rect(c.x - radius, c.y - radius, radius * 2, radius * 2)
                });
            }

            if (list.Count == 0)
                return;

            Sources[scene.handle] = list.ToArray();
            Version++;
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            if (Sources.Remove(scene.handle))
                Version++;
        }

        /// <summary>Выбросить руины выгруженных тайлов пакета (их корень уничтожен).</summary>
        public static void Prune()
        {
            List<object> gone = null;

            foreach (KeyValuePair<object, Ruin[]> pair in Sources)
            {
                if (pair.Key is GeoWorldTileHandle handle && !handle.Loaded)
                    (gone ??= new List<object>()).Add(pair.Key);
            }

            if (gone == null)
                return;

            foreach (object key in gone)
                Sources.Remove(key);

            Version++;
        }

        /// <summary>Руины, чей охват (radius + reach) задевает прямоугольник.</summary>
        public static void Query(Rect area, float reach, List<Ruin> result)
        {
            result.Clear();

            foreach (Ruin[] list in Sources.Values)
            foreach (Ruin r in list)
            {
                if (r.bounds.xMax + reach < area.xMin || r.bounds.xMin - reach > area.xMax ||
                    r.bounds.yMax + reach < area.yMin || r.bounds.yMin - reach > area.yMax)
                    continue;

                result.Add(r);
            }
        }

        /// <summary>Все руины (для карт окна вокруг камеры).</summary>
        public static IEnumerable<Ruin> All
        {
            get
            {
                foreach (Ruin[] list in Sources.Values)
                foreach (Ruin r in list)
                    yield return r;
            }
        }

        /// <summary>Расстояние от точки до контура руины (0 — внутри); у руины без контура — до круга её охвата.</summary>
        public static float Distance(in Ruin ruin, Vector2 p)
        {
            if (ruin.outline == null)
                return Mathf.Max(0f, Vector2.Distance(p, ruin.centre) - ruin.radius);

            bool inside = false;
            float best = float.MaxValue;
            Vector2[] o = ruin.outline;

            for (int i = 0, j = o.Length - 1; i < o.Length; j = i++)
            {
                Vector2 a = o[j], b = o[i];

                if ((b.y > p.y) != (a.y > p.y) && p.x < (a.x - b.x) * (p.y - b.y) / (a.y - b.y) + b.x)
                    inside = !inside;

                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }

            return inside ? 0f : best;
        }

        public static byte Family(string category)
        {
            switch (category)
            {
                case "apartments": case "public": case "school": case "hospital": case "office": case "civic": case "church":
                    return 1;
                case "industrial": case "commercial": case "warehouse": case "garage": case "garages": case "retail":
                    return 2;
                default:
                    return 0;
            }
        }
    }
}
