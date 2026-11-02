// Открытая вода GeoWorld: море и большие водохранилища (WaterSurfaces — по площади). Чем море не озеро:
//  - волнение от ветра игры трёх масштабов — зыбь, ветровые волны, мелкая рябь; гребни бегут по ветру, высота растёт
//    с ветром (в штиль остаётся пологая зыбь);
//  - барашки: с ~5 м/с на гребнях пена, к шторму её всё больше;
//  - прибой: у берега на мели полосы пены идут к берегу;
//  - глубокий сине-зелёный цвет, свет насквозь через гребни волн, небо в отражении и вдали.
// Общее с другой водой (WaterCore.hlsl): поток винтов (лунка, кольца, полосы, пена), кольца от капель, свет URP, туман и
// облака Enviro. Материал — Resources/GeoWorld/Water (sea), его ставит WaterSurfaces.
Shader "Game/Water/Sea"
{
    Properties
    {
        [Header(Colour by depth)]
        _ShallowColor ("Shallow colour", Color) = (0.1, 0.27, 0.25, 1)
        _DeepColor ("Deep colour", Color) = (0.012, 0.045, 0.06, 1)
        _ShallowFalloff ("Shallow falloff (multiply, power)", Vector) = (1.2, 2, 0, 0)
        _WaterAlpha ("Water opacity (multiply, power)", Vector) = (0.35, 0.8, 0, 0)
        _EdgeFalloff ("Soft shore (multiply, power)", Vector) = (3, 0.74, 0, 0)
        _CleanFalloff ("Clean water at the shore (multiply, power)", Vector) = (1, 0.3, 0, 0)
        _Distortion ("Refraction", Range(0, 1)) = 0.5

        [Header(Waves)]
        [Normal][NoScaleOffset] _WaterNormal ("Water normal", 2D) = "bump" {}
        [NoScaleOffset] _WaterHeight ("Water height (A)", 2D) = "black" {}
        _Swell ("Swell (metres per tile, strength at 10 m/s)", Vector) = (55, 0.35, 0, 0)
        _Waves ("Wind waves (metres per tile, strength at 10 m/s)", Vector) = (14, 0.3, 0, 0)
        _Chop ("Chop (metres per tile, strength at 10 m/s)", Vector) = (3.5, 0.2, 0, 0)
        _CalmSwell ("Swell left in calm", Range(0, 1)) = 0.25
        _FarNormal ("Far: flatten from (m), keep", Vector) = (60, 0.6, 0, 0)
        _Translucency ("Light through wave crests (colour multiply, fade m)", Vector) = (0.6, 150, 0, 0)

        [Header(Foam)]
        [NoScaleOffset] _FoamTex ("Foam (A)", 2D) = "black" {}
        _FoamTiling ("Foam: metres per tile", Float) = 4
        _Whitecaps ("Whitecaps: from wind (m/s), full at (m/s), amount", Vector) = (5, 14, 0.8, 0)
        _Surf ("Surf: depth (m), band (m), speed (bands/s), amount", Vector) = (2.5, 0.9, 0.12, 0.8)

        [Header(Light)]
        _Smoothness ("Smoothness (calm, stormy)", Vector) = (0.97, 0.88, 0, 0)
        _Specular ("Specular: deep, shallow, threshold", Vector) = (0.094, 0.02, 0.35, 0)
        _FarReflection ("Sky reflection far away (from m, keep)", Vector) = (400, 0.8, 0, 0)

        [Header(Rotor wash and rain)]
        _Ripples ("Rotor wash ripples", Range(0, 3)) = 1
        _WashFoam ("Rotor wash foam", Range(0, 2)) = 1
        _WashStreaks ("Rotor wash streaks", Range(0, 2)) = 1
        _Rain ("Rain rings", Range(0, 3)) = 0.7
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
            #pragma target 3.5
            #pragma vertex WaterVertex
            #pragma fragment Fragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_instancing

            #define _SPECULAR_SETUP 1
            #define _SURFACE_TYPE_TRANSPARENT 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include_with_pragmas "Assets/Plugins/Enviro 3 - Sky and Weather/Resources/Shader/Includes/FogIncludeHLSL.hlsl"
            #include "WaterCore.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor, _DeepColor;
                float4 _ShallowFalloff, _WaterAlpha, _EdgeFalloff, _CleanFalloff;
                half _Distortion;
                float4 _Swell, _Waves, _Chop;
                half _CalmSwell;
                float4 _FarNormal, _Translucency;
                float _FoamTiling;
                float4 _Whitecaps, _Surf;
                float4 _Smoothness, _Specular, _FarReflection;
                half _Ripples, _WashFoam, _WashStreaks, _Rain;
            CBUFFER_END

            // один сэмплер на все текстуры: у D3D11 их не больше 16 на проход
            TEXTURE2D(_WaterNormal); SAMPLER(sampler_WaterNormal);
            TEXTURE2D(_WaterHeight);
            TEXTURE2D(_FoamTex);

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                WaterView v = GetWaterView(input);
                // зимой стоячая вода подо льдом (WaterCore.hlsl)
                [branch] if (_Season.w > 0.5)
                    return WaterIceShade(input, v, -0.1);

                float2 p = v.positionWS.xz;
                float t = _Time.y;

                half calm = WaterWashCalm(v.positionWS);
                half washFoam;
                float2 rippleSlope = WaterRippleSlope(v.positionWS, washFoam) * _Ripples;
                // воздух от винтов у воды: где он рябит гладь — тусклый размытый круг, капли дождя в нём тонут
                half washAir = WaterWashAir(v.positionWS);
                half washRough = WaterWashRough(washAir);

                // волнение: зыбь, ветровые волны и рябь бегут по ветру, сильнее с ветром; в штиль — пологая зыбь
                float windSpeed = WaterWindSpeed();
                half wind = saturate(windSpeed / 10);
                float2 swellUV = p / _Swell.x - WaterWindScroll(_Swell.x, 0.12) - float2(0.013, 0.007) * t;
                float2 wavesUV = p / _Waves.x - WaterWindScroll(_Waves.x, 0.07) + 0.31;
                float2 chopUV = p / _Chop.x - WaterWindScroll(_Chop.x, 0.035) + 0.67;
                half under = 1 - calm * 0.85;
                // зыбь (волна в десятки метров) поток винтов не гладит — только ветровые волны и рябь
                half3 swellN = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, swellUV), _Swell.y * lerp(_CalmSwell, 1, wind));
                half3 wavesN = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, wavesUV), _Waves.y * (0.1 + 0.9 * wind) * under);
                half3 chopN = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, chopUV), _Chop.y * (0.15 + 0.85 * wind) * under);
                half3 normalTS = WaterBlend(swellN, WaterBlend(wavesN, chopN));

                // высота гребней (для барашков и света насквозь)
                half height = SAMPLE_TEXTURE2D(_WaterHeight, sampler_WaterNormal, swellUV).a * 0.45 +
                              SAMPLE_TEXTURE2D(_WaterHeight, sampler_WaterNormal, wavesUV).a * 0.4 +
                              SAMPLE_TEXTURE2D(_WaterHeight, sampler_WaterNormal, chopUV).a * 0.15;

                WaterWashStreaks(p, t, TEXTURE2D_ARGS(_WaterNormal, sampler_WaterNormal), _WashStreaks, normalTS, washFoam);
                normalTS = WaterAddSlope(normalTS, rippleSlope + WaterRainSlope(v.positionWS, v.distance, _WaterRain) * _Rain * (1 - 0.7 * washRough));

                WaterBelow below = SampleBelow(v, normalTS.xy, _Distortion);

                half shallow = saturate(pow(max(below.depthBelow * _ShallowFalloff.x, 1e-3), -_ShallowFalloff.y));
                half3 colour = lerp(_DeepColor.rgb, _ShallowColor.rgb, shallow);
                // свет насквозь через гребни: бирюзовее на высоких
                half crest = saturate(height * 1.6 - 0.5) * (0.3 + 0.7 * wind) * saturate(1 - v.distance / max(_Translucency.y, 1));
                colour += _ShallowColor.rgb * crest * _Translucency.x;

                half opacity = saturate(pow(saturate(below.depthHere * _WaterAlpha.x), _WaterAlpha.y));
                half clean = saturate(pow(saturate(below.depthBelow * _CleanFalloff.x), _CleanFalloff.y));
                half3 albedo, transmitted;
                WaterBody(below, colour, opacity, clean, albedo, transmitted);
                half3 specular = lerp(_Specular.x, _Specular.y, pow(shallow, _Specular.z));
                half smoothness = WaterWashSmoothness(lerp(_Smoothness.x, _Smoothness.y, wind), washRough);

                normalTS = normalize(lerp(normalTS, normalTS * half3(_FarNormal.y, _FarNormal.y, 1), saturate(sqrt(v.distance / max(_FarNormal.x, 1)))));
                // рябь вдали размывает небо, а не исчезает (WaterFarSmoothness)
                smoothness = WaterFarSmoothness(smoothness, max(length(v.dpdx), length(v.dpdy)), _Chop.x, _Chop.y * (0.15 + 0.85 * wind) * under * 1.5);

                // барашки на гребнях с ~5 м/с, к шторму больше
                half foamPattern = SAMPLE_TEXTURE2D(_FoamTex, sampler_WaterNormal, p / _FoamTiling - WaterWindScroll(_FoamTiling, 0.07)).a;
                half caps = saturate((windSpeed - _Whitecaps.x) / max(_Whitecaps.y - _Whitecaps.x, 0.1));
                half whitecaps = smoothstep(0.9 - 0.3 * caps, 1.0 - 0.3 * caps, height + foamPattern * 0.3) * caps * _Whitecaps.z;

                // прибой: на мели (вертикальная глубина — по лучу и наклону взгляда) полосы пены идут к берегу
                float depth = below.depthHere * saturate(v.view.y);
                half band = 0.5 + 0.5 * sin((depth / max(_Surf.y, 0.1) + t * _Surf.z) * 6.2831853);
                half surf = saturate(1 - depth / max(_Surf.x, 0.1)) * smoothstep(0.55, 0.95, band) * smoothstep(0.25, 0.7, foamPattern) * _Surf.w * (0.4 + 0.6 * wind);

                half foam = saturate(max(whitecaps, surf) + WaterWashLace(washFoam * _WashFoam, foamPattern));
                albedo = lerp(albedo, 0.85, foam);
                smoothness = lerp(smoothness, 0.55, foam);
                transmitted *= 1 - foam;

                half edge = saturate(pow(saturate(below.depthHere * _EdgeFalloff.x), _EdgeFalloff.y));
                return WaterShade(input, v, normalTS, albedo, specular, smoothness, 1, transmitted, edge, opacity, 0, foam, _FarReflection.xy);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
