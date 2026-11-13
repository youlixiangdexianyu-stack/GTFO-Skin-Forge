using System;
using System.Collections.Generic;
using SNetwork;
using UnityEngine;

namespace ForgeUI;

/// <summary>How <see cref="HudInteraction"/> shows a hold in progress.</summary>
public enum InteractionProgress : byte
{
    /// <summary>A bar under the prompt.</summary>
    Bar,
    /// <summary>A thin orange underline under the prompt's text (no extra shape in the middle of the screen).</summary>
    Underline,
}

/// <summary>
/// The game's objectives redrawn compactly in the kit's style (for when the game's HUD is hidden): per layer a small bold line with its
/// tag (with an optional icon) and, after the first tag, the header in plain text (with an optional ruler under it); the objective in at
/// most <see cref="MainLines"/> lines; its current step in one line; one progression line that adds something; the game's objective
/// timer and who has the map open. Text the game repeats across these is shown once. Lines are broken where the font really fills
/// <see cref="Width"/>, and what the game highlights in the objective and its steps keeps its place in <see cref="HighlightColour"/>.
/// Optionally the block stands on a chamfered backing (<see cref="Backing"/>, <see cref="Chamfer"/>) with a thin bar down its edge
/// (<see cref="AccentBar"/>, topped with the first tag's colour); the text is then inset and the tag icon gets a column of its own.
/// New lines slide in and fade in; lines that went away are struck through and fade. Text comes from the game in the game's language
/// (<see cref="UIKit.Objectives"/>); lines are broken again only when it changes. Sizes are 1080p pixels. Make it with
/// <see cref="UIKitMod.AddObjectives"/>.
/// </summary>
public sealed class HudObjectives : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private const float StrikeSeconds = 0.6f, SlideSeconds = 0.2f, TagGap = 8f, BoxPad = 10f, BoxPadY = 8f, IconColumn = 20f, BarCap = 12f;
    private readonly List<string> _wrap = new();
    private readonly List<string> _text = new();
    private readonly List<string> _suffix = new();
    private readonly List<string> _shown = new();
    private readonly List<byte> _kind = new();
    private readonly List<string> _marked = new();
    private readonly List<(int Start, int End)> _spans = new();
    private string _highlight = "";
    private float _unit = -1f;
    private readonly List<float> _since = new();
    private readonly List<string> _gone = new();
    private readonly List<byte> _goneKind = new();
    private readonly List<float> _goneAt = new();
    private readonly Dictionary<string, float> _seen = new(StringComparer.Ordinal);
    private HudCanvas? _canvas;
    private int _version = -1;
    private float _px = -1f, _width = -1f;
    private bool _primed;
    private HudAlign _align;

    internal HudObjectives() { }

    public Color HeaderColour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.7f);
    public Color MainTagColour { get; set; } = UIKitColors.Orange;
    public Color SecondaryTagColour { get; set; } = UIKitColors.Amber;
    public Color OverloadTagColour { get; set; } = UIKitColors.Red;
    public Color TextColour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.95f);
    /// <summary>The current step, a progression line and the map prompt; default ice white at 72%.</summary>
    public Color SubColour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.72f);
    /// <summary>What the game highlights in its text (an item such as HISEC_CARGO_386, a zone) in place of the game's own colour;
    /// default orange.</summary>
    public Color HighlightColour { get; set; } = UIKitColors.Orange;
    /// <summary>A plate behind the whole block; fully transparent (default) draws none.</summary>
    public Color Backing { get; set; } = Color.clear;
    /// <summary>The backing's corner cut (top left and bottom right); 0 (default) draws a plain rectangle.</summary>
    public float Chamfer { get; set; }
    /// <summary>A thin bar down the block's edge on the side its lines line up on, its top part in the first tag's colour; fully
    /// transparent (default) draws none.</summary>
    public Color AccentBar { get; set; } = Color.clear;
    /// <summary>The header (the level and objective title) after the first tag; default 14.</summary>
    public float HeaderPx { get; set; } = 14f;
    public float TagPx { get; set; } = 14f;
    /// <summary>The objective's size; default 16.</summary>
    public float TextPx { get; set; } = 16f;
    /// <summary>The current step, a progression line and the timer; default 14.</summary>
    public float SubPx { get; set; } = 14f;
    public bool ShowHeader { get; set; } = true;
    /// <summary>A small picture before the header; null draws none.</summary>
    public Sprite? HeaderIcon { get; set; }
    /// <summary>A small picture before each layer's tag (an objective mark); null draws none.</summary>
    public Sprite? TagIcon { get; set; }
    /// <summary>A thin ruler under the header, as long as the lines are wide. Default false.</summary>
    public bool Ruler { get; set; }
    /// <summary>New lines slide in from the right and fade in. Default true.</summary>
    public bool Transition { get; set; } = true;
    /// <summary>Lines that went away stay a moment, dimmed and struck through. Default true.</summary>
    public bool DoneStrike { get; set; } = true;
    /// <summary>Shows the game's objective timer (a reactor countdown) under the objectives. Default true.</summary>
    public bool ShowTimer { get; set; } = true;
    /// <summary>Shows the warden intel and sub-objective messages the game flashes (<see cref="UIKit.Intel"/>) under the objectives, in at
    /// most two lines, for as long as the game shows them. Default true.</summary>
    public bool ShowIntel { get; set; } = true;
    /// <summary>The intel lines; default orange.</summary>
    public Color IntelColour { get; set; } = UIKitColors.Orange;
    /// <summary>A picture drawn once per player who has the map open, with the game's map prompt; null draws the prompt alone.</summary>
    public Sprite? MapIcon { get; set; }
    /// <summary>Which side the lines line up on; null (default) the side of its region: right in a right-hand region, left in a
    /// left-hand one, centred in a centred one.</summary>
    public HudAlign? Align { get; set; }
    /// <summary>The widest line, measured with the font, before breaking; default 400. A backing or accent bar adds its inset around it.</summary>
    public float Width { get; set; } = 400f;
    /// <summary>The objective breaks into at most this many lines, the last cut with "…"; default 2. Every other line is one line.</summary>
    public int MainLines { get; set; } = 2;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
        _version = -1;
        // Only intel the game flashes from now on: one shown before this element existed is not replayed.
        _intelVersion = UIKit.Intel.Version;
        _intelLines.Clear();
    }

    private readonly List<string> _intelLines = new();
    private int _intelVersion = -1;
    private float _intelUntil = float.NegativeInfinity, _intelUnit = -1f;

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var objectives = UIKit.Objectives;
        if (objectives.Version != _version || _px != TextPx || _width != Width || _unit != u) { _unit = u; Lay(objectives, now, u); }
        var width = Width * u;
        var timer = ShowTimer && objectives.TimerActive && (objectives.TimerText.Length > 0 || objectives.TimerTitle.Length > 0);
        var map = objectives.MapViewers > 0;
        var intel = UIKit.Intel;
        if (intel.Version != _intelVersion || _intelUnit != u)
        {
            if (intel.Version != _intelVersion) _intelUntil = now + Math.Max(1f, intel.Duration);
            _intelVersion = intel.Version;
            _intelUnit = u;
            var canvas = _canvas;
            TextWrap.Lines(RichText.Plain(RichText.StripTags(intel.Text)).Trim(), s => canvas.TextWidth(s, SubPx) / u, Width, 2, _intelLines);
        }
        var intelShown = ShowIntel && now < _intelUntil && _intelLines.Count > 0;
        var content = 0f;
        for (var i = 0; i < _goneAt.Count; i++) if (now - _goneAt[i] < StrikeSeconds) content += Line(_goneKind[i]);
        for (var i = 0; i < _text.Count; i++) content += Line(_kind[i]);
        content += (timer ? Line(5) : 0f) + (map ? Line(5) : 0f) + (Ruler ? 6f : 0f) + (intelShown ? _intelLines.Count * Line(5) : 0f);
        var (fx, fy) = HudFlow.Of(hud);
        _align = Align ?? (fx < 0 ? HudAlign.Right : fx > 0 ? HudAlign.Left : HudAlign.Centre);
        // With a backing or an accent bar the lines are inset, and on the left the tag icon gets a column of its own.
        var boxed = Backing.a > 0f || AccentBar.a > 0f;
        var pad = boxed ? BoxPad * u : 0f;
        var padY = boxed && content > 0f ? BoxPadY * u : 0f;
        var lead = pad + (boxed && TagIcon != null && _align != HudAlign.Right ? IconColumn * u : 0f);
        var boxWidth = width + lead + pad;
        var height = content * u + 2f * padY;
        var boxLeft = ScreenMath.Span(fx, boxWidth);
        var top = ScreenMath.Span(fy, height) + height;
        var left = _align == HudAlign.Right ? boxLeft + pad : boxLeft + lead;
        var x = _align == HudAlign.Right ? left + width : _align == HudAlign.Centre ? left + width / 2f : left;
        var y = top - padY;
        if (content > 0f) Box(boxLeft, top, boxWidth, height, u);
        for (var i = 0; i < _text.Count; i++)
        {
            var kind = _kind[i];
            var line = Line(kind) * u;
            y -= line / 2f;
            var age = now - _since[i];
            var t = Transition && age < SlideSeconds ? Ease.OutCubic(age / SlideSeconds) : 1f;
            var shift = (1f - t) * 12f * u;
            Draw(_text[i], _suffix[i], kind, x + shift, y, t, u);
            if (i == 0 && Ruler)
            {
                y -= line / 2f + 3f * u;
                _canvas.Rect(left + width / 2f, y, width, u, UIKitColors.Faded(UIKitColors.Cockpit, 0.15f));
                y -= 3f * u;
                continue;
            }
            y -= line / 2f;
        }
        for (var i = 0; i < _gone.Count; i++)
        {
            var age = now - _goneAt[i];
            if (age >= StrikeSeconds) continue;
            var line = Line(_goneKind[i]) * u;
            y -= line / 2f;
            var alpha = 0.45f * (1f - age / StrikeSeconds);
            var px = _goneKind[i] == 4 ? TextPx : SubPx;
            var w = _canvas.Text(_gone[i], x - (Transition ? age / StrikeSeconds * 12f * u : 0f), y, px, UIKitColors.Faded(TextColour, alpha), _align);
            var sx = _align == HudAlign.Right ? x - w / 2f : _align == HudAlign.Centre ? x : x + w / 2f;
            _canvas.Rect(sx, y, w * Ease.OutCubic(Math.Min(1f, age / 0.25f)), u, UIKitColors.Faded(TextColour, alpha));
            y -= line / 2f;
        }
        if (timer)
        {
            var line = Line(5) * u;
            y -= line / 2f;
            var title = objectives.TimerTitle;
            var w = objectives.TimerText.Length > 0 ? _canvas.Text(objectives.TimerText, x, y, SubPx, UIKitColors.Faded(UIKitColors.Amber, 0.95f), _align, true) : 0f;
            if (title.Length > 0)
            {
                var tx = _align == HudAlign.Right ? x - w - 10f * u : x + w + 10f * u;
                _canvas.Text(title, tx, y, HeaderPx, HeaderColour, _align == HudAlign.Right ? HudAlign.Right : HudAlign.Left);
            }
            y -= line / 2f;
        }
        if (map)
        {
            var line = Line(5) * u;
            y -= line / 2f;
            var cursor = x;
            var sign = _align == HudAlign.Right ? -1f : 1f;
            if (MapIcon != null)
                for (var i = 0; i < objectives.MapViewers; i++, cursor += sign * 18f * u)
                    _canvas.Icon(MapIcon, cursor + sign * 8f * u, y, 14f * u, 14f * u, UIKitColors.Faded(UIKitColors.Cockpit, 0.7f));
            if (objectives.MapPrompt.Length > 0) _canvas.Text(objectives.MapPrompt, cursor + sign * 4f * u, y, HeaderPx, HeaderColour, _align == HudAlign.Right ? HudAlign.Right : HudAlign.Left);
            y -= line / 2f;
        }
        if (intelShown)
        {
            var line = Line(5) * u;
            // Fades over its last half second, as the game's own flash goes.
            var colour = UIKitColors.Faded(IntelColour, Math.Clamp((_intelUntil - now) / 0.5f, 0f, 1f));
            for (var i = 0; i < _intelLines.Count; i++)
            {
                y -= line / 2f;
                _canvas.Text(_intelLines[i], x, y, SubPx, colour, _align);
                y -= line / 2f;
            }
        }
        _canvas.End();
    }

    /// <summary>The backing and the accent bar of a block <paramref name="width"/> x <paramref name="height"/> from its top left.</summary>
    private void Box(float left, float top, float width, float height, float u)
    {
        var c = _canvas!;
        var cut = Chamfer * u;
        if (Backing.a > 0f) c.Plate(left + width / 2f, top - height / 2f, width, height, cut, Backing);
        if (AccentBar.a <= 0f) return;
        // Down the edge the lines line up on, clear of the corner cut there (the top left one; the bottom right on the right).
        var right = _align == HudAlign.Right;
        var bx = right ? left + width - u : left + u;
        var high = top - (right ? 2f * u : cut + 2f * u);
        var low = top - height + (right ? cut + 2f * u : 3f * u);
        if (high <= low) return;
        c.Rect(bx, (high + low) / 2f, 2f * u, high - low, AccentBar);
        var first = _kind.Count > 0 && _kind[0] is >= 1 and <= 3 ? _kind[0] : (byte)0;
        if (first == 0) return;
        var cap = Math.Min(BarCap * u, high - low);
        c.Rect(bx, high - cap / 2f, 2f * u, cap, first == 1 ? MainTagColour : first == 2 ? SecondaryTagColour : OverloadTagColour);
    }

    private void Draw(string text, string suffix, byte kind, float x, float y, float alpha, float u)
    {
        var (colour, px, bold) = kind switch
        {
            0 => (HeaderColour, HeaderPx, false),
            1 => (MainTagColour, TagPx, true),
            2 => (SecondaryTagColour, TagPx, true),
            3 => (OverloadTagColour, TagPx, true),
            4 => (TextColour, TextPx, false),
            _ => (SubColour, SubPx, false),
        };
        colour = UIKitColors.Faded(colour, alpha);
        var icon = kind == 0 ? HeaderIcon : kind is >= 1 and <= 3 ? TagIcon : null;
        var gap = text.Length > 0 && suffix.Length > 0 ? TagGap * u : 0f;
        var tagX = x;
        // Right-aligned: the header keeps the right edge and the tag sits before it; otherwise the header follows the tag.
        if (suffix.Length > 0 && _align == HudAlign.Right)
            tagX = x - _canvas!.Text(suffix, x, y, HeaderPx, UIKitColors.Faded(HeaderColour, alpha), HudAlign.Right) - gap;
        var tagAlign = _align == HudAlign.Centre && suffix.Length > 0 ? HudAlign.Left : _align;
        var w = text.Length > 0 ? _canvas!.Text(text, tagX, y, px, colour, tagAlign, bold) : 0f;
        if (suffix.Length > 0 && _align != HudAlign.Right)
            _canvas!.Text(suffix, tagX + w + gap, y, HeaderPx, UIKitColors.Faded(HeaderColour, alpha), HudAlign.Left);
        if (icon == null) return;
        // The icon sits before the tag; left-aligned text keeps its column.
        var ix = _align == HudAlign.Right ? tagX - w - 15f * u : tagAlign == HudAlign.Centre ? x - w / 2f - 15f * u : x - 15f * u;
        _canvas!.Icon(icon, ix, y, 14f * u, 14f * u, colour);
    }

    private float Line(byte kind) => kind switch
    {
        0 => HeaderPx + 6f,
        >= 1 and <= 3 => TagPx + 6f,
        4 => TextPx + 6f,
        _ => SubPx + 6f,
    };

    /// <summary>
    /// Lines and their kinds: 0 the header alone (no layer shown), 1–3 a layer's tag by tier (the first one followed by the header),
    /// 4 a layer's objective (at most <see cref="MainLines"/> lines), 5 its current step or a progression line (one line each). The
    /// game repeats the objective in its current step and in progression lines ("[MAIN] …", "> …"); a line whose text is already shown
    /// is left out, and only the first progression line that adds something is kept.
    /// </summary>
    private void Lay(GameObjectives objectives, float now, float u)
    {
        _version = objectives.Version;
        _px = TextPx;
        _width = Width;
        _highlight = ColorUtility.ToHtmlStringRGB(HighlightColour);
        var before = new List<(string, byte)>(_text.Count);
        for (var i = 0; i < _text.Count; i++) before.Add((_text[i], _kind[i]));
        _text.Clear();
        _kind.Clear();
        _suffix.Clear();
        _since.Clear();
        _shown.Clear();
        var header = ShowHeader ? objectives.Header.Trim() : "";
        if (RichText.StripTags(header).Trim().Length == 0) header = "";
        for (var i = 0; i < objectives.Count; i++)
        {
            var layer = objectives.Layer(i);
            if (!layer.Visible) continue;
            var tag = RichText.StripTags(layer.Tag).Trim();
            var suffix = header;
            header = "";
            if (tag.Length > 0 || suffix.Length > 0) AddTag(tag, (byte)(1 + (int)layer.Tier), suffix, u);
            var main = layer.Main.Length > 0 ? layer.Main : layer.Sub;
            Add(main, 4, TextPx, Math.Max(1, MainLines), u);
            if (main != layer.Sub) Add(layer.Sub, 5, SubPx, 1, u);
        }
        if (header.Length > 0) Add(header, 0, HeaderPx, 1, u);
        for (var i = 0; i < objectives.ProgressionCount; i++)
        {
            var progression = objectives.Progression(i);
            var head = Fresh(progression.Header) ? progression.Header : "";
            var body = Fresh(progression.Text) ? progression.Text : "";
            var line = head.Length > 0 && body.Length > 0 ? head + " · " + body : head + body;
            if (line.Length == 0) continue;
            Add(line, 5, SubPx, 1, u);
            break;
        }
        // Lines new since the last layout slide in; steps that went away are struck through. The first layout is shown as it is.
        for (var i = 0; i < _text.Count; i++)
        {
            var known = _seen.TryGetValue(_text[i], out var since);
            _since.Add(known || !_primed ? (known ? since : -10f) : now);
        }
        _seen.Clear();
        for (var i = 0; i < _text.Count; i++) _seen[_text[i]] = _since[i];
        if (_primed && DoneStrike)
            foreach (var (text, kind) in before)
                if (kind >= 4 && !_seen.ContainsKey(text))
                {
                    _gone.Add(text);
                    _goneKind.Add(kind);
                    _goneAt.Add(now);
                }
        for (var i = _goneAt.Count - 1; i >= 0; i--)
            if (now - _goneAt[i] >= StrikeSeconds) { _gone.RemoveAt(i); _goneKind.RemoveAt(i); _goneAt.RemoveAt(i); }
        _primed = true;
    }

    private void AddTag(string tag, byte kind, string suffix, float u)
    {
        // The header follows the bold tag on the same line in plain text (the tag already says the tier), cut with "…" to what is
        // left of the width.
        if (suffix.Length > 0)
        {
            var room = Width - (tag.Length > 0 ? _canvas!.TextWidth(tag, TagPx, true) / u + TagGap : 0f);
            Wrap(suffix, HeaderPx, Math.Max(0f, room), 1, u);
            suffix = _wrap.Count > 0 ? RichText.Plain(_wrap[0]) : "";
        }
        _text.Add(RichText.Plain(tag));
        _kind.Add(kind);
        _suffix.Add(suffix);
    }

    private void Add(string text, byte kind, float px, int lines, float u)
    {
        var plain = Wrap(text, px, Width, lines, u);
        if (plain.Length == 0 || !Fresh(plain)) return;
        _shown.Add(Key(plain));
        for (var i = 0; i < _marked.Count; i++)
        {
            // The header standing alone stays plain, like the header after a tag.
            _text.Add(kind == 0 ? RichText.Plain(_wrap[i]) : _marked[i]);
            _kind.Add(kind);
            _suffix.Add("");
        }
    }

    /// <summary>The game's <paramref name="raw"/> text broken into at most <paramref name="maxLines"/> lines that the font really fits
    /// in <paramref name="width"/>, written into <see cref="_marked"/> ready to draw: shown as written, with what the game highlights
    /// (an item, a zone) in <see cref="HighlightColour"/>. Tags are read first and the highlight is laid again on each line, so no
    /// break falls inside a tag and no tag shows. Returns the plain text.</summary>
    private string Wrap(string raw, float px, float width, int maxLines, float u)
    {
        _marked.Clear();
        var all = RichText.Highlights(raw, _spans);
        var lead = 0;
        while (lead < all.Length && char.IsWhiteSpace(all[lead])) lead++;
        var plain = all.Trim();
        if (plain.Length == 0) return "";
        var canvas = _canvas!;
        TextWrap.Lines(plain, s => canvas.TextWidth(s, px) / u, width, maxLines, _wrap);
        var cursor = 0;
        foreach (var line in _wrap)
        {
            var core = line.EndsWith('…') && plain.IndexOf(line, cursor, StringComparison.Ordinal) < 0 ? line.Substring(0, line.Length - 1) : line;
            var at = plain.IndexOf(core, cursor, StringComparison.Ordinal);
            if (at < 0) at = cursor;
            _marked.Add(RichText.Mark(line, lead + at, _spans, _highlight));
            cursor = at + core.Length;
        }
        return plain;
    }

    /// <summary>The text says something no line shown so far says (compared without tags, a leading "[MAIN]" or "&gt;", and case).</summary>
    private bool Fresh(string text)
    {
        var key = Key(RichText.StripTags(text));
        if (key.Length == 0) return false;
        foreach (var shown in _shown)
            if (shown.Contains(key, StringComparison.Ordinal) || key.Contains(shown, StringComparison.Ordinal)) return false;
        return true;
    }

    private static string Key(string text)
    {
        var s = text.Trim();
        while (s.Length > 0)
        {
            if (s[0] == '[' && s.IndexOf(']') is var close and > 0) s = s.Substring(close + 1).TrimStart();
            else if (s[0] == '>' || s[0] == '-' || s[0] == '•') s = s.Substring(1).TrimStart();
            else break;
        }
        return s.ToLowerInvariant();
    }
}

