// Где земля у самой воды (WaterShore): вода тайлов сверху в окне вокруг камеры и её расстояние до каждой точки земли.
// Проход 0 рисует меши воды сверху — высоту глади (R) и ноль до воды (G); 1 и 2 — точное евклидово расстояние до
// ближайшей воды в пределах _ShoreReach текселей: по строкам (до ближней воды в строке и её высота), потом по столбцам
// (корень из суммы квадратов). Итог — R: высота ближней глади, м, G: до неё по горизонтали, м (далеко — 1000).
// Террейн (GroundWetness.hlsl) по ним мочит полосу берега над урезом.
Shader "Hidden/Game/Water Shore"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
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
        float4 _MainTex_TexelSize;
        // xy — начало окна в мире (x, z), z — 1/размер окна, w — знак y клипа (−1, где uv идёт сверху)
        float4 _ShoreDraw;
        // x — сколько текселей искать воду в каждую сторону, y — метров в текселе
        float4 _ShoreReach;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float height : TEXCOORD1;
        };

        Varyings Blit(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv;
            output.height = 0;
            return output;
        }
        ENDHLSL

        // 0: вода сверху — высота глади, до воды ноль
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float2 uv = (positionWS.xz - _ShoreDraw.xy) * _ShoreDraw.z;
                output.positionCS = float4(uv.x * 2 - 1, (uv.y * 2 - 1) * _ShoreDraw.w, 0.5, 1);
                output.uv = uv;
                output.height = positionWS.y;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                return float4(input.height, 0, 0, 1);
            }
            ENDHLSL
        }

        // 1: по строке — до ближней воды (м) и её высота
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Blit
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float best = 1000;
                float height = -10000;
                int reach = (int)_ShoreReach.x;

                [loop]
                for (int dx = -reach; dx <= reach; dx++)
                {
                    float2 s = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_PointClamp, input.uv + float2(dx * _MainTex_TexelSize.x, 0), 0).rg;
                    float d = abs(dx) * _ShoreReach.y;

                    if (s.g < 0.5 && d < best)
                    {
                        best = d;
                        height = s.r;
                    }
                }

                return float4(height, best, 0, 1);
            }
            ENDHLSL
        }

        // 2: по столбцу — евклидово расстояние до ближней воды и её высота
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Blit
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float best = 1000;
                float height = -10000;
                int reach = (int)_ShoreReach.x;

                [loop]
                for (int dy = -reach; dy <= reach; dy++)
                {
                    float2 s = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_PointClamp, input.uv + float2(0, dy * _MainTex_TexelSize.y), 0).rg;
                    float across = dy * _ShoreReach.y;
                    float d = sqrt(s.g * s.g + across * across);

                    if (s.g < 999 && d < best)
                    {
                        best = d;
                        height = s.r;
                    }
                }

                return float4(height, best, 0, 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
