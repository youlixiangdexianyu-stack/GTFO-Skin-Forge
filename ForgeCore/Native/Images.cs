using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>PNG or JPEG bytes to a texture, the one decoder the libraries and packages share. The header is read first
/// (<see cref="Png.TrySize"/>, <see cref="Png.TryJpegSize"/>) so a file that declares more than it may is refused before anything
/// allocates, and a texture the game could not decode is destroyed instead of left behind. <see cref="Queue(byte[], System.Action{UnityEngine.Texture2D, string}, bool, bool, int, bool, bool)"/> spreads the work over
/// frames, a PNG's decode on a worker thread; <c>Decode</c> does it at once. Called on the main thread: the game's decoder, compressor and uploads are Unity calls.</summary>
public static class Images
{
    /// <summary>Milliseconds of queued image work a frame may take; a step is started while the frame has used less. One step (decoding,
    /// or compressing, one picture) cannot be split, so a frame takes at least the first one however long it runs.</summary>
    public const double FrameBudgetMs = 4;

    static readonly Queue<ImageJob> Jobs = new();
    static readonly Stopwatch Watch = new();
    static bool _pumping;

    /// <summary>Decodes <paramref name="image"/> as <c>Decode</c> would with the same options, in two steps on later frames (the decode,
    /// then the DXT compression and dropping the CPU copy), within <see cref="FrameBudgetMs"/> a frame, in the order queued. A PNG is
    /// decoded on a worker thread (with its mips and the normal-map layout), so its main-thread step is the upload; a JPEG, or a PNG the
    /// managed reader does not take (interlaced, a transparent key colour), is decoded by the game in its step. On the main
    /// thread <paramref name="done"/> gets the texture (the caller owns it) and an empty failure, or null and why. Cancel the job to drop it;
    /// a texture already decoded for it is destroyed and <paramref name="done"/> is not called.</summary>
    public static ImageJob Queue(byte[] image, Action<Texture2D?, string> done, bool linear = false, bool mipChain = false, int maxSide = 8192,
        bool normalMap = false, bool compress = false)
        => Enqueue(new ImageJob(image, done, linear, mipChain, maxSide, normalMap, compress));

    /// <summary>Uploads texture data that is already in the GPU format (compressed blocks made ahead of time, say) in one step on a
    /// later frame, in the same queue and frame budget as <see cref="Queue(byte[], System.Action{UnityEngine.Texture2D, string}, bool, bool, int, bool, bool)"/>: a <paramref name="width"/> x <paramref name="height"/>
    /// <paramref name="format"/> texture (with Unity's full mip chain when <paramref name="mipChain"/>), filled from
    /// <paramref name="data"/> after <paramref name="offset"/> with <c>LoadRawTextureData</c> (levels largest first, rows from the
    /// bottom), then sent to the GPU without a CPU copy. Nothing is decoded or compressed, so the step costs a copy and the upload.
    /// <paramref name="done"/> as for <see cref="Queue(byte[], System.Action{UnityEngine.Texture2D, string}, bool, bool, int, bool, bool)"/>; the failure names why the game refused the data (too little for the size).</summary>
    public static ImageJob QueueRaw(byte[] data, int offset, int width, int height, TextureFormat format, bool mipChain, bool linear, Action<Texture2D?, string> done)
        => Enqueue(new ImageJob(data, done, linear, mipChain, 0, false, false) { Raw = true, Offset = offset, Width = width, Height = height, Format = format });

    static ImageJob Enqueue(ImageJob job)
    {
        // A PNG decodes on a worker thread (Png.TryDecode, no Unity call), its mips and normal-map swizzle too, so the main thread only
        // uploads it; one the managed reader does not take (a JPEG, an interlaced PNG) is decoded by the game on the main thread as before.
        if (!job.Raw) job.Pixels = System.Threading.Tasks.Task.Run(() => Pixels(job));
        Jobs.Enqueue(job);
        if (!_pumping) { _pumping = true; Frame.OnUpdate("NAinfini.ForgeCore", Pump); }
        return job;
    }

