using ForgeVisual;

namespace ForgeSkin.Format;

public enum CodeEffectBlend { Alpha, Additive }

// A code effect: one of ForgeCore's drawing programs (ForgeCore Visual/Data/fx-programs.json, read through CodeEffects.Catalog), drawn
// live by the game, never baked. On a material (Material set): painted into a copy of that material's Channel texture by its UV,
// over the material's own texture and its dynamic layers. On an attachment (Attachment set): drawn on that attachment's model.
// Params and Colors are keyed by the program's names; a missing one is the program's default. Colours are sRGB RGBA 0..1 (alpha is
// coverage). Ranges come from the catalogue; SkinPack.Validate rejects what is outside them.
public sealed class CodeEffect
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;

    public string? Material { get; set; }
    public TextureChannel Channel { get; set; } = TextureChannel.Albedo; // Albedo or Emissive; Material only
    public string? Attachment { get; set; }

    public string Program { get; set; } = "flowLines";
    public Dictionary<string, float> Params { get; set; } = new();
    public Dictionary<string, float[]> Colors { get; set; } = new();
    // The catalogue's transform: size against the UV, degrees turned, degrees a second it keeps turning, colour multiplier.
    public float Scale { get; set; } = 1;
    public float Rotation { get; set; }
    public float Spin { get; set; }
    public float Intensity { get; set; } = 1;
    public CodeEffectBlend Blend { get; set; }

    // Always: drawn all the time. Any other trigger: drawn for Seconds after the event (a repeat restarts it), its time starting at 0.
    public Trigger Trigger { get; set; } = Trigger.Always;
    public float Seconds { get; set; } = 1;
    public float LowAmmoFraction { get; set; } = 0.2f;

    // What the player may change in game (the skin picker's adjust page); null: nothing.
    public PlayerTweaks? PlayerTweaks { get; set; }
}

// Names of the program's parameters, its colour slots and the transform entries ("scale", "rotation", "spin", "intensity") the player
// may change.
public sealed class PlayerTweaks
{
    public List<string> Params { get; set; } = new();
    public List<string> Colors { get; set; } = new();
    public List<string> Transform { get; set; } = new();
}

public enum TweakKind { Param, Color, Transform }

// The values a code effect draws with, in the catalogue's order: what the shader reads (ProceduralLook in game, the editor's preview).
public sealed class CodeEffectValues
{
    public ProceduralProgram Program = null!;
    public readonly float[] Params = new float[ProceduralCatalog.MaxParams];
    public readonly float[][] Colors = { new float[4], new float[4], new float[4], new float[4] }; // sRGB RGBA; unused slots 0
    public readonly float[] Transform = new float[4]; // scale, rotation, spin, intensity
}

public static class CodeEffects
{
    // At most this many code effects in one skin (each paints every frame while its gear is in hand).
    public const int MaxPerSkin = 8;

    const string CatalogResource = "ForgeVisual.Data.fx-programs.json";
    static ProceduralCatalog? _catalog;

    // ForgeCore's catalogue: the copy embedded beside ProceduralCatalog (ForgeCore.dll in game; the backend embeds the same file). The
    // plugin hands over ForgeCore's own instance (Procedural.Catalog) so the game reads it once.
    public static ProceduralCatalog Catalog
    {
        get
        {
            if (_catalog != null) return _catalog;
            using var stream = typeof(ProceduralCatalog).Assembly.GetManifestResourceStream(CatalogResource)
                ?? throw new InvalidDataException("built without ForgeCore's Visual/Data/fx-programs.json");
            using var reader = new StreamReader(stream);
            return _catalog = ProceduralCatalog.Parse(reader.ReadToEnd());
        }
        set => _catalog = value;
    }

    // The key of a player's change in ForgeSkin.colours.json (beside the player colours of the same skin).
    public static string TweakKey(string effect, TweakKind kind, string name) => $"fx:{effect}/{kind.ToString().ToLowerInvariant()}/{name}";

