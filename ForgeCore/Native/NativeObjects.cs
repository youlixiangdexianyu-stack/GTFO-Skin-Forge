using System;
using System.Diagnostics.CodeAnalysis;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace ForgeCore;

/// <summary>Liveness and real class of a game object read from the native side. Liveness without the Unity <c>==</c> overload. That overload is a call into the game on every use, which in
/// per-frame loops over many objects (enemy slots, placed-model batches) cost a tenth of a millisecond a frame; a destroyed object has a
/// zeroed native pointer, so reading that field answers the same question in managed code. Main thread only.</summary>
public static class NativeObjects
{
    /// <summary>True when <paramref name="target"/> is null, its managed wrapper was collected, or its native object was destroyed (the one definition of "gone" for every package).</summary>
    public static bool Gone([NotNullWhen(false)] UnityEngine.Object? target) => target is null || target.WasCollected || target.m_CachedPtr == IntPtr.Zero;

    /// <summary>True when the native object behind <paramref name="target"/> really is a <typeparamref name="T"/>, read from its IL2CPP class
    /// without making a wrapper (<c>TryCast</c> makes one). IL2CPP folds identical native bodies into one function, so a patched method
    /// can run for an object of an unrelated class whose wrapper Harmony still types as the declaring class: a handler that reads its
    /// arguments checks first.</summary>
    public static bool Is<T>([NotNullWhen(true)] Il2CppObjectBase? target) where T : Il2CppObjectBase
        => target != null && target.Pointer != IntPtr.Zero
            && IL2CPP.il2cpp_class_is_assignable_from(Il2CppClassPointerStore<T>.NativeClassPtr, IL2CPP.il2cpp_object_get_class(target.Pointer));
}
