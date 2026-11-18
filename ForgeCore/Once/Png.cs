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

    /// <summary>The pixels of a PNG as RGBA, 8 bits a channel, rows from the top, decoded without the game (any thread): grey, grey with
    /// alpha, RGB, RGBA and palette images (with <c>tRNS</c>), 1 to 16 bits a sample (16 keeps the high byte), not interlaced. False for
    /// anything else, a size above <paramref name="maxSide"/>, or a damaged file; the caller then decodes it as before.</summary>
    public static bool TryDecode(byte[] bytes, int maxSide, out int width, out int height, out byte[] rgba)
    {
        rgba = Array.Empty<byte>();
        if (!TrySize(bytes, out width, out height) || width > maxSide || height > maxSide || bytes.Length < 33) return false;
        int depth = bytes[24], colour = bytes[25];
        if (bytes[26] != 0 || bytes[27] != 0 || bytes[28] != 0) return false; // compression, filter, interlace
        int samples = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (samples == 0 || depth is not (1 or 2 or 4 or 8 or 16) || (colour is 2 or 4 or 6 && depth < 8) || (colour == 3 && depth == 16)) return false;
        byte[]? palette = null, alpha = null;
        var idat = new System.IO.MemoryStream();
        for (int at = 8; at + 12 <= bytes.Length;)
        {
            var length = (int)Math.Min(BigEndian(bytes, at), int.MaxValue);
            if (length > bytes.Length - at - 12) return false;
            var type = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);
            if (type == "PLTE") palette = bytes.AsSpan(at + 8, length).ToArray();
            else if (type == "tRNS") alpha = bytes.AsSpan(at + 8, length).ToArray();
            else if (type == "IDAT") idat.Write(bytes, at + 8, length);
            else if (type == "IEND") break;
            at += 12 + length;
        }
        if (colour == 3 && palette == null || colour is 0 or 2 && alpha != null) return false; // a transparent key colour: left to the game's decoder
        int bitsPerPixel = samples * depth, stride = (width * bitsPerPixel + 7) / 8, step = Math.Max(1, bitsPerPixel / 8);
        var raw = new byte[(long)(stride + 1) * height];
        try
        {
            idat.Position = 0;
            using var inflate = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
            for (int read = 0, n; read < raw.Length; read += n)
                if ((n = inflate.Read(raw, read, raw.Length - read)) == 0) return false;
        }
        catch (System.IO.InvalidDataException) { return false; }
        // Undo the per-row filters in place (each row's first byte names it).
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1) + 1, prior = row - stride - 1;
            var filter = raw[row - 1];
            if (filter > 4) return false;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= step ? raw[row + x - step] : 0, b = y > 0 ? raw[prior + x] : 0, c = x >= step && y > 0 ? raw[prior + x - step] : 0;
                raw[row + x] += filter switch
                {
                    0 => (byte)0,
                    1 => (byte)a,
                    2 => (byte)b,
                    3 => (byte)((a + b) >> 1),
                    _ => Paeth(a, b, c),
                };
            }
        }
        var px = new byte[(long)width * height * 4];
        int shift = depth == 16 ? 2 : 1;
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1) + 1;
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                if (depth < 8)
                {
                    int bit = x * depth, value = raw[row + bit / 8] >> (8 - depth - bit % 8) & ((1 << depth) - 1);
                    if (colour == 3) Palette(value);
                    else { var g = (byte)(value * 255 / ((1 << depth) - 1)); (px[o], px[o + 1], px[o + 2], px[o + 3]) = (g, g, g, 255); }
                    continue;
                }
                int p = row + x * samples * shift;
                byte S(int i) => raw[p + i * shift];
                switch (colour)
                {
                    case 0: (px[o], px[o + 1], px[o + 2], px[o + 3]) = (S(0), S(0), S(0), 255); break;
                    case 2: (px[o], px[o + 1], px[o + 2], px[o + 3]) = (S(0), S(1), S(2), 255); break;
                    case 3: Palette(raw[p]); break;
                    case 4: (px[o], px[o + 1], px[o + 2], px[o + 3]) = (S(0), S(0), S(0), S(1)); break;
                    default: (px[o], px[o + 1], px[o + 2], px[o + 3]) = (S(0), S(1), S(2), S(3)); break;
                }
                void Palette(int index)
                {
                    if (index * 3 + 2 >= palette!.Length) index = 0;
                    (px[o], px[o + 1], px[o + 2], px[o + 3]) = (palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2], alpha != null && index < alpha.Length ? alpha[index] : (byte)255);
                }
            }
        }
        rgba = px;
        return true;
    }

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static uint BigEndian(IReadOnlyList<byte> value, int offset)
        => (uint)value[offset] << 24 | (uint)value[offset + 1] << 16 | (uint)value[offset + 2] << 8 | value[offset + 3];
}
