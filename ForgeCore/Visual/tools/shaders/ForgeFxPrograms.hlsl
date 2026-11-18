// The parametric drawing programs of ForgeVisual (Data/fx-programs.json is the catalogue: names, order, parameters, colour slots).
// One source for HLSL (the Unity shader ForgeVisualProcedural.shader defines FX_HLSL before including it) and GLSL (an editor or
// web preview includes it as text after its #version line): pure functions, no textures, no language-specific syntax beyond the
// macros below. Rules that keep both compiling: float literals always carry a point (1.0, not 1), no one-argument vector
// constructors, no scalar assigned to a vector, no `static`, no `mul`, no `%` on floats.
//
// A program is `float4 fx_<name>(FxIn i)` where <name> is its catalogue name; its parameters are i.p0.x .. i.p1.w in catalogue
// order, its colours i.c0 .. i.c3 (linear, alpha = coverage). It returns colour and coverage. The consumer generates
// `fx_dispatch(int program, FxIn i)` from the catalogue order (the Unity build: ForgeVisualShaderBuild.cs).
#ifndef FX_HLSL
#define float2 vec2
#define float3 vec3
#define float4 vec4
#define frac fract
#define lerp mix
#define atan2 atan
#define saturate(x) clamp((x), 0.0, 1.0)
#endif

#define FX_TAU 6.28318530718

struct FxIn
{
    float2 uv;    // pattern coordinates: the mesh's UV after the layer's scale, rotation and spin, centred on 0.5
    float3 pos;   // object-space position
    float3 nrm;   // object-space normal (unit)
    float3 view;  // object-space direction to the eye (unit)
    float time;   // seconds
    float4 p0;
    float4 p1;
    float4 c0;
    float4 c1;
    float4 c2;
    float4 c3;
};

// The layer transform: uv about its centre, scaled by xf.x, turned by xf.y radians plus xf.z radians a second.
float2 fx_transform(float2 uv, float4 xf, float time)
{
    float a = xf.y + xf.z * time;
    float s = sin(a);
    float c = cos(a);
    float2 d = uv - float2(0.5, 0.5);
    float2 r = float2(c * d.x - s * d.y, s * d.x + c * d.y);
    return r * max(xf.x, 0.0001) + float2(0.5, 0.5);
}

float fx_hash3(float3 p)
{
    return frac(sin(dot(p, float3(127.1, 311.7, 74.7))) * 43758.5453);
}

// Value noise in 3D, 0..1, smooth.
float fx_noise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (float3(3.0, 3.0, 3.0) - 2.0 * f);
    float n000 = fx_hash3(i);
    float n100 = fx_hash3(i + float3(1.0, 0.0, 0.0));
    float n010 = fx_hash3(i + float3(0.0, 1.0, 0.0));
    float n110 = fx_hash3(i + float3(1.0, 1.0, 0.0));
    float n001 = fx_hash3(i + float3(0.0, 0.0, 1.0));
    float n101 = fx_hash3(i + float3(1.0, 0.0, 1.0));
    float n011 = fx_hash3(i + float3(0.0, 1.0, 1.0));
    float n111 = fx_hash3(i + float3(1.0, 1.0, 1.0));
    float x00 = lerp(n000, n100, u.x);
    float x10 = lerp(n010, n110, u.x);
    float x01 = lerp(n001, n101, u.x);
    float x11 = lerp(n011, n111, u.x);
    return lerp(lerp(x00, x10, u.y), lerp(x01, x11, u.y), u.z);
}

// Coverage blend of two colour slots: over the base (b), the top (t) by amount k.
float4 fx_over(float4 b, float4 t, float k)
{
    float a = saturate(t.a * k);
    return float4(lerp(b.rgb * b.a, t.rgb, a) / max(lerp(b.a, 1.0, a), 0.0001), lerp(b.a, 1.0, a));
}

