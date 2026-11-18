using System;
using System.Globalization;

namespace ForgeUI;

/// <summary>When each beat of a <see cref="BootMessage"/> happens, in seconds of the power-on's own clock (0 when it starts).</summary>
public sealed class BootMessageBeats
{
    /// <summary>The ignition line draws out by <see cref="IgniteEnd"/>; the bar's brackets close in between <see cref="BracketsStart"/>
    /// and <see cref="BracketsEnd"/>; the header decodes between <see cref="HeaderStart"/> and <see cref="HeaderEnd"/>.</summary>
    public float IgniteEnd { get; init; } = 0.12f;
    public float BracketsStart { get; init; } = 0.04f;
    public float BracketsEnd { get; init; } = 0.20f;
    public float HeaderStart { get; init; } = 0.08f;
    public float HeaderEnd { get; init; } = 0.32f;

    /// <summary>The bar fills along <see cref="FillTimes"/> / <see cref="FillValues"/> (time, fraction keys, eased between them; the
    /// first time is when it starts), blinks the stall word between <see cref="StallStart"/> and <see cref="StallEnd"/>, is full at
    /// the last key, flashes for <see cref="BarFlash"/> and is gone after it.</summary>
    public float[] FillTimes { get; init; } = { 0.15f, 0.50f, 0.75f, 1.15f, 1.30f };
    public float[] FillValues { get; init; } = { 0f, 0.35f, 0.48f, 0.92f, 1f };
    public float StallStart { get; init; } = 0.50f;
    public float StallEnd { get; init; } = 0.78f;
    public float BarFlash { get; init; } = 0.08f;

    /// <summary>When each self-test line starts typing on (one per line); a line types over <see cref="CheckType"/>, its OK stamps
    /// <see cref="CheckOk"/> after it starts, the stamp's frame fades over <see cref="CheckStamp"/>; the lines fade out between
    /// <see cref="ChecksFade"/> and <see cref="ChecksGone"/>.</summary>
    public float[] CheckStarts { get; init; } = { 0.42f, 0.58f, 0.74f, 0.90f, 1.06f };
    public float CheckType { get; init; } = 0.10f;
    public float CheckOk { get; init; } = 0.12f;
    public float CheckStamp { get; init; } = 0.08f;
    public float ChecksFade { get; init; } = 1.60f;
    public float ChecksGone { get; init; } = 1.80f;

    /// <summary>The message decodes (split in two colours) from <see cref="MessageStart"/> and locks at <see cref="MessageLock"/>: a
    /// white flash over <see cref="LockFlash"/>, a frame bursting outwards over <see cref="LockBurst"/>, a shake dying over
    /// <see cref="LockShake"/>; it holds until <see cref="FoldStart"/> and folds to a line, then a dot, by <see cref="Duration"/>.</summary>
    public float MessageStart { get; init; } = 1.32f;
    public float MessageLock { get; init; } = 1.50f;
    public float LockFlash { get; init; } = 0.07f;
    public float LockBurst { get; init; } = 0.25f;
    public float LockShake { get; init; } = 0.12f;
    public float FoldStart { get; init; } = 1.90f;
    public float Duration { get; init; } = 2.2f;

    /// <summary>Powering down, on the power-down's own clock (0 when leaving begins): the power-down word decodes from scrambled glyphs
    /// between <see cref="DownStart"/> and <see cref="DownDecoded"/>, then holds, flickering, until <see cref="DownEnd"/>.</summary>
    public float DownStart { get; init; } = 0.06f;
    public float DownDecoded { get; init; } = 0.28f;
    public float DownEnd { get; init; } = 0.8f;

    public float BarStart => FillTimes[0];
    public float BarFull => FillTimes[FillTimes.Length - 1];
    public float BarGone => BarFull + BarFlash;

