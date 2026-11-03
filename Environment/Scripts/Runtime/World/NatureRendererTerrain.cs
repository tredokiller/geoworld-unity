using System.Reflection;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Nature Renderer на террейне тайла (компонент из DLL, доступ через отражение). Он рисует тот TerrainData, с которым
    /// начал: подмену TerrainData и его слоёв травы сам не видит — до перезапуска рисует прежние. Общий для точки взлёта
    /// и травы по всей карте.
    /// </summary>
    public static class NatureRendererTerrain
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string NatureAssembly = ", VisualDesignCafe.Rendering.Nature";
        private const string NatureRendererTypeName = "VisualDesignCafe.Rendering.Nature.NatureRenderer" + NatureAssembly;
        private const string GrassRendererTypeName = "VisualDesignCafe.Rendering.Nature.TerrainGrassRenderer" + NatureAssembly;

        private static System.Type _natureRendererType;
        private static System.Type _grassRendererType;
        private static bool _typesSearched;

        /// <summary>
        /// Nature Renderer тайла; если его нет — добавляет. GeoWorld ставит его только на тайлы с деревьями: на тайле
        /// без деревьев (поле, вода) трава мира и точки взлёта шла встроенными деталями Unity — дороже по GPU и со
        /// своими дистанциями. null — Nature Renderer в проекте нет.
        /// </summary>
        public static Component Ensure(Terrain terrain)
        {
            Component natureRenderer = terrain.GetComponent("NatureRenderer");

            if (natureRenderer != null)
                return natureRenderer;

            FindTypes();

            if (_natureRendererType == null)
                return null;

            natureRenderer = terrain.gameObject.AddComponent(_natureRendererType);
            System.Type type = natureRenderer.GetType();
            // до своей инициализации (первый LateUpdate): рисовать всё, что загружено, — тайлы стримит GeoWorld
            SetProperty(type, natureRenderer, "OnlyInitializeWithinRenderingDistance", false);
            SetProperty(type, natureRenderer, "DelayInitialize", false);
            SetProperty(type, natureRenderer, "RenderTreesWithNatureRenderer", true);
            SetProperty(type, natureRenderer, "RenderDetailsWithNatureRenderer", true);
            // при перезапуске не включать встроенный рендер Unity: он рисовал бы деревья и траву поверх (как у GeoWorld)
            SetProperty(type, natureRenderer, "EnableUnityRendererWhenDisabled", false);
            SetProperty(type, natureRenderer, "OptimizePatchSize", false);
            SetProperty(type, natureRenderer, "Terrain", terrain);
            SetProperty(type, natureRenderer, "TerrainData", terrain.terrainData);
            return natureRenderer;
        }

        // Включить Nature Renderer на террейне и перезапустить его с текущего TerrainData: трава и все деревья заново.
        public static void Refresh(Terrain terrain)
        {
            Component natureRenderer = Ensure(terrain);

            if (natureRenderer == null)
                return;

            System.Type type = natureRenderer.GetType();
            SetProperty(type, natureRenderer, "RenderTreesWithNatureRenderer", true);
            SetProperty(type, natureRenderer, "RenderDetailsWithNatureRenderer", true);
            SetProperty(type, natureRenderer, "DelayInitialize", false);
            SetProperty(type, natureRenderer, "OnlyInitializeWithinRenderingDistance", false);
            SetProperty(type, natureRenderer, "Terrain", terrain);
            SetProperty(type, natureRenderer, "TerrainData", terrain.terrainData);

            try
            {
                InvokeNoArgs(type, natureRenderer, "ReplaceUnsupportedDetails");
                Rebuild(type, natureRenderer);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[NatureRendererTerrain] Nature Renderer rebuild failed: {exception.Message}");
            }

            // траву и деревья рисует Nature Renderer — и пока он не начал (его ещё держит GeoNatureRendererStart),
            // встроенный рендер Unity не должен рисовать их поверх
            terrain.drawTreesAndFoliage = false;
        }

        /// <summary>
        /// Отдать тайлу TerrainData с теми же высотами и деревьями, но другой травой, и пересобрать в Nature Renderer только
        /// траву. Иначе он перезапускался целиком (его будило сообщение Unity о смене террейна: у копии другое число ячеек
        /// травы на патч) — заново грузил все деревья тайла и переписывал каждый слой травы, а встроенный рендер Unity
        /// оставался включённым и рисовал поверх. Коллайдер остаётся на прежних данных: высоты и деревья те же, а его
        /// пересборка стоила ~18 мс. withGrass false — траву Nature Renderer не собирает (тайл далеко, ShowGrass соберёт).
        /// </summary>
        public static void SwapGrass(Terrain terrain, TerrainData data, bool withGrass = true)
        {
            Component natureRenderer = terrain.GetComponent("NatureRenderer");
            System.Type type = natureRenderer != null ? natureRenderer.GetType() : null;

            // сообщение Unity о смене TerrainData перезапустило бы Nature Renderer целиком; данные тайла меняем только мы.
            // Ячейки травы — патчи копии как есть: если он ещё будет инициализироваться, пусть не укрупняет их до 64
            if (natureRenderer != null)
            {
                SetProperty(type, natureRenderer, "AutoRefreshTerrainAtRuntime", false);
                SetProperty(type, natureRenderer, "OptimizePatchSize", false);
            }

            terrain.terrainData = data;

            if (natureRenderer == null)
            {
                terrain.drawTreesAndFoliage = true;
                return;
            }

            SetProperty(type, natureRenderer, "Terrain", terrain);
            SetProperty(type, natureRenderer, "TerrainData", data);

            // ещё не начал рисовать — при инициализации сам возьмёт данные с террейна
            if (!GetBool(type, natureRenderer, "IsInitialized"))
                return;

            try
            {
                if (!RebuildGrass(type, natureRenderer, terrain, withGrass))
                    Rebuild(type, natureRenderer);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[NatureRendererTerrain] Nature Renderer grass rebuild failed: {exception.Message}");
            }

            if (GetBool(type, natureRenderer, "RenderTreesWithNatureRenderer") && GetBool(type, natureRenderer, "RenderDetailsWithNatureRenderer"))
                terrain.drawTreesAndFoliage = false;
        }

        /// <summary>
        /// Есть ли у тайла рендерер травы Nature Renderer: null — Nature Renderer на нём нет или он ещё не рисует (траву
        /// он тогда построит сам, когда начнёт).
        /// </summary>
        public static bool? GrassShown(Terrain terrain)
        {
            Component natureRenderer = terrain.GetComponent("NatureRenderer");

            if (natureRenderer == null)
                return null;

            FieldInfo grassField = natureRenderer.GetType().GetField("_grassRenderer", Members);
            FieldInfo renderingField = natureRenderer.GetType().GetField("_isRendering", Members);

            if (grassField == null || !(renderingField?.GetValue(natureRenderer) is true))
                return null;

            return grassField.GetValue(natureRenderer) != null;
        }

        /// <summary>
        /// Траву Nature Renderer на тайле — снять (TerrainData с её слоями и деревья остаются) или собрать заново с текущего
        /// TerrainData. Свои ячейки (патчи) он каждый кадр перебирает во всех тайлах, видна трава или нет: у дальних
        /// одетых тайлов её снимает GroundDressing. Его LateUpdate рендерер травы null пропускает.
        /// </summary>
        public static void ShowGrass(Terrain terrain, bool shown)
        {
            Component natureRenderer = terrain.GetComponent("NatureRenderer");

            if (natureRenderer == null)
                return;

            System.Type type = natureRenderer.GetType();

            if (!GetBool(type, natureRenderer, "IsInitialized"))
                return;

            try
            {
                RebuildGrass(type, natureRenderer, terrain, shown);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[NatureRendererTerrain] Nature Renderer grass {(shown ? "rebuild" : "release")} failed: {exception.Message}");
            }
        }

        // После возврата прежнего TerrainData: Nature Renderer пересобирает свои ячейки травы и деревьев с него.
        public static void Restart(Terrain terrain)
        {
            Component natureRenderer = terrain.GetComponent("NatureRenderer");

            if (natureRenderer == null)
                return;

            try
            {
                Rebuild(natureRenderer.GetType(), natureRenderer);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[NatureRendererTerrain] Nature Renderer restart failed: {exception.Message}");
            }
        }

        public static void AssignTerrainData(Terrain terrain, TerrainData data)
        {
            Component natureRenderer = terrain.GetComponent("NatureRenderer");

            if (natureRenderer == null)
                return;

            System.Type type = natureRenderer.GetType();
            SetProperty(type, natureRenderer, "Terrain", terrain);
            SetProperty(type, natureRenderer, "TerrainData", data);
        }

        /// <summary>
        /// Траву — заново с нового TerrainData, деревья — как были. Повторяет половину StopRendering(true)/StartRendering
        /// Nature Renderer 6.0.2, которая про траву: у него для этого нет открытого метода. Экземпляры травы он всё равно
        /// создаёт позже, ячейками по мере приближения камеры. withGrass false — только снять прежнюю. false — у этой версии
        /// нет нужных членов.
        /// </summary>
        private static bool RebuildGrass(System.Type type, Component natureRenderer, Terrain terrain, bool withGrass = true)
        {
            FindTypes();
            FieldInfo grassField = type.GetField("_grassRenderer", Members);
            FieldInfo renderingField = type.GetField("_isRendering", Members);
            MethodInfo cacheSettings = type.GetMethod("CacheTerrainSettings", Members, null, System.Type.EmptyTypes, null);
            ConstructorInfo create = _grassRendererType?.GetConstructor(Members, null, new[] { typeof(Terrain) }, null);
            MethodInfo load = _grassRendererType?.GetMethod("Load", Members, null, new[] { typeof(bool) }, null);
            MethodInfo destroy = _grassRendererType?.GetMethod("Destroy", Members, null, System.Type.EmptyTypes, null);

            if (grassField == null || renderingField == null || cacheSettings == null || create == null || load == null || destroy == null)
                return false;

            // префабы с LODGroup — дочерним LOD0, как при инициализации; запомнить размер и ячейки новой травы, иначе
            // следующее сравнение настроек сочтёт террейн изменённым и перезапустит всё
            InvokeNoArgs(type, natureRenderer, "ReplaceUnsupportedDetails");
            cacheSettings.Invoke(natureRenderer, null);

            object previous = grassField.GetValue(natureRenderer);

            if (previous != null)
                destroy.Invoke(previous, null);

            grassField.SetValue(natureRenderer, null);

            // не рисует (вне камер) — построит траву сам, когда начнёт
            if (!withGrass || !(renderingField.GetValue(natureRenderer) is true) || !GetBool(type, natureRenderer, "RenderDetailsWithNatureRenderer"))
                return true;

            object grass = create.Invoke(new object[] { terrain });
            grassField.SetValue(natureRenderer, grass);
            load.Invoke(grass, new object[] { true });
            return true;
        }

        private static void FindTypes()
        {
            if (_typesSearched)
                return;

            _typesSearched = true;
            _natureRendererType = System.Type.GetType(NatureRendererTypeName);
            _grassRendererType = System.Type.GetType(GrassRendererTypeName);
        }

        private static void SetProperty(System.Type type, object target, string propertyName, object value)
        {
            PropertyInfo property = type.GetProperty(propertyName, Members);
            property?.SetValue(target, value);
        }

        private static bool GetBool(System.Type type, object target, string propertyName)
        {
            PropertyInfo property = type.GetProperty(propertyName, Members);
            return property?.GetValue(target) is true;
        }

        private static bool InvokeBool(System.Type type, object target, string methodName)
        {
            MethodInfo method = type.GetMethod(methodName, Members, null, System.Type.EmptyTypes, null);
            return method?.Invoke(target, null) is true;
        }

        private static void InvokeNoArgs(System.Type type, object target, string methodName)
        {
            MethodInfo method = type.GetMethod(methodName, Members, null, System.Type.EmptyTypes, null);
            method?.Invoke(target, null);
        }

        private static void Rebuild(System.Type type, object target)
        {
            // уже рисует — перезапуск, даже если с новых данных он начать не может (тайл без деревьев отдаёт TerrainData
            // без травы, CanInitialize ложно): иначе он так и рисовал бы прежние, уже удалённые данные; теперь он ждёт
            // годных и начинает сам
            if (GetBool(type, target, "IsInitialized"))
            {
                InvokeNoArgs(type, target, "Restart");
                return;
            }

            if (!InvokeBool(type, target, "CanInitialize"))
                return;

            InvokeNoArgs(type, target, "Initialize");
            InvokeNoArgs(type, target, "StartRendering");
        }
    }
}
