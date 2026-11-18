using System;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// A power-on message above the crosshair (<see cref="BootMessage"/>: ignition line and brackets, a header decoding over a segmented bar
/// that stalls, a self-test typing on with OK stamps, a message that decodes split, locks with a flash, a burst and a shake, and folds to
/// a dot), or the power-down word (decoding and flickering in brackets in its own colour), played by your clock: <see cref="Play"/> with
/// the seconds since the power-on started, <see cref="PowerDown"/> with the seconds since the power-down started, <see cref="Stop"/> to end it. The beats, colours and words are yours. It shows
/// only while it has something to draw and takes no room otherwise. Make it with <see cref="UIKitMod.AddBootMessage"/>; call
/// <see cref="Warm"/> once ahead (a prewarm) so its pieces and glyphs exist before the first showing.
/// </summary>
public sealed class HudBootMessage : HudWidget
{
    private readonly BootMessage _scene;
    private readonly Func<string, float, float> _measure;
    private HudCanvas? _canvas;
    private float _boot = -1f, _down = -1f;
    private bool _warmed;

    internal HudBootMessage(BootMessageBeats beats, BootMessageLook look)
    {
        _scene = new BootMessage(beats, look);
        _measure = Measure;
        _scene.Measure = _measure;
    }

    public BootMessageBeats Beats => _scene.Beats;

    /// <summary>The words, once per showing or language (every decoding and typing step is made now).</summary>
    public void Prepare(BootMessageWords words) => _scene.Prepare(words);

    /// <summary>The power-on, <paramref name="seconds"/> after it started (it ends by itself after <see cref="BootMessageBeats.Duration"/>).</summary>
    public void Play(float seconds)
    {
        _boot = seconds;
        _down = -1f;
    }

    /// <summary>The power-down, <paramref name="seconds"/> after it started (the word is gone after <see cref="BootMessageBeats.DownEnd"/>).</summary>
    public void PowerDown(float seconds)
    {
        _boot = -1f;
        _down = Math.Max(0f, seconds);
    }

    public void Stop()
    {
        _boot = _down = -1f;
        if (Element.Visible) Element.Visible = false;
    }

    /// <summary>Draws every step of the power-on once, hidden, so its pieces and glyphs exist; false while the game's HUD is not built yet
    /// (call it again later). The words must be prepared first.</summary>
    public bool Warm()
    {
        if (_warmed) return true;
        if (_canvas == null || Element.Root == null) return false;
        for (var s = 0f; s < _scene.Beats.Duration; s += 0.05f)
        {
            _scene.Build(s, -1f);
            Draw(_canvas);
        }
        for (var d = 0f; d < _scene.Beats.DownEnd; d += 0.05f)
        {
            _scene.Build(-1f, d);
            Draw(_canvas);
        }
        _canvas.PrepareText();
        _canvas.Reserve();
        _warmed = true;
        return true;
    }

    private float Measure(string text, float px)
    {
        var width = _canvas != null ? _canvas.TextWidth(text, px) : 0f;
        return width > 0f ? width : BootMessage.TextWidth(text, px);
    }

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
        _warmed = false;
    }

    internal override void Prepare(HudElement hud, float now)
    {
        _scene.Build(_boot, _down);
        var visible = _scene.Count > 0;
        if (hud.Visible != visible) hud.Visible = visible;
        if (!visible) return;
        var slot = hud.Slot;
        if (Math.Abs(slot.Width - _scene.Width) > 0.5f || Math.Abs(slot.Height - _scene.Height) > 0.5f) hud.Resize(_scene.Width, _scene.Height);
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas != null && _scene.Count > 0) Draw(_canvas);
    }

    private void Draw(HudCanvas canvas)
    {
        if (!canvas.Begin()) return;
        for (var n = 0; n < _scene.Count; n++)
        {
            ref var item = ref _scene.Items[n];
            var c = item.Colour;
            var colour = new Color(c.R, c.G, c.B, c.A);
            switch (item.Kind)
            {
                case BootMessageItemKind.Rect: canvas.Rect(item.X, item.Y, item.W, item.H, colour); break;
                case BootMessageItemKind.Cell: canvas.Cell(item.X, item.Y, item.W, item.H, colour); break;
                case BootMessageItemKind.Line: canvas.Line(item.X, item.Y, item.W, item.H, item.T, colour); break;
                case BootMessageItemKind.Plate: canvas.Plate(item.X, item.Y, item.W, item.H, item.T, colour); break;
                default: canvas.Text(item.Text!, item.X, item.Y, item.Px, colour, (HudAlign)item.Align, item.Bold); break;
            }
        }
        canvas.End();
    }
}
