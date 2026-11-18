using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace ForgeVisual;

/// <summary>A name shown to people in English and Chinese.</summary>
public readonly struct ProceduralLabel
{
    public ProceduralLabel(string en, string zh) { En = en; Zh = zh; }
    public string En { get; }
    public string Zh { get; }
}

/// <summary>One number of a program or of the layer transform: its name, label, unit, range, slider step and default.</summary>
public sealed class ProceduralParam
{
    internal ProceduralParam(int index, string name, ProceduralLabel label, string unit, float min, float max, float step, float @default)
    {
        Index = index; Name = name; Label = label; Unit = unit; Min = min; Max = max; Step = step; Default = @default;
    }

    /// <summary>Its place in the program's parameter list (the shader reads parameter i), or in the transform.</summary>
    public int Index { get; }
    public string Name { get; }
    public ProceduralLabel Label { get; }
    /// <summary>A unit word from the catalogue (`deg`, `hz`, `per-s`, `fraction`, `count`, `switch`, ...), for display.</summary>
    public string Unit { get; }
    public float Min { get; }
    public float Max { get; }
    /// <summary>The slider step; 0 is continuous.</summary>
    public float Step { get; }
    public float Default { get; }

    /// <summary><paramref name="value"/> kept inside the range (a non-finite value gives the default).</summary>
    public float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default;
}

/// <summary>One colour slot of a program; <see cref="Default"/> is sRGB red, green, blue, alpha (0 to 1).</summary>
public sealed class ProceduralColour
{
    internal ProceduralColour(int index, string name, ProceduralLabel label, float[] @default) { Index = index; Name = name; Label = label; _default = @default; }

    private readonly float[] _default;
    public int Index { get; }
    public string Name { get; }
    public ProceduralLabel Label { get; }
    public IReadOnlyList<float> Default => _default;
}

/// <summary>One drawing program of the catalogue: <see cref="Index"/> is the number the shader selects it by.</summary>
public sealed class ProceduralProgram
{
    internal ProceduralProgram(int index, string name, ProceduralLabel label, ProceduralParam[] parameters, ProceduralColour[] colours)
    {
        Index = index; Name = name; Label = label; Params = parameters; Colours = colours;
    }

    public int Index { get; }
    public string Name { get; }
    public ProceduralLabel Label { get; }
    public IReadOnlyList<ProceduralParam> Params { get; }
    public IReadOnlyList<ProceduralColour> Colours { get; }

    /// <summary>The parameter called <paramref name="name"/>, or null.</summary>
    public ProceduralParam? Param(string name)
    {
        foreach (var p in Params) if (p.Name == name) return p;
        return null;
    }
}

/// <summary>
/// The catalogue of drawing programs (<c>Data/fx-programs.json</c>, format <c>forge-fx-programs</c> version <c>1.0.0</c>): the one source the shader's
/// dispatch, the runtime, editor panels, the AI guide and package checks read. A program has at most <see cref="MaxParams"/> numbers
/// and <see cref="MaxColours"/> colours; the transform (scale, rotation, spin, intensity) is shared by every layer. <see cref="Parse"/>
/// throws <see cref="FormatException"/> naming the first fault.
/// </summary>
public sealed class ProceduralCatalog
{
    public const string Format = "forge-fx-programs";
    public const string Version = "1.0.0";
    public const int MaxParams = 8, MaxColours = 4;
    /// <summary>The transform's numbers, in the order the shader reads them.</summary>
    public static readonly string[] TransformNames = { "scale", "rotation", "spin", "intensity" };

    private ProceduralCatalog(ProceduralProgram[] programs, ProceduralParam[] transform) { Programs = programs; Transform = transform; }

    public IReadOnlyList<ProceduralProgram> Programs { get; }
    public IReadOnlyList<ProceduralParam> Transform { get; }

    /// <summary>The program called <paramref name="name"/>, or null.</summary>
    public ProceduralProgram? Find(string name)
    {
        foreach (var p in Programs) if (p.Name == name) return p;
        return null;
    }

