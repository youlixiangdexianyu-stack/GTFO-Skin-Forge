using System;
using System.Buffers.Binary;

namespace ForgeAudio;

// Its own file so the ForgeSkin editor backend compiles the same check (it links this file and Results.cs) and refuses
// when a pack is built the clip the game would refuse when it loads.

internal readonly record struct OggInfo(int Channels, int SampleRate, long Frames)
{
    internal long DecodedBytes => Frames * Channels * sizeof(float);
}

/// <summary>Container checks before any decoding: one Ogg Vorbis stream, mono or stereo, size and length bounds.</summary>
internal static class AssetValidation
{
    internal const int MaxFileBytes = 64 * 1024 * 1024;
    internal const long MaxDecodedBytes = 512L * 1024 * 1024;
    internal const long MaxCompressedCache = 256L * 1024 * 1024;

    /// <param name="bytes">The whole encoded file.</param>
    /// <param name="maxSeconds">The longest clip admitted: an hour, or less where the caller's content format sets a shorter limit.</param>
    internal static Result<OggInfo> Ogg(byte[] bytes, double maxSeconds = 3600)
    {
        if (bytes.Length > MaxFileBytes) return Result<OggInfo>.Fail(AudioError.AssetLimit, "file exceeds 64 MiB.");
        // First Ogg page must contain a Vorbis identification packet; rejects Opus and renamed WAV/MP3.
        if (bytes.Length < 58 || bytes[0] != 'O' || bytes[1] != 'g' || bytes[2] != 'g' || bytes[3] != 'S' || bytes[4] != 0 || bytes[26] < 1)
            return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "not an Ogg file (convert it with convert_audio.py).");
        var packet = 27 + bytes[26];
        if (packet + 30 > bytes.Length || bytes[packet] != 1 || System.Text.Encoding.ASCII.GetString(bytes, packet + 1, 6) != "vorbis")
            return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "Ogg but its first stream is not Vorbis audio (Opus, or a video or picture stream; only Ogg Vorbis plays).");
        var channels = bytes[packet + 11]; var rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(packet + 12));
        if (channels is < 1 or > 2 || rate is < 8000 or > 192000 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(packet + 7)) != 0 || bytes[packet + 29] != 1)
            return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "expected mono or stereo Vorbis at 8 to 192 kHz.");
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14));
        int offset = 0; uint sequence = 0; ulong samples = 0; var ended = false;
        while (offset < bytes.Length)
        {
            if (ended || bytes.Length - offset < 27 || bytes[offset] != 'O' || bytes[offset + 1] != 'g' || bytes[offset + 2] != 'g' || bytes[offset + 3] != 'S'
                || bytes[offset + 4] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 14)) != serial
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 18)) != sequence++)
                return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "invalid or chained Ogg pages (one Vorbis stream is required).");
            var flags = bytes[offset + 5];
            if ((offset == 0 && flags != 2 && flags != 6) || (offset != 0 && (flags & 2) != 0))
                return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "invalid Ogg beginning-of-stream flag.");
            var segments = bytes[offset + 26];
            var body = offset + 27 + segments;
            if (body > bytes.Length) return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "truncated Ogg segment table.");
            int length = 0; for (var i = 0; i < segments; i++) length += bytes[offset + 27 + i];
            if (length > bytes.Length - body || offset == 0 && length < 30) return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "truncated Ogg page.");
            var granule = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 6));
            if (granule != ulong.MaxValue)
            {
                if (granule < samples) return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "non-monotonic Vorbis sample positions.");
                samples = granule;
                if (samples > rate * Math.Min(maxSeconds, 3600) || samples > (ulong)MaxDecodedBytes / channels / 4)
                    return Result<OggInfo>.Fail(AudioError.AssetLimit, "longer than " + Math.Min(maxSeconds, 3600).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " s or over " + (MaxDecodedBytes >> 20) + " MiB of decoded audio.");
            }
            ended = (flags & 4) != 0;
            offset = body + length;
        }
        if (!ended || samples == 0) return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "Ogg stream is truncated (no final page).");
        return Result<OggInfo>.Ok(new OggInfo(channels, (int)rate, (long)samples));
    }
}
