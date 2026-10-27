// The overview of a Geo World tiled world (GeoOverview): satellite imagery on a coarse height grid, the world's own layer and the
// far ring around it, lit by the main light and the ambient like the terrain, with the scene's fog.
// Where a tile is loaded (its texel in _LoadedTiles is 1) the overview is cut away, so it never shows through the real terrain.
Shader "GeoWorld/Overview"
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

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_LoadedTiles);

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

                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _Tint.rgb;
                half3 normal = normalize(input.normalWS);
                Light light = GetMainLight();
                half3 lighting = light.color * saturate(dot(normal, light.direction)) + SampleSH(normal);
                half3 color = MixFog(albedo * lighting, input.fogFactor);
                return half4(color, 1);
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
