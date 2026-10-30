using System;
using System.Reflection;
using UnityEngine;

namespace GeoWorld
{
    // Nature Renderer on a tile's terrain, for a game that puts its own grass on the tiles (Geo World finds Nature Renderer
    // by name and does not depend on it). Nature Renderer draws the TerrainData it started with: it does not see another
    // TerrainData given to the terrain, or other detail layers, until it restarts, and a restart reloads every tree of the
    // tile. These calls swap a tile's grass without that (members of Nature Renderer 6.0.2 by reflection; with another
    // version that lacks them, a full restart).
    public static class GeoNatureRenderer
    {
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const string NatureAssembly = ", VisualDesignCafe.Rendering.Nature";
        const string NatureRendererTypeName = "VisualDesignCafe.Rendering.Nature.NatureRenderer" + NatureAssembly;
        const string GrassRendererTypeName = "VisualDesignCafe.Rendering.Nature.TerrainGrassRenderer" + NatureAssembly;

        static Type natureRendererType, grassRendererType;
        static bool searched;

        // Nature Renderer is in the project.
        public static bool Available { get { FindTypes(); return natureRendererType != null; } }

        // The terrain's Nature Renderer, added when it has none (Geo World adds it only to terrains whose trees it draws: on a
        // treeless tile a game's grass went through Unity's own detail rendering, with other distances and dearer). It draws
        // everything that is loaded (Geo World streams the tiles), never lets Unity's own renderer draw over it while it
        // restarts, and keeps the detail patches of the terrain data as they are (merged into 125 m cells, a cell of crops
        // loaded in one frame: 16 frames over 50 ms in 14 s at 400 km/h, 1-2 with 62.5 m cells). Null without Nature Renderer.
        public static Component Ensure(Terrain terrain)
        {
            var natureRenderer = terrain.GetComponent("NatureRenderer");
            if (!natureRenderer)
            {
                FindTypes();
                if (natureRendererType == null) return null;
                natureRenderer = terrain.gameObject.AddComponent(natureRendererType);
                var added = natureRenderer.GetType();
                Set(added, natureRenderer, "RenderTreesWithNatureRenderer", true);
                Set(added, natureRenderer, "RenderDetailsWithNatureRenderer", true);
                Set(added, natureRenderer, "DelayInitialize", false);
                Set(added, natureRenderer, "Terrain", terrain);
                Set(added, natureRenderer, "TerrainData", terrain.terrainData);
            }
            var type = natureRenderer.GetType();
            Set(type, natureRenderer, "OnlyInitializeWithinRenderingDistance", false);
            Set(type, natureRenderer, "EnableUnityRendererWhenDisabled", false);
            Set(type, natureRenderer, "OptimizePatchSize", false);
            return natureRenderer;
        }

        // Nature Renderer on the terrain, started again from its current TerrainData: grass and every tree anew.
        public static void Refresh(Terrain terrain)
        {
            var natureRenderer = Ensure(terrain);
            if (!natureRenderer) return;
            var type = natureRenderer.GetType();
            Set(type, natureRenderer, "Terrain", terrain);
            Set(type, natureRenderer, "TerrainData", terrain.terrainData);
            try
            {
                Invoke(type, natureRenderer, "ReplaceUnsupportedDetails");
                Rebuild(type, natureRenderer);
            }
            catch (Exception e) { Debug.LogWarning("Geo World: Nature Renderer could not restart on " + terrain.name + " (" + e.Message + ")."); }
            // Nature Renderer draws its trees and grass: Unity's own renderer must not draw them over it, before it starts too
            terrain.drawTreesAndFoliage = false;
        }

        // Gives the terrain TerrainData with the same heights and trees and other grass, and rebuilds only Nature Renderer's
        // grass. Without this, Unity's terrain-changed message restarted Nature Renderer whole (the copy has another number of
        // detail cells a patch): every tree of the tile loaded again and every detail layer was written again (35-107 ms), and
        // Unity's own renderer stayed on, drawing over it. The terrain collider is left on its data: the same heights and
        // trees, and cooking it again cost some 18 ms.
        public static void SwapGrass(Terrain terrain, TerrainData data)
        {
            var natureRenderer = terrain.GetComponent("NatureRenderer");
            var type = natureRenderer ? natureRenderer.GetType() : null;
            if (natureRenderer)
            {
                // only the game changes the tile's data: Unity's message would restart it whole
                Set(type, natureRenderer, "AutoRefreshTerrainAtRuntime", false);
                Set(type, natureRenderer, "OptimizePatchSize", false);
            }
            terrain.terrainData = data;
            if (!natureRenderer) { terrain.drawTreesAndFoliage = true; return; }
            Set(type, natureRenderer, "Terrain", terrain);
            Set(type, natureRenderer, "TerrainData", data);
            // not drawing yet: it takes the data from the terrain when it starts
            if (!Bool(type, natureRenderer, "IsInitialized")) return;
            try
            {
                if (!RebuildGrass(type, natureRenderer, terrain)) Rebuild(type, natureRenderer);
            }
            catch (Exception e) { Debug.LogWarning("Geo World: Nature Renderer could not rebuild the grass of " + terrain.name + " (" + e.Message + ")."); }
            if (Bool(type, natureRenderer, "RenderTreesWithNatureRenderer") && Bool(type, natureRenderer, "RenderDetailsWithNatureRenderer"))
                terrain.drawTreesAndFoliage = false;
        }