    /// <summary>How full the bar is at <paramref name="s"/> (0..1).</summary>
    public float BarFill(float s)
    {
        if (s <= FillTimes[0]) return 0f;
        for (var k = 1; k < FillTimes.Length; k++)
        {
            if (s > FillTimes[k]) continue;
            var t = (s - FillTimes[k - 1]) / Math.Max(1e-4f, FillTimes[k] - FillTimes[k - 1]);
            return FillValues[k - 1] + (FillValues[k] - FillValues[k - 1]) * Ease.Smoothstep(t);
        }
        return 1f;
    }
}

/// <summary>The colours and sizes of a <see cref="BootMessage"/>, in HUD units at 1080p.</summary>
public sealed class BootMessageLook
{
    /// <summary>Lines, brackets, the header, the self-test and the message.</summary>
    public HudColour Accent { get; init; } = new(0.80f, 0.94f, 1f, 1f);
    /// <summary>The bright cells, the percentage and the lock's frame.</summary>
    public HudColour Bright { get; init; } = new(0.957f, 0.965f, 0.957f, 1f);
    /// <summary>The OK stamp's flash and the lock's flash line.</summary>
    public HudColour White { get; init; } = new(1f, 1f, 1f, 1f);
    /// <summary>The two halves of the decoding message's split.</summary>
    public HudColour SplitLeft { get; init; } = new(1f, 0.29f, 0.23f, 1f);
    public HudColour SplitRight { get; init; } = new(0.25f, 0.85f, 1f, 1f);
    /// <summary>The stall word blinking over the bar.</summary>
    public HudColour Stall { get; init; } = new(1f, 0.56f, 0.17f, 1f);
    /// <summary>The dark plate behind the header and the self-test.</summary>
    public HudColour Backing { get; init; } = new(0.016f, 0.03f, 0.047f, 0.62f);
    /// <summary>The power-down word and its brackets.</summary>
    public HudColour Down { get; init; } = new(1f, 0.25f, 0.2f, 1f);

    public float MessagePx { get; init; } = 50f;
    public float HeaderPx { get; init; } = 16f;
    public float CheckPx { get; init; } = 16f;
    public float CheckRow { get; init; } = 20f;
    public float BarWidth { get; init; } = 420f;
    public int Cells { get; init; } = 24;
    public float CellHeight { get; init; } = 6f;
    public float CellGap { get; init; } = 3f;
    /// <summary>The header's height over the bar, and how far under the bar's line the first self-test line sits (the lines run
    /// downwards from there, so the message that locks on the bar's line has the self-test under it).</summary>
    public float HeaderY { get; init; } = 18f;
    public float CheckBase { get; init; } = 44f;
    /// <summary>The height of the bar's and the message's line over the element's centre (the self-test runs down from it); the power-down
    /// word stands on the centre.</summary>
    public float LineY { get; init; } = 0f;
    public float DownPx { get; init; } = 64f;
    /// <summary>The colour of the status word ending each self-test line (<see cref="BootMessageWords.Ok"/>) once its white flash is
    /// over; the line's name keeps <see cref="Accent"/>. Default: the accent.</summary>
    public HudColour? Status { get; init; }
    /// <summary>How far the locked message stands over the bar's line (the header and the bar's brackets are under it, the self-test
    /// further under that), so the message never covers them.</summary>
    public float MessageLift { get; init; } = 0f;
    /// <summary>A box in crosshair space (HUD units, x right, y up, the crosshair at 0, 0) the power-on must stay clear of: when
    /// everything it can draw would overlap it, the whole stack is raised until it clears <see cref="KeepoutGap"/> above it. Null: no
    /// keep-out. <see cref="LineY"/> is the lowest the stack goes.</summary>
    public HudBox? Keepout { get; init; }
    public float KeepoutGap { get; init; } = 12f;
    /// <summary>How far above the crosshair the element's centre is (the banner region's <see cref="HudRegions.BannerRise"/>, which is
    /// where <see cref="UIKitMod.AddBootMessage"/> puts it unless it is given another slot).</summary>
    public float OriginRise { get; init; } = HudRegions.BannerRise;
    /// <summary>The room the element asks for while anything shows (more when what it draws needs more).</summary>
    public float Width { get; init; } = 500f;
    public float Height { get; init; } = 320f;
}

