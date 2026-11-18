// A line that ends in the air fades out over its last metres instead of stopping square. ForgeVisual sets both per copy (FxOptions.EndFade):
// _LineEnd the world point the line ends at, _LineFade the metres it fades over before it (0: no fade, a line that hit something keeps its end).
#ifndef FORGE_LINE_FADE_INCLUDED
#define FORGE_LINE_FADE_INCLUDED

float4 _LineEnd;
float _LineFade;

// In the vertex function: the vertex's world position (o.lineWorld is a float3 interpolator, v the input).
#define FORGE_LINE_VERTEX(o, v) o.lineWorld = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1)).xyz;

// 0..1 by the distance to the line's end; 1 when the line does not fade.
inline half ForgeLineFade(float3 world)
{
    return _LineFade > 0 ? saturate(distance(world, _LineEnd.xyz) / _LineFade) : 1;
}

#endif
