// Power line wires (GeoPowerLines): each wire is a ribbon of vertex pairs on its centreline (the wire's direction in
// NORMAL, the side -1/+1 in TEXCOORD0.x) that the vertex shader opens toward the camera: as wide as the wire, or at least
// _MinPixels where the wire is thinner than that, and then as faint as it is thin (the share of the width it fills), so
// a far wire fades instead of flickering. Near the camera (to _VisibleTo) a wire keeps at least _Visible of its colour, as
// wires read in FPV footage, then fades to nothing at _FadeEnd. Lit as a round wire: sun and sky on the side that faces
// each pixel, a dull highlight. No shadows and no depth: transparent, after the opaque world.
Shader "GeoWorld/Wire"
{
    Properties
    {
        [MainColor] _BaseColor ("Colour", Color) = (0.11, 0.11, 0.115, 1)
        _Radius ("Radius (metres)", Float) = 0.007
        _MinPixels ("Least width (pixels)", Range(0.5, 4)) = 1.5
        _Visible ("Least opacity near", Range(0, 1)) = 0.18
        _VisibleTo ("Least opacity to (metres)", Float) = 120
        _FadeStart ("Fading from (metres)", Float) = 250
        _FadeEnd ("Gone at (metres)", Float) = 420
        _Gloss ("Highlight", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _Radius;
                float _MinPixels;
                float _Visible;
                float _VisibleTo;
                float _FadeStart;
                float _FadeEnd;
                half _Gloss;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 across : TEXCOORD1;
                float side : TEXCOORD2;
                half coverage : TEXCOORD3;
                half widthPixels : TEXCOORD4;
                half fog : TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 centre = TransformObjectToWorld(v.positionOS.xyz);
                float3 along = SafeNormalize(TransformObjectToWorldDir(v.normalOS));
                float3 toCamera = GetCameraPositionWS() - centre;
                float distance = max(length(toCamera), 1e-3);
                float3 view = toCamera / distance;
                float3 across = cross(along, view);
                float l = length(across);
                across = l > 1e-4 ? across / l : SafeNormalize(cross(along, float3(0, 1, 0)));
                // the size of a pixel at this distance (orthographic: the view's height over the screen's); _m11 is
                // negative where the projection is flipped (a render texture on D3D)
                float pixel = unity_OrthoParams.w > 0.5 ? 2.0 * unity_OrthoParams.y / _ScreenParams.y
                    : 2.0 * distance / (_ScreenParams.y * abs(UNITY_MATRIX_P._m11));
                float halfWidth = max(_Radius, 0.5 * _MinPixels * pixel);
                float fill = saturate(_Radius / halfWidth);
                float near = _Visible * saturate((_VisibleTo - distance) / max(_VisibleTo * 0.5, 1));
                float far = saturate((_FadeEnd - distance) / max(_FadeEnd - _FadeStart, 1));
                o.coverage = max(fill, near) * far;
                o.widthPixels = 2.0 * halfWidth / max(pixel, 1e-6);
                float3 p = centre + across * (halfWidth * v.uv.x);
                o.positionWS = p;
                o.positionCS = TransformWorldToHClip(p);
                o.across = across;
                o.side = v.uv.x;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float s = clamp(i.side, -1.0, 1.0);
                float3 view = SafeNormalize(GetCameraPositionWS() - i.positionWS);
                // the round wire's surface under this pixel
                float3 n = SafeNormalize(i.across * s + view * sqrt(saturate(1.0 - s * s)));
                Light sun = GetMainLight();
                half3 ambient = SampleSH(n);
                half diffuse = saturate(dot(n, sun.direction));
                half3 h = SafeNormalize(sun.direction + view);
                half highlight = pow(saturate(dot(n, h)), 24.0) * _Gloss;
                half3 colour = _BaseColor.rgb * (ambient + sun.color * diffuse) + sun.color * highlight * 0.3;
                colour = MixFog(colour, i.fog);
                // a wire wide on the screen gets soft edges; a thin one keeps its whole ribbon (its coverage is its edge)
                half edge = i.widthPixels > 2.0 ? saturate((1.0 - abs(s)) * 0.5 * i.widthPixels) : 1.0;
                return half4(colour, _BaseColor.a * i.coverage * edge);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