/// <summary>The words of a <see cref="BootMessage"/>: the header over the bar, the word blinking while the bar stalls, the self-test
/// lines (one per <see cref="BootMessageBeats.CheckStarts"/>), the stamp each line gets, the message that locks at the end and the word
/// shown while powering down.</summary>
public sealed class BootMessageWords
{
    public string Header { get; init; } = "";
    public string Stall { get; init; } = "";
    public string[] Checks { get; init; } = Array.Empty<string>();
    public string Ok { get; init; } = "";
    public string Message { get; init; } = "";
    public string Down { get; init; } = "";
}

/// <summary>What one <see cref="BootMessageItem"/> is: a filled rectangle (centre x, y; width, height), a slanted cell, a line (from
/// x, y to w, h; thickness t), a dark plate with cut corners (centre; width, height; cut t) or a text (anchor x, y at its middle line).</summary>
internal enum BootMessageItemKind : byte { Rect, Cell, Line, Plate, Text }

internal struct BootMessageItem
{
    public BootMessageItemKind Kind;
    public bool Bold;
    /// <summary>Text only: 0 left, 1 centre, 2 right (HudAlign's order).</summary>
    public byte Align;
    public float X, Y, W, H, T, Px;
    public HudColour Colour;
    public string? Text;
}

/// <summary>
/// A power-on message above the crosshair, without Unity: an ignition line and brackets, a header decoding from scrambled glyphs over a
/// segmented bar that fills with a stall (its word blinking), self-test lines typing on above it on a dark plate with an OK stamp each,
/// then the message decoding split in two colours, locking with a flash, a frame bursting outwards and a short shake, holding and
/// folding to a dot; while powering down, the power-down word in its own colour, decoding and flickering in brackets. The beats, look and
/// words are the caller's. <see cref="Build"/> fills
/// <see cref="Items"/> in place (nothing allocates after <see cref="Prepare"/>), in HUD units at 1080p from the element's centre
/// (x right, y up).
/// </summary>
internal sealed class BootMessage
{
    public const int Capacity = 192;
    private const string Glyphs = "#%$&@*/\\<>=+0123456789ABCDEFXZ";
    private const int Variants = 3;
    private static readonly string[] Percents = MakePercents();

    public readonly BootMessageItem[] Items = new BootMessageItem[Capacity];
    private string[] _checks = Array.Empty<string>();
    private string[][] _typed = Array.Empty<string[]>(), _messageDecode = Array.Empty<string[]>(), _headerDecode = Array.Empty<string[]>(),
        _downDecode = Array.Empty<string[]>();
    private string _message = "", _ok = "", _stall = "", _downWord = "";
    // Added to every height drawn: the power-on's line (LineY) or 0 for the power-down word.
    private float _oy, _lineY = float.NaN, _roomWidth = float.NaN, _roomHeight = float.NaN;

    public BootMessage(BootMessageBeats beats, BootMessageLook look)
    {
        Beats = beats ?? throw new ArgumentNullException(nameof(beats));
        Look = look ?? throw new ArgumentNullException(nameof(look));
        if (beats.FillTimes.Length < 2 || beats.FillTimes.Length != beats.FillValues.Length) throw new ArgumentException("the bar needs at least two fill keys, as many times as values", nameof(beats));
        if (look.Cells < 1) throw new ArgumentException("the bar needs a cell", nameof(look));
    }

    public BootMessageBeats Beats { get; }
    public BootMessageLook Look { get; }
    public int Count { get; private set; }
    /// <summary>The box the message asks for; zero while it draws nothing.</summary>
    public float Width { get; private set; }
    public float Height { get; private set; }

    /// <summary>The width of a text at a size (1080p pixels) in the HUD's font; null measures with <see cref="TextWidth"/>.</summary>
    public Func<string, float, float>? Measure { get; set; }

