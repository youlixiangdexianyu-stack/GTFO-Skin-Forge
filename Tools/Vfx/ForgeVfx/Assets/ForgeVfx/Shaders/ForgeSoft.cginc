// Soft particles for the built-in pipeline: a particle fades where it meets the scene behind it, read from the camera depth texture.
// _ForgeSoftDepth is a global ForgeVisual sets to 1 only while the eye's camera has a depth texture (the player camera renders deferred, which
// always has one, at no extra cost); at 0, the default (no ForgeVisual, a forward camera without one), nothing fades: a hard edge where a sprite
// cuts through a wall, never a particle faded to nothing by a depth texture that is not there.
#ifndef FORGE_SOFT_INCLUDED
#define FORGE_SOFT_INCLUDED

UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
float _ForgeSoftDepth;

// In the vertex function, after o.pos is set: the screen position and the vertex's own eye depth (o.softPos is a float4 interpolator, v the input).
#define FORGE_SOFT_VERTEX(o, v) o.softPos = ComputeScreenPos(o.pos); o.softPos.z = -UnityObjectToViewPos(v.vertex.xyz).z;

// 0..1: how far in front of the scene the particle is, over 1 / perUnit metres; 1 with soft particles off or perUnit 0. With byInverse the gap is
// measured in 1 / depth (1 / particle - 1 / scene): a raw depth-buffer difference, as Master MagicFX's depth fade reads it, its camera folded into perUnit.
inline half ForgeSoftFade(float4 softPos, float perUnit, half byInverse)
{
    float raw = SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(softPos));
    #if defined(UNITY_REVERSED_Z)
    float linear01 = 1 - raw;
    #else
    float linear01 = raw;
    #endif
    // an orthographic camera (the review sheet's) stores depth linearly; LinearEyeDepth is for a perspective one
    float sceneZ = unity_OrthoParams.w > 0 ? lerp(_ProjectionParams.y, _ProjectionParams.z, linear01) : LinearEyeDepth(raw);
    float apart = byInverse > 0.5 ? 1 / max(softPos.z, 0.0001) - 1 / max(sceneZ, 0.0001) : sceneZ - softPos.z;
    half fade = perUnit > 0 ? saturate(apart * perUnit) : 1;
    return lerp(1, fade, _ForgeSoftDepth);
}

#endif
