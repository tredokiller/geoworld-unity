using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // A hand-built, intact building from the "intact" model kit (FBX/GLB). The model root has these children:
    //   Body            walls, doors, windows, porch ... (always shown)
    //   RoofFixed       roofs that never change (porch canopy, annex)
    //   Roof_<variant>  gabled_along, gabled_across, hipped, half_hipped, skillion, flat  (exactly ONE is shown)
    // Every renderer has two material slots: 0 = the baked body atlas, 1 = the roof (a tiling texture times a colour).
    //
    // ApplyRoof() takes the "roof" block of a building from the extractor's export (GeoRoofInfo) and
    //   - picks the variant from shape / renderAs and ridge (along / across the long side),
    //   - scales the roof height so the ridge sits `rise` metres above the eaves,
    //   - turns a skillion so it drains towards slopeBearing,
    //   - swaps the roof material by `material` (metal, clay_tiles, bitumen, asbestos_cement, slate ...) and tints it with `color`.
    // Nothing is invented: a value the block does not have leaves the model's own default in place.
    [DisallowMultipleComponent]
    public class GeoHomeModel : MonoBehaviour
    {
        public const string RoofPrefix = "Roof_";
        [Tooltip("Roof materials by kind (metal_tile, metal_sheet, asbestos_cement, clay_tiles, slate, bitumen, shingles, thatch). Optional: without it only the colour changes.")]
        public GeoRoofMaterialSet roofMaterials;
        [Tooltip("Variant shown when the roof block says nothing usable.")]
        public string defaultVariant = "gabled_along";
        [Tooltip("Height of the wall tops above the model's origin, metres (from <name>_meta.json: roof.eaveZ).")]
        public float eaveHeight = 2.8f;
        [Tooltip("Ridge height above the eaves in the authored model, metres (meta: roof.rise).")]
        public float authoredRise = 2.2f;
        [Tooltip("The roof texture is a neutral grey of about 0.8 brightness; this lifts the tint so the result matches the colour asked for.")]
        public float tintBoost = 1.2f;
        [Tooltip("Authored footprint (meta: roof.rect) used by FitTo.")]
        public float authoredLength = 12f, authoredWidth = 7.5f;
        [HideInInspector] public string appliedVariant;

        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();

        public Transform Variant(string name) => transform.Find(RoofPrefix + name);

        public IEnumerable<Transform> Variants()
        {
            foreach (Transform c in transform)
                if (c.name.StartsWith(RoofPrefix, StringComparison.Ordinal)) yield return c;
        }

        // roof shape names in the export (OSM roof:shape plus renderAs) -> variant of the kit
        public static string VariantFor(string shape, string renderAs, string ridge, string fallback)
        {
            string s = Norm(shape), r = Norm(renderAs);
            string v = Map(s, ridge);
            if (v == null) v = Map(r, ridge);
            return v ?? fallback;
        }

        static string Norm(string s) => string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

        static string Map(string s, string ridge)
        {
            switch (s)
            {
                case "flat": return "flat";
                case "gabled": case "gable": case "saltbox": return ridge == "across" ? "gabled_across" : "gabled_along";
                case "hipped": case "hip": case "pyramidal": case "mansard": case "gambrel": return "hipped";
                case "half_hipped": case "half_hip": case "dutch_gabled": case "gablet": return "half_hipped";
                case "skillion": case "shed": case "lean_to": case "mono_pitch": return "skillion";
                default: return null;
            }
        }

        public void ApplyRoof(GeoRoofInfo roof) { ApplyRoof(roof, null); }

        // buildingYawDegrees is only needed to turn a skillion towards roof.slopeBearing (the building's Y rotation as exported)
        public void ApplyRoof(GeoRoofInfo roof, float? buildingYawDegrees)
        {
            if (roof == null) { Show(defaultVariant); return; }
            string v = VariantFor(roof.shape, roof.renderAs, roof.ridge, defaultVariant);
            var t = Show(v);
            if (t == null) return;
            if (GeoRoofInfo.Has(roof.rise) && authoredRise > 0.05f && v != "flat") ScaleRise(t, Mathf.Clamp(roof.rise, 0.3f, 8f) / authoredRise);
            if (v == "skillion" && GeoRoofInfo.Has(roof.slopeBearing)) TurnSkillion(t, roof.slopeBearing);
            ApplyMaterial(roof.material, roof.color);
        }

        // Shows exactly one variant; returns it (or null when the model has no such variant)
        public Transform Show(string variant)
        {
            Transform shown = null;
            foreach (var c in Variants())
            {
                bool on = c.name == RoofPrefix + variant;
                c.gameObject.SetActive(on);
                if (on) shown = c;
            }
            if (shown == null && variant != defaultVariant) return Show(defaultVariant);
            appliedVariant = shown != null ? variant : null;
            return shown;
        }

        // Scales the roof about the eave line so the ridge ends up `factor` times as high above the eaves.
        public void ScaleRise(Transform roof, float factor)
        {
            var s = roof.localScale; s.y = factor; roof.localScale = s;
            var p = roof.localPosition; p.y = eaveHeight * (1 - factor); roof.localPosition = p;
        }

        // The authored skillion drains one way; find which way from its mesh and flip it 180 degrees if the bearing wants the other
        public void TurnSkillion(Transform roof, float slopeBearing)
        {
            var mf = roof.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            var verts = mf.sharedMesh.vertices;
            if (verts.Length == 0) return;
            Vector3 lo = verts[0], hi = verts[0];
            foreach (var v in verts) { if (v.y < lo.y) lo = v; if (v.y > hi.y) hi = v; }
            Vector3 downLocal = new Vector3(lo.x - hi.x, 0, lo.z - hi.z);
            if (downLocal.sqrMagnitude < 1e-4f) return;
            Vector3 down = roof.TransformDirection(downLocal).normalized;
            float cur = Mathf.Atan2(down.x, down.z) * Mathf.Rad2Deg;          // bearing clockwise from north (+Z)
            if (Mathf.Abs(Mathf.DeltaAngle(cur, slopeBearing)) > 90f)
                roof.RotateAround(transform.position, Vector3.up, 180f);
        }

        public void ApplyMaterial(string appMaterial, string hexColor)
        {
            Color color = Color.white; bool hasColor = !string.IsNullOrEmpty(hexColor) && ColorUtility.TryParseHtmlString(hexColor, out color);
            Material kind = roofMaterials != null ? roofMaterials.For(appMaterial) : null;
            if (kind == null && !hasColor) return;
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                if (mats.Length < 2) continue;
                var baseMat = kind != null ? kind : mats[1];
                mats[1] = hasColor ? Tinted(baseMat, color) : baseMat;
                r.sharedMaterials = mats;
            }
        }

        // one shared material per (roof material, colour): keeps SRP-Batcher batching, no per-instance copies
        Material Tinted(Material source, Color color)
        {
            Color tint = new Color(Mathf.Clamp01(color.r * tintBoost), Mathf.Clamp01(color.g * tintBoost), Mathf.Clamp01(color.b * tintBoost), 1f);
            string key = source.GetInstanceID() + "#" + ColorUtility.ToHtmlStringRGB(tint);
            if (cache.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(source) { name = source.name + " " + ColorUtility.ToHtmlStringRGB(color), hideFlags = HideFlags.DontSave };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
            if (m.HasProperty("_Color")) m.SetColor("_Color", tint);
            cache[key] = m;
            return m;
        }

        // Stretches the body to an oriented box taken from the export (length along the long axis). Heights stay as authored.
        public void FitTo(float length, float width, float maxStretch = 1.35f)
        {
            if (authoredLength < 0.1f || authoredWidth < 0.1f) return;
            float sx = Mathf.Clamp(length / authoredLength, 1f / maxStretch, maxStretch);
            float sz = Mathf.Clamp(width / authoredWidth, 1f / maxStretch, maxStretch);
            transform.localScale = new Vector3(sx, 1f, sz);
        }
    }
}
