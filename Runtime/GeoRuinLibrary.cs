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

        // The model for this building, or null (procedural damage). reason says why there is none.
        public GeoRuinModel Choose(GeoBuilding building, GeoRuinUse use, out string reason)
        {
            reason = null;
            var state = GeoBuildingDamage.StateOf(building);
            if (use == GeoRuinUse.Off) { reason = "ruin models are off"; return null; }
            if (state < (use == GeoRuinUse.AllDamage ? GeoConditionState.Damaged : GeoConditionState.HeavilyDamaged)) { reason = "no damage to show"; return null; }
            List<Vector2> outline;
            try { outline = GeoPolygon.Clean(building.footprint); }
            catch (System.ArgumentException) { reason = "invalid footprint"; return null; }
            var target = GeoRuins.TargetOf(building, outline);
            if (target.frame.fill < minimumFill) { reason = "footprint far from a rectangle"; return null; }
            string tag = string.IsNullOrEmpty(building.buildingTag) || building.buildingTag == "yes" ? null : building.buildingTag;
            var candidates = new List<(GeoRuinModel model, float cost)>();
            float best = float.MaxValue;
            foreach (var model in models)
            {
                if (!model || !model.Usable || !model.Shows(state)) continue;
                bool tagged = tag != null && model.HasTag(tag);
                if (!tagged && !(model.autoSelect && model.HasCategory(building.category))) continue;
                var fit = GeoRuinFit.Measure(model, target);
                // stretching costs, so do features left out and whole copies in a row (end walls inside a long hall),
                // and a model whose damage is far from this building's
                float cost = Mathf.Abs(Mathf.Log(fit.stretch.x)) + Mathf.Abs(Mathf.Log(fit.stretch.z)) + Mathf.Abs(Mathf.Log(fit.stretch.y)) +
                             0.3f * (fit.length.FeaturesLeftOut + fit.width.FeaturesLeftOut + fit.height.FeaturesLeftOut) +
                             0.3f * (fit.length.tiles - 1 + fit.width.tiles - 1) +
                             0.15f * Mathf.Abs((int)state - ((int)model.minState + (int)model.maxState) * 0.5f) - (tagged ? 1f : 0f);
                if (!float.IsFinite(cost)) continue;
                candidates.Add((model, cost));
                best = Mathf.Min(best, cost);
            }
            if (candidates.Count == 0) { reason = "no model for a " + GeoBuildingCondition.Name(state).Replace('_', ' ') + " " + (building.category ?? "building"); return null; }
            var pool = candidates.FindAll(c => c.cost <= best + variety);
            uint seed = GeoBuildingDamage.Seed(building.sourceId ?? building.name);
            return pool[Mathf.Min(pool.Count - 1, Mathf.FloorToInt(GeoBuildingDamage.Hash01(seed, 71) * pool.Count))].model;
        }

        public GeoRuinModel Find(string id) => models.Find(m => m && m.id == id);
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
        // library (none when nothing fits or the building is not damaged), mirrored by the building's ID so neighbours
        // differ, its front towards the street when street (world X/Z -> unit direction to the nearest road) knows one,
        // and the settings' apron. Model and Procedural are the user's choice and stay as they are. Without settings
        // nothing changes.
        public static void Assign(GeoBuilding building, GenerationSettings settings, Func<Vector2, Vector2?> street = null)
        {
            if (building == null || settings == null) return;
            if (building.ruinMode == GeoRuinMode.Procedural) { building.ruin = null; return; }
            if (building.ruinMode == GeoRuinMode.Model) return;
            building.ruin = settings.ruinLibrary ? settings.ruinLibrary.Choose(building, settings.ruinModels, out _) : null;
            uint seed = GeoBuildingDamage.Seed(building.sourceId ?? building.name);
            building.ruinMirror = GeoBuildingDamage.Hash01(seed, 61) < 0.5f;
            building.ruinTurn = GeoBuildingDamage.Hash01(seed, 62) < 0.5f;
            building.ruinApron = Mathf.Clamp(float.IsFinite(settings.ruinApron) ? settings.ruinApron : 3, 0, 12);
            if (building.ruin && street != null) Face(building, street);
        }

        // Turns the model so that its front (door, porch, entrances) is on the side of the footprint towards the street:
        // a long side, or an end for a model deeper than wide. A street off to the other sides leaves it as it was.
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
            var facing = building.ruin.LongAlongZ ? frame.axis : frame.Across;
            var front = transform.TransformDirection(new Vector3(facing.x, 0, facing.y));
            float side = Vector2.Dot(new Vector2(front.x, front.z).normalized, toward.Value);
            if (Mathf.Abs(side) > 0.3f) building.ruinTurn = side < 0;
        }

        public static bool Draws(GeoBuilding building) =>
            building != null && building.ruin != null && building.ruinMode != GeoRuinMode.Procedural;
    }
}
