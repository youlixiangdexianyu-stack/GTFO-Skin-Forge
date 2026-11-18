// 2D and 3D simplex noise, as the PRO Effects (Knife) shaders use it (their Amplify "Noise Generator" node).
// From webgl-noise by Ian McEwan, Stefan Gustavson and Ashima Arts, MIT licence: https://github.com/ashima/webgl-noise
//   Copyright (C) 2011 Ashima Arts. Copyright (C) 2011-2016 by Stefan Gustavson.
//   Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
//   "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
//   distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
//   following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
//   Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
#ifndef FORGE_NOISE_INCLUDED
#define FORGE_NOISE_INCLUDED

float2 ForgeMod289(float2 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float3 ForgeMod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 ForgeMod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float3 ForgePermute(float3 x) { return ForgeMod289((x * 34.0 + 1.0) * x); }
float4 ForgePermute(float4 x) { return ForgeMod289((x * 34.0 + 1.0) * x); }

float ForgeSnoise(float2 v)
{
    const float4 C = float4(0.211324865405187, 0.366025403784439, -0.577350269189626, 0.024390243902439);
    float2 i = floor(v + dot(v, C.yy));
    float2 x0 = v - i + dot(i, C.xx);
    float2 i1 = x0.x > x0.y ? float2(1.0, 0.0) : float2(0.0, 1.0);
    float4 x12 = x0.xyxy + C.xxzz;
    x12.xy -= i1;
    i = ForgeMod289(i);
    float3 p = ForgePermute(ForgePermute(i.y + float3(0.0, i1.y, 1.0)) + i.x + float3(0.0, i1.x, 1.0));
    float3 m = max(0.5 - float3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
    m = m * m;
    m = m * m;
    float3 x = 2.0 * frac(p * C.www) - 1.0;
    float3 h = abs(x) - 0.5;
    float3 ox = floor(x + 0.5);
    float3 a0 = x - ox;
    m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);
    float3 g;
    g.x = a0.x * x0.x + h.x * x0.y;
    g.yz = a0.yz * x12.xz + h.yz * x12.yw;
    return 130.0 * dot(m, g);
}

float ForgeSnoise(float3 v)
{
    const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
    float3 i = floor(v + dot(v, C.yyy));
    float3 x0 = v - i + dot(i, C.xxx);
    float3 g = step(x0.yzx, x0.xyz);
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);
    float3 x1 = x0 - i1 + C.xxx;
    float3 x2 = x0 - i2 + C.yyy;
    float3 x3 = x0 - 0.5;
    i = ForgeMod289(i);
    float4 p = ForgePermute(ForgePermute(ForgePermute(i.z + float4(0.0, i1.z, i2.z, 1.0)) + i.y + float4(0.0, i1.y, i2.y, 1.0)) + i.x + float4(0.0, i1.x, i2.x, 1.0));
    float4 j = p - 49.0 * floor(p / 49.0);
    float4 x_ = floor(j / 7.0);
    float4 y_ = floor(j - 7.0 * x_);
    float4 x = (x_ * 2.0 + 0.5) / 7.0 - 1.0;
    float4 y = (y_ * 2.0 + 0.5) / 7.0 - 1.0;
    float4 h = 1.0 - abs(x) - abs(y);
    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);
    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, 0.0);
    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
    float3 g0 = float3(a0.xy, h.x);
    float3 g1 = float3(a0.zw, h.y);
    float3 g2 = float3(a1.xy, h.z);
    float3 g3 = float3(a1.zw, h.w);
    float4 norm = 1.79284291400159 - 0.85373472095314 * float4(dot(g0, g0), dot(g1, g1), dot(g2, g2), dot(g3, g3));
    g0 *= norm.x;
    g1 *= norm.y;
    g2 *= norm.z;
    g3 *= norm.w;
    float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    m = m * m;
    return 42.0 * dot(m, float4(dot(x0, g0), dot(x1, g1), dot(x2, g2), dot(x3, g3)));
}

// Shader Graph's Simple Noise (three octaves of value noise, its sine hash) and Gradient Noise (its integer hash), the patterns its nodes draw,
// so a ported graph warps by the same field (Master MagicFX UberParticles).
float ForgeHashSine(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

float ForgeHashInt(float2 p)
{
    uint2 v = (uint2)(int2)round(p);
    v.y ^= 1103515245u;
    v.x += v.y;
    v.x *= v.y;
    v.x ^= v.x >> 5u;
    v.x *= 0x27d4eb2du;
    return (v.x >> 8) * (1.0 / 16777215.0);
}

float ForgeValueNoise(float2 uv)
{
    float2 i = floor(uv);
    float2 f = frac(uv);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(ForgeHashSine(i), ForgeHashSine(i + float2(1, 0)), f.x), lerp(ForgeHashSine(i + float2(0, 1)), ForgeHashSine(i + float2(1, 1)), f.x), f.y);
}

float ForgeSimpleNoise(float2 uv, float scale)
{
    return ForgeValueNoise(uv * scale) * 0.125 + ForgeValueNoise(uv * (scale * 0.5)) * 0.25 + ForgeValueNoise(uv * (scale * 0.25)) * 0.5;
}

float2 ForgeGradientDir(float2 p)
{
    float x = ForgeHashInt(p);
    return normalize(float2(x - floor(x + 0.5), abs(x) - 0.5));
}

float ForgeGradientNoise(float2 uv, float scale)
{
    float2 p = uv * scale;
    float2 ip = floor(p);
    float2 fp = frac(p);
    float d00 = dot(ForgeGradientDir(ip), fp);
    float d01 = dot(ForgeGradientDir(ip + float2(0, 1)), fp - float2(0, 1));
    float d10 = dot(ForgeGradientDir(ip + float2(1, 0)), fp - float2(1, 0));
    float d11 = dot(ForgeGradientDir(ip + float2(1, 1)), fp - float2(1, 1));
    fp = fp * fp * fp * (fp * (fp * 6 - 15) + 10);
    return lerp(lerp(d00, d01, fp.y), lerp(d10, d11, fp.y), fp.x) + 0.5;
}

#endif