    static void Pump()
    {
        if (Jobs.Count == 0) return;
        Watch.Restart();
        do
        {
            var job = Jobs.Peek();
            if (job.Cancelled)
            {
                Jobs.Dequeue();
                if (job.Texture != null) UnityEngine.Object.Destroy(job.Texture);
                continue;
            }
            if (job.Raw)
            {
                Jobs.Dequeue();
                job.Texture = Upload(job, out var refused);
                job.Finish(refused);
                continue;
            }
            if (job.Texture == null)
            {
                if (!job.Pixels!.IsCompleted) break; // the worker is still decoding it; the queue keeps its order
                var failure = "";
                job.Texture = job.Pixels.Result is { } pixels
                    ? Upload(pixels, job.PixelWidth, job.PixelHeight, job.Linear || job.NormalMap, job.MipChain, job.Compress)
                    : Read(job.Image, out failure, job.Linear, false, job.MipChain, job.MaxSide, 0, 0, job.NormalMap, job.Compress);
                if (job.Texture == null) { Jobs.Dequeue(); job.Finish(failure); continue; }
                if (job.Compress) continue; // compressed on the next step
            }
            Jobs.Dequeue();
            Finish(job.Texture, false, job.NormalMap, job.Compress);
            job.Finish("");
        } while (Jobs.Count > 0 && Watch.Elapsed.TotalMilliseconds < FrameBudgetMs);
    }

    /// <summary>The texture of <paramref name="image"/>, or null with <paramref name="failure"/> naming why. <paramref name="linear"/> keeps the pixels
    /// as numbers (a metal map) instead of sRGB colour; <paramref name="readable"/> keeps a CPU copy for <c>GetPixels</c>; <paramref name="mipChain"/> builds mips
    /// (call <c>Apply(true, ...)</c> yourself after changing the filter). <paramref name="maxSide"/> caps either side; a non-zero
    /// <paramref name="width"/> x <paramref name="height"/> demands exactly that size. <paramref name="normalMap"/> reads a tangent-space normal map
    /// (OpenGL +Y, x in red) and stores it linear in the layout the game's own normal maps have on its gear shader (DXT5nm: x in alpha, y in
    /// green, red and blue 1), which both of Unity's normal unpackings read alike. <paramref name="compress"/> compresses it to DXT (a quarter
    /// of raw RGBA) when both sides are a multiple of 4, and leaves it raw otherwise. The caller owns the texture and destroys it.</summary>
    internal static Texture2D? Decode(byte[] image, out string failure, bool linear = false, bool readable = false, bool mipChain = false, int maxSide = 8192,
        int width = 0, int height = 0, bool normalMap = false, bool compress = false)
    {
        var texture = Read(image, out failure, linear, readable, mipChain, maxSide, width, height, normalMap, compress);
        if (texture != null) Finish(texture, readable, normalMap, compress);
        return texture;
    }

