// Selection highlight, pass 1 of 2 (SelectionHighlight): a faint additive rim glow on the selected model's own surface
// that also marks its visible pixels in the stencil buffer, so the outline pass draws only outside the silhouette.
// Drawn over the model with Graphics.RenderMesh: the model's own materials, shadows and colliders are untouched.
// Kept under Resources so Shader.Find works in builds (URP, WebGL 2).
Shader "ShipyardTwin/SelectionFill"
{
    Properties
    {
        _Color ("Glow Color", Color) = (0.45, 0.87, 0.76, 1)
        _Strength ("Strength", Float) = 0.32
        _Fade ("Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-2" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "SelectionFill"
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1
            Stencil { Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Replace }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Strength;
                half _Fade;
            CBUFFER_END

            struct Attributes { float4 position : POSITION; float3 normal : NORMAL; };
            struct Varyings { float4 position : SV_POSITION; float3 normalWS : TEXCOORD0; float3 positionWS : TEXCOORD1; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.position.xyz);
                output.position = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normal);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 view = normalize(GetWorldSpaceViewDir(input.positionWS));
                half facing = saturate(dot(normalize(input.normalWS), view));
                half rim = pow(1.0h - facing, 2.5h);
                half pulse = 0.85h + 0.15h * sin(_Time.y * 3.0);
                half glow = (0.05h + rim * pulse) * _Strength * _Fade;
                return half4(_Color.rgb * glow, 0);
            }
            ENDHLSL
        }
    }
}
