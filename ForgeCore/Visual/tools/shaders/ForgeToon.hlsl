// ForgeVisual's one toon shader: everything both lighting entries share (the author's parameters, the vertex path, the toon ramp, rim
// and highlight, the outline hull, the alpha cut-out). ForgeVisualToonForward.shader lights it itself from the game's clustered lights;
// ForgeVisualToonDeferred writes the game's G-buffer and ForgeVisualToonOverlay steps the game's own lighting afterwards. Only the entry
// differs. ForgeVisualLitCutout (the gear shader with an alpha test) shares the vertex path, the cut-out and the light reading.
//
// The game's lighting data, read from its own compiled shaders (Mods/ForgeCore/evidence/toon-render-pipeline.md): globals set by the game's
// ClusteredRendering and PreLitVolume, declared here under the same names so Unity binds them. Light colours and the fog are in the game's
// linear HDR units before its exposure, so everything below adds up in those units and the game's exposure treats it like its own surfaces.
#ifndef FORGE_TOON_INCLUDED
#define FORGE_TOON_INCLUDED

#include "UnityCG.cginc"

// ---- the author's parameters (one list for every lighting entry; the overlay reads the same names) ----
sampler2D _MainTex; float4 _MainTex_ST;
float4 _Color;
float _Cutoff;
sampler2D _BumpMap;
float _UseNormalMap;
float4 _ShadeColor;
sampler2D _ShadeRamp;
float _UseShadeRamp;
float _ShadeThreshold, _ShadeSoftness;
float4 _OutlineColor;
float _OutlineWidth;
float4 _RimColor;
float _RimPower, _RimStrength;
float4 _HighlightColor;
float _HighlightSize, _HighlightSoftness;
sampler2D _EmissiveMap;
float4 _EmissiveColor;
float _EnableEmissive;

// ---- the game's globals ----
float4x4 _FPS_VP;
float _FPSWeight;
float3 _ClusterSize;
float _ClusterFarZ;
float _NearPeak;
float VSM_ShadowResInv;
float4 _ShadowAtlas_TexelSize;
float _VolumesEnabled;
float3 _LitVolumeSizeRcp;
float _FogConversionFrom;
float4 _FogColor;
StructuredBuffer<uint4> _LightCluster;
float4 _PointPosInvRangeSq[32];
float4 _PointColorPhysical[32];
float4 _EffectPosInvRangeSq[32];
float4 _EffectColorPhysical[32];
float4 _SpotPosInvRangeSq[32];
float4 _SpotColor[32];
float4 _SpotParams[32];
float4x4 _SpotVP[32];
sampler3D _FogVolume;
sampler3D _IndirectVolume;
sampler2D _ShadowAtlas;

#define FORGE_INV_PI 0.3183099

struct ToonIn
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float2 uv : TEXCOORD0;
};

struct ToonV2F
{
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 world : TEXCOORD1;
    float3 normal : TEXCOORD2;
    float3 tangent : TEXCOORD3;
    float3 bitangent : TEXCOORD4;
    float4 grab : TEXCOORD5;
};

// The game's first-person items (gear materials with its FPS keyword) are drawn with the item camera's projection, blended by _FPSWeight,
// exactly as Cell/Player/CustomGearShader's vertex program does; lighting stays at the real world position.
float4 ToonClip(float3 world)
{
    float4 clip = mul(UNITY_MATRIX_VP, float4(world, 1.0));
#if defined(FPS_RENDERING_ALLOWED) || defined(ENABLE_FPS_RENDERING)
    if (_FPSWeight > 0.0) clip = lerp(clip, mul(_FPS_VP, float4(world, 1.0)), _FPSWeight);
#endif
    return clip;
}

ToonV2F ToonVert(ToonIn v)
{
    ToonV2F o;
    o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
    o.pos = ToonClip(o.world);
    o.uv = TRANSFORM_TEX(v.uv, _MainTex);
    o.normal = UnityObjectToWorldNormal(v.normal);
    o.tangent = UnityObjectToWorldDir(v.tangent.xyz);
    o.bitangent = cross(o.normal, o.tangent) * v.tangent.w * unity_WorldTransformParams.w;
    o.grab = ComputeGrabScreenPos(o.pos);
    return o;
}

float3 ToonNormal(ToonV2F i)
{
    float3 n = normalize(i.normal);
    if (_UseNormalMap > 0.5)
    {
        float3 t = UnpackNormal(tex2D(_BumpMap, i.uv));
        n = normalize(t.x * i.tangent + t.y * i.bitangent + t.z * n);
    }
    return n;
}

float4 ToonBase(float2 uv) { return tex2D(_MainTex, uv) * _Color; }