    /// <summary>Approximate width of <paramref name="text"/> at <paramref name="px"/> in a monospace font (CJK full width).</summary>
    public static float TextWidth(string text, float px)
    {
        var w = 0f;
        foreach (var c in text) w += c >= 0x2E80 ? px : px * 0.6f;
        return w;
    }

    private float W(string text, float px) => text.Length == 0 ? 0f : Measure?.Invoke(text, px) ?? TextWidth(text, px);

    /// <summary>The words (once per showing or language), with every decoding and typing step of them made now, so drawing them never
    /// builds a string. Self-test lines beyond <see cref="BootMessageBeats.CheckStarts"/> are not shown.</summary>
    public void Prepare(BootMessageWords words)
    {
        if (words == null) throw new ArgumentNullException(nameof(words));
        _message = words.Message ?? "";
        _ok = words.Ok ?? "";
        _stall = words.Stall ?? "";
        var lines = Math.Min(words.Checks.Length, Beats.CheckStarts.Length);
        _checks = new string[lines];
        _typed = new string[lines][];
        for (var i = 0; i < lines; i++)
        {
            var text = _checks[i] = words.Checks[i] ?? "";
            var steps = new string[text.Length + 1];
            for (var n = 0; n <= text.Length; n++) steps[n] = text.Substring(0, n);
            _typed[i] = steps;
        }
        _messageDecode = Decodings(_message, 17);
        _headerDecode = Decodings(words.Header ?? "", 5);
        _downWord = words.Down ?? "";
        _downDecode = Decodings(_downWord, 29);
        _lineY = _roomWidth = _roomHeight = float.NaN;
    }

    /// <summary>The most the self-test lines drift down while fading.</summary>
    private float MaxDrop => (Beats.ChecksGone - Beats.ChecksFade) * 40f;

    /// <summary>Everything the power-on can draw (not the power-down word), in HUD units from the element's centre with the bar's line
    /// at <paramref name="lineY"/>: the header, bar and self-test with their plate, and the message with its frame at
    /// <see cref="BootMessageLook.MessageLift"/>. Call after <see cref="Prepare"/>.</summary>
    public HudBox Extent(float lineY)
    {
        var look = Look;
        var half = look.BarWidth / 2f + 15.5f;
        var top = 30f;
        var bottom = _checks.Length > 0 ? -look.CheckBase - (_checks.Length - 1) * look.CheckRow - 12f - MaxDrop : -14f;
        if (_message.Length > 0)
        {
            var tw = W(_message, look.MessagePx);
            var mHalf = Math.Max(tw / 2f * 1.35f + 18f, tw / 2f + 40f) + 1.5f;
            var hh = look.MessagePx * 0.75f * 1.35f + 1.5f;
            half = Math.Max(half, mHalf);
            top = Math.Max(top, look.MessageLift + hh);
            bottom = Math.Min(bottom, look.MessageLift - hh);
        }
        return new HudBox(-half, lineY + bottom, half, lineY + top);
    }

    /// <summary>The height of the bar's line over the element's centre once the keep-out is respected: <see cref="BootMessageLook.LineY"/>,
    /// or higher when the power-on would overlap <see cref="BootMessageLook.Keepout"/>.</summary>
    public float LineY
    {
        get
        {
            if (!float.IsNaN(_lineY)) return _lineY;
            var line = Look.LineY;
            if (Look.Keepout is { } keep)
            {
                var low = Extent(line);
                var inCrosshair = new HudBox(low.Left, low.Bottom + Look.OriginRise, low.Right, low.Top + Look.OriginRise);
                if (inCrosshair.Overlaps(keep))
                    line += keep.Top + Look.KeepoutGap - inCrosshair.Bottom;
            }
            return _lineY = line;
        }
    }

