// Water for Geo World (URP): a simple, cheap water surface for lakes and rivers.
//  - colour from the water's own depth under the surface (the camera's depth texture): light near the shore, dark in the
//    middle, and the shore line fades out softly instead of ending in a hard edge against the ground;
//  - small waves (a few sine waves in world space, no textures) that move and catch the sun, and fade out with distance so
//    that far water is calm instead of flickering;
//  - the sky's colour at grazing angles (fresnel), the main light's shadow and the scene's fog.
// Needs the pipeline asset's Depth Texture for the depth colour and the soft shore; without it the water is drawn as deep.
Shader "GeoWorld/Water"
{
    Properties
    {
        _ShallowColor ("Shallow colour", Color) = (0.22, 0.45, 0.41, 1)
        _DeepColor ("Deep colour", Color) = (0.06, 0.21, 0.23, 1)
        _DepthRange ("Depth to the deep colour (m)", Float) = 2.5
        _ShoreFade ("Shore fade (m of depth)", Float) = 0.35
        _Opacity ("Opacity of deep water", Range(0, 1)) = 0.93
        _WaveSize ("Wave length (m)", Float) = 6
        _WaveSpeed ("Wave speed", Float) = 0.7
        _WaveStrength ("Wave strength", Range(0, 1)) = 0.14
        _WaveFade ("Waves fade out by (m)", Float) = 350
        _Gloss ("Sun glint sharpness", Float) = 220
        _Glint ("Sun glint strength", Range(0, 3)) = 1.2
        _Reflection ("Sky at grazing angles", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-50" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Water"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor, _DeepColor;
                float _DepthRange, _ShoreFade, _Opacity, _WaveSize, _WaveSpeed, _WaveStrength, _WaveFade, _Gloss, _Glint, _Reflection;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.screenPos = ComputeScreenPos(position.positionCS);
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            // The slope of a few sine waves in world space (x, z): a normal that moves, without textures.
            float2 WaveSlope(float2 p, float t)
            {
                float k = 6.2831853 / max(_WaveSize, 0.1);
                float2 slope = 0;
                // four directions and lengths, so the pattern does not repeat visibly
                const float2 d0 = float2(0.86, 0.50), d1 = float2(-0.42, 0.91), d2 = float2(0.17, -0.98), d3 = float2(-0.95, -0.30);
                slope += d0 * cos(dot(p, d0) * k * 1.00 + t * 1.10) * 1.00;
                slope += d1 * cos(dot(p, d1) * k * 1.73 + t * 1.45) * 0.55;
                slope += d2 * cos(dot(p, d2) * k * 2.61 + t * 1.90) * 0.35;
                slope += d3 * cos(dot(p, d3) * k * 4.07 + t * 2.40) * 0.20;
                return slope;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 toCamera = GetWorldSpaceViewDir(input.positionWS);
                float distance = length(toCamera);
                float3 view = toCamera / max(distance, 1e-4);

                // waves, calmer with distance (far water would only shimmer) and at grazing angles (where waves seen edge-on
                // pile up into streaks)
                float strength = _WaveStrength * saturate(1 - distance / max(_WaveFade, 1)) * saturate(abs(view.y) * 3);
                float2 slope = WaveSlope(input.positionWS.xz, _Time.y * _WaveSpeed) * strength;
                float3 up = normalize(input.normalWS);
                float3 normal = normalize(up + float3(-slope.x, 0, -slope.y));

                // the depth of the water under this point, along the view ray and straight down
                float2 screenUV = input.screenPos.xy / input.screenPos.w;
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float surfaceDepth = input.screenPos.w;
                float alongRay = max(0, sceneDepth - surfaceDepth);
                float depth = alongRay * saturate(abs(view.y) + 0.05);

                float deepness = saturate(depth / max(_DepthRange, 0.01));
                half3 water = lerp(_ShallowColor.rgb, _DeepColor.rgb, deepness);

                // light: the sun (with its shadow) and the sky's ambient light
                Light sun = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half shadow = sun.shadowAttenuation * sun.distanceAttenuation;
                half3 ambient = SampleSH(up);
                half3 colour = water * (ambient + sun.color * saturate(dot(up, sun.direction)) * shadow * 0.55);

                // the sky at grazing angles, and the sun's glint on the waves
                half fresnel = pow(1 - saturate(dot(normal, view)), 5);
                half3 sky = SampleSH(reflect(-view, normal));
                colour = lerp(colour, sky, saturate(fresnel * _Reflection));
                float3 halfway = normalize(sun.direction + view);
                colour += sun.color * shadow * _Glint * pow(saturate(dot(normal, halfway)), _Gloss);

                // see-through at the shore, nearly opaque where deep; more opaque at grazing angles
                half alpha = saturate(depth / max(_ShoreFade, 0.01)) * lerp(0.55, _Opacity, deepness);
                alpha = saturate(alpha + fresnel * 0.35);

                colour = MixFog(colour, input.fogFactor);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Simple Lit"
}