// The alpha cut-out (_ALPHATEST_ON): a pixel whose base-colour alpha is below _Cutoff is not drawn. Every pass that writes the surface or its
// depth calls it (the G-buffer, the forward colour, the overlay, the outline and the shadow), so eyelashes and hair cards cut the same way
// everywhere.
void ToonCut(float2 uv)
{
#ifdef _ALPHATEST_ON
    clip(ToonBase(uv).a - _Cutoff);
#endif
}

float3 ToonEmission(float2 uv) { return _EnableEmissive * 4.0 * _EmissiveColor.a * _EmissiveColor.rgb * tex2D(_EmissiveMap, uv).rgb; }

// The step from shade to light: the shade ramp (left dark, right lit) when given, else a band at _ShadeThreshold _ShadeSoftness wide.
float ToonStep(float t)
{
    float band = smoothstep(_ShadeThreshold - _ShadeSoftness, _ShadeThreshold + _ShadeSoftness, t);
    float ramp = tex2Dlod(_ShadeRamp, float4(saturate(t), 0.5, 0.0, 0.0)).r;
    return _UseShadeRamp > 0.5 ? ramp : band;
}

float ToonHighlight(float3 n, float3 toLight, float3 toEye)
{
    float h = saturate(dot(n, normalize(toLight + toEye)));
    float edge = 1.0 - _HighlightSize;
    return smoothstep(edge - _HighlightSoftness, edge + _HighlightSoftness, h);
}

float ToonRim(float3 n, float3 toEye) { return pow(saturate(1.0 - dot(n, toEye)), max(_RimPower, 0.01)) * _RimStrength; }

float ToonLuminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

// ---- reading the game's light, fog and ambient volumes at a world point ----

// Screen position of a world point in the main camera (top-left origin, as the game's own volume lookups use), and its distance coordinate
// in the game's froxel volumes (fog and indirect light): sqrt of the distance over the far plane, scaled by _FogConversionFrom.
float3 ToonFroxel(float3 world)
{
    float4 clip = mul(UNITY_MATRIX_VP, float4(world, 1.0));
    float2 uv = clip.xy / clip.w * float2(0.5, -0.5) + 0.5;
    float z = sqrt(saturate(distance(world, _WorldSpaceCameraPos) / _ProjectionParams.z * _FogConversionFrom));
    return float3(uv, z);
}

// The game's indirect (ambient) light at a surface point: sampled a little off the surface along its normal, as its forward glass does.
float3 ToonAmbient(float3 world, float3 n)
{
    float d = distance(world, _WorldSpaceCameraPos);
    float3 p = world + n * (min(d * 0.05, 0.5) * (1.0 - exp2(-d * 1.442695)));
    float3 ambient = max(tex3Dlod(_IndirectVolume, float4(ToonFroxel(p), 0.0)).rgb, 0.0);
    return _VolumesEnabled != 0.0 ? ambient : float3(0.0, 0.0, 0.0);
}

// The game's volumetric fog in front of a surface point: colour times exp(-depth * _FogColor) plus the in-scattered light.
float3 ToonFog(float3 colour, float3 world)
{
    float4 fog = tex3Dlod(_FogVolume, float4(ToonFroxel(world), 0.0));
    float3 fogged = colour * exp2(-fog.a * _FogColor.rgb * 1.442695) + fog.rgb;
    return _VolumesEnabled != 0.0 ? fogged : colour;
}

// The game's light-cluster cell of a world point (Glass and ClusterDeferred: x across, y down the screen, z by sqrt of eye depth over
// _ClusterFarZ; cell index (z * Y + y) * X + x).
uint4 ToonCluster(float3 world)
{
    float4 clip = mul(UNITY_MATRIX_VP, float4(world, 1.0));
    float2 uv = clip.xy / clip.w * float2(0.5, -0.5) + 0.5;
    float eye = -mul(UNITY_MATRIX_V, float4(world, 1.0)).z;
    float3 cell = floor(float3(uv.x * _ClusterSize.x, uv.y * _ClusterSize.y, sqrt(saturate(eye / _ClusterFarZ)) * _ClusterSize.z - 0.5));
    cell = clamp(cell, 0.0, _ClusterSize - 1.0);
    return _LightCluster[(uint)((cell.z * _ClusterSize.y + cell.y) * _ClusterSize.x + cell.x)];
}

// A point or effect light's colour at a distance: the game's window times (1 / (d^2 + softening) + window), softened by _NearPeak for
// lights whose colour alpha is below one.
float3 ToonPointLight(float4 posInvRangeSq, float4 colourPhysical, float3 world, out float3 toLight)
{
    float3 delta = posInvRangeSq.xyz - world;
    float d2 = dot(delta, delta);
    toLight = delta * rsqrt(max(d2, 1e-8));
    float window = saturate(1.0 - d2 * posInvRangeSq.w);
    return colourPhysical.rgb * window * (1.0 / (d2 + (1.0 - colourPhysical.w) * _NearPeak + 0.001) + window);
}

