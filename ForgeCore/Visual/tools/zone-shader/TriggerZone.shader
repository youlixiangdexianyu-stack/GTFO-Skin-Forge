Shader "Forge/TriggerZone"
{
    Properties
    {
        _Color ("Colour", Color) = (1,0,0,1)
        _Opacity ("Opacity", Float) = 0.25
        _Glow ("Emission", Float) = 1
        _Pulse ("Pulse", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            float _Opacity, _Glow, _Pulse;
            struct Input { float4 vertex : POSITION; };
            struct Output { float4 vertex : SV_POSITION; };
            Output vert(Input input) { Output output; output.vertex = UnityObjectToClipPos(input.vertex); return output; }
            float4 frag(Output input) : SV_Target
            {
                return float4(_Color.rgb * (1 + max(0, _Glow)) * _Pulse, saturate(_Opacity) * _Pulse);
            }
            ENDCG
        }
    }
}
