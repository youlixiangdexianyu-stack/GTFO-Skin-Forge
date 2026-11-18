// The review sheet's floor (ForgeVfxSheet): a dark flat colour lit by one point light standing in for the slot's light (FxOptions.Light), so its
// reach and colour (in the caller's hue) show offline. An approximation only: the game draws these lights with its own light system (PointLights
// sets the game's light, not a Unity Light), whose falloff is not Unity's built-in one; here the colour times its intensity falls off as
// (1 - d^2/r^2)^2 to nothing at its range, brighter where it faces the light. The fallback's shadow caster puts the floor in the camera depth
// texture (soft particles fade on it).
Shader "Hidden/ForgeVfx/Sheet Floor"
{
    Properties
    {
        _Color ("Floor", Color) = (0.07, 0.075, 0.09, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            half4 _Color;
            float4 _SheetLight;        // xyz position, w range (0: no light)
            half4 _SheetLightColour;   // rgb colour times intensity

            struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                if (_SheetLight.w <= 0) return _Color;
                float3 toLight = _SheetLight.xyz - i.world;
                float reach = saturate(1 - dot(toLight, toLight) / (_SheetLight.w * _SheetLight.w));
                float facing = saturate(normalize(toLight).y);
                return half4(_Color.rgb + 0.45 * _SheetLightColour.rgb * reach * reach * facing, 1);
            }
            ENDCG
        }
    }
    Fallback "VertexLit"
}
