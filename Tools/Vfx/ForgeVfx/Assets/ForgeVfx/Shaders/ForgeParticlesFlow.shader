// Forge's flow-distortion particle shader for Unity 2019.4 built-in: a texture bent by a scrolling flow texture, shaped by a scrolling noise texture and
// a mask, driven by the particle's custom data. It draws Hovl Studio's Shader Graphs/HS_ShockWave term by term (fx_import maps that shader's
// materials onto it); the converter keeps the library's vertex streams for it: TEXCOORD0 = UV + custom1.xy, TEXCOORD1 = UV2 + custom2.xy.
//
//   noise  = _NoiseTex at UV scrolled by _NoiseSpeed.xy (per tiling);  flow = _FlowTex at UV scrolled by _FlowSpeed.xy (per tiling)
//   base   = UV + custom1.xy;  main = _MainTex at base + _FlowSpeed.z x flow.rg x (custom2.y with _CustomDistorts, else 1) / its tiling
//   shade  = noise opacity (noise.a towards 1 by _NoiseOpacityLerp) remapped from 0..1 to (1 - _NoiseSpeed.z)..(_NoiseSpeed.z)
//   colour = _Emission x noise.rgb x shade x saturate(|main| ^ (1 with _CustomDistorts, else custom2.y)) x vertex colour
//   alpha  = vertex alpha x custom2.x x shade x main.a x _MaskTex.a x (base inside the unit square), times the soft fade with _SOFT (ForgeSoft.cginc)
// Blend is two material floats as in Particles Uber (premultiplied One OneMinusSrcAlpha with _Premul 1). Unlit, no depth write, no fog.
Shader "ForgeVfx/Particles Flow"
{
    Properties
    {
        _MainTex ("Main texture", 2D) = "white" {}
        _NoiseTex ("Noise (colour and opacity)", 2D) = "white" {}
        _FlowTex ("Flow (bends the main texture by its red and green)", 2D) = "white" {}
        _MaskTex ("Mask (alpha)", 2D) = "white" {}
        _NoiseSpeed ("Noise scroll xy, opacity power z", Vector) = (0,0,1,0)
        _FlowSpeed ("Flow scroll xy, distortion power z", Vector) = (0,0,0,0)
        _Emission ("Emission", Float) = 2
        _NoiseOpacityLerp ("Noise opacity towards 1", Range(0, 1)) = 0
        [Toggle] _CustomDistorts ("custom2.y scales the distortion (off: it is the main texture's power)", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Toggle] _Premul ("Premultiply colour by alpha", Float) = 0
        [Toggle(_SOFT)] _UseSoft ("Soft particle depth fade", Float) = 0
        _SoftFade ("Soft fade (1 / metres)", Range(0.1, 20)) = 1
        _SoftInverse ("Soft fade measured in 1 / depth", Float) = 0
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
            #pragma shader_feature_local _SOFT
            #include "UnityCG.cginc"
            #include "ForgeSoft.cginc"

            sampler2D _MainTex; float4 _MainTex_ST;
            sampler2D _NoiseTex; float4 _NoiseTex_ST;
            sampler2D _FlowTex; float4 _FlowTex_ST;
            sampler2D _MaskTex; float4 _MaskTex_ST;
            float4 _NoiseSpeed;
            float4 _FlowSpeed;
            half _Emission;
            half _NoiseOpacityLerp;
            half _CustomDistorts;
            half _Premul;
            half _SoftFade;
            half _SoftInverse;

            struct appdata
            {
                float4 vertex : POSITION;
                half4 color : COLOR;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half4 color : COLOR;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
#if defined(_SOFT)
                float4 softPos : TEXCOORD2;
#endif
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv0 = v.uv0;
                o.uv1 = v.uv1;
#if defined(_SOFT)
                FORGE_SOFT_VERTEX(o, v)
#endif
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv0.xy;
                half4 grain = tex2D(_NoiseTex, TRANSFORM_TEX((uv + _NoiseSpeed.xy / _NoiseTex_ST.xy * _Time.y), _NoiseTex));
                half4 flow = tex2D(_FlowTex, TRANSFORM_TEX((uv + _FlowSpeed.xy / _FlowTex_ST.xy * _Time.y), _FlowTex));
                float2 base = uv + i.uv0.zw;
                half custom = i.uv1.w;
                half distorts = step(0.5, _CustomDistorts);
                float2 bend = _FlowSpeed.z * flow.rg * lerp(1, custom, distorts) / _MainTex_ST.xy;
                half4 main = tex2D(_MainTex, TRANSFORM_TEX((base + bend), _MainTex));
                half shade = lerp(1 - _NoiseSpeed.z, _NoiseSpeed.z, lerp(grain.a, 1, _NoiseOpacityLerp));
                half3 col = _Emission * grain.rgb * shade * saturate(pow(abs(main.rgb), lerp(custom, 1, distorts))) * i.color.rgb;
                half inside = ceil(base.x) == 1 && ceil(base.y) == 1 ? 1 : 0;
                half alpha = i.color.a * i.uv1.z * shade * main.a * inside * tex2D(_MaskTex, TRANSFORM_TEX(uv, _MaskTex)).a;
#if defined(_SOFT)
                alpha *= ForgeSoftFade(i.softPos, _SoftFade, _SoftInverse);
#endif
                alpha = saturate(alpha);
                col *= lerp(1, alpha, _Premul);
                return half4(col, alpha);
            }
            ENDCG
        }
    }
}
