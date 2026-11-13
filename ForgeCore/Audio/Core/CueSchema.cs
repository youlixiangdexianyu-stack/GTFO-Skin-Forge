using System.Linq;

namespace ForgeAudio;

/// <summary>How a <c>cues.forge</c> setting is written.</summary>
internal enum FieldKind
{
    /// <summary>A whole number from <see cref="Field.Min"/> to <see cref="Field.Max"/>.</summary>
    Integer,
    /// <summary>A number from <see cref="Field.Min"/> to <see cref="Field.Max"/>.</summary>
    Number,
    /// <summary>A number, or <c>[min, max]</c> of two numbers inside the bounds.</summary>
    Range,
    /// <summary><c>true</c> or <c>false</c>.</summary>
    Flag,
    /// <summary>One of <see cref="Field.Values"/>.</summary>
    Choice,
    /// <summary>A name that must exist elsewhere in the file or folder.</summary>
    Name,
    /// <summary>A list; <see cref="Field.Min"/> and <see cref="Field.Max"/> bound its length.</summary>
    List,
    /// <summary>An object whose own settings are the rows named <c>field.setting</c> of the same table.</summary>
    Group,
}

/// <summary>One setting of <c>cues.forge</c>: its bounds, default and meaning. The parser reads its bounds from these rows and the
/// website manifest is written from them, so the two cannot differ.</summary>
internal sealed record Field(string Name, FieldKind Kind, string Summary, double Min = 0, double Max = 0, object? Default = null,
    string[]? Values = null, string Unit = "", bool Required = false)
{
    /// <summary>The key inside its own object (<c>db</c> for <c>duck.db</c>).</summary>
    internal string Key => Name.Substring(Name.LastIndexOf('.') + 1);
    internal float MinF => (float)Min;
    internal float MaxF => (float)Max;
    internal int MinI => (int)Min;
    internal int MaxI => (int)Max;
}

/// <summary>The settings <c>cues.forge</c> accepts, by the structure that holds them. Rows named <c>a.b</c> are settings of the object
/// <c>a</c>.</summary>
internal static class CueSchema
{
    internal const int MaxClips = 32, MaxLayers = 8;
    /// <summary>The bounds of a distance curve's points, in metres and gain.</summary>
    internal const float CurveMetresMax = 10000, CurveGainMax = 4;
    internal static readonly string[] PlaceValues = { "auto", "2d", "world", "follow" };
    internal static readonly string[] CurveShapes = { "linear", "log" };

    internal static class Bus
    {
        internal static readonly Field Category = new("category", FieldKind.Choice, "The global category the bus belongs to; all mods share each category's voice limit.",
            Values: AudioCategories.Names, Required: true);
        internal static readonly Field Gain = new("gain", FieldKind.Number, "Volume of every cue on the bus, linear. Use either gain or gainDb.", 0, 4, 1f);
        internal static readonly Field GainDb = new("gainDb", FieldKind.Number, "Volume of every cue on the bus, in decibels. Use either gain or gainDb.", -60, 12, Unit: "dB");
        internal static readonly Field Seats = new("seats", FieldKind.Integer, "How many sounds of the bus may play at once from one emitter; the weakest are faded out for a stronger newcomer.",
            1, VoicePolicy.Capacity, 0, Unit: "sounds");
        internal static readonly Field Duck = new("duck", FieldKind.Group, "Lowers other categories while a sound of this bus plays.");
        internal static readonly Field DuckCategories = new("duck.categories", FieldKind.List, "The categories to lower.", 1, AudioCategories.Count,
            Values: AudioCategories.Names, Required: true);
        internal static readonly Field DuckDb = new("duck.db", FieldKind.Number, "How far to lower them.", .5, 48, 6f, Unit: "dB");
        internal static readonly Field[] All = { Category, Gain, GainDb, Seats, Duck, DuckCategories, DuckDb };
    }

