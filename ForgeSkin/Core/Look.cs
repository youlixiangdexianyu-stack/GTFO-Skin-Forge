using System.Buffers.Binary;
using System.Text;
using ForgeVisual;

namespace ForgeSkin.Format;

// A player's look as it travels to teammates: ForgeSkin's lobby value "look" over ForgeCore Net, one per player, written only by
// that player (PLAN §4.11). Little-endian, in this order:
//   version 1 | flags 1 (0; reserved)
//   character: skin number 4 (0: none, nothing else follows) | content hash 8 | pack name | change count 2 | changes
//   gear count 1 (at most MaxGear), then per gear: gear id 4 | skin number 4 (not 0) | content hash 8 | pack name | wear stage 1 |
//     change count 2 | changes
//   pack name: length 1 (at most MaxPackName) and that many UTF-8 bytes; empty for a pack not from a Thunderstore mod
//   change: kind 1 | slot 2 | name 2, then by kind: param or transform 2 (the value across the catalogue's range as
//     0..65535), effect colour 4 (sRGB RGBA bytes), player colour 3 (sRGB RGB bytes); slot is the code effect's index in the skin
//     (0 for a player colour), name the catalogue index of the parameter, colour or transform entry, or the player colour's index.
// A whole value over MaxBytes, a version this plugin does not know, a count or length past the end, or bytes left over is malformed:
// that player shows the original look everywhere.
public enum LookChange : byte { Param = 0, EffectColor = 1, Transform = 2, PlayerColor = 3 }

public enum LookFit { Fits, Missing, Mismatch, BadValue }

public readonly record struct LookTweak(LookChange Kind, ushort Slot, ushort Name, uint Value)
{
    public static LookTweak Of(int slot, LookChange kind, int name, uint value) => new(kind, (ushort)slot, (ushort)name, value);
    public static int Size(LookChange kind) => kind switch { LookChange.EffectColor => 4, LookChange.PlayerColor => 3, _ => 2 };
}

public class LookSkin
{
    public uint Skin; // SkinNumber of the skin id
    public ulong Hash; // ContentHash of the pack file
    public string Pack = ""; // the Thunderstore mod ("author-name") the pack came in; empty otherwise
    public readonly List<LookTweak> Tweaks = new();
}

public sealed class LookGear : LookSkin
{
    public uint GearId; // PlayerOfflineGearDataBlock persistentID
    public byte Wear; // index of the wear stage reached (MaterialOverride.Wear), 0 without wear
}

public sealed class Look
{
    public const byte Version = 1;
    // MaxBytes is the largest message ForgeCore Net's lobby value carries (NetChannel.DefaultMaxBytes); a code effect and a player
    // colour travel by a 16-bit index, so a skin's first 65536 of each can be changed by its wearer.
    public const int MaxBytes = 64 * 1024, MaxGear = 8, MaxPackName = 64, MaxIndex = ushort.MaxValue + 1;

    public LookSkin? Character;
    public readonly List<LookGear> Gear = new();

    // The skin number: FNV-1a 32 of the skin id's UTF-8.
    public static uint SkinNumber(string skinId) => ForgeCore.Fnv.Hash32(skinId);

    public byte[] Encode()
    {
        if (Gear.Count > MaxGear) throw new InvalidOperationException($"{Gear.Count} gear; at most {MaxGear}");
        var b = new List<byte>(256) { Version, 0 };
        void U32(uint v) { for (int s = 0; s < 32; s += 8) b.Add((byte)(v >> s)); }
        void Skin(LookSkin s)
        {
            for (int i = 0; i < 64; i += 8) b.Add((byte)(s.Hash >> i));
            var name = Encoding.UTF8.GetBytes(s.Pack);
            if (name.Length > MaxPackName) name = Array.Empty<byte>(); // only a hint for teammates: a longer one is left out
            b.Add((byte)name.Length);
            b.AddRange(name);
        }
        void Tweaks(LookSkin s)
        {
            if (s.Tweaks.Count > ushort.MaxValue) throw new InvalidOperationException($"{s.Tweaks.Count} changes");
            b.Add((byte)s.Tweaks.Count); b.Add((byte)(s.Tweaks.Count >> 8));
            foreach (var t in s.Tweaks)
            {
                b.Add((byte)t.Kind); b.Add((byte)t.Slot); b.Add((byte)(t.Slot >> 8)); b.Add((byte)t.Name); b.Add((byte)(t.Name >> 8));
                for (int i = 0; i < LookTweak.Size(t.Kind); i++) b.Add((byte)(t.Value >> (8 * i)));
            }
        }
        U32(Character?.Skin ?? 0);
        if (Character is { Skin: not 0 } c) { Skin(c); Tweaks(c); }
        b.Add((byte)Gear.Count);
        foreach (var g in Gear)
        {
            if (g.Skin == 0) throw new InvalidOperationException($"gear {g.GearId} without a skin");
            U32(g.GearId);
            U32(g.Skin);
            Skin(g);
            b.Add(g.Wear);
            Tweaks(g);
        }
        if (b.Count > MaxBytes) throw new InvalidOperationException($"{b.Count} bytes; at most {MaxBytes}");
        return b.ToArray();
    }

