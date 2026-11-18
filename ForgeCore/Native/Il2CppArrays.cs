using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeCore;

/// <summary>Whole-array copies between the game's arrays and plain ones. Indexing a game array element by element costs a call into the
/// game per element (and the implicit conversion of a plain array does the same per element), which made building a screen-sized
/// texture take most of a second and cutting a suit mesh a large part of a frame; these copy the memory in one go. Stores into game
/// arrays of references go through <see cref="Store{T}"/>. Main thread only.</summary>
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

    /// <summary>Stores <paramref name="value"/> at <paramref name="index"/> of a game array of references with the collector's write
    /// barrier, as the game's own stores do. The game runs the incremental collector (boot.config gc-max-time-slice) and
    /// Il2CppInterop's indexer (and its array-copying constructor) writes the pointer without the barrier: an object only that array
    /// holds, stored into an array the collector had already visited, is freed while the array still points at it. Every store into
    /// an array the game keeps goes through here. Reference element types only (not an Il2Cpp value type).</summary>
    public static void Store<T>(Il2CppReferenceArray<T> array, int index, T? value) where T : Il2CppObjectBase
    {
        if ((uint)index >= (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(index));
        IL2CPP.il2cpp_gc_wbarrier_set_field(array.Pointer, array.Pointer + (4 + index) * IntPtr.Size, value?.Pointer ?? IntPtr.Zero);
    }

    /// <summary>A game array of references holding <paramref name="source"/>, each element stored through <see cref="Store{T}"/>; use it
    /// instead of the constructor taking a plain array for an array the game keeps.</summary>
    public static Il2CppReferenceArray<T> ToGameReferences<T>(T?[] source) where T : Il2CppObjectBase
    {
        var result = new Il2CppReferenceArray<T>(source.Length);
        for (var i = 0; i < source.Length; i++) Store(result, i, source[i]);
        return result;
    }

    /// <summary>Fills an RGBA32 texture's top mip from <paramref name="pixels"/> (row by row from the bottom, as SetPixels32). The
    /// caller applies the texture.</summary>
    public static unsafe void Upload(Texture2D texture, Color32[] pixels)
    {
        fixed (Color32* p = pixels) texture.LoadRawTextureData((IntPtr)p, pixels.Length * 4);
    }
}