    // The decode: header checks, LoadImage and the normal-map swizzle. Keeps the CPU copy when Finish still needs it.
    static Texture2D? Read(byte[] image, out string failure, bool linear, bool readable, bool mipChain, int maxSide, int width, int height, bool normalMap, bool compress)
    {
        failure = "";
        if (!Png.TrySize(image, out var declaredWidth, out var declaredHeight) && !Png.TryJpegSize(image, out declaredWidth, out declaredHeight))
        { failure = "the file is not a PNG or JPEG"; return null; }
        if (declaredWidth > maxSide || declaredHeight > maxSide) { failure = "the picture is larger than " + maxSide + " pixels on a side"; return null; }
        if (width != 0 && (declaredWidth != width || declaredHeight != height)) { failure = "the picture is not " + width + " x " + height; return null; }
        // Swizzling and compressing need the pixels; the CPU copy goes once they are done.
        var keepPixels = readable || normalMap || compress;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain && !normalMap, linear || normalMap);
        var native = new Il2CppStructArray<byte>(image);
        try
        {
            if (!ImageConversion.LoadImage(texture, native, !keepPixels))
            {
                failure = "the game could not decode the picture";
                UnityEngine.Object.Destroy(texture);
                return null;
            }
        }
        catch (Exception error)
        {
            failure = "the game could not decode the picture: " + error;
            UnityEngine.Object.Destroy(texture);
            return null;
        }
        finally { GC.KeepAlive(native); }
        if (!normalMap) return texture;
        // LoadImage gives RGB24 for a picture without alpha; the swizzled copy needs an alpha channel.
        var pixels = texture.GetPixels32();
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            pixels[i] = new Color32(255, p.g, 255, p.r);
        }
        var swizzled = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, mipChain, true);
        swizzled.SetPixels32(pixels);
        swizzled.Apply(mipChain, false);
        UnityEngine.Object.Destroy(texture);
        return swizzled;
    }

    static unsafe Texture2D? Upload(ImageJob job, out string failure)
    {
        failure = "";
        if ((uint)job.Offset > (uint)job.Image.Length) { failure = "the offset " + job.Offset + " is outside the " + job.Image.Length + " bytes of data"; return null; }
        var texture = new Texture2D(job.Width, job.Height, job.Format, job.MipChain, job.Linear);
        try
        {
            fixed (byte* p = job.Image) texture.LoadRawTextureData((IntPtr)(p + job.Offset), job.Image.Length - job.Offset);
            texture.Apply(false, true);
            return texture;
        }
        catch (Exception error)
        {
            failure = "the game refused the texture data: " + error.Message;
            UnityEngine.Object.Destroy(texture);
            return null;
        }
    }

    // The compression and dropping the CPU copy kept for it.
    static void Finish(Texture2D texture, bool readable, bool normalMap, bool compress)
    {
        if (compress && texture.width % 4 == 0 && texture.height % 4 == 0) texture.Compress(true);
        if ((normalMap || compress) && !readable && texture.isReadable) texture.Apply(false, true);
    }

    // Worker thread: the job's PNG as RGBA32 in the layout LoadRawTextureData takes (levels largest first, rows from the bottom), with
    // the mip chain (each level the 2 x 2 average of the one above, as the game's own mip build) and the normal-map swizzle Read does.
    // Null when the managed reader does not take the file.
    static byte[]? Pixels(ImageJob job)
    {
        try
        {
            if (!Png.TryDecode(job.Image, job.MaxSide, out var w, out var h, out var top)) return null;
            if (job.NormalMap)
                for (var i = 0; i < top.Length; i += 4) (top[i], top[i + 2], top[i + 3]) = (255, 255, top[i]);
            // One buffer for every level (large arrays are what the collector stops the game's thread for).
            long total = 0;
            for (int lw = w, lh = h; ; lw = Math.Max(1, lw / 2), lh = Math.Max(1, lh / 2))
            {
                total += (long)lw * lh * 4;
                if (!job.MipChain || (lw == 1 && lh == 1)) break;
            }
            var all = new byte[total];
            for (int y = 0, row = w * 4; y < h; y++) Buffer.BlockCopy(top, (h - 1 - y) * row, all, y * row, row);
            for (int lw = w, lh = h, at = 0; job.MipChain && (lw > 1 || lh > 1);)
            {
                int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2), next = at + lw * lh * 4;
                for (var y = 0; y < nh; y++)
                    for (var x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(x * 2, lw - 1), x1 = Math.Min(x * 2 + 1, lw - 1), y0 = Math.Min(y * 2, lh - 1), y1 = Math.Min(y * 2 + 1, lh - 1);
                        for (var k = 0; k < 4; k++)
                            all[next + (y * nw + x) * 4 + k] = (byte)((all[at + (y0 * lw + x0) * 4 + k] + all[at + (y0 * lw + x1) * 4 + k] + all[at + (y1 * lw + x0) * 4 + k] + all[at + (y1 * lw + x1) * 4 + k] + 2) / 4);
                    }
                (at, lw, lh) = (next, nw, nh);
            }
            job.PixelWidth = w;
            job.PixelHeight = h;
            return all;
        }
        catch (Exception) { return null; } // left to the game's decoder
    }

    // Main thread: RGBA32 pixels from Pixels sent to the GPU; the CPU copy kept only when asked (to compress it next).
    static unsafe Texture2D Upload(byte[] pixels, int width, int height, bool linear, bool mipChain, bool keepReadable)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain, linear);
        fixed (byte* p = pixels) texture.LoadRawTextureData((IntPtr)p, pixels.Length);
        texture.Apply(false, !keepReadable);
        return texture;
    }
}

/// <summary>One picture queued with <see cref="Images.Queue(byte[], System.Action{UnityEngine.Texture2D, string}, bool, bool, int, bool, bool)"/>.</summary>
public sealed class ImageJob
{
    internal readonly byte[] Image;
    internal readonly bool Linear, MipChain, NormalMap, Compress;
    internal readonly int MaxSide;
    internal Texture2D? Texture; // decoded, waiting for its second step
    internal bool Raw; // QueueRaw: Image holds GPU-format data
    internal int Offset, Width, Height;
    internal TextureFormat Format;
    internal System.Threading.Tasks.Task<byte[]?>? Pixels; // the worker's decode (Images.Pixels), null for QueueRaw
    internal int PixelWidth, PixelHeight;
    readonly Action<Texture2D?, string> _done;

    internal ImageJob(byte[] image, Action<Texture2D?, string> done, bool linear, bool mipChain, int maxSide, bool normalMap, bool compress)
    {
        Image = image; _done = done; Linear = linear; MipChain = mipChain; MaxSide = maxSide; NormalMap = normalMap; Compress = compress;
    }

    /// <summary>True once cancelled; its callback never runs.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>True once its callback ran.</summary>
    public bool Done { get; private set; }

    /// <summary>Drops the job if its callback has not run yet.</summary>
    public void Cancel() { if (!Done) Cancelled = true; }

    internal void Finish(string failure)
    {
        Done = true;
        var texture = Texture;
        Texture = null;
        _done(texture, failure);
    }
}
