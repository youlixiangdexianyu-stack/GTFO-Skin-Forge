using System;
using System.Text;

namespace ForgeCore;

/// <summary>The one FNV-1a hash (32 and 64 bit). The output is part of wire formats and saved keys, so the algorithm never changes:
/// start from the offset basis, then for every byte XOR it in and multiply by the prime. Strings are hashed as their UTF-8 bytes; the
/// <c>Mix</c> methods feed a value one byte at a time for callers that hash a record rather than text. No game or Unity type.</summary>
public static class Fnv
{
    public const uint Offset32 = 2166136261u, Prime32 = 16777619u;
    public const ulong Offset64 = 14695981039346656037ul, Prime64 = 1099511628211ul;

    /// <summary>Feeds one byte into a 32-bit hash that started at <see cref="Offset32"/>.</summary>
    public static uint Mix32(uint hash, byte value) => unchecked((hash ^ value) * Prime32);

    /// <summary>Feeds one byte into a 64-bit hash that started at <see cref="Offset64"/>.</summary>
    public static ulong Mix64(ulong hash, byte value) => unchecked((hash ^ value) * Prime64);

    /// <summary>Feeds the four bytes of <paramref name="value"/>, least significant first, into a 32-bit hash.</summary>
    public static uint Mix32(uint hash, uint value)
    {
        for (var s = 0; s < 32; s += 8) hash = Mix32(hash, (byte)(value >> s));
        return hash;
    }

    public static uint Mix32(uint hash, int value) => Mix32(hash, unchecked((uint)value));

    /// <summary>Feeds the eight bytes of <paramref name="value"/>, least significant first, into a 32-bit hash.</summary>
    public static uint Mix32(uint hash, ulong value)
    {
        for (var s = 0; s < 64; s += 8) hash = Mix32(hash, (byte)(value >> s));
        return hash;
    }

    /// <summary>Feeds the eight bytes of <paramref name="value"/>, least significant first, into a 64-bit hash.</summary>
    public static ulong Mix64(ulong hash, ulong value)
    {
        for (var s = 0; s < 64; s += 8) hash = Mix64(hash, (byte)(value >> s));
        return hash;
    }

    public static ulong Mix64(ulong hash, long value) => Mix64(hash, unchecked((ulong)value));

    /// <summary>Feeds the UTF-8 bytes of <paramref name="text"/> into a 32-bit hash without allocating; the result equals
    /// <c>Hash32</c> of the text when <paramref name="hash"/> starts at <see cref="Offset32"/> (a lone surrogate is U+FFFD, as in
    /// <see cref="Encoding.UTF8"/>).</summary>
    public static uint MixUtf8(uint hash, string text)
    {
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            var count = rune.EncodeToUtf8(buffer);
            for (var b = 0; b < count; b++) hash = Mix32(hash, buffer[b]);
        }
        return hash;
    }

    /// <summary>As <see cref="MixUtf8(uint, string)"/> for a 64-bit hash.</summary>
    public static ulong MixUtf8(ulong hash, string text)
    {
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in text.EnumerateRunes())
        {
            var count = rune.EncodeToUtf8(buffer);
            for (var b = 0; b < count; b++) hash = Mix64(hash, buffer[b]);
        }
        return hash;
    }

    public static uint Hash32(ReadOnlySpan<byte> bytes)
    {
        var hash = Offset32;
        foreach (var b in bytes) hash = Mix32(hash, b);
        return hash;
    }

    public static ulong Hash64(ReadOnlySpan<byte> bytes)
    {
        var hash = Offset64;
        foreach (var b in bytes) hash = Mix64(hash, b);
        return hash;
    }

    /// <summary>FNV-1a 32 over the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static uint Hash32(string text) => Hash32(Encoding.UTF8.GetBytes(text));

    /// <summary>FNV-1a 64 over the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static ulong Hash64(string text) => Hash64(Encoding.UTF8.GetBytes(text));
}
