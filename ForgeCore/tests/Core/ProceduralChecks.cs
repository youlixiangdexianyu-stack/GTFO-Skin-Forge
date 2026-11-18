using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ForgeVisual;

namespace ForgeCore.Tests;

/// <summary>The drawing-program catalogue (Visual/Data/fx-programs.json) is the one source of the procedural shader's programs: it parses, every
/// program it names has its function in the shared program source, a broken catalogue is refused, and the shipped shader bundle was built
/// from the sources in Git (the build's source hash).</summary>
internal static class ProceduralChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var visual = Path.Combine(Root(), "Mods", "ForgeCore", "Visual");
        var shaders = Path.Combine(visual, "tools", "shaders");
        var json = File.ReadAllText(Path.Combine(visual, "Data", "fx-programs.json"));
        var programs = File.ReadAllText(Path.Combine(shaders, "ForgeFxPrograms.hlsl"));

        var catalog = ProceduralCatalog.Parse(json);
        check(catalog.Programs.Count > 0 && catalog.Transform.Count == ProceduralCatalog.TransformNames.Length, "procedural: the catalogue parses");
        foreach (var program in catalog.Programs)
            check(Regex.IsMatch(programs, @"float4\s+fx_" + program.Name + @"\s*\(\s*FxIn\s+i\s*\)"), "procedural: program " + program.Name + " has its function in ForgeFxPrograms.hlsl");

        // A deliberately broken catalogue must be refused.
        var first = Regex.Match(json, "\"params\": \\[");
        string Broken(string from, string to) { var at = json.IndexOf(from, StringComparison.Ordinal); return json[..at] + to + json[(at + from.Length)..]; }
        var nine = string.Join(", ", Enumerable.Range(0, 9).Select(i => "{ \"name\": \"p" + i + "\", \"label\": { \"en\": \"a\", \"zh\": \"a\" }, \"unit\": \"x\", \"min\": 0, \"max\": 1, \"step\": 0.1, \"default\": 0 }"));
        foreach (var (label, text) in new[]
                 {
                     ("nine numbers", json[..(first.Index + first.Length)] + nine + "], \"unused\": [" + json[(first.Index + first.Length)..]),
                     ("a default outside its range", Broken("\"default\": 12", "\"default\": 99")),
                     ("a program listed twice", Broken("\"name\": \"scanlines\"", "\"name\": \"flowLines\"")),
                     ("a colour that is no hex", Broken("\"#55E0FFFF\"", "\"cyan\"")),
                     ("another version", Broken("\"version\": \"1.0.0\"", "\"version\": 1")),
                 })
            check(Refused(text), "procedural: a catalogue with " + label + " is refused");

        // The shipped bundle matches its sources (ForgeVisualShaderBuild.cs hashes the shaders, then the toon include, the programs and the
        // catalogue's program names in order: what reaches the shaders, so the catalogue's version, labels and defaults never call for a rebuild).
        var all = new StringBuilder();
        foreach (var name in new[] { "ForgeVisualParticle.shader", "ForgeVisualMarker.shader", "ForgeVisualOutline.shader", "ForgeVisualProcedural.shader",
                     "ForgeVisualToonForward.shader", "ForgeVisualToonDeferred.shader", "ForgeVisualToonOverlay.shader", "ForgeVisualLitCutout.shader", "ForgeToon.hlsl" })
            all.Append(File.ReadAllText(Path.Combine(shaders, name)).Replace("\r\n", "\n"));
        all.Append(programs.Replace("\r\n", "\n")).Append(string.Join("\n", catalog.Programs.Select(p => p.Name)));
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString()))).ToLowerInvariant();
        var generated = File.ReadAllText(Path.Combine(visual, "Native", "ShaderBundle.Generated.cs"));
        check(generated.Contains("\"" + sha + "\"", StringComparison.Ordinal), "procedural: the shader bundle is built from the sources in Git (rerun build_shaders.ps1)");
    }

    private static bool Refused(string json)
    {
        try { ProceduralCatalog.Parse(json); return false; }
        catch (FormatException) { return true; }
    }

    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "Mods", "ForgeCore", "Visual"))) return d.FullName;
        throw new DirectoryNotFoundException("the repository root above " + AppContext.BaseDirectory);
    }
}
