using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Library homes of a battlefield after the battle: the war around each home (GeoPropDamage.War — craters around it,
    // damaged buildings near it; a home that is itself damaged at least 0.6) picks one of three wear levels, and its
    // materials become copies on the GeoWorld/Home shader (URP Lit with the wear on top: dust, old stains, windows blown
    // out or boarded, sheets gone from the roof), one copy per library material and level. The models are not changed;
    // a home out of the fighting keeps the library's own materials, and a world without a battlefield is not touched.
    public sealed class GeoHomeWear
    {
        public const string ShaderName = "GeoWorld/Home";
        static readonly float[] Wear = { 0, 0.35f, 0.65f, 0.95f };

        readonly GeoPropDamage.War war;
        readonly Shader shader;
        readonly Action<Object> save;
        readonly Dictionary<(Material, int, bool), Material> made = new Dictionary<(Material, int, bool), Material>();
        readonly int[] counts = new int[4];

        public GeoHomeWear(GeoPropDamage.War war, Action<Object> save)
        {
            this.war = war;
            this.save = save;
            shader = war != null && !war.None ? GeoAssets.Shader(ShaderName) : null;
        }

        public bool Active => shader != null;

        // The wear level of a home at `at` (0 none, 1..3).
        public int Level(Vector2 at, GeoConditionState state)
        {
            if (!Active) return 0;
            float w = war.At(at, out _, out _);
            if (state == GeoConditionState.Damaged) w = Mathf.Max(w, 0.6f);
            int level = w < 0.12f ? 0 : w < 0.4f ? 1 : w < 0.7f ? 2 : 3;
            counts[level]++;
            return level;
        }

        // The material a home part draws with at that level: the library's own at 0, else its worn copy. roof: the roof
        // surface (sheets gone); eaves: the walls' top above the model's origin (stains run down from there).
        public Material Material(Material source, int level, bool roof, float eaves)
        {
            if (level <= 0 || !source || !Active) return source;
            var key = (source, level, roof);
            if (made.TryGetValue(key, out var material)) return material;
            material = new Material(source) { name = source.name + " (war " + level + ")" };
            material.shader = shader;
            material.shaderKeywords = source.shaderKeywords;
            material.renderQueue = source.renderQueue;
            material.enableInstancing = source.enableInstancing;
            material.SetFloat("_Parallax", Wear[level]);
            material.SetFloat("_ClearCoatMask", roof ? 1 : 0);
            material.SetFloat("_ClearCoatSmoothness", roof ? 0 : Mathf.Max(0, eaves) / 10f);
            save?.Invoke(material);
            made[key] = material;
            return material;
        }

        public string Note() =>
            Active && counts[1] + counts[2] + counts[3] > 0
                ? $"Homes after the battle: {counts[1]} lightly, {counts[2]} more, {counts[3]} heavily worn (dust, old stains, windows blown out or boarded, sheets gone from the roofs); {counts[0]} out of the fighting."
                : null;
    }
}