    // `e`'s values with the player's `tweaks` (TweakKey -> value: one number, or sRGB RGBA) over the document's, for what PlayerTweaks
    // allows only; everything kept in its range. `e` must have passed Validate.
    public static void Resolve(CodeEffect e, IReadOnlyDictionary<string, float[]>? tweaks, CodeEffectValues into)
    {
        var program = Catalog.Find(e.Program) ?? throw new SkinFormatException("bad-fx-program", $"code effect {e.Id}: no program {e.Program}");
        into.Program = program;
        float[]? Tweak(TweakKind kind, string name, List<string>? allowed, int length) =>
            tweaks != null && allowed?.Contains(name) == true && tweaks.TryGetValue(TweakKey(e.Id, kind, name), out var v) && v.Length == length ? v : null;
        Array.Clear(into.Params);
        foreach (var p in program.Params)
        {
            var v = Tweak(TweakKind.Param, p.Name, e.PlayerTweaks?.Params, 1)?[0] ?? (e.Params.TryGetValue(p.Name, out var own) ? own : p.Default);
            into.Params[p.Index] = p.Clamp(v);
        }
        foreach (var c in into.Colors) Array.Clear(c);
        foreach (var c in program.Colours)
        {
            var v = Tweak(TweakKind.Color, c.Name, e.PlayerTweaks?.Colors, 4) ?? (e.Colors.TryGetValue(c.Name, out var own) ? own : c.Default.ToArray());
            for (int i = 0; i < 4; i++) into.Colors[c.Index][i] = float.IsFinite(v[i]) ? Math.Clamp(v[i], 0, 1) : c.Default[i];
        }
        float[] own4 = { e.Scale, e.Rotation, e.Spin, e.Intensity };
        foreach (var t in Catalog.Transform)
            into.Transform[t.Index] = t.Clamp(Tweak(TweakKind.Transform, t.Name, e.PlayerTweaks?.Transform, 1)?[0] ?? own4[t.Index]);
    }

    // Rejects code effects the plugin cannot draw (SkinPack.Validate): an unknown program, parameter or colour slot, a value outside its
    // range, a target the skin does not have, an event trigger without a time, a player change of something the program lacks.
    public static void Validate(SkinDocument doc)
    {
        var list = doc.CodeEffects;
        if (list == null) return;
        if (list.Count > MaxPerSkin) throw new SkinFormatException("too-many-fx", $"{list.Count} code effects; at most {MaxPerSkin} in one skin");
        var ids = new HashSet<string>();
        foreach (var e in list)
        {
            void Bad(string code, string why) => throw new SkinFormatException(code, $"code effect {e.Id}: {why}");
            if (string.IsNullOrWhiteSpace(e.Id) || !ids.Add(e.Id)) Bad("bad-fx-target", "empty or repeated id");
            if ((e.Material == null) == (e.Attachment == null)) Bad("bad-fx-target", "needs exactly one of a material and an attachment");
            if (e.Material != null)
            {
                if (!doc.Materials.Exists(m => m.Material == e.Material)) Bad("bad-fx-target", $"material {e.Material} has no override in this skin");
                if (e.Channel is not (TextureChannel.Albedo or TextureChannel.Emissive)) Bad("bad-fx-target", $"channel {e.Channel}; albedo or emissive");
            }
            if (e.Attachment != null && doc.Attachments.Find(a => a.Id == e.Attachment)?.Mesh == null)
                Bad("bad-fx-target", $"attachment {e.Attachment} is not in this skin or has no model");
            var program = Catalog.Find(e.Program);
            if (program == null) Bad("bad-fx-program", $"no program {e.Program}; the catalogue has {string.Join(", ", Catalog.Programs.Select(p => p.Name))}");
            foreach (var (name, value) in e.Params)
            {
                var p = program!.Param(name);
                if (p == null) Bad("bad-fx-value", $"{e.Program} has no parameter {name}");
                if (!(value >= p!.Min && value <= p.Max)) Bad("bad-fx-value", $"{name} {value} is outside {p.Min} to {p.Max}");
            }
            foreach (var (name, value) in e.Colors)
            {
                if (!program!.Colours.Any(c => c.Name == name)) Bad("bad-fx-value", $"{e.Program} has no colour {name}");
                if (value is not { Length: 4 } || value.Any(x => !(x >= 0 && x <= 1))) Bad("bad-fx-value", $"colour {name}: four numbers 0 to 1 (sRGB red, green, blue, alpha)");
            }
            float[] own = { e.Scale, e.Rotation, e.Spin, e.Intensity };
            foreach (var t in Catalog.Transform)
                if (!(own[t.Index] >= t.Min && own[t.Index] <= t.Max)) Bad("bad-fx-value", $"{t.Name} {own[t.Index]} is outside {t.Min} to {t.Max}");
            if (!Enum.IsDefined(e.Blend)) Bad("bad-fx-value", $"blend {e.Blend}");
            if (e.Trigger != Trigger.Always && !(e.Seconds is > 0 and <= 60) || !(e.LowAmmoFraction is >= 0 and <= 1))
                Bad("bad-trigger", "an event trigger needs seconds above 0 up to 60, and lowAmmoFraction 0 to 1");
            if (e.PlayerTweaks is { } tw)
            {
                foreach (var n in tw.Params) if (program!.Param(n) == null) Bad("bad-fx-tweak", $"player change of parameter {n}, which {e.Program} does not have");
                foreach (var n in tw.Colors) if (!program!.Colours.Any(c => c.Name == n)) Bad("bad-fx-tweak", $"player change of colour {n}, which {e.Program} does not have");
                foreach (var n in tw.Transform) if (Array.IndexOf(ProceduralCatalog.TransformNames, n) < 0) Bad("bad-fx-tweak", $"player change of {n}; the transform has {string.Join(", ", ProceduralCatalog.TransformNames)}");
            }
        }
    }
}
