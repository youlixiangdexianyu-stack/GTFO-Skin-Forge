using System;
using System.Runtime.InteropServices;

namespace ForgeAudio;

/// <summary>One play every player hears: the sender picked the variant, pitch and volume, so every copy sounds the same. The
/// library and cue travel as <see cref="SyncKey"/> hashes of their names (no text on the wire, and the receiver finds them without
/// comparing strings). Fixed 50-byte layout; its field layout is part of the kit's network handshake.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SyncedPlay
{
    internal ulong Library, Cue;
    internal float X, Y, Z;
    /// <summary>The per-play gain (<see cref="PlayOptions.Gain"/>) and the cue volume the sender picked from the cue's range.</summary>
    internal float Gain, CueGain, Pitch, StartSeconds;
    /// <summary>The world sound's dimension; -1 for a 2D sound.</summary>
    internal int Dimension;
    internal byte Variant;
    /// <summary><see cref="SyncPlace"/>.</summary>
    internal byte Place;
}

internal enum SyncPlace : byte { TwoD = 0, World = 1 }

/// <summary>A received <see cref="SyncedPlay"/> after its values were checked.</summary>
internal readonly record struct SyncRequest(ulong Library, ulong Cue, SyncPlace Place, float X, float Y, float Z, float Gain, float CueGain,
    float Pitch, float StartSeconds, int Dimension, int Variant);

internal static class SyncCodec
{
    /// <summary>The highest variant number a message carries (a cue has at most <see cref="CueSchema.MaxClips"/>).</summary>
    internal const int MaxVariant = 254;

    internal static SyncedPlay Encode(ulong library, ulong cue, SyncPlace place, float x, float y, float z, float gain, float cueGain, float pitch,
        float startSeconds, int dimension, int variant)
        => new()
        {
            Library = library,
            Cue = cue,
            Place = (byte)place,
            X = x,
            Y = y,
            Z = z,
            Gain = gain,
            CueGain = cueGain,
            Pitch = pitch,
            StartSeconds = startSeconds,
            Dimension = place == SyncPlace.TwoD ? -1 : dimension,
            Variant = (byte)Math.Clamp(variant, 0, MaxVariant),
        };

    /// <summary>Checks a received message: a known place, finite values in the ranges a play accepts (gain 0 to 4, cue volume 0 to 4,
    /// pitch 0.1 to 3, start 0 or later), a dimension for a world sound and none for a 2D one. False, with the reason, otherwise.</summary>
    internal static bool TryDecode(in SyncedPlay message, out SyncRequest request, out string reason)
    {
        request = default;
        reason = "";
        if (message.Place > (byte)SyncPlace.World) { reason = "unknown placement " + message.Place; return false; }
        var place = (SyncPlace)message.Place;
        if (message.Library == 0 || message.Cue == 0) { reason = "no library or cue"; return false; }
        if (!In(message.Gain, 0, 4) || !In(message.CueGain, 0, 4) || !In(message.Pitch, .1f, 3) || !In(message.StartSeconds, 0, 3600))
        { reason = "gain, cue volume, pitch or start out of range"; return false; }
        if (place == SyncPlace.World && (!float.IsFinite(message.X) || !float.IsFinite(message.Y) || !float.IsFinite(message.Z) || message.Dimension < 0))
        { reason = "a world sound needs a finite position and a dimension"; return false; }
        if (message.Variant > MaxVariant) { reason = "variant out of range"; return false; }
        request = new SyncRequest(message.Library, message.Cue, place, message.X, message.Y, message.Z, message.Gain, message.CueGain, message.Pitch,
            message.StartSeconds, place == SyncPlace.TwoD ? -1 : message.Dimension, message.Variant);
        return true;
    }

    private static bool In(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
}

/// <summary>The network name of a library or cue: FNV-1a 64 over its name in lower case (cue names ignore case), computed once when a
/// library loads or a cue reference is made.</summary>
internal static class SyncKey
{
    internal static ulong Of(string name)
    {
        var hash = ForgeCore.Fnv.Offset64;
        foreach (var c in name)
        {
            var lower = char.ToLowerInvariant(c);
            hash = ForgeCore.Fnv.Mix64(ForgeCore.Fnv.Mix64(hash, (byte)lower), (byte)(lower >> 8));
        }
        return hash == 0 ? 1 : hash;
    }
}
