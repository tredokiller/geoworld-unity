using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeoWorld
{
    // Draws a tiled world's baked overview (GeoOverviewData) under its streamed tiles: the world as one coarse satellite layer on
    // a height grid, and around it a far ring of low-detail imagery that sinks to the world's mean height. From high above the
    // tiles around the camera are a small square, and a 20 km world ends in an edge; with the overview the ground goes on to
    // the horizon. Both layers lie a little below the terrain and are cut away where a tile is loaded (_LoadedTiles), and the
    // far clip of a camera that sees them is stretched to the far ring's corners while it renders (and put back after).
    // On the world's object; GeoOverviewBaker adds it.
    [DisallowMultipleComponent]
    public class GeoOverview : MonoBehaviour
    {
        public const string Resource = "GeoWorld/Overview";

        public GeoOverviewData data;
        [Tooltip("Empty: the scene's tile streamer of this world.")]
        public GeoTileStreamer streamer;
        [Tooltip("Metres the overview lies below the terrain, so that it stays hidden while a tile is loaded or unloaded.")]
        [Min(0)] public float depthBelowTerrain = 2;
        public Color tint = Color.white;
        [Tooltip("Cut the overview away where a tile is loaded.")]
        public bool hideUnderLoadedTiles = true;
        [Tooltip("Stretch the far clip of the cameras that see the overview to its farthest corner while they render.")]
        public bool extendFarClip = true;
        [Tooltip("The far clip is never stretched beyond this (metres): depth precision on platforms without reversed Z (OpenGL ES) suffers beyond some 30-50 km.")]
        [Min(1000)] public float maxFarClip = 150000;

        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap"), TintId = Shader.PropertyToID("_Tint"),
            LoadedTilesId = Shader.PropertyToID("_LoadedTiles"), TileGridId = Shader.PropertyToID("_TileGrid"), TileGridSizeId = Shader.PropertyToID("_TileGridSize");
        const float MaskInterval = 0.1f;

        readonly Dictionary<Camera, float> restoreFar = new Dictionary<Camera, float>();
        readonly List<Object> owned = new List<Object>();
        // Per tile of the grid, 255 where a layer is cut away: the world's layer where a tile is loaded or has no imagery (the far
        // ring shows there), the far ring where a tile is loaded.
        Texture2D nearMask, farMask;
        byte[] nearPixels, farPixels;
        string[] tileScenes;
        float lowest, nextMask;

        public bool Drawn { get; private set; }

        void Awake()
        {
            var template = Resources.Load<Material>(Resource);
            if (!data || !data.HasNear || !template)
            {
                Debug.LogWarning("Geo World: " + name + " has no baked overview" + (template ? "" : " (or the package's " + Resource + " material is missing)") +
                    ": bake it in the Geo Tiled World's Inspector.", this);
                enabled = false;
                return;
            }
            if (!streamer)
                foreach (var candidate in FindObjectsByType<GeoTileStreamer>(FindObjectsSortMode.None))
                    if (!streamer || candidate.world && candidate.world.gameObject == gameObject) streamer = candidate;

            nearMask = Own(Mask("near")); farMask = Own(Mask("far"));
            nearPixels = new byte[data.columns * data.rows]; farPixels = new byte[nearPixels.Length];
            IndexTiles();

            var nearMesh = Own(NearMesh(data, depthBelowTerrain));
            lowest = nearMesh.bounds.min.y;
            Layer("Overview: near", nearMesh, Own(MaterialFor(template, data.near, nearMask)));
            if (data.HasFar)
            {
                var farMesh = Own(FarMesh(data, depthBelowTerrain));
                lowest = Mathf.Min(lowest, farMesh.bounds.min.y);
                Layer("Overview: far", farMesh, Own(MaterialFor(template, data.far, farMask)));
            }
            Drawn = true;
            UpdateMasks(true);
        }

        T Own<T>(T thing) where T : Object { owned.Add(thing); return thing; }

        Texture2D Mask(string label) => new Texture2D(data.columns, data.rows, TextureFormat.R8, false, true)
            { name = "Geo World overview: " + label + " cut", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };

        Material MaterialFor(Material template, Texture texture, Texture mask)
        {
            var material = new Material(template) { name = template.name + " (" + texture.name + ")" };
            material.SetTexture(BaseMapId, texture);
            material.SetColor(TintId, tint);
            material.SetTexture(LoadedTilesId, mask);
            material.SetVector(TileGridId, new Vector4(data.origin.x, data.origin.y, data.tileSize, 0));
            material.SetVector(TileGridSizeId, new Vector4(data.firstIndex.x, data.firstIndex.y, data.columns, data.rows));
            return material;
        }

        void Layer(string label, Mesh mesh, Material material)
        {
            var layer = new GameObject(label) { layer = gameObject.layer };
            layer.transform.SetParent(transform, false);
            layer.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            layer.transform.localScale = Vector3.one;
            layer.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = layer.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.allowOcclusionWhenDynamic = false;
        }

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            RenderPipelineManager.endCameraRendering += EndCamera;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            RenderPipelineManager.endCameraRendering -= EndCamera;
            foreach (var pair in restoreFar) if (pair.Key) pair.Key.farClipPlane = pair.Value;
            restoreFar.Clear();
        }

        void OnDestroy()
        {
            foreach (var thing in owned) if (thing) Destroy(thing);
            owned.Clear();
        }

        void Update()
        {
            if (!Drawn || Time.unscaledTime < nextMask) return;
            nextMask = Time.unscaledTime + MaskInterval;
            UpdateMasks(false);
        }

        void UpdateMasks(bool force)
        {
            bool nearChanged = force, farChanged = force, far = data.HasFar;
            for (int row = 0, i = 0; row < data.rows; row++)
                for (int column = 0; column < data.columns; column++, i++)
                {
                    bool loaded = hideUnderLoadedTiles && streamer && streamer.IsLoaded(tileScenes[i]);
                    byte nearCut = loaded || far && data.Missing(column, row) ? (byte)255 : (byte)0, farCut = loaded ? (byte)255 : (byte)0;
                    if (nearPixels[i] != nearCut) { nearPixels[i] = nearCut; nearChanged = true; }
                    if (farPixels[i] != farCut) { farPixels[i] = farCut; farChanged = true; }
                }
            if (nearChanged) { nearMask.SetPixelData(nearPixels, 0); nearMask.Apply(false); }
            if (farChanged) { farMask.SetPixelData(farPixels, 0); farMask.Apply(false); }
        }

        // The scene of each tile of the grid, to ask the streamer whether it is loaded.
        void IndexTiles()
        {
            tileScenes = new string[data.columns * data.rows];
            var world = streamer ? streamer.world : GetComponent<GeoTiledWorld>();
            if (!world) return;
            if (!string.IsNullOrEmpty(data.worldName) && world.name != data.worldName)
                Debug.LogWarning("Geo World: the overview " + data.name + " was baked from " + data.worldName + ", the streamer streams " + world.name + ".", this);
            foreach (var tile in world.tiles)
            {
                int x = tile.index.x - data.firstIndex.x, z = tile.index.y - data.firstIndex.y;
                if (x >= 0 && z >= 0 && x < data.columns && z < data.rows) tileScenes[z * data.columns + x] = tile.scenePath;
            }
        }

        void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!Drawn || !extendFarClip || camera.cameraType != CameraType.Game || (camera.cullingMask & (1 << gameObject.layer)) == 0) return;
            if (camera.TryGetComponent(out UniversalAdditionalCameraData extra) && extra.renderType == CameraRenderType.Overlay) return;
            // The same far clip in every frame: one that followed the farthest corner changed as the camera moved, and the water's
            // colour from the depth under it jumped from frame to frame (flicker seen from high above, the more the farther).
            float far = Mathf.Min(StableFarClip(data.Extent, lowest), maxFarClip);
            if (far <= camera.farClipPlane) return;
            if (!restoreFar.ContainsKey(camera)) restoreFar.Add(camera, camera.farClipPlane);
            camera.farClipPlane = far;
        }

        void EndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!restoreFar.TryGetValue(camera, out float far)) return;
            restoreFar.Remove(camera);
            camera.farClipPlane = far;
        }

        // From a point to the farthest corner of an area at a height.
        public static float FarthestDistance(Rect area, Vector3 point, float height)
        {
            float dx = Mathf.Max(Mathf.Abs(point.x - area.xMin), Mathf.Abs(point.x - area.xMax));
            float dz = Mathf.Max(Mathf.Abs(point.z - area.yMin), Mathf.Abs(point.z - area.yMax));
            float dy = point.y - height;
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Highest a camera is expected to see the overview from, above its lowest point (metres).
        public const float StableFarHeight = 20000;

        // A far clip that reaches the whole area from anywhere over it, up to StableFarHeight above its lowest point.
        public static float StableFarClip(Rect area, float lowest) =>
            FarthestDistance(area, new Vector3(area.xMin, lowest + StableFarHeight, area.yMin), lowest);

        // The world's layer: its height grid, depth metres down, UV (0,0)-(1,1) over the world's area.
        public static Mesh NearMesh(GeoOverviewData data, float depth) =>
            Grid("Geo World overview: near", data.area, data.NearVerticesX, data.NearVerticesZ, data.heights, depth);

        // The far ring: its height grid over the whole far area. Under the world it is below the world's layer, and shows
        // where that layer is cut away (a tile with no imagery).
        public static Mesh FarMesh(GeoOverviewData data, float depth) =>
            Grid("Geo World overview: far", data.farArea, data.farVertices, data.farVertices, data.farHeights, depth);

        static Mesh Grid(string name, Rect area, int countX, int countZ, float[] heights, float depth)
        {
            var vertices = new Vector3[countX * countZ];
            var uvs = new Vector2[vertices.Length];
            float low = float.MaxValue, high = float.MinValue;
            for (int z = 0; z < countZ; z++)
                for (int x = 0; x < countX; x++)
                {
                    int i = z * countX + x;
                    float u = x / (float)(countX - 1), v = z / (float)(countZ - 1), y = heights[i] - depth;
                    vertices[i] = new Vector3(area.xMin + u * area.width, y, area.yMin + v * area.height);
                    uvs[i] = new Vector2(u, v);
                    low = Mathf.Min(low, y); high = Mathf.Max(high, y);
                }
            var triangles = new List<int>((countX - 1) * (countZ - 1) * 6);
            for (int z = 0; z < countZ - 1; z++)
                for (int x = 0; x < countX - 1; x++)
                {
                    int i = z * countX + x;
                    triangles.Add(i); triangles.Add(i + countX); triangles.Add(i + 1);
                    triangles.Add(i + 1); triangles.Add(i + countX); triangles.Add(i + countX + 1);
                }
            var mesh = new Mesh { name = name, indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(new Vector3(area.center.x, (low + high) * 0.5f, area.center.y), new Vector3(area.width, Mathf.Max(1, high - low), area.height));
            return mesh;
        }
    }
}
