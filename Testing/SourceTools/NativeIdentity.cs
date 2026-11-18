using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace ForgeTestInputs;
/// <summary>The one identity check of the static native evidence: the game is its Steam build, GameAssembly.dll, global-metadata.dat and dump.cs (whole-file hashes,
/// these are the game itself); an interop assembly is its <see cref="InteropSignature"/>. Nothing here loads or runs the game.</summary>
internal static class NativeIdentity
{
    internal const string SteamApp = "493520";

    /// <summary>Checks steam.app, steam.build, native.hash, metadata.hash and, when a dump path is given, dump.hash against the spec's buildId, gameAssemblySha256,
    /// metadataSha256 and dumpSha256. The checks keep these ids so negative cases can name them.</summary>
    internal static void CheckGame(JsonElement spec, string gameRoot, string? dump, Action<string, bool, string> check)
    {
        var app = File.ReadAllText(Path.GetFullPath(Path.Combine(gameRoot, "..", "..", "appmanifest_" + SteamApp + ".acf")));
        string Field(string key) => Regex.Match(app, "\"" + key + "\"\\s+\"([^\"]+)\"").Groups[1].Value;
        check("steam.app", Field("appid") == SteamApp, "Steam app ID must be " + SteamApp + ".");
        check("steam.build", Field("buildid") == spec.GetProperty("buildId").GetString(), "Observed build: " + Field("buildid"));
        CheckFile(check, "native.hash", Path.Combine(gameRoot, "GameAssembly.dll"), spec.GetProperty("gameAssemblySha256"));
        CheckFile(check, "metadata.hash", Path.Combine(gameRoot, "GTFO_Data", "il2cpp_data", "Metadata", "global-metadata.dat"), spec.GetProperty("metadataSha256"));
        if (dump != null) CheckFile(check, "dump.hash", dump, spec.GetProperty("dumpSha256"));
    }

    private static void CheckFile(Action<string, bool, string> check, string id, string path, JsonElement expected)
    {
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        check(id, string.Equals(actual, expected.GetString(), StringComparison.OrdinalIgnoreCase), actual);
    }

    /// <summary>Opens the interop assembly a spec row names and checks `interop.&lt;file&gt;` against its signatureSha256. The assembly is the caller's to dispose.</summary>
    internal static AssemblyDefinition OpenInterop(JsonElement row, string bepInEx, Action<string, bool, string> check, string prefix = "", ReaderParameters? reader = null)
    {
        string file = row.GetProperty("file").GetString()!;
        if (string.IsNullOrWhiteSpace(file) || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !file.EndsWith(".dll", StringComparison.Ordinal))
            throw new InvalidDataException("Plain DLL filenames only: " + file);
        var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(bepInEx, "interop", file), reader ?? new ReaderParameters());
        string actual = InteropSignature.Sha256(assembly);
        check(prefix + "interop." + file, string.Equals(actual, row.GetProperty("signatureSha256").GetString(), StringComparison.OrdinalIgnoreCase), actual);
        return assembly;
    }

    /// <summary>Runs <see cref="InteropSignature.DetectionFailure"/> as check `interop.detection`; any real interop assembly proves it, so the small SNet_ASM.dll keeps it quick.</summary>
    internal static void CheckDetection(string bepInEx, Action<string, bool, string> check)
    {
        string? failure = InteropSignature.DetectionFailure(Path.Combine(bepInEx, "interop", "SNet_ASM.dll"));
        check("interop.detection", failure == null, failure ?? "generation noise ignored; renamed method, changed parameter type, removed field and changed body caught");
    }
}
