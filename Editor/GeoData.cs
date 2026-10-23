using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace GeoWorld.Editor
{
    [Serializable] public class GeoBounds
    {
        public float minX, minZ, maxX, maxZ;
        public float Width => maxX - minX;
        public float Depth => maxZ - minZ;
        public bool Contains(float x, float z) => x >= minX && x <= maxX && z >= minZ && z <= maxZ;
    }
    [Serializable] public class GeoOrigin { public double lat, lon; }
    [Serializable] public class HeightmapInfo
    {
        public string file, encoding, rowOrder;
        public int width, height;
        public float cellSize, minX, maxZ, minHeight, maxHeight, originGround;
    }
    [Serializable] public class TerrainInfo { public HeightmapInfo heightmap; }
    [Serializable] public class MaterialClass { public int id; public string name, color; }
    [Serializable] public class MaterialMapInfo
    {
        public string file;
        public string canopyFile;
        public int width, height;
        public float cellSize, minX, maxZ;
        public List<MaterialClass> classes = new List<MaterialClass>();
    }
    [Serializable] public class LineFeature
    {
        public string id, name, type, kind, material, lit, sidewalk;
        public float width;
        public float wear; // 0 unknown, else 0 (new) to 1 (broken)
        public bool bridge, tunnel;
        public int layer;
        public float[] points;
    }
    [Serializable] public class AreaFeature
    {
        // type: the kind of a water area (OSM water=*, or riverbank): a river's own water follows the river (GeoWater)
        public string id, name, material, tag, type;
        public float[][] rings;
    }
    [Serializable] public class TreeFeature { public string id; public float x, z, height, crown; }
    [Serializable] public class PropFeature : GeoPhotoReference
    {
        public string id, type, placementSource, name, material, colour, rotationSource;
        public float x, z, height, width, rotation;
    }
    [Serializable] public class BarrierFeature
    {
        public string id, type, material, fenceType;
        public float height, width;
        public float[] points;
    }
    [Serializable] public class EnvironmentData
    {
        public string format;
        public int version;
        public GeoOrigin origin;
        public GeoBounds bounds;
        public TerrainInfo terrain;
        public MaterialMapInfo materials;
        public List<LineFeature> roads = new List<LineFeature>();
        public List<LineFeature> waterways = new List<LineFeature>();
        public List<AreaFeature> water = new List<AreaFeature>();
        public List<AreaFeature> areas = new List<AreaFeature>();
        public List<TreeFeature> trees = new List<TreeFeature>();
        public List<PropFeature> props = new List<PropFeature>();
        public List<BarrierFeature> barriers = new List<BarrierFeature>();
        public List<BarrierFeature> hedges = new List<BarrierFeature>();
        public List<BarrierFeature> treeRows = new List<BarrierFeature>();
        public List<LineFeature> railways = new List<LineFeature>();
        // Set for one tile of a tiled world, where bounds is the tile: props and trees belong to the tile, but they keep
        // clear of what stands around it (surround: the tile with its margin) and only the export's own edge (outer)
        // keeps them back. Null: bounds.
        [JsonIgnore] public GeoBounds surround, outer;
    }
    [Serializable] public class BuildingFeature
    {
        public string id, category, name, heightSource;
        public string buildingTag; // the OpenStreetMap building=* value ("yes" when it says nothing more)
        public int levels;
        public float x, z, y, width, length, rotation, height;
        public float[] footprint;
        public GeoBuildingVisualDetails visualDetails;
        public GeoMappedAppearance mappedAppearance;
        public GeoBuildingCondition condition;
        public GeoRoofInfo roof; // shape, ridge, rise, colour with their sources; absent in older exports
    }
    [Serializable] public class BuildingArea { public GeoBounds bounds; }
    [Serializable] public class BuildingsData
    {
        public string format;
        public int version;
        public GeoOrigin origin;
        public BuildingArea area;
        public List<BuildingFeature> buildings = new List<BuildingFeature>();
    }

    public static class GeoData
    {
        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        { MaxDepth = 64, TypeNameHandling = TypeNameHandling.None };

        public static EnvironmentData ReadEnvironment(string path)
        {
            var data = JsonConvert.DeserializeObject<EnvironmentData>(File.ReadAllText(path), JsonSettings);
            if (data == null || data.format != "geo-environment" || data.version != 1)
                throw new InvalidDataException("Select environment.json from a geo-environment version 1 export.");
            ValidateBounds(data.bounds);
            data.roads = data.roads ?? new List<LineFeature>();
            data.waterways = data.waterways ?? new List<LineFeature>();
            data.water = data.water ?? new List<AreaFeature>();
            data.areas = data.areas ?? new List<AreaFeature>();
            data.trees = data.trees ?? new List<TreeFeature>();
            data.props = data.props ?? new List<PropFeature>();
            data.barriers = data.barriers ?? new List<BarrierFeature>();
            data.hedges = data.hedges ?? new List<BarrierFeature>();
            data.treeRows = data.treeRows ?? new List<BarrierFeature>();
            data.railways = data.railways ?? new List<LineFeature>();
            return data;
        }

        public static BuildingsData ReadBuildings(string path, GeoOrigin origin)
        {
            if (string.IsNullOrEmpty(path)) return new BuildingsData();
            var data = JsonConvert.DeserializeObject<BuildingsData>(File.ReadAllText(path), JsonSettings);
            if (data == null || data.format != "geo-buildings" || data.version != 1)
                throw new InvalidDataException("Select buildings.json from a geo-buildings version 1 export.");
            if (origin != null && data.origin != null &&
                (Math.Abs(origin.lat - data.origin.lat) > 0.000001 || Math.Abs(origin.lon - data.origin.lon) > 0.000001))
                throw new InvalidDataException("Buildings and environment have different origins. Export both from the same area with the same fixed origin.");
            data.buildings = data.buildings ?? new List<BuildingFeature>();
            return data;
        }

        public static string PackageFile(string jsonPath, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("Package file name is missing.");
            string directory = Path.GetFullPath(Path.GetDirectoryName(jsonPath));
            string path = Path.GetFullPath(Path.Combine(directory, relative));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package files must be inside the export folder.");
            if (!File.Exists(path)) throw new FileNotFoundException("Missing export file: " + relative, path);
            return path;
        }

        public static void ValidateBounds(GeoBounds b)
        {
            if (b == null || !Finite(b.minX) || !Finite(b.maxX) || !Finite(b.minZ) || !Finite(b.maxZ) ||
                b.Width <= 0 || b.Depth <= 0 || b.Width > 20000 || b.Depth > 20000)
                throw new InvalidDataException("This sample supports areas with positive dimensions up to 20 km per side.");
        }

        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static List<Vector3> Points(float[] flat)
        {
            var result = new List<Vector3>();
            if (flat == null || flat.Length % 2 != 0) return result;
            for (int i = 0; i < flat.Length; i += 2)
            {
                if (!Finite(flat[i]) || !Finite(flat[i + 1])) return new List<Vector3>();
                var point = new Vector3(flat[i], 0, flat[i + 1]);
                if (result.Count == 0 || (point - result[result.Count - 1]).sqrMagnitude > 0.0001f) result.Add(point);
            }
            return result;
        }

        public static List<Vector3> Resample(List<Vector3> points, float step)
        {
            var result = new List<Vector3>();
            if (points.Count == 0) return result;
            step = Mathf.Max(0.5f, step);
            result.Add(points[0]);
            for (int i = 1; i < points.Count; i++)
            {
                int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(points[i - 1], points[i]) / step));
                if (count + result.Count > 100000) throw new InvalidDataException("A path exceeds 100,000 samples. Use a larger path step or smaller export.");
                for (int j = 1; j <= count; j++) result.Add(Vector3.Lerp(points[i - 1], points[i], j / (float)count));
            }
            return result;
        }

        public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b, out float t)
        {
            Vector2 d = b - a;
            t = d.sqrMagnitude < 0.000001f ? 0 : Mathf.Clamp01(Vector2.Dot(p - a, d) / d.sqrMagnitude);
            return Vector2.Distance(p, a + d * t);
        }

        public static bool InPolygon(float x, float z, float[][] rings)
        {
            if (rings == null) return false;
            bool inside = false;
            foreach (var ring in rings)
            {
                if (ring == null || ring.Length < 6 || ring.Length % 2 != 0) continue;
                bool inRing = false;
                for (int i = 0, j = ring.Length - 2; i < ring.Length; j = i, i += 2)
                {
                    if ((ring[i + 1] > z) != (ring[j + 1] > z) &&
                        x < (ring[j] - ring[i]) * (z - ring[i + 1]) / (ring[j + 1] - ring[i + 1]) + ring[i])
                        inRing = !inRing;
                }
                if (inRing) inside = !inside;
            }
            return inside;
        }

        public static GroundSurface Surface(string material)
        {
            switch (material)
            {
                case "gravel": return GroundSurface.Gravel;
                case "dirt_road": case "path": case "bare": return GroundSurface.Dirt;
                case "paving": return GroundSurface.Paving;
                default: return GroundSurface.Asphalt;
            }
        }

        public static string Material(GroundSurface surface) =>
            new[] { "asphalt", "gravel", "dirt_road", "paving" }[(int)surface];
    }
}
