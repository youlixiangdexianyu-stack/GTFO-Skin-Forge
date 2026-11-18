// Forge's own alpha-blended particle shader for smoke and dust layers; ships inside the effect bundle like the additive
// one. Unlit, no depth write, no fog and no soft-particle depth read.
Shader "ForgeVfx/Particles Alpha Blended"
{
    Properties
    {
        _MainTex ("Particle Texture", 2D) = "white" {}
        _TintColor ("Tint Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
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
                return half4(texel.rgb * i.color.rgb * _TintColor.rgb, saturate(texel.a * i.color.a * _TintColor.a * ForgeLineFade(i.lineWorld)));
            }
            ENDCG
        }
    }
}
