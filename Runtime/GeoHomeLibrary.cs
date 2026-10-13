using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // AllStanding: every building that still stands (intact, unknown and damaged); with ruin models for heavily damaged and
    // destroyed buildings a town reads as standing or destroyed.
    public enum GeoHomeUse { Off, IntactAndUnknown, AllStanding }

    // The home models the generator chooses from, and the roof materials their roofs take.
    [CreateAssetMenu(menuName = "Geo World/Home library")]
    public class GeoHomeLibrary : ScriptableObject
    {
        public const string DefaultPath = GeoPaths.Project + "/Homes/Home library.asset";
        // how far a model may be stretched to a footprint (and its walls to the building's eaves)
        public const float MinStretch = 0.72f, MaxStretch = 1.4f;
        // what a storey more or less than the building's floor count costs a model, in the same units as the log stretch
        public const float FloorCost = 0.2f;
        // How low a building may be for a model when its height is only estimated (no height tag): a storey counted as 3 m
        // makes a one-storey warehouse 3 m high. The model then keeps its lowest walls (Squash of its eaves), a little
        // taller than the estimate; a mapped height must fit within MinStretch.
        public const float MinEstimatedHeight = 0.5f, Squash = 0.75f;

        public static bool HeightIsEstimated(GeoBuilding building) => building.heightSource != "tag";
        public List<GeoHomeModelAsset> models = new List<GeoHomeModelAsset>();
        public GeoRoofMaterialSet roofMaterials;

        public GeoHomeModelAsset Find(string id) => models.Find(m => m && m.id == id);

        static bool Has(string[] list, string value)
        {
            if (list == null || string.IsNullOrEmpty(value)) return false;
            foreach (var item in list) if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The model that needs the least stretching for the building's footprint, or null when none fits (not a
        // rectangle, damaged beyond what the use allows, wrong kind, too different in size). The pick depends only on the
        // building's ID.
        public GeoHomeModelAsset Choose(GeoBuilding building, IList<Vector2> outline, GeoHomeUse use)
        {
            if (use == GeoHomeUse.Off || building == null || outline == null || outline.Count < 3) return null;
            var state = GeoBuildingDamage.StateOf(building);
            if (state >= (use == GeoHomeUse.AllStanding ? GeoConditionState.HeavilyDamaged : GeoConditionState.Damaged)) return null;
            var frame = GeoRuinFit.FrameOf(outline);
            // sheds are small (a woodshed is 2.4 m deep); the stretch limits keep a model off what is too small for it
            if (frame.fill < 0.8f || frame.length < 2 || frame.width < 1.6f) return null;
            float eaves = Mathf.Max(2, building.height) - (building.roof != GeoRoofShape.Flat ? Mathf.Clamp(building.roofHeight, 0, Mathf.Max(2, building.height) - 2) : 0);
            var candidates = new List<KeyValuePair<GeoHomeModelAsset, float>>();
            float best = float.MaxValue;
            foreach (var m in models)
            {
                if (!m || !m.body || !m.material || m.length < 1 || m.width < 1) continue;
                bool tag = Has(m.tags, building.buildingTag), category = Has(m.categories, building.category);
                if (m.tagOnly ? !tag : !(category || tag)) continue;
                float rl = frame.length / m.length, rw = frame.width / m.width, rh = eaves / Mathf.Max(0.5f, m.eaveHeight);
                float lowest = HeightIsEstimated(building) ? MinEstimatedHeight : MinStretch;
                if (rl < MinStretch || rl > MaxStretch || rw < MinStretch || rw > MaxStretch || rh < lowest || rh > MaxStretch + 0.1f) continue;
                // a two-storey office and a one-storey hall of the same height are not the same building
                float cost = Mathf.Abs(Mathf.Log(rl)) + Mathf.Abs(Mathf.Log(rw)) + Mathf.Abs(Mathf.Log(rh)) * 0.7f + FloorCost * Mathf.Abs(m.floors - building.floors);
                candidates.Add(new KeyValuePair<GeoHomeModelAsset, float>(m, cost));
                best = Mathf.Min(best, cost);
            }
            if (candidates.Count == 0) return null;
            var pool = candidates.FindAll(c => c.Value <= best + 0.14f);
            pool.Sort((a, b) => string.CompareOrdinal(a.Key.id, b.Key.id));
            uint seed = GeoBuildingDamage.Seed(building.sourceId ?? building.name);
            return pool[Mathf.Min(pool.Count - 1, Mathf.FloorToInt(GeoBuildingDamage.Hash01(seed, 83) * pool.Count))].Key;
        }
    }

    // Home models on buildings: choosing one, turning it to the street, fitting it and giving it the roof of the export.
    public static class GeoHomes
    {
        // Editor hook: a material for a roof kind tinted with a colour, saved as an asset so scenes can keep it.
        public static Func<Material, Color, Material> RoofMaterialProvider;

        public static bool Draws(GeoBuilding building) =>
            building != null && building.home != null && building.homeMode != GeoRuinMode.Procedural;

        public static void Assign(GeoBuilding building, GenerationSettings settings, Func<Vector2, Vector2?> street = null)
        {
            if (building == null || settings == null) return;
            if (building.homeMode == GeoRuinMode.Procedural) { building.home = null; return; }
            if (building.homeMode == GeoRuinMode.Model) return;
            building.home = null;
            building.homeLibrary = settings.homeLibrary;
            if (settings.homeLibrary && settings.homeModels != GeoHomeUse.Off)
            {
                List<Vector2> outline;
                try { outline = GeoPolygon.Clean(building.footprint); }
                catch (ArgumentException) { return; }
                building.home = settings.homeLibrary.Choose(building, outline, settings.homeModels);
            }
            uint seed = GeoBuildingDamage.Seed(building.sourceId ?? building.name);
            building.homeMirror = GeoBuildingDamage.Hash01(seed, 84) < 0.5f;
            building.homeTurn = GeoBuildingDamage.Hash01(seed, 85) < 0.5f;
            if (building.home && street != null) Face(building, street);
        }

        // The front (door, porch) towards the nearest street, when it is on one of the long sides.
        static void Face(GeoBuilding building, Func<Vector2, Vector2?> street)
        {
            List<Vector2> outline;
            try { outline = GeoPolygon.Clean(building.footprint); }
            catch (ArgumentException) { return; }
            var frame = GeoRuinFit.FrameOf(outline);
            var transform = building.transform;
            var centre = transform.TransformPoint(new Vector3(frame.centre.x, 0, frame.centre.y));
            var toward = street(new Vector2(centre.x, centre.z));
            if (toward == null) return;
            var front = transform.TransformDirection(new Vector3(frame.Across.x, 0, frame.Across.y));
            float side = Vector2.Dot(new Vector2(front.x, front.z).normalized, toward.Value);
            if (Mathf.Abs(side) > 0.3f) building.homeTurn = side < 0;
        }

        // ---------------------------------------------------------------- fitting

        sealed class Target
        {
            public readonly List<Vector3> vertices = new List<Vector3>(), normals = new List<Vector3>();
            public readonly List<Vector4> tangents = new List<Vector4>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> triangles = new List<int>();
            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name, indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetTangents(tangents); mesh.SetUVs(0, uvs); mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        public struct Placement
        {
            public Vector2 centre, axis, across;
            public float sx, sz, sy, sr, eaveTarget, eaveModel, depth;
            public bool turn, mirror;
        }

        // The total height a model is drawn to: the building's, unless its walls would be lower than the model can be
        // squashed to (an estimated height the chooser let through); then the walls stay at Squash of the model's eaves and
        // the roof sits on them, so it never cuts through the walls.
        public static float DrawnHeight(GeoHomeModelAsset model, float height, float roofRise, bool flatRoof)
        {
            float h = Mathf.Max(2, height);
            float rise = flatRoof ? 0 : Mathf.Clamp(roofRise, 0.4f, Mathf.Max(0.5f, h - 2));   // as Measure has it
            float lowest = model.eaveHeight * GeoHomeLibrary.Squash;
            if (h - rise >= lowest) return h;
            // the full rise on top of the lowest walls (Measure then keeps the rise, or at least 2 m of wall)
            return lowest + (flatRoof ? 0 : Mathf.Max(0.4f, roofRise));
        }

        // How a model sits on a building: stretch to the footprint's rectangle, walls to the eaves, the roof to its rise.
        public static Placement Measure(GeoHomeModelAsset model, IList<Vector2> outline, float height, float roofRise, bool flatRoof, bool turn, bool mirror, float depth)
        {
            var frame = GeoRuinFit.FrameOf(outline);
            float h = Mathf.Max(2, height);
            float rise = flatRoof ? 0 : Mathf.Clamp(roofRise, 0.4f, Mathf.Max(0.5f, h - 2));
            float eaves = h - rise;
            return new Placement
            {
                centre = frame.centre, axis = frame.axis, across = frame.Across,
                sx = frame.length / Mathf.Max(0.1f, model.length), sz = frame.width / Mathf.Max(0.1f, model.width),
                sy = Mathf.Clamp(eaves / Mathf.Max(0.5f, model.eaveHeight), 0.75f, 1.5f),
                sr = flatRoof ? 1 : Mathf.Clamp(rise / Mathf.Max(0.3f, model.rise), 0.5f, 1.7f),
                eaveTarget = eaves, eaveModel = model.eaveHeight, depth = Mathf.Max(0.1f, depth), turn = turn, mirror = mirror
            };
        }

        // part: 0 body (heights scale with the walls, the foot reaches down to the foundation), 1 roof (about the eave line)
        static void Append(Mesh source, Placement p, bool roofPart, Target atlas, Target roof)
        {
            if (!source) return;
            var vertices = source.vertices; var normals = source.normals; var tangents = source.tangents; var uvs = source.uv;
            float flip = (p.turn ? -1f : 1f), mirror = p.mirror ? -1f : 1f;
            bool reflect = p.mirror;
            for (int s = 0; s < Mathf.Min(2, source.subMeshCount); s++)
            {
                var target = s == 0 ? atlas : roof;
                var triangles = source.GetTriangles(s);
                if (triangles.Length == 0) continue;
                // vertices are shared between sub-meshes in the source: copy only what this sub-mesh uses
                var map = new Dictionary<int, int>();
                int First(int index)
                {
                    if (map.TryGetValue(index, out int found)) return found;
                    var v = vertices[index];
                    float u = v.x * p.sx * flip * mirror, w = v.z * p.sz * flip;
                    float y = roofPart ? p.eaveTarget + (v.y - p.eaveModel) * p.sr : (v.y < 0.05f ? -p.depth : v.y * p.sy);
                    var xz = p.centre + p.axis * u + p.across * w;
                    int at = target.vertices.Count;
                    target.vertices.Add(new Vector3(xz.x, y, xz.y));
                    var n = index < normals.Length ? normals[index] : Vector3.up;
                    var nl = new Vector3(n.x / p.sx, n.y / (roofPart ? p.sr : p.sy), n.z / p.sz);
                    float nu = nl.x * flip * mirror, nw = nl.z * flip;
                    var nxz = p.axis * nu + p.across * nw;
                    target.normals.Add(new Vector3(nxz.x, nl.y, nxz.y).normalized);
                    var t = index < tangents.Length ? tangents[index] : new Vector4(1, 0, 0, 1);
                    float tu = t.x * p.sx * flip * mirror, tw = t.z * p.sz * flip;
                    var txz = p.axis * tu + p.across * tw;
                    var td = new Vector3(txz.x, t.y * (roofPart ? p.sr : p.sy), txz.y).normalized;
                    target.tangents.Add(new Vector4(td.x, td.y, td.z, reflect ? -t.w : t.w));
                    target.uvs.Add(index < uvs.Length ? uvs[index] : Vector2.zero);
                    map[index] = at;
                    return at;
                }
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = First(triangles[i]), b = First(triangles[i + 1]), c = First(triangles[i + 2]);
                    if (reflect) { target.triangles.Add(a); target.triangles.Add(c); target.triangles.Add(b); }
                    else { target.triangles.Add(a); target.triangles.Add(b); target.triangles.Add(c); }
                }
            }
        }

        // The roof material of the export's roof block on this model (kind from roof.material, tint from roof.color).
        public static Material RoofMaterial(GeoHomeLibrary library, GeoHomeModelAsset model, GeoRoofInfo roof)
        {
            Material kind = null;
            if (library && library.roofMaterials)
            {
                if (roof != null && !string.IsNullOrEmpty(roof.material)) kind = library.roofMaterials.For(roof.material);
                if (!kind) kind = library.roofMaterials.For(model.defaultRoofMaterial);
            }
            if (!kind) return null;
            string hex = roof != null && !string.IsNullOrEmpty(roof.color) ? roof.color : model.defaultRoofColor;
            if (!ColorUtility.TryParseHtmlString(hex, out var color)) ColorUtility.TryParseHtmlString(model.defaultRoofColor, out color);
            return RoofMaterialProvider != null ? RoofMaterialProvider(kind, Quantize(color)) : kind;
        }

        // Roof colours are drawn from a small palette (8 levels per channel) so a city needs few materials.
        public static Color Quantize(Color c) => new Color(Mathf.Round(c.r * 7) / 7f, Mathf.Round(c.g * 7) / 7f, Mathf.Round(c.b * 7) / 7f, 1f);

        public static GeoBuildingOutput Build(GeoBuildingContext c, float foundationDepth)
        {
            var output = new GeoBuildingOutput();
            try
            {
                var building = c.building; var model = building.home; var roofInfo = building.roofInfo; var library = building.homeLibrary;
                string variant = GeoHomeModel.VariantFor(roofInfo?.shape, roofInfo?.renderAs, roofInfo?.ridge, model.defaultVariant);
                if (model.Variant(variant) == null) variant = model.defaultVariant;
                float roofRise = roofInfo != null && GeoRoofInfo.Has(roofInfo.rise) ? roofInfo.rise : building.roofHeight;
                float height = DrawnHeight(model, building.height, roofRise, variant == "flat");
                var p = Measure(model, c.outline, height, roofRise, variant == "flat", building.homeTurn, building.homeMirror, foundationDepth);
                var atlas = new Target(); var roof = new Target();
                // a merged city's bake asks for the reduced copies (GeoModelDetail): the same foot, eaves and corners
                Append(GeoModelDetail.For(model.body), p, false, atlas, roof);
                Append(GeoModelDetail.For(model.roofFixed), p, false, atlas, roof);
                Append(GeoModelDetail.For(model.Variant(variant)), p, true, atlas, roof);
                output.Add("Model", atlas.ToMesh("Home"), model.material, true);
                var roofMaterial = RoofMaterial(library, model, roofInfo);
                if (roofMaterial) output.Add("Model", roof.ToMesh("Home roof"), roofMaterial, false);
                else if (roof.vertices.Count > 0) output.Add("Model", roof.ToMesh("Home roof"), model.material, false);
                return output;
            }
            catch { output.Dispose(); throw; }
        }
    }
}
