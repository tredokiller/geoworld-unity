// Вода Geo World (пакет, GeoWaterCore.hlsl) с туманом и облаками Enviro.
// Enviro рисует туман полноэкранным проходом до прозрачных объектов (EnviroURPRenderFeature, BeforeRenderingTransparents):
// дома и террейн его получают, а прозрачная вода — нет, и издалека и с высоты она тёмным пятном выделяется на затуманенной
// земле. Для прозрачных поверхностей Enviro даёт функцию тумана (как в его примере EnviroTransparentSurface) — её и зовём.
// Материал лежит в Resources/GeoWorld/Water (project) — пакет (GeoWaterLook) берёт его вместо своего.
Shader "Game/GeoWorld Water (Enviro fog)"
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
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include_with_pragmas "Assets/Plugins/Enviro 3 - Sky and Weather/Resources/Shader/Includes/FogIncludeHLSL.hlsl"

            // Туман, затем облака (буфер облаков Enviro, _EnviroClouds): облака Enviro тоже накладываются до прозрачных
            // объектов, и без них вода просвечивала сквозь облако, в котором летит дрон.
            #define GEO_WATER_FOG(colour, positionWS, screenUV, fogFactor) ApplyClouds(ApplyFogAndVolumetricLights(colour, screenUV, positionWS, 0), screenUV, positionWS)
            #include "Packages/com.tredokiller.geoworld/Shaders/GeoWaterCore.hlsl"
            ENDHLSL
        }
    }
    FallBack "GeoWorld/Water"
}
