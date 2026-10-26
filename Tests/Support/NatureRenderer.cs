using UnityEngine;

namespace GeoWorld.Tests.Support
{
    // A stand-in for Nature Renderer's component, which Geo World finds by its name (outside the Editor folders, so that it can be attached
    // to an object; the assembly is only there while tests are enabled): it keeps the terrain data it was started with, as
    // Nature Renderer does, and notes whether the tile's trees were in when it started.
    public class NatureRenderer : MonoBehaviour
    {
        public bool IsInitialized { get; private set; }
        public Terrain Terrain { get; set; }
        public TerrainData TerrainData { get; set; }
        public int starts, restarts;
        public bool treesInWhenStarted = true;
        // Nature Renderer's default: a terrain is set up only while the camera is within its rendering distance
        public bool OnlyInitializeWithinRenderingDistance { get; set; } = true;
        public TerrainData dataWhenStarted;

        void OnEnable()
        {
            var terrain = GetComponent<Terrain>();
            // the first data it sees stays (as Nature Renderer's does) unless it was told another
            if (!TerrainData) TerrainData = terrain.terrainData;
            dataWhenStarted = TerrainData;
            var loader = GetComponent<GeoTerrainTreeLoader>();
            if (loader && !loader.Ready) treesInWhenStarted = false;
            IsInitialized = true;
            starts++;
        }

        void OnDisable() { IsInitialized = false; }

        public void Restart() { restarts++; TerrainData = GetComponent<Terrain>().terrainData; dataWhenStarted = TerrainData; }
    }
}
