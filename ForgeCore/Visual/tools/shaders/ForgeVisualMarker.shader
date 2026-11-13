// ForgeVisual's world marks (drawn in the world, not interface): beam lines, a camera-facing flare and ground marks, all procedural
// and additive so nothing darkens the scene and no texture is needed. One material is shared; each renderer sets its colours and
// state through a MaterialPropertyBlock, so animating costs no allocation. Built with Unity 2019.4.21f1 by
// tools/shaders/build_shaders.ps1 into Native/ShaderBundle.Generated.cs.
//
//   _Shape.y  mode   0 beam line (LineRenderer: uv.x along, uv.y across)   1 ground mark (uv = local XZ / radius)   2 flare quad
//   _Shape.x  beam length (m) for mode 0, mark radius (m) for mode 1
//   _Shape.z  mode 0: edge sharpness; mode 1: remaining fraction of a countdown (1 at the start, 0 at the end)
//   _Shape.w  mode 0: 1 = even along its length (0 = a column, brightest at its start); mode 1: blink of a tick (1 at the tick, fading)
//   _State    x seconds since the mark appeared, y ping phase (0..1), z urgency (0..1), w opacity
//   _Style    x ground style (0 countdown, 1 hatched, 2 flames, 3 dial, 4 reticle), y drawn-in sweep (0..1),
//             z 0 a disc, above 0 a strip: half its length over half its width (uv = local metres across, along / half width), w 1 = chevrons walking along a strip
//   mode 1 vertex colour: alpha = brightness (dimmer past a step or over a drop), green = this side of every wall
//   mode 1 uv2: x height up a step's drape, y that drape's height (the drape fades in its middle)
// A second pass draws a ground mark's rim as a faint dashed line only where something hides it (behind a wall or crate), so the ring
// always reads whole; it is drawn only while the local eye is near the mark (_Reveal, set from code).
Shader "ForgeVisual/Marker"
{
    Properties
    {
        _Color ("Accent", Color) = (1, 1, 1, 1)
        _Hot ("Core", Color) = (1, 1, 1, 1)
        _State ("Time, ping, urgency, opacity", Vector) = (0, 0, 0, 1)
        _Shape ("Size, mode, sharpness or remaining, even or blink", Vector) = (1, 0, 2, 0)
        _Style ("Ground style, sweep", Vector) = (0, 1, 0, 0)
        _Reveal ("Hidden rim shown, set per frame from the eye's distance", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Offset -1, -1
        Blend One One
        CGINCLUDE
            #include "UnityCG.cginc"

            fixed4 _Color, _Hot;
            float4 _State, _Shape, _Style;
            float _Reveal;

            struct appdata { float4 vertex : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; float2 drape : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; float2 drape : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                o.drape = v.drape;
                return o;
            }

            // Only the ground mark has a hidden rim; the beam and flare are dropped before rasterising.
            v2f vertHidden(appdata v)
            {
                v2f o = vert(v);
                if (abs(_Shape.y - 1.0) > 0.5) o.pos = float4(2.0, 2.0, 2.0, 1.0);
                return o;
            }

            static const float TAU = 6.2831853;

            // 1 on a band of half width hw around c, anti-aliased over aa.
            float Band(float x, float c, float hw, float aa) { return 1.0 - smoothstep(hw, hw + aa, abs(x - c)); }
            float Glow(float x, float c, float s) { float d = (x - c) / s; return exp(-d * d); }
            // 1 inside [a, b] of a periodic coordinate in turns, soft over aa.
            float Arc(float u, float a, float b, float aa) { return smoothstep(a - aa, a + aa, u) * (1.0 - smoothstep(b - aa, b + aa, u)); }
            // Drawn in: the mark sweeps on clockwise from the top.
            float Sweep(float u) { return _Style.y >= 1.0 ? 1.0 : 1.0 - smoothstep(_Style.y - 0.02, _Style.y, u); }

            // Where a ground point lies against the mark's rim: r (1 on the rim, in half widths: a disc's radius or a strip's half width), u (turns
            // clockwise from north) and arc, metres along the rim (a strip's: along the side the point is nearest, so stripes keep their spacing).
            void Outline(float2 p, float R, out float r, out float u, out float arc)
            {
                u = frac(atan2(p.x, p.y) / TAU + 1.0);
                if (_Style.z > 0.0)
                {
                    float2 q = abs(p) - float2(1.0, _Style.z);
                    r = 1.0 + length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
                    arc = (q.x > q.y ? p.y * sign(p.x) : -p.x * sign(p.y)) * R;
                }
                else
                {
                    r = length(p);
                    arc = u * TAU * R;
                }
            }

            float3 Beam(v2f i)
            {
                float d = abs(i.uv.y * 2.0 - 1.0);
                float profile = pow(saturate(1.0 - d), _Shape.z);
                float h = i.uv.x * _Shape.x;
                float t = _State.x;
                // A column is brightest at its start, settles above head height and fades out at its end; an even beam only
                // softens its two ends.
                float column = (0.24 + 0.76 * exp(-h / 9.0)) * saturate((1.0 - i.uv.x) * 5.0);
                float even = saturate(i.uv.x * 12.0) * saturate((1.0 - i.uv.x) * 12.0);
                float fall = lerp(column, even, saturate(_Shape.w));
                // Faint bands of energy climbing the beam.
                float climb = 0.86 + 0.14 * sin(h * 0.8 - t * 6.0) * (0.6 + 0.4 * sin(h * 0.21 - t * 1.9));
                float3 c = lerp(_Color.rgb, _Hot.rgb, profile * profile);
                return c * profile * fall * climb * _State.w;
            }

            float3 Flare(v2f i)
            {
                float r = length(i.uv * 2.0 - 1.0);
                float core = Glow(r, 0.0, 0.16);
                float halo = Glow(r, 0.0, 0.42) * 0.35;
                float3 c = _Color.rgb * halo + _Hot.rgb * core;
                return c * saturate(1.0 - r) * _State.w;
            }

            // 1 on floor, fading toward nothing in the middle of a tall face (a low step stays lit); the rim line stays half lit down
            // it, so it reads unbroken.
            float Drape(v2f i, float rim) { return lerp(1.0 - smoothstep(0.12, 0.35, min(i.drape.x, i.drape.y - i.drape.x)), 1.0, 0.5 * rim); }

            // A countdown, drawn only in thin lines so the game's bloom never fills it into a disc: a crisp rim whose lit arc is the time
            // left (emptying clockwise from north), 24 scale ticks (dimmer on the spent arc), four brackets turning outside, a
            // counter-rotating dashed inner ring, a small centre dot and a thin ring pinging out past the rim. No fill and no soft glows.
            float3 Countdown(v2f i, float2 p, float r, float m, float px, float pxa, float u)
            {
                float t = _State.x, pulse = _State.y, urgency = _State.z;
                float remaining = _Shape.z, blink = _Shape.w;
                float3 accent = lerp(_Color.rgb, _Hot.rgb, 0.25 * urgency);
                float lit = 1.0 - smoothstep(remaining - pxa, remaining + pxa, u);
                float rimLine = Band(r, 1.0, max(0.010 * m, 0.55 * px), px);
                float rimHalo = Glow(r, 1.0, 0.035 * m);
                float rim = rimLine * lerp(0.22, 1.0, lit) + rimHalo * 0.16 * lit;
                rim *= 1.0 + 0.8 * blink;
                float tickAng = Band(frac(u * 24.0), 0.5, max(0.006 * m / max(r, 0.02) / TAU * 24.0, 0.5 * pxa * 24.0), pxa * 24.0);
                float isLong = Band(frac(u * 4.0), 0.5, 1.0 / 48.0, 1.0 / 96.0);
                float tickLen = lerp(0.06, 0.14, isLong) * m;
                float ticks = tickAng * Band(r, 1.0 - 0.035 * m - tickLen * 0.5, tickLen * 0.5, px) * (0.26 + 0.24 * isLong) * lerp(0.45, 1.0, lit);
                float spin = t * (0.035 + 0.12 * urgency);
                float bu = frac(u + spin);
                float bracketR = 1.0 + lerp(0.14, 0.07, urgency) * m;
                float bracket = Band(r, bracketR, max(0.008 * m, 0.5 * px), px) * Arc(frac(bu * 4.0), 0.44, 0.56, pxa * 4.0) * (0.45 + 0.35 * urgency);
                float innerR = lerp(0.42, 0.28, urgency);
                float du = frac(u - t * 0.06 * (1.0 + 2.0 * urgency));
                float inner = Band(r, innerR, max(0.006 * m, 0.5 * px), px) * Arc(frac(du * 12.0), 0.25, 0.75, pxa * 12.0) * 0.30;
                float dot = Band(r, 0.0, 0.025 * m, px) * 0.8;
                float pr = lerp(0.15, 1.18, pulse);
                float ping = Band(r, pr, max(0.006 * m, 0.5 * px), px) * pow(1.0 - pulse, 1.6) * (0.28 + 0.30 * urgency);
                float edge = 1.0 - smoothstep(1.22, 1.28, r);
                return (accent * (rim * 0.8 + ticks + bracket + inner + ping) + _Hot.rgb * (rimLine * lit * 0.25 + dot)) * edge;
            }

            // The area styles and the reticle, drawn in by the sweep.
            float3 Area(v2f i, float2 p, float r, float R, float m, float px, float pxa, float u, float arc, float arcPx, float style)
            {
                float t = _State.x;
                float inside = 1.0 - smoothstep(1.0 - px, 1.0 + px, r);
                float3 accent = _Color.rgb, hot = _Hot.rgb;
                float3 c = 0;
                if (style < 1.5)
                {
                    // Hatched: a band of slanted stripes creeping round the rim between two lines, and a faint drifting fill.
                    float bandW = 0.24 * m;
                    float inBand = smoothstep(1.0 - bandW - px, 1.0 - bandW + px, r) * inside;
                    float along = arc / 0.42 + (r * R) / 0.42 - t * 0.35;
                    float sa = max((arcPx + R * px) / 0.42, 0.04);
                    float stripes = smoothstep(0.5 - sa, 0.5 + sa, abs(frac(along) * 2.0 - 1.0));
                    float lines = Band(r, 1.0, max(0.012 * m, 0.55 * px), px) * 0.95 + Band(r, 1.0 - bandW, max(0.007 * m, 0.5 * px), px) * 0.45;
                    float halo = Glow(r, 1.0, 0.12 * m) * 0.22;
                    float2 q = p * R;
                    float drift = 0.5 + 0.5 * sin(q.x * 0.9 + t * 0.45) * sin(q.y * 1.1 - t * 0.33 + sin(q.x * 0.4));
                    float fill = (0.025 + 0.045 * drift) * inside * smoothstep(0.0, 0.85, r);
                    c = accent * (inBand * stripes * 0.30 + lines + halo + fill) + hot * lines * 0.25;
                }
                else if (style < 2.5)
                {
                    // Flames: a clean rim whose glow rises outward in flickering tongues, a heat wash brightening toward the rim and
                    // a dim dashed inner line.
                    float a = u * TAU;
                    float tongues = saturate(0.5 + 0.5 * sin(a * 9.0 + t * 2.3 + 1.7 * sin(a * 4.0 - t * 1.3)));
                    tongues *= 0.6 + 0.4 * sin(a * 23.0 - t * 5.1);
                    float flicker = 0.88 + 0.12 * sin(t * 17.0 + a * 2.0) * sin(t * 6.3);
                    float rim = Band(r, 1.0, max(0.012 * m, 0.55 * px), px);
                    float outside = saturate((r - 1.0) / (0.35 * m));
                    float flames = (1.0 - smoothstep(0.0, 1.0, outside / max(tongues, 0.05))) * step(1.0, r) * 0.45;
                    float halo = Glow(r, 1.0, 0.10 * m) * 0.25;
                    float innerLine = Band(r, 1.0 - 0.2 * m, max(0.006 * m, 0.5 * px), px) * Arc(frac(u * 18.0 - t * 0.12), 0.15, 0.85, pxa * 18.0) * 0.30;
                    float heat = 0.10 * pow(saturate(r), 2.4) * inside;
                    float3 deep = accent * float3(1.0, 0.45, 0.35);
                    c = (accent * (rim * 0.9 + halo + innerLine) + deep * (heat + flames)) * flicker + hot * rim * 0.35 * flicker;
                }
                else if (style < 3.5)
                {
                    // Dial: a crisp rim, a dial of 60 ticks that turns very slowly, and echo rings that sink inward and fade.
                    float rim = Band(r, 1.0, max(0.010 * m, 0.5 * px), px);
                    float halo = Glow(r, 1.0, 0.08 * m) * 0.25;
                    float du = frac(u + t * 0.008);
                    float tickAng = Band(frac(du * 60.0), 0.5, max(0.005 * m / max(r, 0.02) / TAU * 60.0, 0.5 * pxa * 60.0), pxa * 60.0);
                    float isLong = Band(frac(du * 12.0), 0.5, 1.0 / 120.0, 1.0 / 240.0);
                    float tickLen = lerp(0.10, 0.24, isLong) * m;
                    float ticks = tickAng * Band(r, 1.0 - 0.04 * m - tickLen * 0.5, tickLen * 0.5, px) * (0.30 + 0.25 * isLong);
                    float echoes = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        float ph = frac(t / 3.6 + k / 3.0);
                        float er = lerp(0.96, 0.12, ph);
                        echoes += Band(r, er, max(0.006 * m, 0.5 * px), px) * sin(ph * 3.14159) * 0.32 + Glow(r, er, 0.06 * m) * sin(ph * 3.14159) * 0.08;
                    }
                    float fill = 0.035 * inside;
                    c = accent * (rim * 0.9 + halo + ticks + echoes + fill) + hot * rim * 0.3;
                }
                else
                {
                    // Reticle: three arcs spinning round a burning point, four inward ticks.
                    float spin = frac(u - t * 0.35);
                    float arcs = Band(r, 1.0, max(0.02 * m, 0.6 * px), px) * Arc(frac(spin * 3.0), 0.12, 0.88, pxa * 3.0);
                    float outer = Band(r, 1.14, max(0.006 * m, 0.5 * px), px) * 0.3;
                    float2 q = abs(p);
                    float cross = Band(min(q.x, q.y) * R, 0.0, 0.016, px * R) * smoothstep(0.30, 0.32, r) * (1.0 - smoothstep(0.70, 0.72, r));
                    float breathe = 0.85 + 0.15 * sin(t * 9.0);
                    float core = Glow(r, 0.0, 0.10 * m) * 0.9 * breathe + Glow(r, 0.0, 0.45) * 0.18;
                    float halo = Glow(r, 1.0, 0.09 * m) * 0.3;
                    c = accent * (arcs * 0.95 + outer + cross + halo) + hot * (core + arcs * 0.35 + cross * 0.3);
                }
                if (_Style.w > 0.5 && _Style.z > 0.0)
                {
                    // Chevrons 2 m apart walking along the strip at 1.6 m/s, pointing the way the strike goes, clear of the rim band.
                    float2 q = p * R;
                    float chevron = frac((q.y - abs(q.x) * 0.8 - t * 1.6) / 2.0);
                    float cw = max(fwidth(q.y - abs(q.x) * 0.8) / 2.0, 1e-4);
                    c += accent * Band(chevron, 0.5, 0.06, cw) * (1.0 - smoothstep(0.55, 0.7, abs(p.x))) * 0.45;
                }
                float edge = 1.0 - smoothstep(1.18, 1.25, r);
                return c * Sweep(u) * edge;
            }

            float3 Ground(v2f i)
            {
                float2 p = i.uv;
                float R = max(_Shape.x, 0.05);
                float r, u, arc;
                Outline(p, R, r, u, arc);
                float m = 1.0 / R;                       // one metre in r units
                float px = max(fwidth(r), 1e-4);          // one pixel in r units
                float pxa = px / max(r, 0.02) / TAU;      // one pixel in turns at this radius
                float arcPx = _Style.z > 0.0 ? fwidth(arc) : TAU * R * pxa; // one pixel along the rim, metres
                float style = _Style.x;
                float3 c = style < 0.5 ? Countdown(i, p, r, m, px, pxa, u) : Area(i, p, r, R, m, px, pxa, u, arc, arcPx, style);
                return c * i.color.a * i.color.g * Drape(i, Band(r, 1.0, 0.025 * m, px)) * _State.w;
            }

            // The rim where something hides it: a thin dashed line, dim (a countdown's spent arc dimmer still).
            float3 Hidden(v2f i)
            {
                float2 p = i.uv;
                float R = max(_Shape.x, 0.05);
                float r, u, arc;
                Outline(p, R, r, u, arc);
                float m = 1.0 / R;
                float px = max(fwidth(r), 1e-4);
                float pxa = px / max(r, 0.02) / TAU;
                if (_Style.x < 0.5)
                {
                    float dashes = max(12.0, floor(TAU * R / 0.2));
                    float lit = 1.0 - smoothstep(_Shape.z - pxa, _Shape.z + pxa, u);
                    float dash = Band(r, 1.0, max(0.009 * m, 0.7 * px), px) * Arc(frac(u * dashes), 0.0, 0.55, pxa * dashes);
                    float3 accent = lerp(_Color.rgb, _Hot.rgb, 0.35 * _State.z);
                    return accent * dash * lerp(0.15, 0.38, lit) * (1.0 + 0.9 * _Shape.w) * _Reveal * _State.w;
                }
                // Dashes about 0.45 m apart along the rim (a whole number round a disc).
                float areaDashes = max(12.0, floor(TAU * R / 0.45));
                float dashAt = _Style.z > 0.0 ? arc / 0.45 : u * areaDashes;
                float dashPx = _Style.z > 0.0 ? max(fwidth(arc), 1e-4) / 0.45 : pxa * areaDashes;
                float areaDash = Band(r, 1.0, max(0.012 * m, 0.8 * px), px) * Arc(frac(dashAt), 0.0, 0.55, dashPx);
                return _Color.rgb * areaDash * 0.6 * Sweep(u) * _Reveal * _State.w;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float mode = _Shape.y;
                float3 c = mode < 0.5 ? Beam(i) : mode < 1.5 ? Ground(i) : Flare(i);
                return fixed4(c, 0.0);
            }

            fixed4 fragHidden(v2f i) : SV_Target { return fixed4(Hidden(i), 0.0); }
        ENDCG
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            // Unity 2019.4.21 shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; compiling unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            ENDCG
        }
        Pass
        {
            ZTest Greater
            CGPROGRAM
            #pragma vertex vertHidden
            #pragma fragment fragHidden
            #pragma target 3.0
            #pragma enable_d3d11_debug_symbols
            ENDCG
        }
    }
}
