using GeoWorld;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>Small runtime helpers for the GeoWorld tile layout and loaded terrain.</summary>
    public static class GeoWorldTerrainRuntime
    {
        public static GeoTileStreamer Streamer => Object.FindFirstObjectByType<GeoTileStreamer>();
        public static GeoTiledWorld World => Streamer != null
            ? Streamer.world
            : Object.FindFirstObjectByType<GeoTiledWorld>();

        public static bool IsSurfaceLoadedAt(Vector3 position)
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null)
                    continue;

                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                if (position.x >= origin.x && position.x <= origin.x + size.x &&
                    position.z >= origin.z && position.z <= origin.z + size.z)
                    return true;
            }

            return false;
        }

        public static bool IsCurrentSurfaceLoaded(GeoTileStreamer streamer)
        {
            if (streamer == null)
                return true;

            Transform target = streamer.target != null
                ? streamer.target
                : Camera.main != null ? Camera.main.transform : null;

            return target != null && IsSurfaceLoadedAt(target.position);
        }
    }
}
