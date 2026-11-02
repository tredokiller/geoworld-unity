// Вода болот GeoWorld: пруды и озёра, у которых берег — болото по карте земель (WaterSurfaces). Чем болото не озеро:
//  - бурая торфяная вода (гуминовые кислоты): дна не видно уже в ладони от кромки, просвета на гребнях нет;
//  - почти без волны: тростник и ряска гасят ветер — только слабая рябь в сильный ветер;
//  - ряска и водоросли пятнами, гуще у берега и на мели, на открытой воде реже — между ними тёмные окна чистой воды;
//  - на воде плёнка: тусклые пятна, где гладь не блестит.
// Общее с другой водой (WaterCore.hlsl): поток винтов (лунка, кольца, полосы, пена; ряску разгоняет), кольца от капель,
// свет URP, туман и облака Enviro. Вода между кочек самого болота — в шейдере террейна (GroundWetness.hlsl).
// Материал — Resources/GeoWorld/Water (swamp), его ставит WaterSurfaces.
Shader "Game/Water/Swamp"
{
    Properties
    {
        [Header(Colour by depth)]
        _ShallowColor ("Shallow colour", Color) = (0.17, 0.14, 0.07, 1)
        _DeepColor ("Deep colour", Color) = (0.04, 0.032, 0.014, 1)
        _ShallowFalloff ("Shallow falloff (multiply, power)", Vector) = (8, 3, 0, 0)
        _WaterAlpha ("Water opacity (multiply, power)", Vector) = (3, 0.6, 0, 0)
        _EdgeFalloff ("Soft shore (multiply, power)", Vector) = (5.19, 0.74, 0, 0)
        _CleanFalloff ("Clean water at the shore (multiply, power)", Vector) = (3, 0.3, 0, 0)
        _Distortion ("Refraction", Range(0, 1)) = 0.2

        [Header(Surface)]
        [Normal][NoScaleOffset] _WaterNormal ("Water normal", 2D) = "bump" {}
        _WaterTiling ("Water normal: metres per tile", Float) = 3.33
        _CalmNormal ("Water normal strength in calm", Range(0, 1)) = 0.02
        _WaterDrift ("Water drift (m/s, x z)", Vector) = (0.004, 0.0025, 0, 0)
        _WindRipples ("Wind ripples (metres per tile, strength at 8 m/s)", Vector) = (3, 0.04, 0, 0)
        _Scum ("Scum film (metres, amount)", Vector) = (6, 0.25, 0, 0)
        _FarNormal ("Far: flatten from (m), keep", Vector) = (30, 0.5, 0, 0)

        [Header(Light)]
        _Smoothness ("Smoothness (water, scum)", Vector) = (0.97, 0.72, 0, 0)
        _Specular ("Specular: deep, shallow, threshold", Vector) = (0.06, 0, 0.35, 0)
        _FarReflection ("Sky reflection far away (from m, keep)", Vector) = (250, 0.55, 0, 0)

        [Header(Duckweed and algae)]
        [NoScaleOffset] _DuckweedTex ("Duckweed (RGB linear, A cover)", 2D) = "black" {}
        [Normal][NoScaleOffset] _DuckweedNormal ("Duckweed normal", 2D) = "bump" {}
        [NoScaleOffset] _AlgaeTex ("Algae (RGB, A cover)", 2D) = "black" {}
        [Normal][NoScaleOffset] _AlgaeNormal ("Algae normal", 2D) = "bump" {}
        [NoScaleOffset] _DetailSmoothness ("Smoothness (G duckweed, A algae)", 2D) = "white" {}
        [NoScaleOffset] _DetailNoise ("Patches noise (G duckweed, A algae)", 2D) = "black" {}
        _Duckweed ("Duckweed: metres per tile, noise metres, noise power, noise multiply", Vector) = (0.33, 3, 4, 2.6)
        _Algae ("Algae: metres per tile, noise metres, noise power, noise multiply", Vector) = (0.125, 1.6, 5, 2)
        _Details ("Duckweed and algae amount", Range(0, 1)) = 1
        _ShoreWeeds ("Weeds thicken where the water is shallower than (m)", Float) = 1.2
        _OpenWeeds ("Weeds on open water (share of the shore amount)", Range(0, 1)) = 0.35

        [Header(Foam)]
        [NoScaleOffset] _FoamTex ("Foam (A)", 2D) = "black" {}
        _FoamTiling ("Foam: metres per tile", Float) = 3

        [Header(Rotor wash and rain)]
        _Ripples ("Rotor wash ripples", Range(0, 3)) = 0.6
        _WashFoam ("Rotor wash foam", Range(0, 2)) = 0.5
        _WashStreaks ("Rotor wash streaks", Range(0, 2)) = 0.5
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
                float4 _WaterDrift, _WindRipples, _Scum, _FarNormal;
                float4 _Smoothness, _Specular, _FarReflection;
                float4 _Duckweed, _Algae;
                half _Details;
                float _ShoreWeeds;
                half _OpenWeeds;
                float _FoamTiling;
                half _Ripples, _WashFoam, _WashStreaks, _Rain;
            CBUFFER_END

            // один сэмплер на все текстуры: у D3D11 их не больше 16 на проход
            TEXTURE2D(_WaterNormal); SAMPLER(sampler_WaterNormal);
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
                    return WaterIceShade(input, v, 0.25);

                float2 p = v.positionWS.xz;
                float t = _Time.y;

                half calm = WaterWashCalm(v.positionWS);
                half washFoam;
                float2 rippleSlope = WaterRippleSlope(v.positionWS, washFoam) * _Ripples;
                // воздух от винтов у воды: где он рябит гладь — тусклый размытый круг, капли дождя в нём тонут
                half washAir = WaterWashAir(v.positionWS);
                half washRough = WaterWashRough(washAir);

                // ряска и водоросли пятнами, гуще у берега и на мели (вертикальная глубина — по лучу и наклону взгляда), окна
                // чистой воды между ними; поток винтов их разгоняет. Маски — до нормалей: в пятне ряски вода спокойна —
                // плёнка глушит рябь, полосы и кольца капель, и тусклый круг вокруг дрона ложится только на чистую воду (иначе
                // листочки, которые почти везде лишь частью закрывают воду, растворялись в отражении неба на десятки метров)
                float2 drift = _WaterDrift.xy * t + _WaterWindRun.xy * 0.002;
                float2 weedUV = (p + drift) / _Duckweed.x;
                float2 algaeUV = (p + drift) / _Algae.x;
                half4 duckweed = 0;
                half4 algae = 0;
                half m1 = 0;
                half m2 = 0;
                half open = 1;

                if (_Details > 0.001)
                {
                    half4 noise1 = SAMPLE_TEXTURE2D(_DetailNoise, sampler_WaterNormal, (p + drift) / _Duckweed.y);
                    half4 noise2 = SAMPLE_TEXTURE2D(_DetailNoise, sampler_WaterNormal, (p + drift) / _Algae.y);
                    duckweed = SAMPLE_TEXTURE2D(_DuckweedTex, sampler_WaterNormal, weedUV);
                    algae = SAMPLE_TEXTURE2D(_AlgaeTex, sampler_WaterNormal, algaeUV);
                    float depthHere = max(0, LinearEyeDepth(SampleSceneDepth(v.screenUV), _ZBufferParams) - v.surfaceDepth) * v.rayScale;
                    half shore = saturate(1 - depthHere * saturate(v.view.y) / max(_ShoreWeeds, 0.1));
                    half amount = _Details * lerp(_OpenWeeds, 1, shore) * (1 - WaterWashClear(v.positionWS));
                    half patch1 = saturate(pow(abs(noise1.g), _Duckweed.z) * _Duckweed.w) * amount;
                    half patch2 = saturate(pow(abs(noise2.a), _Algae.z) * _Algae.w) * amount;
                    m1 = saturate(duckweed.a * 1.5) * patch1;
                    m2 = algae.a * patch2;
                    open = 1 - max(patch1, patch2);
                }

                // ветер почти не доходит: слабая рябь только в сильный ветер
                half wind = saturate(WaterWindSpeed() / 8);
                half3 normalTS = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, (p + drift) / _WaterTiling), _CalmNormal * (1 - calm * 0.85));
                float2 windUV = p / _WindRipples.x - WaterWindScroll(_WindRipples.x, 0.02) + 0.43;
                normalTS = WaterBlend(normalTS, WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, windUV), _WindRipples.y * wind * wind * (1 - calm * 0.85)));

                WaterWashStreaks(p, t, TEXTURE2D_ARGS(_WaterNormal, sampler_WaterNormal), _WashStreaks * open, normalTS, washFoam);
                normalTS = WaterAddSlope(normalTS, (rippleSlope + WaterRainSlope(v.positionWS, v.distance, _WaterRain) * _Rain * (1 - 0.7 * washRough)) * open);

                WaterBelow below = SampleBelow(v, normalTS.xy, _Distortion);

                // торфяная вода: бурая, дна почти не видно
                half shallow = saturate(pow(max(below.depthBelow * _ShallowFalloff.x, 1e-3), -_ShallowFalloff.y));
                half3 colour = lerp(_DeepColor.rgb, _ShallowColor.rgb, shallow);
                half opacity = saturate(pow(saturate(below.depthHere * _WaterAlpha.x), _WaterAlpha.y));
                half clean = saturate(pow(saturate(below.depthBelow * _CleanFalloff.x), _CleanFalloff.y));
                half3 albedo, transmitted;
                WaterBody(below, colour, opacity, clean, albedo, transmitted);
                half3 specular = lerp(_Specular.x, _Specular.y, pow(shallow, _Specular.z));

                // плёнка: тусклые пятна на глади
                half scum = smoothstep(0.45, 0.75, WaterValueNoise((p + drift) / _Scum.x, 31u) * 0.6 + WaterValueNoise((p + drift) / (_Scum.x * 0.3), 32u) * 0.4) * _Scum.y;
                half smoothness = WaterWashSmoothness(lerp(_Smoothness.x, _Smoothness.y, scum), washRough * open);
                albedo *= 1 - scum * 0.25;

                half details = 0;

                if (_Details > 0.001)
                {
                    transmitted *= 1 - max(m1, m2);
                    half duckweedSmooth = SAMPLE_TEXTURE2D(_DetailSmoothness, sampler_WaterNormal, weedUV).g;
                    half algaeSmooth = SAMPLE_TEXTURE2D(_DetailSmoothness, sampler_WaterNormal, algaeUV).a;
                    // альбедо ряски импортировано линейным (sRGB выключен у NM): переводим
                    albedo = lerp(albedo, SRGBToLinear(duckweed.rgb), m1);
                    albedo = lerp(albedo, algae.rgb * 0.8, m2);
                    smoothness = lerp(lerp(smoothness, duckweedSmooth, m1), algaeSmooth, m2);
                    specular = lerp(lerp(specular, 0, m1), algae.rgb * 0.092, m2);
                    half3 duckweedN = UnpackNormal(SAMPLE_TEXTURE2D(_DuckweedNormal, sampler_WaterNormal, weedUV));
                    half3 algaeN = UnpackNormal(SAMPLE_TEXTURE2D(_AlgaeNormal, sampler_WaterNormal, algaeUV));
                    normalTS = normalize(lerp(lerp(normalTS, duckweedN, m1), algaeN, m2));
                    details = max(m1, m2);
                }

                normalTS = normalize(lerp(normalTS, normalTS * half3(_FarNormal.y, _FarNormal.y, 1), saturate(sqrt(v.distance / max(_FarNormal.x, 1)))));

                half foamPattern = SAMPLE_TEXTURE2D(_FoamTex, sampler_WaterNormal, (p + drift) / _FoamTiling).a;
                // пена от винтов на болотной воде бурая
                half foam = saturate(WaterWashLace(washFoam * _WashFoam, foamPattern)) * (1 - details);
                albedo = lerp(albedo, half3(0.45, 0.4, 0.3), foam);
                smoothness = lerp(smoothness, 0.5, foam);
                transmitted *= 1 - foam;

                half edge = saturate(pow(saturate(below.depthHere * _EdgeFalloff.x), _EdgeFalloff.y));
                return WaterShade(input, v, normalTS, albedo, specular, smoothness, 1, transmitted, edge, opacity, details, foam, _FarReflection.xy);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
