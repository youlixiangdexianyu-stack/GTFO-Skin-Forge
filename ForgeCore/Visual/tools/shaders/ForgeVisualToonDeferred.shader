// ForgeVisual toon, deferred entry (prototype "deferred"): writes the game's G-buffer exactly as Cell/Player/CustomGearShader's DEFERRED
// pass does (RT0 diffuse, RT1 specular colour and smoothness, RT2 normal, RT3 emission with the shading type in alpha as (type + 1) / 255),
// so the game lights, shadows and fogs it and its thermal sight and decals see it. The surface is a matte dielectric (dim specular, low
// smoothness): the game's light gives soft shading, which ForgeVisualToonOverlay then steps into toon tones, adding rim, highlight and outline.
Shader "ForgeVisual/ToonDeferred"
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
        [Enum(Normal,0,Translucent,1,Skin,2,Cloth,3,NormalWarm,4,SkinCold,5)] _ShadingType ("Shading type", Float) = 4
    }
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }
        Pass
        {
            Name "DEFERRED"
            Tags { "LightMode" = "Deferred" }
            Cull Back
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex ToonVert
            #pragma fragment frag
            #pragma target 5.0
            #pragma multi_compile _ FPS_RENDERING_ALLOWED ENABLE_FPS_RENDERING
            #pragma multi_compile_local _ _ALPHATEST_ON
            #pragma multi_compile _ UNITY_HDR_ON
            // Unity 2019.4.21's shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            #include "ForgeToon.hlsl"

            float _ShadingType;

            void frag(ToonV2F i, out float4 diffuse : SV_Target0, out float4 specular : SV_Target1, out float4 normal : SV_Target2,
                out float4 emission : SV_Target3)
            {
                ToonCut(i.uv);
                float4 base = ToonBase(i.uv);
                diffuse = float4(base.rgb, 1.0);
                specular = float4(0.02, 0.02, 0.02, 0.1);
                normal = float4(ToonNormal(i) * 0.5 + 0.5, 1.0);
                float3 e = ToonEmission(i.uv);
#ifndef UNITY_HDR_ON
                e = exp2(-e);
#endif
                emission = float4(e, (_ShadingType + 1.0) / 255.0);
            }
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
