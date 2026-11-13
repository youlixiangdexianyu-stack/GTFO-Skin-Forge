using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>The size a PNG (or a JPEG, <see cref="TryJpegSize"/>) declares in its header, read before anything decodes it (a decoder allocates whatever the header claims). No game or Unity type.</summary>
public static class Png
{
    /// <summary>False unless <paramref name="bytes"/> start with the PNG signature and an IHDR chunk of a non-zero size.</summary>
    public static bool TrySize(IReadOnlyList<byte> bytes, out int width, out int height)
    {
        width = 0; height = 0;
        ReadOnlySpan<byte> head = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        if (bytes.Count < 24) return false;
        for (int index = 0; index < head.Length; index++) if (bytes[index] != head[index]) return false;
        uint w = BigEndian(bytes, 16), h = BigEndian(bytes, 20);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;
        width = (int)w; height = (int)h;
        return true;
    }

    /// <summary>The size a JPEG declares in its first frame header (SOF0 to SOF15 but DHT, JPG and DAC), read the same way before anything
    /// decodes it. False unless <paramref name="bytes"/> start with the JPEG start marker and reach such a header of a non-zero size.</summary>
    public static bool TryJpegSize(IReadOnlyList<byte> bytes, out int width, out int height)
    {
        width = 0; height = 0;
        if (bytes.Count < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
        var at = 2;
        while (at + 4 <= bytes.Count)
        {
            if (bytes[at] != 0xFF) return false;
            var marker = bytes[at + 1];
            if (marker == 0xFF) { at++; continue; } // fill byte
            if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01) { at += 2; continue; } // markers without a length
            if (marker == 0xD9 || marker == 0xDA) return false; // end of image, or the scan before any frame header
            var length = bytes[at + 2] << 8 | bytes[at + 3];
            if (length < 2) return false;
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (at + 9 > bytes.Count) return false;
                height = bytes[at + 5] << 8 | bytes[at + 6];
                width = bytes[at + 7] << 8 | bytes[at + 8];
                return width != 0 && height != 0;
            }
            at += 2 + length;
        }
        return false;
    }

    private static uint BigEndian(IReadOnlyList<byte> value, int offset)
        => (uint)value[offset] << 24 | (uint)value[offset + 1] << 16 | (uint)value[offset + 2] << 8 | value[offset + 3];
}
