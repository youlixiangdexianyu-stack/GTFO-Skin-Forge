using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

// The Unity value types and the frame clock, kept apart from the object model so a test project whose own object model differs can still
// share the math. Unity.cs needs this file.

namespace UnityEngine
{
    public partial struct Vector2
    {
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float x, y;
    }

    /// <summary>Unity's Vector3: the production readers read x/y/z as fields, exactly like the interop struct.</summary>
    public partial struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0f, 0f, 0f);
        public static Vector3 one => new(1f, 1f, 1f);
        public static Vector3 up => new(0f, 1f, 0f);
        public static Vector3 down => new(0f, -1f, 0f);
        public static Vector3 Scale(Vector3 a, Vector3 b) => new(a.x * b.x, a.y * b.y, a.z * b.z);
        public static Vector3 operator -(Vector3 value) => new(-value.x, -value.y, -value.z);
        public void Normalize() => this = normalized;
        public static bool operator ==(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float scale) => new(a.x * scale, a.y * scale, a.z * scale);
        public static Vector3 operator /(Vector3 a, float scale) => new(a.x / scale, a.y / scale, a.z / scale);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized
        {
            get
            {
                var length = magnitude;
                return length <= 0f ? zero : new Vector3(x / length, y / length, z / length);
            }
        }
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDelta) { var d = target - current; var m = d.magnitude; return m <= maxDelta || m == 0f ? target : current + d * (maxDelta / m); }
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public override bool Equals(object? other) => other is Vector3 v && this == v;
        public override int GetHashCode() => HashCode.Combine(x, y, z);
    }

    public partial struct Vector4
    {
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public float x, y, z, w;
        public static Vector4 zero => new(0f, 0f, 0f, 0f);
    }

    public partial struct Quaternion
    {
        public static Quaternion Inverse(Quaternion q) { float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w; return new Quaternion(-q.x / n, -q.y / n, -q.z / n, q.w / n); }
        // Transform below rotates by these, and the Player adapter links this file without the Map doubles.
        public static Vector3 operator *(Quaternion rotation, Vector3 vector)
        {
            var result = System.Numerics.Vector3.Transform(new(vector.x, vector.y, vector.z), new System.Numerics.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w));
            return new(result.X, result.Y, result.Z);
        }
        public static Quaternion operator *(Quaternion left, Quaternion right)
        {
            var result = new System.Numerics.Quaternion(left.x, left.y, left.z, left.w) * new System.Numerics.Quaternion(right.x, right.y, right.z, right.w);
            return new(result.X, result.Y, result.Z, result.W);
        }
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new() { w = 1f };
        /// <summary>The direction the last `LookRotation` was asked about, so a case can read back the direction
        /// an effect was oriented by. The rotation itself is the identity: this double mirrors which member was
        /// reached, not the engine's own math.</summary>
        public static Vector3? LastLookRotation;
        public static Quaternion LookRotation(Vector3 forward) { LastLookRotation = forward; return identity; }
        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards) => LookRotation(forward);
        /// <summary>The rotation an authored Euler triple names, in the engine's own Z-X-Y order. A pose that
        /// only turns about +Y comes back out of `eulerAngles` as the same yaw; a pose that also pitches or rolls
        /// is carried here in full even though this double's `eulerAngles` only answers yaw.</summary>
        public static Quaternion Euler(float x, float y, float z)
        {
            const float ToHalfRadians = MathF.PI / 360f;
            float cx = MathF.Cos(x * ToHalfRadians), sx = MathF.Sin(x * ToHalfRadians);
            float cy = MathF.Cos(y * ToHalfRadians), sy = MathF.Sin(y * ToHalfRadians);
            float cz = MathF.Cos(z * ToHalfRadians), sz = MathF.Sin(z * ToHalfRadians);
            return new Quaternion(cy * sx * cz + sy * cx * sz, sy * cx * cz - cy * sx * sz,
                                  cy * cx * sz - sy * sx * cz, cy * cx * cz + sy * sx * sz);
        }
        /// <summary>The rotation's own Euler angles. Only the yaw is read, and it is the pitch-free yaw of a
        /// rotation about +Y — which is the only rotation every pose of this package composes.</summary>
        public Vector3 eulerAngles => new(0f, 2f * MathF.Atan2(y, w) * (180f / MathF.PI), 0f);
    }

    public partial struct Bounds
    {
        public Bounds(Vector3 center, Vector3 size) { this.center = center; this.size = size; }
        public Vector3 center, size;
        public Vector3 min => new(center.x - size.x / 2f, center.y - size.y / 2f, center.z - size.z / 2f);
        public Vector3 max => new(center.x + size.x / 2f, center.y + size.y / 2f, center.z + size.z / 2f);
        public void Encapsulate(Vector3 value) => Encapsulate(new Bounds(value, Vector3.zero));
        public void Encapsulate(Bounds value)
        {
            var lower = new Vector3(MathF.Min(min.x, value.min.x), MathF.Min(min.y, value.min.y), MathF.Min(min.z, value.min.z));
            var upper = new Vector3(MathF.Max(max.x, value.max.x), MathF.Max(max.y, value.max.y), MathF.Max(max.z, value.max.z));
            center = (lower + upper) * .5f;
            size = upper - lower;
        }
        /// <summary>Half the box's own size, which is the offset a corner stands at from its centre.</summary>
        public Vector3 extents => size * 0.5f;
    }

    /// <summary>Unity's 4x4 matrix with its own row-by-column fields (`mRC`), column-vector convention: a translation sits
    /// in `m03`, `m13` and `m23`.</summary>
    public partial struct Matrix4x4
    {
        public float m00, m10, m20, m30;
        public float m01, m11, m21, m31;
        public float m02, m12, m22, m32;
        public float m03, m13, m23, m33;

        public static Matrix4x4 identity => new Matrix4x4 { m00 = 1f, m11 = 1f, m22 = 1f, m33 = 1f };

        private static Matrix4x4 FromNumerics(System.Numerics.Matrix4x4 n) => new Matrix4x4
        {
            m00 = n.M11,
            m01 = n.M21,
            m02 = n.M31,
            m03 = n.M41,
            m10 = n.M12,
            m11 = n.M22,
            m12 = n.M32,
            m13 = n.M42,
            m20 = n.M13,
            m21 = n.M23,
            m22 = n.M33,
            m23 = n.M43,
            m30 = n.M14,
            m31 = n.M24,
            m32 = n.M34,
            m33 = n.M44
        };

        public static Matrix4x4 TRS(Vector3 position, Quaternion rotation, Vector3 scale)
            => FromNumerics(System.Numerics.Matrix4x4.CreateScale(scale.x, scale.y, scale.z)
                * System.Numerics.Matrix4x4.CreateFromQuaternion(new(rotation.x, rotation.y, rotation.z, rotation.w))
                * System.Numerics.Matrix4x4.CreateTranslation(position.x, position.y, position.z));

        public Vector3 MultiplyPoint3x4(Vector3 point)
            => new(m00 * point.x + m01 * point.y + m02 * point.z + m03,
                   m10 * point.x + m11 * point.y + m12 * point.z + m13,
                   m20 * point.x + m21 * point.y + m22 * point.z + m23);

        /// <summary>`left * right`: `right` applied first, then `left`, the way Unity composes matrices.</summary>
        public static Matrix4x4 operator *(Matrix4x4 left, Matrix4x4 right) => new Matrix4x4
        {
            m00 = left.m00 * right.m00 + left.m01 * right.m10 + left.m02 * right.m20 + left.m03 * right.m30,
            m01 = left.m00 * right.m01 + left.m01 * right.m11 + left.m02 * right.m21 + left.m03 * right.m31,
            m02 = left.m00 * right.m02 + left.m01 * right.m12 + left.m02 * right.m22 + left.m03 * right.m32,
            m03 = left.m00 * right.m03 + left.m01 * right.m13 + left.m02 * right.m23 + left.m03 * right.m33,
            m10 = left.m10 * right.m00 + left.m11 * right.m10 + left.m12 * right.m20 + left.m13 * right.m30,
            m11 = left.m10 * right.m01 + left.m11 * right.m11 + left.m12 * right.m21 + left.m13 * right.m31,
            m12 = left.m10 * right.m02 + left.m11 * right.m12 + left.m12 * right.m22 + left.m13 * right.m32,
            m13 = left.m10 * right.m03 + left.m11 * right.m13 + left.m12 * right.m23 + left.m13 * right.m33,
            m20 = left.m20 * right.m00 + left.m21 * right.m10 + left.m22 * right.m20 + left.m23 * right.m30,
            m21 = left.m20 * right.m01 + left.m21 * right.m11 + left.m22 * right.m21 + left.m23 * right.m31,
            m22 = left.m20 * right.m02 + left.m21 * right.m12 + left.m22 * right.m22 + left.m23 * right.m32,
            m23 = left.m20 * right.m03 + left.m21 * right.m13 + left.m22 * right.m23 + left.m23 * right.m33,
            m30 = left.m30 * right.m00 + left.m31 * right.m10 + left.m32 * right.m20 + left.m33 * right.m30,
            m31 = left.m30 * right.m01 + left.m31 * right.m11 + left.m32 * right.m21 + left.m33 * right.m31,
            m32 = left.m30 * right.m02 + left.m31 * right.m12 + left.m32 * right.m22 + left.m33 * right.m32,
            m33 = left.m30 * right.m03 + left.m31 * right.m13 + left.m32 * right.m23 + left.m33 * right.m33
        };
    }

    public partial struct Color
    {
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public Color(float r, float g, float b) : this(r, g, b, 1f) { }
        public float r, g, b, a;
        public override string ToString() => r + "," + g + "," + b + "," + a;
        public Color linear => this;
        public static Color white => new(1f, 1f, 1f, 1f);
        public static Color clear => new(0f, 0f, 0f, 0f);
        public static bool operator ==(Color left, Color right) => left.r == right.r && left.g == right.g && left.b == right.b && left.a == right.a;
        public static bool operator !=(Color left, Color right) => !(left == right);
        public bool Equals(Color other) => this == other;
        public override bool Equals(object? obj) => obj is Color other && this == other;
        public override int GetHashCode() => HashCode.Combine(r, g, b, a);
        public static Color Lerp(Color from, Color to, float progress) => new(
            from.r + (to.r - from.r) * progress, from.g + (to.g - from.g) * progress,
            from.b + (to.b - from.b) * progress, from.a + (to.a - from.a) * progress);
    }

    /// <summary>The frame's own delta: the one reading the session hands its clock, which the light fade steps by.</summary>
    public static partial class Time
    {
        public static int frameCount { get; set; }
        public static float realtimeSinceStartup { get; set; }
        public static float fixedDeltaTime { get; set; } = 0.02f;
        public static float deltaTime { get; set; } = 1f / 60f;
        /// <summary>The frame's own absolute time, which the interaction-prompt readback stamps a rule with.</summary>
        public static float time { get; set; }
    }

}
