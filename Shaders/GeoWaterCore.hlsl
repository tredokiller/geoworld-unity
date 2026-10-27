// The water of Geo World (GeoWater.shader), as an include: a project whose fog is drawn by its sky asset as a full-screen pass
// before the transparent objects (Enviro, for one) fogs the ground and not the water, which then stands out from far away and
// from high above. Its own water shader defines GEO_WATER_FOG(colour, positionWS, screenUV, fogFactor) with that asset's fog
// for transparent surfaces, includes this file and puts its material in a Resources folder at GeoWaterLook.ProjectResource.
// Without GEO_WATER_FOG the water takes the pipeline's own fog, as before.
#ifndef GEO_WATER_CORE_INCLUDED
#define GEO_WATER_CORE_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

#ifndef GEO_WATER_FOG
#define GEO_WATER_FOG(colour, positionWS, screenUV, fogFactor) MixFog(colour, fogFactor)
#endif

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

    colour = GEO_WATER_FOG(colour, input.positionWS, screenUV, input.fogFactor);
    return half4(colour, alpha);
}

#endif
