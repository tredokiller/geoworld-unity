using System;
using UnityEngine;

namespace GeoWorld
{
    // How a ruin model may be cut along one of its axes so that it can be made longer or shorter. Joints are the planes
    // where it may be cut (in the middle of a pier between window bays, at a party wall, between two storeys); the
    // segments between them say what may be done with each piece:
    //   C  cap      an end of the building (end wall, corner): always kept, once
    //   F  feature  the damage or something one of a kind (breach, collapsed bay, porch, tower): kept once, left out
    //               only to fit a much smaller building
    //   R  repeat   a regular bay, box or storey: repeated for a longer or taller building, left out for a shorter one
    // Pieces only join without a seam where the model looks the same at both joints, so an R segment should span whole
    // bays or storeys. An axis without joints is one F segment: the model is only stretched along it.
    [Serializable]
    public class GeoRuinAxis
    {
        [Tooltip("Model-space positions (metres, ascending, inside the footprint) where the model may be cut.")]
        public float[] joints = new float[0];
        [Tooltip("One letter per segment, from the footprint's start to its end: C cap, F feature, R repeat. One more letter than joints.")]
        public string segments = "F";
        [Tooltip("Every joint lies on a whole wall (a party wall between boxes), so a short building may use only part of the model.")]
        public bool openEnds;

        public GeoRuinAxis() { }
        public GeoRuinAxis(string segments, bool openEnds, params float[] joints) { this.segments = segments; this.openEnds = openEnds; this.joints = joints; }
    }

    // One destroyed-building model (from ruins/models, imported by Tools > Geo World > Import ruin models) and what it
    // takes to fit it onto a real building: which buildings it can stand for, its intact footprint and heights in model
    // space, and where it may be cut. Model space is Unity's: X is the long axis, Z the depth (the front facade faces
    // +Z), Y up, the ground at y = 0.
    [CreateAssetMenu(menuName = "Geo World/Ruin model", fileName = "Ruin model")]
    public class GeoRuinModel : ScriptableObject
    {
        [Tooltip("The model's ID in the ruin set, e.g. B09.")]
        public string id;
        [TextArea(2, 6)] public string title;
        public Mesh mesh;
        public Material material;

        [Header("What it can stand for")]
        [Tooltip("Building categories of the export: house, shed, garage, farm, apartments, industrial, commercial, public, religious ...")]
        public string[] categories = new string[0];
        [Tooltip("OpenStreetMap building=* values it fits best (school, church, garages ...). A matching tag wins over the category.")]
        public string[] tags = new string[0];
        [Tooltip("The lightest and the heaviest condition this ruin depicts.")]
        public GeoConditionState minState = GeoConditionState.Damaged, maxState = GeoConditionState.Destroyed;
        [Tooltip("Off: used only for a building whose tag matches, or when assigned by hand (one-of-a-kind buildings).")]
        public bool autoSelect = true;

        [Header("The intact building, in model space")]
        [Tooltip("Corner of the intact footprint with the smallest X and Z (outer wall faces, without yard and debris).")]
        public Vector2 footprintMin = new Vector2(-5, -4);
        [Tooltip("Corner of the intact footprint with the largest X and Z.")]
        public Vector2 footprintMax = new Vector2(5, 4);
        [Min(1)] public int floors = 1;
        [Tooltip("Height of the intact walls: where the roof began. Fitted to the building's eaves.")]
        [Min(0.5f)] public float eaves = 2.8f;
        [Tooltip("Height of the intact roof above the eaves; 0 for a flat roof. Everything above the eaves is fitted to the building's roof rise.")]
        [Min(0)] public float roofRise;
        [Tooltip("Heights follow the footprint (scaled as much as length and width are on average), not the building's height and roof: for one-of-a-kind buildings with domes, towers or a headframe, whose height in the data says little.")]
        public bool keepProportions;

        [Header("How it may be cut")]
        [Tooltip("Along X, the long axis.")] public GeoRuinAxis length = new GeoRuinAxis();
        [Tooltip("Along Z, the depth.")] public GeoRuinAxis width = new GeoRuinAxis();
        [Tooltip("Along Y between the ground and the eaves: storeys.")] public GeoRuinAxis height = new GeoRuinAxis();
        [Tooltip("Metres of yard, rubble and debris kept around the footprint; low parts further out are left out so they do not cover streets and neighbours.")]
        [Min(0)] public float apron = 3;

        public Vector2 FootprintSize => footprintMax - footprintMin;
        // A model deeper than wide (a theatre): its Z runs along the footprint's long side, and its front faces an end.
        public bool LongAlongZ => FootprintSize.y > FootprintSize.x * 1.05f;
        public bool Usable => mesh != null && material != null && FootprintSize.x > 0.1f && FootprintSize.y > 0.1f;

        public bool Shows(GeoConditionState state) => state >= minState && state <= maxState;

        public bool HasCategory(string category) => Has(categories, category);
        public bool HasTag(string tag) => Has(tags, tag);

        static bool Has(string[] list, string value)
        {
            if (list == null || string.IsNullOrEmpty(value)) return false;
            foreach (var item in list) if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
