using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;

namespace ForgeAudio;

internal static class ClipUpload
{
    internal static AudioFailure? Failure;

    internal static unsafe void Initialize()
    {
        Failure = null;
        try
        {
            // GetIl2CppMethod[ByToken] can return a nonzero synthetic MethodInfo for a missing
            // method. Enumerate the actual native class instead of trusting interop declarations.
            var klass = Il2CppClassPointerStore<AudioClip>.NativeClassPtr;
            RequireNativeMethod(klass, "Create", 7);
            RequireNativeMethod(klass, "Construct_Internal", 0);
            RequireNativeMethod(klass, "CreateUserSound", 5);
            if (IL2CPP.il2cpp_resolve_icall("UnityEngine.AudioClip::SetData") == IntPtr.Zero)
                throw new MissingMethodException("Native UnityEngine.AudioClip::SetData icall is unavailable.");
        }
        catch (Exception e)
        {
            Failure = new AudioFailure(AudioError.DecodeFailed, "PCM upload unavailable at startup: " + e);
            AudioLog.Report(Failure.Value);
        }
    }

    private static unsafe void RequireNativeMethod(IntPtr klass, string name, uint parameters)
    {
        var iterator = IntPtr.Zero;
        IntPtr method;
        while (klass != IntPtr.Zero && (method = IL2CPP.il2cpp_class_get_methods(klass, ref iterator)) != IntPtr.Zero)
            if (Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method)) == name
                && IL2CPP.il2cpp_method_get_param_count(method) == parameters
                && UnityVersionHandler.Wrap((Il2CppMethodInfo*)method).MethodPointer != IntPtr.Zero) return;
        throw new MissingMethodException("Native AudioClip." + name + "/" + parameters + " is unavailable.");
    }

    internal static void SetData(AudioClip clip, DecodedAudio audio)
    {
        var native = new Il2CppStructArray<float>(audio.Samples);
        // Same explicit-count binding used by the former OrdnanceSound. Count is sample
        // frames (interleaved floats / channels), as in Unity's instance wrapper.
        if (!AudioClip.SetData(clip, native, audio.Frames, 0))
            throw new InvalidOperationException("Unity refused decoded PCM.");
        GC.KeepAlive(native);
    }
}