    /// <summary>Every step of <paramref name="text"/> decoding from scrambled glyphs, left to right: [fixed characters][variant].</summary>
    private static string[][] Decodings(string text, int seed)
    {
        var random = new Random(seed);
        var steps = new string[text.Length + 1][];
        var chars = new char[text.Length];
        for (var fixedCount = 0; fixedCount <= text.Length; fixedCount++)
        {
            steps[fixedCount] = new string[Variants];
            for (var v = 0; v < Variants; v++)
            {
                for (var c = 0; c < text.Length; c++)
                    chars[c] = c < fixedCount || char.IsWhiteSpace(text[c]) ? text[c] : Glyphs[random.Next(Glyphs.Length)];
                steps[fixedCount][v] = new string(chars);
            }
        }
        return steps;
    }

    private static string[] MakePercents()
    {
        var all = new string[101];
        for (var i = 0; i <= 100; i++) all[i] = i.ToString("000", CultureInfo.InvariantCulture) + "%";
        return all;
    }

    private static string Decoded(string[][] steps, float k, float clock, float rate)
    {
        if (steps.Length == 0) return "";
        var n = steps.Length - 1;
        var fixedCount = Math.Clamp((int)(n * Math.Clamp(k, 0f, 1f)), 0, n);
        return steps[fixedCount][(int)(Math.Max(0f, clock) * rate) % Variants];
    }

    /// <summary>The message at <paramref name="boot"/> seconds into the power-on (negative or past the end: nothing), or the power-down
    /// word at <paramref name="down"/> seconds into the power-down (negative when not powering down; powering down wins over the power-on).</summary>
    public void Build(float boot, float down)
    {
        Count = 0;
        _oy = 0f;
        if (down >= 0f) DownWord(down);
        else if (boot >= 0f && boot < Beats.Duration)
        {
            var line = LineY;
            _oy = line;
            Group(boot);
            _oy = line + Look.MessageLift;
            Message(boot);
        }
        var any = Count > 0;
        if (!any) { Width = Height = 0f; return; }
        // The element is centred on its slot: it asks for room on both sides of the centre for everything it can draw.
        if (down >= 0f) { Width = Look.Width; Height = Look.Height; return; }
        if (float.IsNaN(_roomWidth))
        {
            var extent = Extent(LineY);
            _roomWidth = Math.Max(Look.Width, 2f * Math.Max(-extent.Left, extent.Right));
            _roomHeight = Math.Max(Look.Height, 2f * Math.Max(-extent.Bottom, extent.Top));
        }
        Width = _roomWidth;
        Height = _roomHeight;
    }

    // ---- the header, the bar and the self-test ------------------------------------------------------------------------------------

