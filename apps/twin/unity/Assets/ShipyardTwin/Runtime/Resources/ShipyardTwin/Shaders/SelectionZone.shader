// Ground ring of a selected heavy machine's simulated danger zone (EquipmentZone): drawn on a unit disk
// (CraneSafetyView.RingMesh(0, 1), uv.y = 0 centre .. 1 edge, uv.x = angle) scaled to the radius. A soft fill that
// rises to the edge, a solid edge line, a slowly turning dashed ring inside it and an outward pulse. Widths are in
// metres (_Radius), so small and large machines get the same line weight. Unlit and transparent, no shadows.
// Kept under Resources so Shader.Find works in builds (URP, WebGL 2).
Shader "ShipyardTwin/SelectionZone"
{
    Properties
    {
        _Color ("Zone Color", Color) = (1, 0.62, 0.22, 1)
        _Radius ("Radius (m)", Float) = 5
        _Fade ("Fade", Range(0, 1)) = 1
        _BoxHalfSize ("Box half size (m)", Vector) = (0, 0, 0, 0)
        _Alert ("Walk mode alert", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "SelectionZone"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -2, -2

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Radius;
                half _Fade;
                float4 _BoxHalfSize;
                half _Alert;
            CBUFFER_END

            struct Attributes { float4 position : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float2 ground : TEXCOORD1; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.position = TransformObjectToHClip(input.position.xyz);
                output.uv = input.uv;
                output.ground = input.position.xz;
                return output;
            }

            half band(float d, float from, float to, float soft)
            {
                return smoothstep(from - soft, from, d) * (1.0 - smoothstep(to, to + soft, d));
            }

            half4 frag(Varyings input) : SV_Target
            {
                float radius = max(_Radius, 0.5);
                if (_BoxHalfSize.x > 0.0)
                {
                    float2 ground = input.ground * _BoxHalfSize.xy;
                    float2 edges = _BoxHalfSize.xy - abs(ground);
                    float edge = 1.0 - smoothstep(0.28, 0.55, min(edges.x, edges.y));
                    float stripe = smoothstep(0.62, 0.73, frac((ground.x + ground.y) / 2.2));
                    return half4(_Color.rgb, max(edge * 0.92, 0.12 + stripe * 0.2) * _Fade * _Color.a);
                }
                float d = input.uv.y * radius;                 // metres from the centre
                float soft = max(0.03, radius * 0.004);
                half fill = 0.04 + 0.14 * input.uv.y * input.uv.y;
                half edge = band(d, radius - 0.22, radius, soft) * 0.95;
                float dashes = step(frac(input.uv.x * max(24.0, round(radius * 4.0)) - _Time.y * 0.08), 0.55);
                half dashed = band(d, radius - 0.75, radius - 0.55, soft) * dashes * 0.75;
                float wave = frac(_Time.y * 0.35);
                float x = (input.uv.y - wave) * 14.0;          // x * x, not pow(x, 2): pow of a negative base is undefined in GLSL
                half pulse = exp(-x * x) * (1.0 - wave) * 0.28;
                float2 ground = input.ground * radius;
                float stripe = smoothstep(0.62, 0.73, frac((ground.x + ground.y) / 2.2));
                fill += _Alert * (0.08 + stripe * 0.18);
                half alpha = saturate(max(max(edge, dashed), fill + pulse)) * _Fade;
                return half4(_Color.rgb, alpha * _Color.a);
            }
            ENDHLSL
        }
    }
}
