using System.Globalization;
using System.Text.Json;
using ForgeAudio;

// ForgeAudio.Validate <audio folder>
//     Reads an audio folder exactly as AudioKit.Load does (same catalog rules, container checks, decoder and mixer) and
//     reports every cue that would not play, and each bus with its global category. Exit code 0 only when every cue loads.
// ForgeAudio.Validate render <audio folder> <cue> --out <wav> [--distance <metres>] [--variant <n>] [--self]
//     Writes the mixed preview of one cue exactly as the game would play it at that listener distance (default 0): the layers
//     of the distance stretch the distance falls in, mixed with the same code, scaled by cue gain, bus gain and the distance
//     curve. 16-bit WAV at the mix's rate. Prints one JSON line with the stretch, variant count and length.
if (args.Length >= 1 && args[0] == "render") return Render(args.Skip(1).ToArray());
if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ForgeAudio.Validate <audio folder> | render <audio folder> <cue> --out <wav> [--distance <m>] [--variant <n>]");
    return 2;
}
var folder = Path.GetFullPath(args[0]);
if (!Directory.Exists(folder)) { Console.Error.WriteLine("audio folder not found: " + folder); return 2; }
var catalog = OpenCatalog(folder);
if (catalog.Error is { } error) { Console.Error.WriteLine(error); return 1; }
var problems = catalog.Failures.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
var broken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var pcm = new Dictionary<string, DecodedAudio>(StringComparer.OrdinalIgnoreCase);
long compressed = 0, decoded = 0;
foreach (var file in catalog.Files)
{
    try
    {
        var bytes = File.ReadAllBytes(Path.Combine(folder, file));
        var info = AssetValidation.Ogg(bytes);
        if (!info.Success) { broken[file] = info.Error.ToString(); continue; }
        var audio = VorbisDecode.Decode(bytes, info.Value, CancellationToken.None);
        compressed += bytes.Length; decoded += (long)audio.Samples.Length * sizeof(float);
        pcm[file] = audio;
    }
    catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
    { broken[file] = "DecodeFailed: " + e.Message; }
}
// What the game keeps resident once loaded: the clips of direct cues and every mix (the PCM a mix was made from is freed). A mix that
// would not fit beside them fails its cue, as in the game.
var resident = catalog.Cues.Values.Where(cue => cue.IsDirect).SelectMany(cue => cue.Clips).Distinct(StringComparer.OrdinalIgnoreCase)
    .Where(pcm.ContainsKey).Sum(file => (long)pcm[file].Samples.Length * sizeof(float));
foreach (var cue in catalog.Cues.Values)
{
    if (cue.Clips.FirstOrDefault(broken.ContainsKey) is { } file) { problems[cue.Name] = file + ": " + broken[file]; continue; }
    if (cue.IsDirect) continue;
    try
    {
        foreach (var profile in CueMix.Profiles(cue))
            for (var variant = 0; variant < CueMix.Variants(cue) && !problems.ContainsKey(cue.Name); variant++)
            {
                var mixed = CueMix.Render(cue, profile, variant, name => pcm[name]);
                if (mixed == null) continue;
                if (!CueMix.Fits(resident, mixed, AssetValidation.MaxDecodedBytes)) problems[cue.Name] = "AssetLimit: the mix would exceed the audio cache (512 MiB decoded in total).";
                else resident += (long)mixed.Samples.Length * sizeof(float);
            }
    }
    catch (InvalidDataException e) { problems[cue.Name] = "cannot be mixed: " + e.Message; }
}
if (compressed > AssetValidation.MaxCompressedCache || decoded > AssetValidation.MaxDecodedBytes)
    problems["(folder)"] = "AssetLimit: the folder alone exceeds the shared audio cache (256 MiB compressed / 512 MiB decoded).";
foreach (var problem in problems.OrderBy(x => x.Key)) Console.Error.WriteLine("cue '" + problem.Key + "': " + problem.Value);
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = problems.Count == 0 ? "valid" : "invalid",
    cues = catalog.Cues.Keys.Except(problems.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(),
    buses = catalog.Buses.Values.OrderBy(bus => bus.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(bus => bus.Name, bus => AudioCategories.Name(bus.Category)),
    unavailable = problems.Count,
    files = catalog.Files.Length,
    compressedBytes = compressed,
    decodedBytes = decoded,
}));
return problems.Count == 0 ? 0 : 1;