    private void Group(float b)
    {
        var beats = Beats;
        var look = Look;
        float left = -look.BarWidth / 2f, right = look.BarWidth / 2f;
        var barShown = b < beats.BarGone;
        var checksShown = _checks.Length > 0 && b >= beats.CheckStarts[0] && b < beats.ChecksGone;
        var fade = b > beats.ChecksFade ? 1f - Ease.Smoothstep((b - beats.ChecksFade) / Math.Max(1e-4f, beats.ChecksGone - beats.ChecksFade)) : 1f;
        // Fading, the self-test lines drift down, away from the message locking on the bar's line.
        var drop = b > beats.ChecksFade ? (b - beats.ChecksFade) * 40f : 0f;
        var lines = 0;
        for (var i = 0; i < _checks.Length; i++) if (b >= beats.CheckStarts[i]) lines++;

        // The dark plate behind the header, the bar and the self-test under it: it grows with the lines and fades with them.
        if (b >= beats.HeaderStart && (barShown || checksShown))
        {
            var top = barShown ? look.HeaderY + 12f : -look.CheckBase + 13f - drop;
            var bottom = checksShown && lines > 0 ? -look.CheckBase - (lines - 1) * look.CheckRow - drop - 12f : -14f;
            Plate(0f, (top + bottom) / 2f, look.BarWidth + 24f, top - bottom, 10f, look.Backing, checksShown ? fade : 1f);
        }

        if (barShown)
        {
            Rect(0f, 0f, look.BarWidth * Ease.Smoothstep(b / Math.Max(1e-4f, beats.IgniteEnd)), 2f, look.Accent, 0.6f);
            if (b >= beats.BracketsStart)
            {
                var k = Ease.Smoothstep((b - beats.BracketsStart) / Math.Max(1e-4f, beats.BracketsEnd - beats.BracketsStart));
                const float top = 26f, bottom = -12f;
                for (var side = -1; side <= 1; side += 2)
                {
                    var x = side < 0 ? left - 14f : right + 14f;
                    var inward = -side * 22f * k;
                    Line(x, top, x, bottom, 2.5f, look.Accent, 0.86f);
                    Line(x, top, x + inward, top, 2.5f, look.Accent, 0.86f);
                    Line(x, bottom, x + inward, bottom, 2.5f, look.Accent, 0.86f);
                }
            }
            var fill = beats.BarFill(b);
            if (b >= beats.HeaderStart)
            {
                var k = Ease.Smoothstep((b - beats.HeaderStart) / Math.Max(1e-4f, beats.HeaderEnd - beats.HeaderStart));
                Text(Decoded(_headerDecode, k, b, 40f), left, look.HeaderY, look.HeaderPx, 0, look.Accent, 0.95f, false);
                Text(Percents[Math.Clamp((int)MathF.Round(fill * 100f), 0, 100)], right, look.HeaderY, look.HeaderPx, 2, look.Bright, 0.95f, false);
                if (b > beats.StallStart && b < beats.StallEnd && (int)(b * 12f) % 2 == 0) Text(_stall, 0f, look.HeaderY, look.HeaderPx - 2f, 1, look.Stall, 0.9f, true);
            }
            if (b >= beats.BarStart)
            {
                var cells = look.Cells;
                var cell = (look.BarWidth - look.CellGap * (cells - 1)) / cells;
                var lit = fill * cells;
                var flash = b >= beats.BarFull ? 1f - (b - beats.BarFull) / Math.Max(1e-4f, beats.BarFlash) : 0f;
                for (var i = 0; i < cells; i++)
                {
                    var x = left + i * (cell + look.CellGap) + cell / 2f;
                    if (i + 1 <= lit) Cell(x, 0f, cell, look.CellHeight, flash > 0f || i + 1 > lit - 1f && b < beats.BarFull ? look.Bright : look.Accent, 0.92f);
                    else if (i < lit) Cell(x, 0f, cell, look.CellHeight, look.Bright, 0.92f * (lit - i));
                    else Cell(x, 0f, cell, look.CellHeight, look.Accent, 0.15f);
                }
            }
        }

        if (!checksShown) return;
        for (var i = 0; i < lines; i++)
        {
            var at = beats.CheckStarts[i];
            var y = -look.CheckBase - i * look.CheckRow - drop;
            var label = _checks[i];
            var steps = _typed[i];
            var n = (int)(label.Length * Math.Min(1f, (b - at) / Math.Max(1e-4f, beats.CheckType)));
            var typed = steps[Math.Clamp(n, 0, label.Length)];
            Text(typed, left, y, look.CheckPx, 0, look.Accent, 0.9f * fade, false);
            if (n < label.Length) Rect(left + W(typed, look.CheckPx) + 6f, y, 8f, look.CheckPx * 0.85f, look.Bright, 0.85f * fade);
            if (b < at + beats.CheckOk) continue;
            var okWidth = W(_ok, look.CheckPx);
            Line(left + W(label, look.CheckPx) + 10f, y, right - okWidth - 14f, y, 1f, look.Accent, 0.28f * fade);
            var stamp = 1f - Math.Clamp((b - at - beats.CheckOk) / Math.Max(1e-4f, beats.CheckStamp), 0f, 1f);
            Text(_ok, right, y, look.CheckPx, 2, stamp > 0f ? look.White : look.Status ?? look.Accent, fade, true);
            if (stamp > 0f)
            {
                float x0 = right - okWidth - 7f, x1 = right + 5f, y0 = y - 11f, y1 = y + 11f;
                Line(x0, y0, x1, y0, 2f, look.White, stamp);
                Line(x1, y0, x1, y1, 2f, look.White, stamp);
                Line(x1, y1, x0, y1, 2f, look.White, stamp);
                Line(x0, y1, x0, y0, 2f, look.White, stamp);
            }
        }
    }

