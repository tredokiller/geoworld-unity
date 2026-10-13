using System;
using System.Collections.Generic;

namespace GeoWorld
{
    // What state a building is in, from the app's condition layer (extractor/condition.py).
    // Unknown means nobody has evidence; it is drawn like an intact building but is not a claim.
    public enum GeoConditionState { Unknown = 0, Intact = 1, Damaged = 2, HeavilyDamaged = 3, Destroyed = 4 }

    [Serializable]
    public class GeoBuildingCondition
    {
        // state: intact, damaged, heavily_damaged, destroyed. basis: observed, reported, mapped, estimated.
        public string state, basis, confidence, asOf, note;
        // Radar change score of an estimate; negative when the evidence has none.
        public float score = -1;
        public List<string> sources = new List<string>();

        public GeoConditionState State => Parse(state);
        public bool Estimated => basis == "estimated";

        public static GeoConditionState Parse(string value)
        {
            switch (value)
            {
                case "intact": return GeoConditionState.Intact;
                case "damaged": return GeoConditionState.Damaged;
                case "heavily_damaged": return GeoConditionState.HeavilyDamaged;
                case "destroyed": return GeoConditionState.Destroyed;
                default: return GeoConditionState.Unknown;
            }
        }

        public static string Name(GeoConditionState state)
        {
            switch (state)
            {
                case GeoConditionState.Intact: return "intact";
                case GeoConditionState.Damaged: return "damaged";
                case GeoConditionState.HeavilyDamaged: return "heavily_damaged";
                case GeoConditionState.Destroyed: return "destroyed";
                default: return "unknown";
            }
        }

        public GeoBuildingCondition Copy()
        {
            var copy = (GeoBuildingCondition)MemberwiseClone();
            copy.sources = sources == null ? new List<string>() : new List<string>(sources);
            return copy;
        }
    }
}
