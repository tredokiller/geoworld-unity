using System;
using UnityEngine;

namespace GeoWorld
{
    // The "roof" block of a building in a geo-buildings export. Every fact says where it came from:
    //   reviewed   a person checked it against imagery
    //   mapped     an OpenStreetMap roof:* tag
    //   estimated  what is typical for this kind of building; never an observation
    // The block already merges those three (reviewed over mapped over estimated). Numbers missing from the file
    // stay at Absent (-1) so that 0 (a flat roof, a ridge running north) remains a real value.
    [Serializable]
    public class GeoRoofInfo
    {
        public const float Absent = -1;
        public const string Reviewed = "reviewed", Mapped = "mapped", Estimated = "estimated";

        public string shape, shapeSource;
        [Tooltip("The nearest of flat / gabled / hipped, for a shape this renderer cannot draw.")]
        public string renderAs;
        public string ridge, ridgeSource;
        public string material, materialSource;
        public string color, colorSource, colorRaw;
        public string pitchSource, riseSource;
        public float shapeConfidence = Absent, colorConfidence = Absent;
        [Tooltip("Degrees clockwise from north, 0 to 180: the direction the ridge runs.")]
        public float ridgeBearing = Absent;
        [Tooltip("Degrees clockwise from north: where a single-slope roof drains to. Mapped only.")]
        public float slopeBearing = Absent;
        [Tooltip("Slope in degrees.")]
        public float pitch = Absent;
        [Tooltip("Metres from the eaves to the highest point of the roof.")]
        public float rise = Absent;

        public static bool Has(float value) => value >= 0 && !float.IsNaN(value) && !float.IsInfinity(value);
        static bool IsEstimate(string source) => source == Estimated;
        // pitch and rise carry a source only when they were mapped; otherwise they follow from the shape
        bool PitchEstimated => Has(pitch) && pitchSource != Mapped;
        bool RiseEstimated => Has(rise) && riseSource != Mapped;

        public bool ShapeEstimated() => !string.IsNullOrEmpty(shape) && IsEstimate(shapeSource);
        public bool ColorEstimated() => !string.IsNullOrEmpty(color) && IsEstimate(colorSource);

        // Whether any part of this roof is a typical value rather than something known about the building.
        public bool HasEstimates() =>
            ShapeEstimated() || ColorEstimated() || (!string.IsNullOrEmpty(material) && IsEstimate(materialSource)) || PitchEstimated || RiseEstimated;

        // The same roof with only the mapped and reviewed facts, or null when nothing of it is known.
        public GeoRoofInfo WithoutEstimates()
        {
            var kept = new GeoRoofInfo { colorRaw = colorRaw };
            bool shapeKnown = !string.IsNullOrEmpty(shape) && !IsEstimate(shapeSource);
            if (shapeKnown)
            {
                kept.shape = shape; kept.shapeSource = shapeSource; kept.renderAs = renderAs; kept.shapeConfidence = shapeConfidence;
                if (ridgeSource == Mapped) { kept.ridge = ridge; kept.ridgeSource = ridgeSource; kept.ridgeBearing = ridgeBearing; }
                kept.slopeBearing = slopeBearing;
            }
            if (Has(pitch) && pitchSource == Mapped) { kept.pitch = pitch; kept.pitchSource = pitchSource; }
            if (Has(rise) && riseSource == Mapped) { kept.rise = rise; kept.riseSource = riseSource; }
            if (!string.IsNullOrEmpty(material) && !IsEstimate(materialSource)) { kept.material = material; kept.materialSource = materialSource; }
            if (!string.IsNullOrEmpty(color) && !IsEstimate(colorSource)) { kept.color = color; kept.colorSource = colorSource; kept.colorConfidence = colorConfidence; }
            bool anything = shapeKnown || Has(kept.pitch) || Has(kept.rise) || kept.material != null || kept.color != null || !string.IsNullOrEmpty(kept.colorRaw);
            return anything ? kept : null;
        }
    }
}