/// <summary>
/// The game's interaction prompt redrawn under the crosshair: an optional lead picture, a key box (plain or a chamfered outline), the
/// text (what the game colours kept in <see cref="AlarmColour"/> for red and <see cref="HighlightColour"/> otherwise), the hold progress (a bar, or an underline), the game's extra line (the whole prompt turns amber when the game shows it as not
/// usable); then the game's message line in its style's colour (a scan in <see cref="ScanColour"/>, an alarm scan in
/// <see cref="AlarmColour"/>, a warning amber) with its progress bar and, for a scan, a mark per player it counts, filled for those
/// inside; and the game's pick-up lines. Content and timing come from the game (<see cref="UIKit.Interaction"/>, <see cref="UIKit.Scan"/>,
/// <see cref="UIKit.Pickups"/>). Sizes are 1080p pixels. Make it with <see cref="UIKitMod.AddInteraction"/>.
/// </summary>
public sealed class HudInteraction : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private HudCanvas? _canvas;
    private int _version = -1;
    private string _text = "", _key = "", _message = "", _extra = "";
    private Color _highlight, _warning;
    private float _gameHeight, _messageHeight, _messageStart = float.NaN;
    private long _gameKey, _messageKey;

    internal HudInteraction() { }

    public Color TextColour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.92f);
    public Color KeyColour { get; set; } = UIKitColors.Bright;
    /// <summary>The hold progress; default orange.</summary>
    public Color RingColour { get; set; } = UIKitColors.Orange;
    public Color DisabledColour { get; set; } = UIKitColors.Amber;
    /// <summary>A scan's message line, its progress bar and the marks of the players inside; default the cockpit's ice.</summary>
    public Color ScanColour { get; set; } = UIKitColors.Cockpit;
    /// <summary>A scan that sounds an alarm, and what the game colours red in its texts (an alarm door's "[WARNING:… DETECTED]"); default red.</summary>
    public Color AlarmColour { get; set; } = UIKitColors.Red;
    /// <summary>What the game colours otherwise in its texts (an item, a zone); default orange.</summary>
    public Color HighlightColour { get; set; } = UIKitColors.Orange;
    public float TextPx { get; set; } = 19f;
    /// <summary>How far under the crosshair the first line is; default 128. With <see cref="Above"/>: how far over it the lowest line is.</summary>
    public float Offset { get; set; } = 128f;
    /// <summary>Stacks the lines over the crosshair instead of under it, growing upwards from <see cref="Offset"/>. Default false.</summary>
    public bool Above { get; set; }
    /// <summary>Puts the first line where the game puts its own prompt (its height, read from the game's prompt object when the screen size or the
    /// game's interaction layer changes), instead of at <see cref="Offset"/>; the lines are still drawn by this element. Default false.</summary>
    public bool AtGamePrompt { get; set; }
    /// <summary>Puts the message line (a scan, a reactor step and its code) where the game puts its own message object, read the same way as
    /// <see cref="AtGamePrompt"/>, instead of under the prompt. Default false.</summary>
    public bool AtGameMessage { get; set; }
    /// <summary>Draws the key in a chamfered outline instead of a plain box. Default false.</summary>
    public bool KeyBox { get; set; }
    /// <summary>How a hold in progress is shown; default <see cref="InteractionProgress.Bar"/>.</summary>
    public InteractionProgress ProgressStyle { get; set; } = InteractionProgress.Bar;
    /// <summary>A picture before the prompt (for example "go round the back"); null draws none.</summary>
    public Sprite? LeadIcon { get; set; }
    /// <summary>Shows the game's pick-up lines under the prompt. Default true.</summary>
    public bool ShowPickups { get; set; } = true;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
        _version = -1;
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var game = UIKit.Interaction;
        var highlight = HighlightColour;
        var warning = AlarmColour;
        if (game.Version != _version || highlight != _highlight || warning != _warning)
        {
            _version = game.Version;
            _highlight = highlight;
            _warning = warning;
            var lit = ColorUtility.ToHtmlStringRGB(highlight);
            var red = ColorUtility.ToHtmlStringRGB(warning);
            _text = RichText.Toned(game.Text, lit, red);
            _key = RichText.Plain(RichText.StripTags(game.Key)).Trim('[', ']', ' ');
            _message = game.MessageVisible ? RichText.Toned(game.Message.Trim(), lit, red) : "";
            _extra = RichText.Toned(game.AdditionalText, lit, red);
        }
        var start = -Offset * u;
        if (AtGamePrompt && GameHeight(hud, false, out var gameHeight)) start = gameHeight;
        else if (Above) start = Offset * u - Stack(_canvas, false, u, 0f, game);
        _messageStart = AtGameMessage && GameHeight(hud, true, out var messageHeight) ? messageHeight : float.NaN;
        Stack(_canvas, true, u, start, game);
        _canvas.End();
    }

    /// <summary>The height of the game's prompt object in this element's units, read again only when the element's size, its root or the
    /// game's interaction layer is another one (no allocation otherwise); false while the game's prompt does not exist.</summary>
    private bool GameHeight(HudElement hud, bool message, out float height)
    {
        height = message ? _messageHeight : _gameHeight;
        var layer = GuiManager.InteractionLayer;
        var piece = layer != null && !layer.WasCollected ? (message ? layer.m_message : layer.m_interactPrompt) : null;
        var root = hud.Root;
        var known = message ? _messageKey : _gameKey;
        if (piece == null || piece.WasCollected || root == null) return known != 0;
        var key = (long)piece.Pointer ^ ((long)root.GetInstanceID() << 20) ^ (long)(hud.HalfHeight * 100f);
        if (key != known)
        {
            height = root.InverseTransformPoint(piece.transform.position).y;
            if (message) { _messageKey = key; _messageHeight = height; }
            else { _gameKey = key; _gameHeight = height; }
        }
        return true;
    }

    /// <summary>Lays out the prompt, the message line and the pick-up lines from <paramref name="y"/> down, drawing them only when
    /// <paramref name="draw"/>, and returns the middle height of the lowest line.</summary>
    private float Stack(HudCanvas canvas, bool draw, float u, float y, GameInteraction game)
    {
        var c = draw ? canvas : null;
        var low = y;
        var small = TextPx - 4f;
        if (game.Visible && (_text.Length > 0 || _key.Length > 0))
        {
            var colour = game.Usable ? TextColour : DisabledColour;
            var keyHeight = 28f * u;
            var keyWidth = _key.Length > 0 ? Math.Max(30f * u, canvas.TextWidth(_key, TextPx - 2f, true) + 14f * u) : 0f;
            var textWidth = canvas.TextWidth(_text, TextPx);
            var lead = LeadIcon != null ? 30f * u : 0f;
            var total = lead + keyWidth + (keyWidth > 0f && textWidth > 0f ? 10f * u : 0f) + textWidth;
            var left = -total / 2f;
            if (LeadIcon != null)
            {
                c?.Icon(LeadIcon, left + 12f * u, y, 24f * u, 24f * u, colour);
                left += lead;
            }
            if (keyWidth > 0f)
            {
                var line = game.Usable ? UIKitColors.Faded(UIKitColors.Cockpit, 0.62f) : DisabledColour;
                var cx = left + keyWidth / 2f;
                if (KeyBox) c?.Frame(cx, y, keyWidth, keyHeight, 8f * u, line);
                else
                {
                    c?.Rect(cx, y + keyHeight / 2f - 0.5f * u, keyWidth, u, line);
                    c?.Rect(cx, y - keyHeight / 2f + 0.5f * u, keyWidth, u, line);
                    c?.Rect(left + 0.5f * u, y, u, keyHeight, line);
                    c?.Rect(left + keyWidth - 0.5f * u, y, u, keyHeight, line);
                }
                c?.Text(_key, cx, y, TextPx - 2f, game.Usable ? KeyColour : DisabledColour, HudAlign.Centre, true);
                left += keyWidth + 10f * u;
            }
            if (textWidth > 0f) c?.Text(_text, left, y, TextPx, colour);
            low = y;
            var progress = game.Progress;
            if (progress >= 0f)
            {
                var p = Ease.Clamp01(progress);
                if (ProgressStyle == InteractionProgress.Underline && textWidth > 0f)
                {
                    var uy = y - TextPx * 0.62f * u;
                    c?.Rect(left + textWidth / 2f, uy, textWidth, 2f * u, UIKitColors.Faded(RingColour, 0.2f));
                    if (p > 0f) c?.Rect(left + textWidth * p / 2f, uy, textWidth * p, 2f * u, RingColour);
                }
                else
                {
                    var bar = 160f * u;
                    c?.Rect(0f, y - 22f * u, bar, 3f * u, UIKitColors.Shade);
                    if (p > 0f) c?.Rect(-bar / 2f + bar * p / 2f, y - 22f * u, bar * p, 3f * u, RingColour);
                }
            }
            y -= 32f * u;
            if (_extra.Length > 0)
            {
                c?.Text(_extra, 0f, y, small, UIKitColors.Faded(colour, 0.7f), HudAlign.Centre);
                low = y;
                y -= (small + 8f) * u;
            }
        }
        if (game.MessageVisible && (_message.Length > 0 || game.MessageProgress >= 0f))
        {
            if (!float.IsNaN(_messageStart)) y = _messageStart;
            var colour = game.MessageStyle switch
            {
                InteractionStyle.Scan => ScanColour,
                InteractionStyle.ScanAlarm => AlarmColour,
                InteractionStyle.Warning => DisabledColour,
                _ => TextColour,
            };
            if (_message.Length > 0)
            {
                c?.Text(_message, 0f, y, TextPx - 2f, colour, HudAlign.Centre);
                low = y;
                y -= (TextPx + 2f) * u;
            }
            // The scan's (or the reactor step's) progress as a thin bar, then one mark per player the scan counts, filled for those inside.
            var bar = 240f * u;
            if (game.MessageProgress >= 0f)
            {
                var p = Ease.Clamp01(game.MessageProgress);
                low = y;
                c?.Rect(0f, y, bar, 3f * u, UIKitColors.Faded(colour, 0.2f));
                if (p > 0f) c?.Rect(-bar / 2f + bar * p / 2f, y, bar * p, 3f * u, colour);
                y -= 12f * u;
            }
            var scan = UIKit.Scan;
            if (scan.Active && scan.PlayersMax > 1 && game.MessageStyle is InteractionStyle.Scan or InteractionStyle.ScanAlarm)
            {
                const float mark = 14f, step = 20f;
                var left = -(scan.PlayersMax - 1) * step * u / 2f;
                low = y;
                for (var i = 0; i < scan.PlayersMax; i++)
                {
                    var x = left + i * step * u;
                    if (i < scan.PlayersIn) c?.Plate(x, y, mark * u, 8f * u, 2f * u, colour);
                    else c?.Frame(x, y, mark * u, 8f * u, 2f * u, UIKitColors.Faded(colour, 0.45f));
                }
                y -= 16f * u;
            }
            y -= 6f * u;
        }
        if (ShowPickups)
        {
            var pickups = UIKit.Pickups;
            for (var i = 0; i < pickups.Count; i++, y -= (small + 6f) * u)
            {
                c?.Text(pickups.Text(i), 0f, y, small, UIKitColors.Faded(UIKitColors.Cockpit, 0.62f), HudAlign.Centre);
                low = y;
            }
        }
        return low;
    }
}

