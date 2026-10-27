using System;
using UnityEngine;

namespace GeoWorld
{
    public enum GeoBuildingMode { Auto, Editable, Merged }
    // How the satellite texture is stored: DXT1, or DXT1 with Crunch compression on top (smaller on disk and in a build)
    public enum GeoSatelliteCompression { DXT1, Crunched }

    [Serializable]
    public class GenerationSettings
    {
        public int heightResolution = 513;
        public int textureResolution = 512;
        [Tooltip("Detail of roads and painted centre lines on the terrain. 2048 is economical; 4096 gives sharper lines on an 800 m area.")]
        public int roadPaintResolution = 2048;
        [Tooltip("The terrain casts shadows (hills shading valleys at a low sun, as in the Carpathians). Off by default: buildings and trees still cast theirs on the ground, the imagery has its own, and a city frame takes 3-6 ms less.")]
        public bool terrainShadows = false;
        [Range(0, 12)] public float blendMetres = 3;
        [Range(0, 12)] public float roadShoulder = 2.5f;
        [Range(0.5f, 8)] public float riverDepth = 1.5f;
        [Range(0.5f, 10)] public float pathStep = 2;
        [Range(0, 3)] public float junctionSnap = 0.75f;
        public bool gradeRoads = true;
        public bool carveRivers = true;
        public bool laneMarkings = true;
        [Tooltip("Draw ground roads as meshes with crisp edges, real markings and the photographic asphalt, instead of only painting them into the terrain (which is blurry at 1-2 m per texel).")]
        public bool roadMeshes = true;
        public bool buildings = true;
        [Header("Buildings")]
        [Tooltip("Editable: one object per building (a village). Merged: buildings baked into chunks of mesh (a city). Auto merges above Merge Above buildings, and always in a tiled world.")]
        public GeoBuildingMode buildingMode = GeoBuildingMode.Auto;
        [Range(100, 20000)] public int mergeAbove = 2500;
        [Range(50, 500)] public float chunkSize = 200;
        [Tooltip("Merged chunks get a mesh collider (slower to build, heavier). Terrain collision is always there.")]
        public bool mergedColliders = false;
        [Tooltip("Draw war damage from the export's building condition, exactly as the app gives it (which evidence counts, radar estimates included, is decided in the app). Off: every building whole.")]
        public bool applyCondition = true;
        [Tooltip("Draw roof shape and colour that are only typical for the kind of building (the export marks them estimated). Off: only mapped and reviewed roof data; the rest keeps the style's roof.")]
        public bool estimatedRoofs = true;
        [Tooltip("Draw damaged buildings with destroyed-building models from the ruin library, fitted to each footprint. Buildings no model fits keep the procedural damage. Default: every building the app gives as damaged.")]
        public GeoRuinUse ruinModels = GeoRuinUse.AllDamage;
        [Tooltip("The ruin models to choose from (Tools > Geo World > Import ruin models). Empty: procedural damage only.")]
        public GeoRuinLibrary ruinLibrary;
        [Tooltip("Draw buildings with intact-building models from the home library, fitted to each footprint, with the roofs of the export: those not known to be damaged, or all that still stand (damaged ones too, without their soot). Buildings no model fits keep the style's walls and roof.")]
        public GeoHomeUse homeModels = GeoHomeUse.IntactAndUnknown;
        [Tooltip("The home models to choose from (Tools > Geo World > Import home models). Empty: the style's walls and roof only.")]
        public GeoHomeLibrary homeLibrary;
        [Tooltip("Metres of a ruin's yard and debris kept around its footprint, at most (a big building may keep up to a fifth of its width). Less keeps streets and neighbours clear.")]
        [Range(0, 12)] public float ruinApron = 3;
        [Tooltip("A merged city draws its ruin and home models with reduced copies (about half the triangles, the same corners, eaves and foot): smaller assets and faster frames for buildings seen from the street or further. Editable buildings always show the full models.")]
        public bool reducedModels = true;
        [Tooltip("A merged city draws each home as the home library's own model with a transform (one object per home), instead of baking a stretched copy of its geometry into the chunks: a fraction of the size. On sloping ground a home stands on a plain plinth.")]
        public bool instancedHomes = true;
        public bool trees = true;
        [Tooltip("Photographic ground textures (Tools > Geo World > Import ground textures). Without them, or off, layers are plain colours.")]
        public bool groundTextures = true;
        [Tooltip("Drape satellite imagery over the terrain instead of the blended ground layers (the default way of generating). Tiles are downloaded from the tile server below (check its terms of use) and cached in Library. Roads are then only seen in the imagery, unless Road Meshes is on. Off: the blended ground layers.")]
        public bool satelliteImagery = true;
        [Tooltip("Texels along each side of the satellite texture (1024 to 8192); 0 is Auto: about 0.4 m a texel, the detail of the imagery (2048 for a 1 km tile, at most 8192). The tile zoom follows.")]
        public int satelliteResolution = 0;
        [Tooltip("XYZ (Web Mercator) tile URL with {z}, {x} and {y}. Empty: Esri World Imagery.")]
        public string satelliteTiles = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}?blankTile=false";
        [Tooltip("Deepest tile zoom to ask the server for (19 is about 20-30 cm per pixel).")]
        [Range(1, 22)] public int satelliteMaxZoom = 19;
        [Tooltip("How the satellite texture is kept in the world's assets and in a build. Crunched: DXT1 with Crunch compression on top, about a quarter of the size (a 1 km tile's 2048 texture 2.7 MB to about 0.6-0.9 MB); Unity turns it back into DXT1 on the loading thread, so it takes the same memory and looks the same on the GPU. DXT1: as before, at DXT1's best encoding.")]
        public GeoSatelliteCompression satelliteCompression = GeoSatelliteCompression.Crunched;
        [Tooltip("Crunch quality, 1-100: higher keeps more of the picture's detail and is bigger. At 75 the blocks of Crunch hardly show from a street's height; 50 is Unity's default for imported textures.")]
        [Range(1, 100)] public int satelliteQuality = 75;
        [Header("Streaming (a tiled world)")]
        [Tooltip("Metres around the camera within which the tiles are loaded while playing (1-10 km). Every tile within it is in memory: 2 km is 21-24 tiles of 1 km, 5 km about 100, 10 km about 360; memory and the time to load grow with the square of the radius. Applied to the world's tile streamer; the tiles are not rebuilt.")]
        [Range(GeoTileStreamer.MinRadius, GeoTileStreamer.MaxRadius)] public float loadRadius = GeoTileStreamer.DefaultRadius;
        [Header("Props and vegetation")]
        public bool mappedProps = true;
        public bool vegetation = true;
        [Tooltip("Where the project has Nature Renderer (Visual Design Cafe): the trees are the terrain's own trees, drawn by Nature Renderer, instead of Geo World's instanced trees. Ignored without it.")]
        [GeoNatureRendererOnly] public bool natureRenderer = false;
        [Tooltip("Your own tree prefabs (a LOD Group or a Mesh Renderer on the root, pivot on the ground). Each tree takes one of them and is scaled to its mapped or generated height (from half to twice the prefab's own size). They are the terrain's trees, drawn by Unity's terrain or by Nature Renderer. Empty: Geo World's low-poly tree.")]
        public GameObject[] treePrefabs = new GameObject[0];
        [Tooltip("Invent plausible lamps near built-up roads and benches on park paths. These positions are labeled generated.")]
        public bool streetDecoration = false;
        public GeoPropCatalog propCatalog;
        [Range(3, 25)] public float treeSpacing = 8;
        [Range(0, 1)] public float vegetationDensity = 0.7f;
        public int decorationSeed = 137;
        [Tooltip("Props and trees. As separate objects (editable world) the limit is 20 000; baked into chunks (merged city) it can be far higher.")]
        [Range(100, 200000)] public int maxProps = 60000;
        [Tooltip("In a merged city, bake trees, fences and street props into chunks of mesh too (one object per tree would stall the editor). Props from the prop catalog and photo references stay objects.")]
        public bool mergeProps = true;
        // Last of the settings: a tile's fingerprint leaves it out (GeoTiles.TileSettingsJson), so changing it rebuilds no tile.
        [Header("Overview (a tiled world)")]
        [Tooltip("The whole world as one coarse satellite layer under its tiles and a far ring of low-detail imagery around it, seen from high above (GeoOverview).")]
        public GeoOverviewSettings overview = new GeoOverviewSettings();
    }

    [DisallowMultipleComponent]
    public class GeoWorldRoot : MonoBehaviour
    {
        [HideInInspector] public string environmentPath;
        [HideInInspector] public string buildingsPath;
        [HideInInspector] public string assetPath;
        public GenerationSettings settings = new GenerationSettings();
        [HideInInspector] public Terrain terrain;
        [HideInInspector] public int roadCount, riverCount, buildingCount, waterCount, treeCount;
        [HideInInspector] public int junctionCount;
        [HideInInspector] public int propCount;
        [HideInInspector] public string report;
        // One tile of a tiled world (GeoTiledWorld): its area in world metres, and the height scale all tiles share.
        [HideInInspector] public bool tile;
        [HideInInspector] public Vector2Int tileIndex;
        [HideInInspector] public Rect tileArea;
        [HideInInspector] public float tileSize, tileBaseY, tileHeightRange;
        [HideInInspector] public int tileParts = 1; // exports this tile is built from (more than one where exports meet)

        // The tile's water takes the water material all tiles share (tiles made before it had a Lit material of their own that reflected the sky).
        void Awake() { if (Application.isPlaying) GeoWaterLook.Apply(transform); }
    }
}
