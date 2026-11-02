// Пруд и озеро GeoWorld («Water | <id>», плоские). Вода NatureManufacture (R.A.M 2019) в своём шейдере: цвет по глубине,
// дно сквозь мель с преломлением, чистая вода у кромки, просвет на гребнях, ряска и водоросли (карты NM). Чем озеро не
// река:
//  - стоячая: никуда не течёт, в штиль — почти зеркало;
//  - рябь от ветра игры: бежит по ветру, сильнее при сильном ветре, пятнами порывов (между ними гладь), гладь тусклее;
//  - вода прозрачнее речной, зеленоватая;
//  - ряска и водоросли — у берегов, на мели, а не посреди озера.
// Общее с другой водой (WaterCore.hlsl): поток винтов (лунка, кольца, полосы, пена; ряску разгоняет), кольца от капель,
// свет URP, туман и облака Enviro. Материал — Resources/GeoWorld/Water (lakes), его ставит WaterSurfaces.
Shader "Game/Water/Lake"
{
    Properties
    {
        [Header(Colour by depth)]
        _ShallowColor ("Shallow colour", Color) = (0.21, 0.29, 0.24, 1)
        _DeepColor ("Deep colour", Color) = (0.035, 0.07, 0.06, 1)
        _ShallowFalloff ("Shallow falloff (multiply, power)", Vector) = (4, 4.48, 0, 0)
        _WaterAlpha ("Water opacity (multiply, power)", Vector) = (0.55, 0.7, 0, 0)
        _EdgeFalloff ("Soft shore (multiply, power)", Vector) = (5.19, 0.74, 0, 0)
        _CleanFalloff ("Clean water at the shore (multiply, power)", Vector) = (1.5, 0.2, 0, 0)
        _Distortion ("Refraction", Range(0, 1)) = 0.427

        [Header(Wind)]
        [Normal][NoScaleOffset] _WaterNormal ("Water normal", 2D) = "bump" {}
        [NoScaleOffset] _WaterHeight ("Water height (A)", 2D) = "black" {}
        _WaterTiling ("Water normal: metres per tile", Float) = 3.33
        _CalmNormal ("Water normal strength in calm", Range(0, 1)) = 0.025
        _WaterDrift ("Water drift (m/s, x z)", Vector) = (0.01, 0.006, 0, 0)
        _SmallWaves ("Small wind waves (metres per tile, strength at 8 m/s)", Vector) = (4, 0.22, 0, 0)
        _LargeWaves ("Large wind waves (metres per tile, strength at 8 m/s)", Vector) = (11, 0.16, 0, 0)
        _Gusts ("Gust patches (metres, contrast)", Vector) = (40, 0.7, 0, 0)
        _FarNormal ("Far: flatten from (m), keep", Vector) = (30, 0.5, 0, 0)
        _Translucency ("Light through wave crests (hardness, power, multiply, fade m)", Vector) = (2.5, 0.97, 0.05, 30)

        [Header(Light)]
        _Smoothness ("Smoothness (calm, windy)", Vector) = (0.985, 0.95, 0, 0)
        _Specular ("Specular: deep, shallow, threshold (F0 of water 0.02)", Vector) = (0.02, 0, 0.35, 0)
        _FarReflection ("Sky reflection far away (from m, keep)", Vector) = (250, 0.85, 0, 0)

        [Header(Duckweed and algae)]
        [NoScaleOffset] _DuckweedTex ("Duckweed (RGB linear, A cover)", 2D) = "black" {}
        [Normal][NoScaleOffset] _DuckweedNormal ("Duckweed normal", 2D) = "bump" {}
        [NoScaleOffset] _AlgaeTex ("Algae (RGB, A cover)", 2D) = "black" {}
        [Normal][NoScaleOffset] _AlgaeNormal ("Algae normal", 2D) = "bump" {}
        [NoScaleOffset] _DetailSmoothness ("Smoothness (G duckweed, A algae)", 2D) = "white" {}
        [NoScaleOffset] _DetailNoise ("Patches noise (G duckweed, A algae)", 2D) = "black" {}
        _Duckweed ("Duckweed: metres per tile, noise metres, noise power, noise multiply", Vector) = (0.33, 2, 3.99, 5)
        _Algae ("Algae: metres per tile, noise metres, noise power, noise multiply", Vector) = (0.125, 1.25, 6.38, 1.7)
        _Details ("Duckweed and algae amount", Range(0, 1)) = 1
        _ShoreWeeds ("Weeds grow where the water is shallower than (m)", Float) = 1.5

        [Header(Foam)]
        [NoScaleOffset] _FoamTex ("Foam (A)", 2D) = "black" {}
        _FoamTiling ("Foam: metres per tile", Float) = 3

        [Header(Rotor wash and rain)]
        _Ripples ("Rotor wash ripples", Range(0, 3)) = 1
        _WashFoam ("Rotor wash foam", Range(0, 2)) = 1
        _WashStreaks ("Rotor wash streaks", Range(0, 2)) = 1
        _Rain ("Rain rings", Range(0, 3)) = 1
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
                float _WaterTiling;
                half _CalmNormal;
                float4 _WaterDrift, _SmallWaves, _LargeWaves, _Gusts, _FarNormal, _Translucency;
                float4 _Smoothness, _Specular, _FarReflection;
                float4 _Duckweed, _Algae;
                half _Details;
                float _ShoreWeeds;
                float _FoamTiling;
                half _Ripples, _WashFoam, _WashStreaks, _Rain;
            CBUFFER_END

            // один сэмплер на все текстуры: у D3D11 их не больше 16 на проход
            TEXTURE2D(_WaterNormal); SAMPLER(sampler_WaterNormal);
            TEXTURE2D(_WaterHeight);
            TEXTURE2D(_DuckweedTex);
            TEXTURE2D(_DuckweedNormal);
            TEXTURE2D(_AlgaeTex);
            TEXTURE2D(_AlgaeNormal);
            TEXTURE2D(_DetailSmoothness);
            TEXTURE2D(_DetailNoise);
            TEXTURE2D(_FoamTex);

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                WaterView v = GetWaterView(input);
                // зимой стоячая вода подо льдом (WaterCore.hlsl)
                [branch] if (_Season.w > 0.5)
                    return WaterIceShade(input, v, 0);

                float2 p = v.positionWS.xz;
                float t = _Time.y;

                half calm = WaterWashCalm(v.positionWS);
                half washFoam;
                float2 rippleSlope = WaterRippleSlope(v.positionWS, washFoam) * _Ripples;
                // воздух от винтов у воды: где он рябит гладь — тусклый размытый круг, капли дождя в нём тонут
                half washAir = WaterWashAir(v.positionWS);
                half washRough = WaterWashRough(washAir);

                // ветер: рябь бежит по ветру пятнами порывов (между ними гладь), под дроном спокойнее
                half wind = saturate(WaterWindSpeed() / 8);
                float2 run = _WaterWindRun.xy;
                half gust = WaterValueNoise((p - run * 0.5) / _Gusts.x, 21u) * 0.7 + WaterValueNoise((p - run * 0.7) / (_Gusts.x * 0.37), 22u) * 0.3;
                gust = smoothstep(0.5 - 0.5 * _Gusts.y, 0.5 + 0.5 * _Gusts.y, gust);
                half windy = wind * lerp(0.35, 1.25, gust) * (1 - calm * 0.85);

                float2 drift = _WaterDrift.xy * t;
                half3 waterN = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, (p + drift) / _WaterTiling), _CalmNormal * (1 - calm * 0.85) + 0.03 * windy);
                float2 smallUV = p / _SmallWaves.x - WaterWindScroll(_SmallWaves.x, 0.04);
                float2 largeUV = p / _LargeWaves.x - WaterWindScroll(_LargeWaves.x, 0.06) + 0.21;
                half3 smallN = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, smallUV), _SmallWaves.y * windy);
                half3 largeN = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, largeUV), _LargeWaves.y * windy);
                half3 normalTS = WaterBlend(waterN, WaterBlend(smallN, largeN));

                WaterWashStreaks(p, t, TEXTURE2D_ARGS(_WaterNormal, sampler_WaterNormal), _WashStreaks, normalTS, washFoam);
                normalTS = WaterAddSlope(normalTS, rippleSlope + WaterRainSlope(v.positionWS, v.distance, _WaterRain) * _Rain * (1 - 0.7 * washRough));

                WaterBelow below = SampleBelow(v, normalTS.xy, _Distortion);

                half shallow = saturate(pow(max(below.depthBelow * _ShallowFalloff.x, 1e-3), -_ShallowFalloff.y));
                half3 colour = lerp(_DeepColor.rgb, _ShallowColor.rgb, shallow);
                half crestHeight = SAMPLE_TEXTURE2D(_WaterHeight, sampler_WaterNormal, largeUV).a * windy +
                                   SAMPLE_TEXTURE2D(_WaterHeight, sampler_WaterNormal, (p + drift) / _WaterTiling).a;
                half crest = saturate(lerp(pow(abs(crestHeight * 1.98 * _Translucency.x), _Translucency.y) * _Translucency.z, 0,
                                           saturate(v.distance / max(_Translucency.w, 1))));
                colour = lerp(colour, _ShallowColor.rgb, crest);

                half opacity = saturate(pow(saturate(below.depthHere * _WaterAlpha.x), _WaterAlpha.y));
                half clean = saturate(pow(saturate(below.depthBelow * _CleanFalloff.x), _CleanFalloff.y));
                half3 albedo, transmitted;
                WaterBody(below, colour, opacity, clean, albedo, transmitted);
                half3 specular = lerp(_Specular.x, _Specular.y, pow(shallow, _Specular.z));
                half smoothness = WaterWashSmoothness(lerp(_Smoothness.x, _Smoothness.y, wind), washRough);

                // ряска и водоросли пятнами по шуму — у берегов, на мели (вертикальная глубина — по лучу и наклону
                // взгляда); поток винтов их разгоняет
                half details = 0;
                half shore = saturate(1 - below.depthHere * saturate(v.view.y) / max(_ShoreWeeds, 0.1));

                if (_Details * shore > 0.001)
                {
                    half4 noise1 = SAMPLE_TEXTURE2D(_DetailNoise, sampler_WaterNormal, p / _Duckweed.y);
                    half4 noise2 = SAMPLE_TEXTURE2D(_DetailNoise, sampler_WaterNormal, p / _Algae.y);
                    float2 weedUV = p / _Duckweed.x + drift * 0.3;
                    float2 algaeUV = p / _Algae.x + drift * 0.3;
                    half4 duckweed = SAMPLE_TEXTURE2D(_DuckweedTex, sampler_WaterNormal, weedUV);
                    half4 algae = SAMPLE_TEXTURE2D(_AlgaeTex, sampler_WaterNormal, algaeUV);
                    half amount = _Details * shore * (1 - WaterWashClear(v.positionWS));
                    half m1 = duckweed.a * duckweed.a * saturate(pow(abs(noise1.g), _Duckweed.z) * _Duckweed.w) * amount;
                    half m2 = algae.a * algae.a * saturate(pow(abs(noise2.a), _Algae.z) * _Algae.w) * amount;
                    transmitted *= 1 - max(m1, m2);
                    half duckweedSmooth = SAMPLE_TEXTURE2D(_DetailSmoothness, sampler_WaterNormal, weedUV).g;
                    half algaeSmooth = SAMPLE_TEXTURE2D(_DetailSmoothness, sampler_WaterNormal, algaeUV).a;
                    // альбедо ряски импортировано линейным (sRGB выключен у NM): переводим, как цвет водорослей
                    albedo = lerp(albedo, SRGBToLinear(duckweed.rgb), m1);
                    albedo = lerp(albedo, algae.rgb, m2);
                    smoothness = lerp(lerp(smoothness, duckweedSmooth, m1), algaeSmooth, m2);
                    specular = lerp(lerp(specular, 0, m1), algae.rgb * 0.092, m2);
                    half3 duckweedN = UnpackNormal(SAMPLE_TEXTURE2D(_DuckweedNormal, sampler_WaterNormal, weedUV));
                    half3 algaeN = UnpackNormal(SAMPLE_TEXTURE2D(_AlgaeNormal, sampler_WaterNormal, algaeUV));
                    normalTS = normalize(lerp(lerp(normalTS, duckweedN, m1), algaeN, m2));
                    details = max(m1, m2);
                }

                normalTS = normalize(lerp(normalTS, normalTS * half3(_FarNormal.y, _FarNormal.y, 1), saturate(sqrt(v.distance / max(_FarNormal.x, 1)))));
                // мелкая рябь вдали размывает небо, а не исчезает (WaterFarSmoothness); ряска и плёнка — без неё
                smoothness = lerp(WaterFarSmoothness(smoothness, max(length(v.dpdx), length(v.dpdy)), _SmallWaves.x,
                    (_CalmNormal + (0.03 + _SmallWaves.y) * windy) * 1.5), smoothness, details);

                half foamPattern = SAMPLE_TEXTURE2D(_FoamTex, sampler_WaterNormal, (p + drift * 0.5) / _FoamTiling).a;
                half foam = saturate(WaterWashLace(washFoam * _WashFoam, foamPattern)) * (1 - details);
                albedo = lerp(albedo, 0.8, foam);
                smoothness = lerp(smoothness, 0.6, foam);
                transmitted *= 1 - foam;

                half edge = saturate(pow(saturate(below.depthHere * _EdgeFalloff.x), _EdgeFalloff.y));
                return WaterShade(input, v, normalTS, albedo, specular, smoothness, 1, transmitted, edge, opacity, details, foam, _FarReflection.xy);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
