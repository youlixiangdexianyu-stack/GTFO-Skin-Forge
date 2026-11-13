using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>What a <see cref="HudCommandList"/> shows.</summary>
public enum CommandListMode : byte
{
    /// <summary>One compact row per command: its category mark, its name and a dark value cell (the uses left, ∞ without a limit,
    /// outlined in orange with an orange block at its left while it can be called; the cooldown seconds over a filling bar while it cools).</summary>
    Idle,
    /// <summary>The table while a code is typed: each row opens to show its code as key cells; on the commands the typed steps still
    /// fit, the typed cells light in the accent and the next one is outlined; every other row is dimmed. A cancel hint closes it.</summary>
    Entering,
    /// <summary>The armed command alone: its whole code lit, its name and the held label in the value cell, the throw hint under it.</summary>
    Holding
}

/// <summary>One command of a <see cref="HudCommandList"/>: a name, the arrow code that calls it, its category colour and its state. Keep
/// one instance per command and change <see cref="Uses"/> and <see cref="SetCooldown"/> as they change.</summary>
public sealed class CommandItem
{
    public CommandItem(string name, IReadOnlyList<ArrowDirection> code, Color accent)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Accent = accent;
    }

    /// <summary>Shown in capitals.</summary>
    public string Name { get; }

    public IReadOnlyList<ArrowDirection> Code { get; }

    /// <summary>The category colour: the small mark before the name and the cooldown bar.</summary>
    public Color Accent { get; }

    /// <summary>Uses left; negative for no limit, 0 for used up.</summary>
    public int Uses { get; set; } = -1;

    /// <summary>Whole seconds of cooldown left (rounded up); 0 when not cooling.</summary>
    public int CooldownSeconds { get; private set; }

    /// <summary>How much of the cooldown has passed, 0..1 (the bar along a cooling row); 0 while not cooling or the full length is unknown.</summary>
    public float CooldownProgress { get; private set; }

    /// <summary>The cooldown: <paramref name="remaining"/> seconds left of <paramref name="total"/> (0 or less: not cooling; a total of 0
    /// or less: the length is unknown and the bar stays empty). The same arithmetic as every other cooldown in the kit (<see cref="CooldownMath"/>).</summary>
    public void SetCooldown(double remaining, double total)
    {
        if (!(remaining > 0)) { CooldownSeconds = 0; CooldownProgress = 0f; return; }
        CooldownSeconds = (int)Math.Ceiling(remaining);
        CooldownProgress = total > 0 ? 1f - CooldownMath.Remaining01((float)remaining, (float)total) : 0f;
    }

    public CommandState State => new(Uses, CooldownSeconds);
}

