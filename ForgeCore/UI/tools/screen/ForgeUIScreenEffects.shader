// ForgeUI's screen effect stack: one full-screen quad on the screen layer (drawn after every camera) that lays every merged screen
// effect over the picture in one pass. Layers, back to front (the order the separate overlay quads had): the hit-edge bands, the
// vignette, the overheat glow, the scanline interference, the boot sweep, the low-health pulse and the colour flash. Colours are set
// with Material.SetColor (decoded like Sprites/Default's _Color), alphas are 0..1.
// Two subshaders: LOD 200 grabs the screen and blurs and colour-splits it under the layers (only while blur or colour split is on);
// LOD 100 only blends the layers over the picture. ForgeUI picks one with Shader.maximumLOD, so the screen is grabbed only when needed.
Shader "Hidden/ForgeUI/ScreenEffects"
{
    Properties
    {
        _BandTex ("Hit edge band", 2D) = "black" {}
        _InterferenceTex ("Interference", 2D) = "black" {}
    }
    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _BandTex;
    sampler2D _InterferenceTex;
    float4 _Flash, _Vignette, _LowHealth, _Heat, _Interference, _Sweep, _BandColour;
    // x: aspect (width per height), y: blur radius and z: colour split, both in screen heights, w: interference shift (screen heights).
    float4 _Layout;
    // x: the sweep line's height in screen units from the middle (-0.5 bottom, 0.5 top).
    float4 _SweepAt;
    // x, y: a hit band's width and depth in screen heights.
    float4 _BandSize;
    // Each hit band: x, y its centre in screen heights from the middle, z its rotation in radians, w its alpha (0: off).
    float4 _Band0, _Band1, _Band2, _Band3, _Band4, _Band5;

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
        float4 grab : TEXCOORD1;
    };

    v2f vert(appdata_base v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord.xy;
        o.grab = ComputeGrabScreenPos(o.pos);
        return o;
    }

    float smooth01(float t)
    {
        t = saturate(t);
        return t * t * (3.0 - 2.0 * t);
    }

    // ScreenPatterns.Vignette: clear inside 55 % of the way to the edge, full at the corners.
    float vignette(float2 uv)
    {
        float2 d = (uv - 0.5) * 2.0;
        return smooth01((length(d) - 0.55) / 0.75);
    }

    // The low-health pulse reaches further in than the vignette: clear inside 35 %, full at the corners.
    float lowHealth(float2 uv)
    {
        float2 d = (uv - 0.5) * 2.0;
        return smooth01((length(d) - 0.35) / 0.9);
    }

    // ScreenPatterns.HeatGlow.
    float heatGlow(float2 uv)
    {
        float band = 1.0 - saturate(uv.y / (260.0 / 1080.0));
        float wave = 0.85 + 0.15 * sin(uv.x * 40.0 + uv.y * 9.0);
        return band * band * wave;
    }

    // ScreenPatterns.SweepTrail.
    float sweepTrail(float t)
    {
        t = saturate(t);
        return t < 0.02 ? 1.0 : 0.3 * (1.0 - t) * (1.0 - t);
    }

    void over(inout float4 acc, float3 colour, float alpha)
    {
        acc.rgb = colour * alpha + acc.rgb * (1.0 - alpha);
        acc.a = alpha + acc.a * (1.0 - alpha);
    }

    // One hit band: the screen point p in the band's own square, its picture sampled there.
    float band(float4 b, float2 p)
    {
        float2 d = p - b.xy;
        float s = sin(b.z), c = cos(b.z);
        float2 t = float2(c * d.x + s * d.y, -s * d.x + c * d.y) / _BandSize.xy + 0.5;
        float inside = step(0.0, t.x) * step(t.x, 1.0) * step(0.0, t.y) * step(t.y, 1.0);
        return tex2Dlod(_BandTex, float4(saturate(t), 0.0, 0.0)).a * inside * b.w;
    }

    float4 layers(float2 uv)
    {
        float2 p = float2((uv.x - 0.5) * _Layout.x, uv.y - 0.5);
        float4 acc = float4(0.0, 0.0, 0.0, 0.0);
        over(acc, _BandColour.rgb, band(_Band0, p));
        over(acc, _BandColour.rgb, band(_Band1, p));
        over(acc, _BandColour.rgb, band(_Band2, p));
        over(acc, _BandColour.rgb, band(_Band3, p));
        over(acc, _BandColour.rgb, band(_Band4, p));
        over(acc, _BandColour.rgb, band(_Band5, p));
        over(acc, _Vignette.rgb, _Vignette.a * vignette(uv));
        over(acc, _Heat.rgb, _Heat.a * heatGlow(uv));
        float iv = uv.y - _Layout.w;
        float interference = tex2Dlod(_InterferenceTex, float4(uv.x, saturate(iv), 0.0, 0.0)).a * step(0.0, iv) * step(iv, 1.0);
        over(acc, _Interference.rgb, _Interference.a * interference);
        float sweep = (p.y - _SweepAt.x) / 0.25;
        over(acc, _Sweep.rgb, _Sweep.a * sweepTrail(sweep) * step(0.0, sweep) * step(sweep, 1.0));
        over(acc, _LowHealth.rgb, _LowHealth.a * lowHealth(uv));
        over(acc, _Flash.rgb, _Flash.a);
        return acc;
    }
    ENDCG

    SubShader
    {
        LOD 200
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        GrabPass { "_ForgeScreenGrab" }
        Pass
        {
            Name "Grabbed"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            // Unity 2019.4.21's shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols

            sampler2D _ForgeScreenGrab;

            // A soft disc: the middle and twelve taps on a ring of the blur radius.
            float3 blurred(float2 at, float2 reach)
            {
                float3 sum = tex2D(_ForgeScreenGrab, at).rgb;
                [unroll]
                for (int k = 0; k < 12; k++)
                {
                    float angle = k * 0.5235988;
                    sum += tex2D(_ForgeScreenGrab, at + float2(cos(angle), sin(angle)) * reach).rgb;
                }
                return sum / 13.0;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 at = i.grab.xy / i.grab.w;
                float2 reach = float2(_Layout.y / _Layout.x, _Layout.y);
                // The colour split grows from nothing in the middle to its full width at the corners.
                float2 split = (i.uv - 0.5) * 1.41421356 * float2(_Layout.z / _Layout.x, _Layout.z);
                float3 scene;
                scene.r = blurred(at + split, reach).r;
                scene.g = blurred(at, reach).g;
                scene.b = blurred(at - split, reach).b;
                float4 acc = layers(i.uv);
                return float4(acc.rgb + scene * (1.0 - acc.a), 1.0);
            }
            ENDCG
        }
    }
    SubShader
    {
        LOD 100
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Layers"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma enable_d3d11_debug_symbols

            float4 frag(v2f i) : SV_Target
            {
                return layers(i.uv);
            }
            ENDCG
        }
    }
    Fallback Off
}
