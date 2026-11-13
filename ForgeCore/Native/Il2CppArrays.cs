using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>Whole-array copies between the game's arrays and plain ones. Indexing a game array element by element costs a call into the
/// game per element (and the implicit conversion of a plain array does the same per element), which made building a screen-sized
/// texture take most of a second and cutting a suit mesh a large part of a frame; these copy the memory in one go. Main thread only.</summary>
public static class Il2CppArrays
{
    /// <summary>A plain copy of a game array of value types (a mesh's triangles or bone weights, a texture's pixels).</summary>
    public static unsafe T[] ToManaged<T>(Il2CppStructArray<T> source) where T : unmanaged
    {
        var length = source.Length;
        var result = new T[length];
        if (length > 0) new ReadOnlySpan<T>((void*)IntPtr.Add(source.Pointer, 4 * IntPtr.Size), length).CopyTo(result);
        return result;
    }

    /// <summary>A game array holding a copy of <paramref name="source"/>.</summary>
    public static unsafe Il2CppStructArray<T> ToGame<T>(T[] source) where T : unmanaged
    {
        var result = new Il2CppStructArray<T>(source.Length);
        if (source.Length > 0) source.AsSpan().CopyTo(new Span<T>((void*)IntPtr.Add(result.Pointer, 4 * IntPtr.Size), source.Length));
        return result;
    }

    /// <summary>Fills an RGBA32 texture's top mip from <paramref name="pixels"/> (row by row from the bottom, as SetPixels32). The
    /// caller applies the texture.</summary>
    public static unsafe void Upload(Texture2D texture, Color32[] pixels)
    {
        fixed (Color32* p = pixels) texture.LoadRawTextureData((IntPtr)p, pixels.Length * 4);
    }
}
