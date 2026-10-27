using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The editor's generation target (GeoGeneration): a progress bar with Cancel, the editor's texture compressor (DXT1 at its best
    // encoding, or Crunch), prefab instances, and the project's reduced models brought up to date. Registered when the editor loads,
    // so the generator saves worlds as assets as it always did; a world package (GeoWorldGenerator) runs with its own target.
    [InitializeOnLoad]
    public sealed class GeoEditorGeneration : IGeoGenerationTarget
    {
        public static readonly GeoEditorGeneration Target = new GeoEditorGeneration();

        static GeoEditorGeneration()
        {
            GeoGeneration.EditorTarget = Target;
            GeoGeneration.EditorReducedModels = GeoModelReduction.Ensure;
        }

        public bool SavesAssets => true;

        public void Progress(string label, float value)
        {
            if (EditorUtility.DisplayCancelableProgressBar("Geo World Generator", label, value))
                throw new OperationCanceledException("Generation cancelled. Previous world preserved.");
        }

        // DXT1 at its best encoding (the same size as at the normal one, with fewer wrong colours in each block), or Crunched: DXT1
        // with Crunch compression on top, which takes a quarter of the bytes or less in the world's assets and in a build and
        // becomes the same DXT1 when Unity loads it (on the loading thread). Where this editor cannot crunch, the texture is DXT1
        // and the report says so.
        public void CompressSatellite(Texture2D texture, Color32[] pixels, GenerationSettings settings, List<string> notes)
        {
            if (settings.satelliteCompression == GeoSatelliteCompression.Crunched)
            {
                string reason = null;
                try { EditorUtility.CompressTexture(texture, TextureFormat.DXT1Crunched, Mathf.Clamp(settings.satelliteQuality, 1, 100)); }
                catch (Exception e) { reason = e.Message; }
                if (texture.format == TextureFormat.DXT1Crunched) return;
                notes?.Add("Satellite imagery: the texture could not be crunched" + (reason != null ? " (" + reason + ")" : "") + "; it is DXT1.");
                if (texture.format != TextureFormat.RGBA32)
                {
                    texture.Reinitialize(texture.width, texture.height, TextureFormat.RGBA32, true);
                    texture.SetPixels32(pixels); texture.Apply(true);
                }
            }
            EditorUtility.CompressTexture(texture, TextureFormat.DXT1, TextureCompressionQuality.Best);
        }

        public GeoReducedModels ReducedModels(GeoRuinLibrary ruins, GeoHomeLibrary homes) => GeoModelReduction.Ensure(ruins, homes);

        public GameObject Instantiate(GameObject prefab, Transform parent) => (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
    }
}
