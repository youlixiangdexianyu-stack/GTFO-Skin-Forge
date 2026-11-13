using System;
using UnityEngine;

namespace ForgeCore;

/// <summary>The vector constants and lengths of Unity's <c>Vector3</c> and <c>Quaternion</c> as plain managed values. In the game's
/// interop every <c>Vector3.up</c>, <c>Vector3.zero</c>, <c>Quaternion.identity</c>, <c>sqrMagnitude</c> or <c>magnitude</c> is a
/// native call; on a per-frame path these are the same numbers without the call (contract P8, hot-path getters).</summary>
public static class Vec
{
    public static readonly Vector3 Zero = default;
    public static readonly Vector3 Up = new(0f, 1f, 0f);
    public static readonly Vector3 Down = new(0f, -1f, 0f);
    public static readonly Vector3 Forward = new(0f, 0f, 1f);
    public static readonly Quaternion Identity = new(0f, 0f, 0f, 1f);

    /// <summary>The same value as <c>Vector3.Dot</c>, computed here: the game's interop makes that a native call.</summary>
    public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

    /// <summary>True when no component of <paramref name="v"/> is NaN or infinite.</summary>
    public static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    /// <summary>True when no component of <paramref name="q"/> is NaN or infinite.</summary>
    public static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w);

    /// <summary>The same value as <c>Vector3.sqrMagnitude</c>.</summary>
    public static float SqrLength(Vector3 v) => v.x * v.x + v.y * v.y + v.z * v.z;

    /// <summary>The same value as <c>Vector3.magnitude</c>.</summary>
    public static float Length(Vector3 v) => (float)Math.Sqrt(SqrLength(v));

    /// <summary><c>new Vector3(x, y, z)</c> without the interop constructor (a native call).</summary>
    public static Vector3 Of(float x, float y, float z) => new() { x = x, y = y, z = z };

    /// <summary>The same value as <c>m.MultiplyPoint3x4(new Vector3(x, y, z))</c>.</summary>
    public static Vector3 Point(Matrix4x4 m, float x, float y, float z)
        => Of(m.m00 * x + m.m01 * y + m.m02 * z + m.m03, m.m10 * x + m.m11 * y + m.m12 * z + m.m13, m.m20 * x + m.m21 * y + m.m22 * z + m.m23);

    /// <summary>The same value as <c>a * b</c>.</summary>
    public static Matrix4x4 Multiply(Matrix4x4 a, Matrix4x4 b) => new()
    {
        m00 = a.m00 * b.m00 + a.m01 * b.m10 + a.m02 * b.m20 + a.m03 * b.m30,
        m01 = a.m00 * b.m01 + a.m01 * b.m11 + a.m02 * b.m21 + a.m03 * b.m31,
        m02 = a.m00 * b.m02 + a.m01 * b.m12 + a.m02 * b.m22 + a.m03 * b.m32,
        m03 = a.m00 * b.m03 + a.m01 * b.m13 + a.m02 * b.m23 + a.m03 * b.m33,
        m10 = a.m10 * b.m00 + a.m11 * b.m10 + a.m12 * b.m20 + a.m13 * b.m30,
        m11 = a.m10 * b.m01 + a.m11 * b.m11 + a.m12 * b.m21 + a.m13 * b.m31,
        m12 = a.m10 * b.m02 + a.m11 * b.m12 + a.m12 * b.m22 + a.m13 * b.m32,
        m13 = a.m10 * b.m03 + a.m11 * b.m13 + a.m12 * b.m23 + a.m13 * b.m33,
        m20 = a.m20 * b.m00 + a.m21 * b.m10 + a.m22 * b.m20 + a.m23 * b.m30,
        m21 = a.m20 * b.m01 + a.m21 * b.m11 + a.m22 * b.m21 + a.m23 * b.m31,
        m22 = a.m20 * b.m02 + a.m21 * b.m12 + a.m22 * b.m22 + a.m23 * b.m32,
        m23 = a.m20 * b.m03 + a.m21 * b.m13 + a.m22 * b.m23 + a.m23 * b.m33,
        m30 = a.m30 * b.m00 + a.m31 * b.m10 + a.m32 * b.m20 + a.m33 * b.m30,
        m31 = a.m30 * b.m01 + a.m31 * b.m11 + a.m32 * b.m21 + a.m33 * b.m31,
        m32 = a.m30 * b.m02 + a.m31 * b.m12 + a.m32 * b.m22 + a.m33 * b.m32,
        m33 = a.m30 * b.m03 + a.m31 * b.m13 + a.m32 * b.m23 + a.m33 * b.m33,
    };

    /// <summary>Every element equal (exact; Unity's <c>==</c> is a native call).</summary>
    public static bool Same(Matrix4x4 a, Matrix4x4 b)
        => a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 && a.m03 == b.m03 && a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12
        && a.m13 == b.m13 && a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22 && a.m23 == b.m23 && a.m30 == b.m30 && a.m31 == b.m31
        && a.m32 == b.m32 && a.m33 == b.m33;
}
