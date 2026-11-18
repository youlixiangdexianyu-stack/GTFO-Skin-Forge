using System;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// A key prompt: the key in a thin box and what it does ("[F] REVIVE"). The key name is read from the player's actual binding
/// once a second, so a rebound key shows correctly. With <see cref="HoldSeconds"/> above zero it is a hold prompt: call
/// <see cref="Hold"/> every frame with whether the key is down and the box fills with the accent colour as the key is held.
/// Make it with <see cref="UIKitMod.AddKeyHint(string, KeyCode, UIText, HudSlot?)"/> or the <see cref="InputAction"/> overload.
/// </summary>
public sealed class HudKeyHint : HudWidget
{
    private const float KeyPx = KeyCap.Px, LabelPx = 16f, Height = KeyCap.Height, Gap = 8f, CapPad = KeyCap.Pad, Reread = 1f;
    private readonly Func<string> _key;
    private HudCanvas? _canvas;
    private readonly ProgressGauge _hold = new();
    private bool _holds;
    private string _keyName = "";
    private float _nextRead;

    internal HudKeyHint(Func<string> key, UIText label)
    {
        _key = key;
        Label = label;
    }

    /// <summary>A white picture drawn left of the key box, tinted like the label; null draws none.</summary>
    public Sprite? Icon { get; set; }

    /// <summary>What the key does, shown after the box.</summary>
    public UIText Label { get; set; }

    /// <summary>How long the key has to be held; 0 makes it a plain prompt.</summary>
    public float HoldSeconds
    {
        get => _holds ? _hold.HoldSeconds : 0f;
        set
        {
            _holds = value > 0f;
            if (_holds) _hold.HoldSeconds = value;
            else _hold.Reset();
        }
    }

    /// <summary>How far the hold has got, 0..1 (0 for a plain prompt).</summary>
    public float Progress => _holds ? _hold.Held : 0f;

    /// <summary>Feed it whether the key is down; call every frame with your frame time. True on the one frame the hold completes.</summary>
    public bool Hold(bool down, float dt) => _holds && _hold.Hold(down, dt, ForgeCore.Frame.GameTime);

    private FamiliarFade? _fade;
    private float _lastTick = float.MinValue;
    private bool _usedPending, _attentionPending;

    /// <summary>Fades the prompt once the player knows it: after <paramref name="idleSeconds"/> shown without use, or shortly after the
    /// first <see cref="Used"/>, it fades to <paramref name="fadedAlpha"/>; holding the key or <see cref="Attention"/> brings it back, and
    /// every time it is shown again the clock starts over.</summary>
    public void FadeWhenFamiliar(float idleSeconds = 30f, float fadedAlpha = 0.3f) => _fade = new FamiliarFade(idleSeconds, fadedAlpha);

    /// <summary>The key was just used.</summary>
    public void Used() => _usedPending = true;

    /// <summary>Full strength again for four seconds (for example after a refusal).</summary>
    public void Attention() => _attentionPending = true;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
        _nextRead = 0f;
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var label = Show(Label);
        if (now >= _nextRead)
        {
            _nextRead = now + Reread;
            var name = _key();
            if (name != _keyName) _keyName = name;
        }
        var cap = Math.Max(Height, TextWidth.Estimate(_keyName, KeyPx) + CapPad * 2f);
        var labelWidth = label.Length > 0 ? TextWidth.Estimate(label, LabelPx) : 0f;
        var icon = Icon != null ? Height + Gap : 0f;
        var total = icon + cap + (labelWidth > 0f ? Gap + labelWidth : 0f);
        var (fx, fy) = HudFlow.Of(hud);
        var start = ScreenMath.Span(fx, total);
        var left = start + icon;
        var centre = ScreenMath.Span(fy, Height) + Height / 2f;
        var capCentre = left + cap / 2f;

        var progress = Progress;
        var held = progress > 0f;
        var alpha = 1f;
        if (_fade != null)
        {
            if (now - _lastTick > 0.25f) _fade.Hidden();
            if (_usedPending) { _usedPending = false; _fade.Used(now); }
            if (_attentionPending) { _attentionPending = false; _fade.Attention(now); }
            alpha = _fade.Update(now, dt, held);
        }
        _lastTick = now;
        if (Icon != null) _canvas.Icon(Icon, start + Height / 2f, centre, Height, Height, UIKitColors.Faded(UIKitColors.Text, alpha));
        KeyCap.Draw(_canvas, capCentre, centre, cap, Height, _keyName, UIKitColors.Faded(held ? UIKitColors.Accent : UIKitColors.Line, alpha),
            UIKitColors.Faded(held && progress > 0.5f ? UIKitColors.OnAccent : UIKitColors.Bright, alpha), progress, UIKitColors.Faded(UIKitColors.Accent, 0.85f * alpha));
        if (labelWidth > 0f) _canvas.Text(label, left + cap + Gap, centre, LabelPx, UIKitColors.Faded(UIKitColors.Text, alpha));
        _canvas.End();
    }
}

/// <summary>
/// The kit's one key cap, the same in a key prompt, an ability slot and an inventory row: the key's name centred in a thin box, the
/// box filled with an accent from the left as far as <c>fill</c> goes (a hold). The outline's colour says the state (the accent while
/// the key does something now, the line grey otherwise). Sizes are 1080p pixels.
/// </summary>
internal static class KeyCap
{
    internal const float Px = 15f, Height = 22f, Pad = 7f;

    /// <summary>The cap's width for <paramref name="key"/> in element units, measured with the font.</summary>
    internal static float Width(HudCanvas canvas, string key) => Math.Max(Height * canvas.Unit, canvas.TextWidth(key, Px) + 2f * Pad * canvas.Unit);

    internal static void Draw(HudCanvas canvas, float cx, float cy, float width, float height, string key, Color line, Color text, float fill, Color fillColour)
    {
        var px = canvas.Unit;
        var left = cx - width / 2f;
        if (fill > 0f) canvas.Rect(left + (width - 2f * px) * fill / 2f + px, cy, (width - 2f * px) * fill, height - 2f * px, fillColour);
        canvas.Rect(cx, cy + height / 2f - 0.5f * px, width, px, line);
        canvas.Rect(cx, cy - height / 2f + 0.5f * px, width, px, line);
        canvas.Rect(left + 0.5f * px, cy, px, height, line);
        canvas.Rect(left + width - 0.5f * px, cy, px, height, line);
        if (key.Length > 0) canvas.Text(key, cx, cy, Px, text, HudAlign.Centre);
    }
}
