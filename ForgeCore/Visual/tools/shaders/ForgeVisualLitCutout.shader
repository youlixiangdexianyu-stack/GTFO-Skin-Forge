// ForgeVisual lit cut-out: the game's gear shader (Cell/Player/CustomGearShader, `_ShadingType` 4) with an alpha test, which the game's own
// shader does not have. Same property names as the gear shader (base colour, metal-and-smoothness map, DXT5nm normal map, glow through
// Glow.Write), so LitMaterial fills it the same way. DEFERRED writes the G-buffer exactly as the gear shader's DEFERRED pass does
// (Mods/ForgeCore/evidence/toon-render-pipeline.md: RT0 diffuse = base x (1 - metal), RT1 specular = lerp(0.04, base, metal) with the
// smoothness under the same specular anti-aliasing, RT2 normal, RT3 emission with the shading type in alpha as (type + 1) / 255), so the
// game lights, shadows and fogs it and its thermal sight sees it; every pass that writes depth (DEFERRED, FORWARD for a forward camera,
// ShadowCaster) drops the pixels whose base-colour alpha is below `_Cutoff`.
Shader "ForgeVisual/LitCutout"
{
    Properties
    {
        _MainTex ("Base colour", 2D) = "white" {}
        _Color ("Base tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha cut-off", Range(0,1)) = 0.5
        _MetallicGlossMap ("Metal (R) and smoothness (G)", 2D) = "white" {}
        [Normal] _BumpMap ("Normal map", 2D) = "bump" {}
        _UseNormalMap ("Use normal map", Float) = 0
        _EmissiveMap ("Emission", 2D) = "black" {}
        [HDR] _EmissiveColor ("Emission colour", Color) = (1,1,1,1)
        _EnableEmissive ("Enable emission", Float) = 0
        [Enum(Normal,0,Translucent,1,Skin,2,Cloth,3,NormalWarm,4,SkinCold,5)] _ShadingType ("Shading type", Float) = 4
        _IsCustomGearMaterial ("Gear material", Float) = 1
        _SupportsFPSRendering ("Supports first person", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" }
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
            #pragma multi_compile _ UNITY_HDR_ON
            #define _ALPHATEST_ON 1
            // Unity 2019.4.21's shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            #include "ForgeToon.hlsl"

            sampler2D _MetallicGlossMap;
            float _ShadingType;

            void frag(ToonV2F i, out float4 diffuse : SV_Target0, out float4 specular : SV_Target1, out float4 normal : SV_Target2,
                out float4 emission : SV_Target3)
            {
                ToonCut(i.uv);
                float3 base = ToonBase(i.uv).rgb;
                float2 surface = tex2D(_MetallicGlossMap, i.uv).rg;
                float3 n = ToonNormal(i);
                float3 dx = ddx(n), dy = ddy(n);
                float smooth = min(1.0 - pow(max(dot(dx, dx), dot(dy, dy)), 0.333), surface.y);
                diffuse = float4(base * (1.0 - surface.x), 1.0);
                specular = float4(lerp(float3(0.04, 0.04, 0.04), base, surface.x), smooth);
                normal = float4(n * 0.5 + 0.5, 1.0);
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
            // A forward camera only (the deferred camera takes DEFERRED): lit from the game's clustered lights, ambient volume and fog as
            // ForgeVisualToonForward is, with the gear shader's matte diffuse instead of the toon step.
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
            #define _ALPHATEST_ON 1
            #pragma enable_d3d11_debug_symbols
            #include "ForgeToon.hlsl"

            sampler2D _MetallicGlossMap;

            float4 frag(ToonV2F i) : SV_Target
            {
                ToonCut(i.uv);
                float3 base = ToonBase(i.uv).rgb * (1.0 - tex2D(_MetallicGlossMap, i.uv).r);
                float3 n = ToonNormal(i);
                float3 light = float3(0.0, 0.0, 0.0), toLight;
                uint4 cell = ToonCluster(i.world);
                uint mask = cell.x;
                [loop] while (mask != 0u)
                {
                    uint k = firstbitlow(mask);
                    mask &= ~(1u << k);
                    float3 c = ToonPointLight(_PointPosInvRangeSq[k], _PointColorPhysical[k], i.world, toLight);
                    light += c * saturate(dot(n, toLight));
                }
                mask = cell.y;
                [loop] while (mask != 0u)
                {
                    uint k = firstbitlow(mask);
                    mask &= ~(1u << k);
                    float3 c = ToonPointLight(_EffectPosInvRangeSq[k], _EffectColorPhysical[k], i.world, toLight);
                    light += c * saturate(dot(n, toLight));
                }
                mask = cell.z;
                [loop] while (mask != 0u)
                {
                    uint k = firstbitlow(mask);
                    mask &= ~(1u << k);
                    float shadow;
                    float3 c = ToonSpotLight(k, i.world, toLight, shadow);
                    light += c * (shadow * saturate(dot(n, toLight)));
                }
                float3 colour = base * (light * FORGE_INV_PI + ToonAmbient(i.world, n)) + ToonEmission(i.uv);
                return float4(ToonFog(colour, i.world), 1.0);
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
            #define _ALPHATEST_ON 1
            #pragma enable_d3d11_debug_symbols
            #include "ForgeToon.hlsl"
            ENDCG
        }
    }
    Fallback Off
}