/// <summary>
/// The game's subtitles redrawn low in the middle of the screen: the speaker, then the line broken to <see cref="MaxWidth"/>, over an
/// optional chamfered backing with an accent bar at its left; each line slides up into place and fades out when the game's line
/// ends. Text comes from the game (<see cref="UIKit.Subtitles"/>). Sizes are 1080p pixels. Make it with <see cref="UIKitMod.AddSubtitles"/>.
/// </summary>
public sealed class HudSubtitles : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private const float InSeconds = 0.15f, OutSeconds = 0.3f;
    private readonly List<string> _lines = new();
    private HudCanvas? _canvas;
    private int _version = -1;
    private float _width = -1f, _shownAt = -10f, _endedAt = -10f;
    private bool _wasVisible;
    private string _speaker = "";

    internal HudSubtitles() { }

    public Color SpeakerColour { get; set; } = UIKitColors.Orange;
    public Color TextColour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.95f);
    /// <summary>The backing behind the text; fully transparent draws none.</summary>
    public Color Backing { get; set; } = UIKitColors.Faded(UIKitColors.Plate, 0.42f);
    /// <summary>A bar down the backing's left edge; fully transparent draws none (default).</summary>
    public Color AccentBar { get; set; } = Color.clear;
    /// <summary>The backing's corner cut; 0 (default) draws a plain rectangle.</summary>
    public float Chamfer { get; set; }
    public float TextPx { get; set; } = 20f;
    public float SpeakerPx { get; set; } = 15f;
    /// <summary>How far under the middle of the screen; default 390.</summary>
    public float Offset { get; set; } = 390f;
    public float MaxWidth { get; set; } = 900f;

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
        _version = -1;
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var game = UIKit.Subtitles;
        if (game.Version != _version || _width != MaxWidth)
        {
            if (game.Version != _version) _shownAt = now;
            _version = game.Version;
            _width = MaxWidth;
            _speaker = RichText.Plain(RichText.StripTags(game.Speaker));
            TextWrap.Lines(RichText.StripTags(game.Text), TextPx, MaxWidth, 4, _lines);
            for (var i = 0; i < _lines.Count; i++) _lines[i] = RichText.Plain(_lines[i]);
        }
        var visible = game.Visible;
        if (_wasVisible && !visible) _endedAt = now;
        _wasVisible = visible;
        var fade = visible ? 1f : 1f - Ease.Clamp01((now - _endedAt) / OutSeconds);
        if (fade > 0f && _lines.Count > 0)
        {
            var rise = 1f - Ease.OutCubic(Ease.Clamp01((now - _shownAt) / InSeconds));
            var line = (TextPx + 6f) * u;
            var speakerLine = _speaker.Length > 0 ? (SpeakerPx + 6f) * u : 0f;
            var height = _lines.Count * line + speakerLine;
            var centre = -Offset * u - rise * 8f * u;
            var alpha = fade * (1f - rise);
            var widest = 0f;
            foreach (var l in _lines) widest = Math.Max(widest, _canvas.TextWidth(l, TextPx));
            var boxWidth = widest + 36f * u;
            if (Backing.a > 0f) _canvas.Plate(0f, centre, boxWidth, height + 14f * u, Chamfer * u, UIKitColors.Faded(Backing, alpha));
            if (AccentBar.a > 0f) _canvas.Rect(-boxWidth / 2f + u, centre, 2f * u, height + 6f * u, UIKitColors.Faded(AccentBar, alpha));
            var y = centre + height / 2f;
            if (_speaker.Length > 0)
            {
                y -= speakerLine / 2f;
                _canvas.Text(_speaker, 0f, y, SpeakerPx, UIKitColors.Faded(SpeakerColour, alpha), HudAlign.Centre, true);
                y -= speakerLine / 2f;
            }
            for (var i = 0; i < _lines.Count; i++)
            {
                y -= line / 2f;
                _canvas.Text(_lines[i], 0f, y, TextPx, UIKitColors.Faded(TextColour, alpha), HudAlign.Centre);
                y -= line / 2f;
            }
        }
        _canvas.End();
    }
}

