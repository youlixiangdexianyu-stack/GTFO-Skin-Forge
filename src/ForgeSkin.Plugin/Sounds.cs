using System.Text.Json;
using ForgeAudio;
using ForgeSkin.Format;

namespace ForgeSkin.Plugin;

// Skin sounds through ForgeCore's audio part: the pack's Ogg Vorbis bytes become one audio library per loaded pack, in the
// shared mix (the weapon category, the kit's and the game's volumes), 2D, heard by the local player only; nothing goes
// over the network. A pack loaded again gets a new library; the old one is disposed with its skin.
static class SkinSounds
{
    static int _loads;

    /// <summary>The pack's sounds as a library, each file a cue (<see cref="Cues"/> names them). A file that is no usable Ogg
    /// Vorbis, or longer than the format allows, refuses the whole pack (bad-audio), naming the file.</summary>
    public static AudioLibrary Load(IEnumerable<SkinSound> sounds, Func<string, byte[]> read, Dictionary<string, string> cues)
    {
        var clips = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var settingCues = new Dictionary<string, object>();
        foreach (var s in sounds)
        {
            if (cues.ContainsKey(s.File)) continue;
            var cue = "s" + cues.Count;
            var bytes = read(s.File);
            // Named as the author named it in the editor (else by its file), so they know which one to pick again.
            if (AudioKit.CheckClip(bytes, SkinPack.MaxSoundSeconds) is { } bad)
                throw new SkinFormatException("bad-audio", $"sound {(s.Name.Length > 0 ? s.Name : Path.GetFileNameWithoutExtension(s.File))}: {bad.Detail} Convert it to Ogg Vorbis (adding it in the ForgeSkin editor does) and build the pack again");
            clips[cue + ".ogg"] = bytes;
            settingCues[cue] = new { bus = "skin", place = "2d" };
            cues[s.File] = cue;
        }
        var library = AudioKit.Load("ForgeSkin.pack" + ++_loads, JsonSerializer.Serialize(new { buses = new { skin = new { category = "weapon" } }, cues = settingCues }), clips, SkinPack.MaxSoundSeconds);
        if (library.Error is not { } error) return library;
        library.Dispose();
        // Not a clip of the pack (every one passed above): the audio kit itself; which cue is which file goes to the author log.
        ForgeSkinPlugin.Info($"sound cues: {string.Join(", ", cues.Select(c => c.Value + ".ogg = " + c.Key))}; {error.Detail}");
        throw new SkinFormatException("bad-audio", "the skin's sounds could not be loaded, so the skin is not worn");
    }
}

// The skin's sounds of one group (one trigger; for Kill, one enemy): each firing plays one of them, picked with
// chance weight / total, and never the one that played last while the group has another. Until its library has decoded (a
// moment after a pack loads) and for a sound whose decoding failed (logged by the audio kit) the pick plays nothing.
sealed class SoundGroup : ITriggered
{
    readonly AudioCue[] _cues;
    readonly PlayOptions[] _options;
    readonly float[] _weights;
    readonly float _total;
    int _last = -1;

    public SoundGroup(IReadOnlyList<(AudioCue Cue, float Volume, float Weight)> sounds)
    {
        _cues = new AudioCue[sounds.Count];
        _options = new PlayOptions[sounds.Count];
        _weights = new float[sounds.Count];
        for (int i = 0; i < sounds.Count; i++)
        {
            _cues[i] = sounds[i].Cue;
            _options[i] = new PlayOptions { Gain = Math.Clamp(sounds[i].Volume, 0, 1) };
            _weights[i] = sounds[i].Weight;
            _total += _weights[i];
        }
    }

    public void Start(float time)
    {
        int skip = _cues.Length > 1 ? _last : -1;
        float roll = UnityEngine.Random.value * (_total - (skip >= 0 ? _weights[skip] : 0));
        int pick = -1;
        for (int i = 0; i < _cues.Length; i++)
        {
            if (i == skip) continue;
            pick = i;
            if ((roll -= _weights[i]) < 0) break;
        }
        _last = pick;
        if (pick >= 0 && _cues[pick].IsReady) _cues[pick].Play(_options[pick]);
    }

    public void Update(float time) { }
}
