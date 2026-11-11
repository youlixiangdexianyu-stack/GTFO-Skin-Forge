using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SkinForge.Format;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SkinForge.Plugin;

// Skin sounds, the ForgeAudio way: Ogg Vorbis decoded to PCM and played through a Unity AudioSource, not the game's Wwise.
// Only the local player hears them; nothing goes over the network. Unity needs an enabled AudioListener and GTFO (Wwise)
// has none, so one is added to the sound object while no other is active.
static class SoundOut
{
    static GameObject? _root;
    static AudioSource? _source;
    static AudioListener? _listener;

    public static void Play(AudioClip clip, float volume)
    {
        if (_root == null || _root.WasCollected)
        {
            _root = new GameObject("SkinForge Sounds");
            Object.DontDestroyOnLoad(_root);
            // Non-generic AddComponent: the generic form lacks an interop instantiation for some component types in this build.
            _source = _root.AddComponent(Il2CppType.Of<AudioSource>()).Cast<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0; // 2D: the same in both ears wherever the player is
            _listener = null;
        }
        EnsureListener();
        _source!.PlayOneShot(clip, Math.Clamp(volume, 0, 1));
    }

    static void EnsureListener()
    {
        bool other = false;
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<AudioListener>()))
            if (o.TryCast<AudioListener>() is { } l && l.isActiveAndEnabled && (_listener == null || l.Pointer != _listener.Pointer)) other = true;
        if (other) { if (_listener != null) _listener.enabled = false; return; }
        _listener ??= _root!.AddComponent(Il2CppType.Of<AudioListener>()).Cast<AudioListener>();
        _listener.enabled = true;
    }

    public static AudioClip Decode(byte[] ogg, string name)
    {
        float[] samples;
        int channels, rate;
        try { (samples, channels, rate) = Vorbis.Decode(ogg); }
        catch (InvalidDataException e) { throw new SkinFormatException("bad-audio", $"{name}: {e.Message}"); }
        int frames = samples.Length / channels;
        var clip = AudioClip.Create("SkinForge " + name, frames, channels, rate, false);
        clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var native = new Il2CppStructArray<float>(samples);
        // The static binding with an explicit frame count, as ForgeAudio uses; the instance SetData is stripped in this build.
        if (!AudioClip.SetData(clip, native, frames, 0))
        {
            Object.Destroy(clip);
            throw new SkinFormatException("bad-audio", $"{name}: Unity refused the samples");
        }
        return clip;
    }
}

// A skin sound on its trigger.
sealed class SoundCue : ITriggered
{
    readonly AudioClip _clip;
    readonly float _volume;

    public SoundCue(AudioClip clip, float volume) { _clip = clip; _volume = volume; }

    public void Start(float time) => SoundOut.Play(_clip, _volume);

    public void Update(float time) { }
}
