// ForgeVisual toon, forward entry (prototype "forward"): an opaque forward pass the game draws after its deferred lighting. It lights the
// surface itself from the game's clustered lights (point, effect and spot lights with their shadow atlas), its indirect volume and its
// volumetric fog (ForgeToon.hlsl), stepping each light into a lit and a shaded tone. It writes no G-buffer: the game's thermal sight, screen
// reflections and deferred decals do not see it. The outline is a second pass (the back faces pushed out).
Shader "ForgeVisual/ToonForward"
{
    Properties
    {
        _MainTex ("Base colour", 2D) = "white" {}
        _Color ("Base tint", Color) = (1,1,1,1)
        [Normal] _BumpMap ("Normal map", 2D) = "bump" {}
        _UseNormalMap ("Use normal map", Float) = 0
        _ShadeColor ("Shade colour", Color) = (0.45,0.42,0.55,1)
        _ShadeRamp ("Shade ramp", 2D) = "white" {}
        _UseShadeRamp ("Use shade ramp", Float) = 0
        _ShadeThreshold ("Shade threshold", Range(0,1)) = 0.5
        _ShadeSoftness ("Shade softness", Range(0,0.5)) = 0.03
        _OutlineColor ("Outline colour", Color) = (0,0,0,1)
        _OutlineWidth ("Outline width (px at 1080p)", Range(0,8)) = 1.5
        _RimColor ("Rim colour", Color) = (1,1,1,1)
        _RimPower ("Rim power", Range(0.5,8)) = 4
        _RimStrength ("Rim strength", Range(0,2)) = 0.3
        _HighlightColor ("Highlight colour", Color) = (1,1,1,1)
        _HighlightSize ("Highlight size", Range(0,1)) = 0.05
        _HighlightSoftness ("Highlight softness", Range(0,0.2)) = 0.01
        _EmissiveMap ("Emission", 2D) = "black" {}
        [HDR] _EmissiveColor ("Emission colour", Color) = (1,1,1,1)
        _EnableEmissive ("Enable emission", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }
        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }
            Cull Back
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex ToonVert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile _ FPS_RENDERING_ALLOWED ENABLE_FPS_RENDERING
            #pragma multi_compile_local _ _ALPHATEST_ON
            // Unity 2019.4.21's shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            #include "ForgeToon.hlsl"

            // The game lights a surface by light * N.L (half-Lambert for skin and cloth), so the lit tone is light * N.L too, never full light;
            // it never drops below the shaded tone, so the step from shade to light stays a step and not a dark seam. The shadow is the
            // game's exponential one: soft where another body casts it, but on the side facing away the body shadows itself and the
            // exponential test there follows the body's thickness (thin parts half lit, thick parts dark), which the game hides behind
            // N.L = 0. The shaded tone therefore takes the shadow stepped to all or nothing, as the game's deferred path leaves that side
            // dark from this light; shading it without the shadow would light the far side through the body and through walls.
            void Accumulate(float3 light, float shadow, float3 toLight, float3 n, float3 toEye, inout float3 diffuse, inout float3 highlight)
            {
                float nl = dot(n, toLight);
                float wrap = saturate(nl * 0.5 + 0.5);
                float lit = ToonStep(wrap);
                float3 shade = _ShadeColor.rgb * (wrap * smoothstep(0.5, 0.75, shadow));
                diffuse += light * lerp(shade, max(saturate(nl) * shadow, shade), lit);
                highlight += light * (lit * shadow * ToonHighlight(n, toLight, toEye));
            }

            float4 frag(ToonV2F i) : SV_Target
            {
                ToonCut(i.uv);
                float4 base = ToonBase(i.uv);
                float3 n = ToonNormal(i);
                float3 toEye = normalize(_WorldSpaceCameraPos - i.world);
                float3 diffuse = float3(0.0, 0.0, 0.0), highlight = float3(0.0, 0.0, 0.0), toLight;
                uint4 cell = ToonCluster(i.world);
                uint mask = cell.x;
                [loop] while (mask != 0u)
                {
                    uint k = firstbitlow(mask);
                    mask &= ~(1u << k);
                    float3 c = ToonPointLight(_PointPosInvRangeSq[k], _PointColorPhysical[k], i.world, toLight);
                    Accumulate(c, 1.0, toLight, n, toEye, diffuse, highlight);
                }
                mask = cell.y;
                [loop] while (mask != 0u)
                {
                    uint k = firstbitlow(mask);
                    mask &= ~(1u << k);
                    float3 c = ToonPointLight(_EffectPosInvRangeSq[k], _EffectColorPhysical[k], i.world, toLight);
                    Accumulate(c, 1.0, toLight, n, toEye, diffuse, highlight);
                }
                mask = cell.z;
                [loop] while (mask != 0u)
                {
                    uint k = firstbitlow(mask);
                    mask &= ~(1u << k);
                    float shadow;
                    float3 c = ToonSpotLight(k, i.world, toLight, shadow);
                    Accumulate(c, shadow, toLight, n, toEye, diffuse, highlight);
                }
                float3 ambient = ToonAmbient(i.world, n);
                // The game's diffuse is albedo * light * N.L / pi.
                float3 lightAll = diffuse * FORGE_INV_PI + ambient * _ShadeColor.rgb;
                float3 colour = base.rgb * lightAll + highlight * FORGE_INV_PI * _HighlightColor.rgb;
                colour += _RimColor.rgb * ToonRim(n, toEye) * ToonLuminance(lightAll + ambient);
                colour += ToonEmission(i.uv);
                return float4(ToonFog(colour, i.world), 1.0);
            }
            ENDCG
        }
        Pass
        {
            Name "OUTLINE"
            Cull Front
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex ToonOutlineVert
            #pragma fragment ToonOutlineFrag
            #pragma target 5.0
            #pragma multi_compile _ FPS_RENDERING_ALLOWED ENABLE_FPS_RENDERING
            #pragma multi_compile_local _ _ALPHATEST_ON
            #pragma enable_d3d11_debug_symbols
            #include "ForgeToon.hlsl"
            ENDCG
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull Back
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex ToonShadowVert
            #pragma fragment ToonShadowFrag
            #pragma target 5.0
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_local _ _ALPHATEST_ON
            #pragma enable_d3d11_debug_symbols
            #include "ForgeToon.hlsl"
            ENDCG
        }
    }
    Fallback Off
}
