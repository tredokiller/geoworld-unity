// Рябь воды под дроном (RotorWashWater): волновое уравнение на сетке вокруг дрона, шаг — один Blit.
// R — высота поверхности сейчас (м), G — шаг назад, B — пена/брызги на воде (0..1), A — где поток согнал ряску и плёнку.
// Поток винтов вдавливает воду под дроном (лунка), от её края струя бежит по воде (стенная струя, воздух — как у листьев
// RotorWashLeaves): толкает кольца, уходящие от дрона, и рвёт воду рябью; когда дрон летит, лунка движется и тянет за
// собой след. Шейдеры воды и луж читают текстуру глобально (_WaterRippleTex, _WaterRippleRect).
Shader "Hidden/Game/Water Ripple Sim"
{
    Properties
    {
        _MainTex ("State", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        // sampler_PointClamp — из URP (Core.hlsl)
        float4 _MainTex_TexelSize;

        // x, z — начало окна в мире, y — размер окна (м)
        float4 _SimRect;
        // x — (c·dt/dx)², y — затухание за шаг, z — номер шага (зерно шума), w — спад пены за шаг
        float4 _SimParams;
        // xz — точка под дроном на воде, y — радиус струи у воды (м), w — воздух у края струи, доля полного потока (20 м/с)
        float4 _SimWash;
        // x — глубина лунки (м), y — доля «поршня» за шаг, z — толчки ряби (м за шаг), w — пена от струи
        float4 _SimForce;
        // xy — сдвиг окна в текселях (Shift)
        float4 _SimShift;
        // x — толчок колец за шаг (м), y — их волновое число (рад/м), z — фаза (рад), w — время порывов (с, по кругу 4π)
        float4 _SimWave;
        // x, y — на сколько за шаг окно в ряске раскрывается и затягивается
        float4 _SimClear;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv;
            return output;
        }

        float4 State(float2 uv)
        {
            return SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_PointClamp, uv, 0);
        }

        // случайное 0..1 клетки окна на этом шаге: клетки считаем от угла окна, хеш целочисленный — float-хеш на
        // мировых координатах в километрах вырождался в константу, и вместо ряби выходила гладкая чаша
        float CellNoise(float2 cell, float step)
        {
            uint2 id = (uint2)max(cell, 0);
            return GenerateHashedRandomFloat(uint3(id, (uint)step));
        }

        // То же, но плавное: между углами клеток — сглаженное смешивание. Ступенчатый шум клетками ~60 см ложился в волны
        // и пену квадратами (на краях клеток крутизна давала ещё и белые линии пены).
        float SmoothCellNoise(float2 position, float step)
        {
            float2 cell = floor(position);
            float2 f = position - cell;
            f = f * f * (3 - 2 * f);
            float a = CellNoise(cell, step), b = CellNoise(cell + float2(1, 0), step);
            float c = CellNoise(cell + float2(0, 1), step), d = CellNoise(cell + 1, step);
            return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
        }
        ENDHLSL

        // 0: шаг волнового уравнения с потоком винтов
        Pass
        {
            Name "Step"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Step

            float4 Step(Varyings input) : SV_Target
            {
                float2 texel = _MainTex_TexelSize.xy;
                float4 state = State(input.uv);
                float h = state.r;
                float laplacian = State(input.uv + float2(texel.x, 0)).r + State(input.uv - float2(texel.x, 0)).r +
                                  State(input.uv + float2(0, texel.y)).r + State(input.uv - float2(0, texel.y)).r - 4 * h;
                float next = (2 * h - state.g + _SimParams.x * laplacian) * _SimParams.y;

                float2 positionWS = _SimRect.xz + input.uv * _SimRect.y;
                float2 offset = positionWS - _SimWash.xz;
                float jet = max(_SimWash.y, 0.1);
                float d = length(offset);
                float power = _SimWash.w;
                // воздух у воды — как у листьев: под струёй 0.6–1 от края, дальше слабеет ~(струя/d)^0.9
                float air = power * (d < jet ? lerp(0.6, 1, d / jet) : pow(jet / max(d, 1e-3), 0.9));

                // лунка: поток давит на воду под струёй (радиус 0.6 струи), поверхность тянется к прогибу
                float core = exp(-d * d / (0.36 * jet * jet));
                next = lerp(next, -_SimForce.x * power * core, _SimForce.y * power * core);

                // от края лунки струя бежит по воде: толкает кольца, которые уходят от дрона со скоростью волн самого поля
                // (порывы — медленно бегущие по кругу доли), и рвёт воду плавными толчками ~60 см, новыми каждые 0.1 с
                // (мельче и каждый шаг шум читался серым асфальтом); сила — воздух^0.7: слабый поток с высоты ещё виден
                float zone = smoothstep(0.5 * jet, jet, d);
                float drive = pow(abs(air), 0.7) * zone;
                float angle = atan2(offset.y, offset.x + 1e-6);
                float gust = 0.65 + 0.35 * sin(3 * angle + 1.5 * _SimWave.w) * sin(5 * angle - _SimWave.w + 1.3);
                next += _SimWave.x * drive * gust * sin(_SimWave.z - _SimWave.y * d);
                float noise = SmoothCellNoise((positionWS - _SimRect.xz) * 1.6, floor(_SimParams.z / 6)) - 0.5;
                next += noise * _SimForce.z * drive;

                // у краёв окна волны гаснут, а не отражаются обратно
                float2 edge = min(input.uv, 1 - input.uv);
                next *= lerp(0.85, 1, saturate(min(edge.x, edge.y) * 16));

                // пена: где воздух у воды быстрее ~5 м/с (у края струи низко над водой) и на крутых гребнях — только сколько
                // её; клочья и кружево рисует шейдер воды по своему рисунку пены (WaterWashLace)
                float slope = abs(laplacian);
                float spray = saturate(air * 4 - 1) * zone;
                float foam = max(state.b * _SimParams.w, saturate(spray * _SimForce.w * 0.5 + slope * 1.2));

                // ряску и плёнку поток сгоняет с ~1.6 м/с воздуха, всю — с ~4.4 м/с; отплывает она не сразу и
                // возвращается медленно — окно остаётся там, где его выдуло
                float clearTarget = smoothstep(0.08, 0.22, air);
                float cleared = clearTarget > state.a ? min(clearTarget, state.a + _SimClear.x) : max(clearTarget, state.a - _SimClear.y);
                // история окон в ряске и пена затягиваются к 20 м от дрона: край окна (не ближе 21 м) и его сдвиг
                // отбрасывают уже пустые данные — след не обрывается прямой линией
                float keep = saturate((20 - d) / 5);
                return float4(next, h, min(foam, keep), min(cleared, keep));
            }
            ENDHLSL
        }

        // 1: окно уехало за дроном — сдвигаем рябь на целое число текселей, новое поле — спокойная вода
        Pass
        {
            Name "Shift"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Shift

            float4 Shift(Varyings input) : SV_Target
            {
                float2 uv = input.uv + _SimShift.xy * _MainTex_TexelSize.xy;

                if (any(uv < 0) || any(uv > 1))
                    return 0;

                return State(uv);
            }
            ENDHLSL
        }
    }
}
