using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ForgeAudio;

/// <summary>What one audio folder offers: playable cue definitions, its buses and, per unavailable cue name, the reason.</summary>
internal sealed class Catalog
{
    internal readonly Dictionary<string, CueDefinition> Cues = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, BusDefinition> Buses = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, AudioFailure> Failures = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A failure of the whole folder (unreadable cues.forge); no cue loads.</summary>
    internal AudioFailure? Error;
    /// <summary>Distinct clip files the playable cues use, in a stable order.</summary>
    internal string[] Files => Cues.Values.SelectMany(cue => cue.Clips).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
    /// <summary>Files a direct cue plays as they are (they become Unity clips).</summary>
    internal HashSet<string> DirectFiles => new(Cues.Values.Where(cue => cue.IsDirect).SelectMany(cue => cue.Clips), StringComparer.OrdinalIgnoreCase);
    /// <summary>Files a mixed cue reads as PCM (they are mixed at load).</summary>
    internal HashSet<string> MixedFiles => new(Cues.Values.Where(cue => !cue.IsDirect).SelectMany(cue => cue.Clips), StringComparer.OrdinalIgnoreCase);
}

/// <summary>The folder convention. <c>cues.forge</c> is <c>{"buses": {...}, "cues": {...}}</c>: buses by name, each mapped to a
/// global category, and cues by name, each on one bus. A cue named like a file (<c>name.ogg</c>, a file in a subfolder is
/// <c>sub/name</c>) plays that file unless it lists its <c>layers</c>; a file used by a layer of another cue is not a cue of its own.
/// A file no cue names is reported, not played: without a bus it has no category.</summary>
internal static class CueCatalog
{
    internal const string SettingsFile = "cues.forge";
    internal const int MaxSettingsBytes = 1024 * 1024;
    private static readonly string[] OtherAudio = { ".wav", ".mp3", ".flac", ".m4a", ".aac", ".opus", ".wma", ".aif", ".aiff" };
    private static readonly string[] CueFields = CueSchema.Names(CueSchema.Cue.All);
    private static readonly string[] LayerFields = CueSchema.Names(CueSchema.Layer.All);
    private static readonly string[] SpaceFields = CueSchema.Names(CueSchema.Space.All);
    private static readonly string[] BusFields = CueSchema.Names(CueSchema.Bus.All);

    internal static string CueName(string relativeFile)
    {
        var file = ForgeCore.PosixPath.ToPosix(relativeFile);
        return file.Substring(0, file.Length - Path.GetExtension(file).Length);
    }

    internal static bool IsOgg(string file) => string.Equals(Path.GetExtension(file), ".ogg", StringComparison.OrdinalIgnoreCase);

