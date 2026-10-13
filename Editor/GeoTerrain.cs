using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GeoWorld.Editor
{
    public class HeightField
    {
        public readonly GeoBounds bounds;
        public readonly int resolution;
        public readonly float[,] heights;
        public HeightField(GeoBounds bounds, int resolution)
        { this.bounds = bounds; this.resolution = resolution; heights = new float[resolution, resolution]; }
        public float X(int x) => Mathf.Lerp(bounds.minX, bounds.maxX, x / (float)(resolution - 1));
        public float Z(int z) => Mathf.Lerp(bounds.minZ, bounds.maxZ, z / (float)(resolution - 1));
        public float Sample(float x, float z) => Bilinear(heights,
            (x - bounds.minX) / bounds.Width * (resolution - 1),
            (z - bounds.minZ) / bounds.Depth * (resolution - 1));
        public static float Bilinear(float[,] values, float x, float z)
        {
            x = Mathf.Clamp(x, 0, values.GetLength(1) - 1); z = Mathf.Clamp(z, 0, values.GetLength(0) - 1);
            int x0 = (int)x, z0 = (int)z;
            int x1 = Mathf.Min(x0 + 1, values.GetLength(1) - 1), z1 = Mathf.Min(z0 + 1, values.GetLength(0) - 1);
            return Mathf.Lerp(Mathf.Lerp(values[z0, x0], values[z0, x1], x - x0),
                Mathf.Lerp(values[z1, x0], values[z1, x1], x - x0), z - z0);
        }

        public static HeightField Load(EnvironmentData data, string environmentPath, int resolution, List<string> notes)
        {
            var result = new HeightField(data.bounds, resolution);
            var source = GeoHeightSource.Load(data, environmentPath);
            if (source == null) { notes.Add("No heightmap: generated flat terrain at Y = 0."); return result; }
            for (int z = 0; z < resolution; z++)
                for (int x = 0; x < resolution; x++)
                    result.heights[z, x] = source.Sample(result.X(x), result.Z(z));
            return result;
        }

        public void RasterSegment(Vector3 a, Vector3 b, float radius, Action<int, int, float, float> visit)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.x, b.x) - radius - bounds.minX) / bounds.Width * (resolution - 1)), 0, resolution - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.x, b.x) + radius - bounds.minX) / bounds.Width * (resolution - 1)), 0, resolution - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.z, b.z) - radius - bounds.minZ) / bounds.Depth * (resolution - 1)), 0, resolution - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.z, b.z) + radius - bounds.minZ) / bounds.Depth * (resolution - 1)), 0, resolution - 1);
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
            {
                float d = GeoData.SegmentDistance(new Vector2(X(x), Z(z)), new Vector2(a.x, a.z), new Vector2(b.x, b.z), out float t);
                if (d <= radius) visit(x, z, d, t);
            }
        }
    }

    // The export's heightmap as ground height at any world x/z (bilinear between its cells). A height field samples it at
    // its grid points; a tiled world samples it for every tile, so tiles side by side get the same heights at their edges.
    public sealed class GeoHeightSource
    {
        readonly float[,] source;
        readonly HeightmapInfo hm;
        public readonly float min, max;

        GeoHeightSource(float[,] source, HeightmapInfo hm)
        {
            this.source = source; this.hm = hm; min = float.MaxValue; max = float.MinValue;
            foreach (float h in source) { min = Mathf.Min(min, h); max = Mathf.Max(max, h); }
        }

        // Null when the export has no heightmap.
        public static GeoHeightSource Load(EnvironmentData data, string environmentPath)
        {
            var hm = data.terrain?.heightmap;
            if (hm == null || string.IsNullOrEmpty(hm.file)) return null;
            if (hm.width < 2 || hm.height < 2 || (long)hm.width * hm.height > 17000000 || hm.cellSize <= 0 ||
                !GeoData.Finite(hm.cellSize) || !GeoData.Finite(hm.minHeight) || !GeoData.Finite(hm.maxHeight) ||
                !GeoData.Finite(hm.originGround) || hm.maxHeight < hm.minHeight ||
                (!string.IsNullOrEmpty(hm.encoding) && hm.encoding != "uint16-le") ||
                (!string.IsNullOrEmpty(hm.rowOrder) && hm.rowOrder != "north-to-south"))
                throw new InvalidDataException("Invalid heightmap metadata. Expected uint16-le, north-to-south rows.");
            byte[] bytes = File.ReadAllBytes(GeoData.PackageFile(environmentPath, hm.file));
            if (bytes.LongLength != (long)hm.width * hm.height * 2)
                throw new InvalidDataException("Heightmap byte count does not match its width and height.");
            var source = new float[hm.height, hm.width];
            for (int row = 0; row < hm.height; row++)
                for (int col = 0; col < hm.width; col++)
                {
                    int index = (row * hm.width + col) * 2;
                    int value = bytes[index] | bytes[index + 1] << 8;
                    source[row, col] = Mathf.Lerp(hm.minHeight, hm.maxHeight, value / 65535f) - hm.originGround;
                }
            return new GeoHeightSource(source, hm);
        }

        // Export rows start at NORTH; Unity terrain rows start at SOUTH.
        public float Sample(float x, float z) => HeightField.Bilinear(source, (x - hm.minX) / hm.cellSize, (hm.maxZ - z) / hm.cellSize);
    }

    public static class GeoTerrain
    {
        public const int AsphaltLayer = 8, MarkingLayer = 9;
        public static readonly string[] LayerNames = { "Grass", "Forest floor", "Soil", "Sand & riverbank", "Gravel", "Paving", "Crops", "Rock", "Asphalt", "Road paint", "Urban ground", "Dirt road" };
        public static readonly Color[] LayerColors = {
            new Color(0.32f,0.43f,0.19f), new Color(0.20f,0.29f,0.13f), new Color(0.38f,0.27f,0.16f),
            new Color(0.63f,0.56f,0.38f), new Color(0.43f,0.43f,0.37f), new Color(0.45f,0.45f,0.41f),
            new Color(0.56f,0.48f,0.22f), new Color(0.39f,0.40f,0.37f),
            new Color(0.13f,0.15f,0.16f), new Color(0.88f,0.88f,0.84f),
            new Color(0.50f,0.48f,0.44f), new Color(0.55f,0.42f,0.27f) };

        public static int LayerIndex(string name)
        {
            switch (name)
            {
                case "forest": case "scrub": case "orchard": return 1;
                case "bare": return 2;
                case "dirt_road": case "path": return 11;
                case "sand": case "water": case "wetland": return 3;
                case "gravel": case "rail": return 4;
                case "paving": return 5;
                case "urban": case "building": return 10;
                case "asphalt": return AsphaltLayer;
                case "cropland": return 6;
                case "rock": case "snow": return 7;
                default: return 0;
            }
        }

        public static Texture2D Texture(string name, Color color, int seed, bool crops = false)
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGB24, true) { name = name, wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var pixels = new Color[size * size];
            var random = new System.Random(seed);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                float noise = (float)random.NextDouble() * 0.16f + Mathf.PerlinNoise(x * 0.12f + seed, y * 0.12f) * 0.18f;
                float stripe = crops ? Mathf.Sin(x * Mathf.PI / 8) * 0.08f : 0;
                pixels[y * size + x] = color * (0.83f + noise + stripe);
            }
            texture.SetPixels(pixels); texture.Apply(); return texture;
        }

        public static void Grade(HeightField field, List<PathBuild> paths, GenerationSettings settings)
        {
            int n = field.resolution;
            var sums = new float[n, n]; var weights = new float[n, n]; var influence = new float[n, n];
            foreach (var path in paths)
            {
                if (path.Elevated || path.river || !settings.gradeRoads) continue;
                float half = path.feature.width * 0.5f;
                float cellDiagonal = new Vector2(field.bounds.Width,field.bounds.Depth).magnitude / (n - 1);
                // Support the complete terrain triangles under the painted road, not
                // just vertices whose centres fall inside its width. Otherwise outside
                // hillside vertices pull the road edge into a repeated sawtooth profile.
                float bedHalf = half + cellDiagonal;
                float feather = Mathf.Max(settings.roadShoulder,cellDiagonal * 2);
                for (int i = 1; i < path.samples.Count; i++)
                {
                    Vector3 a = path.samples[i - 1], b = path.samples[i];
                    field.RasterSegment(a, b, bedHalf + feather, (x, z, d, t) => {
                        float w = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(bedHalf, bedHalf + feather, d));
                        sums[z, x] += Mathf.Lerp(a.y, b.y, t) * w;
                        weights[z, x] += w; influence[z, x] = Mathf.Max(influence[z, x], w);
                    });
                }
            }
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
                if (weights[z, x] > 0) field.heights[z, x] = Mathf.Lerp(field.heights[z, x], sums[z, x] / weights[z, x], influence[z, x]);
        }

        public static void Carve(HeightField field, List<PathBuild> paths, GenerationSettings settings)
        {
            if (!settings.carveRivers) return;
            // A minimum envelope makes overlapping river segments independent of traversal order.
            var result = (float[,])field.heights.Clone();
            foreach (var path in paths)
            {
                if (!path.river) continue;
                float half = path.feature.width * 0.5f;
                float feather = Mathf.Max(3, Mathf.Max(field.bounds.Width, field.bounds.Depth) / (field.resolution - 1) * 1.5f);
                for (int i = 1; i < path.samples.Count; i++)
                {
                    Vector3 a = path.samples[i - 1], b = path.samples[i];
                    field.RasterSegment(a, b, half + feather, (x, z, d, t) => {
                        float w = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(half * 0.9f, half + feather, d));
                        float bed = Mathf.Lerp(a.y, b.y, t) - settings.riverDepth;
                        result[z, x] = Mathf.Min(result[z, x], Mathf.Lerp(field.heights[z, x], bed, w));
                    });
                }
            }
            Array.Copy(result, field.heights, result.Length);
        }

        // fixedRange > 0: the height scale of a tiled world (base and range shared by all tiles, so their edges agree).
        public static TerrainData CreateData(HeightField field, GenerationSettings settings, out float baseY, float fixedBase = 0, float fixedRange = 0)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (float h in field.heights) { min = Mathf.Min(min, h); max = Mathf.Max(max, h); }
            baseY = min - 8;
            float range = Mathf.Max(32, max - baseY + 8);
            if (fixedRange > 0) { baseY = fixedBase; range = fixedRange; }
            var normalized = new float[field.resolution, field.resolution];
            for (int z = 0; z < field.resolution; z++) for (int x = 0; x < field.resolution; x++)
                normalized[z, x] = (field.heights[z, x] - baseY) / range;
            var data = new TerrainData { name = "Generated Terrain", heightmapResolution = field.resolution,
                alphamapResolution = Mathf.Max(settings.textureResolution, settings.roadPaintResolution),
                baseMapResolution = Mathf.Max(settings.textureResolution, settings.roadPaintResolution),
                size = new Vector3(field.bounds.Width, range, field.bounds.Depth) };
            data.SetHeights(0, 0, normalized); return data;
        }

        public static float[,,] Paint(EnvironmentData env, string environmentPath, List<PathBuild> paths, GenerationSettings settings, List<string> notes)
        {
            int n = settings.textureResolution;
            var weights = new float[n, n, LayerNames.Length];
            var b = env.bounds;
            Texture2D map = null; Color32[] pixels = null;
            var metadata = env.materials;
            var ids = new int[256];
            if (metadata != null && !string.IsNullOrEmpty(metadata.file))
            {
                if (metadata.width < 1 || metadata.height < 1 || (long)metadata.width * metadata.height > 17000000 || metadata.cellSize <= 0)
                    throw new InvalidDataException("Invalid materials.png metadata.");
                map = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                if (!map.LoadImage(File.ReadAllBytes(GeoData.PackageFile(environmentPath, metadata.file))))
                { UnityEngine.Object.DestroyImmediate(map); throw new InvalidDataException("Could not decode materials.png."); }
                if (map.width != metadata.width || map.height != metadata.height)
                { UnityEngine.Object.DestroyImmediate(map); throw new InvalidDataException("Material map dimensions do not match environment.json."); }
                pixels = map.GetPixels32();
                foreach (var item in metadata.classes ?? new List<MaterialClass>())
                    if (item.id >= 0 && item.id < 256) ids[item.id] = LayerIndex(item.name);
            }
            else notes.Add("No material raster: terrain layers painted from land polygons and paths.");
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
            {
                float wx = Mathf.Lerp(b.minX, b.maxX, (x + 0.5f) / n), wz = Mathf.Lerp(b.minZ, b.maxZ, (z + 0.5f) / n);
                int layer = 0;
                if (pixels != null)
                {
                    int col = Mathf.FloorToInt((wx - metadata.minX) / metadata.cellSize);
                    int rowNorth = Mathf.FloorToInt((metadata.maxZ - wz) / metadata.cellSize);
                    if (col >= 0 && col < metadata.width && rowNorth >= 0 && rowNorth < metadata.height)
                        layer = ids[pixels[(metadata.height - 1 - rowNorth) * metadata.width + col].r];
                }
                else
                {
                    foreach (var area in env.areas)
                        if (!string.IsNullOrEmpty(area.material) && GeoData.InPolygon(wx, wz, area.rings)) layer = LayerIndex(area.material);
                    foreach (var water in env.water) if (GeoData.InPolygon(wx, wz, water.rings)) layer = 3;
                }
                weights[z, x, layer] = 1;
            }
            if (map != null) UnityEngine.Object.DestroyImmediate(map);

            // Blend the landscape and shoulders first. GeoRoadPaint adds the actual road
            // surfaces and centre lines afterwards, at a separate detail resolution.
            foreach (var path in paths)
            {
                if (path.Elevated) continue;
                int layer = path.river ? 3 : path.feature.material == "asphalt" ? 4 : LayerIndex(path.feature.material);
                float radius = path.feature.width * 0.5f + (path.river ? 2 : settings.roadShoulder);
                for (int i = 1; i < path.samples.Count; i++)
                {
                    var a = path.samples[i - 1]; var c = path.samples[i];
                    int x0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.x,c.x)-radius-b.minX)/b.Width*n),0,n-1);
                    int x1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.x,c.x)+radius-b.minX)/b.Width*n),0,n-1);
                    int z0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.z,c.z)-radius-b.minZ)/b.Depth*n),0,n-1);
                    int z1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.z,c.z)+radius-b.minZ)/b.Depth*n),0,n-1);
                    for(int z=z0;z<=z1;z++) for(int x=x0;x<=x1;x++)
                    {
                        var p = new Vector2(Mathf.Lerp(b.minX,b.maxX,(x+0.5f)/n),Mathf.Lerp(b.minZ,b.maxZ,(z+0.5f)/n));
                        if (GeoData.SegmentDistance(p,new Vector2(a.x,a.z),new Vector2(c.x,c.z),out _) > radius) continue;
                        for(int l=0;l<LayerNames.Length;l++) weights[z,x,l] = l == layer ? 1 : 0;
                    }
                }
            }
            int radiusX = Mathf.Clamp(Mathf.RoundToInt(settings.blendMetres / b.Width * n), 0, 12);
            int radiusZ = Mathf.Clamp(Mathf.RoundToInt(settings.blendMetres / b.Depth * n), 0, 12);
            Blur(weights, radiusX, true); Blur(weights, radiusZ, false);
            return weights;
        }

        static void Blur(float[,,] data, int radius, bool horizontal)
        {
            if (radius == 0) return;
            int n = data.GetLength(0), layers = data.GetLength(2);
            var source = (float[,,])data.Clone();
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
            {
                float total = 0;
                for (int l = 0; l < layers; l++)
                {
                    float sum = 0, divisor = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        float w = radius + 1 - Mathf.Abs(k);
                        sum += source[horizontal ? z : Mathf.Clamp(z+k,0,n-1), horizontal ? Mathf.Clamp(x+k,0,n-1) : x,l] * w;
                        divisor += w;
                    }
                    data[z,x,l] = sum / divisor; total += data[z,x,l];
                }
                for (int l = 0; l < layers; l++) data[z,x,l] /= Mathf.Max(total, 0.00001f);
            }
        }
    }
}
