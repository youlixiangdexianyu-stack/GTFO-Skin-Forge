// ForgeUI's power-on panel (Native/PowerOn.cs): one panel of a screen power-on draws the picture already on the screen (the game's own
// frame, its HUD included, copied once per frame by the named GrabPass) with a glitch on it, or plain black. The glitch: the picture
// monochrome in the tint, banded, scanlined, split into three copies slid sideways (left, in place, right) each lit in its channel colour,
// a few sideways tears with a wider split lit in one channel colour, short noise bars in the channel colours and a fine colour grain.
// The mod gives the colours; PowerOnTimeline (Core) gives the numbers. Built with Unity 2019.4.21f1 by tools/screen/build_screen_shaders.ps1
// and embedded in the plugin.
Shader "ForgeUI/PowerOnPanel"
{
    Properties
    {
        _State ("Glitch strength 0..1, mix to black 0..1, opacity 0..1, seed", Vector) = (0, 1, 1, 0)
        _Tint ("Monochrome tint of the picture coming up", Color) = (0.80, 0.94, 1.0, 1)
        _ChannelLeft ("Colour of the copy slid left", Color) = (1, 0, 0, 1)
        _ChannelMiddle ("Colour of the copy in place", Color) = (0, 1, 0, 1)
        _ChannelRight ("Colour of the copy slid right", Color) = (0, 0, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        GrabPass { "_ForgeUIPowerOnGrab" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            // Unity 2019.4.21 shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; compiling unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            #include "UnityCG.cginc"

            sampler2D _ForgeUIPowerOnGrab;
            float4 _State;
            fixed4 _Tint;
            fixed4 _ChannelLeft;
            fixed4 _ChannelMiddle;
            fixed4 _ChannelRight;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float4 grab : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.grab = ComputeGrabScreenPos(o.pos);
                return o;
            }

            float hash(float n) { return frac(sin(n * 12.9898) * 43758.5453); }

            float luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

            float3 channel(float h) { return h < 0.3333 ? _ChannelLeft.rgb : (h < 0.6667 ? _ChannelMiddle.rgb : _ChannelRight.rgb); }

            // The picture at uv: monochrome in the tint, banded into fewer and fewer steps, its highlights blown to white, fine scanlines.
            float3 look(float2 uv, float shift)
            {
                float3 src = tex2D(_ForgeUIPowerOnGrab, uv).rgb;
                float l = luma(src);
                float steps = lerp(48.0, 9.0, shift);
                l = floor(l * steps + 0.5) / steps;
                float3 mono = lerp(_Tint.rgb * l * 1.25, float3(1.0, 1.0, 1.0), smoothstep(0.62, 1.0, l));
                mono *= 1.0 - 0.18 * shift * step(0.5, frac(uv.y * 270.0));
                return lerp(src, mono, saturate(shift * 1.6)) * (1.0 + shift * 0.25);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float shift = _State.x, black = _State.y, alpha = _State.z, seed = _State.w;
                float2 uv = i.grab.xy / i.grab.w;
                float tick = floor(_Time.y * 24.0);
                // Sideways tears: a few of the thin bands slide, more of them the stronger the glitch.
                float band = floor(uv.y * 34.0);
                float tearOn = step(1.0 - 0.14 * shift, hash(band * 7.13 + tick * 3.7 + seed * 11.0));
                float tear = (hash(band + tick * 1.3 + seed * 5.0) - 0.5) * 0.08 * tearOn;
                // The channel split jitters every tick and is wider in a torn band; the three copies sum back to the picture when aligned.
                float split = shift * (0.004 + 0.010 * hash(tick * 0.71 + seed * 3.1)) * (1.0 + 2.5 * tearOn);
                float x = uv.x + tear + shift * 0.006;
                float3 a = look(float2(x - split, uv.y), shift);
                float3 b = look(float2(x, uv.y), shift);
                float3 c = look(float2(x + split, uv.y), shift);
                float3 col = (a * _ChannelLeft.rgb + b * _ChannelMiddle.rgb + c * _ChannelRight.rgb)
                    / max(_ChannelLeft.rgb + _ChannelMiddle.rgb + _ChannelRight.rgb, 0.001);
                // A torn band lights up in one channel colour.
                col = lerp(col, channel(hash(band * 3.3 + tick * 0.9 + seed * 7.0)) * (0.35 + luma(col)), tearOn * 0.45);
                // Short noise bars in the channel colours, more of them the stronger the glitch.
                float row = floor(uv.y * 180.0);
                float n = hash(row * 1.7 + floor(uv.x * 14.0 + hash(row * 0.37 + seed) * 3.0) * 9.3 + tick * 5.1 + seed * 2.0);
                col += channel(hash(n * 7.0 + row)) * 0.55 * step(1.0 - 0.035 * shift, n);
                // Fine colour grain.
                float cell = floor(uv.x * 480.0) + floor(uv.y * 270.0) * 57.0;
                col += (float3(hash(cell + tick * 0.13), hash(cell + tick * 0.13 + 0.31), hash(cell + tick * 0.13 + 0.67)) - 0.5) * 0.05 * shift;
                col = lerp(col, float3(0.0, 0.0, 0.0), black);
                return fixed4(saturate(col), alpha);
            }
            ENDCG
        }
    }
}
