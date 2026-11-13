using System.Diagnostics;

namespace ForgeAudio.Tests;

internal static class DecodeChecks
{
    private static readonly string[] InstalledMods = { "NAinfini-ForgeOrdnance/Carrier", "NAinfini-ForgeOrdnance/Arsenal", "NAinfini-ForgeOrdnance/Frames" };

    internal static void Run(Action<bool, string> check)
    {
        foreach (var channels in new[] { 1, 2 })
        {
            var bytes = Fixture($"tone-{channels}ch.ogg");
            var info = AssetValidation.Ogg(bytes);
            check(info.Success && info.Value.Channels == channels && info.Value.Frames == 4800, $"real {channels}ch Ogg passes container validation");
            var audio = VorbisDecode.Decode(bytes, info.Value, CancellationToken.None);
            check(audio.SampleRate == 48000 && audio.Channels == channels, $"real {channels}ch rate and channels");
            check(audio.Frames == 4800 && audio.Samples.Length == 4800 * channels, $"real {channels}ch exact PCM frames/interleaved count");
            check(audio.Samples.All(float.IsFinite) && audio.Samples.Any(x => Math.Abs(x) > 0.01f), $"real {channels}ch finite non-silent PCM");

            // The final granule is the clip length: decoded frames past it are padding and are dropped.
            var shorter = VorbisDecode.Decode(bytes, info.Value with { Frames = 4800 - 400 }, CancellationToken.None);
            check(shorter.Frames == 4800 - 400, $"real {channels}ch frames past the final granule are dropped");
            var longer = VorbisDecode.Decode(bytes, info.Value with { Frames = 4800 + 400 }, CancellationToken.None);
            check(longer.Frames >= 4800 && longer.Frames <= 4800 + 400, $"real {channels}ch granule slightly past the audio keeps the decoded PCM");
            try { VorbisDecode.Decode(bytes, info.Value with { Frames = 4800 + 2 * VorbisDecode.ToleranceFrames }, CancellationToken.None); check(false, $"real {channels}ch audio far shorter than declared rejected"); }
            catch (InvalidDataException) { check(true, $"real {channels}ch audio far shorter than declared rejected"); }
            try { VorbisDecode.Decode(bytes, info.Value with { Channels = 3 - channels }, CancellationToken.None); check(false, $"real {channels}ch header mismatch rejected"); }
            catch (InvalidDataException) { check(true, $"real {channels}ch header mismatch rejected"); }
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try { VorbisDecode.Decode(bytes, info.Value, cancel.Token); check(false, "decode cancellation observed"); }
            catch (OperationCanceledException) { check(true, "decode cancellation observed"); }
        }
        // A valid identification/EOS envelope is insufficient: real codec headers are required.
        try { VorbisDecode.Decode(new byte[64], new OggInfo(1, 48000, 48000), CancellationToken.None); check(false, "malformed codec rejected"); }
        catch (InvalidDataException) { check(true, "malformed codec rejected"); }
        // A content format's own length limit (clips loaded from memory): the 0.1 s fixture passes at 0.1 s and is refused at 0.05 s.
        var tone = Fixture("tone-1ch.ogg");
        check(AssetValidation.Ogg(tone, 0.1).Success && AssetValidation.Ogg(tone, 0.05) is { Success: false, Error.Code: AudioError.AssetLimit },
            "a clip longer than the caller's limit is refused");

        BrokenSetupFailsAlone(check);
        ShrunkGranuleTerminates(check);
        InstalledAssets(check);
    }

    // NVorbis 0.10.5 alone spins forever in ReadSamples when the final granule is cut by more than one block. Kit's decode
    // runs on the calling thread with no watchdog, so returning at all proves the hang is gone; it must also fail explicitly.
    private static void ShrunkGranuleTerminates(Action<bool, string> check)
    {
        var good = Fixture("tone-long-1ch.ogg");
        var frames = AssetValidation.Ogg(good).Value.Frames;
        foreach (var cut in new[] { VorbisDecode.ToleranceFrames + 1, 40000, 90000 })
        {
            var broken = PatchLastGranule(good, frames - cut);
            var info = AssetValidation.Ogg(broken);
            var clock = Stopwatch.StartNew();
            Exception? error = null;
            // A cut below an earlier page's granule is already refused by the container check.
            if (!info.Success) { check(info.Error.Code == AudioError.UnsupportedCodec, $"granule cut by {cut} frames: container check refuses it"); continue; }
            try { VorbisDecode.Decode(broken, info.Value, CancellationToken.None); }
            catch (Exception e) { error = e; }
            check(error is InvalidDataException && clock.Elapsed < TimeSpan.FromSeconds(5),
                $"granule cut by {cut} frames: same-thread decode returns promptly with an explicit error");
        }
        // Cut within one block: real encoder padding, accepted and trimmed to the granule.
        var padded = PatchLastGranule(good, frames - 1000);
        var trimmed = VorbisDecode.Decode(padded, AssetValidation.Ogg(padded).Value, CancellationToken.None);
        check(trimmed.Frames == frames - 1000, "granule cut within one block trims the clip to the granule");
    }

