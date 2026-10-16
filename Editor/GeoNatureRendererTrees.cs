using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Trees drawn by Nature Renderer (Visual Design Cafe), where a project has it: the trees become the terrain's own
    // trees and the terrain gets a Nature Renderer component, which draws them instanced with its own culling and
    // distances. Geo World does not reference Nature Renderer: it finds the component by name, so projects without it
    // compile and keep Geo World's instanced trees. Nature Renderer only draws materials whose shader includes its
    // procedural instancing code, which comes with Nature Renderer; so the tree shader with that code (and the tree
    // prefabs that use it) are written into the project when they are first needed, from TreeShader below.
    public static class GeoNatureRendererTrees
    {
        public const string Folder = GeoPaths.Project + "/Nature Renderer";
        const string DefaultInclude = "Assets/Visual Design Cafe/Nature Renderer/Shader Includes/Nature Renderer.templatex";

        // Nature Renderer's component, or null where the project does not have it.
        public static Type Component => TypeCache.GetTypesDerivedFrom<MonoBehaviour>()
            .FirstOrDefault(t => t.Name == "NatureRenderer" && (t.Namespace ?? "").StartsWith("VisualDesignCafe", StringComparison.Ordinal));

        // asked on every repaint of the window and the Inspector: found once per domain (importing or removing Nature
        // Renderer reloads it)
        static bool? installed;
        public static bool Installed => installed ??= Component != null && Include() != null;

        // Nature Renderer's shader code: in its documented folder, or wherever it was moved.
        public static string Include()
        {
            if (File.Exists(DefaultInclude)) return DefaultInclude;
            foreach (var guid in AssetDatabase.FindAssets("Nature Renderer"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("/Nature Renderer.templatex", StringComparison.Ordinal)) return path;
            }
            return null;
        }

        // The trees on the terrain, drawn by Nature Renderer (a terrain without trees gets no component). Returns how
        // many were planted.
        public static int Plant(Terrain terrain, IList<PropPlacement> trees, int seed)
        {
            if (Component == null) throw new InvalidOperationException("Nature Renderer is not in this project.");
            int planted = GeoTerrainTrees.PlantOnTerrain(terrain, trees, seed, Prefabs());
            if (planted > 0) Prepare(terrain);
            return planted;
        }

        // A terrain whose trees Nature Renderer draws: its component, and the detail map it needs.
        public static void Prepare(Terrain terrain)
        {
            var component = Component ?? throw new InvalidOperationException("Nature Renderer is not in this project.");
            // Nature Renderer cuts a terrain into patches by its detail map and turns down a terrain without one (Geo World
            // paints no grass, so its terrains have none): patches of about 125 m, with no detail layers to store
            var data = terrain.terrainData;
            if (data.detailResolution == 0)
            {
                int patches = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(data.size.x, data.size.z) / 125f), 1, 128);
                data.SetDetailResolution(patches * 16, 16);
                EditorUtility.SetDirty(data);
            }
            var natureRenderer = terrain.GetComponent(component);
            if (!natureRenderer) natureRenderer = terrain.gameObject.AddComponent(component);
            // switched off for a moment while a tile loads (GeoNatureRendererStart): Unity's own tree drawing stays off
            component.GetProperty("EnableUnityRendererWhenDisabled")?.SetValue(natureRenderer, false);
            if (!terrain.GetComponent<GeoNatureRendererStart>()) terrain.gameObject.AddComponent<GeoNatureRendererStart>();
        }

        // Your tree prefabs' materials whose shader Nature Renderer cannot draw (it needs its procedural instancing),
        // as notes: Nature Renderer makes a copy of a shader that it can draw (right-click the shader, Nature Renderer >
        // Patch Shader).
        public static void CheckPrefabs(IEnumerable<GameObject> prefabs, List<string> notes)
        {
            foreach (var prefab in prefabs)
            {
                var unsupported = prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials)
                    .Where(m => m && !Drawable(m)).Select(m => m.name).Distinct().ToList();
                if (unsupported.Count > 0)
                    notes.Add("Tree prefab " + prefab.name + ": Nature Renderer cannot draw " + string.Join(", ", unsupported) +
                              " (the shader needs Nature Renderer's instancing: right-click the shader > Nature Renderer > Patch Shader, and use the patched copy).");
            }
        }

        // A terrain's Nature Renderer, when its trees are drawn by Geo World again.
        public static void Remove(Terrain terrain)
        {
            var component = Component;
            if (component != null && terrain.GetComponent(component) is Object found && found) Object.DestroyImmediate(found);
            var start = terrain.GetComponent<GeoNatureRendererStart>();
            if (start) Object.DestroyImmediate(start);
        }

        // Can Nature Renderer draw the material? Its shader has procedural instancing (Nature Renderer's code adds the
        // PROCEDURAL_INSTANCING_ON variant, in code or through its Shader Graph node), or the tag of a patched shader.
        public static bool Drawable(Material material) =>
            Array.IndexOf(material.shader.keywordSpace.keywordNames, "PROCEDURAL_INSTANCING_ON") >= 0 ||
            string.Equals(material.GetTag("NatureRendererInstancing", false), "True", StringComparison.OrdinalIgnoreCase);

        // Geo World's four trees with materials of the Nature Renderer tree shader, made in Folder when missing.
        public static GameObject[] Prefabs()
        {
            string include = Include() ?? throw new InvalidOperationException("Nature Renderer is not in this project.");
            var shader = WriteShader(Folder + "/Geo tree (Nature Renderer).shader", include);
            var sources = GeoTerrainTrees.Prefabs();
            var prefabs = new GameObject[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                string name = sources[i].name + " (Nature Renderer)", path = Folder + "/" + name + ".prefab";
                prefabs[i] = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefabs[i]) continue;
                var source = sources[i].GetComponent<MeshRenderer>();
                var go = new GameObject(name);
                try
                {
                    go.AddComponent<MeshFilter>().sharedMesh = sources[i].GetComponent<MeshFilter>().sharedMesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = source.sharedMaterials.Select(m => Material(m, shader)).ToArray();
                    float cull = sources[i].GetComponent<LODGroup>().GetLODs()[0].screenRelativeTransitionHeight;
                    go.AddComponent<LODGroup>().SetLODs(new[] { new LOD(cull, new Renderer[] { renderer }) });
                    prefabs[i] = PrefabUtility.SaveAsPrefabAsset(go, path);
                }
                finally { Object.DestroyImmediate(go); }
            }
            return prefabs;
        }

        // A copy of a tree material with the Nature Renderer shader and the same colour.
        static Material Material(Material source, Shader shader)
        {
            string path = Folder + "/" + source.name + " (Nature Renderer).mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material) return material;
            material = new Material(shader) { name = source.name + " (Nature Renderer)", enableInstancing = true };
            material.SetColor("_BaseColor", source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.color);
            if (source.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", source.GetFloat("_Smoothness"));
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // The tree shader including the given file, at path (rewritten when the include moved).
        public static Shader WriteShader(string path, string include)
        {
            string text = TreeShader.Replace("{INCLUDE}", include);
            string folder = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
            }
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                File.WriteAllText(path, text);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }
            return AssetDatabase.LoadAssetAtPath<Shader>(path);
        }

        // Geo World's low-poly tree for URP: a colour, the main light with its shadows, ambient light and fog, and the
        // shadow, depth and depth-normals passes. Every pass has Nature Renderer's procedural instancing (the tag, the
        // include and the pragma its documentation asks for).
        const string TreeShader = @"// Written by Geo World for Nature Renderer: Geo World's tree with Nature Renderer's procedural instancing.
