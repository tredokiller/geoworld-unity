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
        public const string DefaultPath = GeoPaths.Package + "/Homes/Home library.asset";
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

        // How far a model may be stretched when it is the last way to draw a standing building with a model (Cover): no
        // model of the kind fits within MinStretch..MaxStretch, nor as a row of sections.
        public const float LooseMinStretch = 0.6f, LooseMaxStretch = 1.72f;
        // How much a model's proportions may change: its length and its width are stretched apart, and 1.4 one way with 0.72
        // the other drew a house almost twice as long for its width (log of the ratio; 0.25 is 1.28 times, 0.4 1.5 times).
        public const float MaxDistortion = 0.25f, LooseMaxDistortion = 0.4f;
        // What a model turned a quarter (its front to an end of the footprint) costs, so the authored way round is the rule.
        public const float QuarterCost = 0.15f;
        // What a plan unlike the footprint costs per share of their union that is not shared (1 - intersection / union).
        public const float ShapeCost = 1f;
        // The share of a model's plan that may lie outside its footprint (a box model on a footprint that fills 0.8 of its
        // rectangle), and the area in square metres it may reach into another building's outline.
        public const float MaxOutside = 0.2f, MaxIntoNeighbour = 0.3f;
        // What the front costs turned away from the side it should face: a street along the long side, else the south
        // (the yard side of a house with its gable to the street, where the porch and the windows of a hata look).
        public const float StreetFacing = 0.3f, SunFacing = 0.12f;

        // The model that fits the building's footprint best, or null when none fits (not a rectangle, damaged beyond what
        // the use allows, wrong kind, too different in size). The pick depends only on the building and its neighbours.
        public GeoHomeModelAsset Choose(GeoBuilding building, IList<Vector2> outline, GeoHomeUse use) => ChooseFit(building, outline, use, null).model;

        // The same with the way round: a model's plan (GeoHomePlan) is stretched onto the footprint's rectangle in each of
        // eight ways (its front to either long side or, turned a quarter, to either end, and mirrored or not), and the way
        // that fits the outline best, keeps out of the neighbours' outlines (site) and faces the street wins.
        public GeoHomeFit ChooseFit(GeoBuilding building, IList<Vector2> outline, GeoHomeUse use, GeoHomeSite site)
        {
            if (!Covers(building, outline, use)) return default;
            var frame = GeoRuinFit.FrameOf(outline);
            if (frame.fill < 0.8f) return default;
            return Pick(building, frame, outline, false, false, null, Surroundings.Of(building, frame, site));
        }

        // A standing building no single model fits, still drawn with models: its rectangle as a row of two to four
        // sections of one model (semi-detached houses, the sections of a block of flats, a row of garages; every other one
        // mirrored), or one model stretched further (LooseMinStretch..LooseMaxStretch); an L, T or U footprint as its
        // rectangles (GeoRuins.Parts), each covered the same way and with the first one's model where that fits it about as
        // well. A model of the building's kind first, then of a related one (GeoRuinLibrary.RelatedTo). Null when some part
        // is left without a model (the building keeps its procedural walls). On Bakhmut's centre this draws 4 / 5 of the
        // standing buildings the single models leave out.
        public List<GeoHomePart> Cover(GeoBuilding building, IList<Vector2> outline, GeoHomeUse use) => Cover(building, outline, use, null);

        public List<GeoHomePart> Cover(GeoBuilding building, IList<Vector2> outline, GeoHomeUse use, GeoHomeSite site)
        {
            if (!Covers(building, outline, use)) return null;
            var frame = GeoRuinFit.FrameOf(outline);
            bool whole = frame.fill >= 0.8f;
            var rectangles = whole ? new List<GeoRuinFit.Frame> { frame } : GeoRuins.Parts(outline, frame, 0.85f);
            if (rectangles == null) return null;
            var around = Surroundings.Of(building, frame, site);
            var parts = new List<GeoHomePart>();
            GeoHomeModelAsset first = null;
            foreach (var rectangle in rectangles)
            {
                // a part is a rectangle inside the outline: its model is fitted to that rectangle
                var cover = CoverRectangle(building, rectangle, whole ? outline : Corners(rectangle), first, around);
                if (cover == null) return null;
                if (!first) first = cover[0].model;
                parts.AddRange(cover);
            }
            return parts;
        }

        List<GeoHomePart> CoverRectangle(GeoBuilding building, GeoRuinFit.Frame frame, IList<Vector2> target, GeoHomeModelAsset prefer, Surroundings around)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                bool related = pass == 1;
                var whole = Pick(building, frame, target, false, related, prefer, around);
                if (whole.model) return new List<GeoHomePart> { Part(frame, whole, false) };
                for (int k = 2; k <= 4; k++)
                {
                    var first = Section(frame, k, 0);
                    var fit = Pick(building, first, Corners(first), false, related, prefer, around);
                    if (!fit.model) continue;
                    // every other section mirrored (semi-detached houses, the sections of a block), each fitted to its own
                    // rectangle and kept out of the neighbours like the first
                    var plan = GeoHomePlan.Of(fit.model, GeoHomes.VariantOf(building, fit.model));
                    var row = new List<GeoHomePart>();
                    for (int i = 0; i < k && row != null; i++)
                    {
                        var part = Part(Section(frame, k, i), fit, (i & 1) == 1);
                        if (i > 0 && around.Blocked(GeoHomes.Boxes(plan, GeoHomes.Layout(plan, part.frame, part.quarter, part.turn, part.mirror)))) row = null;
                        else row.Add(part);
                    }
                    if (row != null) return row;
                }
                var stretched = Pick(building, frame, target, true, related, prefer, around);
                if (stretched.model) return new List<GeoHomePart> { Part(frame, stretched, false) };
            }
            return null;
        }

        static GeoHomePart Part(GeoRuinFit.Frame frame, GeoHomeFit fit, bool mirrored) =>
            new GeoHomePart { frame = frame, model = fit.model, turn = fit.turn, quarter = fit.quarter, mirror = fit.mirror ^ mirrored };

        // The corners of a rectangle, as an outline.
        static Vector2[] Corners(GeoRuinFit.Frame f)
        {
            Vector2 a = f.axis * (f.length * 0.5f), b = f.Across * (f.width * 0.5f);
            return new[] { f.centre - a - b, f.centre + a - b, f.centre + a + b, f.centre - a + b };
        }

        // The i-th of k equal sections of a rectangle along its axis, with its own longer side as its axis.
        public static GeoRuinFit.Frame Section(GeoRuinFit.Frame frame, int k, int i)
        {
            var section = frame;
            section.length = frame.length / k;
            section.centre = frame.centre + frame.axis * (frame.length * ((i + 0.5f) / k - 0.5f));
            section.fill = 1;
            if (section.length < section.width)
            {
                section.axis = frame.Across; section.length = frame.width; section.width = frame.length / k;
                // one direction for a given axis, as GeoRuinFit.FrameOf has it
                if (section.axis.x < -1e-6f || (Mathf.Abs(section.axis.x) <= 1e-6f && section.axis.y < 0)) section.axis = -section.axis;
            }
            return section;
        }

        static bool Outbuilding(string category) => category == "shed" || category == "garage";
        static bool Dwelling(GeoHomeModelAsset m) => Has(m.categories, "house") && !Has(m.categories, "shed") && !Has(m.categories, "garage");

        static bool Covers(GeoBuilding building, IList<Vector2> outline, GeoHomeUse use)
        {
            if (use == GeoHomeUse.Off || building == null || outline == null || outline.Count < 3) return false;
            var state = GeoBuildingDamage.StateOf(building);
            return state < (use == GeoHomeUse.AllStanding ? GeoConditionState.HeavilyDamaged : GeoConditionState.Damaged);
        }

        // The model that fits one rectangle (the footprint's or a part of it) best, and which way round: its plan stretched
        // within MinStretch..MaxStretch or, loose, LooseMinStretch..LooseMaxStretch, its proportions kept within
        // MaxDistortion, of the building's kind (category or OSM tag) or, related, of a kind that may stand in for it.
        // target is the outline the plan should cover (the footprint, or the rectangle of a part). prefer is taken when it
        // is among the best (the parts of one building alike); otherwise the pick depends only on the building's ID.
        GeoHomeFit Pick(GeoBuilding building, GeoRuinFit.Frame frame, IList<Vector2> target, bool loose, bool related, GeoHomeModelAsset prefer, Surroundings around)
        {
            // sheds are small (a woodshed is 2.4 m deep); the stretch limits keep a model off what is too small for it
            if (frame.length < 2 || frame.width < 1.6f) return default;
            float min = loose ? LooseMinStretch : MinStretch, max = loose ? LooseMaxStretch : MaxStretch;
            float distortion = loose ? LooseMaxDistortion : MaxDistortion;
            float eaves = Mathf.Max(2, building.height) - (building.roof != GeoRoofShape.Flat ? Mathf.Clamp(building.roofHeight, 0, Mathf.Max(2, building.height) - 2) : 0);
            float lowest = HeightIsEstimated(building) ? Mathf.Min(MinEstimatedHeight, min) : min;
            float targetArea = Mathf.Abs(GeoPolygon.Area(target));
            var kinds = related ? GeoRuinLibrary.RelatedTo(building.category) : null;
            uint seed = GeoBuildingDamage.Seed(building.sourceId ?? building.name);
            var fits = new List<GeoHomeFit>();
            float best = float.MaxValue;
            foreach (var m in models)
            {
                if (!m || !m.body || !m.material || m.length < 1 || m.width < 1) continue;
                bool tag = Has(m.tags, building.buildingTag), category = Has(m.categories, building.category);
                bool own = m.tagOnly ? tag : category || tag;
                if (related)
                {
                    // a stand-in: not of the building's own kind (those had their turn) and never a model kept for its tag
                    if (own || m.tagOnly) continue;
                    // nor a house with its porch to the street for a shed or a garage (GeoRuinLibrary.RelatedTo has houses)
                    if (Outbuilding(building.category) && Dwelling(m)) continue;
                    bool near = false;
                    foreach (var kind in kinds) near |= Has(m.categories, kind);
                    if (!near) continue;
                }
                else if (!own) continue;
                float rh = eaves / Mathf.Max(0.5f, m.eaveHeight);
                if (rh < lowest || rh > max + 0.1f) continue;
                var plan = GeoHomePlan.Of(m, GeoHomes.VariantOf(building, m));
                var fit = new GeoHomeFit { cost = float.MaxValue };
                for (int q = 0; q < 2; q++)
                {
                    bool quarter = q == 1;
                    var f = quarter ? GeoHomes.Quarter(frame) : frame;
                    float rl = f.length / Mathf.Max(0.1f, plan.Size.x), rw = f.width / Mathf.Max(0.1f, plan.Size.y);
                    if (rl < min || rl > max || rw < min || rw > max || Mathf.Abs(Mathf.Log(rl / rw)) > distortion) continue;
                    // a two-storey office and a one-storey hall of the same height are not the same building
                    float stretch = Mathf.Abs(Mathf.Log(rl)) + Mathf.Abs(Mathf.Log(rw)) + Mathf.Abs(Mathf.Log(rh)) * 0.7f + FloorCost * Mathf.Abs(m.floors - building.floors)
                                    + (quarter ? QuarterCost : 0);
                    if (stretch >= fit.cost) continue;
                    for (int way = 0; way < 4; way++)
                    {
                        bool turn = (way & 1) == 1, mirror = (way & 2) == 2;
                        var boxes = GeoHomes.Boxes(plan, GeoHomes.Layout(plan, frame, quarter, turn, mirror));
                        float planArea = 0, inside = 0;
                        foreach (var box in boxes) { planArea += box.Area; inside += GeoPolygon.AreaInside(target, box, around.a, around.b); }
                        if (planArea - inside > MaxOutside * planArea || around.Blocked(boxes)) continue;
                        float shape = 1 - inside / Mathf.Max(1e-3f, targetArea + planArea - inside);
                        // ties (a symmetric plan, no street) are broken by the building's ID, so a street's houses differ
                        float cost = stretch + ShapeCost * shape + around.Facing(GeoHomes.Front(frame, quarter, turn)) + 0.01f * GeoBuildingDamage.Hash01(seed, 86 + way + 4 * q);
                        if (cost < fit.cost) fit = new GeoHomeFit { model = m, cost = cost, quarter = quarter, turn = turn, mirror = mirror };
                    }
                }
                if (!fit.model) continue;
                fits.Add(fit);
                best = Mathf.Min(best, fit.cost);
            }
            if (fits.Count == 0) return default;
            var pool = fits.FindAll(c => c.cost <= best + 0.14f);
            if (prefer)
            {
                int preferred = pool.FindIndex(c => c.model == prefer);
                if (preferred >= 0) return pool[preferred];
            }
            pool.Sort((a, b) => string.CompareOrdinal(a.model.id, b.model.id));
            return pool[Mathf.Min(pool.Count - 1, Mathf.FloorToInt(GeoBuildingDamage.Hash01(seed, 83) * pool.Count))];
        }

        // A building's neighbourhood in its own local space, gathered once for all the ways its models are tried: the
        // outlines of the other buildings around it and the direction its front should face.
        internal sealed class Surroundings
        {
            public readonly List<Vector2[]> neighbours = new List<Vector2[]>();
            public Vector2 front;
            public float facing;
            // scratch lists for clipping
            public readonly List<Vector2> a = new List<Vector2>(), b = new List<Vector2>();

            static readonly List<Vector2[]> found = new List<Vector2[]>();

            public static Surroundings Of(GeoBuilding building, GeoRuinFit.Frame frame, GeoHomeSite site)
            {
                var s = new Surroundings();
                if (building == null) return s;
                var transform = building.transform;
                Vector2 World(Vector2 p) { var w = transform.TransformPoint(new Vector3(p.x, 0, p.y)); return new Vector2(w.x, w.z); }
                Vector2 Local(Vector2 p) { var l = transform.InverseTransformPoint(new Vector3(p.x, transform.position.y, p.y)); return new Vector2(l.x, l.z); }
                Vector2 LocalDirection(Vector2 d) { var l = transform.InverseTransformDirection(new Vector3(d.x, 0, d.y)); return new Vector2(l.x, l.z).normalized; }
                if (site != null && site.Count > 0)
                {
                    // a model fills the footprint's rectangle, so what it can reach is that rectangle (and a metre for safety)
                    float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                    Vector2 a = frame.axis * (frame.length * 0.5f + 1), b = frame.Across * (frame.width * 0.5f + 1);
                    foreach (var corner in new[] { frame.centre - a - b, frame.centre + a - b, frame.centre + a + b, frame.centre - a + b })
                    {
                        var w = World(corner);
                        minX = Mathf.Min(minX, w.x); maxX = Mathf.Max(maxX, w.x); minZ = Mathf.Min(minZ, w.y); maxZ = Mathf.Max(maxZ, w.y);
                    }
                    site.Near(Rect.MinMaxRect(minX, minZ, maxX, maxZ), building.sourceId, found);
                    foreach (var outline in found)
                    {
                        var local = new Vector2[outline.Length];
                        for (int i = 0; i < outline.Length; i++) local[i] = Local(outline[i]);
                        s.neighbours.Add(local);
                    }
                }
                // the street along a long side, else the south: a house with its gable to the street has its porch towards
                // the yard, and an old hata looks south whatever the street does
                s.front = LocalDirection(new Vector2(0, -1)); s.facing = SunFacing;
                var toward = site?.street?.Invoke(World(frame.centre));
                if (toward != null)
                {
                    var local = LocalDirection(toward.Value);
                    if (Mathf.Abs(Vector2.Dot(local, frame.Across)) >= 0.5f) { s.front = local; s.facing = StreetFacing; }
                }
                return s;
            }

            // Whether boxes in the building's local space reach into another building's outline.
            public bool Blocked(List<GeoPolygon.Box> boxes)
            {
                if (neighbours.Count == 0) return false;
                foreach (var outline in neighbours)
                {
                    float into = 0;
                    foreach (var box in boxes) into += GeoPolygon.AreaInside(outline, box, a, b);
                    if (into > MaxIntoNeighbour) return true;
                }
                return false;
            }

            // What a front direction (local, unit) costs against the one it should face.
            public float Facing(Vector2 direction) => facing * (1 - Vector2.Dot(direction, front)) * 0.5f;
        }
    }

    // One way of drawing a rectangle of a building with a model: the model, which way round it goes and what that costs.
    public struct GeoHomeFit
    {
        public GeoHomeModelAsset model;
        // turn: the front to the other side; mirror: along its length; quarter: turned a quarter, the front to an end
        public bool turn, mirror, quarter;
        public float cost;
    }

    // One home model on a rectangle of a building (GeoHomeLibrary.Cover), in the building's local space.
    public struct GeoHomePart
    {
        public GeoRuinFit.Frame frame;
        public GeoHomeModelAsset model;
        public bool turn, mirror, quarter;
    }

    // Home models on buildings: choosing one, turning it to the street, fitting it and giving it the roof of the export.
    public static class GeoHomes
    {
        // Editor hook: a material for a roof kind tinted with a colour, saved as an asset so scenes can keep it.
        public static Func<Material, Color, Material> RoofMaterialProvider;

        public static bool Draws(GeoBuilding building) =>
            building != null && building.home != null && building.homeMode != GeoRuinMode.Procedural;

        public static void Assign(GeoBuilding building, GenerationSettings settings, Func<Vector2, Vector2?> street = null) =>
            Assign(building, settings, street != null ? new GeoHomeSite { street = street } : null);

        // Gives a building the home model the settings call for, the way round that fits its outline, keeps out of its
        // neighbours' outlines and faces the street (site; GeoHomeLibrary.ChooseFit). Model and Procedural are the user's
        // choice and stay as they are.
        public static void Assign(GeoBuilding building, GenerationSettings settings, GeoHomeSite site)
        {
            if (building == null || settings == null) return;
            building.homeParts = null;
            if (building.homeMode == GeoRuinMode.Procedural) { building.home = null; return; }
            if (building.homeMode == GeoRuinMode.Model) return;
            building.home = null;
            building.homeLibrary = settings.homeLibrary;
            if (!settings.homeLibrary || settings.homeModels == GeoHomeUse.Off) return;
            List<Vector2> outline;
            try { outline = GeoPolygon.Clean(building.footprint); }
            catch (ArgumentException) { return; }
            var fit = settings.homeLibrary.ChooseFit(building, outline, settings.homeModels, site);
            if (fit.model)
            {
                building.home = fit.model;
                building.homeTurn = fit.turn; building.homeMirror = fit.mirror; building.homeQuarter = fit.quarter;
                return;
            }
            // no single model fits: a row of sections, a further stretch, or a model on each rectangle of an L, T or U
            // (not where a ruin model already draws the building)
            if (GeoRuins.Draws(building)) return;
            var parts = settings.homeLibrary.Cover(building, outline, settings.homeModels, site);
            if (parts == null || parts.Count == 0) return;
            building.homeParts = parts; building.home = parts[0].model;
            building.homeTurn = parts[0].turn; building.homeMirror = parts[0].mirror; building.homeQuarter = parts[0].quarter;
        }

        // The roof variant a building gives a model (the export's roof shape, else the model's default).
        public static string VariantOf(GeoBuilding building, GeoHomeModelAsset model)
        {
            var roofInfo = building != null ? building.roofInfo : null;
            string name = GeoHomeModel.VariantFor(roofInfo?.shape, roofInfo?.renderAs, roofInfo?.ridge, model.defaultVariant);
            return model.Variant(name) != null ? name : model.defaultVariant;
        }

        // A rectangle turned a quarter: its axis along the old one's width, so a model laid on it has its front to an end.
        public static GeoRuinFit.Frame Quarter(GeoRuinFit.Frame frame) =>
            new GeoRuinFit.Frame { centre = frame.centre, axis = frame.Across, length = frame.width, width = frame.length, fill = frame.fill };

        // Where a model's front (its +Z) looks, in the building's local space.
        public static Vector2 Front(GeoRuinFit.Frame frame, bool quarter, bool turn)
        {
            var across = quarter ? -frame.axis : frame.Across;
            return turn ? -across : across;
        }

        // How a model's plan lies on a rectangle of its building: stretched so the whole plan (walls, porches, wings, eaves)
        // covers the rectangle, which way round. Only the horizontal part of a Placement; Measure adds the heights.
        public static Placement Layout(GeoHomePlan plan, GeoRuinFit.Frame frame, bool quarter, bool turn, bool mirror)
        {
            var f = quarter ? Quarter(frame) : frame;
            var size = plan != null ? plan.Size : Vector2.one;
            var middle = plan != null ? plan.bounds.center : Vector2.zero;
            float sx = f.length / Mathf.Max(0.1f, size.x), sz = f.width / Mathf.Max(0.1f, size.y);
            float flip = turn ? -1f : 1f, flop = mirror ? -1f : 1f;
            return new Placement
            {
                // the plan's middle on the rectangle's centre
                centre = f.centre - f.axis * (middle.x * sx * flip * flop) - f.Across * (middle.y * sz * flip),
                axis = f.axis, across = f.Across, sx = sx, sz = sz, turn = turn, mirror = mirror
            };
        }

        // A plan's rectangles where a placement puts them, in the building's local space.
        public static List<GeoPolygon.Box> Boxes(GeoHomePlan plan, Placement p)
        {
            var boxes = new List<GeoPolygon.Box>(plan != null && plan.rects != null ? plan.rects.Length : 0);
            if (plan == null || plan.rects == null) return boxes;
            float flip = p.turn ? -1f : 1f, flop = p.mirror ? -1f : 1f;
            foreach (var r in plan.rects)
            {
                var c = r.center;
                var centre = p.centre + p.axis * (c.x * p.sx * flip * flop) + p.across * (c.y * p.sz * flip);
                boxes.Add(new GeoPolygon.Box(centre, p.axis, r.width * 0.5f * p.sx, r.height * 0.5f * p.sz));
            }
            return boxes;
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

        // How a model sits on a rectangle of a building (the footprint's, or a part of GeoHomeLibrary.Cover): its plan with
        // the roof variant stretched over the rectangle (Layout), walls to the eaves, the roof to its rise.
        public static Placement Measure(GeoHomeModelAsset model, string variant, GeoRuinFit.Frame frame, bool quarter, float height, float roofRise, bool flatRoof, bool turn, bool mirror, float depth)
        {
            float h = Mathf.Max(2, height);
            float rise = flatRoof ? 0 : Mathf.Clamp(roofRise, 0.4f, Mathf.Max(0.5f, h - 2));
            float eaves = h - rise;
            var p = Layout(GeoHomePlan.Of(model, variant), frame, quarter, turn, mirror);
            p.sy = Mathf.Clamp(eaves / Mathf.Max(0.5f, model.eaveHeight), 0.75f, 1.5f);
            p.sr = flatRoof ? 1 : Mathf.Clamp(rise / Mathf.Max(0.3f, model.rise), 0.5f, 1.7f);
            p.eaveTarget = eaves; p.eaveModel = model.eaveHeight; p.depth = Mathf.Max(0.1f, depth);
            return p;
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
                var building = c.building;
                var parts = building.homeParts;
                if (parts != null && parts.Count > 0)
                    foreach (var part in parts) Add(output, building, part.model, part.frame, part.quarter, part.turn, part.mirror, foundationDepth);
                else Add(output, building, building.home, GeoRuinFit.FrameOf(c.outline), building.homeQuarter, building.homeTurn, building.homeMirror, foundationDepth);
                return output;
            }
            catch { output.Dispose(); throw; }
        }

        // one model fitted to one rectangle of the building, with the roof of the export
        static void Add(GeoBuildingOutput output, GeoBuilding building, GeoHomeModelAsset model, GeoRuinFit.Frame frame, bool quarter, bool turn, bool mirror, float foundationDepth)
        {
            var roofInfo = building.roofInfo; var library = building.homeLibrary;
            string variant = VariantOf(building, model);
            float roofRise = roofInfo != null && GeoRoofInfo.Has(roofInfo.rise) ? roofInfo.rise : building.roofHeight;
            float height = DrawnHeight(model, building.height, roofRise, variant == "flat");
            var p = Measure(model, variant, frame, quarter, height, roofRise, variant == "flat", turn, mirror, foundationDepth);
            var atlas = new Target(); var roof = new Target();
            // a merged city's bake asks for the reduced copies (GeoModelDetail): the same foot, eaves and corners
            Append(GeoModelDetail.For(model.body), p, false, atlas, roof);
            Append(GeoModelDetail.For(model.roofFixed), p, false, atlas, roof);
            Append(GeoModelDetail.For(model.Variant(variant)), p, true, atlas, roof);
            output.Add("Model", atlas.ToMesh("Home"), model.material, true);
            var roofMaterial = RoofMaterial(library, model, roofInfo);
            if (roofMaterial) output.Add("Model", roof.ToMesh("Home roof"), roofMaterial, false);
            else if (roof.vertices.Count > 0) output.Add("Model", roof.ToMesh("Home roof"), model.material, false);
        }
    }
}
