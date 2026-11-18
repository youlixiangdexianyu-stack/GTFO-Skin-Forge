using System;

namespace ForgeUI;

/// <summary>Cooldown arithmetic and its text.</summary>
internal static class CooldownMath
{
    /// <summary>How many sectors a cooldown circle is drawn in.</summary>
    public const int PieSteps = 48;

    /// <summary>The share of the cooldown still to run, 0..1 (0 when there is no cooldown).</summary>
    public static float Remaining01(float remaining, float total) => total > 0f ? Ease.Clamp01(remaining / total) : 0f;

    /// <summary>The circle's sector count for a remaining share: 0 when done, <see cref="PieSteps"/> when it has just started. Any
    /// time left shows at least one sector.</summary>
    public static int PieStep(float remaining01)
    {
        if (remaining01 <= 0f) return 0;
        return Math.Clamp((int)MathF.Ceiling(remaining01 * PieSteps - 1e-4f), 1, PieSteps);
    }
}

/// <summary>Small numbers as text without allocating: every string is made once and handed out again.</summary>
public static class NumberText
{
    /// <summary><paramref name="digits"/> with every character the same width (TextMeshPro rich text), so changing digits do not shift;
    /// call it when the value changes, not every frame.</summary>
    public static string Tabular(string digits) => "<mspace=0.6em>" + digits + "</mspace>";

    private const int WholeLimit = 1000;
    private static readonly string[] Whole = new string[WholeLimit];
    private static readonly string[] Tenths = new string[100];
    private static readonly string[] Percents = new string[101];
    private static readonly string[] MeterTexts = new string[WholeLimit];

    private static string Make(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The integer as text (cached up to 999; above that the number is formatted each time).</summary>
    public static string Count(int n)
    {
        if (n < 0 || n >= WholeLimit) return Make(n);
        return Whole[n] ??= Make(n);
    }

    /// <summary>Time left for a countdown: whole seconds rounded up from 10 on, tenths below ("9.4").</summary>
    public static string Left(float seconds)
    {
        if (seconds <= 0f) return Count(0);
        var tenths = (int)MathF.Ceiling(seconds * 10f - 1e-4f);
        if (tenths >= 100) return Count((int)MathF.Ceiling(seconds - 1e-4f));
        return Tenths[tenths] ??= Make(tenths / 10) + "." + Make(tenths % 10);
    }

    /// <summary>Health taken by a hit: tenths below 10 ("4.5"), whole numbers from 10 on (rounded).</summary>
    public static string Damage(float amount)
    {
        var tenths = (int)MathF.Round(Math.Max(0f, amount) * 10f);
        if (tenths >= 100) return Count((int)MathF.Round(amount));
        return Tenths[tenths] ??= Make(tenths / 10) + "." + Make(tenths % 10);
    }

    /// <summary>"73%" for 0..100 (clamped).</summary>
    public static string Percent(int n)
    {
        n = Math.Clamp(n, 0, 100);
        return Percents[n] ??= Make(n) + "%";
    }

    /// <summary>"42 m" (cached up to 999 m; above that formatted each time).</summary>
    public static string Meters(int n)
    {
        if (n < 0) n = 0;
        if (n >= WholeLimit) return Make(n) + " m";
        return MeterTexts[n] ??= Make(n) + " m";
    }
}

/// <summary>How wide a text is, estimated for sizing backgrounds: the HUD font is monospaced (about 0.6 of its size per character),
/// and the CJK characters of its fallback fonts take a full size each.</summary>
internal static class TextWidth
{
    public static float Estimate(string text, float px)
    {
        var width = 0f;
        foreach (var c in text) width += c < 0x2E80 ? 0.6f : 1f;
        return width * px;
    }
}
