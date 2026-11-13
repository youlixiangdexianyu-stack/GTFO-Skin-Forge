using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace ForgeUI;

public sealed partial class UIKitMod
{
    /// <summary>
    /// Adds a switch for hit feedback: while it is <see cref="HitFeedback.Active"/>, damage numbers float up from where hits land (hits
    /// on one target close together add up into one number), a marker flashes round the crosshair on a hit (stronger for a weak spot,
    /// larger and red for a kill), a kill leaves a short mark where the target was and a small confirmation beside the crosshair. With
    /// <see cref="HitFeedbackStyle.LocalHits"/> the local player's hits on enemies drive it on their own; <see cref="HitFeedback.Pulse"/>,
    /// <see cref="HitFeedback.Show"/> and <see cref="HitFeedback.ShowAt"/> drive it from your own code. The player can turn all hit
    /// feedback off in ForgeUI's config (<c>HitFeedback / Enabled</c>).
    /// </summary>
    /// <param name="name">Unique and stable inside your mod, for example <c>Cockpit</c>.</param>
    /// <param name="style">Which parts show and how they look; null for the defaults.</param>
    public HitFeedback? AddHitFeedback(string name, HitFeedbackStyle? style = null)
    {
        var id = Full(name, "hit feedback");
        if (id == null) return null;
        style ??= new HitFeedbackStyle();
        var problem = style.Problem();
        if (problem != null) { Plugin.Error("ui-hit-feedback-not-added", id + ": hit feedback not added: " + problem + "."); return null; }
        return HitFeedbacks.Add(new HitFeedback(id, style));
    }
}

/// <summary>What the hit feedback shows and how (<see cref="UIKitMod.AddHitFeedback"/>). Every part is the caller's switch; colours
/// and sizes are the caller's to replace.</summary>
public sealed class HitFeedbackStyle
{
    /// <summary>Damage numbers.</summary>
    public bool Numbers { get; init; } = true;

    /// <summary>The marker round the crosshair.</summary>
    public bool HitMarker { get; init; } = true;

    /// <summary>A short mark where a killed target was.</summary>
    public bool KillMark { get; init; } = true;

    /// <summary>A small cross beside the crosshair on a kill.</summary>
    public bool KillConfirm { get; init; } = true;

    /// <summary>The local player's hits on enemies drive the feedback on their own (numbers where they land, the marker, the kill
    /// mark); off leaves only what your code calls.</summary>
    public bool LocalHits { get; init; } = true;

    /// <summary>Numbers showing at once (1..<see cref="FloatingNumbers.Limit"/>); past it the oldest gives way (counted in the log).</summary>
    public int MaxNumbers { get; init; } = 8;

    /// <summary>Text size of a number, 1080p pixels (8..48).</summary>
    public float NumberPx { get; init; } = 15f;

    /// <summary>How far a number rises over its life (0..200).</summary>
    public float Rise { get; init; } = 26f;

    /// <summary>Seconds a number lives after its last hit (0.2..5).</summary>
    public float NumberSeconds { get; init; } = 0.8f;

    /// <summary>Seconds within which hits on one target add into one number (0..2; 0 never merges).</summary>
    public float MergeSeconds { get; init; } = 0.45f;

    public Color NumberColour { get; init; } = UIKitColors.Cockpit;
    /// <summary>A weak-spot hit: its number (bold) and the marker.</summary>
    public Color CriticalColour { get; init; } = UIKitColors.Amber;
    /// <summary>A kill: the mark, the confirmation, the marker and a kill number.</summary>
    public Color KillColour { get; init; } = UIKitColors.Red;
    /// <summary>The marker on a plain hit.</summary>
    public Color MarkerColour { get; init; } = UIKitColors.Bright;

    internal string? Problem()
    {
        if (MaxNumbers < 1 || MaxNumbers > FloatingNumbers.Limit) return "MaxNumbers must be 1.." + FloatingNumbers.Limit;
        if (!(NumberPx >= 8f && NumberPx <= 48f)) return "NumberPx must be 8..48";
        if (!(Rise >= 0f && Rise <= 200f)) return "Rise must be 0..200";
        if (!(NumberSeconds >= 0.2f && NumberSeconds <= 5f)) return "NumberSeconds must be 0.2..5 seconds";
        if (!(MergeSeconds >= 0f && MergeSeconds <= 2f)) return "MergeSeconds must be 0..2 seconds";
        return null;
    }
}

