// ForgeVisual toon, the overlay of the deferred entry: drawn over a ForgeVisual/ToonDeferred surface (same mesh, same material values) in the
// transparent queue. It takes the frame the game lit (one shared grab per frame), undoes the game's fog and the surface's own emission and
// albedo to get the light that reached the surface, steps that light into a shade and a lit tone (the step is measured against the game's
// ambient there: light no brighter than the ambient is shade, 16 times the ambient is fully lit) and flattens its level into half-stop bands,
// then adds the rim and the fog back. A second pass draws the outline. The game's lighting has already been computed per pixel, so this can
// only approximate a toon ramp: the light's direction is not known here.
Shader "ForgeVisual/ToonOverlay"
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
        _EmissiveMap ("Emission", 2D) = "black" {}
        [HDR] _EmissiveColor ("Emission colour", Color) = (1,1,1,1)
        _EnableEmissive ("Enable emission", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent-100" "RenderType" = "Transparent" "IgnoreProjector" = "True" "ForceNoShadowCasting" = "True" }
        GrabPass { "_ForgeToonLit" }
        Pass
        {
            Name "STEP"
            Cull Back
            ZWrite Off
            ZTest LEqual
            Offset -1, -1
            Blend One Zero

            CGPROGRAM
            #pragma vertex ToonVert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile _ FPS_RENDERING_ALLOWED ENABLE_FPS_RENDERING
            #pragma multi_compile_local _ _ALPHATEST_ON
            // Unity 2019.4.21's shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            #include "ForgeToon.hlsl"

            sampler2D _ForgeToonLit;

            float4 frag(ToonV2F i) : SV_Target
            {
                ToonCut(i.uv);
                float3 lit = tex2Dproj(_ForgeToonLit, i.grab).rgb;
                float3 n = ToonNormal(i);
                float3 toEye = normalize(_WorldSpaceCameraPos - i.world);
                float3 emission = ToonEmission(i.uv);
                float3 surface = lit, transmit = float3(1.0, 1.0, 1.0), inscatter = float3(0.0, 0.0, 0.0);
                if (_VolumesEnabled != 0.0)
                {
                    float4 fog = tex3Dlod(_FogVolume, float4(ToonFroxel(i.world), 0.0));
                    transmit = exp2(-fog.a * _FogColor.rgb * 1.442695);
                    inscatter = fog.rgb;
                    surface = max(lit - inscatter, 0.0) / max(transmit, 0.001);
                }
                float3 albedo = max(ToonBase(i.uv).rgb, 0.02);
                float3 light = max(surface - emission, 0.0) / albedo;
                float level = max(ToonLuminance(light), 1e-5);
                float3 hue = light / level;
                float ambient = max(ToonLuminance(ToonAmbient(i.world, n)), 1e-5);
                float lit01 = ToonStep(saturate(log2(max(level / ambient, 1.0)) / 4.0));
                float banded = exp2(floor(log2(level) * 2.0 + 0.5) * 0.5);
                float3 toonLight = hue * banded * lerp(_ShadeColor.rgb, float3(1.0, 1.0, 1.0), lit01);
                float3 colour = albedo * toonLight + _RimColor.rgb * ToonRim(n, toEye) * level + emission;
                return float4(colour * transmit + inscatter, 1.0);
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
    }
    Fallback Off
}