// The game's shadow-atlas read (ClusterDeferred): a cubic B-spline filter over a 4 x 4 texel footprint in four bilinear taps, on both
// channels (the exponential depth and the mask), before the exponential test. One bilinear tap shows the atlas texels as hard blocks where a
// body shadows itself.
float2 ToonShadowTaps(float2 atlas)
{
    float2 p = atlas * _ShadowAtlas_TexelSize.zw - 0.5;
    float2 f = frac(p);
    p -= f;
    float2 a = 1.0 - f, b = 2.0 - f, c = 3.0 - f;
    float2 w0 = a * a * a, w1 = b * b * b - 4.0 * w0, w2 = 6.0 * w0 + c * c * c - 4.0 * b * b * b;
    float2 w3 = 6.0 - w0 - w1 - w2;
    float2 g0 = w0 + w1, g1 = w2 + w3;
    float2 t0 = (p - 0.5 + w1 / g0) * _ShadowAtlas_TexelSize.xy, t1 = (p + 1.5 + w3 / g1) * _ShadowAtlas_TexelSize.xy;
    float2 s = g0 / (g0 + g1);
    float2 row0 = lerp(tex2Dlod(_ShadowAtlas, float4(t1.x, t0.y, 0.0, 0.0)).xy, tex2Dlod(_ShadowAtlas, float4(t0.x, t0.y, 0.0, 0.0)).xy, s.x);
    float2 row1 = lerp(tex2Dlod(_ShadowAtlas, float4(t1.x, t1.y, 0.0, 0.0)).xy, tex2Dlod(_ShadowAtlas, float4(t0.x, t1.y, 0.0, 0.0)).xy, s.x);
    return lerp(row1, row0, s.y);
}

// A spot light: its view-projection square and falloff; `shadow` is its shadow from the game's 8 x 5 shadow atlas (exponential shadow
// map), left for the caller to apply.
float3 ToonSpotLight(uint k, float3 world, out float3 toLight, out float shadow)
{
    float3 delta = _SpotPosInvRangeSq[k].xyz - world;
    float d2 = dot(delta, delta);
    toLight = delta * rsqrt(max(d2, 1e-8));
    float window = saturate(1.0 - d2 * _SpotPosInvRangeSq[k].w);
    float falloff = window * (1.0 / (d2 + 0.001) + window);
    float4 s = mul(_SpotVP[k], float4(world, 1.0));
    float2 xy = s.xy / s.w;
    float inside = (max(abs(xy.x), abs(xy.y)) < 1.0 - 4.0 * VSM_ShadowResInv ? 1.0 : 0.0) * saturate(s.w * 100.0);
    float tile = _SpotParams[k].y;
    float2 atlas = saturate(xy * 0.5 + 0.5) * float2(0.125, 0.2) + float2(floor(tile / 5.0) * 0.125, fmod(tile, 5.0) * 0.2);
    float2 map = ToonShadowTaps(atlas);
    shadow = min(exp2((abs(map.x) - (1.0 - s.z * _SpotParams[k].x)) * 144.2695), 1.0) * map.y;
    return _SpotColor[k].rgb * (inside * falloff);
}

// ---- the shadow: Unity's shadow caster (a pass with `#pragma multi_compile_shadowcaster`), cut out like the surface ----
struct ToonShadowV2F
{
    V2F_SHADOW_CASTER;
    float2 uv : TEXCOORD1;
};

ToonShadowV2F ToonShadowVert(appdata_base v)
{
    ToonShadowV2F o;
    TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
    o.uv = TRANSFORM_TEX(v.texcoord.xy, _MainTex);
    return o;
}

float4 ToonShadowFrag(ToonShadowV2F i) : SV_Target
{
    ToonCut(i.uv);
    SHADOW_CASTER_FRAGMENT(i)
}

// ---- the outline: the back faces pushed out along the normal, a fixed width in screen pixels (at 1080 lines) ----
ToonV2F ToonOutlineVert(ToonIn v)
{
    ToonV2F o = ToonVert(v);
    float4 clipNormal = ToonClip(o.world + o.normal) - o.pos;
    float2 dir = clipNormal.xy;
    float len = length(dir);
    dir = len > 1e-6 ? dir / len : float2(0.0, 0.0);
    o.pos.xy += dir * float2(_ScreenParams.y / _ScreenParams.x, 1.0) * (_OutlineWidth * 2.0 / 1080.0) * o.pos.w;
    return o;
}

// The outline colour as the surface beside it is lit (a black line stays black; a coloured line darkens with its room, so the game's
// exposure does not turn it white) and fogged like the surface.
float4 ToonOutlineFrag(ToonV2F i) : SV_Target
{
    clip(_OutlineWidth - 0.001);
    ToonCut(i.uv);
    float3 light = ToonAmbient(i.world, normalize(i.normal)) + 0.02;
    return float4(ToonFog(_OutlineColor.rgb * light, i.world), 1.0);
}

#endif
