using System.Text.Json;
using ForgeAudio;
using ForgeSkin.Format;

namespace ForgeSkin.Plugin;

// Skin sounds through ForgeCore's audio part: the pack's Ogg Vorbis bytes become one audio library per loaded pack, in the
// shared mix (the weapon category, the kit's and the game's volumes), 2D, heard by the local player only; nothing goes
// over the network. A pack loaded again gets a new library; the old one is disposed with its skin.
static class SkinSounds
{
    const double MaxSeconds = 10; // the pack format's limit for one sound
    static int _loads;

    /// <summary>The pack's sounds as a library, each file a cue (<see cref="Cues"/> names them). A file that is no usable Ogg
    /// Vorbis, or longer than the format allows, refuses the whole pack (bad-audio).</summary>
    public static AudioLibrary Load(SkinPack pack, IEnumerable<SkinSound> sounds, Dictionary<string, string> cues)
    {
        var clips = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var settingCues = new Dictionary<string, object>();
        foreach (var s in sounds)
        {
            if (cues.ContainsKey(s.File)) continue;
            var cue = "s" + cues.Count;
            clips[cue + ".ogg"] = pack.ReadBytes(s.File);
            settingCues[cue] = new { bus = "skin", place = "2d" };
            cues[s.File] = cue;
        }
        var library = AudioKit.Load("ForgeSkin.pack" + ++_loads, JsonSerializer.Serialize(new { buses = new { skin = new { category = "weapon" } }, cues = settingCues }), clips, MaxSeconds);
        if (library.Error is not { } error) return library;
        library.Dispose();
        throw new SkinFormatException("bad-audio", error.Detail + " (" + string.Join(", ", cues.Select(c => c.Value + ".ogg = " + c.Key)) + ")");
    }
}

// A skin sound on its trigger. Until its library has decoded (a moment after a pack loads) and for a sound whose decoding
// failed (logged by the audio kit) the trigger plays nothing.
sealed class SoundCue : ITriggered
{
    readonly AudioCue _cue;
    readonly PlayOptions _options;

    public SoundCue(AudioCue cue, float volume) { _cue = cue; _options = new PlayOptions { Gain = Math.Clamp(volume, 0, 1) }; }

    public void Start(float time) { if (_cue.IsReady) _cue.Play(_options); }

    public void Update(float time) { }
}
