// Forge's own particle shader: it ships inside the effect bundle, so the game never has to resolve a built-in or
// project shader by name. Additive, unlit, no depth write, no fog and no soft-particle depth read, which keeps it
// independent of the game's own camera setup. Vertex color carries the particle system's color and alpha; _TintColor
// is HDR so an effect can drive the game's bloom.
Shader "ForgeVfx/Particles Additive"
{
    Properties
    {
        _MainTex ("Particle Texture", 2D) = "white" {}
        [HDR] _TintColor ("Tint Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Blend One One
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
            #include "UnityCG.cginc"
            #include "ForgeLineFade.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half4 _TintColor;

            struct appdata
            {
                float4 vertex : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 lineWorld : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                FORGE_LINE_VERTEX(o, v)
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                half4 texel = tex2D(_MainTex, i.uv);
                half alpha = texel.a * i.color.a * _TintColor.a * ForgeLineFade(i.lineWorld);
                return half4(texel.rgb * i.color.rgb * _TintColor.rgb * alpha, 0);
            }
            ENDCG
        }
    }
}
