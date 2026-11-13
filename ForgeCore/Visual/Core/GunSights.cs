using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;

namespace ForgeVisual;

/// <summary>A sight ForgeVisual ships (<c>Data/sights.json</c>, its model in <c>Assets/Sights/</c>): where the eye lines up through it, in its own
/// frame (Unity axes, front +Z, up +Y, origin at the middle of its base, metres, real size), and how much it magnifies (the aim field of view,
/// <see cref="GunHold.SightLookFov"/>).</summary>
public sealed record SightKind(string Id, Vector3 Look, float Magnification);

/// <summary>Which ForgeVisual sight goes on one gun model: the point on the model where the middle of the sight's base stands (the model's own frame,
/// its own size, metres, the export's axes) and the model's own sight parts the sight takes the place of (drawn parts, by name).</summary>
public sealed record GunSightFit(string Sight, Vector3 At, IReadOnlyList<string> Replace);

/// <summary>A package's sights for its gun models (<c>forge-gun-sights</c>, schema 1, <c>models</c> by prefab name, each a <c>sight</c> id from
/// <c>Data/sights.json</c>, <c>at</c> and optional <c>replace</c>). <see cref="GunMountOptions.Sights"/> takes it; the mount stands the sight on the model,
/// takes the replaced parts away, moves the model's <c>SightLook</c> to the sight's look point and aims at the sight's magnification.</summary>
public sealed class GunSights
{
    private readonly Dictionary<string, GunSightFit> _models = new(StringComparer.Ordinal);

    /// <summary>The sights ForgeVisual ships; throws <see cref="FormatException"/> naming what is wrong.</summary>
    public static IReadOnlyDictionary<string, SightKind> ParseKinds(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "forge-sights" || !ForgeCore.FormatVersion.Matches(root))
            throw new FormatException("Not a forge-sights file of schema 1.");
        var kinds = new Dictionary<string, SightKind>(StringComparer.Ordinal);
        foreach (var sight in root.GetProperty("sights").EnumerateObject())
        {
            var magnification = sight.Value.GetProperty("magnification").GetSingle();
            if (!(magnification >= 1f) || !float.IsFinite(magnification)) throw new FormatException("sight " + sight.Name + " magnifies under 1.");
            kinds[sight.Name] = new SightKind(sight.Name, GunHoldRules.Vector(sight.Value.GetProperty("look")), magnification);
        }
        return kinds;
    }

    /// <summary>Reads a package's table against <paramref name="kinds"/>; throws <see cref="FormatException"/> naming what is wrong (an unknown sight among it).</summary>
    public static GunSights Parse(string json, IReadOnlyDictionary<string, SightKind> kinds)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "forge-gun-sights" || !ForgeCore.FormatVersion.Matches(root))
            throw new FormatException("Not a forge-gun-sights file of schema 1.");
        var sights = new GunSights();
        foreach (var model in root.GetProperty("models").EnumerateObject())
        {
            var id = model.Value.GetProperty("sight").GetString() ?? "";
            if (!kinds.ContainsKey(id)) throw new FormatException("gun-sights model " + model.Name + " names the sight " + id + ", which ForgeVisual does not ship.");
            var replace = new List<string>();
            if (model.Value.TryGetProperty("replace", out var parts))
                foreach (var part in parts.EnumerateArray()) replace.Add(part.GetString() ?? throw new FormatException("gun-sights model " + model.Name + " replaces a part with no name."));
            sights._models[model.Name] = new GunSightFit(id, GunHoldRules.Vector(model.Value.GetProperty("at")), replace);
        }
        return sights;
    }

    /// <summary>Reads the table a package embeds as <paramref name="resource"/> against the sights ForgeVisual ships; throws
    /// <see cref="System.IO.InvalidDataException"/> when it is missing.</summary>
    public static GunSights Read(System.Reflection.Assembly assembly, string resource) => Parse(Text(assembly, resource), Kinds);

    private static IReadOnlyDictionary<string, SightKind>? _kinds;

    /// <summary>The sights ForgeVisual ships (<c>Data/sights.json</c>, embedded).</summary>
    public static IReadOnlyDictionary<string, SightKind> Kinds => _kinds ??= ParseKinds(Text(typeof(GunSights).Assembly, "ForgeVisual.Data.sights.json"));

    internal static string Text(System.Reflection.Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new System.IO.InvalidDataException(assembly.GetName().Name + " is built without " + resource + ".");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The sight of <paramref name="model"/> (its prefab name); false when the table has none.</summary>
    public bool TryGet(string model, out GunSightFit fit) => _models.TryGetValue(model, out fit!);
}