    // ---- the message ----------------------------------------------------------------------------------------------------------------

    private void Message(float b)
    {
        var beats = Beats;
        var look = Look;
        if (b < beats.MessageStart || _message.Length == 0) return;
        var px = look.MessagePx;
        var tw = W(_message, px);
        if (b < beats.MessageLock)
        {
            // Decoding, the only two-colour split of the power-on.
            var k = Ease.Smoothstep((b - beats.MessageStart) / Math.Max(1e-4f, beats.MessageLock - beats.MessageStart));
            var text = Decoded(_messageDecode, k, b, 60f);
            var split = 10f * (1f - k);
            Text(text, -split, 0f, px, 1, look.SplitLeft, 0.55f, true);
            Text(text, split, 0f, px, 1, look.SplitRight, 0.55f, true);
            Text(text, 0f, 0f, px, 1, look.Accent, 0.95f, true);
            return;
        }
        if (b < beats.FoldStart)
        {
            var age = b - beats.MessageLock;
            var shake = 3f * Math.Max(0f, 1f - age / Math.Max(1e-4f, beats.LockShake)) * ((int)(age * 90f) % 2 == 1 ? 1f : -1f);
            var white = Math.Max(0f, 1f - age / Math.Max(1e-4f, beats.LockFlash));
            Text(_message, shake, 0f, px * 1.06f, 1, look.Accent, 0.2f, true);
            Text(_message, shake, 0f, px, 1, look.Accent.Mix(look.White, white), 1f, true);
            if (age < beats.LockBurst)
            {
                var g = 1f + 0.35f * Ease.Smoothstep(age / beats.LockBurst);
                float hw = tw / 2f * g + 18f, hh = px * 0.75f * g, a = 1f - age / beats.LockBurst;
                Line(-hw, -hh, hw, -hh, 2.5f, look.Bright, a);
                Line(hw, -hh, hw, hh, 2.5f, look.Bright, a);
                Line(hw, hh, -hw, hh, 2.5f, look.Bright, a);
                Line(-hw, hh, -hw, -hh, 2.5f, look.Bright, a);
                if (age < 0.06f) Line(-tw / 2f - 40f, -px * 0.62f, tw / 2f + 40f, -px * 0.62f, 3f, look.White, 1f);
            }
            for (var side = -1; side <= 1; side += 2)
            {
                var x = side * (tw / 2f + 26f);
                var half = px * 0.55f;
                Line(x, -half, x, half, 2.5f, look.Accent, 0.8f);
                Line(x, half, x - side * 14f, half, 2.5f, look.Accent, 0.8f);
                Line(x, -half, x - side * 14f, -half, 2.5f, look.Accent, 0.8f);
            }
            return;
        }
        // The fold: the message squashes into a line, the line shrinks to a dot.
        var u = (b - beats.FoldStart) / Math.Max(1e-4f, beats.Duration - beats.FoldStart);
        if (u < 0.4f)
        {
            var squash = 1f - Ease.Smoothstep(u / 0.4f);
            Text(_message, 0f, 0f, px, 1, look.Accent, squash, true);
            Rect(0f, 0f, tw, 2f + 6f * (1f - squash), look.Bright, 1f);
        }
        else
        {
            var w = tw / 2f * (1f - Ease.Smoothstep((u - 0.4f) / 0.5f));
            Rect(0f, 0f, 2f * w + 6f, 4f, look.Bright, 1f);
        }
    }

    // ---- items ----------------------------------------------------------------------------------------------------------------------