    public static ProceduralCatalog Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Text(root, "format", "catalogue") != Format) throw new FormatException("the catalogue's format is not " + Format);
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != Version)
            throw new FormatException("the catalogue's version is not " + Version + "; regenerate it from the current Data/fx-programs.json");
        var limits = Get(root, "limits", "catalogue");
        if (Number(limits, "params", "limits") != MaxParams || Number(limits, "colours", "limits") != MaxColours)
            throw new FormatException("the catalogue's limits are not " + MaxParams + " numbers and " + MaxColours + " colours");
        var transform = Params(Get(root, "transform", "catalogue"), "transform", TransformNames.Length);
        for (var i = 0; i < TransformNames.Length; i++)
            if (transform[i].Name != TransformNames[i]) throw new FormatException("transform entry " + i + " is not " + TransformNames[i]);
        var list = Get(root, "programs", "catalogue");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) throw new FormatException("the catalogue lists no programs");
        var programs = new List<ProceduralProgram>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list.EnumerateArray())
        {
            var name = Text(entry, "name", "program " + programs.Count);
            if (!Identifier(name)) throw new FormatException("program name '" + name + "' is not a letter followed by letters and digits");
            if (!names.Add(name)) throw new FormatException("program '" + name + "' is listed twice");
            var where = "program " + name;
            var parameters = Params(Get(entry, "params", where), where, MaxParams);
            var colourList = Get(entry, "colours", where);
            if (colourList.ValueKind != JsonValueKind.Array || colourList.GetArrayLength() == 0 || colourList.GetArrayLength() > MaxColours)
                throw new FormatException(where + " has " + (colourList.ValueKind == JsonValueKind.Array ? colourList.GetArrayLength() : 0) + " colours; 1 to " + MaxColours + " are allowed");
            var colours = new List<ProceduralColour>();
            foreach (var c in colourList.EnumerateArray())
            {
                var colourName = Text(c, "name", where + " colour " + colours.Count);
                colours.Add(new ProceduralColour(colours.Count, colourName, Label(c, where + " colour " + colourName), Colour(Text(c, "default", where + " colour " + colourName), where + " colour " + colourName)));
            }
            programs.Add(new ProceduralProgram(programs.Count, name, Label(entry, where), parameters, colours.ToArray()));
        }
        return new ProceduralCatalog(programs.ToArray(), transform);
    }

    private static ProceduralParam[] Params(JsonElement list, string where, int max)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > max)
            throw new FormatException(where + " has " + (list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0) + " numbers; at most " + max + " are allowed");
        var result = new List<ProceduralParam>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in list.EnumerateArray())
        {
            var name = Text(p, "name", where + " number " + result.Count);
            var at = where + " number " + name;
            if (!Identifier(name) || !names.Add(name)) throw new FormatException(at + " is not a unique name of letters and digits");
            float min = Number(p, "min", at), max2 = Number(p, "max", at), step = Number(p, "step", at), @default = Number(p, "default", at);
            if (!(min < max2)) throw new FormatException(at + ": min is not below max");
            if (step < 0f || step > max2 - min) throw new FormatException(at + ": the step is outside 0 to the range");
            if (@default < min || @default > max2) throw new FormatException(at + ": the default is outside the range");
            result.Add(new ProceduralParam(result.Count, name, Label(p, at), Text(p, "unit", at), min, max2, step, @default));
        }
        return result.ToArray();
    }

    private static bool Identifier(string name)
    {
        static bool Letter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
        if (name.Length == 0 || !Letter(name[0])) return false;
        foreach (var c in name) if (!Letter(c) && c is < '0' or > '9') return false;
        return true;
    }

    private static ProceduralLabel Label(JsonElement e, string where)
    {
        var label = Get(e, "label", where);
        return new ProceduralLabel(Text(label, "en", where + " label"), Text(label, "zh", where + " label"));
    }

    /// <summary>"#RRGGBB" or "#RRGGBBAA", sRGB.</summary>
    internal static float[] Colour(string hex, string where)
    {
        if ((hex.Length != 7 && hex.Length != 9) || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new FormatException(where + ": '" + hex + "' is not #RRGGBB or #RRGGBBAA");
        if (hex.Length == 7) v = (v << 8) | 0xFF;
        return new[] { (v >> 24 & 0xFF) / 255f, (v >> 16 & 0xFF) / 255f, (v >> 8 & 0xFF) / 255f, (v & 0xFF) / 255f };
    }

    private static JsonElement Get(JsonElement e, string name, string where) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : throw new FormatException(where + " has no " + name);

    private static string Text(JsonElement e, string name, string where)
    {
        var v = Get(e, name, where);
        return v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : throw new FormatException(where + ": " + name + " is not a text");
    }

    private static float Number(JsonElement e, string name, string where)
    {
        var v = Get(e, name, where);
        return v.ValueKind == JsonValueKind.Number && float.IsFinite(v.GetSingle()) ? v.GetSingle() : throw new FormatException(where + ": " + name + " is not a number");
    }
}
