using System;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;

namespace ForgeCore;

/// <summary>The Il2Cpp reads <see cref="HookBridge"/> needs. Tests replace this file with a double that needs no running Il2Cpp VM;
/// the repair logic itself is the production source.</summary>
internal static class HookBridgeBackend
{
    /// <summary>The one Il2CppInterop.HarmonySupport build whose generated bridges <see cref="HookBridge"/> was reviewed against
    /// (its module version id). Any other build answers no generator, so a by-reference target is refused rather than repaired blind.</summary>
    private static readonly Guid PinnedSupport = new("7e35e396-35f6-4f3d-8c58-5682870d6b42");

    internal static readonly MethodInfo BoxMethod = typeof(HookBridgeBackend).GetMethod(nameof(Box), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>The pinned build's native-to-managed bridge generator, or null when that exact build is not the one loaded.</summary>
    internal static MethodInfo? Generator()
        => Patcher()?.GetMethod("GenerateNativeToManagedTrampoline", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>The same pinned build's `CopyOriginal`: the copy of a hooked method's interop wrapper that a patched method
    /// runs as its original body; null when that exact build is not the one loaded.</summary>
    internal static MethodInfo? OriginalCopier()
        => Patcher()?.GetMethod("CopyOriginal", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>Why <see cref="Generator"/> answers null, naming the module id the bridge was reviewed against and the ones actually
    /// loaded; null when the pinned build is the one loaded. Written once at startup.</summary>
    internal static string? Mismatch()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "Il2CppInterop.HarmonySupport").ToArray();
        if (loaded.Length == 1 && loaded[0].ManifestModule.ModuleVersionId == PinnedSupport) return null;
        return "Il2CppInterop.HarmonySupport expected module " + PinnedSupport + ", loaded: "
            + (loaded.Length == 0 ? "none" : string.Join(", ", loaded.Select(assembly => assembly.ManifestModule.ModuleVersionId.ToString())))
            + "; by-reference game hooks that need the bridge repair are refused (native-hook-bridge-backend-unavailable).";
    }

    private static Type? Patcher()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "Il2CppInterop.HarmonySupport").ToArray();
        return loaded.Length == 1 && loaded[0].ManifestModule.ModuleVersionId == PinnedSupport
            ? loaded[0].GetType("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher", false) : null;
    }

    /// <summary>`il2cpp_object_unbox`: the address of the struct data inside a boxed object, which is what the native
    /// invoker expects for a by-reference value-type argument.</summary>
    internal static readonly MethodInfo UnboxMethod = typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_object_unbox), BindingFlags.Static | BindingFlags.Public)!;

    /// <summary>The Il2Cpp class of a value type, as the stock generator reads it for a by-value struct argument.</summary>
    internal static IntPtr ClassPointer(Type valueType) => Il2CppClassPointerStore.GetNativeClassPointer(valueType);

    /// <summary>A boxed copy of the struct at <paramref name="data"/>; called from the repaired bridge for every native call.</summary>
    private static IntPtr Box(IntPtr data, long klass) => IL2CPP.il2cpp_value_box(new IntPtr(klass), data);
}
