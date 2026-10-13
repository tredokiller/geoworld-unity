using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // New shapes go at the end: the numbers are saved in scenes and assets.
    public enum GeoRoofShape { Flat, Gabled, Hipped, Skillion }

    [SelectionBase, DisallowMultipleComponent]
    public class GeoBuilding : MonoBehaviour
    {
        [HideInInspector] public string sourceId, category, heightSource;
        [HideInInspector] public string importNote;
        [HideInInspector] public Transform generatedGeometry;
        [HideInInspector] public GeoBuildingVisualDetails visualDetails;
        [HideInInspector] public GeoMappedAppearance mappedAppearance;
        [HideInInspector] public GeoBuildingCondition condition;
        [HideInInspector] public GeoRoofInfo roofInfo;
        public GeoBuildingStyle style;
        [Tooltip("Outline in the building's local X/Z plane. Select Edit footprint in the Inspector to move corners.")]
        public List<Vector2> footprint = new List<Vector2> {new Vector2(-4,-5),new Vector2(4,-5),new Vector2(4,5),new Vector2(-4,5)};
        [Min(2)] public float height = 6;
        [Min(1)] public int floors = 2;
        public GeoRoofShape roof = GeoRoofShape.Flat;
        [Min(0)] public float roofHeight = 2;
        [Tooltip("Degrees. The direction the roof slopes down to, measured in the building's local plane (0 = along local +X). A gabled or hipped roof is the same turned by 180; a single-slope roof is not.")]
        [Range(0,360)] public float roofDirection;
        [Tooltip("Additional height above the highest ground sample under the footprint.")]
        public float groundOffset;
        public bool windows = true;
        [Min(1)] public float windowSpacing = 3;
        [Min(0.2f)] public float windowWidth = 1.1f, windowHeight = 1.3f;
        public bool entrance = true;
        [Min(0)] public int entranceEdge;
        [Range(0.05f,0.95f)] public float entrancePosition = 0.5f;
        [Tooltip("Optional per-building overrides; otherwise the style's materials are used.")]
        public Material wallMaterial, roofMaterial;
        [Tooltip("Tint the style's wall material. An explicit Wall Material takes precedence.")]
        public bool overrideWallColor;
        public Color wallColor = Color.white;
        [Tooltip("Tint the style's roof material. An explicit Roof Material takes precedence.")]
        public bool overrideRoofColor;
        public Color roofColor = Color.white;
        [HideInInspector] public bool editFootprint;

        [Header("Ruin model")]
        [Tooltip("Auto: the generator picks a destroyed-building model from the ruin library for a damaged building (again on every rebuild). Model: always the Ruin below. Procedural: procedural damage, no model.")]
        public GeoRuinMode ruinMode = GeoRuinMode.Auto;
        [Tooltip("Drawn instead of walls and roof, fitted to the footprint: whole bays, boxes and storeys are repeated or left out, then the model is stretched the rest of the way. Choosing one by hand sets Ruin Mode to Model.")]
        public GeoRuinModel ruin;
        [Tooltip("Mirror the model along its length.")]
        public bool ruinMirror;
        [Tooltip("Turn the model round, so its front faces the other long side of the footprint.")]
        public bool ruinTurn;
        [Tooltip("Metres of the model's yard and debris kept around the footprint (the model's own apron is the upper limit).")]
        [Min(0)] public float ruinApron = 3;
        [Header("Home model")]
        [Tooltip("Auto: the generator picks an intact-building model from the home library for a building the world's Home Models setting covers (again on every rebuild). Model: always the Home below. Procedural: the style's walls and roof.")]
        public GeoRuinMode homeMode = GeoRuinMode.Auto;
        [Tooltip("Drawn instead of walls and roof, fitted to the footprint, with the roof shape, rise, material and colour of the export.")]
        public GeoHomeModelAsset home;
        public GeoHomeLibrary homeLibrary;
        public bool homeMirror, homeTurn;
        // The OpenStreetMap building=* value (school, church, garages ...), which can pick a ruin model.
        [HideInInspector] public string buildingTag;
    }
}
