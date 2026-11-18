using System;
using System.Globalization;

namespace ForgeUI;

/// <summary>Which point of the screen a <see cref="HudPlacement"/> is measured from; on a wider or narrower screen the element stays with
/// that edge.</summary>
public enum HudAnchor : byte
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Middle,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

/// <summary>What a <see cref="HudPlaced"/> draws.</summary>
public enum HudPlacedKind : byte
{
    /// <summary>Only its text, one line per line of <see cref="HudPlaced.Text"/>.</summary>
    Text,
    /// <summary>A thin filled line, with the text before the first placeholder at its left and the rest at its right.</summary>
    Bar,
    /// <summary>One cell per unit of the maximum along the bottom of the box, the text above them.</summary>
    Segments,
    /// <summary>The value as a clock (seconds) at full size, with the text before the first placeholder small above it.</summary>
    Time,
}

/// <summary>An author-chosen box on the screen: <see cref="X"/> and <see cref="Y"/> are 1080p pixels from the anchor point (x right, y
/// down) to the same point of the box, so an element anchored bottom with y = -128 has its bottom edge 128 pixels above the screen's.
/// Sizes follow the screen's height: at any resolution the box is the same share of it.</summary>
public readonly struct HudPlacement
{
    /// <summary>A 1080p pixel in HUD units on a screen whose half height is <paramref name="halfHeight"/>.</summary>
    public static float Unit(float halfHeight) => halfHeight / 540f;

    public HudPlacement(HudAnchor anchor, float x, float y, float width, float height)
    {
        Anchor = anchor;
        X = x;
        Y = y;
        Width = Math.Max(0f, width);
        Height = Math.Max(0f, height);
    }

    public HudAnchor Anchor { get; }

    public float X { get; }

    public float Y { get; }

    public float Width { get; }

    public float Height { get; }

    /// <summary>The anchor across the screen: 0 left, 0.5 middle, 1 right.</summary>
    public float AnchorX => (int)Anchor % 3 / 2f;

    /// <summary>The anchor down the screen: 0 top, 0.5 middle, 1 bottom.</summary>
    public float AnchorY => (int)Anchor / 3 / 2f;

    /// <summary>The box in HUD units from the middle of the screen (x right, y up).</summary>
    public HudBox Box(float halfWidth, float halfHeight)
    {
        var u = Unit(halfHeight);
        var left = (2f * AnchorX - 1f) * halfWidth + (X - AnchorX * Width) * u;
        var top = (1f - 2f * AnchorY) * halfHeight - (Y - AnchorY * Height) * u;
        return new HudBox(left, top - Height * u, left + Width * u, top);
    }
}

/// <summary>What a <see cref="HudPlaced"/> shows: its value, carried forward by a rate, and the whole numbers it reads as. A picture is
/// drawn again only when these readings differ from the last ones.</summary>
internal struct PlacedReading : IEquatable<PlacedReading>
{
    internal long Value, Maximum, Clock;
    internal int Percent;
    internal bool Low;

    public readonly bool Equals(PlacedReading other) =>
        Value == other.Value && Maximum == other.Maximum && Clock == other.Clock && Percent == other.Percent && Low == other.Low;

    /// <summary>The share filled, 0..1: the percent read, so a bar moves only when the reading does.</summary>
    internal readonly float Fill => Percent / 100f;

    /// <summary>The readings of <paramref name="value"/> out of <paramref name="maximum"/> (none: the value is the share itself).</summary>
    internal static PlacedReading Of(double value, double maximum, double lowBelow) => new()
    {
        Value = Whole(value),
        Maximum = Whole(maximum),
        Clock = HudClock.Seconds(value),
        Percent = (int)Math.Round(Math.Clamp(maximum > 0d ? value / maximum : value, 0d, 1d) * 100d),
        Low = value < lowBelow,
    };

    /// <summary>The value <paramref name="elapsed"/> seconds after it was set at <paramref name="rate"/> per second; it stops at 0 falling
    /// and at the maximum (if any) rising.</summary>
    internal static double Carried(double value, double maximum, double rate, double elapsed)
    {
        var now = value + rate * elapsed;
        if (rate < 0d) return Math.Min(value, Math.Max(0d, now));
        return maximum > 0d ? Math.Max(value, Math.Min(maximum, now)) : now;
    }

    private static long Whole(double n) => double.IsFinite(n) ? (long)Math.Round(n) : 0L;
}

/// <summary>A time as a clock.</summary>
public static class HudClock
{
    /// <summary>The whole seconds a clock shows: fractions round up, so it reads 0 only once the time is really spent; a negative or
    /// non-finite time reads 0.</summary>
    public static long Seconds(double seconds) => double.IsFinite(seconds) && seconds > 0d ? (long)Math.Ceiling(seconds) : 0L;

    /// <summary><c>MM:SS</c>, and <c>H:MM:SS</c> from one hour.</summary>
    public static string Text(double seconds)
    {
        var whole = Seconds(seconds);
        long hours = whole / 3600L, minutes = whole / 60L % 60L, rest = whole % 60L;
        var clock = minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + rest.ToString("00", CultureInfo.InvariantCulture);
        return hours > 0L ? hours.ToString(CultureInfo.InvariantCulture) + ":" + clock : clock;
    }
}

/// <summary>The placeholders a <see cref="HudPlaced"/> text may hold, each under its English and Chinese name (the language files of a mod
/// can use either): the value, the maximum, the percent and the clock.</summary>
public static class HudPlacedText
{
    private static readonly string[] Value = { "{value}", "{数值}" }, Maximum = { "{max}", "{最大值}" }, Percent = { "{percent}", "{百分比}" }, Clock = { "{time}", "{时间}" };

    /// <summary>Where the first placeholder of any kind starts, or -1.</summary>
    public static int FirstPlaceholder(string text)
    {
        var first = -1;
        foreach (var names in new[] { Value, Maximum, Percent, Clock })
            foreach (var name in names)
            {
                var at = text.IndexOf(name, StringComparison.Ordinal);
                if (at >= 0 && (first < 0 || at < first)) first = at;
            }
        return first;
    }

    /// <summary><paramref name="text"/> with its placeholders filled in.</summary>
    public static string Fill(string text, long value, long maximum, int percent, string clock)
    {
        if (text.IndexOf('{') < 0) return text;
        return Put(Put(Put(Put(text, Value, value.ToString(CultureInfo.InvariantCulture)), Maximum, maximum.ToString(CultureInfo.InvariantCulture)),
            Percent, percent.ToString(CultureInfo.InvariantCulture) + "%"), Clock, clock);
    }

    private static string Put(string text, string[] names, string shown)
    {
        foreach (var name in names) text = text.Replace(name, shown);
        return text;
    }
}
