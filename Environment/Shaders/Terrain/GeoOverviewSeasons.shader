// Обзорная карта GeoWorld (GeoOverview) с порой года: копия шейдера пакета GeoWorld/Overview (те же свойства — их значения
// переходят при подмене шейдера), к которой добавлены зелень по поре, убранные летом поля и снег зимой (Seasons.hlsl,
// Snow.hlsl; пашня — из дальней карты GroundWetness, она накрывает обзорную карту на ~1.5 км за тайлами). Вдали пятен
// снега не различить — покров ложится долей _SnowCover на ровное, серее над тёмной землёй, как на тайлах. Подменяет его GroundWetness (материалы слоёв обзорная карта
// создаёт сама в Play), в билде шейдер держит Resources/GeoWorld/Overview (seasons).
// Освещение — как у террейна тайлов (URP PBR), и в дождь она мокнет, как их земля (GroundWetness.hlsl): темнее и глаже,
// грунт сильнее травы. Освещённая по Ламберту и затемнённая целиком, она не отражала серое небо, как мокрые дальние тайлы
// под косым углом, и край загруженных тайлов в дождь шёл светлой полосой.
Shader "Game/GeoWorld Overview (Seasons)"
{
    Properties
    {
        [MainTexture] _BaseMap ("Satellite", 2D) = "gray" {}
        [MainColor] _Tint ("Tint", Color) = (1, 1, 1, 1)
        [NoScaleOffset] _LoadedTiles ("Loaded tiles (1 = hide)", 2D) = "black" {}
        _TileGrid ("Grid origin XZ, tile size", Vector) = (0, 0, 1000, 0)
        _TileGridSize ("First tile index XY, columns, rows", Vector) = (0, 0, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.tredokiller.geoworld/Environment/Shaders/Seasons.hlsl"
        #include "Packages/com.tredokiller.geoworld/Environment/Shaders/Snow.hlsl"
        #include "Packages/com.tredokiller.geoworld/Environment/Shaders/Terrain/Puddles.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_LoadedTiles);
        // мокрость земли (GroundWetness), та же, что у террейна тайлов
        float _GroundWetness;

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _Tint;
            float4 _TileGrid;
            float4 _TileGridSize;
        CBUFFER_END

        void ClipLoadedTile(float3 positionWS)
        {
            int2 tile = (int2)floor((positionWS.xz - _TileGrid.xy) / _TileGrid.z) - (int2)_TileGridSize.xy;
            if (tile.x >= 0 && tile.y >= 0 && tile.x < (int)_TileGridSize.z && tile.y < (int)_TileGridSize.w)
                clip(0.5 - LOAD_TEXTURE2D(_LoadedTiles, tile).r);
        }
        ENDHLSL

        Pass
        {
            Name "Overview"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(position.positionCS.z);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                ClipLoadedTile(input.positionWS);

                half3 normal = normalize(input.normalWS);
                half3 ground = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _Tint.rgb;
                // мокрая земля — как в GroundWetness.hlsl: голость по цвету самой земли до поры года и снега (зелень —
                // трава, бурое и серое — грунт), иначе осенью в дождь край загруженных тайлов снова стоял светлой полосой
                half greenness = (ground.g - max(ground.r, ground.b)) / max(ground.g, 0.02);
                half bare = saturate(1 - greenness * 4);
                // летом убранные поля — по пашне дальней карты (GroundLand), как на тайлах
                half3 albedo = SeasonGround(SeasonForestCanopy(SeasonHarvest(ground, GroundFieldShare(input.positionWS)), input.positionWS.xz));
                // снег — как на тайлах вдали: над тёмной землёй (лес, пашня) серее, поля и лесополосы видны
                half over = SnowOverGround(albedo);
                half3 snow = lerp(SEASON_SNOW_COLOR * over, albedo * 1.8 + 0.12, (1 - over) * 0.35);
                half snowed = _SnowCover * smoothstep(0.55, 0.85, normal.y);
                albedo = lerp(albedo, snow, snowed);
                half wet = _GroundWetness * (1 - snowed);
                albedo *= lerp(1, lerp(0.8, 0.6, bare), wet);

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normal;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1), input.fogFactor);
                inputData.bakedGI = SampleSH(normal);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.smoothness = max(snowed * 0.25, wet * lerp(0.2, 0.42, bare));
                surface.occlusion = 1;
                surface.alpha = 1;
                surface.normalTS = half3(0, 0, 1);
                half4 color = UniversalFragmentPBR(inputData, surface);
                return half4(MixFog(color.rgb, inputData.fogCoord), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Back
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half Fragment(Varyings input) : SV_Target
            {
                ClipLoadedTile(input.positionWS);
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; half3 normalWS : TEXCOORD1; };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                ClipLoadedTile(input.positionWS);
                return half4(NormalizeNormalPerPixel(input.normalWS), 0);
            }
            ENDHLSL
        }
    }
}