/// <summary>
/// A list of commands called by typing an arrow code (hold a key, press the arrows): the call-in list of a stratagem-style mod. It stands
/// against the right edge of the screen with its middle on the middle of the screen (<see cref="CommandDock"/>), left of anything that
/// fills that column (a stand-in inventory) and clear of everything else on the HUD; it takes no room in the regions. Cut-corner plates
/// with thin white outlines, a category mark per command and a dark value cell with light figures, outlined in orange for what can
/// be called now. Between its forms
/// (<see cref="CommandListMode"/>) every size eases at one rate from the frame the form changes (<see cref="CommandMotion"/>): the final
/// size and place are known at once and the list moves continuously, never after the fact. Pieces are drawn once each, without the
/// shared glow and colour fringe, and while it shows the game's glass reflection (an offset copy of the whole HUD) is off, so no text
/// has a second image. You give it data every frame with <see cref="Show"/>. Make it with <see cref="UIKitMod.AddCommandList"/>.
/// </summary>
public sealed class HudCommandList : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private static readonly Color PlateDark = new(0.039f, 0.051f, 0.063f, 1f);
    private const string SecondsUnit = "<space=0.1em><size=62%><color=#D9F2FAB3>s</color></size>";
    private const float LiveDim = 0.36f, ReadyBlock = 3f, KeyCut = 6f, Glyph = 12f, HintPx = 14f, TitlePx = 13f, KeyGap = 7f, WideGap = 15f;
    private const int MaxObstacles = 48;

    private readonly int _maxItems, _maxSteps;
    private readonly CommandMotion _motion;
    private readonly string[] _upper, _meta;
    private readonly CommandItem?[] _upperOf;
    private readonly int[] _metaUses, _metaSeconds;
    private readonly HudBox[] _obstacles = new HudBox[MaxObstacles];
    private HudCanvas? _canvas;
    private GameHudHide? _glass;

    // The data of the latest Show.
    private CommandListMode _mode;
    private IReadOnlyList<CommandItem> _items = Array.Empty<CommandItem>();
    private IReadOnlyList<ArrowDirection> _typed = Array.Empty<ArrowDirection>();
    private CommandItem? _held;
    private string? _noticeText;
    private float _noticeUntil;

    // Measured once per item list, language and screen.
    private IReadOnlyList<CommandItem>? _measuredFor;
    private int _measuredLanguage = -1, _measuredScreen = -1, _measuredGeneration = -1;
    private float _nameIdle, _nameTable;
    private int _longest;
    private HintPart[] _hintParts = Array.Empty<HintPart>();
    private string[] _hintWords = Array.Empty<string>();
    private int _hintLanguage = -1;
    private bool _hintDirty = true;

    // Where it stands (1080p units from the middle of the screen).
    private bool _placed;
    private float _right;

    internal HudCommandList(UIText title, UIText heldTitle, UIText heldLabel, int maxItems, int maxSteps)
    {
        Title = title;
        HeldTitle = heldTitle;
        HeldLabel = heldLabel;
        _maxItems = maxItems;
        _maxSteps = maxSteps;
        _motion = new CommandMotion(maxItems);
        _upper = new string[maxItems];
        _meta = new string[maxItems];
        _upperOf = new CommandItem?[maxItems];
        _metaUses = new int[maxItems];
        _metaSeconds = new int[maxItems];
        for (var i = 0; i < maxItems; i++) { _upper[i] = _meta[i] = ""; _metaUses[i] = _metaSeconds[i] = int.MinValue; }
    }

    /// <summary>The header while a code is typed.</summary>
    public UIText Title { get; }

    /// <summary>The header while a command is armed.</summary>
    public UIText HeldTitle { get; }

    /// <summary>The text in the armed command's value cell (for example THROW).</summary>
    public UIText HeldLabel { get; }

    /// <summary>The game's glass reflection is held off through this hide while the list shows (set by <see cref="UIKitMod.AddCommandList"/>).</summary>
    internal GameHudHide? Glass { set => _glass = value; }

    /// <summary>Sets what the hint line under the table says (replaced by a notice while one is up); no parts hides it.</summary>
    public void SetHint(params HintPart[] parts)
    {
        _hintParts = parts == null ? Array.Empty<HintPart>() : (HintPart[])parts.Clone();
        _hintDirty = true;
    }

    /// <summary>Gives the list this frame's data and shows it (it is hidden with no commands and no notice). Call it every frame the list
    /// should be up, after updating the items; <see cref="Hide"/> takes it down.</summary>
    /// <param name="mode">Which form to draw.</param>
    /// <param name="items">The commands, in order; pass the same list object while it does not change.</param>
    /// <param name="typed">The steps typed so far (<see cref="CommandListMode.Entering"/>).</param>
    /// <param name="held">The armed command (<see cref="CommandListMode.Holding"/>).</param>
    public void Show(CommandListMode mode, IReadOnlyList<CommandItem> items, IReadOnlyList<ArrowDirection> typed, CommandItem? held)
    {
        _mode = mode;
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _typed = typed ?? throw new ArgumentNullException(nameof(typed));
        _held = held;
        ExpireNotice(ForgeCore.Frame.GameTime);
        if (items.Count == 0 && _noticeText == null) { Hide(); return; }
        Up();
    }

    /// <summary>Shows a line of text under the list for <paramref name="seconds"/> and takes it away by itself; a new notice replaces the
    /// one showing. With no commands the list is up for the notice alone.</summary>
    public void Notify(string text, float seconds)
    {
        if (string.IsNullOrEmpty(text) || seconds <= 0f) return;
        _noticeText = text;
        _noticeUntil = ForgeCore.Frame.GameTime + seconds;
        Up();
    }

    /// <summary>Takes the list down; the next <see cref="Show"/> places it again without easing in.</summary>
    public void Hide()
    {
        Visible = false;
        _placed = false;
        _motion.Reset();
        if (_glass is { Active: true } glass) glass.Active = false;
    }

    private void Up()
    {
        if (!Visible) { Visible = true; _placed = false; _motion.Reset(); }
        if (_glass is { Active: false } glass && GameHud.CanHold(out _)) glass.Active = true;
    }

    private void ExpireNotice(float now)
    {
        if (_noticeText != null && now >= _noticeUntil) _noticeText = null;
    }

    // ---- build and update ----------------------------------------------------------------------------------

    internal override void Build(HudElement hud)
    {
        hud.Effects = false;
        _canvas = new HudCanvas(hud);
        _placed = false;
        _motion.Reset();
        _measuredFor = null;
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        var canvas = _canvas;
        if (canvas == null) return;
        ExpireNotice(now);
        var items = _items;
        if (items.Count == 0 && _noticeText == null) { Hide(); return; }
        if (!canvas.Begin()) return;
        var u = canvas.Unit;
        var count = Math.Min(items.Count, _maxItems);
        Measure(hud, canvas, items, count, u);

        var mode = _mode;
        var held = mode == CommandListMode.Holding ? IndexOf(items, count, _held) : -1;
        var codes = mode == CommandListMode.Entering ? CommandGeometry.Codes(_longest)
            : mode == CommandListMode.Holding && held >= 0 ? CommandGeometry.Codes(Math.Min(items[held].Code.Count, _maxSteps)) : 0f;
        var notice = _noticeText;
        _motion.Step(mode != CommandListMode.Idle, count, held, codes, notice != null, dt);

        // The column, from the largest the list gets; then the middle, from what it is now.
        var halfW = hud.HalfWidth / u;
        var halfH = hud.HalfHeight / u;
        var margin = Plugin.HudSafeMargin / u;
        var obstacles = Obstacles(hud, u);
        var widest = CommandGeometry.Width(CommandGeometry.Codes(_longest), Math.Max(_nameIdle, _nameTable), 1f);
        var right = CommandDock.Right(_obstacles.AsSpan(0, obstacles), halfW - margin, -halfH + margin, halfH - margin, widest, CommandMotion.TallestHeight(count));
        _right = _placed ? Ease.Damp(_right, right, CommandMotion.Rate, dt) : right;
        if (Math.Abs(_right - right) < 0.05f) _right = right;
        CommandDock.Limits(_obstacles.AsSpan(0, obstacles), _right - widest, _right, -halfH + margin, halfH - margin, out var floor, out var ceiling, out _, out _);
        var height = _motion.Height;
        var centre = CommandDock.Centre(height, floor, ceiling);
        var table = _motion.Table;
        var width = CommandGeometry.Width(_motion.Codes, _nameIdle + (_nameTable - _nameIdle) * table, table);
        var top = centre + height / 2f;
        _placed = true;

        Draw(canvas, items, count, held, top, width, u, mode, notice);
        canvas.End();
    }

    /// <summary>Names in capitals, their widths at the idle and table sizes and the longest code, again only when the items, the
    /// language, the screen or the HUD changed.</summary>
    private void Measure(HudElement hud, HudCanvas canvas, IReadOnlyList<CommandItem> items, int count, float u)
    {
        if (ReferenceEquals(items, _measuredFor) && _measuredLanguage == UITexts.Version && _measuredScreen == hud.ScreenVersion && _measuredGeneration == hud.Generation)
        {
            Hints();
            return;
        }
        _measuredFor = items;
        _measuredLanguage = UITexts.Version;
        _measuredScreen = hud.ScreenVersion;
        _measuredGeneration = hud.Generation;
        _nameIdle = _nameTable = 0f;
        _longest = 1;
        for (var i = 0; i < count; i++)
        {
            var name = Upper(i, items[i]);
            _nameIdle = Math.Max(_nameIdle, canvas.TextWidth(name, CommandGeometry.IdleName, true) / u);
            _nameTable = Math.Max(_nameTable, canvas.TextWidth(name, CommandGeometry.TableName, true) / u);
            _longest = Math.Max(_longest, Math.Min(items[i].Code.Count, _maxSteps));
        }
        _hintDirty = true;
        Hints();
    }

    private void Hints()
    {
        if (!_hintDirty && _hintLanguage == UITexts.Version) return;
        _hintDirty = false;
        _hintLanguage = UITexts.Version;
        if (_hintWords.Length != _hintParts.Length) _hintWords = new string[_hintParts.Length];
        for (var i = 0; i < _hintParts.Length; i++)
        {
            var text = Show(_hintParts[i].Text);
            _hintWords[i] = _hintParts[i].IsKey ? text : text.ToUpperInvariant();
        }
    }

    private static int IndexOf(IReadOnlyList<CommandItem> items, int count, CommandItem? item)
    {
        if (item == null) return -1;
        for (var i = 0; i < count; i++) if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }

    /// <summary>Everything else the HUD shows, relative to the middle of the screen in 1080p units: the game's weapon list and vitals
    /// while the game draws them, and every element placed in a region. Returns how many.</summary>
    private int Obstacles(HudElement hud, float u)
    {
        var n = 0;
        var rootX = hud.Entry.RootX;
        var rootY = hud.Entry.RootY;
        var scene = Hud.Scene;
        if (scene.WeaponList is { } list && !scene.WeaponListFree) Add(ref n, list, rootX, rootY, u);
        if (scene.Vitals is { } vitals && !scene.VitalsFree) Add(ref n, vitals, rootX, rootY, u);
        var elements = Hud.Elements;
        for (var i = 0; i < elements.Count && n < MaxObstacles; i++)
        {
            var e = elements[i];
            if (ReferenceEquals(e, hud) || e.Entry.State != LayoutState.Placed) continue;
            Add(ref n, e.Entry.Box, rootX, rootY, u);
        }
        return n;
    }

    private void Add(ref int n, HudBox box, float rootX, float rootY, float u)
    {
        if (n >= MaxObstacles) return;
        _obstacles[n] = new HudBox((box.Left - rootX) / u, (box.Bottom - rootY) / u, (box.Right - rootX) / u, (box.Top - rootY) / u);
        n++;
    }

    // ---- drawing (1080p units, scaled by u) ---------------------------------------------------------------

    private void Draw(HudCanvas c, IReadOnlyList<CommandItem> items, int count, int held, float top, float width, float u, CommandListMode mode, string? notice)
    {
        var t = _motion.Table;
        var right = _right;
        var left = right - width;
        var row = CommandGeometry.Row(t);
        var gap = CommandGeometry.Gap(t);
        var namePx = CommandGeometry.NamePx(t);
        var cell = CommandGeometry.Cell(t);
        var codes = _motion.Codes;
        var y = top;
        if (t > 0.01f)
        {
            var title = Show(mode == CommandListMode.Holding ? HeldTitle : Title);
            var ty = top - 9f;
            var tw = title.Length > 0 ? c.Text(title, right * u, ty * u, TitlePx, Faded(UIKitColors.Cockpit, 0.72f * t), HudAlign.Right, true) / u : 0f;
            var line = width - tw - 14f;
            if (line > 0f) c.Rect((left + line / 2f) * u, ty * u, line * u, u, Faded(UIKitColors.Cockpit, 0.22f * t));
            y -= CommandGeometry.Header * t;
        }
        for (var i = 0; i < count; i++)
        {
            var shown = _motion.Shown(i);
            if (shown <= 0.001f) continue;
            var h = row * shown;
            var cy = y - h / 2f;
            Row(c, items[i], i, i == held, cy, h, shown, left, right, width, codes, namePx, cell, t, mode, u);
            y -= (row + gap) * shown;
        }
        var line2 = CommandGeometry.Hint * Math.Max(t, _motion.Note);
        if (line2 > 0.5f)
        {
            var hy = y + gap - line2 / 2f - 2f;
            if (notice != null) c.Text(notice, right * u, hy * u, HintPx, Faded(UIKitColors.Orange, Math.Max(t, _motion.Note)), HudAlign.Right, true);
            else if (t > 0.01f) HintLine(c, right, hy, t, u);
        }
    }

    private void Row(HudCanvas c, CommandItem item, int index, bool armed, float cy, float h, float shown, float left, float right, float width,
        float codes, float namePx, float cell, float t, CommandListMode mode, float u)
    {
        var state = item.State;
        var ready = state.Ready;
        var typed = _typed;
        var fits = mode != CommandListMode.Entering || CommandCode.StartsWith(item.Code, typed);
        var live = ready && fits;
        var dim = mode == CommandListMode.Entering && !live ? 1f - (1f - LiveDim) * t : 1f;
        var a = dim * shown;
        var cx = (left + right) / 2f;
        c.Plate(cx * u, cy * u, width * u, h * u, CommandGeometry.Cut * u, Faded(PlateDark, 0.62f * shown));
        c.Frame(cx * u, cy * u, width * u, h * u, CommandGeometry.Cut * u, Faded(UIKitColors.Cockpit, (ready ? 0.42f : 0.18f) * a));
        // The contents come in once the row is more than half open (an armed command's row closes the others).
        var inner = Ease.Clamp01((shown - 0.55f) / 0.45f) * dim;
        if (inner <= 0.01f) return;

        // Key cells of the code.
        var code = item.Code;
        var steps = Math.Min(code.Count, _maxSteps);
        if (codes > 0.5f)
        {
            var keys = Ease.Clamp01(codes / Math.Max(1f, CommandGeometry.Codes(steps))) * inner * t;
            for (var k = 0; k < steps; k++)
            {
                var kx = left + CommandGeometry.KeyPad + k * CommandGeometry.KeyStep + CommandGeometry.Key / 2f;
                if (kx + CommandGeometry.Key / 2f > left + codes) break;
                var hit = armed || (mode == CommandListMode.Entering && live && k < typed.Count);
                var next = mode == CommandListMode.Entering && live && k == typed.Count;
                var size = CommandGeometry.Key * u;
                c.Plate(kx * u, cy * u, size, size, KeyCut * u, hit ? Faded(UIKitColors.Orange, 0.95f * keys) : Faded(PlateDark, 0.7f * keys));
                c.Frame(kx * u, cy * u, size, size, KeyCut * u, hit ? Faded(UIKitColors.Orange, keys) : Faded(UIKitColors.Cockpit, (next ? 1f : 0.32f) * keys));
                // The next step's outline is drawn twice, one unit apart, so it reads as a bold frame (a frame line is an eighth of its cut).
                if (next) c.Frame(kx * u, cy * u, size - 2f * u, size - 2f * u, (KeyCut - 1f) * u, Faded(UIKitColors.Cockpit, keys));
                c.Sprite(UIKitSprites.ArrowGlyph(code[k]), kx * u, cy * u, Glyph * u, Glyph * u, hit ? Faded(UIKitColors.OnAccent, keys) : Faded(UIKitColors.Cockpit, (next ? 1f : 0.7f) * keys));
            }
        }

        // Category mark and name.
        var nx = left + codes + CommandGeometry.Pad;
        c.Rect((nx + CommandGeometry.Block / 2f) * u, cy * u, CommandGeometry.Block * u, CommandGeometry.Block * u, Faded(item.Accent, inner));
        c.Text(Upper(index, item), (nx + CommandGeometry.Block + CommandGeometry.BlockGap) * u, cy * u, namePx, Faded(UIKitColors.Cockpit, (ready || armed ? 0.96f : 0.55f) * inner), HudAlign.Left, true);

        // Value cell.
        var vx = right - CommandGeometry.CellInset - cell / 2f;
        var vh = Math.Max(0f, h - 2f * CommandGeometry.CellInset);
        // Every value cell is a dark plate with light figures; one that can be called now (or is armed) has an orange outline and an
        // orange block down its left side.
        c.Plate(vx * u, cy * u, cell * u, vh * u, KeyCut * u, Faded(UIKitColors.Plate, 0.9f * inner));
        if (armed || ready)
        {
            c.Frame(vx * u, cy * u, cell * u, vh * u, KeyCut * u, Faded(UIKitColors.Orange, 0.9f * inner));
            c.Rect((vx - cell / 2f + ReadyBlock / 2f) * u, cy * u, ReadyBlock * u, Math.Max(0f, vh - 4f) * u, Faded(UIKitColors.Orange, inner));
            var tx = (vx + ReadyBlock / 2f) * u;
            if (armed) c.Text(Show(HeldLabel), tx, cy * u, namePx - 4f, Faded(UIKitColors.Orange, inner), HudAlign.Centre, true);
            else c.Text(Meta(index, state), tx, cy * u, namePx + (state.Uses < 0 ? 3f : 0f), Faded(UIKitColors.Bright, inner), HudAlign.Centre, true);
        }
        else
        {
            c.Frame(vx * u, cy * u, cell * u, vh * u, KeyCut * u, Faded(UIKitColors.Cockpit, 0.3f * inner));
            c.Text(Meta(index, state), vx * u, cy * u, namePx, state.Empty ? Faded(UIKitColors.Red, 0.9f * inner) : Faded(UIKitColors.Cockpit, inner), HudAlign.Centre, true);
            if (state.Cooling)
            {
                var bx = left + CommandGeometry.Cut + 2f;
                var bw = width - 2f * CommandGeometry.Cut - 4f;
                var by = cy - h / 2f + 2.5f;
                var p = Ease.Clamp01(item.CooldownProgress);
                c.Rect((bx + bw / 2f) * u, by * u, bw * u, 2f * u, Faded(UIKitColors.Cockpit, 0.12f * inner));
                if (p > 0f) c.Rect((bx + bw * p / 2f) * u, by * u, bw * p * u, 2f * u, Faded(item.Accent, 0.9f * inner));
            }
        }
    }

    /// <summary>The hint parts right-aligned at <paramref name="right"/>: words in capitals, keys in the kit's key cap.</summary>
    private void HintLine(HudCanvas c, float right, float y, float alpha, float u)
    {
        var x = right;
        for (var i = _hintParts.Length - 1; i >= 0; i--)
        {
            var text = _hintWords[i];
            if (text.Length == 0) continue;
            if (_hintParts[i].IsKey)
            {
                var w = KeyCap.Width(c, text) / u;
                KeyCap.Draw(c, (x - w / 2f) * u, y * u, w * u, 20f * u, text, Faded(UIKitColors.Cockpit, 0.75f * alpha), Faded(UIKitColors.Bright, alpha), 0f, default);
                x -= w + (_hintParts[i].Wide ? WideGap : KeyGap);
            }
            else
            {
                var w = c.Text(text, x * u, y * u, HintPx, Faded(UIKitColors.Cockpit, 0.72f * alpha), HudAlign.Right, true) / u;
                x -= w + KeyGap;
            }
        }
    }

    private static Color Faded(Color c, float share) => new(c.r, c.g, c.b, c.a * Ease.Clamp01(share));

    // ---- texts ---------------------------------------------------------------------------------------------

    private string Upper(int index, CommandItem item)
    {
        if (!ReferenceEquals(_upperOf[index], item))
        {
            _upperOf[index] = item;
            _upper[index] = item.Name.ToUpperInvariant();
            _metaUses[index] = _metaSeconds[index] = int.MinValue;
        }
        return _upper[index];
    }

    /// <summary>The value cell's text: cooldown seconds while cooling, else ∞ or the uses left. Rebuilt only when it changes.</summary>
    private string Meta(int index, CommandState state)
    {
        var seconds = state.Cooling ? state.CooldownSeconds : 0;
        var uses = state.Cooling ? int.MinValue + 1 : state.Uses;
        if (_metaSeconds[index] == seconds && _metaUses[index] == uses) return _meta[index];
        _metaSeconds[index] = seconds;
        _metaUses[index] = uses;
        _meta[index] = state.Cooling ? seconds + SecondsUnit : uses < 0 ? "∞" : uses.ToString();
        return _meta[index];
    }

}