    // A file whose setup header is destroyed fails on its own; a good file decoded afterwards is unaffected.
    private static void BrokenSetupFailsAlone(Action<bool, string> check)
    {
        var good = Fixture("tone-1ch.ogg");
        var broken = (byte[])good.Clone();
        var secondPage = 27 + good[26] + good.Skip(27).Take(good[26]).Sum(x => x);
        var setupBody = secondPage + 27 + good[secondPage + 26];
        Array.Clear(broken, setupBody, Math.Min(64, broken.Length - setupBody));
        var info = AssetValidation.Ogg(broken);
        Exception? error = null;
        try { VorbisDecode.Decode(broken, info.Value, CancellationToken.None); }
        catch (Exception e) { error = e; }
        check(info.Success && error is InvalidDataException, "corrupt codec setup passes the container check and fails decoding explicitly");
        check(VorbisDecode.Decode(good, AssetValidation.Ogg(good).Value, CancellationToken.None).Frames == 4800, "a good file still decodes after a broken one");
    }

    // Rewrites the final page's granule position and recomputes its Ogg CRC so the container still validates.
    private static byte[] PatchLastGranule(byte[] bytes, long granule)
    {
        var result = (byte[])bytes.Clone();
        var page = result.AsSpan().LastIndexOf("OggS"u8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(page + 6), granule);
        var length = 27 + result[page + 26];
        for (var i = 0; i < result[page + 26]; i++) length += result[page + 27 + i];
        result.AsSpan(page + 22, 4).Clear();
        uint crc = 0;
        for (var i = page; i < page + length; i++)
        {
            crc ^= (uint)result[i] << 24;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(page + 22), crc);
        return result;
    }

    // Needs the installed Forge-Multiplayer profile (or FORGE_AUDIO_PLUGINS); never counted as a pass without it.
    private static void InstalledAssets(Action<bool, string> check)
    {
        var plugins = Environment.GetEnvironmentVariable("FORGE_AUDIO_PLUGINS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "r2modmanPlus-local", "GTFO", "profiles", "Forge-Multiplayer", "BepInEx", "plugins");
        if (!Directory.Exists(plugins)) { Console.WriteLine("installed Ogg decode: needs local resources, not run"); return; }
        foreach (var mod in InstalledMods)
        {
            var folder = Path.Combine(plugins, mod, AudioKitFolder);
            if (!Directory.Exists(folder)) { Console.WriteLine("installed Ogg decode " + mod + ": needs local resources, not run"); continue; }
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            var settings = Path.Combine(folder, CueCatalog.SettingsFile);
            var catalog = CueCatalog.Build(files.Select(file => Path.GetRelativePath(folder, file)), File.Exists(settings) ? File.ReadAllText(settings) : null);
            check(catalog.Error == null && catalog.Failures.Count == 0 && catalog.Cues.Count > 0, "installed folder builds a clean catalog: " + mod);
            var stereo = 0;
            foreach (var file in catalog.Files)
            {
                var bytes = File.ReadAllBytes(Path.Combine(folder, file));
                var info = AssetValidation.Ogg(bytes);
                try
                {
                    var audio = VorbisDecode.Decode(bytes, info.Value, CancellationToken.None);
                    check(audio.Frames == info.Value.Frames, "installed Ogg decodes to its final granule: " + mod + "/" + file);
                    if (audio.Channels == 2) stereo++;
                }
                catch (Exception e) { check(false, "installed Ogg decodes: " + mod + "/" + file + " " + e.Message); }
            }
            Console.WriteLine("installed Ogg decode " + mod + ": " + catalog.Cues.Count + " cues, " + catalog.Files.Length + " files, " + stereo + " stereo");
        }
    }

    private const string AudioKitFolder = "audio";
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
}
