// Screen distortion for heat haze, shock waves and warps: the particle bends what is behind it. The effect library does this with
// GrabPass-like opaque-texture reads (URP) or refraction nodes; here a named GrabPass copies the frame once per frame and every
// particle reads it, offset by a normal-like texture (_DistortTex, RG) or, when there is none, by the shape of _MainTex. ForgeUI's
// screen-effect shaders already use the same named GrabPass in this game, so the pass itself is known to run in its forward
// transparent queue. Draws in the transparent queue after opaque and earlier transparents (so it does not bend itself or later particles).
// Not valid in the shadow, depth or deferred passes; it has none.
Shader "ForgeVfx/Particles Distort"
{
    Properties
    {
        _MainTex ("Shape (A, or brightness when no alpha)", 2D) = "white" {}
        _DistortTex ("Distortion normal (RG)", 2D) = "bump" {}
        _Strength ("Strength", Range(0, 0.2)) = 0.03
        [HDR] _TintColor ("Tint over the bent picture", Color) = (1,1,1,0)
        _Scroll ("Distortion scroll (xy per second)", Vector) = (0,0.2,0,0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent+1" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        GrabPass { "_ForgeVfxGrab" }
        Cull Off
        ZWrite Off
        Lighting Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _ForgeVfxGrab;
            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _DistortTex;
            float4 _DistortTex_ST;
            half _Strength;
            half4 _TintColor;
            float4 _Scroll;

            struct v2f
            {
                float4 pos : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 grab : TEXCOORD1;
            };

            v2f vert (appdata_full v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.texcoord.xy;
                o.grab = ComputeGrabScreenPos(o.pos);
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                half4 shape = tex2D(_MainTex, TRANSFORM_TEX(i.uv, _MainTex));
                half mask = shape.a * max(max(shape.r, shape.g), shape.b) * i.color.a;
                half2 bend = tex2D(_DistortTex, TRANSFORM_TEX(i.uv, _DistortTex) + _Scroll.xy * _Time.y).rg * 2 - 1;
                float2 grabUv = i.grab.xy / i.grab.w + bend * _Strength * mask;
                half3 behind = tex2D(_ForgeVfxGrab, grabUv).rgb;
                half3 col = lerp(behind, behind * _TintColor.rgb, _TintColor.a * mask);
                return half4(col, 1);
            }
            ENDCG
        }
    }
}
