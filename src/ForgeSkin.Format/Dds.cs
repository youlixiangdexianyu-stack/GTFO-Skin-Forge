namespace ForgeSkin.Format;

// A texture the editor compressed when it built the pack, so the plugin uploads it as it is (no decode, no DXT
// compression on the game's main thread). The document names it where it would name a PNG or JPEG, on a texture set
// straight on a material (not a layer atlas); a pack made with PNGs keeps working from them. It is read for one channel,
// whose sRGB it must fit. Standard DDS with the DX10 header (148 bytes), so any DDS tool reads the
// size, format and mip count and the sRGB flag is part of the format: BC1 (DXT1) for an opaque picture, BC3 (DXT5) for one
// with alpha and for normal maps (DXT5nm: x in alpha, y in green, red and blue 1, the game's gear layout). The full mip chain
// down to 1 x 1 follows the header, largest first, each level's rows from the bottom as Unity stores them (a DDS viewer
// shows it upside down). Both sides a multiple of 4 and at most MaxSide.
public sealed class Dds
{
    public const int HeaderSize = 148;
    public const int MaxSide = 8192;
    const uint Magic = 0x20534444; // "DDS "
    const uint FourCcDx10 = 0x30315844; // "DX10"
    const int Bc1 = 71, Bc1Srgb = 72, Bc3 = 77, Bc3Srgb = 78; // DXGI_FORMAT

    public int Width, Height, MipCount;
    public bool Alpha; // BC3; else BC1
    public bool Srgb;
    public byte[] Data = Array.Empty<byte>(); // the whole file: the blocks start at HeaderSize

    // Normal maps and metal maps are numbers, not colours.
    public static bool Linear(TextureChannel channel) => channel is TextureChannel.Normal or TextureChannel.MetallicSmoothness;

    public static bool IsDds(byte[] head) => head.Length >= 4 && BitConverter.ToUInt32(head, 0) == Magic;

    // Unity's full chain for these sides.
    public static int Mips(int width, int height)
    {
        int n = 1;
        for (int side = Math.Max(width, height); side > 1; side >>= 1) n++;
        return n;
    }

    public static long BlockBytes(int width, int height, int mips, bool alpha)
    {
        long total = 0;
        for (int i = 0; i < mips; i++)
            total += (long)((Math.Max(1, width >> i) + 3) / 4) * ((Math.Max(1, height >> i) + 3) / 4) * (alpha ? 16 : 8);
        return total;
    }

    // The file for `levels` (largest first, Unity's full chain, rows from the bottom).
    public static byte[] Write(int width, int height, bool alpha, bool srgb, IReadOnlyList<byte[]> levels)
    {
        var data = new byte[HeaderSize + levels.Sum(l => (long)l.Length)];
        void U32(int at, long v) => BitConverter.TryWriteBytes(data.AsSpan(at), (uint)v);
        U32(0, Magic);
        U32(4, 124);
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000); // caps, height, width, pixel format, mip count, linear size
        U32(12, height);
        U32(16, width);
        U32(20, BlockBytes(width, height, 1, alpha));
        U32(28, levels.Count);
        U32(76, 32); // pixel format: size, FOURCC, "DX10"
        U32(80, 0x4);
        U32(84, FourCcDx10);
        U32(108, 0x1000 | 0x400000 | 0x8); // texture, mipmap, complex
        U32(128, alpha ? srgb ? Bc3Srgb : Bc3 : srgb ? Bc1Srgb : Bc1);
        U32(132, 3); // 2D
        U32(140, 1); // array size
        int at = HeaderSize;
        foreach (var level in levels) { level.CopyTo(data, at); at += level.Length; }
        return data;
    }

    // Reads and checks a file meant for `channel`; anything the plugin could not upload as it is is `bad-image`.
    public static Dds Read(byte[] data, TextureChannel channel, string where)
    {
        void Bad(string why) => throw new SkinFormatException("bad-image", $"{where}: {why}");
        if (data.Length < HeaderSize) Bad("shorter than a DDS header");
        uint U32(int at) => BitConverter.ToUInt32(data, at);
        if (U32(0) != Magic || U32(4) != 124 || U32(76) != 32 || (U32(80) & 0x4) == 0 || U32(84) != FourCcDx10)
            Bad("not a DDS file with the DX10 header");
        if (U32(132) != 3 || U32(140) != 1 || U32(24) > 1) Bad("not a single 2D texture");
        var format = (int)U32(128);
        if (format is not (Bc1 or Bc1Srgb or Bc3 or Bc3Srgb)) Bad($"DXGI format {format} is not BC1 or BC3");
        var dds = new Dds
        {
            Width = (int)Math.Min(U32(16), int.MaxValue),
            Height = (int)Math.Min(U32(12), int.MaxValue),
            MipCount = (int)Math.Min(U32(28), int.MaxValue),
            Alpha = format is Bc3 or Bc3Srgb,
            Srgb = format is Bc1Srgb or Bc3Srgb,
            Data = data,
        };
        if (dds.Width is < 4 or > MaxSide || dds.Height is < 4 or > MaxSide || dds.Width % 4 != 0 || dds.Height % 4 != 0)
            Bad($"{dds.Width}x{dds.Height}: sides must be multiples of 4 from 4 to {MaxSide}");
        if (dds.MipCount != Mips(dds.Width, dds.Height)) Bad($"{dds.MipCount} mips, the full chain is {Mips(dds.Width, dds.Height)}");
        if (data.Length - HeaderSize != BlockBytes(dds.Width, dds.Height, dds.MipCount, dds.Alpha))
            Bad($"{data.Length - HeaderSize} bytes of blocks, {dds.Width}x{dds.Height} with mips needs {BlockBytes(dds.Width, dds.Height, dds.MipCount, dds.Alpha)}");
        if (dds.Srgb == Linear(channel)) Bad($"{(dds.Srgb ? "sRGB" : "linear")} data for a {channel} texture");
        if (channel == TextureChannel.Normal && !dds.Alpha) Bad("a normal map needs BC3 (x in alpha)");
        return dds;
    }
}
