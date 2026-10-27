using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Bakes the overview of a tiled world (GeoOverviewData, drawn by GeoOverview on the world's object):
    //  - near: the satellite imagery of every tile scaled down into one texture (from the tiles' own textures, nothing is
    //    downloaded) and the terrain's height on a coarse grid;
    //  - far: imagery of an area GeoOverviewSettings.farScale times the world's side around it, at low detail (GeoSatellite,
    //    from the tile server, cached in Library), on a grid that keeps under the world's layer where they overlap and sinks
    //    to the world's mean height a few cells away from its edge.
    // GeoTiles.Generate bakes it after the tiles; the Geo Tiled World's Inspector has Bake overview for a world made earlier.
    public static class GeoOverviewBaker
    {
        // Far grid vertices along a side: 129 over 200 km is 1.6 km a cell.
        public const int FarVertices = 129;
        // The far ring keeps the world's edge height this many far cells out, then sinks to the mean over FarFade cells.
        const float FarKeep = 1.5f, FarFade = 3;
        // Metres the far ring lies below the world's layer where they overlap, so that the two never fight for a pixel.
        public const float FarBelow = 10;
        static readonly Color32 Missing = new Color32(110, 110, 100, 255);

        public static string AssetPath(GeoTiledWorld world)
        {
            string folder = string.IsNullOrEmpty(world.folder) ? GeoPaths.Project : world.folder.TrimEnd('/');
            return folder + "/" + Path.GetFileName(folder) + " overview.asset";
        }

        // The far ring's imagery is kept from the last bake while what it is made from is the same (FarKey: its area, size and
        // tile server), so rebuilding tiles does not make it again; only its heights follow the world's. refreshFar makes it again.
        public static GeoOverviewData Bake(GeoTiledWorld world, List<string> notes = null, bool refreshFar = false)
        {
            if (!world) throw new ArgumentNullException(nameof(world));
            if (world.columns <= 0 || world.rows <= 0 || world.tiles.Count == 0) throw new InvalidOperationException(world.name + " has no tiles to make an overview of.");
            notes ??= new List<string>();
            var options = world.settings.overview ?? new GeoOverviewSettings();
            try
            {
                var near = Near(world, options, out float[] heights, out bool[] missing, out int baked);
                var area = Area(world);
                var data = Asset(world, out string path);
                data.worldName = world.name;
                data.area = area; data.origin = world.origin; data.tileSize = world.tileSize;
                data.firstIndex = world.firstIndex; data.columns = world.columns; data.rows = world.rows;
                data.cellsPerTile = Mathf.Clamp(options.cellsPerTile, 2, 64); data.heights = heights;
                Replace(data, near);
                data.near = near;
                notes.Add($"Overview: {baked} tiles in {near.width} x {near.height} ({world.tileSize * world.columns / near.width:F1} m a texel).");

                data.missing = missing;
                var origin = options.farScale > 1.01f ? Origin(world) : null;
                float side = Mathf.Max(area.width, area.height) * options.farScale;
                var farArea = new Rect(area.center.x - side / 2, area.center.y - side / 2, side, side);
                string key = origin != null ? FarKey(origin, farArea, world.settings, options) : null;
                if (key != null && !refreshFar && data.far && data.farKey == key)
                {
                    data.farHeight = Mean(heights);
                    data.farHeights = FarHeights(area, data.NearVerticesX, data.NearVerticesZ, heights, farArea, data.farVertices, data.farHeight);
                    notes.Add($"Overview: far ring of {side / 1000:F0} x {side / 1000:F0} km kept from the last bake (Bake far ring again to download it anew).");
                }
                else
                {
                    data.far = null; data.farHeights = null; data.farVertices = 0; data.farArea = default; data.farKey = null;
                    if (options.farScale > 1.01f && origin == null) notes.Add("Overview: no far ring, the world's export (environment.json) with its geographic origin is not found.");
                    if (origin != null)
                        try
                        {
                            var far = Far(origin, farArea, world.settings, options, notes);
                            data.farArea = farArea; data.farVertices = FarVertices; data.farKey = key;
                            data.farHeight = Mean(heights);
                            data.farHeights = FarHeights(area, data.NearVerticesX, data.NearVerticesZ, heights, farArea, FarVertices, data.farHeight);
                            Replace(data, far);
                            data.far = far;
                            notes.Add($"Overview: far ring of {side / 1000:F0} x {side / 1000:F0} km in {far.width} x {far.height} ({side / far.width:F0} m a texel).");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { notes.Add("Overview: no far ring, its imagery could not be had (" + e.Message + ")."); }
                }
                // a far ring that is gone leaves no texture behind in the asset
                if (!data.far) RemoveTexture(data, "Overview far imagery");
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssetIfDirty(data);
                Attach(world, data);
                return data;
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        // The world's tiles in metres.
        public static Rect Area(GeoTiledWorld world) => new Rect(
            world.origin.x + world.firstIndex.x * world.tileSize, world.origin.y + world.firstIndex.y * world.tileSize,
            world.columns * world.tileSize, world.rows * world.tileSize);

        // The tiles' satellite textures, each scaled down on the GPU into its square of one texture (row 0 = south), and the
        // terrain heights at the grid's vertices (world Y). Missing: the tiles that got no imagery.
        static Texture2D Near(GeoTiledWorld world, GeoOverviewSettings options, out float[] heights, out bool[] missing, out int baked)
        {
            int columns = world.columns, rows = world.rows, cells = Mathf.Clamp(options.cellsPerTile, 2, 64);
            int texels = Mathf.Max(4, Mathf.Clamp(options.nearResolution, 512, 8192) / Mathf.Max(columns, rows) / 4 * 4);
            int width = columns * texels, height = rows * texels, countX = columns * cells + 1, countZ = rows * cells + 1;
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Missing;
            missing = new bool[columns * rows];
            for (int i = 0; i < missing.Length; i++) missing[i] = true;
            heights = new float[countX * countZ];
            for (int i = 0; i < heights.Length; i++) heights[i] = float.NaN;

            // HideAndDontSave: unloading the tiles' assets on the way must not take these too
            var scaled = new RenderTexture(texels, texels, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { hideFlags = HideFlags.HideAndDontSave };
            var readback = new Texture2D(texels, texels, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.HideAndDontSave };
            baked = 0;
            try
            {
                for (int t = 0; t < world.tiles.Count; t++)
                {
                    var tile = world.tiles[t];
                    if (EditorUtility.DisplayCancelableProgressBar("Geo World overview", $"Tile {tile.index.x}, {tile.index.y} ({t + 1} / {world.tiles.Count})", 0.5f * t / world.tiles.Count))
                        throw new OperationCanceledException("The overview was cancelled.");
                    int column = tile.index.x - world.firstIndex.x, row = tile.index.y - world.firstIndex.y;
                    var terrain = tile.empty ? null : Terrain(tile.assetPath);
                    var layer = terrain && terrain.terrainLayers.Length > 0 ? terrain.terrainLayers[0] : null;
                    if (column < 0 || row < 0 || column >= columns || row >= rows || !terrain) continue;

                    // the satellite layer spans the tile exactly (its tile size is the tile's, no offset)
                    if (layer && layer.diffuseTexture)
                    {
                        var active = RenderTexture.active; // Blit makes its target the active one
                        Graphics.Blit(layer.diffuseTexture, scaled);
                        RenderTexture.active = scaled;
                        readback.ReadPixels(new Rect(0, 0, texels, texels), 0, 0, false);
                        RenderTexture.active = active;
                        missing[row * columns + column] = false;
                        var block = readback.GetPixels32();
                        for (int y = 0; y < texels; y++) Array.Copy(block, y * texels, pixels, (row * texels + y) * width + column * texels, texels);
                    }
                    for (int z = 0; z <= cells; z++)
                        for (int x = 0; x <= cells; x++)
                            heights[(row * cells + z) * countX + column * cells + x] = world.baseY + terrain.GetInterpolatedHeight(x / (float)cells, z / (float)cells);
                    baked++;
                    if (baked % 25 == 0) EditorUtility.UnloadUnusedAssetsImmediate();
                }
            }
            finally
            {
                scaled.Release();
                Object.DestroyImmediate(scaled);
                Object.DestroyImmediate(readback);
            }
            // tiles with no terrain (nothing was exported there): the world's base
            for (int i = 0; i < heights.Length; i++) if (float.IsNaN(heights[i])) heights[i] = world.baseY;

            EditorUtility.DisplayProgressBar("Geo World overview", "Compressing the overview", 0.55f);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, false)
                { name = "Overview satellite imagery", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            texture.SetPixels32(pixels); texture.Apply(true);
            GeoSatellite.Compress(texture, pixels, world.settings, null);
            return texture;
        }

        static TerrainData Terrain(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                if (asset is TerrainData terrain) return terrain;
            return null;
        }

        // The far imagery: GeoSatellite over the far area at the far resolution, the world's tile server and compression.
        static Texture2D Far(GeoOrigin origin, Rect farArea, GenerationSettings settings, GeoOverviewSettings options, List<string> notes)
        {
            var copy = JsonUtility.FromJson<GenerationSettings>(JsonUtility.ToJson(settings));
            copy.satelliteResolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(options.farResolution), 1024, 8192);
            var bounds = new GeoBounds { minX = farArea.xMin, minZ = farArea.yMin, maxX = farArea.xMax, maxZ = farArea.yMax };
            var far = GeoSatellite.Build(origin, bounds, copy, notes, (label, f) =>
            {
                if (EditorUtility.DisplayCancelableProgressBar("Geo World overview: far ring", label, 0.6f + 0.4f * f))
                    throw new OperationCanceledException("The overview was cancelled.");
            });
            far.name = "Overview far imagery";
            return far;
        }

        // Geographic origin of the world's exports (they share one).
        static GeoOrigin Origin(GeoTiledWorld world)
        {
            var paths = new List<string>();
            if (world.parts != null) foreach (var part in world.parts) if (part != null) paths.Add(part.environmentPath);
            paths.Add(world.environmentPath);
            foreach (var path in paths)
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var origin = GeoData.ReadEnvironment(path)?.origin;
                    if (origin != null) return origin;
                }
            return null;
        }

        // Heights of the far grid (world Y, rows from the south). Over the world and within FarKeep cells of it a vertex is the
        // lowest near height within one far cell of it (of the nearest point of the world, outside): a far cell that the world's
        // layer covers is then nowhere above it. Further out it sinks to level (the mean) over FarFade cells.
        public static float[] FarHeights(Rect area, int nearX, int nearZ, float[] near, Rect farArea, int count, float level)
        {
            var result = new float[count * count];
            float cell = farArea.width / (count - 1), stepX = area.width / (nearX - 1), stepZ = area.height / (nearZ - 1);
            for (int j = 0; j < count; j++)
                for (int i = 0; i < count; i++)
                {
                    float x = farArea.xMin + i * cell, z = farArea.yMin + j * cell;
                    float cx = Mathf.Clamp(x, area.xMin, area.xMax), cz = Mathf.Clamp(z, area.yMin, area.yMax);
                    int x0 = Mathf.Clamp(Mathf.FloorToInt((cx - cell - area.xMin) / stepX), 0, nearX - 1), x1 = Mathf.Clamp(Mathf.CeilToInt((cx + cell - area.xMin) / stepX), 0, nearX - 1);
                    int z0 = Mathf.Clamp(Mathf.FloorToInt((cz - cell - area.yMin) / stepZ), 0, nearZ - 1), z1 = Mathf.Clamp(Mathf.CeilToInt((cz + cell - area.yMin) / stepZ), 0, nearZ - 1);
                    float low = float.MaxValue;
                    for (int b = z0; b <= z1; b++) for (int a = x0; a <= x1; a++) low = Mathf.Min(low, near[b * nearX + a]);
                    float distance = Mathf.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                    float t = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(FarKeep * cell, (FarKeep + FarFade) * cell, distance));
                    result[j * count + i] = Mathf.Lerp(low - FarBelow, level, t);
                }
            return result;
        }

        static float Mean(float[] values)
        {
            double sum = 0;
            foreach (float v in values) sum += v;
            return values.Length > 0 ? (float)(sum / values.Length) : 0;
        }

        static GeoOverviewData Asset(GeoTiledWorld world, out string path)
        {
            path = AssetPath(world);
            var data = AssetDatabase.LoadAssetAtPath<GeoOverviewData>(path);
            if (data) return data;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            data = ScriptableObject.CreateInstance<GeoOverviewData>();
            AssetDatabase.CreateAsset(data, path);
            return data;
        }

        // What the far imagery is made from: the same key, the same imagery.
        public static string FarKey(GeoOrigin origin, Rect farArea, GenerationSettings settings, GeoOverviewSettings options) =>
            string.Join("|", origin.lat.ToString("R"), origin.lon.ToString("R"), farArea.x.ToString("R"), farArea.y.ToString("R"), farArea.width.ToString("R"),
                Mathf.ClosestPowerOfTwo(options.farResolution), settings.satelliteTiles, settings.satelliteMaxZoom, settings.satelliteCompression, settings.satelliteQuality);

        // Puts a texture into the overview's asset in place of the one of the same name it had.
        static void Replace(GeoOverviewData data, Texture2D texture)
        {
            RemoveTexture(data, texture.name);
            AssetDatabase.AddObjectToAsset(texture, data);
        }

        static void RemoveTexture(GeoOverviewData data, string name)
        {
            foreach (var old in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(data)))
                if (old is Texture2D && old.name == name) Object.DestroyImmediate(old, true);
        }

        // The world's object draws the overview.
        static void Attach(GeoTiledWorld world, GeoOverviewData data)
        {
            var overview = world.GetComponent<GeoOverview>();
            if (!overview) overview = Undo.AddComponent<GeoOverview>(world.gameObject);
            else Undo.RecordObject(overview, "Geo World overview");
            overview.data = data;
            foreach (var streamer in Object.FindObjectsByType<GeoTileStreamer>(FindObjectsSortMode.None))
                if (streamer.world == world) overview.streamer = streamer;
            EditorUtility.SetDirty(overview);
            if (world.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        }
    }
}
