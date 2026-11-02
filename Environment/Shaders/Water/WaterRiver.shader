// Река GeoWorld («Water | rivers», одна поверхность на тайл с уклоном по течению). Вода NatureManufacture (R.A.M 2019),
// перенесённая в свой шейдер: цвет по глубине вдоль луча, дно сквозь мель с преломлением (копия кадра), чистая вода у
// кромки, просвет на гребнях, блик и небо в отражении. Своё — то, чем река не озеро:
//  - течёт: карта нормалей сносится по потоку в UV3 меша (м/с, мир xz; WaterSurfaces считает его по осям рек GeoWorld —
//    вниз по течению, на стрежне быстрее, у берегов медленнее) двумя фазами со сдвигом в полцикла;
//  - мутная, зеленовато-бурая (взвесь), дно видно только у самого берега;
//  - на быстрине у берегов и на перекатах пена, её сносит течение;
//  - ветер игры добавляет мелкую рябь поверх течения, пятнами порывов, которые бегут по ветру;
//  - с высоты она не ровное зеркало: у берегов затишье (гладь отражает берег), вдоль течения полосы глади (плёнка там, где
//    сходятся струи — длинные, извилистые, рвутся), посередине рябь от течения и ветра; гладь отражает небо чётко и
//    светлее, рябь — размыто и тусклее (оси реки — UV2 меша, WaterSurfaces); вдали мелкая рябь не пропадает, а размывает
//    небо (WaterFarSmoothness).
// Общее с другой водой (WaterCore.hlsl): поток винтов (лунка, кольца, полосы, пена), кольца от капель, свет URP, туман и
// облака Enviro. Материал — Resources/GeoWorld/Water (project): его пакет GeoWorld (GeoWaterLook) отдаёт всей воде тайлов,
// а прудам, болотам и морю свои материалы ставит WaterSurfaces.
Shader "Game/Water/River"
{
    Properties
    {
        [Header(Colour by depth)]
        _ShallowColor ("Shallow colour", Color) = (0.26, 0.28, 0.18, 1)
        _DeepColor ("Deep colour", Color) = (0.08, 0.085, 0.052, 1)
        _ShallowFalloff ("Shallow falloff (multiply, power)", Vector) = (4, 4.48, 0, 0)
        _WaterAlpha ("Turbidity (multiply, power)", Vector) = (2.2, 0.65, 0, 0)
        _EdgeFalloff ("Soft shore (multiply, power)", Vector) = (5.19, 0.74, 0, 0)
        _CleanFalloff ("Clean water at the shore (multiply, power)", Vector) = (1.5, 0.2, 0, 0)
        _Distortion ("Refraction", Range(0, 1)) = 0.35

        [Header(Current)]
        [Normal][NoScaleOffset] _WaterNormal ("Water normal", 2D) = "bump" {}
        [NoScaleOffset] _WaterHeight ("Water height (A)", 2D) = "black" {}
        _WaterTiling ("Current normal: metres per tile", Float) = 4
        _FlowNormal ("Current normal strength (slow, fast)", Vector) = (0.06, 0.16, 0, 0)
        _FlowCycle ("Current cycle (s)", Float) = 1.6
        _StillDrift ("Drift where there is no current (m/s, x z)", Vector) = (0.02, 0.012, 0, 0)
        _FarNormal ("Far: flatten from (m), keep", Vector) = (30, 0.5, 0, 0)
        _Translucency ("Light through wave crests (hardness, power, multiply, fade m)", Vector) = (2.5, 0.97, 0.05, 30)

        [Header(Wind)]
        _WindRipples ("Wind ripples (metres per tile, strength at 8 m/s)", Vector) = (2.5, 0.1, 0, 0)
        _Gusts ("Gust patches (metres, contrast)", Vector) = (40, 0.7, 0, 0)

        [Header(Surface from above)]
        _Slicks ("Slicks along the current (spacing m, dash length m, share of lanes)", Vector) = (9, 60, 0.45, 0)
        _Shelter ("Calm water at the banks (from, to: share of the half-width)", Vector) = (0.6, 0.95, 0, 0)

        [Header(Light)]
        _Smoothness ("Smoothness (calm water)", Range(0, 1)) = 0.95
        _RuffledSmoothness ("Smoothness where rippled", Range(0, 1)) = 0.82
        _RuffledReflection ("Reflection where rippled", Range(0, 1)) = 0.68
        _Specular ("Specular: deep, shallow, threshold (F0 of water 0.02)", Vector) = (0.02, 0, 0.35, 0)
        _FarReflection ("Sky reflection far away (from m, keep)", Vector) = (250, 0.85, 0, 0)

        [Header(Foam)]
        [NoScaleOffset] _FoamTex ("Foam (A)", 2D) = "black" {}
        _FoamTiling ("Foam: metres per tile", Float) = 3
        _BankFoam ("Foam on fast shallows", Range(0, 2)) = 0.6

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
                float _WaterTiling, _FlowCycle;
                float4 _FlowNormal, _StillDrift, _FarNormal, _Translucency, _WindRipples, _Gusts, _Slicks, _Shelter;
                half _Smoothness, _RuffledSmoothness, _RuffledReflection;
                float4 _Specular, _FarReflection;
                float _FoamTiling;
                half _BankFoam;
                half _Ripples, _WashFoam, _WashStreaks, _Rain;
            CBUFFER_END

            // один сэмплер на все текстуры (повтор, анизотропия карты нормалей): у D3D11 их не больше 16 на проход, а
            // глубина, копия кадра, тени, отражения и туман Enviro занимают свои
            TEXTURE2D(_WaterNormal); SAMPLER(sampler_WaterNormal);
            TEXTURE2D(_WaterHeight);
            TEXTURE2D(_FoamTex);

            // Поверхность реки с высоты по её осям (bank: метры от оси со знаком и полуширина; ноль — реки рядом нет):
            // shelter — затишье у берега, slick — полосы глади вдоль течения (_Slicks: шаг полос, длина штриха, доля полос,
            // где плёнка есть). Полосы вьются (сдвиг по шуму мира) и рвутся на штрихи; где пиксель шире полосы, она
            // бледнеет до своей средней доли — вдали не мельтешит.
            void RiverSurface(float2 p, float2 bank, float footprint, out half shelter, out half slick)
            {
                shelter = 0;
                slick = 0;

                if (bank.y < 0.5)
                    return;

                shelter = smoothstep(_Shelter.x, _Shelter.y, abs(bank.x) / bank.y);
                float wander = (WaterValueNoise(p / 110, 41u) - 0.5) * 14 + (WaterValueNoise(p / 37, 42u) - 0.5) * 4;
                float lane = (bank.x + wander) / max(_Slicks.x, 1);
                float id = floor(lane);
                half present = step(1 - _Slicks.z, WaterHash(float2(id, 17), 43u));
                half width = lerp(0.1, 0.3, WaterHash(float2(id, 18), 44u));
                // край полосы мягкий почти на всю её ширину, штрихи сходят на нет постепенно: с резким краем и обрывом
                // плёнка читалась белыми лентами
                half stripe = 1 - smoothstep(width * 0.15, width, abs(frac(lane) - 0.5));
                stripe = lerp(stripe, width * 1.15, saturate(footprint / (width * max(_Slicks.x, 1)) - 0.5));
                half dashes = smoothstep(0.35, 0.72, WaterValueNoise(p / max(_Slicks.y, 1) + id * 7.31, 45u));
                slick = present * stripe * dashes * (1 - shelter);
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                WaterView v = GetWaterView(input);
                float2 p = v.positionWS.xz;
                float t = _Time.y;

                // поток винтов: гладит мелкую волну под дроном; рябь и пена — из поля
                half calm = WaterWashCalm(v.positionWS);
                half washFoam;
                float2 rippleSlope = WaterRippleSlope(v.positionWS, washFoam) * _Ripples;
                // воздух от винтов у воды: где он рябит гладь — тусклый размытый круг, капли дождя в нём тонут
                half washAir = WaterWashAir(v.positionWS);
                half washRough = WaterWashRough(washAir);

                // течение: карта сносится по потоку двумя фазами со сдвигом в полцикла; фаза сбрасывается, когда её вес
                // ноль, поэтому карта не рвётся, хоть поток в каждой точке свой
                float2 flow = input.flow;
                float speed = length(flow);
                float cycle = max(_FlowCycle, 0.1);
                float phase0 = frac(t / cycle);
                float phase1 = frac(t / cycle + 0.5);
                half second = abs(phase0 * 2 - 1);
                float2 still = _StillDrift.xy * t;
                float2 uv0 = (p + still - flow * (phase0 * cycle)) / _WaterTiling;
                float2 uv1 = (p + still - flow * (phase1 * cycle)) / _WaterTiling + 0.5;

                // с высоты: затишье у берегов, полосы глади вдоль течения, пятна порывов ветра (бегут по ветру); ruffle —
                // насколько здесь рябь (0 — гладь, 1 — рябь): течение и ветер рябят воду, берег и плёнка гасят
                float footprint = max(length(v.dpdx), length(v.dpdy));
                half shelter, slick;
                RiverSurface(p, input.bank, footprint, shelter, slick);
                half wind = saturate(WaterWindSpeed() / 8);
                float2 run = _WaterWindRun.xy;
                half gust = WaterValueNoise((p - run * 0.5) / _Gusts.x, 21u) * 0.7 + WaterValueNoise((p - run * 0.7) / (_Gusts.x * 0.37), 22u) * 0.3;
                gust = smoothstep(0.5 - 0.5 * _Gusts.y, 0.5 + 0.5 * _Gusts.y, gust);
                half ruffle = saturate((0.35 + 0.4 * saturate(speed / 1.2) + wind * lerp(0.4, 1.4, gust)) * (1 - shelter * 0.75) * (1 - slick * 0.7));
                half damp = (1 - calm * 0.85) * lerp(0.25, 1.15, ruffle);

                half strength = lerp(_FlowNormal.x, _FlowNormal.y, saturate(speed / 1.2)) * damp;
                half3 n0 = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, uv0), strength);
                half3 n1 = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, uv1), strength);
                half3 normalTS = normalize(lerp(n0, n1, second));

                // мелкая рябь от ветра игры бежит по ветру поверх течения
                float2 windUV = p / _WindRipples.x - WaterWindScroll(_WindRipples.x, 0.03) + 0.37;
                half windStrength = _WindRipples.y * (0.15 + wind) * damp;
                half3 windN = WaterNormalOf(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, windUV), windStrength);
                normalTS = WaterBlend(normalTS, windN);

                WaterWashStreaks(p, t, TEXTURE2D_ARGS(_WaterNormal, sampler_WaterNormal), _WashStreaks, normalTS, washFoam);
                normalTS = WaterAddSlope(normalTS, rippleSlope + WaterRainSlope(v.positionWS, v.distance, _WaterRain) * _Rain * (1 - 0.7 * washRough));

                WaterBelow below = SampleBelow(v, normalTS.xy, _Distortion);

                // цвет по глубине: мель — первые десятки сантиметров, дальше мутная взвесь; просвет на гребнях
                half shallow = saturate(pow(max(below.depthBelow * _ShallowFalloff.x, 1e-3), -_ShallowFalloff.y));
                half3 colour = lerp(_DeepColor.rgb, _ShallowColor.rgb, shallow);
                half crestHeight = lerp(SAMPLE_TEXTURE2D(_WaterHeight, sampler_WaterNormal, uv0).a,
                                        SAMPLE_TEXTURE2D(_WaterHeight, sampler_WaterNormal, uv1).a, second) * 2;
                half crest = saturate(lerp(pow(abs(crestHeight * 1.98 * _Translucency.x), _Translucency.y) * _Translucency.z, 0,
                                           saturate(v.distance / max(_Translucency.w, 1))));
                colour = lerp(colour, _ShallowColor.rgb, crest);

                half opacity = saturate(pow(saturate(below.depthHere * _WaterAlpha.x), _WaterAlpha.y));
                half clean = saturate(pow(saturate(below.depthBelow * _CleanFalloff.x), _CleanFalloff.y));
                half3 albedo, transmitted;
                WaterBody(below, colour, opacity, clean, albedo, transmitted);
                half3 specular = lerp(_Specular.x, _Specular.y, pow(shallow, _Specular.z));
                half smoothness = WaterWashSmoothness(lerp(_Smoothness, _RuffledSmoothness, ruffle), washRough);
                // рябь ловит небо и под другими углами: отражение в ней тусклее, чем в глади
                half reflection = lerp(1, _RuffledReflection, ruffle);

                // вдали нормали мягче (полосы ряби не мельтешат), а их разброс уходит в шероховатость: небо размыто
                normalTS = normalize(lerp(normalTS, normalTS * half3(_FarNormal.y, _FarNormal.y, 1), saturate(sqrt(v.distance / max(_FarNormal.x, 1)))));
                smoothness = WaterFarSmoothness(smoothness, footprint, _WaterTiling, (strength + windStrength) * 1.5);

                // пена: на мели при быстром течении (вертикальная глубина — по лучу и наклону взгляда), сносится течением;
                // от винтов — своя
                float2 foamUV0 = (p - flow * (phase0 * cycle)) / _FoamTiling;
                float2 foamUV1 = (p - flow * (phase1 * cycle)) / _FoamTiling + 0.5;
                half foamPattern = lerp(SAMPLE_TEXTURE2D(_FoamTex, sampler_WaterNormal, foamUV0).a,
                                        SAMPLE_TEXTURE2D(_FoamTex, sampler_WaterNormal, foamUV1).a, second);
                half shoal = saturate(1 - below.depthHere * saturate(v.view.y) / 0.4) * saturate((speed - 0.25) * 2) * _BankFoam;
                half foam = saturate(WaterWashLace(washFoam * _WashFoam, foamPattern) + shoal * smoothstep(0.35, 0.8, foamPattern));
                albedo = lerp(albedo, 0.8, foam);
                smoothness = lerp(smoothness, 0.6, foam);
                transmitted *= 1 - foam;

                half edge = saturate(pow(saturate(below.depthHere * _EdgeFalloff.x), _EdgeFalloff.y));
                return WaterShade(input, v, normalTS, albedo, specular, smoothness, reflection, transmitted, edge, opacity, 0, foam, _FarReflection.xy);
            }
            ENDHLSL
        }
    }

    // без запасного шейдера: у цепочки GeoWorld/Water → Simple Lit есть ShadowCaster, и вода бросала бы тень на своё дно
    FallBack Off
}
