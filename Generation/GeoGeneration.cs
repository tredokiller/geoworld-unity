using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // What the generator does differently where it runs. In the editor a world is saved as assets (progress bar, the
    // editor's texture compressor with Crunch, prefab links, the project's reduced models brought up to date); in a player,
    // and for a world package made in the editor (GeoWorldGenerator), everything stays in memory and is written to the
    // package, with what a player can do (Texture2D.Compress, Object.Instantiate, the reduced models of GeoRuntimeAssets).
    public interface IGeoGenerationTarget
    {
        // Saves generated objects as assets (the editor's worlds); false: they are kept in memory for a package.
        bool SavesAssets { get; }
        // Progress of the generation (0-1); throws OperationCanceledException when the generation is to stop.
        void Progress(string label, float value);
        // The satellite texture (mipmapped RGBA32 made from pixels) as the world keeps it: DXT1, or crunched DXT1 in the editor.
        void CompressSatellite(Texture2D texture, Color32[] pixels, GenerationSettings settings, List<string> notes);
        // The reduced copies of the library models a merged city draws (null: the full models).
        GeoReducedModels ReducedModels(GeoRuinLibrary ruins, GeoHomeLibrary homes);
        // A copy of a prefab under parent (a prefab instance in the editor).
        GameObject Instantiate(GameObject prefab, Transform parent);
    }

    public static class GeoGeneration
    {
        static IGeoGenerationTarget current, fallback;
        // The editor's target, registered by GeoWorld.Editor when it loads; in a player the in-memory one.
        public static IGeoGenerationTarget EditorTarget;
        // The editor's reduced models (GeoModelReduction.Ensure), for a package made in the editor.
        public static Func<GeoRuinLibrary, GeoHomeLibrary, GeoReducedModels> EditorReducedModels;

        public static IGeoGenerationTarget Target => current ?? EditorTarget ?? (fallback ??= new GeoMemoryTarget(null, default));

        // Generation within the scope runs with target (one generation at a time on the main thread). An in-memory target also
        // tints the homes' roofs itself (the editor's tints are saved as assets, GeoHomeImporter).
        public static IDisposable Use(IGeoGenerationTarget target)
        {
            var previous = current;
            var roofs = GeoHomes.RoofMaterialProvider;
            current = target;
            if (target is GeoMemoryTarget memory) GeoHomes.RoofMaterialProvider = memory.TintedRoof;
            return new Scope(() => { current = previous; GeoHomes.RoofMaterialProvider = roofs; });
        }

        sealed class Scope : IDisposable
        {
            Action undo;
            public Scope(Action undo) { this.undo = undo; }
            public void Dispose() { undo?.Invoke(); undo = null; }
        }
    }

    // A generation whose objects stay in memory (a player, or a package made in the editor): progress goes to a callback
    // and a cancellation token stops it.
    public sealed class GeoMemoryTarget : IGeoGenerationTarget
    {
        readonly Action<string, float> progress;
        readonly CancellationToken cancel;

        public GeoMemoryTarget(Action<string, float> progress, CancellationToken cancel) { this.progress = progress; this.cancel = cancel; }

        // Materials it made itself (the tinted roofs), to destroy with the generation's other objects.
        public readonly List<Object> created = new List<Object>();
        readonly Dictionary<(Material, Color32), Material> roofs = new Dictionary<(Material, Color32), Material>();

        // A roof material tinted with a colour, as the editor's GeoHomeImporter makes it (there saved as an asset): the roof
        // texture is a neutral grey of about 0.8, so the tint is lifted to match the colour asked for.
        public Material TintedRoof(Material source, Color color)
        {
            if (!source) return source;
            var key = (source, (Color32)color);
            if (roofs.TryGetValue(key, out var found) && found) return found;
            var material = new Material(source) { name = source.name + "_" + ColorUtility.ToHtmlStringRGB(color) };
            var tint = new Color(Mathf.Clamp01(color.r * 1.2f), Mathf.Clamp01(color.g * 1.2f), Mathf.Clamp01(color.b * 1.2f), 1f);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
            roofs[key] = material; created.Add(material);
            return material;
        }

        public bool SavesAssets => false;

        public void Progress(string label, float value)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(label, value);
        }

        // Texture2D.Compress makes DXT1 of a texture without alpha (DXT5 with it): the texture becomes RGB24 first. Crunch
        // needs the editor; a package keeps DXT1 (four times the bytes of crunched, read without decoding).
        public void CompressSatellite(Texture2D texture, Color32[] pixels, GenerationSettings settings, List<string> notes)
        {
            texture.Reinitialize(texture.width, texture.height, TextureFormat.RGB24, true);
            texture.SetPixels32(pixels);
            texture.Apply(true);
            texture.Compress(true);
            if (settings.satelliteCompression == GeoSatelliteCompression.Crunched)
                notes?.Add("Satellite imagery: kept as DXT1 (Crunch needs the editor).");
        }

        public GeoReducedModels ReducedModels(GeoRuinLibrary ruins, GeoHomeLibrary homes)
        {
            if (GeoGeneration.EditorReducedModels != null) return GeoGeneration.EditorReducedModels(ruins, homes);
            return GeoAssets.ReducedModels;
        }

        public GameObject Instantiate(GameObject prefab, Transform parent) => prefab ? Object.Instantiate(prefab, parent) : null;
    }
}