/// <summary>One mod's hit feedback switch (see <see cref="UIKitMod.AddHitFeedback"/>). Off when added.</summary>
public sealed class HitFeedback
{
    private HitFeedbackStyle _style;
    private int _spread;

    internal HitFeedback(string id, HitFeedbackStyle style) { Id = id; _style = style; }

    /// <summary>The full ID, <c>modId/name</c>.</summary>
    public string Id { get; }

    /// <summary>Feedback shows while any mod's switch is on, in the style of the switch turned on last. Cheap to set every frame.</summary>
    public bool Active
    {
        get => HitFeedbacks.IsOn(this);
        set => HitFeedbacks.SetActive(this, value);
    }

    /// <summary>What shows. A style with a problem is refused (logged) and the old one kept.</summary>
    public HitFeedbackStyle Style
    {
        get => _style;
        set
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var problem = value.Problem();
            if (problem != null) { Plugin.Error("ui-hit-feedback-style-refused", Id + ": hit feedback style refused: " + problem + "."); return; }
            _style = value;
            HitFeedbacks.Restyle(this);
        }
    }

    /// <summary>Flashes the marker round the crosshair now (while this switch is on).</summary>
    public void Pulse(HitKind kind = HitKind.Hit)
    {
        if (Active) HitFeedbacks.Pulse(kind);
    }

    /// <summary>A number beside the crosshair, up and to the right, spread a little so a burst does not stack (while this switch is on).</summary>
    public void Show(float amount, HitKind kind = HitKind.Hit)
    {
        if (!Active) return;
        var n = _spread++;
        HitFeedbacks.Number(0, amount, kind, 36f + n * 13 % 5 * 7f, 14f + n * 7 % 4 * 9f, 0f, false);
    }

    /// <summary>A number rising from a place in the world (while this switch is on); a <see cref="HitKind.Kill"/> also leaves the kill
    /// mark there and the confirmation.</summary>
    public void ShowAt(Vector3 at, float amount, HitKind kind = HitKind.Hit)
    {
        if (!Active) return;
        HitFeedbacks.Number(0, amount, kind, at.x, at.y, at.z, true);
        if (kind == HitKind.Kill) HitFeedbacks.Killed(at);
    }

    /// <summary>Turns the switch off and frees the name.</summary>
    public void Remove() => HitFeedbacks.Remove(this);
}

/// <summary>
/// The hit feedback: the switches, the state (<see cref="HitFeedbackBook"/>, sized to the current style) and the kit's own
/// <see cref="HudRegion.World"/> element that draws it. The local player's hits arrive from <see cref="LocalHits"/>.
/// </summary>
internal static class HitFeedbacks
{
    private static readonly Dictionary<string, HitFeedback> Registered = new(StringComparer.Ordinal);
    private static readonly ScopeSwitches<HitFeedback> Scopes = new();
    private static ConfigEntry<bool>? _enabled;
    private static HitFeedbackView? _view;

    internal static HitFeedbackBook Book { get; private set; } = new(8);

    internal static HitFeedback? Add(HitFeedback feedback)
    {
        if (Registered.ContainsKey(feedback.Id))
        {
            Plugin.Error("ui-hit-feedback-not-added-name-already", feedback.Id + ": hit feedback not added: the name is already taken.");
            return null;
        }
        PatchGroups.Ensure(PatchGroup.LocalHits);
        _enabled ??= ForgeCore.Settings.Bind(Plugin.Id, "HitFeedback", "Enabled", true,
            "Shows the damage numbers, hit marker and kill marks that mods such as Forge Frames switch on. Turn off to hide them in every mod.");
        if (_view == null)
        {
            _view = Hud.Add(ModIds.Join(Plugin.Id, "HitFeedback"), new HitFeedbackView(), new HudSlot(HudRegion.World));
            if (_view == null) return null;
        }
        Registered.Add(feedback.Id, feedback);
        return feedback;
    }

    internal static bool IsOn(HitFeedback feedback) => Scopes.IsOn(feedback);