    /// <param name="files">Every file in the folder, relative to it.</param>
    /// <param name="settings">The text of cues.forge, or null when the folder has none (the folder then fails: no cue has a bus).</param>
    internal static Catalog Build(IEnumerable<string> files, string? settings)
    {
        var catalog = new Catalog();
        var oggs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unconverted = new List<string>();
        foreach (var raw in files)
        {
            var file = ForgeCore.PosixPath.ToPosix(raw);
            if (IsOgg(file)) oggs[file] = file;
            else if (Array.IndexOf(OtherAudio, Path.GetExtension(file).ToLowerInvariant()) >= 0) unconverted.Add(file);
        }
        var entries = new List<(string Name, JsonElement Value)>();
        JsonDocument? document = null;
        try
        {
            if (settings == null)
                throw new FormatException("the folder has no " + SettingsFile + ": every cue plays on a bus and every bus names its category ("
                    + AudioCategories.Known + "), so the cues must be listed there.");
            {
                if (settings.Length > MaxSettingsBytes) throw new FormatException(SettingsFile + " exceeds 1 MiB.");
                document = JsonDocument.Parse(settings, new JsonDocumentOptions { MaxDepth = 12, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new FormatException(SettingsFile + " must be an object with \"buses\" and \"cues\".");
                foreach (var section in document.RootElement.EnumerateObject())
                {
                    if (section.Name is not ("buses" or "cues")) throw new FormatException(SettingsFile + " has unknown section '" + section.Name + "' (known: buses, cues).");
                    if (section.Value.ValueKind != JsonValueKind.Object) throw new FormatException(SettingsFile + " section '" + section.Name + "' must be an object.");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in section.Value.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(property.Name)) throw new FormatException(SettingsFile + " has an empty " + section.Name + " name.");
                        if (!seen.Add(property.Name)) throw new FormatException(SettingsFile + " names '" + property.Name + "' twice in " + section.Name + ".");
                        if (section.Name == "buses") catalog.Buses[property.Name] = ParseBus(property.Name, property.Value);
                        else entries.Add((property.Name, property.Value));
                    }
                }
            }
            var explicitClips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in entries)
            {
                try
                {
                    var cue = ParseCue(name, value, oggs, catalog.Buses);
                    foreach (var clip in cue.Clips) if (!string.Equals(CueName(clip), name, StringComparison.OrdinalIgnoreCase)) explicitClips.Add(clip);
                    catalog.Cues[name] = cue;
                    catalog.Failures.Remove(name);
                }
                catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException)
                {
                    catalog.Failures[name] = new AudioFailure(AudioError.InvalidSettings, SettingsFile + " cue '" + name + "': " + e.Message);
                }
            }
            foreach (var file in oggs.Keys)
            {
                var name = CueName(file);
                if (explicitClips.Contains(file) || catalog.Cues.ContainsKey(name) || catalog.Failures.ContainsKey(name)) continue;
                catalog.Failures[name] = new AudioFailure(AudioError.InvalidSettings,
                    file + " is not a cue in " + SettingsFile + " and no cue's layer uses it; name it there with its bus (\"" + name + "\": {\"bus\": \"...\"}).");
            }
            foreach (var file in unconverted)
            {
                var name = CueName(file);
                if (catalog.Cues.ContainsKey(name) || catalog.Failures.ContainsKey(name)) continue;
                catalog.Failures[name] = new AudioFailure(AudioError.UnsupportedCodec,
                    file + " is not Ogg Vorbis. Convert the folder: python convert_audio.py convert <source folder> <audio folder>");
            }
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            catalog.Cues.Clear();
            catalog.Error = new AudioFailure(AudioError.InvalidSettings, SettingsFile + ": " + e.Message);
        }
        finally { document?.Dispose(); }
        return catalog;
    }

    private static void CheckFields(JsonElement value, string[] known, string what)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException(what + " must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (Array.IndexOf(known, field.Name) < 0)
                throw new FormatException("unknown " + what + " setting '" + field.Name + "' (known: " + string.Join(", ", known) + ").");
            if (!seen.Add(field.Name)) throw new FormatException(what + " setting '" + field.Name + "' appears twice.");
        }
    }

    private static BusDefinition ParseBus(string name, JsonElement value)
    {
        try
        {
            CheckFields(value, BusFields, "bus");
            var bus = new BusDefinition
            {
                Name = name,
                Category = CategoryOf(value, CueSchema.Bus.Category.Name, "category"),
                Gain = GainOf(value, CueSchema.Bus.Gain, CueSchema.Bus.GainDb, 1),
                Seats = Integer(value, CueSchema.Bus.Seats, 0),
            };
            if (value.TryGetProperty(CueSchema.Bus.Duck.Name, out var duck))
            {
                CheckFields(duck, new[] { "categories", "db" }, "duck");
                var list = CueSchema.Bus.DuckCategories;
                if (!duck.TryGetProperty("categories", out var targets) || targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() < list.MinI || targets.GetArrayLength() > list.MaxI)
                    throw new FormatException("duck.categories must list " + list.MinI + " to " + list.MaxI + " categories (" + AudioCategories.Known + ").");
                bus.Duck = targets.EnumerateArray().Select(t => CategoryOf(t, "duck.categories")).Distinct().ToArray();
                foreach (var category in bus.Duck) bus.DuckMask |= AudioCategories.Bit(category);
                bus.DuckDb = Number(duck, CueSchema.Bus.DuckDb, bus.DuckDb);
            }
            return bus;
        }
        catch (FormatException e) { throw new FormatException("bus '" + name + "': " + e.Message); }
    }

    private static CueDefinition ParseCue(string name, JsonElement value, Dictionary<string, string> oggs, Dictionary<string, BusDefinition> buses)
    {
        CheckFields(value, CueFields, "cue");
        var cue = new CueDefinition { Name = name };
        if (value.TryGetProperty("layers", out var layers))
        {
            var layerRows = CueSchema.Cue.Layers;
            if (layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() < layerRows.MinI || layers.GetArrayLength() > layerRows.MaxI)
                throw new FormatException("layers must list " + layerRows.MinI + " to " + layerRows.MaxI + " layers.");
            cue.Layers = layers.EnumerateArray().Select(layer => ParseLayer(layer, oggs)).ToArray();
        }
        else
        {
            var own = oggs.Keys.FirstOrDefault(file => string.Equals(CueName(file), name, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException("there is no file " + name + ".ogg and no layers list.");
            cue.Layers = new[] { new LayerDefinition { Clips = new[] { own } } };
        }
        (cue.GainMin, cue.Gain) = GainRange(value);
        if (value.TryGetProperty("pitch", out var pitch)) (cue.PitchMin, cue.PitchMax) = PitchRange(pitch);
        cue.Loop = Flag(value, CueSchema.Cue.Loop.Key, cue.Loop);
        cue.Priority = Integer(value, CueSchema.Cue.Priority, cue.Priority);
        cue.MaxVoices = Integer(value, CueSchema.Cue.MaxVoices, cue.MaxVoices);
        cue.MaxPerEmitter = Integer(value, CueSchema.Cue.MaxPerEmitter, cue.MaxVoices, cue.MaxVoices);
        cue.MinInterval = Number(value, CueSchema.Cue.MinInterval, cue.MinInterval);
        cue.Environment = Flag(value, CueSchema.Cue.Environment.Key, cue.Environment);
        cue.MaxSeconds = Number(value, CueSchema.Cue.MaxSeconds, 0);
        if (!value.TryGetProperty(CueSchema.Cue.BusName.Key, out var bus))
            throw new FormatException("a cue needs a bus (the bus names its category: " + AudioCategories.Known + ").");
        var busName = bus.ValueKind == JsonValueKind.String ? bus.GetString()!.Trim() : "";
        if (!buses.ContainsKey(busName)) throw new FormatException("bus '" + busName + "' is not defined in \"buses\".");
        cue.Bus = buses[busName].Name;
        if (value.TryGetProperty("place", out var place))
        {
            if (place.ValueKind == JsonValueKind.String) cue.SelfPlace = cue.OtherPlace = PlaceOf(place);
            else
            {
                CheckFields(place, new[] { "self", "other" }, "place");
                if (place.TryGetProperty("self", out var self)) cue.SelfPlace = PlaceOf(self);
                if (place.TryGetProperty("other", out var other)) cue.OtherPlace = PlaceOf(other);
            }
        }
        if (value.TryGetProperty("space", out var space)) cue.Space = ParseSpace(space);
        if (cue.Loop)
        {
            if (cue.Layers.Length != 1 || cue.Layers[0].OffsetMs != 0 || cue.Layers[0].RangeStart != 0 || cue.Layers[0].RangeEnd >= 0
                || cue.Layers[0].FadeInMs != 0 || cue.Layers[0].FadeOutMs != 0 || cue.Layers[0].PitchMin != 1 || cue.Layers[0].PitchMax != 1
                || cue.Layers[0].DistanceMin != 0 || !float.IsPositiveInfinity(cue.Layers[0].DistanceMax) || cue.Layers[0].LowpassHz != 0)
                throw new FormatException("a loop has exactly one layer with only clips and gain; its sample-exact loop point cannot be shaped.");
            if (cue.MaxSeconds != 0) throw new FormatException("a loop has no maxSeconds.");
        }
        return cue;
    }

    private static LayerDefinition ParseLayer(JsonElement value, Dictionary<string, string> oggs)
    {
        CheckFields(value, LayerFields, "layer");
        var layer = new LayerDefinition();
        var clipRows = CueSchema.Layer.Clips;
        if (!value.TryGetProperty(clipRows.Key, out var clips) || clips.ValueKind != JsonValueKind.Array || clips.GetArrayLength() < clipRows.MinI || clips.GetArrayLength() > clipRows.MaxI)
            throw new FormatException("a layer's clips must list " + clipRows.MinI + " to " + clipRows.MaxI + " .ogg files.");
        var list = new List<string>();
        foreach (var clip in clips.EnumerateArray())
        {
            var file = (clip.ValueKind == JsonValueKind.String ? clip.GetString() : null) is { } clipName
                ? ForgeCore.PosixPath.ToPosix(clipName)
                : throw new FormatException("clips entries must be file names.");
            if (!IsOgg(file) || Path.IsPathRooted(file) || file.Split('/').Contains(".."))
                throw new FormatException("clip '" + file + "' must be a .ogg file inside the audio folder.");
            if (!oggs.TryGetValue(file, out var actual)) throw new FormatException("clip file '" + file + "' was not found.");
            if (list.Contains(actual, StringComparer.OrdinalIgnoreCase)) throw new FormatException("clip '" + file + "' is listed twice.");
            list.Add(actual);
        }
        layer.Clips = list.ToArray();
        layer.OffsetMs = Number(value, CueSchema.Layer.OffsetMs, 0);
        layer.Gain = GainOf(value, CueSchema.Layer.Gain, CueSchema.Layer.GainDb, 1);
        if (value.TryGetProperty(CueSchema.Layer.Range.Key, out var range))
        {
            var bounds = CueSchema.Layer.Range;
            if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2 || range[0].ValueKind != JsonValueKind.Number || range[1].ValueKind != JsonValueKind.Number)
                throw new FormatException("range must be [startSeconds, endSeconds].");
            layer.RangeStart = Range(range[0].GetSingle(), bounds.MinF, bounds.MaxF, "range start");
            layer.RangeEnd = Range(range[1].GetSingle(), layer.RangeStart + .001f, bounds.MaxF, "range end");
        }
        layer.FadeInMs = Number(value, CueSchema.Layer.FadeInMs, 0);
        layer.FadeOutMs = Number(value, CueSchema.Layer.FadeOutMs, 0);
        if (value.TryGetProperty(CueSchema.Layer.Pitch.Key, out var pitch)) (layer.PitchMin, layer.PitchMax) = PitchRange(pitch);
        if (value.TryGetProperty(CueSchema.Layer.Distance.Key, out var distance))
        {
            CheckFields(distance, new[] { "min", "max" }, "distance");
            layer.DistanceMin = Number(distance, CueSchema.Layer.DistanceMin, 0);
            layer.DistanceMax = Number(distance, "max", layer.DistanceMin + .01f, CueSchema.Layer.DistanceMax.MaxF, float.PositiveInfinity);
        }
        layer.LowpassHz = Number(value, CueSchema.Layer.LowpassHz, 0);
        return layer;
    }

    private static SpaceDefinition ParseSpace(JsonElement value)
    {
        CheckFields(value, SpaceFields, "space");
        var space = new SpaceDefinition
        {
            Near = Number(value, CueSchema.Space.Near, 2),
            Far = Number(value, CueSchema.Space.Far, 30),
            Blend = Number(value, CueSchema.Space.Blend, 1),
            Spread = Number(value, CueSchema.Space.Spread, 0),
            Doppler = Number(value, CueSchema.Space.Doppler, 0),
            Cull = Flag(value, CueSchema.Space.Cull.Key, true),
        };
        if (space.Far <= space.Near) throw new FormatException("far must be greater than near.");
        if (value.TryGetProperty(CueSchema.Space.Curve.Key, out var curve))
        {
            if (curve.ValueKind == JsonValueKind.String)
                space.Curve.Shape = curve.GetString() switch
                {
                    "linear" => DistanceCurve.Kind.Linear,
                    "log" => DistanceCurve.Kind.Log,
                    _ => throw new FormatException("curve must be \"linear\", \"log\" or a list of [metres, gain] points."),
                };
            else if (curve.ValueKind == JsonValueKind.Array && curve.GetArrayLength() is >= 2 and <= 32)
            {
                var distances = new List<float>(); var gains = new List<float>();
                foreach (var point in curve.EnumerateArray())
                {
                    if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2 || point[0].ValueKind != JsonValueKind.Number || point[1].ValueKind != JsonValueKind.Number)
                        throw new FormatException("curve points must be [metres, gain] pairs.");
                    var d = Range(point[0].GetSingle(), 0, CueSchema.CurveMetresMax, "curve metres");
                    if (distances.Count > 0 && d <= distances[^1]) throw new FormatException("curve metres must increase.");
                    distances.Add(d); gains.Add(Range(point[1].GetSingle(), 0, CueSchema.CurveGainMax, "curve gain"));
                }
                space.Curve = new DistanceCurve { Shape = DistanceCurve.Kind.Points, Distances = distances.ToArray(), Gains = gains.ToArray() };
            }
            else throw new FormatException("curve must be \"linear\", \"log\" or 2 to 32 [metres, gain] points.");
        }
        return space;
    }

    private static (float, float) PitchRange(JsonElement pitch)
    {
        var (floor, ceiling) = (CueSchema.Cue.Pitch.MinF, CueSchema.Cue.Pitch.MaxF);
        if (pitch.ValueKind == JsonValueKind.Number) { var p = Range(pitch.GetSingle(), floor, ceiling, "pitch"); return (p, p); }
        if (pitch.ValueKind == JsonValueKind.Array && pitch.GetArrayLength() == 2)
        {
            var low = Range(pitch[0].GetSingle(), floor, ceiling, "pitch");
            return (low, Range(pitch[1].GetSingle(), low, ceiling, "pitch"));
        }
        throw new FormatException("pitch must be a number or [min, max].");
    }
    private static AudioCategory CategoryOf(JsonElement owner, string key, string what)
    {
        if (!owner.TryGetProperty(key, out var value)) throw new FormatException("needs a " + what + " (" + AudioCategories.Known + ").");
        return CategoryOf(value, what);
    }
    private static AudioCategory CategoryOf(JsonElement value, string what)
        => value.ValueKind == JsonValueKind.String && AudioCategories.TryParse(value.GetString(), out var category) ? category
            : throw new FormatException(what + " must be one of " + AudioCategories.Known + ".");
    private static Place PlaceOf(JsonElement e)
        => e.ValueKind == JsonValueKind.String ? e.GetString() switch
        {
            "auto" => Place.Auto,
            "2d" => Place.TwoD,
            "world" => Place.World,
            "follow" => Place.Follow,
            _ => throw new FormatException("place must be \"2d\", \"world\", \"follow\" or \"auto\"."),
        } : throw new FormatException("place must be \"2d\", \"world\", \"follow\" or \"auto\".");
    private static float GainOf(JsonElement e, Field linear, Field decibels, float fallback)
    {
        var hasLinear = e.TryGetProperty(linear.Key, out _); var hasDb = e.TryGetProperty(decibels.Key, out _);
        if (hasLinear && hasDb) throw new FormatException(linear.Key + " and " + decibels.Key + " cannot both be set.");
        return hasDb ? MathF.Pow(10, Number(e, decibels, 0) / 20) : Number(e, linear, fallback);
    }
    /// <summary>A cue's volume as a number or a <c>[min, max]</c> range (in <c>gain</c>, linear 0 to 4, or <c>gainDb</c>, -60 to 12 dB):
    /// the quietest and loudest a play may pick.</summary>
    private static (float Min, float Max) GainRange(JsonElement e)
    {
        var hasLinear = e.TryGetProperty("gain", out var linear); var hasDb = e.TryGetProperty("gainDb", out var db);
        if (hasLinear && hasDb) throw new FormatException("gain and gainDb cannot both be set.");
        if (!hasLinear && !hasDb) return (1, 1);
        var row = hasDb ? CueSchema.Cue.GainDb : CueSchema.Cue.Gain;
        var (value, name, low, high) = (hasDb ? db : linear, row.Key, row.MinF, row.MaxF);
        float From(float v) => hasDb ? MathF.Pow(10, v / 20) : v;
        if (value.ValueKind == JsonValueKind.Number) { var g = From(Range(value.GetSingle(), low, high, name)); return (g, g); }
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2 && value[0].ValueKind == JsonValueKind.Number && value[1].ValueKind == JsonValueKind.Number)
        {
            var min = Range(value[0].GetSingle(), low, high, name);
            return (From(min), From(Range(value[1].GetSingle(), min, high, name)));
        }
        throw new FormatException(name + " must be a number or [min, max].");
    }
    private static bool Flag(JsonElement e, string key, bool fallback)
        => !e.TryGetProperty(key, out var v) ? fallback : v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException(key + " must be true or false."),
        };
    private static int Integer(JsonElement e, Field field, int fallback) => Integer(e, field.Key, field.MinI, field.MaxI, fallback);
    private static int Integer(JsonElement e, Field field, int max, int fallback) => Integer(e, field.Key, field.MinI, max, fallback);
    private static float Number(JsonElement e, Field field, float fallback) => Number(e, field.Key, field.MinF, field.MaxF, fallback);
    private static int Integer(JsonElement e, string key, int min, int max, int fallback)
    {
        if (!e.TryGetProperty(key, out var v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max)
            throw new FormatException(key + " must be a whole number from " + min + " to " + max + ".");
        return n;
    }
    private static float Number(JsonElement e, string key, float min, float max, float fallback)
    {
        if (!e.TryGetProperty(key, out var v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number) throw new FormatException(key + " must be a number.");
        return Range(v.GetSingle(), min, max, key);
    }
    private static float Range(float n, float min, float max, string key)
        => float.IsFinite(n) && n >= min && n <= max ? n : throw new FormatException(key + " must be from " + min + " to " + max + ".");
}

internal readonly record struct OggInfo(int Channels, int SampleRate, long Frames)
{
    internal long DecodedBytes => Frames * Channels * sizeof(float);
}

/// <summary>Container checks before any decoding: one Ogg Vorbis stream, mono or stereo, size and length bounds.</summary>
internal static class AssetValidation
{
    internal const int MaxFileBytes = 64 * 1024 * 1024;
    internal const long MaxDecodedBytes = 512L * 1024 * 1024;
    internal const long MaxCompressedCache = 256L * 1024 * 1024;

    /// <param name="bytes">The whole encoded file.</param>
    /// <param name="maxSeconds">The longest clip admitted: an hour, or less where the caller's content format sets a shorter limit.</param>
    internal static Result<OggInfo> Ogg(byte[] bytes, double maxSeconds = 3600)
    {
        if (bytes.Length > MaxFileBytes) return Result<OggInfo>.Fail(AudioError.AssetLimit, "file exceeds 64 MiB.");
        // First Ogg page must contain a Vorbis identification packet; rejects Opus and renamed WAV/MP3.
        if (bytes.Length < 58 || bytes[0] != 'O' || bytes[1] != 'g' || bytes[2] != 'g' || bytes[3] != 'S' || bytes[4] != 0 || bytes[26] < 1)
            return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "not an Ogg file (convert it with convert_audio.py).");
        var packet = 27 + bytes[26];
        if (packet + 30 > bytes.Length || bytes[packet] != 1 || System.Text.Encoding.ASCII.GetString(bytes, packet + 1, 6) != "vorbis")
            return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "Ogg but not Vorbis (Opus is not supported).");
        var channels = bytes[packet + 11]; var rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(packet + 12));
        if (channels is < 1 or > 2 || rate is < 8000 or > 192000 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(packet + 7)) != 0 || bytes[packet + 29] != 1)
            return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "expected mono or stereo Vorbis at 8 to 192 kHz.");
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14));
        int offset = 0; uint sequence = 0; ulong samples = 0; var ended = false;
        while (offset < bytes.Length)
        {
            if (ended || bytes.Length - offset < 27 || bytes[offset] != 'O' || bytes[offset + 1] != 'g' || bytes[offset + 2] != 'g' || bytes[offset + 3] != 'S'
                || bytes[offset + 4] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 14)) != serial
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 18)) != sequence++)
                return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "invalid or chained Ogg pages (one Vorbis stream is required).");
            var flags = bytes[offset + 5];
            if ((offset == 0 && flags != 2 && flags != 6) || (offset != 0 && (flags & 2) != 0))
                return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "invalid Ogg beginning-of-stream flag.");
            var segments = bytes[offset + 26];
            var body = offset + 27 + segments;
            if (body > bytes.Length) return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "truncated Ogg segment table.");
            int length = 0; for (var i = 0; i < segments; i++) length += bytes[offset + 27 + i];
            if (length > bytes.Length - body || offset == 0 && length < 30) return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "truncated Ogg page.");
            var granule = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 6));
            if (granule != ulong.MaxValue)
            {
                if (granule < samples) return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "non-monotonic Vorbis sample positions.");
                samples = granule;
                if (samples > rate * Math.Min(maxSeconds, 3600) || samples > (ulong)MaxDecodedBytes / channels / 4)
                    return Result<OggInfo>.Fail(AudioError.AssetLimit, "longer than " + Math.Min(maxSeconds, 3600).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " s or over 512 MiB of decoded audio.");
            }
            ended = (flags & 4) != 0;
            offset = body + length;
        }
        if (!ended || samples == 0) return Result<OggInfo>.Fail(AudioError.UnsupportedCodec, "Ogg stream is truncated (no final page).");
        return Result<OggInfo>.Ok(new OggInfo(channels, (int)rate, (long)samples));
    }
}
