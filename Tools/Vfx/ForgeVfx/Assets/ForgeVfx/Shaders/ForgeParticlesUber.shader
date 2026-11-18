// Forge's general particle shader for Unity 2019.4 built-in. One shader replaces the custom particle shaders of the effect library
// (Hovl DissolveNoise / AddTrail, SineVFX DissolveParticle*, GabrielAguiar Erosion_Unlit, Piloto UberFXSG, MasterMagicFX UberParticles):
// each of those is a texture, a tint, and a few of the features below. Features are keywords, so an effect that uses none of them
// costs the same as ForgeVfx/Particles Additive. Blend is two material floats: additive SrcAlpha One, alpha SrcAlpha OneMinusSrcAlpha,
// premultiplied One OneMinusSrcAlpha with _Premul = 1. Unlit, no depth write, no fog.
//
// Particle data: vertex colour is the system colour and alpha. With _DISSOLVE_ALPHA the particle's alpha is not a fade but the dissolve
// progress (the SineVFX / Hovl way: the shape erodes away as the particle ages). _FLIPBOOK reads the next frame from TEXCOORD1.xy and the
// blend factor from TEXCOORD1.z (renderer vertex streams UV2 + AnimBlend, set by ForgeVfxConvert).
// _TINT takes its hue from _Hue (a fixed hue written by @colour) times the caller's _Color weighted by _CallerWeight (1 with @tint).
// _SOFT fades a particle where it meets the scene over 1 / _SoftFade metres (ForgeSoft.cginc: only while the camera has a depth texture).
// _CUSTOM_DATA reads the particle's custom data streams (Custom1XYZW after UV: TEXCOORD0.zw + TEXCOORD1.xy, set by ForgeVfxConvert) as Master MagicFX
// UberParticles does: custom1.x adds to the colour gain, custom1.y moves the main texture by _MainCustomOffset, custom1.z the mask by
// _MaskCustomOffset, custom1.w adds to the mask threshold. _MaskThreshold makes the mask a feathered threshold (that shader's Mask subgraph) instead
// of a factor; _RampLuminance indexes the ramp by 1 - remapped luminance (its LUT); _EDGEFADE fades by the angle between surface and view (its edge fade);
// _WARP moves the main texture by its procedural noise (Shader Graph Simple and Gradient Noise of the scrolled UV, ForgeNoise.cginc); _SoftInverse measures
// the soft fade in 1 / depth, as its raw depth difference does.
// Every one of these is off at its default, so a material that sets none of them draws as before.
Shader "ForgeVfx/Particles Uber"
{
    Properties
    {
        _MainTex ("Particle Texture", 2D) = "white" {}
        [HDR] _TintColor ("Tint Color", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 1
        [Toggle] _Premul ("Premultiply colour by alpha", Float) = 0

        [Toggle(_ALPHA_FROM_RGB)] _AlphaFromRgb ("Alpha from brightest colour channel", Float) = 0
        [Toggle(_ALPHA_ONLY)] _AlphaOnly ("Texture is alpha only: colour comes from the tint", Float) = 0
        [Toggle(_FLIPBOOK)] _Flipbook ("Flipbook frame blending", Float) = 0

        [Toggle(_MASK)] _UseMask ("Mask", Float) = 0
        _MaskTex ("Mask", 2D) = "white" {}
        _MaskChannel ("Mask channel weights", Vector) = (1,0,0,0)
        _MaskScroll ("Mask scroll (xy per second)", Vector) = (0,0,0,0)
        _MaskReplace ("Mask replaces the texture's alpha (Piloto alpha override)", Float) = 0
        _MaskThreshold ("Mask is a feathered threshold at _MaskIntensity, not a factor", Float) = 0
        _MaskFeather ("Mask threshold feather", Float) = 0.2
        _MaskIntensity ("Mask threshold (0 open, 1 closed)", Float) = 0

        _MainScroll ("Main texture scroll (xy per second, after tiling)", Vector) = (0,0,0,0)
        _AddIntensity ("Colour gain above 1", Float) = 0
        [Toggle(_CUSTOM_DATA)] _UseCustomData ("Custom data streams (Master MagicFX)", Float) = 0
        _MainCustomOffset ("Main texture offset per custom1.y", Vector) = (0,0,0,0)
        _MaskCustomOffset ("Mask offset per custom1.z", Vector) = (0,0,0,0)
        [Toggle(_EDGEFADE)] _UseEdgeFade ("Fade by the angle between surface and view", Float) = 0
        _EdgeFade ("Edge fade: facing below x is clear, above y is solid", Vector) = (0,1,0,0)
        [Toggle(_WARP)] _UseWarp ("Main texture warped by procedural noise (Master MagicFX)", Float) = 0
        _WarpIntensity ("Warp: UV offset per noise unit", Float) = 0
        _WarpScale ("Warp: noise scale", Float) = 1
        _WarpScroll ("Warp: noise scroll (xy per second)", Vector) = (0,0,0,0)

        [Toggle(_DISSOLVE)] _UseDissolve ("Dissolve", Float) = 0
        [Toggle(_DISSOLVE_ALPHA)] _DissolveByAlpha ("Dissolve driven by particle alpha", Float) = 0
        _NoiseTex ("Dissolve noise (R)", 2D) = "white" {}
        _Dissolve ("Dissolve amount", Range(0, 1)) = 0
        _DissolveSoft ("Dissolve edge softness", Range(0.001, 1)) = 0.1
        [HDR] _EdgeColor ("Dissolve edge colour", Color) = (0,0,0,0)
        _EdgeWidth ("Dissolve edge width", Range(0.001, 0.5)) = 0.05
        _NoiseScroll ("Noise scroll (xy per second)", Vector) = (0,0,0,0)

        [Toggle(_UVNOISE)] _UseUvNoise ("UV noise warp", Float) = 0
        _UVNoiseTex ("UV noise (RG)", 2D) = "gray" {}
        _UVNoiseStrength ("UV noise strength", Range(0, 0.5)) = 0.05
        _UVNoiseScroll ("UV noise scroll (xy per second)", Vector) = (0.1,0.1,0,0)

        [Toggle(_RAMP)] _UseRamp ("Colour ramp", Float) = 0
        _RampTex ("Ramp (indexed by brightness)", 2D) = "white" {}
        _RampLuminance ("Ramp indexed by 1 - remapped luminance of texture x vertex colour, which it replaces", Float) = 0
        _RampRemap ("Luminance remap (dark, white)", Vector) = (0,1,0,0)

        [Toggle(_SOFT)] _UseSoft ("Soft particle depth fade", Float) = 0
        _SoftFade ("Soft fade (1 / metres)", Range(0.1, 20)) = 1
        _SoftInverse ("Soft fade measured in 1 / depth (_SoftFade per unit of it)", Float) = 0

        [Toggle(_EMISSION)] _UseEmission ("Emission map added to the colour (URP / Standard particle emission)", Float) = 0
        _EmissionTex ("Emission", 2D) = "black" {}
        [HDR] _EmissionColor ("Emission colour", Color) = (0,0,0,0)
        [Toggle(_TINT)] _UseTint ("Hue from the caller's colour and/or a fixed hue", Float) = 0
        _Color ("Caller colour (ForgeVisual Fx sets it per copy)", Color) = (1,1,1,1)
        _Hue ("Fixed hue (@colour)", Color) = (1,1,1,1)
        _CallerWeight ("Caller colour weight (1 with @tint, 0 for @colour alone)", Float) = 1

    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Blend [_SrcBlend] [_DstBlend]
        ColorMask RGB
        Cull Off
        Lighting Off
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma shader_feature_local _ALPHA_FROM_RGB
            #pragma shader_feature_local _ALPHA_ONLY
            #pragma shader_feature_local _FLIPBOOK
            #pragma shader_feature_local _MASK
            #pragma shader_feature_local _DISSOLVE
            #pragma shader_feature_local _DISSOLVE_ALPHA
            #pragma shader_feature_local _UVNOISE
            #pragma shader_feature_local _RAMP
            #pragma shader_feature_local _SOFT
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _TINT
            #pragma shader_feature_local _CUSTOM_DATA
            #pragma shader_feature_local _EDGEFADE
            #pragma shader_feature_local _WARP
            #include "UnityCG.cginc"
            #include "ForgeSoft.cginc"
            #include "ForgeLineFade.cginc"
            #include "ForgeNoise.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half4 _TintColor;
            half4 _Color;
            half4 _Hue;
            half _CallerWeight;
            half _Premul;
            half _SrcBlend;

            sampler2D _MaskTex;
            float4 _MaskTex_ST;
            half4 _MaskChannel;
            float4 _MaskScroll;
            half _MaskReplace;
            half _MaskThreshold;
            half _MaskFeather;
            half _MaskIntensity;

            float4 _MainScroll;
            half _AddIntensity;
            float4 _MainCustomOffset;
            float4 _MaskCustomOffset;
            float4 _EdgeFade;
            float _WarpIntensity;
            float _WarpScale;
            float4 _WarpScroll;
            half _RampLuminance;
            float4 _RampRemap;

            sampler2D _NoiseTex;
            float4 _NoiseTex_ST;
            half _Dissolve;
            half _DissolveSoft;
            half4 _EdgeColor;
            half _EdgeWidth;
            float4 _NoiseScroll;

            sampler2D _UVNoiseTex;
            float4 _UVNoiseTex_ST;
            half _UVNoiseStrength;
            float4 _UVNoiseScroll;

            sampler2D _RampTex;

            sampler2D _EmissionTex;
            float4 _EmissionTex_ST;
            half4 _EmissionColor;

            half _SoftFade;
            half _SoftInverse;

            // flipbook blending and custom data both ride TEXCOORD1; a layer never has both (ForgeVfxConvert), flipbook wins if it did
#if defined(_CUSTOM_DATA) && !defined(_FLIPBOOK)
#define FORGE_CUSTOM 1
#endif

            struct appdata
            {
                float4 vertex : POSITION;
                half4 color : COLOR;
                float4 uv : TEXCOORD0;
#if defined(_FLIPBOOK)
                float3 uv2 : TEXCOORD1;
#elif defined(FORGE_CUSTOM)
                float4 custom : TEXCOORD1;
#endif
#if defined(_EDGEFADE)
                float3 normal : NORMAL;
#endif
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
#if defined(_FLIPBOOK)
                float3 uv2 : TEXCOORD1;
#endif
#if defined(_SOFT)
                float4 softPos : TEXCOORD2;
#endif
                float3 lineWorld : TEXCOORD3;
#if defined(FORGE_CUSTOM)
                float4 custom : TEXCOORD4; // custom1.xyzw
#endif
#if defined(_EDGEFADE)
                float3 edgeNormal : TEXCOORD5;
                float3 edgeView : TEXCOORD6;
#endif
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv.xy;
#if defined(_FLIPBOOK)
                o.uv2 = v.uv2;
#elif defined(FORGE_CUSTOM)
                o.custom = float4(v.uv.zw, v.custom.xy);
#endif
#if defined(_EDGEFADE)
                o.edgeNormal = UnityObjectToWorldNormal(v.normal);
                o.edgeView = WorldSpaceViewDir(v.vertex);
#endif
#if defined(_SOFT)
                FORGE_SOFT_VERTEX(o, v)
#endif
                FORGE_LINE_VERTEX(o, v)
                return o;
            }

            half4 SampleMain (float2 uv, float2 shift)
            {
                half4 t = tex2D(_MainTex, TRANSFORM_TEX(uv, _MainTex) + shift);
#if defined(_ALPHA_FROM_RGB)
                t.a = max(max(t.r, t.g), t.b);
#endif
#if defined(_ALPHA_ONLY)
                t.rgb = 1;
#endif
                return t;
            }

            half4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
#if defined(_UVNOISE)
                float2 warp = tex2D(_UVNoiseTex, uv * _UVNoiseTex_ST.xy + _UVNoiseTex_ST.zw + _UVNoiseScroll.xy * _Time.y).rg * 2 - 1;
                uv += warp * _UVNoiseStrength;
#endif
                float2 shift = _MainScroll.xy * _Time.y;
                half gain = 1 + _AddIntensity;
#if defined(FORGE_CUSTOM)
                shift += _MainCustomOffset.xy * i.custom.y;
                gain += i.custom.x;
#endif
#if defined(_WARP)
                // Master MagicFX's warp: (Simple Noise, Gradient Noise) - 0.5 of the particle UV scrolled by _WarpScroll, times _WarpIntensity, added to the main UV
                float2 warpUv = i.uv + _WarpScroll.xy * _Time.y;
                shift += (float2(ForgeSimpleNoise(warpUv, _WarpScale), ForgeGradientNoise(warpUv, _WarpScale)) - 0.5) * _WarpIntensity;
#endif
                half4 t = SampleMain(uv, shift);
#if defined(_FLIPBOOK)
                t = lerp(t, SampleMain(i.uv2.xy + (uv - i.uv), shift), i.uv2.z);
#endif
                half alpha = t.a;
                half3 rgb = t.rgb;
                half3 vertexRgb = i.color.rgb;
#if defined(_MASK)
                float2 maskUv = TRANSFORM_TEX(i.uv, _MaskTex) + _MaskScroll.xy * _Time.y;
                half maskAt = _MaskIntensity;
#if defined(FORGE_CUSTOM)
                maskUv += _MaskCustomOffset.xy * i.custom.z;
                maskAt += i.custom.w;
#endif
                half mask = dot(tex2D(_MaskTex, maskUv), _MaskChannel);
                // Master MagicFX's mask: a smooth step of the mask's width 2 x feather, its middle swept from fully open (0) to closed (1) by the threshold
                half feather = max(_MaskFeather, 0.0001);
                half middle = lerp(-feather, 1 + feather, maskAt);
                mask = lerp(mask, smoothstep(middle - feather, middle + feather, mask), step(0.5, _MaskThreshold));
                alpha = lerp(alpha * mask, mask, _MaskReplace);
#endif
                half particleAlpha = i.color.a;
#if defined(_DISSOLVE)
                half n = tex2D(_NoiseTex, TRANSFORM_TEX(i.uv, _NoiseTex) + _NoiseScroll.xy * _Time.y).r;
#if defined(_DISSOLVE_ALPHA)
                half threshold = (1 - particleAlpha) * (1 + _DissolveSoft + _EdgeWidth) + _Dissolve;
                particleAlpha = 1;
#else
                half threshold = _Dissolve * (1 + _DissolveSoft + _EdgeWidth);
#endif
                half past = n - threshold + _DissolveSoft;
                half cut = saturate(past / _DissolveSoft);
                half edge = saturate(1 - (n - threshold) / _EdgeWidth) * cut;
                alpha *= cut;
                rgb += _EdgeColor.rgb * edge * _EdgeColor.a;
#endif
#if defined(_RAMP)
                if (_RampLuminance > 0.5)
                {
                    // Master MagicFX's LUT: the ramp at 1 - remapped luminance of texture x vertex colour (bright at the ramp's start), kept off its end texels;
                    // it replaces the colour, vertex colour included
                    half luma = dot(rgb * vertexRgb, half3(0.2989, 0.587, 0.114));
                    rgb = tex2D(_RampTex, float2(clamp(1 - lerp(_RampRemap.x, _RampRemap.y, luma), 0.01, 0.95), 0)).rgb;
                    vertexRgb = 1;
                }
                else
                    rgb = tex2D(_RampTex, float2(saturate(max(max(rgb.r, rgb.g), rgb.b) * alpha), 0.5)).rgb;
#endif
                half4 tint = _TintColor;
                half3 col = rgb * vertexRgb * tint.rgb * gain;
#if defined(_EMISSION)
                // as URP's particle shaders: the emission map times the emission colour is added to the colour, not tinted by the particle
                half3 glow = tex2D(_EmissionTex, TRANSFORM_TEX(uv, _EmissionTex)).rgb;
#if defined(_FLIPBOOK)
                glow = lerp(glow, tex2D(_EmissionTex, TRANSFORM_TEX(i.uv2.xy + (uv - i.uv), _EmissionTex)).rgb, i.uv2.z);
#endif
                col += glow * _EmissionColor.rgb;
#endif
                alpha *= particleAlpha * tint.a;
#if defined(_TINT)
                // The authored hue gives way to the caller's colour at the same brightness; the hottest part of the texture stays white, as a light's core
                // does. Hot is judged without the tint's gain above 1, so a bright tint brightens the layer without turning it white (a tint up to 1 is
                // judged as before).
                half luma = dot(col, half3(0.299, 0.587, 0.114));
                half hot = luma / max(1, max(max(tint.r, tint.g), tint.b));
                half3 want = _Hue.rgb * lerp(half3(1, 1, 1), _Color.rgb, _CallerWeight);
                half3 hue = want / max(dot(want, half3(0.299, 0.587, 0.114)), 0.05);
                col = lerp(luma * hue, luma.xxx, saturate((hot - 0.8) * 1.5));
#endif
                half fade = ForgeLineFade(i.lineWorld);
#if defined(_EDGEFADE)
                half facing = abs(dot(normalize(i.edgeNormal), normalize(i.edgeView)));
                fade *= saturate((facing - _EdgeFade.x) / (_EdgeFade.y - _EdgeFade.x));
#endif
#if defined(_SOFT)
                fade *= ForgeSoftFade(i.softPos, _SoftFade, _SoftInverse);
#endif
                alpha *= fade;
                // A One-blend layer whose colour is not premultiplied here (_Premul 0: the texture carries it) fades its colour too; with _Premul the
                // line below multiplies the colour by the already faded alpha, so it is faded once.
                col *= _SrcBlend < 1.5 && _Premul < 0.5 ? fade : 1;
                col *= lerp(1, alpha, _Premul);
                return half4(col, alpha);
            }
            ENDCG
        }
    }
}