    internal static void SetActive(HitFeedback feedback, bool on)
    {
        if (on && !Registered.ContainsKey(feedback.Id)) return;
        if (!Scopes.Set(feedback, on)) return;
        var current = Scopes.Current;
        if (current == null) { Book.Clear(); return; }
        Restyle(current);
    }

    /// <summary>The current style changed: the number pool takes its size (made again only when the size changes) and timings.</summary>
    internal static void Restyle(HitFeedback feedback)
    {
        if (!ReferenceEquals(Scopes.Current, feedback)) return;
        var s = feedback.Style;
        if (Book.Numbers.Capacity != s.MaxNumbers) Book = new HitFeedbackBook(s.MaxNumbers);
        Book.Numbers.Seconds = s.NumberSeconds;
        Book.Numbers.MergeSeconds = s.MergeSeconds;
    }

    internal static void Remove(HitFeedback feedback)
    {
        SetActive(feedback, false);
        if (Registered.TryGetValue(feedback.Id, out var current) && ReferenceEquals(current, feedback)) Registered.Remove(feedback.Id);
    }

    /// <summary>The style drawn now; null while no switch is on or the player turned hit feedback off.</summary>
    internal static HitFeedbackStyle? Live => Scopes.Current is { } scope && _enabled is { Value: true } ? scope.Style : null;

    /// <summary>Whether the local player's hits are wanted now.</summary>
    internal static bool WantsLocalHits => Live is { LocalHits: true };

    internal static void Pulse(HitKind kind) => Book.Pulse(kind, ForgeCore.Frame.GameTime, Time.frameCount);

    internal static void Number(long key, float amount, HitKind kind, float x, float y, float z, bool world)
    {
        if (amount <= 0f || float.IsNaN(amount)) return;
        Book.Numbers.Add(key, ForgeCore.Frame.GameTime, amount, kind, x, y, z, world, Time.frameCount);
    }

    internal static void Killed(Vector3 at) => Book.Killed(ForgeCore.Frame.GameTime, at.x, at.y + 0.2f, at.z);

    /// <summary>A hit of the local player on an enemy, as the game applied it: <paramref name="drop"/> health taken at
    /// <paramref name="at"/>, <paramref name="killed"/> when it took the last.</summary>
    internal static void LocalHit(long key, float drop, Vector3 at, bool killed)
    {
        if (!WantsLocalHits) return;
        // The number keeps the hit's colour (a weak spot turns it critical); the kill shows in the marker, the mark and the confirmation.
        Number(key, drop, HitKind.Hit, at.x, at.y, at.z, true);
        Pulse(killed ? HitKind.Kill : HitKind.Hit);
        if (killed) Killed(at);
    }

    /// <summary>The hit just applied on <paramref name="key"/> was on a weak spot (the game's own hit indicator says so).</summary>
    internal static void Critical(long key)
    {
        if (!WantsLocalHits) return;
        Book.Numbers.MarkCritical(key, Time.frameCount);
        Pulse(HitKind.Critical);
    }

    internal static void Clear() => Book.Clear();
}

