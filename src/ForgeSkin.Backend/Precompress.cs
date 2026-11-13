using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// A pack texture compressed the way the plugin would have compressed it in the game (Core Dds), so the game only uploads
// it: BC1 when every pixel is opaque, else BC3; a normal map swizzled to the game's DXT5nm layout first (x in alpha, y in
// green, red and blue 1, as ForgeCore Images does), always BC3. Mips are 2 x 2 box averages, colour textures averaged in
// linear light. BCnEncoder (already the backend's encoder for game asset patches) does the blocks.
public static class Precompress
{
    // Part of the cache key: raise it when the output changes.
    public const string Version = "dds2";

    // The Dds of `image` (a PNG or JPEG) read as `channel`; null when a side is not a multiple of 4 or above Dds.MaxSide
    // (the plugin then decodes the image and leaves it uncompressed, as it always did).
    public static byte[]? Encode(byte[] image, TextureChannel channel, string what)
    {
        using var bmp = Images.Decode(image, what);
        int w = bmp.Width, h = bmp.Height;
        if (w % 4 != 0 || h % 4 != 0 || w > Dds.MaxSide || h > Dds.MaxSide) return null;
        var rgba = bmp.Bytes;
        bool normal = channel == TextureChannel.Normal, srgb = !Dds.Linear(channel);
        if (normal)
            for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2], rgba[i + 3]) = (255, 255, rgba[i]);
        bool alpha = normal;
        for (int i = 3; !alpha && i < rgba.Length; i += 4) alpha = rgba[i] != 255;
        // Unity's rows run from the bottom.
        var level = new byte[rgba.Length];
        for (int y = 0, row = w * 4; y < h; y++) Buffer.BlockCopy(rgba, (h - 1 - y) * row, level, y * row, row);

        var encoder = new BcEncoder(alpha ? CompressionFormat.Bc3 : CompressionFormat.Bc1);
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.BestQuality;
        var levels = new List<byte[]>();
        int lw = w, lh = h;
        for (int i = 0, n = Dds.Mips(w, h); i < n; i++)
        {
            levels.Add(encoder.EncodeToRawBytes(level, lw, lh, PixelFormat.Rgba32)[0]);
            if (i + 1 < n) (level, lw, lh) = Half(level, lw, lh, srgb);
        }
        return Dds.Write(w, h, alpha, srgb, levels);
    }

    // A pack texture of `channel` as straight RGBA rows from the top, in the layout an author paints (a normal map with x in
    // red, y in green and z rebuilt in blue): a PNG or JPEG decoded, a Dds's top level decoded back (lossy, as compressed).
    public static SkiaSharp.SKBitmap Pixels(byte[] bytes, TextureChannel channel, string what)
    {
        if (!Dds.IsDds(bytes)) return Images.Decode(bytes, what);
        var dds = Dds.Read(bytes, channel, what);
        int w = dds.Width, h = dds.Height;
        var px = new BCnEncoder.Decoder.BcDecoder().DecodeRaw(bytes.AsSpan(Dds.HeaderSize, (int)Dds.BlockBytes(w, h, 1, dds.Alpha)).ToArray(), w, h,
            dds.Alpha ? CompressionFormat.Bc3 : CompressionFormat.Bc1);
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = px[(h - 1 - y) * w + x];
                int o = (y * w + x) * 4;
                if (channel == TextureChannel.Normal)
                {
                    double nx = p.a / 127.5 - 1, ny = p.g / 127.5 - 1;
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (p.a, p.g, (byte)Math.Round((Math.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny)) + 1) * 127.5), 255);
                }
                else (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (p.r, p.g, p.b, p.a);
            }
        return Images.FromRgba(w, h, rgba);
    }

    // A pack texture as PNG bytes (for a format that only reads pictures): a PNG or JPEG as it is, a Dds decoded back.
    public static byte[] Png(byte[] bytes, TextureChannel channel, string what)
    {
        if (!Dds.IsDds(bytes)) return bytes;
        using var bmp = Pixels(bytes, channel, what);
        using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i =>
    {
        double c = i / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    }).ToArray();

    static byte ToSrgb(float l)
    {
        double c = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(c * 255), 0, 255);
    }

    // The next mip: each pixel the average of a 2 x 2 square (a side already 1 stays 1); alpha and linear data as numbers.
    static (byte[], int, int) Half(byte[] src, int w, int h, bool srgb)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        Parallel.For(0, nh, y =>
        {
            int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4, o = (y * nw + x) * 4;
                for (int k = 0; k < 4; k++)
                    dst[o + k] = srgb && k < 3
                        ? ToSrgb((ToLinear[src[a + k]] + ToLinear[src[b + k]] + ToLinear[src[c + k]] + ToLinear[src[d + k]]) * 0.25f)
                        : (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) / 4);
            }
        });
        return (dst, nw, nh);
    }
}
