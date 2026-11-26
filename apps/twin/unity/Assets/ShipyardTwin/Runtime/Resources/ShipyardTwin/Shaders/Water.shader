// Sea for URP. Eight Gerstner waves (spread around the wind, lengths 4-90 m) give the surface normals everywhere;
// near the camera a ring mesh (SeaSurface, _SeaRole 1) is also displaced by them, so swells rise and crests
// sharpen. The far sea is the flat yard sea mesh (_SeaRole 0), cut out where the ring takes over (global _SeaNear).
// Dock pits are cut out of the ring (global _SeaHole*). Shading: Fresnel sky reflection, water body with light
// scattering through the crests, crest foam from the wave compression, sun glint clamped for bloom. Short waves fade
// with distance (no shimmer); everything is analytic plus two ripple texture reads (cheap on WebGL and phones).
// Kept under Resources so Shader.Find works in builds (ShipPalette uses it for the "water" paint).
Shader "ShipyardTwin/Water"
{
    Properties
    {
        _BaseColor ("Deep Colour", Color) = (0.05, 0.16, 0.23, 1)
        _ShallowColor ("Scatter Colour", Color) = (0.10, 0.42, 0.44, 1)
        _FoamColor ("Foam Colour", Color) = (0.90, 0.93, 0.94, 1)
        _SkyHorizon ("Sky Horizon", Color) = (0.70, 0.79, 0.86, 1)
        _SkyZenith ("Sky Zenith", Color) = (0.30, 0.47, 0.68, 1)
        _BaseMap ("Unused (kept for texture offset calls)", 2D) = "white" {}
        _BumpMap ("Ripple Normal", 2D) = "bump" {}
        _RippleTiling ("Ripple Tiling (per m)", Float) = 0.11
        _RippleStrength ("Ripple Strength", Range(0, 1)) = 0.22
        _WaveStrength ("Wave Slope", Range(0, 2)) = 0.8
        _WaveHeight ("Wave Height (ring mesh)", Range(0, 1.5)) = 0.4
        _WaveSpeed ("Wave Speed", Range(0, 3)) = 1
        _WindDirection ("Wind Direction (deg from yard +x)", Float) = 200
        _Smoothness ("Smoothness", Range(0, 1)) = 0.9
        _Foam ("Foam Amount", Range(0, 1)) = 0.6
        _Glint ("Sun Glint Limit", Range(0, 4)) = 0.55
        _SeaRole ("Role (0 far flat, 1 near ring)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define WAVES 8
            #define HOLES 8

            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _ShallowColor;
                half4 _FoamColor;
                half4 _SkyHorizon;
                half4 _SkyZenith;
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float _RippleTiling;
                half _RippleStrength;
                half _WaveStrength;
                half _WaveHeight;
                half _WaveSpeed;
                float _WindDirection;
                half _Smoothness;
                half _Foam;
                half _Glint;
                float _SeaRole;
            CBUFFER_END

            // Set by SeaSurface: ring centre (x, z), radius, 1 when the ring is active.
            float4 _SeaNear;
            float _ShipyardDepthMapVisible;
            // Dock pits cut out of the ring: centre (x, z) and half sizes, then (cos, sin) of the heading.
            float4 _SeaHoleA[HOLES];
            float4 _SeaHoleB[HOLES];
            float _SeaHoleCount;

            // Wavelength (m), steepness k*A, direction off the wind (deg), crest sharpening Q, phase.
            static const float4 WaveShape[WAVES] = {
                float4(90.0, 0.055, 0.0, 0.45), float4(57.0, 0.060, 23.0, 0.55), float4(37.0, 0.065, -31.0, 0.6),
                float4(23.0, 0.070, 47.0, 0.65), float4(14.5, 0.070, -58.0, 0.7), float4(9.3, 0.060, 12.0, 0.7),
                float4(6.1, 0.050, 73.0, 0.7), float4(4.0, 0.045, -18.0, 0.7) };
            static const float WavePhase[WAVES] = { 0.0, 1.7, 4.1, 2.3, 5.2, 0.9, 3.6, 2.8 };

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 restXZ : TEXCOORD1;   // undisplaced position: waves are evaluated there
                half fog : TEXCOORD2;
            };

            // Sum of the waves at a rest position: displacement (x, y, z), slope (dx, dz) and compression.
            void Waves(float2 xz, float distance, float heightScale, out float3 offset, out float2 slope, out float compression)
            {
                offset = 0;
                slope = 0;
                compression = 0;
                float time = _Time.y * _WaveSpeed;
                [unroll] for (int i = 0; i < WAVES; i++)
                {
                    float4 w = WaveShape[i];
                    float fade = saturate(1.5 - distance / (w.x * 32.0));   // short waves fade first
                    float k = 6.28318 / w.x;
                    float amplitude = w.y / k;
                    float a = radians(_WindDirection + w.z);
                    float2 d = float2(cos(a), sin(a));   // yard heading -> Unity (x, z)
                    float theta = k * dot(d, xz) - sqrt(9.81 * k) * time + WavePhase[i];
                    float s, c;
                    sincos(theta, s, c);
                    float wa = w.y * fade;
                    offset += float3(d.x * w.w * amplitude * c, amplitude * s, d.y * w.w * amplitude * c) * (fade * heightScale);
                    slope += d * (wa * c);
                    compression += w.w * wa * s;
                }
            }

            bool InHole(float2 xz)
            {
                [loop] for (int i = 0; i < HOLES; i++)
                {
                    if (i >= (int)_SeaHoleCount) break;
                    float2 d = xz - _SeaHoleA[i].xy;
                    float2 uv = float2(d.x * _SeaHoleB[i].x + d.y * _SeaHoleB[i].y, -d.x * _SeaHoleB[i].y + d.y * _SeaHoleB[i].x);
                    if (abs(uv.x) < _SeaHoleA[i].z && abs(uv.y) < _SeaHoleA[i].w) return true;
                }
                return false;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.restXZ = positionWS.xz;
                if (_SeaRole > 0.5 && _ShipyardDepthMapVisible < 0.5)
                {
                    float distance = length(_WorldSpaceCameraPos - positionWS);
                    // The swell dies out toward the ring edge, where the flat far sea continues.
                    float edge = saturate((_SeaNear.z - length(positionWS.xz - _SeaNear.xy)) / (0.25 * _SeaNear.z));
                    float3 offset;
                    float2 slope;
                    float compression;
                    Waves(positionWS.xz, distance, _WaveHeight * edge, offset, slope, compression);
                    // Troughs stay near the rest level: the seabed just under it (shoals, the satellite's surf) must
                    // not show through; crests still rise.
                    offset.y = max(offset.y, -0.12 * edge);
                    positionWS += offset;
                }

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 xz = input.restXZ;
                float ring = length(xz - _SeaNear.xy);
                if (_SeaNear.w > 0.5)
                {
                    // Far flat sea gives way to the ring inside its radius; the ring ends there and skips the dock pits.
                    if (_SeaRole < 0.5) clip(ring - _SeaNear.z);
                    else { clip(_SeaNear.z - ring); if (InHole(xz)) discard; }
                }

                float3 positionWS = input.positionWS;
                float distance = length(_WorldSpaceCameraPos - positionWS);
                float3 offset;
                float2 slope;
                float compression;
                Waves(xz, distance, 0, offset, slope, compression);
                slope *= _WaveStrength;

                // Two ripple layers drifting with and across the wind, fading with distance.
                float a = radians(_WindDirection);
                float2 wind = float2(cos(a), sin(a));
                float near = saturate(1.0 - distance / 700.0);
                float time = _Time.y * _WaveSpeed;
                half3 r1 = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, xz * _RippleTiling + wind * (time * 0.035)));
                half3 r2 = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, xz * (_RippleTiling * 0.37) + float2(-wind.y, wind.x) * (time * 0.017)));
                float2 ripple = (r1.xy * near + r2.xy * saturate(1.2 - distance / 2500.0)) * _RippleStrength;

                float3 normalWS = normalize(float3(-slope.x - ripple.x, 1.0, -slope.y - ripple.y));
                float3 viewWS = normalize(_WorldSpaceCameraPos - positionWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(positionWS));
                half shadow = light.shadowAttenuation;
                half ndotl = saturate(dot(normalWS, light.direction));
                half ndotv = saturate(dot(normalWS, viewWS));
                half3 ambient = SampleSH(normalWS);

                // Water body plus light scattered through wave crests seen against the sun.
                half height = saturate(compression / 0.35 * 0.5 + 0.5);   // 0 trough .. 1 crest
                half scatter = pow(saturate(dot(viewWS, -light.direction) * 0.6 + 0.4), 4.0) * saturate(compression * 3.0 + 0.3);
                half3 body = _BaseColor.rgb * (ambient * 0.8 + light.color * (0.2 + 0.25 * ndotl) * shadow);
                body += _ShallowColor.rgb * light.color * (scatter * 0.9 + height * 0.12) * shadow;

                // Sky reflection from a horizon-to-zenith gradient (the scene's reflection probe also holds the
                // satellite terrain, whose greens and yellows would tint the sea), sun haze toward the light.
                half fresnel = 0.02 + 0.98 * pow(1.0 - ndotv, 5.0);
                float3 ray = reflect(-viewWS, normalWS);
                half up = saturate(ray.y);
                half3 reflection = lerp(_SkyHorizon.rgb, _SkyZenith.rgb, sqrt(up));
                reflection += light.color * pow(saturate(dot(ray, light.direction)), 24.0) * 0.12;
                half3 color = lerp(body, reflection, saturate(fresnel));

                float3 halfway = normalize(light.direction + viewWS);
                half glint = pow(saturate(dot(normalWS, halfway)), lerp(2400.0, 400.0, saturate(distance / 2500.0))) * 1.6;
                color += light.color * min(glint, _Glint) * shadow;

                // Crest foam where the waves pile up, broken up by the ripples, near the camera only.
                half foam = smoothstep(0.24, 0.38, compression) * _Foam * saturate(1.0 - distance / 800.0);
                foam *= saturate((r1.x - r2.y) * 2.5 + 0.15);   // streaks, not blobs
                foam *= 0.75;
                color = lerp(color, _FoamColor.rgb * (ambient + light.color * shadow * 0.7), saturate(foam));

                color = MixFog(color, input.fog);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Unlit"
}
