using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace ForgeCore;

/// <summary>Liveness, identity and real class of a game object read from the native side. Liveness without the Unity <c>==</c> overload. That overload is a call into the game on every use, which in
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

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> wrap the same native object, or are both null. Il2CppInterop makes a
    /// new managed wrapper every time a game object crosses into C# (a getter, a hook argument, <c>GetComponentsInChildren</c>) and keeps no
    /// cache, and no wrapper overrides <see cref="object.Equals(object)"/> or <see cref="object.GetHashCode"/>: two reads of one renderer are two
    /// unequal objects to <c>ReferenceEquals</c>, <c>==</c> on a class that is not a <see cref="UnityEngine.Object"/> (a course node, an
    /// <c>AgentTarget</c>, a data block), <c>object.Equals</c>, and every <c>List</c>, <c>HashSet</c>, <c>Dictionary</c> and LINQ set
    /// operation that uses the default comparer. The native pointer is the object's identity while a wrapper holds it.</summary>
    public static bool Same(Il2CppObjectBase? a, Il2CppObjectBase? b)
        => ReferenceEquals(a, b) || a is not null && b is not null && a.Pointer == b.Pointer;

    /// <summary>Compares wrappers by native object (<see cref="Same"/>): pass it to a <c>HashSet</c>, <c>Dictionary</c> or LINQ set
    /// operation keyed by game objects, which otherwise compare the wrappers. It works for any wrapper type (the comparer is contravariant).
    /// A key keeps its native object alive, so its pointer is not reused while the collection holds it.</summary>
    public static IEqualityComparer<Il2CppObjectBase> Identity { get; } = new NativeIdentity();

    private sealed class NativeIdentity : IEqualityComparer<Il2CppObjectBase>
    {
        public bool Equals(Il2CppObjectBase? x, Il2CppObjectBase? y) => Same(x, y);
        public int GetHashCode(Il2CppObjectBase obj) => obj.Pointer.GetHashCode();
    }

    /// <summary>The byte offset of <typeparamref name="T"/>'s field <paramref name="name"/> in its native object, for per-frame reads at
    /// <c>Pointer + offset</c> without the wrapper a generated getter makes. Resolve it once (a static field) and keep it: every
    /// lookup is a call into the game. Throws <see cref="MissingFieldException"/> when the class has no such field.</summary>
    public static int FieldOffset<T>(string name) where T : Il2CppObjectBase
    {
        var field = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<T>.NativeClassPtr, name);
        if (field == IntPtr.Zero) throw new MissingFieldException(typeof(T).Name, name);
        return checked((int)IL2CPP.il2cpp_field_get_offset(field));
    }
}
