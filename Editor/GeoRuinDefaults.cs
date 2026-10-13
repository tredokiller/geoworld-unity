using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GeoWorld.Editor
{
    // How each model of the ruin set fits real buildings: what it can stand for, its intact footprint and heights, and
    // where it may be cut. The numbers come from each model's generator script (ruins/work/<ID>/gen_*.py) and spec
    // (ruins/roster) and are written in the scripts' coordinates: X the long axis, Y the depth with the front at -Y,
    // Z up. Unity sees the same model with X mirrored and the front facing +Z; Apply converts.
    // A model that is not in the table gets a plain fit from its spec: its intact box, no joints (only stretched).
    static class GeoRuinDefaults
    {
        sealed class Spec
        {
            public string[] categories, tags;
            public GeoConditionState min = GeoConditionState.Damaged, max = GeoConditionState.Destroyed;
            public bool auto = true, proportions;
            public float x0, x1, y0, y1;            // intact footprint
            public int floors = 1;
            public float eaves, roofRise, apron = 3; // intact eaves and roof rise
            public string xSegments = "F", ySegments = "F", zSegments = "F";
            public float[] xJoints = new float[0], yJoints = new float[0], zJoints = new float[0];
            public bool xOpen, yOpen;
        }

        static readonly Dictionary<string, Spec> Specs = new Dictionary<string, Spec>
        {
            // B01: five-storey panel block, three of four sections pancaked into a debris ramp: the ramp cannot be cut (one long
            // cap); in the standing section a 3.55 m bay repeats and the bay without a ground-floor window may go. Storey 4 repeats
            // or goes (the ramp and stub stay below). Flat roof.
            ["B01"] = new Spec
            {
                categories = new[] { "apartments" },
                tags = new[] { "apartments", "residential", "dormitory" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.Destroyed,
                x0 = -32f, x1 = 32f, y0 = -5.9f, y1 = 5.9f,
                floors = 5, eaves = 14.3f, roofRise = 0f, apron = 9.5f,
                xJoints = new[] { 22.8f, 25.15f, 28.7f }, xSegments = "CFRC",
                zJoints = new[] { 9.06f, 11.76f }, zSegments = "CRC"
            },
            // B02: five-storey brick block, burnt hip roof and breached corner: only a 8.7 m two-window bay of the west section (x
            // -1.2..7.5) repeats, carrying its own shell hole and balcony. Storey 4 repeats or goes (+1 at most looks right). Intact
            // eaves 15, ridge 2.8 m higher.
            ["B02"] = new Spec
            {
                categories = new[] { "apartments" },
                tags = new[] { "apartments", "residential", "dormitory" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -19f, x1 = 19f, y0 = -6f, y1 = 6f,
                floors = 5, eaves = 15f, roofRise = 2.8f, apron = 7.5f,
                xJoints = new[] { -1.2f, 7.5f }, xSegments = "CRC",
                zJoints = new[] { 8.3f, 11.1f }, zSegments = "CRC"
            },
            // B03: nine-storey panel block with a stepped collapse: a 3.6 m bay repeats, the first entrance (stair, canopy, lift
            // room) may go, the collapse is a cap. Storeys 4-5 repeat or go (cut just above the slabs). Flat roof.
            ["B03"] = new Spec
            {
                categories = new[] { "apartments" },
                tags = new[] { "apartments", "residential" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.Destroyed,
                x0 = -28.5f, x1 = 28.5f, y0 = -6.3f, y1 = 6.3f,
                floors = 9, eaves = 26.5f, roofRise = 0f, apron = 7f,
                xJoints = new[] { -27.1f, -23.5f, -14.15f }, xSegments = "CRFC",
                zJoints = new[] { 9.8f, 12.6f, 15.4f }, zSegments = "CRRC"
            },
            // B04: nine-storey panel block with the upper floors lost over two sections: two bays of the standing section repeat,
            // its entrance core may go. Storeys 2-3 repeat or go, with identical cuts. Flat roof.
            ["B04"] = new Spec
            {
                categories = new[] { "apartments" },
                tags = new[] { "apartments", "residential" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -27f, x1 = 27f, y0 = -6.3f, y1 = 6.3f,
                floors = 9, eaves = 26f, roofRise = 0f, apron = 6f,
                xJoints = new[] { -25.45f, -22.15f, -14.35f, -10f }, xSegments = "CRFRC",
                zJoints = new[] { 4.05f, 6.85f, 9.65f }, zSegments = "CRRC"
            },
            // B05: nine-storey panel block, fire-gutted: one 18 m stair section (x -20.5..-2.5) repeats; the burnt bays are features
            // and the NE breach a cap. Storeys 2-3 repeat (cuts 0.5 m above the slabs, in the spandrel band); the burnt storeys 4-6
            // go for a low block. Flat roof: parapet and machine rooms keep their size.
            ["B05"] = new Spec
            {
                categories = new[] { "apartments" },
                tags = new[] { "apartments", "residential", "dormitory" },
                min = GeoConditionState.Damaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -27f, x1 = 27f, y0 = -6.3f, y1 = 6.3f,
                floors = 9, eaves = 26.4f, roofRise = 0f, apron = 6f,
                xJoints = new[] { -20.5f, -2.5f, 8.9f, 20.65f }, xSegments = "CRFFC",
                zJoints = new[] { 4.5f, 7.3f, 10.1f, 15.7f, 18.5f }, zSegments = "CRRFFC"
            },
            // B06: sixteen-storey tower: a 6.6 m loggia-and-window bay repeats; the lower breach is a feature. Storeys 6-15 are
            // identical and repeat or go (6 storeys at least); only the baked soot steps at the cuts.
            ["B06"] = new Spec
            {
                categories = new[] { "apartments" },
                tags = new[] { "apartments", "residential" },
                min = GeoConditionState.Damaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -13.35f, x1 = 13.35f, y0 = -6.65f, y1 = 6.65f,
                floors = 16, eaves = 44.8f, roofRise = 0f, apron = 5.5f,
                xJoints = new[] { -9.9f, 3.3f, 9.9f }, xSegments = "CFRC",
                zJoints = new[] { 14f, 16.8f, 19.6f, 22.4f, 25.2f, 28f, 30.8f, 33.6f, 36.4f, 39.2f, 42f }, zSegments = "CRRRRRRRRRRC"
            },
            // B07: Stalinist corner block, tower and one wing: a 4.3 m wing bay repeats, the burnt roof gap is a feature, the tower
            // is a cap. Wing storey 2 repeats (the tower's storeys are out of phase, so only one band is clean).
            ["B07"] = new Spec
            {
                categories = new[] { "apartments" },
                tags = new[] { "apartments", "residential" },
                min = GeoConditionState.Damaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -21f, x1 = 21f, y0 = -7f, y1 = 7f,
                floors = 5, eaves = 16f, roofRise = 3.93f, apron = 5f,
                xJoints = new[] { -3.75f, 0.55f, 16.25f }, xSegments = "CRFC",
                zJoints = new[] { 3.7f, 6.9f }, zSegments = "CRC"
            },
            // B08: five-storey block collapsed to a rubble mound with fins: every plane cuts slabs or fins, so it is only stretched
            // (a row of two for a very long block).
            ["B08"] = new Spec
            {
                categories = new[] { "apartments" },
                tags = new[] { "apartments", "residential", "dormitory" },
                min = GeoConditionState.Destroyed, max = GeoConditionState.Destroyed,
                x0 = -20f, x1 = 20f, y0 = -6f, y1 = 6f,
                floors = 5, eaves = 13.5f, roofRise = 0f, apron = 6f
            },
            // B09: brick house, roof stripped. Front and rear windows do not line up, so nothing repeats along the length; the blast
            // bay (x 0.55..4.45) may go for a short house. A blank 1.85 m band of the end walls repeats in depth. Intact eaves 2.8,
            // ridge 5.6.
            ["B09"] = new Spec
            {
                categories = new[] { "house", "farm" },
                tags = new[] { "house", "detached", "bungalow", "residential", "farm" },
                min = GeoConditionState.Damaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -5f, x1 = 5f, y0 = -4f, y1 = 4f,
                floors = 1, eaves = 2.8f, roofRise = 2.8f, apron = 3.3f,
                xJoints = new[] { 0.55f, 4.45f }, xSegments = "CFC",
                yJoints = new[] { -2.78f, -0.93f }, ySegments = "CRC"
            },
            // B10: roofless brick house with gable stump and chimney: partitions, heaps, chimney and porch cross every plane, so it
            // is only stretched. Intact eaves 2.8, ridge 5.6.
            ["B10"] = new Spec
            {
                categories = new[] { "house", "farm" },
                tags = new[] { "house", "detached", "bungalow", "residential", "farm" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.Destroyed,
                x0 = -5f, x1 = 5f, y0 = -4f, y1 = 4f,
                floors = 1, eaves = 2.8f, roofRise = 2.8f, apron = 2.9f
            },
            // B11: two-storey cottage: the bay x -6.3..-3.45 (window column, intact roof) repeats with identical cuts. The footprint
            // includes the garage wing. Two storeys of different heights: no joints in height.
            ["B11"] = new Spec
            {
                categories = new[] { "house" },
                tags = new[] { "house", "detached", "residential" },
                min = GeoConditionState.Damaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -7f, x1 = 7f, y0 = -5f, y1 = 5f,
                floors = 2, eaves = 6.05f, roofRise = 2.75f, apron = 3.5f,
                xJoints = new[] { -6.3f, -3.45f }, xSegments = "CRC"
            },
            // B12: razed plot: a noisy mound over the house (off centre in the model), a corner stub and a crater; nothing to cut,
            // only stretched. The roof is gone; the intact roof rose about 2.2 m above 2.8 m walls.
            ["B12"] = new Spec
            {
                categories = new[] { "house", "shed", "farm" },
                tags = new[] { "house", "detached", "semidetached_house", "residential", "bungalow", "farm", "cabin", "hut", "shed", "farm_auxiliary", "barn" },
                min = GeoConditionState.Destroyed, max = GeoConditionState.Destroyed,
                x0 = -7.1f, x1 = 1.9f, y0 = -2.3f, y1 = 4.7f,
                floors = 1, eaves = 2.8f, roofRise = 2.2f, apron = 1.7f
            },
            // B13: garage row, eight 3.2 m boxes; every joint lies in a full party wall, so any run of boxes is closed (a shed is
            // one or two boxes). Box 7 is the only one that repeats seamlessly; boxes 3-4 collapsed together.
            ["B13"] = new Spec
            {
                categories = new[] { "garage", "shed" },
                tags = new[] { "garages", "garage", "shed", "farm_auxiliary" },
                min = GeoConditionState.Damaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -12.8f, x1 = 12.8f, y0 = -3f, y1 = 3f,
                floors = 1, eaves = 2.47f, roofRise = 0f, apron = 3f,
                xJoints = new[] { -9.53f, -6.4f, 0f, 3.2f, 6.42f, 9.55f }, xSegments = "CFFFFRC", xOpen = true
            },
            // B14: hospital wing, front facade peeled off over most of its length, porch and hip ends at the other: no two bays
            // alike, only stretched (best for wings of about 30-45 m).
            ["B14"] = new Spec
            {
                categories = new[] { "public" },
                tags = new[] { "hospital", "clinic", "kindergarten" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -18f, x1 = 18f, y0 = -7f, y1 = 7f,
                floors = 2, eaves = 8.1f, roofRise = 1.4f, apron = 4.5f
            },
            // B15: school wing: the 3.6 m bays x 8.7..15.9 repeat (window column on all floors under the flat roof); the collapsed
            // end bay, stair core and entrance are one cap. Flat roof.
            ["B15"] = new Spec
            {
                categories = new[] { "public" },
                tags = new[] { "school", "college", "university" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -18f, x1 = 18f, y0 = -6.5f, y1 = 6.5f,
                floors = 3, eaves = 11.8f, roofRise = 0f, apron = 8.5f,
                xJoints = new[] { 8.7f, 12.3f, 15.9f }, xSegments = "CRRC"
            },
            // B16: drama theatre with portico, deeper than wide: its front faces an end of the footprint. One of a kind, only
            // stretched, heights follow the plan. Only for theatre tags.
            ["B16"] = new Spec
            {
                categories = new[] { "public" },
                tags = new[] { "theatre", "concert_hall" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.Destroyed, auto = false, proportions = true,
                x0 = -21f, x1 = 21f, y0 = -20.5f, y1 = 31.5f,
                floors = 2, eaves = 14.8f, roofRise = 6.6f, apron = 15f
            },
            // B17: palace of culture: a 4.4 m bay and an 8.8 m double bay (x -23.9..-10.7) repeat; the pedimented centre and the
            // burnt half are a cap. Intact eaves 14.3, ridge 16.9.
            ["B17"] = new Spec
            {
                categories = new[] { "public" },
                tags = new[] { "civic", "public", "university", "college" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -33f, x1 = 33f, y0 = -16.5f, y1 = 16.5f,
                floors = 3, eaves = 14.3f, roofRise = 2.6f, apron = 7f,
                xJoints = new[] { -23.9f, -19.5f, -10.7f }, xSegments = "CRRC"
            },
            // B18: regional administration: 5 m bays repeat on both wings (x -31.9..-21.9, 18.1..28.1); the collapsed centre is a
            // feature that a smaller building loses, leaving a burnt but standing block. Flat roof, technical floor kept as it is.
            ["B18"] = new Spec
            {
                categories = new[] { "public" },
                tags = new[] { "government", "civic", "public", "office" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.Destroyed,
                x0 = -35f, x1 = 35f, y0 = -9f, y1 = 9f,
                floors = 4, eaves = 17f, roofRise = 0f, apron = 7.5f,
                xJoints = new[] { -31.9f, -26.9f, -21.9f, 18.1f, 23.1f, 28.1f }, xSegments = "CRRFRRC"
            },
            // B19: steel big box, roof caved in over 40 of 54 m: every bay differs (sag, burnt roof, cladding), so only stretched.
            ["B19"] = new Spec
            {
                categories = new[] { "commercial" },
                tags = new[] { "retail", "supermarket", "commercial" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.Destroyed,
                x0 = -27f, x1 = 27f, y0 = -15.5f, y1 = 20.5f,
                floors = 1, eaves = 9f, roofRise = 1.2f, apron = 6f
            },
            // B20: heavy shop hall: lantern, roof holes, a folded bay and the bent craneway make every 12 m bay different; only
            // stretched.
            ["B20"] = new Spec
            {
                categories = new[] { "industrial" },
                tags = new[] { "industrial", "factory", "manufacture", "hangar" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged,
                x0 = -24.7f, x1 = 24.55f, y0 = -18.5f, y1 = 18.5f,
                floors = 1, eaves = 14.5f, roofRise = 6f, apron = 8.5f
            },
            // B21: production hall: the 6 m bay x -12..-6 repeats (cuts on column lines; small steps in the annex wall and duct show
            // at seams). The burnt middle (x -6..14.8) may go for a short hall, and the lean-to annex (y 6.32..12.15) for a shallow
            // one.
            ["B21"] = new Spec
            {
                categories = new[] { "industrial" },
                tags = new[] { "industrial", "factory", "manufacture", "warehouse" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.Destroyed,
                x0 = -18f, x1 = 18f, y0 = -12f, y1 = 12.15f,
                floors = 1, eaves = 9.5f, roofRise = 2.6f, apron = 5.3f,
                xJoints = new[] { -12f, -6f, 14.8f }, xSegments = "CRFC",
                yJoints = new[] { 6.32f }, ySegments = "CF"
            },
            // B22: grain silo bank: the silo column x -8..18.35 repeats in 26.35 m steps; the head house is a feature. Only for
            // building=silo; heights follow the plan.
            ["B22"] = new Spec
            {
                categories = new[] { "industrial" },
                tags = new[] { "silo" },
                min = GeoConditionState.Damaged, max = GeoConditionState.HeavilyDamaged, auto = false, proportions = true,
                x0 = -33f, x1 = 33f, y0 = -25f, y1 = 25f,
                floors = 1, eaves = 27f, roofRise = 18.3f, apron = 4f,
                xJoints = new[] { -8f, 18.35f }, xSegments = "CRF"
            },
            // B23: mine surface complex: the yard between the shaft group and the admin block repeats in depth; the shaft group may
            // go. Only for mine tags; heights follow the plan.
            ["B23"] = new Spec
            {
                categories = new[] { "industrial" },
                tags = new[] { "mineshaft", "headframe", "mine" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged, auto = false, proportions = true,
                x0 = -35.5f, x1 = 26.5f, y0 = -33f, y1 = 32f,
                floors = 3, eaves = 10.8f, roofRise = 36.75f, apron = 10f,
                yJoints = new[] { -14.5f, 0.5f }, ySegments = "FRC"
            },
            // B24: red-brick station: one 3 m wing bay repeats (x 12.2..15.2). Platform, canopy and rails lie outside the footprint
            // and are trimmed. Only for train_station.
            ["B24"] = new Spec
            {
                categories = new[] { "public" },
                tags = new[] { "train_station", "transportation" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged, auto = false,
                x0 = -18f, x1 = 18f, y0 = -14f, y1 = 0f,
                floors = 2, eaves = 9f, roofRise = 4f, apron = 2f,
                xJoints = new[] { 12.2f, 15.2f }, xSegments = "CRC"
            },
            // B25: Orthodox church: no regular part; only stretched, heights follow the plan. Only for church tags.
            ["B25"] = new Spec
            {
                categories = new[] { "religious" },
                tags = new[] { "church", "cathedral", "religious" },
                min = GeoConditionState.HeavilyDamaged, max = GeoConditionState.HeavilyDamaged, auto = false, proportions = true,
                x0 = -19.5f, x1 = 19.2f, y0 = -13.95f, y1 = 13.95f,
                floors = 1, eaves = 9f, roofRise = 20.3f, apron = 3f
            },
        };

        public static bool Has(string id) => id != null && Specs.ContainsKey(id);

        public static void Apply(GeoRuinModel model, JObject entry, JObject roster)
        {
            if (Specs.TryGetValue(model.id ?? "", out var spec)) Apply(model, spec);
            else Guess(model, entry, roster);
        }

        static void Apply(GeoRuinModel model, Spec s)
        {
            model.categories = (string[])s.categories.Clone();
            model.tags = (string[])s.tags.Clone();
            model.minState = s.min; model.maxState = s.max;
            model.autoSelect = s.auto; model.keepProportions = s.proportions;
            // script X runs the other way in Unity, script Y becomes Unity -Z
            model.footprintMin = new Vector2(-s.x1, -s.y1);
            model.footprintMax = new Vector2(-s.x0, -s.y0);
            model.floors = Mathf.Max(1, s.floors);
            model.eaves = s.eaves; model.roofRise = s.roofRise; model.apron = s.apron;
            model.length = Mirrored(s.xJoints, s.xSegments, s.xOpen);
            model.width = Mirrored(s.yJoints, s.ySegments, s.yOpen);
            model.height = new GeoRuinAxis(s.zSegments, false, (float[])s.zJoints.Clone());
        }

        // An axis seen from the other side: joints negated and in reverse order, segments reversed.
        static GeoRuinAxis Mirrored(float[] joints, string segments, bool open)
        {
            var mirrored = new float[joints.Length];
            for (int i = 0; i < joints.Length; i++) mirrored[i] = -joints[joints.Length - 1 - i];
            var letters = segments.ToCharArray();
            Array.Reverse(letters);
            return new GeoRuinAxis(new string(letters), open, mirrored);
        }

        // A model without a table entry: its intact box from the spec, centred, and only stretched.
        static void Guess(GeoRuinModel model, JObject entry, JObject roster)
        {
            var intact = roster?["intact"] as JObject;
            var size = entry?["size_m"] as JArray;
            float length = (float?)intact?["lengthM"] ?? (size != null ? (float)size[0] * 0.75f : 10);
            float width = (float?)intact?["widthM"] ?? (size != null ? (float)size[1] * 0.75f : 8);
            float height = (float?)intact?["heightM"] ?? (size != null ? (float)size[2] : 6);
            string roof = ((string)intact?["roof"] ?? "").ToLowerInvariant();
            bool pitched = roof.Contains("gable") || roof.Contains("hip") || roof.Contains("pitch") && !roof.Contains("low-pitch");
            model.footprintMin = new Vector2(-length / 2, -width / 2);
            model.footprintMax = new Vector2(length / 2, width / 2);
            model.floors = Mathf.Max(1, (int?)intact?["floors"] ?? 1);
            model.eaves = pitched ? height * 0.7f : height;
            model.roofRise = pitched ? height * 0.3f : 0;
            model.apron = 3;
            model.length = new GeoRuinAxis(); model.width = new GeoRuinAxis(); model.height = new GeoRuinAxis();
            model.minState = GeoConditionState.Damaged; model.maxState = GeoConditionState.Destroyed;
            model.autoSelect = true; model.keepProportions = false;
            model.tags = new string[0];
            switch ((string)entry?["category"] ?? (string)roster?["category"])
            {
                case "soviet-apartment": case "stalinka-brick": model.categories = new[] { "apartments" }; break;
                case "private-house": model.categories = new[] { "house", "farm" }; break;
                case "small-structure": model.categories = new[] { "shed", "garage" }; break;
                case "commercial": model.categories = new[] { "commercial" }; break;
                case "industrial": model.categories = new[] { "industrial" }; break;
                case "religious": model.categories = new[] { "religious" }; model.autoSelect = false; model.keepProportions = true; break;
                case "public": case "infrastructure": model.categories = new[] { "public" }; break;
                default: model.categories = new string[0]; break;
            }
        }
    }
}