static Catalog OpenCatalog(string folder)
{
    var settingsPath = Path.Combine(folder, CueCatalog.SettingsFile);
    return CueCatalog.Build(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(folder, file)),
        File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null);
}

static int Render(string[] args)
{
    string? output = null; var distance = 0f; var variant = 0;
    var positional = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out" when i + 1 < args.Length: output = args[++i]; break;
            case "--distance" when i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out distance): i++; break;
            case "--variant" when i + 1 < args.Length && int.TryParse(args[i + 1], out variant): i++; break;
            default: positional.Add(args[i]); break;
        }
    }
    if (positional.Count != 2 || output == null || !float.IsFinite(distance) || distance < 0)
    {
        Console.Error.WriteLine("usage: ForgeAudio.Validate render <audio folder> <cue> --out <wav> [--distance <metres>] [--variant <n>]");
        return 2;
    }
    var folder = Path.GetFullPath(positional[0]);
    if (!Directory.Exists(folder)) { Console.Error.WriteLine("audio folder not found: " + folder); return 2; }
    var catalog = OpenCatalog(folder);
    if (catalog.Error is { } error) { Console.Error.WriteLine(error); return 1; }
    if (catalog.Failures.TryGetValue(positional[1], out var unavailable)) { Console.Error.WriteLine(unavailable); return 1; }
    if (!catalog.Cues.TryGetValue(positional[1], out var cue)) { Console.Error.WriteLine("cue '" + positional[1] + "' does not exist."); return 1; }
    var variants = CueMix.Variants(cue);
    if (variant < 0 || variant >= variants) { Console.Error.WriteLine("variant must be from 0 to " + (variants - 1) + "."); return 1; }
    var profiles = CueMix.Profiles(cue);
    var stretch = CueMix.ProfileAt(profiles, distance);
    try
    {
        var sources = new Dictionary<string, DecodedAudio>(StringComparer.OrdinalIgnoreCase);
        DecodedAudio Source(string file)
        {
            if (sources.TryGetValue(file, out var cached)) return cached;
            var bytes = File.ReadAllBytes(Path.Combine(folder, file));
            var info = AssetValidation.Ogg(bytes);
            if (!info.Success) throw new InvalidDataException(file + ": " + info.Error);
            return sources[file] = VorbisDecode.Decode(bytes, info.Value, CancellationToken.None);
        }
        var mixed = CueMix.Render(cue, profiles[stretch], variant, Source);
        var busGain = catalog.Buses[cue.Bus].Gain;
        var loudness = mixed == null ? 0 : CueMix.Loudness(cue, busGain, distance);
        if (mixed == null || loudness <= 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { audible = false, distance, stretch, stretches = profiles.Length, variants }));
            return 3;
        }
        WriteWave(output, mixed, loudness);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            audible = true,
            distance,
            stretch,
            stretches = profiles.Length,
            variants,
            variant,
            loudness,
            seconds = Math.Round(mixed.Seconds, 6),
            channels = mixed.Channels,
            sampleRate = mixed.SampleRate,
        }));
        return 0;
    }
    catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

static void WriteWave(string path, MixedAudio audio, float gain)
{
    var data = new byte[audio.Samples.Length * 2];
    for (var i = 0; i < audio.Samples.Length; i++)
        BitConverter.TryWriteBytes(data.AsSpan(i * 2), (short)Math.Round(Math.Clamp(audio.Samples[i] * gain, -1f, 1f) * 32767));
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8.ToArray()); writer.Write(36 + data.Length); writer.Write("WAVEfmt "u8.ToArray());
    writer.Write(16); writer.Write((short)1); writer.Write((short)audio.Channels); writer.Write(audio.SampleRate);
    writer.Write(audio.SampleRate * audio.Channels * 2); writer.Write((short)(audio.Channels * 2)); writer.Write((short)16);
    writer.Write("data"u8.ToArray()); writer.Write(data.Length); writer.Write(data);
}