    private ref BootMessageItem Next()
    {
        if (Count >= Capacity) throw new InvalidOperationException("the power-on message is full");
        ref var item = ref Items[Count++];
        item = default;
        return ref item;
    }

    private void Rect(float cx, float cy, float w, float h, HudColour c, float alpha)
    {
        if (alpha <= 0f || w <= 0f) return;
        ref var i = ref Next();
        i.Kind = BootMessageItemKind.Rect; i.X = cx; i.Y = cy + _oy; i.W = w; i.H = h; i.Colour = c.Faded(alpha);
    }

    private void Cell(float cx, float cy, float w, float h, HudColour c, float alpha)
    {
        if (alpha <= 0f) return;
        ref var i = ref Next();
        i.Kind = BootMessageItemKind.Cell; i.X = cx; i.Y = cy + _oy; i.W = w; i.H = h; i.Colour = c.Faded(alpha);
    }

    private void Plate(float cx, float cy, float w, float h, float cut, HudColour c, float alpha)
    {
        if (alpha <= 0f || h <= 0f) return;
        ref var i = ref Next();
        i.Kind = BootMessageItemKind.Plate; i.X = cx; i.Y = cy + _oy; i.W = w; i.H = h; i.T = cut; i.Colour = c.Faded(alpha);
    }

    private void Line(float x0, float y0, float x1, float y1, float t, HudColour c, float alpha)
    {
        if (alpha <= 0f) return;
        ref var i = ref Next();
        i.Kind = BootMessageItemKind.Line; i.X = x0; i.Y = y0 + _oy; i.W = x1; i.H = y1 + _oy; i.T = t; i.Colour = c.Faded(alpha);
    }

    private void Text(string text, float x, float y, float px, byte align, HudColour c, float alpha, bool bold)
    {
        if (text.Length == 0 || alpha <= 0f) return;
        ref var i = ref Next();
        i.Kind = BootMessageItemKind.Text; i.X = x; i.Y = y + _oy; i.Px = px; i.Align = align; i.Colour = c.Faded(alpha); i.Text = text; i.Bold = bold;
    }

    // ---- powering down ----------------------------------------------------------------------------------------------------------

    /// <summary>The power-down word <paramref name="d"/> seconds into the power-down: on a dark plate in brackets, decoding with a split
    /// in two shades of its colour, then holding with a short flicker until <see cref="BootMessageBeats.DownEnd"/>.</summary>
    private void DownWord(float d)
    {
        var beats = Beats;
        var look = Look;
        if (_downWord.Length == 0 || d < beats.DownStart || d >= beats.DownEnd) return;
        var px = look.DownPx;
        var tw = W(_downWord, px);
        var k = Ease.Smoothstep((d - beats.DownStart) / Math.Max(1e-4f, beats.DownDecoded - beats.DownStart));
        var text = Decoded(_downDecode, k, d, 50f);
        // Two quick drop-outs while it holds, like a failing feed.
        var held = d - beats.DownDecoded;
        var alpha = held > 0f && (int)(held * 22f) % 7 == 3 ? 0.35f : 1f;
        float hw = tw / 2f + 30f, hh = px * 0.72f;
        Plate(0f, 0f, 2f * hw + 16f, 2f * hh, 10f, look.Backing, alpha);
        var split = 7f * (1f - k) + 2f;
        Text(text, -split, 0f, px, 1, look.Down.Mix(look.White, 0.25f), 0.45f * alpha, true);
        Text(text, split, 0f, px, 1, look.Down.Mix(look.Backing, 0.5f), 0.6f * alpha, true);
        Text(text, 0f, 0f, px, 1, look.Down, alpha, true);
        for (var side = -1; side <= 1; side += 2)
        {
            var x = side * hw;
            Line(x, -hh, x, hh, 3f, look.Down, 0.9f * alpha);
            Line(x, hh, x - side * 18f, hh, 3f, look.Down, 0.9f * alpha);
            Line(x, -hh, x - side * 18f, -hh, 3f, look.Down, 0.9f * alpha);
        }
    }
}
