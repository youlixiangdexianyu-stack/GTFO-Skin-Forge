using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeVisual;

/// <summary>
/// The profile a model bundle carries as the TextAsset <see cref="AssetName"/> (the format the Forge model pipeline writes, format
/// 1): its prefabs with the surfaces of each renderer, the surfaces (albedo, metallic and normal maps, an emission map and its lit
/// colour) and the standalone textures. A bundle without a profile is an ordinary bundle: its prefabs keep the materials they were
/// built with.
/// </summary>
internal sealed class ModelProfile
{
    internal const string AssetName = "ordnance-profile";
    internal const int Format = 1;

    internal sealed class Surface
    {
        public string key { get; set; } = "";
        public string albedo { get; set; } = "";
        public string metallic { get; set; } = "";
        public string normal { get; set; } = "";
        public string emission { get; set; } = "";
        public float[] emissionColor { get; set; } = Array.Empty<float>();

        internal bool Emissive => emission.Length != 0 && emissionColor.Length == 3;
    }

    internal sealed class RendererEntry
    {
        public string name { get; set; } = "";
        public string[] surfaces { get; set; } = Array.Empty<string>();
    }

    internal sealed class Prefab
    {
        public string name { get; set; } = "";
        public RendererEntry[] renderers { get; set; } = Array.Empty<RendererEntry>();
        public string[] anchors { get; set; } = Array.Empty<string>();
    }

    private sealed class Document
    {
        public int format { get; set; }
        public Prefab[] prefabs { get; set; } = Array.Empty<Prefab>();
        public Surface[] surfaces { get; set; } = Array.Empty<Surface>();
        public string[] textures { get; set; } = Array.Empty<string>();
    }

    private ModelProfile(Document d)
    {
        Prefabs = d.prefabs;
        Surfaces = d.surfaces;
        Textures = d.textures;
    }

    internal IReadOnlyList<Prefab> Prefabs { get; }
    internal IReadOnlyList<Surface> Surfaces { get; }
    internal IReadOnlyList<string> Textures { get; }

    /// <summary>The profile in <paramref name="json"/>, or null with the reason: not JSON, another format, a name missing or
    /// repeated, a renderer naming a surface the profile does not have.</summary>
    internal static ModelProfile? Read(string json, out string? why)
    {
        why = null;
        Document? d;
        try { d = JsonSerializer.Deserialize<Document>(json); }
        catch (JsonException error) { why = "the profile is not valid JSON: " + error.Message; return null; }
        if (d == null) { why = "the profile is empty"; return null; }
        if (d.format != Format) { why = "profile format " + d.format + " is not " + Format + "; rebuild the bundle"; return null; }
        var surfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in d.surfaces)
        {
            if (s == null || s.key.Length == 0) { why = "a surface has no key"; return null; }
            if (!surfaces.Add(s.key)) { why = "surface " + s.key + " is listed twice"; return null; }
            if (s.albedo.Length == 0 || s.metallic.Length == 0) { why = "surface " + s.key + " lacks its albedo or metallic map"; return null; }
        }
        var prefabs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in d.prefabs)
        {
            if (p == null || p.name.Length == 0) { why = "a prefab has no name"; return null; }
            if (VisualNames.CheckItem(p.name) is { } bad) { why = bad; return null; }
            if (!prefabs.Add(p.name)) { why = "prefab " + p.name + " is listed twice"; return null; }
            foreach (var r in p.renderers)
                foreach (var key in r.surfaces)
                    if (!surfaces.Contains(key)) { why = "prefab " + p.name + " renderer " + r.name + " names the unknown surface " + key; return null; }
        }
        var textures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in d.textures)
        {
            if (VisualNames.CheckItem(t) is { } bad) { why = bad; return null; }
            if (!textures.Add(t)) { why = "texture " + t + " is listed twice"; return null; }
        }
        return new ModelProfile(d);
    }
}