    // Null when malformed (see the layout above).
    public static Look? Decode(byte[] data)
    {
        if (data.Length > MaxBytes || data.Length < 2 || data[0] != Version) return null;
        int at = 2;
        var look = new Look();
        bool Take(int n) => (at += n) <= data.Length;
        bool U32(out uint v)
        {
            v = Take(4) ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at - 4)) : 0;
            return at <= data.Length;
        }
        bool Skin(LookSkin s)
        {
            if (!Take(9)) return false;
            s.Hash = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at - 9));
            int n = data[at - 1];
            if (n > MaxPackName || !Take(n)) return false;
            s.Pack = Encoding.UTF8.GetString(data, at - n, n);
            return true;
        }
        bool Tweaks(LookSkin s)
        {
            if (!Take(2)) return false;
            for (int count = data[at - 2] | data[at - 1] << 8; count > 0; count--)
            {
                if (!Take(5) || data[at - 5] > (byte)LookChange.PlayerColor) return false;
                var kind = (LookChange)data[at - 5];
                ushort slot = (ushort)(data[at - 4] | data[at - 3] << 8), name = (ushort)(data[at - 2] | data[at - 1] << 8);
                int size = LookTweak.Size(kind);
                if (!Take(size)) return false;
                uint v = 0;
                for (int i = 0; i < size; i++) v |= (uint)data[at - size + i] << (8 * i);
                s.Tweaks.Add(new LookTweak(kind, slot, name, v));
            }
            return true;
        }
        if (!U32(out var character)) return null;
        if (character != 0)
        {
            look.Character = new LookSkin { Skin = character };
            if (!Skin(look.Character) || !Tweaks(look.Character)) return null;
        }
        if (!Take(1)) return null;
        int gears = data[at - 1];
        if (gears > MaxGear) return null;
        for (int i = 0; i < gears; i++)
        {
            var g = new LookGear();
            if (!U32(out g.GearId) || !U32(out g.Skin) || g.Skin == 0 || !Skin(g) || !Take(1)) return null;
            g.Wear = data[at - 1];
            if (!Tweaks(g)) return null;
            look.Gear.Add(g);
        }
        return at == data.Length ? look : null;
    }

    // Whether a teammate's `entry` can be shown with the skin installed here under its number (`doc` null: none is), whose pack hashes to
    // `hash`; when it can, `picks` are the teammate's changes (Picks). Anything else shows that gear in its original look.
    public static LookFit Fit(LookSkin entry, SkinDocument? doc, ulong hash, out Dictionary<string, float[]>? picks, out string? why)
    {
        picks = null;
        why = null;
        if (doc == null) return LookFit.Missing;
        if (hash != entry.Hash) return LookFit.Mismatch;
        picks = Picks(doc, entry.Tweaks, out why);
        return picks == null ? LookFit.BadValue : LookFit.Fits;
    }

    // ---- the player's changes of one skin, both ways ----

    static byte Byte(float v) => (byte)MathF.Round(Math.Clamp(float.IsFinite(v) ? v : 0, 0, 1) * 255);
    static uint Rgb(float[] c) => (uint)(Byte(c[0]) | Byte(c[1]) << 8 | Byte(c[2]) << 16);
    static uint Rgba(float[] c) => Rgb(c) | (uint)Byte(c[3]) << 24;
    static float[] Unpack(uint v, int channels) { var c = new float[channels]; for (int i = 0; i < channels; i++) c[i] = (v >> (8 * i) & 255) / 255f; return c; }
    static uint Q16(ProceduralParam p, float v) => p.Max > p.Min ? (uint)MathF.Round((p.Clamp(v) - p.Min) / (p.Max - p.Min) * 65535) : 0;
    static float UnQ16(ProceduralParam p, uint q) => p.Max > p.Min ? p.Min + q / 65535f * (p.Max - p.Min) : p.Min;

    // What the player changed on `doc` (`picks`: player colour id or CodeEffects.TweakKey -> value, as in ForgeSkin.colours.json), as
    // changes: only what the author lets the player change, and only where it differs from the author's own value once quantised.
    public static void Tweaks(SkinDocument doc, IReadOnlyDictionary<string, float[]>? picks, List<LookTweak> into)
    {
        into.Clear();
        if (picks == null || picks.Count == 0) return;
        var colours = doc.PlayerColors ?? new();
        for (int i = 0; i < colours.Count && i < MaxIndex; i++)
            if (picks.TryGetValue(colours[i].Id, out var rgb) && rgb.Length == 3 && Rgb(rgb) != Rgb(colours[i].Default))
                into.Add(LookTweak.Of(0, LookChange.PlayerColor, i, Rgb(rgb)));
        var effects = doc.CodeEffects ?? new();
        for (int e = 0; e < effects.Count && e < MaxIndex; e++)
        {
            var fx = effects[e];
            if (fx.PlayerTweaks is not { } allowed || CodeEffects.Catalog.Find(fx.Program) is not { } program) continue;
            foreach (var p in program.Params)
                if (allowed.Params.Contains(p.Name) && picks.TryGetValue(CodeEffects.TweakKey(fx.Id, TweakKind.Param, p.Name), out var v) && v.Length == 1
                    && Q16(p, v[0]) is var q && q != Q16(p, fx.Params.TryGetValue(p.Name, out var own) ? own : p.Default))
                    into.Add(LookTweak.Of(e, LookChange.Param, p.Index, q));
            foreach (var c in program.Colours)
                if (allowed.Colors.Contains(c.Name) && picks.TryGetValue(CodeEffects.TweakKey(fx.Id, TweakKind.Color, c.Name), out var v) && v.Length == 4
                    && Rgba(v) is var q && q != Rgba(fx.Colors.TryGetValue(c.Name, out var own) ? own : c.Default.ToArray()))
                    into.Add(LookTweak.Of(e, LookChange.EffectColor, c.Index, q));
            float[] own4 = { fx.Scale, fx.Rotation, fx.Spin, fx.Intensity };
            foreach (var t in CodeEffects.Catalog.Transform)
                if (allowed.Transform.Contains(t.Name) && picks.TryGetValue(CodeEffects.TweakKey(fx.Id, TweakKind.Transform, t.Name), out var v) && v.Length == 1
                    && Q16(t, v[0]) is var q && q != Q16(t, own4[t.Index]))
                    into.Add(LookTweak.Of(e, LookChange.Transform, t.Index, q));
        }
    }

    // A teammate's changes of `doc` as picks (the same keys as Tweaks reads). Null, with what is wrong, when one names something `doc`
    // has not or does not let the player change: that gear shows the original look.
    public static Dictionary<string, float[]>? Picks(SkinDocument doc, IReadOnlyList<LookTweak> tweaks, out string? bad)
    {
        bad = null;
        var picks = new Dictionary<string, float[]>();
        var colours = doc.PlayerColors ?? new();
        var effects = doc.CodeEffects ?? new();
        foreach (var t in tweaks)
        {
            if (t.Kind == LookChange.PlayerColor)
            {
                if (t.Slot != 0 || t.Name >= colours.Count) { bad = $"player colour {t.Name}"; return null; }
                picks[colours[t.Name].Id] = Unpack(t.Value, 3);
                continue;
            }
            var fx = t.Slot < effects.Count ? effects[t.Slot] : null;
            var program = fx == null ? null : CodeEffects.Catalog.Find(fx.Program);
            if (fx?.PlayerTweaks is not { } allowed || program == null) { bad = $"code effect {t.Slot}"; return null; }
            switch (t.Kind)
            {
                case LookChange.Param when program.Params.FirstOrDefault(p => p.Index == t.Name) is { } p && allowed.Params.Contains(p.Name):
                    picks[CodeEffects.TweakKey(fx.Id, TweakKind.Param, p.Name)] = new[] { UnQ16(p, t.Value) };
                    break;
                case LookChange.EffectColor when program.Colours.FirstOrDefault(c => c.Index == t.Name) is { } c && allowed.Colors.Contains(c.Name):
                    picks[CodeEffects.TweakKey(fx.Id, TweakKind.Color, c.Name)] = Unpack(t.Value, 4);
                    break;
                case LookChange.Transform when t.Name < CodeEffects.Catalog.Transform.Count && CodeEffects.Catalog.Transform[t.Name] is var tr && allowed.Transform.Contains(tr.Name):
                    picks[CodeEffects.TweakKey(fx.Id, TweakKind.Transform, tr.Name)] = new[] { UnQ16(tr, t.Value) };
                    break;
                default:
                    bad = $"code effect {fx.Id} change {t.Kind} {t.Name}";
                    return null;
            }
        }
        return picks;
    }
}
