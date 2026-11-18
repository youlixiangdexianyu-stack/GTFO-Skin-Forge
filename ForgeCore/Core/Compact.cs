using System;

namespace ForgeCore;

/// <summary>Compact fixed-layout encoding helpers for long-running records and network messages: variable-length integers,
/// zig-zag deltas, range quantization and smallest-three quaternions. Everything writes into a caller-owned byte array at a
/// caller-owned position and allocates nothing. A malformed or truncated read throws <see cref="FormatException"/>; it never
/// returns a guess.</summary>
public static class Compact
{
    /// <summary>The most bytes a variable-length 64-bit integer takes.</summary>
    public const int MaxVarBytes = 10;

    public static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    public static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    public static void WriteVar(byte[] buffer, ref int position, ulong value)
    {
        while (value >= 0x80)
        {
            buffer[position++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buffer[position++] = (byte)value;
    }

    public static ulong ReadVar(byte[] buffer, ref int position)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= buffer.Length) throw new FormatException("A variable-length integer runs past the end of the data.");
            var b = buffer[position++];
            if (shift == 63 && b > 1) throw new FormatException("A variable-length integer does not fit 64 bits.");
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new FormatException("A variable-length integer is longer than 10 bytes.");
    }

    public static void WriteVarSigned(byte[] buffer, ref int position, long value) => WriteVar(buffer, ref position, ZigZag(value));

    public static long ReadVarSigned(byte[] buffer, ref int position) => UnZigZag(ReadVar(buffer, ref position));

    /// <summary>Writes <paramref name="current"/> as the zig-zag variable-length difference from <paramref name="previous"/>.</summary>
    public static void WriteDelta(byte[] buffer, ref int position, long previous, long current) =>
        WriteVarSigned(buffer, ref position, current - previous);

    public static long ReadDelta(byte[] buffer, ref int position, long previous) => previous + ReadVarSigned(buffer, ref position);

    /// <summary>The quantized step count of <paramref name="value"/>: <c>round((value - min) / step)</c>, clamped to
    /// 0..<paramref name="maxSteps"/> so a value outside the range saturates instead of wrapping.</summary>
    public static int Quantize(float value, float min, float step, int maxSteps)
    {
        if (step <= 0f) throw new ArgumentOutOfRangeException(nameof(step), "A quantization step is positive.");
        var steps = MathF.Round((value - min) / step);
        if (float.IsNaN(steps)) return 0;
        return steps <= 0f ? 0 : steps >= maxSteps ? maxSteps : (int)steps;
    }

    public static float Dequantize(int steps, float min, float step) => min + steps * step;

    public static void WriteUInt16(byte[] buffer, ref int position, ushort value)
    {
        buffer[position++] = (byte)value;
        buffer[position++] = (byte)(value >> 8);
    }

    public static ushort ReadUInt16(byte[] buffer, ref int position)
    {
        if (position + 2 > buffer.Length) throw new FormatException("A 16-bit value runs past the end of the data.");
        var v = (ushort)(buffer[position] | (buffer[position + 1] << 8));
        position += 2;
        return v;
    }

    /// <summary>Quaternion bytes: 2 bits for which component is largest, then the other three at 15 bits each, 6 bytes in all.
    /// Worst-case angular error is under 0.01 degrees.</summary>
    public const int QuaternionBytes = 6;

    private const int QuatBits = 15;
    private const int QuatMax = (1 << QuatBits) - 1;
    private const float QuatRange = 0.70710678f; // the other three components of a unit quaternion never exceed 1/sqrt(2)

    public static void WriteQuaternion(byte[] buffer, ref int position, float x, float y, float z, float w)
    {
        Span<float> q = stackalloc float[4] { x, y, z, w };
        var length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (!(length > 0f)) throw new ArgumentException("A quaternion cannot be zero or not a number.");
        var largest = 0;
        for (var i = 1; i < 4; i++) if (MathF.Abs(q[i]) > MathF.Abs(q[largest])) largest = i;
        var sign = q[largest] < 0f ? -1f : 1f; // q and -q are the same rotation: keep the largest component positive
        ulong packed = (ulong)largest;
        var shift = 2;
        for (var i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            var steps = Quantize(q[i] * sign / length, -QuatRange, 2f * QuatRange / QuatMax, QuatMax);
            packed |= (ulong)steps << shift;
            shift += QuatBits;
        }
        for (var i = 0; i < QuaternionBytes; i++) buffer[position++] = (byte)(packed >> (8 * i));
    }

    public static void ReadQuaternion(byte[] buffer, ref int position, out float x, out float y, out float z, out float w)
    {
        if (position + QuaternionBytes > buffer.Length) throw new FormatException("A quaternion runs past the end of the data.");
        ulong packed = 0;
        for (var i = 0; i < QuaternionBytes; i++) packed |= (ulong)buffer[position++] << (8 * i);
        var largest = (int)(packed & 3);
        Span<float> q = stackalloc float[4];
        var shift = 2;
        var sumSquares = 0f;
        for (var i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            var steps = (int)((packed >> shift) & QuatMax);
            q[i] = Dequantize(steps, -QuatRange, 2f * QuatRange / QuatMax);
            sumSquares += q[i] * q[i];
            shift += QuatBits;
        }
        q[largest] = MathF.Sqrt(MathF.Max(0f, 1f - sumSquares));
        x = q[0]; y = q[1]; z = q[2]; w = q[3];
    }
}
