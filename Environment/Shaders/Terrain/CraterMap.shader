// Карта воронок вокруг камеры (CraterMap): каждая воронка (CraterField) рисуется сверху квадратом по своей зоне (яма, вал,
// выброс — 2.25 радиуса); в окно пишется со смешением Min (все каналы — не больше нуля, пустое окно — нули):
//  R — рельеф мелких воронок (м, чаша ниже нуля): тех, что уже сетки высот тайла и не врезаны в неё (крупные — в ней);
//  G — минус вал и выброс мелких (м);
//  B — минус «чашность» любой воронки (1 − s² в яме);
//  A — минус свежий выброс (светлый вывороченный грунт у свежих воронок, к краю зоны — на нет);
// и во вторую цель (_CraterWater): R — минус «чашность» воронок, где стоит вода (у края ямы — ноль, без ступеньки), G —
// воронок и окопов, которых нет на снимке (рассыпанных: найденные на нём уже темны в спутнике).
// Где воронки накрывают друг друга, берётся самая глубокая чаша и самый высокий вал — как в генераторе (GeoCombatGround).
Shader "Hidden/Game/Crater Map"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend One One
        BlendOp Min

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "CraterCommon.hlsl"

            // xy — начало окна в мире (x, z), z — 1/размер окна, w — знак y клипа (−1, где uv идёт сверху)
            float4 _CraterDraw;

            struct Attributes
            {
                float3 positionOS : POSITION;
                // центр воронки (мир xz); у окопа — начало отрезка осевой линии
                float2 centre : TEXCOORD0;
                // радиус ямы (у окопа — ширина), глубина, износ, флаги (1 — мелкая: рельеф её рисует окно, 2 — вода,
                // 4 — свежая, 8 — окоп)
                float4 crater : TEXCOORD1;
                // у окопа — конец отрезка (у воронки — её центр)
                float2 end : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 p : TEXCOORD0;
                nointerpolation float2 centre : TEXCOORD1;
                nointerpolation float4 crater : TEXCOORD2;
                nointerpolation float2 end : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float2 uv = (input.positionOS.xz - _CraterDraw.xy) * _CraterDraw.z;
                output.positionCS = float4(uv.x * 2 - 1, (uv.y * 2 - 1) * _CraterDraw.w, 0.5, 1);
                output.p = input.positionOS.xz;
                output.centre = input.centre;
                output.crater = input.crater;
                output.end = input.end;
                return output;
            }

            struct Targets
            {
                float4 map : SV_Target0;
                float4 water : SV_Target1;
            };

            Targets Frag(Varyings input)
            {
                uint flags = (uint)round(input.crater.w);
                bool small = (flags & 1u) != 0;
                bool water = (flags & 2u) != 0;
                bool fresh = (flags & 4u) != 0;
                bool seen = (flags & 16u) != 0;
                float offset, bowl, ejecta = 0;

                [branch] if ((flags & 8u) != 0)
                {
                    // окоп: расстояние до отрезка осевой линии
                    float2 ab = input.end - input.centre;
                    float t = saturate(dot(input.p - input.centre, ab) / max(dot(ab, ab), 1e-6));
                    float d = distance(input.p, input.centre + ab * t);
                    float half = TrenchHalfWidth(input.crater.x, input.crater.z);

                    if (d >= half + 2.4)
                        discard;

                    offset = TrenchOffset(input.crater.y, input.crater.x, input.crater.z, d);
                    float u = d / half;
                    bowl = u < 1 ? 1 - u * u : 0;
                }
                else
                {
                    float radius = input.crater.x;
                    float s = distance(input.p, input.centre) / radius;

                    if (s >= CRATER_REACH)
                        discard;

                    offset = CraterOffset(input.crater.y, input.crater.z, s);
                    bowl = s < 1 ? 1 - s * s : 0;
                    ejecta = fresh ? smoothstep(0.85, 1.05, s) * (1 - smoothstep(1.3, CRATER_REACH, s)) : 0;
                }
                Targets targets;
                targets.map = float4(small ? min(offset, 0) : 0, small ? -max(offset, 0) : 0, -bowl, -ejecta);
                // R: чаши с водой; G: чаши, которых нет на снимке (рассыпанные, окопы) — их террейн темнит сам
                targets.water = float4(water ? -bowl : 0, seen ? 0 : -bowl, 0, 0);
                return targets;
            }
            ENDHLSL
        }
    }
}
