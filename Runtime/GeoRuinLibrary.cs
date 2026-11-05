using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // Which damaged buildings the generator draws with a ruin model instead of procedural damage.
    public enum GeoRuinUse { Off, HeavilyDamagedAndDestroyed, AllDamage }

    // A building's ruin. Auto: the generator picks one from the ruin library for the building's condition (again on
    // every rebuild). Model: the building's Ruin, chosen by hand and kept. Procedural: no model, procedural damage.
    // New values go at the end: the numbers are saved in scenes.
    public enum GeoRuinMode { Auto, Model, Procedural }

    // The ruin models a generator may choose from, and how it chooses: a model must depict the building's condition
    // and stand for its category (or carry its OpenStreetMap building tag); among those, the one that needs the least
    // stretching after bays and storeys are added or left out wins, with some variety between near equals so that a
    // street of damaged houses is not one model printed over and over.
    [CreateAssetMenu(menuName = "Geo World/Ruin library", fileName = "Ruin library")]
    public class GeoRuinLibrary : ScriptableObject
    {
        public const string DefaultPath = GeoPaths.Package + "/Ruins/Ruin library.asset";

        public List<GeoRuinModel> models = new List<GeoRuinModel>();
        [Tooltip("Footprints that fill less of their smallest bounding rectangle than this (L, U and odd shapes) keep the procedural damage.")]
        [Range(0, 1)] public float minimumFill = 0.6f;
        [Tooltip("How much more stretch than the best model another one may need and still be picked. More gives more variety.")]
        [Range(0, 1)] public float variety = 0.35f;

        public const string FarFromRectangle = "footprint far from a rectangle";

        // The model for this building, or null (procedural damage). reason says why there is none.
        public GeoRuinModel Choose(GeoBuilding building, GeoRuinUse use, out string reason) => Pick(building, use, null, false, null, out reason);

        // As Choose, but where the library has no model for the building's own condition and category it takes the nearest
        // one it has: a condition a step off (a damaged factory drawn with a heavily damaged one instead of a bare box), then a
        // related category (a "ruins" or "construction" footprint as a house, apartments or industry, a destroyed garage as a
        // shed). target: a part of the footprint (GeoRuins.Parts; null: the whole, which must be close to a rectangle);
        // prefer: a model kept when it is among the near-best for this part too, so the parts of one building match.
        public GeoRuinModel ChooseNearest(GeoBuilding building, GeoRuinUse use, GeoRuinFit.Target target, GeoRuinModel prefer, out string reason) =>
            Pick(building, use, target, true, prefer, out reason);

        GeoRuinModel Pick(GeoBuilding building, GeoRuinUse use, GeoRuinFit.Target target, bool nearest, GeoRuinModel prefer, out string reason)
        {
            reason = null;
            var state = GeoBuildingDamage.StateOf(building);
            if (use == GeoRuinUse.Off) { reason = "ruin models are off"; return null; }
            if (state < (use == GeoRuinUse.AllDamage ? GeoConditionState.Damaged : GeoConditionState.HeavilyDamaged)) { reason = "no damage to show"; return null; }
            if (target == null)
            {
                List<Vector2> outline;
                try { outline = GeoPolygon.Clean(building.footprint); }
                catch (System.ArgumentException) { reason = "invalid footprint"; return null; }
                target = GeoRuins.TargetOf(building, outline);
                if (target.frame.fill < minimumFill) { reason = FarFromRectangle; return null; }
            }
            string tag = string.IsNullOrEmpty(building.buildingTag) || building.buildingTag == "yes" ? null : building.buildingTag;
            var candidates = new List<(GeoRuinModel model, float cost)>();
            float best = float.MaxValue;
            // the building's own category and condition; nearest: then a condition a step off, related categories, and both
            for (int pass = 0; pass < (nearest ? 4 : 1) && candidates.Count == 0; pass++)
            {
                bool stepOff = pass == 1 || pass == 3, related = pass >= 2;
                foreach (var model in models)
                {
                    if (!model || !model.Usable || !(stepOff ? Near(model, state) : model.Shows(state))) continue;
                    bool tagged = tag != null && model.HasTag(tag);
                    if (!tagged && !(model.autoSelect && (related ? Related(building.category, model) : model.HasCategory(building.category)))) continue;
                    var fit = GeoRuinFit.Measure(model, target);
                    // stretching costs, so do features left out and whole copies in a row (end walls inside a long hall),
                    // and a model whose damage is far from this building's
                    float cost = Mathf.Abs(Mathf.Log(fit.stretch.x)) + Mathf.Abs(Mathf.Log(fit.stretch.z)) + Mathf.Abs(Mathf.Log(fit.stretch.y)) +
                                 0.3f * (fit.length.FeaturesLeftOut + fit.width.FeaturesLeftOut + fit.height.FeaturesLeftOut) +
                                 0.3f * (fit.length.tiles - 1 + fit.width.tiles - 1) + 0.5f * fit.shortfall +
                                 0.15f * Mathf.Abs((int)state - ((int)model.minState + (int)model.maxState) * 0.5f) - (tagged ? 1f : 0f);
                    if (!float.IsFinite(cost)) continue;
                    candidates.Add((model, cost));
                    best = Mathf.Min(best, cost);
                }
            }
            if (candidates.Count == 0) { reason = "no model for a " + GeoBuildingCondition.Name(state).Replace('_', ' ') + " " + (building.category ?? "building"); return null; }
            var pool = candidates.FindAll(c => c.cost <= best + variety);
            if (prefer && pool.Exists(c => c.model == prefer)) return prefer;
            uint seed = GeoBuildingDamage.Seed(building.sourceId ?? building.name);
            return pool[Mathf.Min(pool.Count - 1, Mathf.FloorToInt(GeoBuildingDamage.Hash01(seed, 71) * pool.Count))].model;
        }

        // a condition at most one step from those the model depicts
        static bool Near(GeoRuinModel model, GeoConditionState state) =>
            (int)state >= (int)model.minState - 1 && (int)state <= (int)model.maxState + 1;

        // categories whose models may stand in for a building's, nearest first; an unknown category takes any, by fit
        static readonly Dictionary<string, string[]> RelatedCategories = new Dictionary<string, string[]>
        {
            ["ruins"] = new[] { "house", "apartments", "industrial" },
            ["construction"] = new[] { "apartments", "industrial", "commercial" },
            ["utility"] = new[] { "industrial", "garage", "shed" },
            ["commercial"] = new[] { "public", "industrial", "apartments" },
            ["public"] = new[] { "commercial", "apartments" },
            ["industrial"] = new[] { "commercial", "garage" },
            ["religious"] = new[] { "public" },
            ["garage"] = new[] { "shed", "house" },
            ["shed"] = new[] { "garage", "house", "farm" },
            ["farm"] = new[] { "house", "shed" },
            ["house"] = new[] { "farm", "apartments" },
            ["apartments"] = new[] { "public", "house" },
        };
        static readonly string[] AnyCategory = { "house", "apartments", "industrial", "commercial", "public", "garage", "shed", "farm" };

        static bool Related(string category, GeoRuinModel model)
        {
            foreach (var c in RelatedTo(category)) if (model.HasCategory(c)) return true;
            return false;
        }

        // the categories whose models may stand in for this one's (the home library uses the same table)
        public static string[] RelatedTo(string category) =>
            category != null && RelatedCategories.TryGetValue(category, out var related) ? related : AnyCategory;

        public GeoRuinModel Find(string id) => models.Find(m => m && m.id == id);
    }

    // One rectangle of an L, T or U footprint drawn with its own ruin model (GeoRuins.Parts), in the building's local space.
    public struct GeoRuinPart
    {
        public GeoRuinFit.Frame frame;
        public GeoRuinModel model;
        public bool turn;
    }

    // Ruin models on buildings: choosing one for a building and fitting it.
    public static class GeoRuins
    {
        // What the building asks of its ruin, from its footprint, height and roof.
        public static GeoRuinFit.Target TargetOf(GeoBuilding building, IList<Vector2> outline)
        {
            float height = Mathf.Max(2, building.height);
            float rise = building.roof != GeoRoofShape.Flat ? Mathf.Clamp(building.roofHeight, 0, height - 2) : 0;
            return new GeoRuinFit.Target
            {
                frame = GeoRuinFit.FrameOf(outline), height = height, eaves = height - rise, roofRise = rise,
                mirror = building.ruinMirror, turn = building.ruinTurn, apron = building.ruinApron
            };
        }

        // Gives a building the ruin the settings call for. Auto leaves everything to the generator: a model from the
        // library (the nearest it has: GeoRuinLibrary.ChooseNearest; none when the building is not damaged), mirrored by
        // the building's ID so neighbours differ, its front towards the street when street (world X/Z -> unit direction to
        // the nearest road) knows one, and the settings' apron. An L, T or U footprint gets a model on each of its
        // rectangles (ruinParts) instead of procedural damage. Model and Procedural are the user's choice and stay as they
        // are. Without settings nothing changes.
        public static void Assign(GeoBuilding building, GenerationSettings settings, Func<Vector2, Vector2?> street = null)
        {
            if (building == null || settings == null) return;
            building.ruinParts = null;
            if (building.ruinMode == GeoRuinMode.Procedural) { building.ruin = null; return; }
            if (building.ruinMode == GeoRuinMode.Model) return;
            var library = settings.ruinLibrary;
            string reason = null;
            building.ruin = library ? library.ChooseNearest(building, settings.ruinModels, null, null, out reason) : null;
            uint seed = GeoBuildingDamage.Seed(building.sourceId ?? building.name);
            building.ruinMirror = GeoBuildingDamage.Hash01(seed, 61) < 0.5f;
            building.ruinTurn = GeoBuildingDamage.Hash01(seed, 62) < 0.5f;
            building.ruinApron = Mathf.Clamp(float.IsFinite(settings.ruinApron) ? settings.ruinApron : 3, 0, 12);
            if (!building.ruin && reason == GeoRuinLibrary.FarFromRectangle)
            {
                building.ruinParts = PartsFor(building, settings, street);
                if (building.ruinParts != null) building.ruin = building.ruinParts[0].model;
            }
            else if (building.ruin && street != null) building.ruinTurn = Faced(building, GeoRuinFit.FrameOf(GeoPolygon.Clean(building.footprint)), building.ruin, street, building.ruinTurn);
        }

        // A model for each rectangle of an L, T or U footprint (the largest first; the others keep its model where it
        // fits them about as well), or null when the footprint does not split well or a part has no model. A heavily
        // damaged or destroyed building may leave half of its footprint to the parts' debris (what fell in); a damaged one
        // must be covered to three quarters, the rest a thin strip the parts' aprons lie over.
        static List<GeoRuinPart> PartsFor(GeoBuilding building, GenerationSettings settings, Func<Vector2, Vector2?> street)
        {
            List<Vector2> outline;
            try { outline = GeoPolygon.Clean(building.footprint); }
            catch (ArgumentException) { return null; }
            var frames = Parts(outline, GeoRuinFit.FrameOf(outline), GeoBuildingDamage.StateOf(building) >= GeoConditionState.HeavilyDamaged ? 0.5f : 0.75f);
            if (frames == null) return null;
            var parts = new List<GeoRuinPart>();
            GeoRuinModel first = null;
            foreach (var frame in frames)
            {
                var target = TargetOf(building, outline);
                target.frame = frame;
                var model = settings.ruinLibrary.ChooseNearest(building, settings.ruinModels, target, first, out _);
                if (!model) return null;
                if (!first) first = model;
                parts.Add(new GeoRuinPart
                {
                    frame = frame, model = model,
                    turn = street != null ? Faced(building, frame, model, street, building.ruinTurn) : building.ruinTurn
                });
            }
            return parts;
        }

        // Rectangles that make up an L, T, U or similar footprint, in the building's local space, each with its longer side
        // along its axis: the largest one that fits in what is left, again and again (at most ten, each at least 3 m across
        // and a sixteenth of the footprint), on a raster of the footprint in its frame. Where two touch, each reaches 0.6 m
        // into the other, so its end wall stands behind the other's wall and not on it. Null when they cover less than
        // coverage of the footprint (an odd shape keeps procedural damage). On Bakhmut's centre 4 / 5 of the footprints far
        // from a rectangle split at 0.75.
        public static List<GeoRuinFit.Frame> Parts(IList<Vector2> outline, GeoRuinFit.Frame frame, float coverage = 0.75f)
        {
            const float MinSide = 3f, Reach = 0.6f;
            float area = Mathf.Abs(GeoPolygon.Area(outline));
            float cell = Mathf.Max(0.5f, Mathf.Max(frame.length, frame.width) / 256f);
            int nu = Mathf.CeilToInt(frame.length / cell), nv = Mathf.CeilToInt(frame.width / cell);
            if (area < 1 || nu < 2 || nv < 2) return null;
            var across = frame.Across;
            Vector2 corner = frame.centre - frame.axis * (frame.length * 0.5f) - across * (frame.width * 0.5f);
            var local = new List<Vector2>(outline.Count);
            foreach (var p in outline) local.Add(new Vector2(Vector2.Dot(p - corner, frame.axis), Vector2.Dot(p - corner, across)));
            var inside = new bool[nu * nv];
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                    inside[j * nu + i] = GeoPolygon.Contains(local, new Vector2((i + 0.5f) * cell, (j + 0.5f) * cell));
            var free = (bool[])inside.Clone();
            var owner = new int[nu * nv];
            for (int k = 0; k < owner.Length; k++) owner[k] = -1;
            var rects = new List<RectInt>();
            float covered = 0;
            while (rects.Count < 10)
            {
                var r = LargestRectangle(free, nu, nv);
                float w = r.width * cell, h = r.height * cell;
                if (Mathf.Min(w, h) < MinSide || w * h < area / 16f) break;
                for (int j = r.yMin; j < r.yMax; j++)
                    for (int i = r.xMin; i < r.xMax; i++) { free[j * nu + i] = false; owner[j * nu + i] = rects.Count; }
                rects.Add(r);
                covered += w * h;
                if (covered > area * 0.95f) break;
            }
            if (rects.Count == 0 || covered < area * coverage) return null;
            var result = new List<GeoRuinFit.Frame>();
            for (int n = 0; n < rects.Count; n++)
            {
                var r = rects[n];
                // a side whose next row of cells is mostly another part reaches into it
                bool Touches(int i0, int i1, int j0, int j1)
                {
                    int other = 0, all = 0;
                    for (int j = j0; j < j1; j++)
                        for (int i = i0; i < i1; i++)
                        {
                            if (i < 0 || j < 0 || i >= nu || j >= nv) continue;
                            all++;
                            if (owner[j * nu + i] >= 0 && owner[j * nu + i] != n) other++;
                        }
                    return all > 0 && other * 2 >= all;
                }
                float u0 = r.xMin * cell - (Touches(r.xMin - 1, r.xMin, r.yMin, r.yMax) ? Reach : 0);
                float u1 = r.xMax * cell + (Touches(r.xMax, r.xMax + 1, r.yMin, r.yMax) ? Reach : 0);
                float v0 = r.yMin * cell - (Touches(r.xMin, r.xMax, r.yMin - 1, r.yMin) ? Reach : 0);
                float v1 = r.yMax * cell + (Touches(r.xMin, r.xMax, r.yMax, r.yMax + 1) ? Reach : 0);
                u0 = Mathf.Max(0, u0); v0 = Mathf.Max(0, v0); u1 = Mathf.Min(frame.length, u1); v1 = Mathf.Min(frame.width, v1);
                var centre = corner + frame.axis * ((u0 + u1) * 0.5f) + across * ((v0 + v1) * 0.5f);
                float lu = u1 - u0, lv = v1 - v0;
                var axis = lu >= lv ? frame.axis : across;
                // one direction for a given axis, as GeoRuinFit.FrameOf, so a rebuild never turns a model round
                if (axis.x < -1e-6f || (Mathf.Abs(axis.x) <= 1e-6f && axis.y < 0)) axis = -axis;
                result.Add(new GeoRuinFit.Frame { centre = centre, axis = axis, length = Mathf.Max(lu, lv), width = Mathf.Min(lu, lv), fill = 1 });
            }
            return result;
        }

        // The largest all-true rectangle of a raster (row j, column i at j * nu + i): for each row the run of true cells
        // above every column as a histogram, and its largest rectangle with a stack. Ties go to the first found.
        internal static RectInt LargestRectangle(bool[] cells, int nu, int nv)
        {
            var heights = new int[nu + 1];
            var stack = new int[nu + 1];
            var best = new RectInt(0, 0, 0, 0);
            int bestArea = 0;
            for (int j = 0; j < nv; j++)
            {
                for (int i = 0; i < nu; i++) heights[i] = cells[j * nu + i] ? heights[i] + 1 : 0;
                heights[nu] = 0;
                int top = 0;
                for (int i = 0; i <= nu; i++)
                {
                    while (top > 0 && heights[stack[top - 1]] >= heights[i])
                    {
                        int h = heights[stack[--top]];
                        int left = top > 0 ? stack[top - 1] + 1 : 0;
                        int a = h * (i - left);
                        if (a > bestArea) { bestArea = a; best = new RectInt(left, j - h + 1, i - left, h); }
                    }
                    stack[top++] = i;
                }
            }
            return best;
        }

        // Which way round a model goes so that its front (door, porch, entrances) is on the side of its frame towards the
        // street: a long side, or an end for a model deeper than wide. A street off to the other sides leaves turn as it is.
        static bool Faced(GeoBuilding building, GeoRuinFit.Frame frame, GeoRuinModel model, Func<Vector2, Vector2?> street, bool turn)
        {
            var transform = building.transform;
            var centre = transform.TransformPoint(new Vector3(frame.centre.x, 0, frame.centre.y));
            var toward = street(new Vector2(centre.x, centre.z));
            if (toward == null) return turn;
            var facing = model.LongAlongZ ? frame.axis : frame.Across;
            var front = transform.TransformDirection(new Vector3(facing.x, 0, facing.y));
            float side = Vector2.Dot(new Vector2(front.x, front.z).normalized, toward.Value);
            return Mathf.Abs(side) > 0.3f ? side < 0 : turn;
        }

        public static bool Draws(GeoBuilding building) =>
            building != null && building.ruin != null && building.ruinMode != GeoRuinMode.Procedural;
    }
}
