// Карта дорог вокруг камеры (RoadMap): каждый отрезок оси дороги (RoadAreas) рисуется сверху прямоугольником по полотну с
// запасом _RoadReach; в каждый его пиксель пишется сам отрезок: цель 0 — его концы (мир xz: A, B), цель 1 — полуширина,
// метры от начала дороги до A, вид покрытия + 8 × разметка + 16 × износ (0..7) + 128 × бордюр + 256 × начало + 1024 ×
// конец дороги (RoadAreas.End), метры от A до конца дороги. Где дороги накрывают друг друга, побеждает главная
// (RoadMapCommon.hlsl: внутри полотна — важнее, при равной — глубже в полотне; за полотнами — ближе к кромке) — через буфер
// глубины. Террейн (GroundRoads.hlsl) по отрезкам четырёх соседних пикселей считает расстояния точно — в каждом своём
// пикселе, без смешивания разных дорог.
Shader "Hidden/Game/Road Map"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite On
        ZTest LEqual

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "RoadMapCommon.hlsl"

            // xy — начало окна в мире (x, z), z — 1/размер окна, w — знак y клипа (−1, где uv идёт сверху)
            float4 _RoadDraw;
            // x — запас за краем полотна, м (у дальнего окна — с текселем окна), y — тексель окна сверх обычного запаса
            float4 _RoadReach;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 a : TEXCOORD0;
                float2 b : TEXCOORD1;
                // полуширина, вдоль у A, вид и концы, от A до конца дороги
                float4 road : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 p : TEXCOORD0;
                nointerpolation float2 a : TEXCOORD1;
                nointerpolation float2 b : TEXCOORD2;
                nointerpolation float4 road : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float2 uv = (input.positionOS.xz - _RoadDraw.xy) * _RoadDraw.z;
                output.positionCS = float4(uv.x * 2 - 1, (uv.y * 2 - 1) * _RoadDraw.w, 0.5, 1);
                output.p = input.positionOS.xz;
                output.a = input.a;
                output.b = input.b;
                output.road = input.road;
                return output;
            }

            struct Targets
            {
                float4 segment : SV_Target0;
                float4 info : SV_Target1;
            };

            Targets Frag(Varyings input, out float depth : SV_Depth)
            {
                RoadMeasure m = MeasureRoad(input.p, input.a, input.b, input.road);

                if (!m.valid || m.distance > input.road.x + _RoadReach.x)
                    discard;

                // главнее — ближе
                depth = saturate((m.key + 620) / 640);
            #if UNITY_REVERSED_Z
                depth = 1 - depth;
            #endif
                Targets targets;
                targets.segment = float4(input.a, input.b);
                targets.info = input.road;
                return targets;
            }
            ENDHLSL
        }

        // 1: перекрёстки — какие дороги накрывают точку полотном с бордюром (до 0.6 м за кромкой): R — меньший номер дороги,
        // A — больший (BlendOp Min, Max); разные — перекрёсток. Торцы отрезков не в счёт: где одна линия OSM продолжает
        // другую, полотна встречаются только торцами, и это не перекрёсток.
        Pass
        {
            ZWrite Off
            ZTest Always
            Blend One One
            BlendOp Min, Max

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _RoadDraw;
            // y — тексель окна: у дальнего окна (тексель в несколько метров) перекрёсток не проваливается между текселями
            float4 _RoadReach;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 a : TEXCOORD0;
                float2 b : TEXCOORD1;
                float4 road : TEXCOORD2;
                float2 id : TEXCOORD3;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 p : TEXCOORD0;
                nointerpolation float2 a : TEXCOORD1;
                nointerpolation float2 b : TEXCOORD2;
                nointerpolation float2 halfAndId : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float2 uv = (input.positionOS.xz - _RoadDraw.xy) * _RoadDraw.z;
                output.positionCS = float4(uv.x * 2 - 1, (uv.y * 2 - 1) * _RoadDraw.w, 0.5, 1);
                output.p = input.positionOS.xz;
                output.a = input.a;
                output.b = input.b;
                output.halfAndId = float2(input.road.x, input.id.x);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 ab = input.b - input.a;
                float length2 = max(dot(ab, ab), 1e-6);
                float t = dot(input.p - input.a, ab) / length2;
                float d = distance(input.p, input.a + ab * saturate(t));

                if (t < 0 || t > 1 || d > input.halfAndId.x + 0.6 + _RoadReach.y)
                    discard;

                return float4(input.halfAndId.y, 0, 0, input.halfAndId.y);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
