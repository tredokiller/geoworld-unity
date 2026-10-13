using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoWorld.Editor
{
    // Rendering settings for large generated worlds, applied to the render pipeline assets in use:
    //   GPU Resident Drawer   thousands of renderers of the same mesh and material (homes, trees) drawn as instances,
    //                         prepared on the GPU instead of one draw call each on the CPU
    //   GPU occlusion culling buildings hidden behind nearer ones are not drawn
    // The drawer needs the Forward+ renderer and the SRP Batcher (the project's PC renderer has both), and a player
    // keeps its shader variants only with BatchRendererGroup stripping set to Keep All.
    public static class GeoRenderingSetup
    {
        [MenuItem("Tools/Geo World/Set up rendering for large worlds", false, 20)]
        public static void ApplyMenu() => Debug.Log(Apply());

        public static string Apply()
        {
            var assets = new HashSet<RenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline) assets.Add(GraphicsSettings.defaultRenderPipeline);
            if (QualitySettings.renderPipeline) assets.Add(QualitySettings.renderPipeline);
            var done = new List<string>();
            foreach (var asset in assets)
            {
                var so = new SerializedObject(asset);
                var mode = so.FindProperty("m_GPUResidentDrawerMode");
                var occlusion = so.FindProperty("m_GPUResidentDrawerEnableOcclusionCullingInCameras");
                if (mode == null) { done.Add(asset.name + ": no GPU Resident Drawer setting"); continue; }
                mode.intValue = 1; // instanced drawing
                if (occlusion != null) occlusion.boolValue = true;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                done.Add(asset.name + ": GPU Resident Drawer and GPU occlusion culling on");
            }
            // read-only in the API: set in Project Settings > Graphics, here through the settings asset
            var graphics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (graphics.Length > 0)
            {
                var so = new SerializedObject(graphics[0]);
                var stripping = so.FindProperty("m_BrgStripping");
                if (stripping != null) { stripping.intValue = (int)BatchRendererGroupStrippingMode.KeepAll; so.ApplyModifiedProperties(); }
            }
            AssetDatabase.SaveAssets();
            if (EditorGraphicsSettings.batchRendererGroupShaderStrippingMode != BatchRendererGroupStrippingMode.KeepAll)
                done.Add("set Project Settings > Graphics > BatchRendererGroup Variants to Keep All by hand");
            return string.Join("; ", done) + "; BatchRendererGroup variants kept in builds.";
        }
    }
}
