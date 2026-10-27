// Water for Geo World (URP): a simple, cheap water surface for lakes and rivers.
//  - colour from the water's own depth under the surface (the camera's depth texture): light near the shore, dark in the
//    middle, and the shore line fades out softly instead of ending in a hard edge against the ground;
//  - small waves (a few sine waves in world space, no textures) that move and catch the sun, and fade out with distance so
//    that far water is calm instead of flickering;
//  - the sky's colour at grazing angles (fresnel), the main light's shadow and the scene's fog (a project's own fog:
//    GeoWaterCore.hlsl).
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

            #include "GeoWaterCore.hlsl"
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Simple Lit"
}