/// <summary>
/// The other players under an optional header line (a picture, a word, a rule with an end tick), one row each on a chamfered plate
/// with an optional chamfered outline and a thin bar down its left: a picture (a short dash when the callback gives none), the name
/// with a speaking mark, the health in figures at the right, the game's health as a continuous bar or as <see cref="Segments"/> slanted
/// cells (amber and red when low), the infection as a hairline under it, and a vertical cell gauge of the player's ammunition. Every
/// mod's teammate tags (<see cref="UIKitMod.AddTeammateTag"/>) add status pictures and short values before the health figures and
/// hairline shares under the health. A downed or grabbed player's row, outline and bar flash red three times, then stay red with the
/// game's word. Values come from the game (<see cref="UIKit.Teammates"/>). Sizes are 1080p pixels. Make it with
/// <see cref="UIKitMod.AddTeammates"/>.
/// </summary>
public sealed class HudTeammates : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private const int MaxRows = 8;
    private const float HeaderHeight = 22f, CellGap = 2f, CellLean = 2f, CellHeight = 5f;
    private readonly float[] _troubleSince = new float[MaxRows];
    private readonly int[] _healthShown = new int[MaxRows];
    private readonly string[] _healthText = new string[MaxRows];
    // One row's tags as read this frame; grown only when more tags are added.
    private TeammateTag[] _tags = new TeammateTag[4];
    private bool[] _tagShown = new bool[4];
    private HudCanvas? _canvas;

    internal HudTeammates()
    {
        for (var i = 0; i < MaxRows; i++) { _troubleSince[i] = -1f; _healthShown[i] = -1; _healthText[i] = ""; }
    }

    /// <summary>A picture before each player's name (for example a model mark); null, or a null result, draws a short dash.</summary>
    public Func<SNet_Player, Sprite?>? Icon { get; set; }
    /// <summary>The picture's colour; default orange.</summary>
    public Color IconColour { get; set; } = UIKitColors.Faded(UIKitColors.Orange, 0.95f);
    /// <summary>A header line above the players (for example "SQUAD"); empty draws none unless there is a <see cref="HeaderIcon"/>.</summary>
    public UIText Header { get; set; } = UIText.Empty;
    /// <summary>A small picture on the header line (a teammates icon); null draws none.</summary>
    public Sprite? HeaderIcon { get; set; }
    /// <summary>The plate behind each row; fully transparent draws none. Default plate at 34%.</summary>
    public Color RowBacking { get; set; } = UIKitColors.Faded(UIKitColors.Plate, 0.34f);
    /// <summary>A chamfered outline round each row; fully transparent (default) draws none.</summary>
    public Color RowFrame { get; set; } = Color.clear;
    /// <summary>The corner cut of a row's plate and outline; default 8.</summary>
    public float Chamfer { get; set; } = 8f;
    /// <summary>The health bar as this many slanted cells, the cell the health ends in lit in proportion; 0 (default) draws one
    /// continuous bar.</summary>
    public int Segments { get; set; }
    /// <summary>The health in figures at the row's right. Default true.</summary>
    public bool ShowNumber { get; set; } = true;
    /// <summary>The infection hairline under the health bar. Default true.</summary>
    public bool ShowInfection { get; set; } = true;
    public Color InfectionColour { get; set; } = new(0.35f, 0.85f, 0.45f, 0.9f);
    /// <summary>The ammunition cell gauge at the row's right end. Default true.</summary>
    public bool ShowAmmo { get; set; } = true;
    /// <summary>Cells of the ammunition gauge; default 4.</summary>
    public int AmmoCells { get; set; } = 4;
    /// <summary>Below this share the ammunition gauge takes <see cref="AmmoWarnColour"/>; default 0.25.</summary>
    public float AmmoWarnBelow { get; set; } = 0.25f;
    public Color AmmoWarnColour { get; set; } = UIKitColors.Amber;
    /// <summary>A row's height; default 38.</summary>
    public float RowHeight { get; set; } = 38f;
    /// <summary>From one row to the next; default 46.</summary>
    public float RowStep { get; set; } = 46f;
    /// <summary>The rows' width; default 300.</summary>
    public float Width { get; set; } = 300f;

    internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var mates = UIKit.Teammates;
        var count = Math.Min(mates.Count, MaxRows);
        var width = Width * u;
        var step = RowStep * u;
        var row = RowHeight * u;
        var header = Show(Header);
        var headed = count > 0 && (header.Length > 0 || HeaderIcon != null);
        var headerHeight = headed ? HeaderHeight * u : 0f;
        var height = headerHeight + (count > 0 ? (count - 1) * step + row : 0f);
        var (fx, fy) = HudFlow.Of(hud);
        var left = ScreenMath.Span(fx, width);
        var y = ScreenMath.Span(fy, height) + height;
        if (headed)
        {
            var hy = y - headerHeight / 2f + 2f * u;
            var x = left;
            if (HeaderIcon != null) { _canvas.Icon(HeaderIcon, x + 7f * u, hy, 12f * u, 12f * u, UIKitColors.Faded(UIKitColors.Cockpit, 0.6f)); x += 18f * u; }
            if (header.Length > 0) x += _canvas.Text(header, x, hy, 12f, UIKitColors.Faded(UIKitColors.Cockpit, 0.72f), HudAlign.Left, true) + 8f * u;
            var end = left + width - 6f * u;
            if (end > x) _canvas.Rect((x + end) / 2f, hy, end - x, u, UIKitColors.Faded(UIKitColors.Cockpit, 0.2f));
            _canvas.Rect(end + 0.5f * u, hy - 0.5f * u, u, 7f * u, UIKitColors.Faded(UIKitColors.Cockpit, 0.45f));
            y -= headerHeight;
        }
        var cut = Chamfer * u;
        for (var i = 0; i < count; i++, y -= step)
        {
            var mate = mates.Get(i);
            var cy = y - row / 2f;
            Sprite? icon = null;
            try { if (Icon != null) icon = Icon(mate.Player); }
            catch (Exception error) { Plugin.Error("ui-teammates-picture-callback-failed-turned-off", "Teammates '" + Id + "' picture callback failed and is turned off: " + error); Icon = null; }
            var trouble = mate.Downed || !mate.Alive || mate.Held == PlayerHold.Grabbed;
            if (trouble) { if (_troubleSince[i] < 0f) _troubleSince[i] = now; }
            else _troubleSince[i] = -1f;
            var flash = GaugeMath.CriticalAlpha(MeterPulse.FlashThenSteady, trouble ? now - _troubleSince[i] : -1f, now);
            var main = trouble ? UIKitColors.Faded(UIKitColors.Red, 0.95f * flash) : UIKitColors.Faded(UIKitColors.Cockpit, 0.92f);
            if (RowBacking.a > 0f) _canvas.Plate(left + width / 2f, cy, width, row, cut, RowBacking);
            if (trouble) _canvas.Frame(left + width / 2f, cy, width, row, cut, UIKitColors.Faded(UIKitColors.Red, 0.8f * flash));
            else if (RowFrame.a > 0f) _canvas.Frame(left + width / 2f, cy, width, row, cut, RowFrame);
            _canvas.Rect(left + 4f * u, cy, 2f * u, Math.Max(u, row - 16f * u), trouble ? main : UIKitColors.Faded(UIKitColors.Cockpit, 0.55f));
            var x = left + 12f * u;
            var ny = cy + 7f * u;
            if (icon != null) _canvas.Icon(icon, x + 8f * u, ny, 14f * u, 14f * u, trouble ? main : IconColour);
            else _canvas.Rect(x + 7.5f * u, ny, 9f * u, 1.5f * u, UIKitColors.Faded(UIKitColors.Cockpit, 0.42f));
            x += 22f * u;
            var after = x + _canvas.Text(mate.Name, x, ny, 14f, main) + 6f * u;
            if (mate.Speaking) { _canvas.Rect(after + 1.5f * u, ny, 2.5f * u, 9f * u, UIKitColors.Orange); after += 9f * u; }
            if (trouble)
            {
                var word = mate.Held == PlayerHold.Grabbed ? UIKit.Kit.Text("teammates.grabbed") : UIKit.Kit.Text("teammates.downed");
                _canvas.Text(word, after, ny, 12f, main, HudAlign.Left, true);
            }
            var right = left + width - 9f * u;
            if (ShowAmmo && mate.Ammo >= 0f)
            {
                _canvas.CellGauge(right - 2.5f * u, cy, 5f * u, Math.Max(u, row - 14f * u), AmmoCells, mate.Ammo, AmmoWarnBelow, UIKitColors.Faded(UIKitColors.Cockpit, 0.85f), AmmoWarnColour);
                right -= 12f * u;
            }
            if (ShowNumber)
            {
                var health = (int)MathF.Round(Ease.Clamp01(mate.Health) * 100f);
                if (health != _healthShown[i]) { _healthShown[i] = health; _healthText[i] = NumberText.Count(health); }
                right -= _canvas.Text(_healthText[i], right, ny, 13f, main, HudAlign.Right, true) + 6f * u;
            }
            // Tags' pictures and values, right to left before the health figures, never over the name.
            var tags = TeammateTags.All;
            if (_tags.Length < tags.Count) { _tags = new TeammateTag[tags.Count * 2]; _tagShown = new bool[tags.Count * 2]; }
            for (var t = 0; t < tags.Count; t++)
            {
                var source = tags[t];
                _tagShown[t] = source.Place == TeammateTagPlace.Row && source.Read(mate.Player, out _tags[t]);
                if (!_tagShown[t]) continue;
                var tag = _tags[t];
                var colour = trouble ? main : tag.Colour ?? source.Colour;
                if (!string.IsNullOrEmpty(tag.Text) && right - after > 24f * u)
                    right -= _canvas.Text(tag.Text!, right, ny, 12f, colour, HudAlign.Right, true) + 5f * u;
                if (tag.Icon != null && right - after > 16f * u)
                {
                    _canvas.Icon(tag.Icon, right - 6f * u, ny, 12f * u, 12f * u, colour);
                    right -= 17f * u;
                }
            }
            var barLeft = x;
            var barWidth = Math.Max(10f * u, left + width - (ShowAmmo ? 23f : 10f) * u - barLeft);
            var by = cy - 6f * u;
            var tone = MeterMath.Tone(mate.Health, 0.4f, 0.25f, false);
            var fill = UIKitColors.Faded(UIKitColors.Tone(tone, UIKitColors.Cockpit), 0.92f * (trouble ? flash : 1f));
            if (Segments > 0) Cells(barLeft, by, barWidth, mate.Health, fill, u);
            else Bar(barLeft, by, barWidth, 3f * u, mate.Health, fill);
            var under = by - (Segments > 0 ? CellHeight / 2f + 3f : 4f) * u;
            if (ShowInfection && mate.Infection > 0f) { Bar(barLeft, under, barWidth, 1.5f * u, mate.Infection, InfectionColour); under -= 3f * u; }
            for (var t = 0; t < tags.Count; t++)
            {
                if (!_tagShown[t] || _tags[t].Bar is not { } share) continue;
                Bar(barLeft, under, barWidth, 1.5f * u, share, _tags[t].Colour ?? tags[t].Colour);
                under -= 3f * u;
            }
        }
        _canvas.End();
    }

    private void Bar(float left, float y, float width, float height, float value, Color colour)
    {
        value = Ease.Clamp01(value);
        _canvas!.Rect(left + width / 2f, y, width, height, UIKitColors.Faded(UIKitColors.Cockpit, 0.13f));
        if (value > 0f) _canvas.Rect(left + width * value / 2f, y, width * value, height, colour);
    }

    /// <summary><see cref="Segments"/> slanted cells over <paramref name="width"/>, lit up to <paramref name="value"/>.</summary>
    private void Cells(float left, float y, float width, float value, Color colour, float u)
    {
        var n = Segments;
        var gap = CellGap * u;
        var lean = CellLean * u;
        var h = CellHeight * u;
        var cell = (width - gap * (n - 1)) / n;
        if (cell <= 0f) { Bar(left, y, width, 3f * u, value, colour); return; }
        value = Ease.Clamp01(value);
        for (var i = 0; i < n; i++)
        {
            var x = left + i * (cell + gap);
            var level = GaugeMath.Segment(value, i, n);
            _canvas!.Skew(x + cell / 2f, y, cell, h, lean, UIKitColors.Faded(UIKitColors.Cockpit, 0.12f));
            if (level > 0f) _canvas.Skew(x + cell * level / 2f, y, cell * level, h, lean * level, colour);
        }
    }
}

