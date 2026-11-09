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
        // Whether a building centred here is built by these bounds: one standing on the line between two tiles is the
        // upper tile's (the lower edges are in, the upper ones out), not built by both.
        public bool Owns(float x, float z) => x >= minX && x < maxX && z >= minZ && z < maxZ;
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
        // OpenStreetMap leaf_type of a wood (broadleaved, needleleaved, mixed) and the bearing of a field's long side (degrees)
        public string leafType;
        public float rotation;
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
    // A war crater (GeoCombat): its centre, the pit's diameter and depth below the ground around it, how worn it is (0 fresh,
    // 1 weathered: rim worn down, bowl partly filled and grown over), water standing in it, where it comes from
    // (GeoCraterSource) and how sure that is.
    [Serializable] public class CraterFeature
    {
        public float x, z, diameter, depth, age, confidence = 1;
        public int source;
        public bool water;
    }
    // A trench or ditch of the war (GeoCombat): its centreline (x, z pairs), its width and depth as dug, how worn it is (0 fresh:
    // steep walls, flat floor; 1 abandoned for years: walls slumped into a V, partly filled, grown over), its kind
    // (GeoTrenchKind) and where it comes from (GeoCraterSource).
    [Serializable] public class TrenchFeature
    {
        public float[] points;
        public float width = 1, depth = 1.6f, age;
        public int kind, source;
    }
    // A data source the export used (its credit): "terrain", "osm" when OpenStreetMap's roads, water and land use loaded, ...
    [Serializable] public class SourceFeature { public string name, attribution; }
    [Serializable] public class EnvironmentData
    {
        public string format;
        public int version;
        public List<SourceFeature> sources = new List<SourceFeature>();
        // Whether OpenStreetMap's map data (roads, water, land use, trees) loaded. The extractor goes on without it (a warning)
        // and the world then has no roads at all; an export that names no source at all is an old one, taken as loaded.
        [JsonIgnore] public bool OsmLoaded => sources == null || sources.Count == 0 || sources.Exists(s => s != null && s.name == "osm");
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
        // War craters: the export's own, and the combat bake's next to it (combat.json, GeoCombatFile).
        public List<CraterFeature> craters = new List<CraterFeature>();
        // Trenches and ditches: the export's own, and the combat bake's (combat.json).
        public List<TrenchFeature> trenches = new List<TrenchFeature>();
        // Set for one tile of a tiled world, where bounds is the tile: props and trees belong to the tile, but they keep
        // clear of what stands around it (surround: the tile with its margin) and only the export's own edge (outer)
        // keeps them back. Null: bounds.
        [JsonIgnore] public GeoBounds surround, outer;
        // The export has a combat bake of its own (combat.json next to it): its settlements get the war's look where it
        // was, power lines along their streets everywhere (GeoPowerLines).
        [JsonIgnore] public bool battlefield;
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
        // where the outline comes from ("osm", "microsoft") and how its category was found ("tag", "guessed" from its shape);
        // absent in older exports
        public string source, categorySource;
        public BuildingFeature Clone() => (BuildingFeature)MemberwiseClone();
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

        // combat: with the craters of the combat bake next to the export (combat.json, GeoCombatFile); false: the export's own only.
        public static EnvironmentData ReadEnvironment(string path, bool combat = true)
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
            data.craters = data.craters ?? new List<CraterFeature>();
            data.trenches = data.trenches ?? new List<TrenchFeature>();
            // the export's own craters and trenches are given by it: without a source they would read as found on the imagery
            // (0), and a crater without a depth would not be carved
            foreach (var c in data.craters)
            {
                if (c == null) continue;
                if (c.source == GeoCraterSource.Detected) c.source = GeoCraterSource.Given;
                if (!(c.depth > 0) && c.diameter > 0) c.depth = Mathf.Min(6, c.diameter * (c.diameter >= 7 ? 0.33f : 0.27f));
            }
            foreach (var t in data.trenches)
                if (t != null && t.source == GeoCraterSource.Detected) t.source = GeoCraterSource.Given;
            if (combat) GeoCombatFile.AddTo(data, path);
            return data;
        }

        // combat: with the combat bake's estimated states for buildings the app has no evidence for (combat.json next to the
        // export, GeoBuildingEstimates); false: the export's own conditions only.
        public static BuildingsData ReadBuildings(string path, GeoOrigin origin, bool combat = true)
        {
            if (string.IsNullOrEmpty(path)) return new BuildingsData();
            var data = JsonConvert.DeserializeObject<BuildingsData>(File.ReadAllText(path), JsonSettings);
            if (data == null || data.format != "geo-buildings" || data.version != 1)
                throw new InvalidDataException("Select buildings.json from a geo-buildings version 1 export.");
            if (origin != null && data.origin != null &&
                (Math.Abs(origin.lat - data.origin.lat) > 0.000001 || Math.Abs(origin.lon - data.origin.lon) > 0.000001))
                throw new InvalidDataException("Buildings and environment have different origins. Export both from the same area with the same fixed origin.");
            data.buildings = data.buildings ?? new List<BuildingFeature>();
            if (combat) GeoBuildingEstimates.ApplyTo(data, path);
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