// flowLines: density, speed, width, angle (deg), glow.
float4 fx_flowLines(FxIn i)
{
    float a = radians(i.p0.w);
    float2 dir = float2(cos(a), sin(a));
    float t = dot(i.uv, dir) * i.p0.x - i.time * i.p0.y;
    float d = abs(frac(t) - 0.5) * 2.0;
    float w = saturate(i.p0.z) * 2.0;
    float core = 1.0 - smoothstep(w * 0.5, w * 0.5 + 0.02, d);
    float glow = (1.0 - smoothstep(0.0, w * 0.5 + 0.35, d)) * i.p1.x;
    return fx_over(i.c1, i.c0, saturate(core + glow * 0.6));
}

// scanlines: count, speed, thickness, sweep width, sweep speed.
float4 fx_scanlines(FxIn i)
{
    float y = i.uv.y * i.p0.x + i.time * i.p0.y;
    float lit = step(frac(y), saturate(i.p0.z));
    float s = frac(i.uv.y - i.time * i.p1.x);
    float sweepWidth = max(i.p0.w, 0.0001);
    float sweep = i.p0.w > 0.0 ? pow(saturate(1.0 - abs(s - 0.5) * 2.0 / sweepWidth), 2.0) : 0.0;
    float4 lines = float4(i.c0.rgb, i.c0.a * lit * 0.75);
    return fx_over(lines, i.c1, sweep);
}

// pulse: rate (Hz), sharpness, floor, rings (0: the whole surface pulses; more: rings run out from the centre).
float4 fx_pulse(FxIn i)
{
    float r = length(i.uv - float2(0.5, 0.5)) * 2.0;
    float phase = i.time * i.p0.x - (i.p0.w > 0.0 ? r * i.p0.w * 0.5 : 0.0);
    float wave = pow(0.5 + 0.5 * cos(phase * FX_TAU), i.p0.y);
    float k = lerp(saturate(i.p0.z), 1.0, wave);
    return lerp(i.c0, i.c1, k);
}

// noise: grain, speed, octaves, contrast, object space (0: uv, 1: object position).
float4 fx_noise(FxIn i)
{
    float3 at = lerp(float3(i.uv.x, i.uv.y, 0.0), i.pos, step(0.5, i.p1.x)) * i.p0.x + float3(0.0, 0.0, i.time * i.p0.y);
    float sum = 0.0;
    float amp = 0.5;
    float total = 0.0;
    for (int o = 0; o < 5; o++)
    {
        if (float(o) < i.p0.z)
        {
            sum += fx_noise3(at) * amp;
            total += amp;
        }
        at = at * 2.03 + float3(17.0, 9.0, 3.0);
        amp *= 0.5;
    }
    float n = saturate(sum / max(total, 0.0001));
    n = saturate((n - 0.5) * i.p0.w + 0.5);
    return lerp(i.c0, i.c1, n);
}

// fresnel: power, bias, flicker, flicker rate.
float4 fx_fresnel(FxIn i)
{
    float facing = saturate(dot(i.nrm, i.view));
    float rim = saturate(i.p0.y + (1.0 - i.p0.y) * pow(1.0 - facing, i.p0.x));
    float flicker = 1.0 - i.p0.z * (0.5 + 0.5 * sin(i.time * i.p0.w * FX_TAU) * sin(i.time * i.p0.w * 2.3));
    return fx_over(i.c1, i.c0, rim * flicker);
}

// polar: petals, rings, twist (turns), sharpness, radius, ring drift.
float4 fx_polar(FxIn i)
{
    float2 d = i.uv - float2(0.5, 0.5);
    float r = length(d) * 2.0;
    float a = atan2(d.y, d.x);
    float petal = pow(0.5 + 0.5 * cos(a * floor(i.p0.x + 0.5) + r * i.p0.z * FX_TAU), i.p0.w);
    float ring = i.p0.y > 0.0 ? pow(0.5 + 0.5 * cos((r * i.p0.y - i.time * i.p1.y) * FX_TAU), i.p0.w * 2.0) : 0.0;
    float inside = 1.0 - smoothstep(i.p1.x * 2.0 - 0.04, i.p1.x * 2.0, r);
    float4 c = fx_over(i.c2, i.c1, ring * inside);
    return fx_over(c, i.c0, petal * inside * (1.0 - ring * 0.5));
}