        // After the terrain got its earlier TerrainData back: Nature Renderer rebuilds its grass and tree cells from it.
        public static void Restart(Terrain terrain)
        {
            var natureRenderer = terrain.GetComponent("NatureRenderer");
            if (!natureRenderer) return;
            try { Rebuild(natureRenderer.GetType(), natureRenderer); }
            catch (Exception e) { Debug.LogWarning("Geo World: Nature Renderer could not restart on " + terrain.name + " (" + e.Message + ")."); }
        }

        // The grass anew from the new TerrainData, the trees as they were: the half of Nature Renderer 6.0.2's
        // StopRendering(true) / StartRendering that is about grass (it has no public call for it). It still makes the grass
        // instances later, cell by cell as the camera comes near. False: this version lacks the members.
        static bool RebuildGrass(Type type, Component natureRenderer, Terrain terrain)
        {
            FindTypes();
            var grassField = type.GetField("_grassRenderer", Members);
            var renderingField = type.GetField("_isRendering", Members);
            var cacheSettings = type.GetMethod("CacheTerrainSettings", Members, null, Type.EmptyTypes, null);
            var create = grassRendererType?.GetConstructor(Members, null, new[] { typeof(Terrain) }, null);
            var load = grassRendererType?.GetMethod("Load", Members, null, new[] { typeof(bool) }, null);
            var destroy = grassRendererType?.GetMethod("Destroy", Members, null, Type.EmptyTypes, null);
            if (grassField == null || renderingField == null || cacheSettings == null || create == null || load == null || destroy == null) return false;
            // prefabs with a LOD Group by their LOD0 child, as when it starts; and the new grass's size and cells remembered,
            // or its next look at the settings would take the terrain for changed and restart everything
            Invoke(type, natureRenderer, "ReplaceUnsupportedDetails");
            cacheSettings.Invoke(natureRenderer, null);
            var previous = grassField.GetValue(natureRenderer);
            if (previous != null) destroy.Invoke(previous, null);
            grassField.SetValue(natureRenderer, null);
            // not drawing (out of the cameras): it builds the grass itself when it starts
            if (!(renderingField.GetValue(natureRenderer) is true) || !Bool(type, natureRenderer, "RenderDetailsWithNatureRenderer")) return true;
            var grass = create.Invoke(new object[] { terrain });
            grassField.SetValue(natureRenderer, grass);
            load.Invoke(grass, new object[] { true });
            return true;
        }

        // Drawing already: a restart, even when it cannot start on the new data (a treeless tile's TerrainData has no detail
        // map, CanInitialize is false); else it kept drawing the earlier data, destroyed by then. It waits for data it can
        // draw and starts by itself.
        static void Rebuild(Type type, Component natureRenderer)
        {
            if (Bool(type, natureRenderer, "IsInitialized")) { Invoke(type, natureRenderer, "Restart"); return; }
            var canInitialize = type.GetMethod("CanInitialize", Members, null, Type.EmptyTypes, null);
            if (!(canInitialize?.Invoke(natureRenderer, null) is true)) return;
            Invoke(type, natureRenderer, "Initialize");
            Invoke(type, natureRenderer, "StartRendering");
        }

        static void FindTypes()
        {
            if (searched) return;
            searched = true;
            natureRendererType = Type.GetType(NatureRendererTypeName);
            grassRendererType = Type.GetType(GrassRendererTypeName);
        }

        static void Set(Type type, object target, string name, object value) => type.GetProperty(name, Members)?.SetValue(target, value);
        static bool Bool(Type type, object target, string name) => type.GetProperty(name, Members)?.GetValue(target) is true;
        static void Invoke(Type type, object target, string name) => type.GetMethod(name, Members, null, Type.EmptyTypes, null)?.Invoke(target, null);
    }
}
