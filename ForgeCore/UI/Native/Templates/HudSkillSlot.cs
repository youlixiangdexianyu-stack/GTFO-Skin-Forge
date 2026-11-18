using System;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// An ability slot: a square plate with its top left and bottom right corners cut, an icon (or a short label), the ability's name
/// under it in small text and its key inside the plate in the kit's key cap, outlined in the accent colour while the ability can be used and grey and faint while
/// it cools or is used up. Cooling down, the icon dims under a clockwise sweep with the seconds left in the middle; in effect
/// (<see cref="Active"/>), the outline and icon turn orange over a faint orange fill. It flashes white the moment it is ready and red
/// when refused; nothing moves while its state holds. Sizes are 1080p pixels. Make it with <see cref="UIKitMod.AddSkillSlot"/>.
/// </summary>
public sealed class HudSkillSlot : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private readonly Func<float> _remaining;
    private HudCanvas? _canvas;
    private bool _warm, _denyPending, _holds;
    private float _deniedAt = -10f;
    private readonly ProgressGauge _cooldown = new(), _hold = new(), _progress = new();

    internal HudSkillSlot(Func<float> remaining, float cooldown, UIText key)
    {
        _remaining = remaining;
        Cooldown = cooldown;
        Key = key;
    }

    /// <summary>The full cooldown in seconds, which the sweep is measured against.</summary>
    public float Cooldown { get; set; }

    /// <summary>The key shown inside the slot (empty for none), for example <see cref="UIKitKeys.Name"/>.</summary>
    public UIText Key { get; set; }

    /// <summary>The picture in the slot; null shows <see cref="Text"/> instead.</summary>
    public Sprite? Icon { get; set; }

    /// <summary>The ability's name under the slot; empty draws none.</summary>
    public UIText Label { get; set; } = UIText.Empty;

    /// <summary>Short text inside the slot when it has no icon; empty draws none.</summary>
    public UIText Text { get; set; } = UIText.Empty;

    /// <summary>Uses left, read every frame: shown at the lower right, 0 greys the slot out, below 0 shows ∞. Null shows no count.</summary>
    public Func<int>? Count { get; set; }

    /// <summary>The slot's side; default 76.</summary>
    public float Size { get; set; } = 76f;

    /// <summary>The corner cut; default 12.</summary>
    public float Cut { get; set; } = 12f;

    /// <summary>Flashes white once the moment the cooldown ends; default true.</summary>
    public bool ReadyFlash { get; set; } = true;

    /// <summary>The colour in effect (<see cref="Active"/>); default orange.</summary>
    public Color ActiveColour { get; set; } = UIKitColors.Orange;

    /// <summary>The fill's opacity while <see cref="Active"/>; default 0.10.</summary>
    public float ActiveFill { get; set; } = 0.10f;

    /// <summary>While true the slot is drawn in <see cref="ActiveColour"/> (the ability is running: a charge, a deployed stance).</summary>
    public bool Active { get; set; }

    /// <summary>A thin bar under the slot, 0..1, read every frame (for example deploying); negative draws none. Null draws none.</summary>
    public Func<float>? Progress { get; set; }

    /// <summary>Small text to the right of the slot (for example "RATE ×2"); empty draws none.</summary>
    public UIText Note { get; set; } = UIText.Empty;

    /// <summary>How long the key has to be held; 0 (the default) for none. Drive it with <see cref="Hold"/>.</summary>
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

    /// <summary>Feed it whether the key is down every frame with your frame time; the slot fills as it is held. True on the one frame
    /// the hold completes.</summary>
    public bool Hold(bool down, float dt) => _holds && _hold.Hold(down, dt, ForgeCore.Frame.GameTime);

    /// <summary>A short red flash: the ability was refused.</summary>
    public void Deny() => _denyPending = true;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var key = Show(Key);
        var label = Show(Label);
        var inside = Show(Text);
        var note = Show(Note);
        if (!_warm) _warm = UIKitSprites.WarmPies(4);
        var remaining = Math.Max(0f, _remaining());
        var count = Count != null ? Count() : 1;
        var cooling = remaining > 0f;
        var empty = count == 0;
        var ready = !cooling && !empty;
        if (_denyPending) { _denyPending = false; _deniedAt = now; }
        _cooldown.Cooldown(remaining, Cooldown, now);
        var size = Math.Max(16f, Size) * u;
        var cut = Math.Max(0f, Cut) * u;
        var labelRow = label.Length > 0 ? 22f * u : 0f;
        var height = size + labelRow;
        var (fx, fy) = HudFlow.Of(hud);
        var cx = ScreenMath.Span(fx, size) + size / 2f;
        var cy = ScreenMath.Span(fy, height) + labelRow + size / 2f;
        var ice = UIKitColors.Cockpit;

        var main = Active ? ActiveColour : ice;
        _canvas.Plate(cx, cy, size, size, cut, Active ? UIKitColors.Faded(ActiveColour, ActiveFill) : UIKitColors.Faded(UIKitColors.Plate, 0.35f));
        var frame = Active ? UIKitColors.Faded(ActiveColour, 0.95f) : ready ? UIKitColors.Faded(ice, 0.45f) : UIKitColors.Faded(ice, 0.25f);
        var denied = now - _deniedAt;
        if (denied < 0.3f) frame = UIKitColors.Faded(UIKitColors.Red, 1f - denied / 0.3f * 0.5f);
        _canvas.Frame(cx, cy, size, size, cut, frame);
        var iconColour = cooling || empty ? UIKitColors.Faded(main, 0.38f) : UIKitColors.Faded(main, 0.9f);
        var lift = key.Length > 0 ? size * 0.1f : 0f;
        if (Icon != null) _canvas.Icon(Icon, cx, cy + lift, size * 0.5f, size * 0.5f, iconColour);
        else if (inside.Length > 0 && !cooling) _canvas.Text(inside, cx, cy, 16f, iconColour, HudAlign.Centre, true);
        if (cooling)
        {
            _canvas.Ring(_cooldown, cx, cy, size * 0.82f, UIKitColors.Faded(ice, 0.7f), Color.clear);
            _canvas.Text(_cooldown.Text, cx, cy, 30f, UIKitColors.Faded(ice, 0.95f), HudAlign.Centre, true);
        }
        if (_holds && _hold.Running) _canvas.Ring(_hold, cx, cy, size * 0.82f, UIKitColors.Faded(ice, 0.35f), Color.clear, disc: true);
        var flash = _cooldown.DoneAge;
        if (ReadyFlash && flash >= 0f && flash < 0.15f) _canvas.Frame(cx, cy, size, size, cut, UIKitColors.Faded(Color.white, 1f - flash / 0.15f));
        if (label.Length > 0) _canvas.Text(label, cx, cy - size / 2f - 13f * u, 13f, UIKitColors.Faded(ice, Active ? 0.95f : 0.72f), HudAlign.Centre);
        if (Count != null)
            _canvas.Text(count < 0 ? "∞" : NumberText.Count(count), cx + size / 2f - 6f * u, cy - size / 2f + 10f * u, 14f, empty ? UIKitColors.Red : UIKitColors.Faded(ice, 0.9f), HudAlign.Right, true);
        var below = cy - size / 2f;
        if (Progress != null)
        {
            float value;
            try { value = Progress(); }
            catch (Exception error) { value = -1f; Plugin.Error("ui-skill-slot-progress-failed", "Skill slot '" + Id + "' progress failed: " + error); Progress = null; }
            _progress.Fraction(value, now);
            if (_progress.Running)
            {
                var fill = _progress.Fill;
                var py = below - 4f * u;
                _canvas.Rect(cx, py, size, 2f * u, UIKitColors.Faded(ice, 0.13f));
                if (fill > 0f) _canvas.Rect(cx - size / 2f + size * fill / 2f, py, size * fill, 2f * u, ActiveColour);
            }
        }
        if (note.Length > 0) _canvas.Text(note, cx + size / 2f + 8f * u, cy, 14f, Active ? ActiveColour : UIKitColors.Faded(ice, 0.72f));
        if (key.Length > 0)
        {
            var ky = below + 6f * u + KeyCap.Height * u / 2f;
            KeyCap.Draw(_canvas, cx, ky, KeyCap.Width(_canvas, key), KeyCap.Height * u, key,
                ready ? UIKitColors.Faded(UIKitColors.Accent, 0.9f) : UIKitColors.Faded(UIKitColors.Line, 0.6f),
                ready ? UIKitColors.Bright : UIKitColors.Faded(UIKitColors.Text, 0.45f), 0f, default);
        }
        _canvas.End();
    }
}