/// <summary>
/// A few short lines of small text read every frame (for example the zone, heading and altitude in a corner). Return the same string
/// while it is the same. With <see cref="Inline"/> the values stand side by side on one row instead of one under the other. Sizes are
/// 1080p pixels. Make it with <see cref="UIKitMod.AddLabel(string, Func{string}[])"/>.
/// </summary>
public sealed class HudLabel : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private readonly Func<string>[] _lines;
    private readonly string[] _read;
    private HudCanvas? _canvas;

    internal HudLabel(Func<string>[] lines)
    {
        _lines = lines;
        _read = new string[lines.Length];
    }

    public Color Colour { get; set; } = UIKitColors.Faded(UIKitColors.Cockpit, 0.85f);
    /// <summary>The text size; default 16.</summary>
    public float Px { get; set; } = 16f;
    /// <summary>From one line to the next; default 21.</summary>
    public float LineStep { get; set; } = 21f;
    /// <summary>Which side the lines line up on; default left.</summary>
    public HudAlign Align { get; set; } = HudAlign.Left;
    /// <summary>The values stand on one row, side by side with a thin divider between them, instead of one under the other. Empty
    /// values take no room. Default false.</summary>
    public bool Inline { get; set; }
    /// <summary>With <see cref="Inline"/>: the space between two values (the divider is in the middle); default 28.</summary>
    public float Gap { get; set; } = 28f;

    internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

    private void Read()
    {
        for (var i = 0; i < _lines.Length; i++)
        {
            try { _read[i] = _lines[i]() ?? ""; }
            catch (Exception error) { Plugin.Error("ui-label-line-failed", "Label '" + Id + "' line " + i + " failed: " + error); _lines[i] = () => ""; _read[i] = ""; }
        }
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var step = LineStep * u;
        var width = hud.Slot.Width;
        var (fx, fy) = HudFlow.Of(hud);
        var left = ScreenMath.Span(fx, width);
        Read();
        if (Inline)
        {
            var gap = Gap * u;
            float total = 0f;
            var shown = 0;
            for (var i = 0; i < _read.Length; i++)
                if (_read[i].Length > 0) total += (shown++ > 0 ? gap : 0f) + _canvas.TextWidth(_read[i], Px);
            var row = ScreenMath.Span(fy, step) + step / 2f;
            var cursor = Align == HudAlign.Right ? left + width - total : Align == HudAlign.Centre ? left + (width - total) / 2f : left;
            var first = true;
            for (var i = 0; i < _read.Length; i++)
            {
                if (_read[i].Length == 0) continue;
                if (!first)
                {
                    _canvas.Rect(cursor - gap / 2f, row, u, Px * 0.75f * u, UIKitColors.Faded(Colour, 0.4f));
                }
                first = false;
                cursor += _canvas.Text(_read[i], cursor, row, Px, Colour, HudAlign.Left) + gap;
            }
            _canvas.End();
            return;
        }
        var height = _lines.Length * step;
        var x = Align == HudAlign.Right ? left + width : Align == HudAlign.Centre ? left + width / 2f : left;
        var y = ScreenMath.Span(fy, height) + height - step / 2f;
        for (var i = 0; i < _read.Length; i++, y -= step)
            if (_read[i].Length > 0) _canvas.Text(_read[i], x, y, Px, Colour, Align);
        _canvas.End();
    }
}

