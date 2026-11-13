// ForgeVisual's one particle and sprite shader: texture x tint x vertex colour, drawn either with ordinary alpha blending (smoke,
// dust, scorch marks) or additively (fire, flashes, glows, sparks, rings). The two modes are two materials of this shader
// (_Src/_Dst); with _Additive the colour is scaled by its alpha, so a fading additive sprite darkens instead of leaving an edge.
// The tint comes per sprite from a MaterialPropertyBlock (_Color) and per particle from the particle system's vertex colour.
// Built with Unity 2019.4.21f1 by tools/shaders/build_shaders.ps1 into Native/ShaderBundle.Generated.cs.
Shader "ForgeVisual/Particle"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _Src ("Source blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _Dst ("Destination blend", Float) = 10
        _Additive ("Scale colour by alpha (additive)", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend [_Src] [_Dst]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            // Unity 2019.4.21 shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; compiling unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Additive;

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                if (_Additive > 0.5) c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
