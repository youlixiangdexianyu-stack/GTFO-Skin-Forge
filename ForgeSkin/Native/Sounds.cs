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

// The skin's sounds on one trigger: each firing picks one of them, chance = its weight / the total, and plays it. Until a
// pack's library has decoded (a moment after it loads) and for a sound whose decoding failed (logged by the audio kit) the
// trigger plays nothing.
sealed class SoundPickCue : ITriggered
{
    public readonly record struct Pick(AudioCue Cue, PlayOptions Options, float Weight);

    readonly Pick[] _picks;
    readonly float _total;

    public SoundPickCue(Pick[] picks)
    {
        _picks = picks;
        foreach (var p in picks) _total += MathF.Max(0, p.Weight);
    }

    public void Start(float time)
    {
        if (_total <= 0) return;
        float roll = UnityEngine.Random.value * _total;
        for (int i = 0; i < _picks.Length; i++)
        {
            roll -= MathF.Max(0, _picks[i].Weight);
            if (roll >= 0 && i < _picks.Length - 1) continue;
            var pick = _picks[i];
            if (pick.Cue.IsReady) pick.Cue.Play(pick.Options);
            return;
        }
    }

    public void Update(float time) { }
}