    internal static class Cue
    {
        internal static readonly Field BusName = new("bus", FieldKind.Name, "The bus the cue plays on; it must be defined in \"buses\". The bus decides the category.", Required: true);
        internal static readonly Field Layers = new("layers", FieldKind.List, "The layers mixed into the cue. Without it the cue plays the .ogg file of the same name.", 1, MaxLayers);
        internal static readonly Field Gain = new("gain", FieldKind.Range, "Volume, linear; a [min, max] pair lets each play pick between them. Use either gain or gainDb.", 0, 4, 1f);
        internal static readonly Field GainDb = new("gainDb", FieldKind.Range, "Volume in decibels; a [min, max] pair lets each play pick between them.", -60, 12, Unit: "dB");
        internal static readonly Field Pitch = new("pitch", FieldKind.Range, "Playback speed as a factor; a [min, max] pair lets each play pick between them.", .1, 3, 1f);
        internal static readonly Field Loop = new("loop", FieldKind.Flag, "Repeats seamlessly. A loop has exactly one layer with only clips and gain.", Default: false);
        internal static readonly Field Priority = new("priority", FieldKind.Integer, "Higher priority wins when voices run out; 200 and up counts as a warning.", 0, 255, 100);
        internal static readonly Field MaxVoices = new("maxVoices", FieldKind.Integer, "How many plays of the cue may sound at once.", 1, VoicePolicy.LogicalCapacity, 8);
        internal static readonly Field MaxPerEmitter = new("maxPerEmitter", FieldKind.Integer, "How many plays of the cue may sound at once from one emitter; at most maxVoices.",
            1, VoicePolicy.LogicalCapacity, 8);
        internal static readonly Field MinInterval = new("minInterval", FieldKind.Number, "The shortest gap between two plays of the cue.", 0, 60, 0f, Unit: "s");
        internal static readonly Field Environment = new("environment", FieldKind.Flag, "Whether room acoustics (occlusion, room tone) apply.", Default: true);
        internal static readonly Field MaxSeconds = new("maxSeconds", FieldKind.Number, "The longest the cue may last; the mix is cut there and fades out. 0 means no cap.", .05, 60, 0f, Unit: "s");
        internal static readonly Field Place = new("place", FieldKind.Choice, "Where the sound sits: in the listener's head (2d), at a world point (world) or following an object (follow); auto keeps what the caller supplied. A { \"self\", \"other\" } pair sets the local player's own sounds and everyone else's separately.",
            Values: PlaceValues, Default: "auto");
        internal static readonly Field Space = new("space", FieldKind.Group, "How the cue is heard in space; see the space settings.");
        internal static readonly Field[] All = { BusName, Layers, Gain, GainDb, Pitch, Loop, Priority, MaxVoices, MaxPerEmitter, MinInterval, Environment, MaxSeconds, Place, Space };
    }

    internal static class Layer
    {
        internal static readonly Field Clips = new("clips", FieldKind.List, "The .ogg files of the layer; each play picks one.", 1, MaxClips, Required: true);
        internal static readonly Field OffsetMs = new("offsetMs", FieldKind.Number, "Delay before the layer starts.", 0, 60000, 0f, Unit: "ms");
        internal static readonly Field Gain = new("gain", FieldKind.Number, "Layer volume, linear. Use either gain or gainDb.", 0, 4, 1f);
        internal static readonly Field GainDb = new("gainDb", FieldKind.Number, "Layer volume in decibels. Use either gain or gainDb.", -60, 12, Unit: "dB");
        internal static readonly Field Range = new("range", FieldKind.List, "[start, end] seconds of the clip to use.", 0, 600, Unit: "s");
        internal static readonly Field FadeInMs = new("fadeInMs", FieldKind.Number, "Fade-in length.", 0, 10000, 0f, Unit: "ms");
        internal static readonly Field FadeOutMs = new("fadeOutMs", FieldKind.Number, "Fade-out length.", 0, 10000, 0f, Unit: "ms");
        internal static readonly Field Pitch = new("pitch", FieldKind.Range, "Layer playback speed as a factor.", .1, 3, 1f);
        internal static readonly Field Distance = new("distance", FieldKind.Group, "The listener distances inside which the layer plays, so a far layer can replace the near one.");
        internal static readonly Field DistanceMin = new("distance.min", FieldKind.Number, "Nearest distance.", 0, 100000, 0f, Unit: "m");
        internal static readonly Field DistanceMax = new("distance.max", FieldKind.Number, "Farthest distance; unbounded when omitted.", 0, 100000, Unit: "m");
        internal static readonly Field LowpassHz = new("lowpassHz", FieldKind.Number, "Low-pass cutoff; 0 leaves the layer unfiltered.", 50, 20000, 0f, Unit: "Hz");
        internal static readonly Field[] All = { Clips, OffsetMs, Gain, GainDb, Range, FadeInMs, FadeOutMs, Pitch, Distance, DistanceMin, DistanceMax, LowpassHz };
    }

    internal static class Space
    {
        internal static readonly Field Near = new("near", FieldKind.Number, "Distance up to which the sound is at full volume.", .01, 10000, 2f, Unit: "m");
        internal static readonly Field Far = new("far", FieldKind.Number, "Distance beyond which the sound is silent (when cull is on); greater than near.", .01, 10000, 30f, Unit: "m");
        internal static readonly Field Curve = new("curve", FieldKind.Choice, "Loudness by distance: \"linear\", \"log\", or 2 to 32 [metres, gain] points with increasing metres.",
            Values: CurveShapes, Default: "linear");
        internal static readonly Field Blend = new("blend", FieldKind.Number, "0 hears the sound flat (no distance loss, no panning), 1 fully positioned.", 0, 1, 1f);
        internal static readonly Field Spread = new("spread", FieldKind.Number, "How much the sound widens, which reduces panning.", 0, 180, 0f, Unit: "degrees");
        internal static readonly Field Doppler = new("doppler", FieldKind.Number, "How strongly relative motion shifts the pitch.", 0, 5, 0f);
        internal static readonly Field Cull = new("cull", FieldKind.Flag, "Beyond far the sound is silent and takes no voice.", Default: true);
        internal static readonly Field[] All = { Near, Far, Curve, Blend, Spread, Doppler, Cull };
    }

    /// <summary>The names of the top-level settings in <paramref name="fields"/> (rows named <c>a.b</c> belong to <c>a</c>).</summary>
    internal static string[] Names(Field[] fields) => fields.Where(field => !field.Name.Contains('.')).Select(field => field.Name).ToArray();
}
