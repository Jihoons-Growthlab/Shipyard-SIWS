// Selection highlight, pass 2 of 2 (SelectionHighlight): the selected model's back faces pushed out along smoothed
// normals by a constant number of screen pixels (inverted hull). The stencil written by SelectionFill keeps it off the
// model itself, so only a clean silhouette line shows; where a GPU has no stencil the depth test still hides the inner
// part on opaque models. Kept under Resources so Shader.Find works in builds (URP, WebGL 2).
Shader "ShipyardTwin/SelectionOutline"
{
    Properties
    {
        _Color ("Outline Color", Color) = (0.45, 0.87, 0.76, 1)
        _Width ("Width (px)", Float) = 2.5
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-1" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "SelectionOutline"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Front
            Stencil { Ref 128 ReadMask 128 Comp NotEqual }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Width;
                half _Fade;
            CBUFFER_END

            struct Attributes { float4 position : POSITION; float3 normal : NORMAL; };
            struct Varyings { float4 position : SV_POSITION; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float4 clip = TransformObjectToHClip(input.position.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normal);
                float2 direction = mul((float3x3)UNITY_MATRIX_VP, normalWS).xy;
                float len = length(direction);
                if (len > 1e-5)
                {
                    // Pixels to clip space: 2 / screen size per pixel, times w so the width stays constant on screen.
                    clip.xy += direction / len * (_Width * _Fade) * 2.0 / _ScreenParams.xy * clip.w;
                }
                output.position = clip;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(_Color.rgb, _Color.a * _Fade);
            }
            ENDHLSL
        }
    }
}