/// <summary>The kit's own element that draws the hit feedback: numbers, the marker round the crosshair, kill marks and the
/// confirmation. Sizes are HUD units, as the enemy health bars beside them.</summary>
internal sealed class HitFeedbackView : HudWidget
{
    private HudCanvas? _canvas;
    private bool _drawn;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null) return;
        var style = HitFeedbacks.Live;
        if (style == null || hud.HalfWidth <= 0f)
        {
            if (_drawn && _canvas.Begin()) { _canvas.End(); _drawn = false; }
            return;
        }
        var book = HitFeedbacks.Book;
        book.Tick(now);
        if (!_canvas.Begin()) return;
        _drawn = true;
        var world = WorldView.Read(out var clip, out _);
        float hw = hud.HalfWidth, hh = hud.HalfHeight;
        if (style.Numbers)
        {
            var numbers = book.Numbers;
            for (var i = 0; i < numbers.Capacity; i++)
            {
                ref readonly var n = ref numbers[i];
                if (!n.Used) continue;
                float x = n.X, y = n.Y;
                if (n.World)
                {
                    if (!world || !Project(clip, n.X, n.Y, n.Z, hw, hh, out x, out y)) continue;
                    x += 14f;
                    y += 6f;
                }
                numbers.Motion(n, now, out var rise, out var fade);
                var colour = n.Kind == HitKind.Kill ? style.KillColour : n.Kind == HitKind.Critical ? style.CriticalColour : style.NumberColour;
                _canvas.Text(NumberText.Damage(n.Amount), x, y + rise * style.Rise, style.NumberPx,
                    UIKitColors.Faded(colour, 0.9f * (1f - Ease.InCubic(fade))), HudAlign.Left, n.Kind != HitKind.Hit);
            }
        }
        if (style.KillMark && world)
            for (var i = 0; i < HitFeedbackBook.Kills; i++)
            {
                ref var k = ref book.Marks[i];
                if (!k.Used || !Project(clip, k.X, k.Y, k.Z, hw, hh, out var x, out var y)) continue;
                DrawKill(_canvas, x, y, Ease.Clamp01((now - k.Born) / HitFeedbackBook.KillMarkSeconds), style.KillColour);
            }
        if (style.KillConfirm)
        {
            var since = now - book.LastKill;
            if (since < HitFeedbackBook.ConfirmSeconds) DrawConfirm(_canvas, since / HitFeedbackBook.ConfirmSeconds, style.KillColour);
        }
        if (style.HitMarker)
        {
            var t = book.PulseProgress(now);
            if (t >= 0f && t < 1f) DrawMarker(_canvas, book.PulseKind, t, style);
        }
        _canvas.End();
    }

    private static bool Project(in ClipTransform clip, float wx, float wy, float wz, float hw, float hh, out float x, out float y)
    {
        x = y = 0f;
        if (!clip.ToViewport(wx, wy, wz, out var vx, out var vy)) return false;
        if (vx < -0.05f || vx > 1.05f || vy < -0.05f || vy > 1.05f) return false;
        (x, y) = ScreenMath.ViewportToHud(vx, vy, hw, hh);
        return true;
    }

    /// <summary>A small diamond outline that flashes white, turns the kill colour and shrinks away.</summary>
    private static void DrawKill(HudCanvas canvas, float x, float y, float t, Color kill)
    {
        var r = 11f * (1f - 0.6f * Ease.InCubic(t));
        var colour = t < 0.2f ? UIKitColors.Faded(UIKitColors.Bright, 0.9f) : UIKitColors.Faded(kill, 0.75f * (1f - t));
        canvas.Line(x, y + r, x + r, y, 1.5f, colour);
        canvas.Line(x + r, y, x, y - r, 1.5f, colour);
        canvas.Line(x, y - r, x - r, y, 1.5f, colour);
        canvas.Line(x - r, y, x, y + r, 1.5f, colour);
        canvas.Sprite(UIKitSprites.Pixel, x, y, 3f, 3f, colour, 45f);
    }

    /// <summary>A small slanted cross beside the crosshair, up and to the right.</summary>
    private static void DrawConfirm(HudCanvas canvas, float t, Color kill)
    {
        var colour = UIKitColors.Faded(kill, 0.85f * (1f - Ease.InCubic(t)));
        const float x = 26f, y = 14f, r = 4.5f;
        canvas.Line(x - r + 1f, y - r, x + r + 1f, y + r, 1.5f, colour);
        canvas.Line(x - r - 1f, y + r, x + r - 1f, y - r, 1.5f, colour);
    }

    /// <summary>Four diagonal ticks round the crosshair that spread and fade: the marker colour for a hit, the critical colour for a weak
    /// spot, larger and thicker in the kill colour for a kill.</summary>
    private static void DrawMarker(HudCanvas canvas, HitKind kind, float t, HitFeedbackStyle style)
    {
        var kill = kind == HitKind.Kill;
        var colour = kind == HitKind.Critical ? style.CriticalColour : kill ? style.KillColour : style.MarkerColour;
        colour = UIKitColors.Faded(colour, 1f - Ease.InCubic(t));
        var inner = (kill ? 12f : 9f) + Ease.OutCubic(t) * (kill ? 10f : 5f);
        var outer = inner + (kill ? 13f : 9f);
        const float d = 0.70710678f;
        for (var i = 0; i < 4; i++)
        {
            var sx = (i & 1) == 0 ? -1f : 1f;
            var sy = (i & 2) == 0 ? -1f : 1f;
            canvas.Line(sx * inner * d, sy * inner * d, sx * outer * d, sy * outer * d, kill ? 3f : 2f, colour);
        }
    }
}