Shader ""GeoWorld/Tree (Nature Renderer)""
{
    Properties
    {
        _BaseColor(""Colour"", Color) = (1, 1, 1, 1)
        _Smoothness(""Smoothness"", Range(0, 1)) = 0.05
    }
    SubShader
    {
        Tags { ""RenderType"" = ""Opaque"" ""Queue"" = ""Geometry"" ""RenderPipeline"" = ""UniversalPipeline"" ""NatureRendererInstancing"" = ""True"" }

        HLSLINCLUDE
        #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            half _Smoothness;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name ""ForwardLit""
            Tags { ""LightMode"" = ""UniversalForward"" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl""
            #include ""{INCLUDE}""
            #pragma instancing_options procedural:SetupNatureRenderer

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float4 screen : TEXCOORD2; half fog : TEXCOORD3; };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.screen = ComputeScreenPos(output.positionCS);
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float3 normal = normalize(input.normalWS);
            #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                float4 shadowCoord = input.screen;
            #else
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
            #endif
                Light light = GetMainLight(shadowCoord);
                half3 lit = SampleSH(normal) + light.color * (saturate(dot(normal, light.direction)) * light.shadowAttenuation * light.distanceAttenuation);
                return half4(MixFog(_BaseColor.rgb * lit, input.fog), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name ""ShadowCaster""
            Tags { ""LightMode"" = ""ShadowCaster"" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl""
            #include ""{INCLUDE}""
            #pragma instancing_options procedural:SetupNatureRenderer

            float3 _LightDirection;
            float3 _LightPosition;
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };

            float4 Vertex(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 direction = normalize(_LightPosition - positionWS);
            #else
                float3 direction = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, direction));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            half4 Fragment() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name ""DepthOnly""
            Tags { ""LightMode"" = ""DepthOnly"" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            #include ""{INCLUDE}""
            #pragma instancing_options procedural:SetupNatureRenderer

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };

            float4 Vertex(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return TransformObjectToHClip(input.positionOS.xyz);
            }

            half Fragment() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name ""DepthNormals""
            Tags { ""LightMode"" = ""DepthNormals"" }
            ZWrite On
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_instancing
            #include ""{INCLUDE}""
            #pragma instancing_options procedural:SetupNatureRenderer

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target { return half4(NormalizeNormalPerPixel(input.normalWS), 0); }
            ENDHLSL
        }
    }
}
";
    }

    // Settings for Nature Renderer are shown only where the project has it.
    [CustomPropertyDrawer(typeof(GeoNatureRendererOnlyAttribute))]
    public class GeoNatureRendererOnlyDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            GeoNatureRendererTrees.Installed ? EditorGUI.GetPropertyHeight(property, label, true) : -EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (GeoNatureRendererTrees.Installed) EditorGUI.PropertyField(position, property, label, true);
        }
    }
}
