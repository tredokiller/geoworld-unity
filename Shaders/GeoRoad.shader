// A road surface that blends into the ground: lit like URP Lit (sun, shadows, ambient, fog, normal map), but transparent,
// fading out over the last metre or so of its edges along a slightly broken line, so it lies on the terrain (or on the
// satellite imagery draped over it) instead of ending in a hard border.
// The road mesh carries its edges in TexCoord1: x = metres left of the centreline, y = half the road's width
// (GeoMeshes.RoadEdges / JunctionEdges). A mesh without them (y = 0) is drawn without fading.
Shader "GeoWorld/Road"
{
    Properties
    {
        [MainTexture] _BaseMap ("Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap ("Normal map", 2D) = "bump" {}
        _BumpScale ("Normal strength", Float) = 1
        _Smoothness ("Smoothness", Range(0, 1)) = 0.12
        _Opacity ("Opacity", Range(0, 1)) = 0.95
        _EdgeFade ("Edge fade (metres)", Range(0, 6)) = 1.4
        _EdgeNoise ("Edge break-up (metres)", Range(0, 3)) = 0.6
        _NoiseSize ("Break-up size (metres)", Range(0.5, 30)) = 3.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent-50" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                half4 _BaseColor;
                half _BumpScale;
                half _Smoothness;
                half _Opacity;
                half _EdgeFade;
                half _EdgeNoise;
                half _NoiseSize;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 edge : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 edge : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half3 normalWS : TEXCOORD3;
                float4 shadowCoord : TEXCOORD4;
                half fogFactor : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.edge = input.edge;
                output.shadowCoord = GetShadowCoord(position);
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3 - 2 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), u.x), lerp(Hash(i + float2(0, 1)), Hash(i + 1), u.x), u.y);
            }

            // Normal mapping without mesh tangents (the road meshes have none): the tangent frame comes from the screen-space
            // derivatives of the position and the texture coordinates.
            half3 PerturbNormal(float3 normal, float3 position, float2 uv, half3 normalTS)
            {
                float3 dp1 = ddx(position), dp2 = ddy(position);
                float2 duv1 = ddx(uv), duv2 = ddy(uv);
                float3 dp2perp = cross(dp2, normal), dp1perp = cross(normal, dp1);
                float3 tangent = dp2perp * duv1.x + dp1perp * duv2.x;
                float3 bitangent = dp2perp * duv1.y + dp1perp * duv2.y;
                float scale = rsqrt(max(1e-12, max(dot(tangent, tangent), dot(bitangent, bitangent))));
                return normalize(mul(normalTS, float3x3(tangent * scale, bitangent * scale, normal)));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = TRANSFORM_TEX(input.uv, _BaseMap);
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _BaseColor;
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, TRANSFORM_TEX(input.uv, _BumpMap)), _BumpScale);
                float3 normalWS = normalize(input.normalWS);
                normalWS = PerturbNormal(normalWS, input.positionWS, uv, normalTS);

                // metres inside the edge, the edge broken up by two octaves of noise so it is not a ruled line
                float2 p = input.positionWS.xz / max(0.5, _NoiseSize);
                float noise = ValueNoise(p) * 0.65 + ValueNoise(p * 2.7 + 17.3) * 0.35;
                float inside = input.edge.y - abs(input.edge.x) + (noise - 0.5) * 2 * _EdgeNoise;
                // a narrow lane keeps a solid middle: the fade takes at most about a third of its width on each side
                half fade = smoothstep(0, max(0.001, min(_EdgeFade, input.edge.y * 0.7)), inside);
                fade = input.edge.y > 0.001 ? fade : 1;
                half alpha = saturate(albedo.a * _Opacity * fade);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                // per pixel, as URP Lit does with cascades: a coordinate from the vertices would be interpolated across
                // cascade borders on these long triangles and land in the wrong shadow map tile
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    inputData.shadowCoord = input.shadowCoord;
                #else
                    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo.rgb;
                surface.alpha = alpha;
                surface.metallic = 0;
                surface.specular = 0;
                surface.smoothness = _Smoothness;
                surface.normalTS = normalTS;
                surface.occlusion = 1;

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                color.a = alpha;
                return color;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
