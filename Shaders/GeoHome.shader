// A library home after the battle (GeoCityBuilder: homes of a battlefield where the fighting was, GeoHomeWear): URP Lit
// with a wear layer on top of the model's own atlas — the models are not changed. Walls: grime and dust, dust thick at
// the foot of the walls, old dark stains run down from the eaves (no fresh soot: the battle is over); the windows of a
// home blown out (dark), boarded with OSB, covered with film or only dirty, one look per home. Roofs: missing and
// cracked sheets in a grid of sheets on the roof's slopes.
//
// SRP Batcher: the material is URP Lit's (LitInput.hlsl), and the wear is carried in properties URP Lit leaves unused
// without their keywords — _Parallax the wear (0 none, 1 heaviest; no _PARALLAXMAP), _ClearCoatMask 1 for a roof surface,
// _ClearCoatSmoothness the eaves' height above the model's origin in tens of metres — so the shadow, depth and meta
// passes are URP Lit's own. The forward pass is URP Lit's (LitForwardPass.hlsl) with HomeWear between the surface and
// the lighting.
Shader "GeoWorld/Home"
{
    Properties
    {
        _WorkflowMode("WorkflowMode", Float) = 1.0
        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _SmoothnessTextureChannel("Smoothness texture channel", Float) = 0
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}
        _SpecColor("Specular", Color) = (0.2, 0.2, 0.2)
        _SpecGlossMap("Specular", 2D) = "white" {}
        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _EnvironmentReflections("Environment Reflections", Float) = 1.0
        _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}
        _Parallax("War wear (0 none, 1 heaviest)", Range(0, 1)) = 0
        _ParallaxMap("Height Map", 2D) = "black" {}
        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}
        [HDR] _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}
        _DetailMask("Detail Mask", 2D) = "white" {}
        _DetailAlbedoMapScale("Scale", Range(0.0, 2.0)) = 1.0
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "linearGrey" {}
        _DetailNormalMapScale("Scale", Range(0.0, 2.0)) = 1.0
        [Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}
        _ClearCoatMask("Roof surface (1)", Float) = 0.0
        _ClearCoatSmoothness("Eaves height (tens of metres)", Float) = 0.0
        _Surface("__surface", Float) = 0.0
        _Blend("__blend", Float) = 0.0
        _Cull("__cull", Float) = 2.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector] _AddPrecomputedVelocity("_AddPrecomputedVelocity", Float) = 0.0
        [HideInInspector] _XRMotionVectorsPass("_XRMotionVectorsPass", Float) = 1.0
        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        _QueueOffset("Queue offset", Float) = 0.0
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _GlossMapScale("Smoothness", Float) = 0.0
        [HideInInspector] _Glossiness("Smoothness", Float) = 0.0
        [HideInInspector] _GlossyReflections("EnvironmentReflections", Float) = 0.0
        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "UniversalMaterialType" = "Lit" "IgnoreProjector" = "True" }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend[_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha]
            ZWrite[_ZWrite]
            Cull[_Cull]
            AlphaToMask[_AlphaToMask]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex LitPassVertex
            #pragma fragment HomePassFragment

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ _ALPHAPREMULTIPLY_ON _ALPHAMODULATE_ON
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"

            float HomeHash(float2 cell, uint salt)
            {
                return GenerateHashedRandomFloat(uint3(asuint((int)cell.x), asuint((int)cell.y), salt));
            }

            float HomeNoise(float2 p, uint salt)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(HomeHash(i, salt), HomeHash(i + float2(1, 0), salt), u.x),
                            lerp(HomeHash(i + float2(0, 1), salt), HomeHash(i + float2(1, 1), salt), u.x), u.y);
            }

            half HomeLuminance(half3 c) { return dot(c, half3(0.2126, 0.7152, 0.0722)); }

            // The wear of a home after the battle, on the surface before it is lit (see the header).
            void HomeWear(inout SurfaceData s, float3 positionWS, float3 normalWS)
            {
                half wear = saturate(_Parallax);
                [branch] if (wear < 0.02)
                    return;

                float3 origin = TransformObjectToWorld(float3(0, 0, 0));
                float4x4 toWorld = GetObjectToWorldMatrix();
                float3 scale = float3(length(toWorld._m00_m10_m20), length(toWorld._m01_m11_m21), length(toWorld._m02_m12_m22));
                // the home's own draw, the same at every distance
                float home = GenerateHashedRandomFloat(uint3(asuint((int)floor(origin.x * 4.0)), asuint((int)floor(origin.z * 4.0)), 211u));
                float h = positionWS.y - origin.y;

                [branch] if (_ClearCoatMask > 0.5)
                {
                    // a roof: a grid of sheets on its slopes (in the home's own axes, metres), some gone, some cracked
                    if (normalWS.y < 0.2)
                        return;
                    float3 local = TransformWorldToObject(positionWS) * scale;
                    float2 cell = floor(float2(local.x / 1.13, local.z / 1.75) + float2(home * 7.0, home * 3.0));
                    float r = HomeHash(cell, 223u);
                    s.albedo *= lerp(1.0, 0.88, wear);
                    if (r < 0.16 * wear)
                    {
                        // a hole into the dark attic
                        s.albedo *= 0.05;
                        s.smoothness = 0;
                        s.occlusion *= 0.25;
                        s.specular *= 0.1;
                    }
                    else if (r < 0.28 * wear)
                    {
                        s.albedo *= 0.72 + 0.1 * HomeHash(cell, 227u);
                        s.smoothness *= 0.5;
                    }
                    return;
                }

                // walls: the colour before the wear decides where the glass is
                half3 painted = s.albedo;
                half lum = HomeLuminance(painted);
                half grey = HomeLuminance(painted);
                s.albedo = lerp(painted, grey.xxx, 0.15 * wear) * lerp(1.0, 0.85, wear);

                float2 across = normalize(float2(-normalWS.z, normalWS.x) + 1e-5);
                float along = dot(positionWS.xz, across);

                // dust thick at the foot of the walls, thinning out up to a metre or two
                float top = 0.9 + 0.7 * HomeNoise(float2(along / 1.7, home * 13.0), 229u);
                half dust = wear * (1.0 - smoothstep(0.1, top, h)) * (0.55 + 0.45 * HomeNoise(float2(along * 1.3, h * 2.0), 233u));
                const half3 dustTone = half3(0.30, 0.285, 0.255);
                s.albedo = lerp(s.albedo, dustTone * (0.85 + 0.3 * HomeNoise(float2(along * 4.0, h * 4.0), 239u)), dust * 0.8);
                s.smoothness = lerp(s.smoothness, 0.04, dust);

                // old stains run down from the eaves
                float eaves = _ClearCoatSmoothness * 10.0 * scale.y;
                if (eaves > 0.5)
                {
                    float below = eaves - h;
                    float streak = smoothstep(0.45, 0.9, HomeNoise(float2(along * 2.6 + home * 17.0, 0.5), 241u));
                    float run = 0.6 + 1.6 * HomeNoise(float2(along * 0.9, home * 5.0), 251u);
                    half stain = wear * 0.55 * streak * (1.0 - smoothstep(0.0, run, below)) * step(-0.05, below);
                    s.albedo = lerp(s.albedo, half3(0.11, 0.10, 0.09), stain * 0.6);
                }

                // the glass of the windows: cool, not very saturated, neither black nor white, on a wall
                half spread = max(painted.r, max(painted.g, painted.b)) - min(painted.r, min(painted.g, painted.b));
                half glass = smoothstep(0.015, 0.05, painted.b - painted.r) * (1.0 - smoothstep(0.18, 0.26, spread))
                           * smoothstep(0.05, 0.09, lum) * (1.0 - smoothstep(0.7, 0.85, lum)) * step(abs(normalWS.y), 0.5);
                [branch] if (glass > 0.001)
                {
                    half3 pane;
                    half paneSmooth;
                    if (home < 0.45 * wear)
                    {
                        // blown out: the dark room behind
                        pane = painted * 0.08;
                        paneSmooth = 0.1;
                    }
                    else if (home < 0.8 * wear)
                    {
                        // boarded with OSB, weathered: strands in two sizes
                        float2 board = float2(along, h);
                        half strands = 0.75 + 0.3 * HomeNoise(board * 23.0, 257u) + 0.2 * HomeNoise(board * 6.0, 263u);
                        pane = half3(0.36, 0.26, 0.14) * strands;
                        paneSmooth = 0.05;
                    }
                    else if (home < 0.9 * wear)
                    {
                        // film over the frame
                        pane = half3(0.42, 0.44, 0.46);
                        paneSmooth = 0.35;
                    }
                    else
                    {
                        pane = painted * 0.6;
                        paneSmooth = s.smoothness;
                    }
                    s.albedo = lerp(s.albedo, pane, glass);
                    s.smoothness = lerp(s.smoothness, paneSmooth, glass);
                }
            }

            // LitPassFragment (URP 17.3) with the wear between the surface and the lighting.
            void HomePassFragment(
                Varyings input
                , out half4 outColor : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                SurfaceData surfaceData;
                InitializeStandardLitSurfaceData(input.uv, surfaceData);
                HomeWear(surfaceData, input.positionWS, normalize(input.normalWS));

            #ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(input.positionCS);
            #endif

                InputData inputData;
                InitializeInputData(input, surfaceData.normalTS, inputData);
                SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

            #if defined(_DBUFFER)
                ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
            #endif

                InitializeBakedGIData(input, inputData);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent(_Surface));
                outColor = color;

            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
        UsePass "Universal Render Pipeline/Lit/DEPTHONLY"
        UsePass "Universal Render Pipeline/Lit/DEPTHNORMALS"
        UsePass "Universal Render Pipeline/Lit/META"
        UsePass "Universal Render Pipeline/Lit/MOTIONVECTORS"
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
