using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeAudio;

internal static class ListenerOutput
{
    private static AudioListener? _owned, _selected;
    private static readonly List<AudioListener> Cached = new();
    private static bool _scanned, _focused;
    private static IntPtr _camera;
    private static bool _duplicateReported;
    internal static bool Present => Active(_selected);

    // Emitter.Gone also catches an object Unity destroyed (its wrapper stays; reading it would throw).
    private static bool Alive(AudioListener? listener) => !ForgeCore.NativeObjects.Gone(listener);
    private static bool Active(AudioListener? listener) => Alive(listener) && listener!.isActiveAndEnabled;
    internal static bool Active(Camera? camera) => !ForgeCore.NativeObjects.Gone(camera) && camera!.isActiveAndEnabled;

    internal static void Refresh(Camera? camera, bool focused, bool levelChanged)
    {
        var cameraId = Active(camera) ? camera!.Pointer : IntPtr.Zero;
        var changed = cameraId != _camera || levelChanged;
        if (changed) { ReleaseOwned(); _camera = cameraId; }
        // Scanned when the camera, the level or the window focus changes, or the chosen listener went away. The game's player camera
        // (FPSLookCamera) has no AudioListener of its own, so the kit's stands in for the whole level; the listeners the game ships
        // (DissonanceSetup, the main menu and free-flight cameras) come with a scene or a camera, which those changes catch.
        if (changed || !_scanned || focused != _focused || _selected is not null && !Active(_selected))
        {
            // Includes disabled components on active objects, so later enabling one is visible without a scan.
            var listeners = Object.FindObjectsOfType(Il2CppType.Of<AudioListener>());
            Cached.Clear();
            for (var i = 0; i < listeners.Length; i++)
                if (listeners[i]?.TryCast<AudioListener>() is { } listener) Cached.Add(listener);
            _scanned = true;
            _focused = focused;
        }

        AudioListener? external = null;
        var otherActive = 0;
        for (var i = 0; i < Cached.Count; i++)
        {
            var listener = Cached[i];
            if (!Active(listener) || Alive(_owned) && listener.Pointer == _owned!.Pointer) continue;
            otherActive++;
            if (external == null || cameraId != IntPtr.Zero && listener.gameObject.Pointer == camera!.gameObject.Pointer)
                external = listener;
        }
        var ownedOnCamera = Alive(_owned) && cameraId != IntPtr.Zero && _owned!.gameObject.Pointer == camera!.gameObject.Pointer;
        var decision = ListenerPolicy.Decide(cameraId != IntPtr.Zero, ownedOnCamera, otherActive);
        if (decision != ListenerDecision.KeepOwned) ReleaseOwned();
        if (decision == ListenerDecision.CreateOwned) _owned = camera!.gameObject.AddComponent(Il2CppType.Of<AudioListener>()).Cast<AudioListener>();
        if (Alive(_owned)) _owned!.enabled = true;
        _selected = external ?? _owned;
        if (otherActive > 1 && !_duplicateReported)
            AudioLog.Report(AudioError.ListenerUnavailable, "Multiple external Unity AudioListeners are active; Kit created none and will not modify foreign components.");
        _duplicateReported = otherActive > 1;
    }

    private static void ReleaseOwned()
    {
        if (Alive(_owned))
        {
            // Destroy is deferred. Disable first so camera swaps never overlap two Kit listeners.
            _owned!.enabled = false;
            Object.Destroy(_owned);
        }
        _owned = null;
    }

    internal static void Shutdown()
    {
        ReleaseOwned(); _selected = null; Cached.Clear(); _scanned = false; _camera = IntPtr.Zero;
        _duplicateReported = false;
        // Background mute is per-source gain. Kit never writes global AudioListener.pause/volume.
    }
}