public sealed partial class UIKitMod
{
    /// <summary>A compact segmented bar for the top of the screen (see <see cref="HudSegmentBar"/>). Default slot: top centre.</summary>
    /// <example><c>var bar = ui.AddSegmentBar("Armor", () =&gt; armor / maxArmor, 10); bar.Overlay = () =&gt; shield / maxShield;</c></example>
    public HudSegmentBar? AddSegmentBar(string name, Func<float> value, int segments, HudSlot? slot = null)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudSegmentBar(value, Math.Max(1, segments)), slot ?? new HudSlot(HudRegion.TopCenter, 50, 600f, 44f));
    }

    /// <summary>A half arc beside the crosshair (see <see cref="HudArcGauge"/>; region <see cref="HudRegion.World"/>).</summary>
    /// <example><c>ui.AddArcGauge("Heat", () =&gt; heat, ArcSide.Right).HigherIsWorse = true;</c></example>
    public HudArcGauge? AddArcGauge(string name, Func<float> value, ArcSide side)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudArcGauge(value, side), new HudSlot(HudRegion.World));
    }

    /// <summary>Contacts round the crosshair (see <see cref="HudBearingArc"/>; region <see cref="HudRegion.World"/>).</summary>
    /// <example><c>var arc = ui.AddBearingArc("Contacts"); arc.Begin(); arc.Add(UIKit.Bearing(enemy.position), 0.8f, EnemyAlert.Hunting);</c></example>
    public HudBearingArc? AddBearingArc(string name, int capacity = 24, HudSlot? slot = null)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudBearingArc(Math.Clamp(capacity, 1, 256)), slot ?? new HudSlot(HudRegion.World));
    }

    /// <summary>A hull status disc of same-size <paramref name="layers"/> (see <see cref="HudSilhouette"/>). Default slot: bottom left.
    /// A missing picture (null, for example an art file that was not found) is logged as an error and its layer is not drawn; the
    /// other layers keep their index.</summary>
    /// <example><c>var hull = ui.AddSilhouette("Hull", new[] { front, back, left, right }); hull.Set(Bearing.Quadrant(azimuth), 0.6f);</c></example>
    public HudSilhouette? AddSilhouette(string name, IReadOnlyList<Sprite?> layers, HudSlot? slot = null)
    {
        if (layers == null) throw new ArgumentNullException(nameof(layers));
        var id = Full(name, "HUD element");
        if (id == null) return null;
        for (var i = 0; i < layers.Count; i++)
            if (layers[i] == null) Plugin.Error("ui-silhouette-layer-has-no-picture", "Silhouette '" + id + "': layer " + i + " has no picture; that layer is not drawn.");
        return Hud.Add(id, new HudSilhouette(layers), slot ?? new HudSlot(HudRegion.BottomLeft, 0, 170f, 190f));
    }

    /// <summary>The game's objectives in the kit's style (see <see cref="HudObjectives"/>). Default slot: the notices corner.</summary>
    /// <example><c>ui.HideGameHud("Cockpit"); ui.AddObjectives("Objectives");</c></example>
    public HudObjectives? AddObjectives(string name, HudSlot? slot = null)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudObjectives(), slot ?? new HudSlot(HudRegion.Notices, 100, 480f, 160f));
    }

    /// <summary>The game's interaction prompt under the crosshair (see <see cref="HudInteraction"/>; region <see cref="HudRegion.World"/>).</summary>
    public HudInteraction? AddInteraction(string name)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudInteraction(), new HudSlot(HudRegion.World));
    }

    /// <summary>The game's subtitles low in the middle (see <see cref="HudSubtitles"/>; region <see cref="HudRegion.World"/>).</summary>
    public HudSubtitles? AddSubtitles(string name)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudSubtitles(), new HudSlot(HudRegion.World));
    }

    /// <summary>The other players (see <see cref="HudTeammates"/>). Default slot: bottom left.</summary>
    /// <example><c>var mates = ui.AddTeammates("Team"); mates.Icon = player =&gt; SuitMark(player);</c></example>
    public HudTeammates? AddTeammates(string name, HudSlot? slot = null)
    {
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudTeammates(), slot ?? new HudSlot(HudRegion.BottomLeft, 40, 400f, 220f));
    }

    /// <summary>A few short lines of small text (see <see cref="HudLabel"/>). Default slot: top left.</summary>
    /// <example><c>ui.AddLabel("Readings", () =&gt; zoneText, () =&gt; headingText);</c></example>
    public HudLabel? AddLabel(string name, params Func<string>[] lines) => AddLabel(name, null, lines);

    /// <summary>A few short lines of small text in <paramref name="slot"/> (null: the top left corner; see <see cref="HudLabel"/>).</summary>
    /// <example><c>ui.AddLabel("Readings", new HudSlot(HudRegion.TopCenter, 60, 480f, 28f), () =&gt; zoneText, () =&gt; headingText).Inline = true;</c></example>
    public HudLabel? AddLabel(string name, HudSlot? slot, params Func<string>[] lines)
    {
        if (lines == null || lines.Length == 0) throw new ArgumentException("A label needs at least one line.", nameof(lines));
        foreach (var line in lines)
            if (line == null) throw new ArgumentException("A label line is null.", nameof(lines));
        var id = Full(name, "HUD element");
        return id == null ? null : Hud.Add(id, new HudLabel((Func<string>[])lines.Clone()), slot ?? new HudSlot(HudRegion.TopLeft, 0, 320f, 21f * lines.Length + 8f));
    }
}
