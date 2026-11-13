using System;
using System.Globalization;

namespace ForgeUI;

/// <summary>The pure parts of a world marker that counts down to a moment: the label's rich text and where the marker stands beside
/// the thing it marks. See <see cref="NavMarkerHandle"/>.</summary>
internal static class Countdown
{
    /// <summary>The label turns red and grows over the last this many seconds.</summary>
    internal const float UrgentSeconds = 3f;

    internal const string UrgentHex = "FF5A3C", NameHex = "D5DEE4";

    /// <summary>Standing closer than this to the marked point, the marker stays this far ahead, where it can be read.</summary>
    internal const float NearReach = 2.4f;

    /// <summary>The remaining time rounded to the tenth of a second the label shows. Single-to-decimal keeps the seven significant
    /// digits the "0.0" formatter uses; rounding the raw binary float would disagree near values such as 1.15f.</summary>
    internal static decimal Tenth(float remaining) => decimal.Round((decimal)Math.Max(0f, remaining), 1, MidpointRounding.AwayFromZero);

    internal static bool Urgent(float remaining) => remaining <= UrgentSeconds;

    /// <summary>The name small, spaced and pale.</summary>
    internal static string NamePart(string name) =>
        "<color=#" + NameHex + "><size=78%><cspace=0.06em><noparse>" + name.ToUpperInvariant() + "</noparse></cspace></size></color>";

    /// <summary>The seconds in monospaced digits so the number does not jitter as it counts; the point keeps its own narrow width.
    /// Over the last seconds the digits grow.</summary>
    internal static string Digits(float remaining)
    {
        var text = Math.Max(0f, remaining).ToString("0.0", CultureInfo.InvariantCulture);
        var point = text.IndexOf('.');
        return "<size=" + (Urgent(remaining) ? "150" : "128") + "%><b><mspace=0.58em>" + text.Substring(0, point) + "</mspace>.<mspace=0.58em>"
            + text.Substring(point + 1) + "</mspace></b></size>";
    }

    /// <summary>The whole label: the name, then <paramref name="seconds"/> (the digits with their unit) small, in the accent colour or,
    /// over the last seconds, red.</summary>
    internal static string Label(string namePart, float remaining, string accentHex, string seconds) =>
        namePart + "<space=0.8em><color=#" + (Urgent(remaining) ? UrgentHex : accentHex) + "><size=74%>" + seconds + "</size></color>";

    /// <summary>Where the marker stands for a thing at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) seen from an eye
    /// at (<paramref name="eyeX"/>, <paramref name="eyeY"/>, <paramref name="eyeZ"/>) looking along (<paramref name="forwardX"/>,
    /// <paramref name="forwardZ"/>): to the eye's right and a little under eye level, set apart <paramref name="gap"/> metres per metre of
    /// distance so the text keeps the same clear gap on screen near and far. Closer than <see cref="NearReach"/> it stays that far
    /// ahead along the line to the thing instead of sliding beside or behind the eye.</summary>
    internal static (float X, float Y, float Z) Beside(float eyeX, float eyeY, float eyeZ, float forwardX, float forwardZ, float x, float y, float z, float gap)
    {
        var dx = x - eyeX;
        var dz = z - eyeZ;
        var distance = MathF.Sqrt(dx * dx + dz * dz);
        float ax, az;
        if (distance > 0.05f) { ax = dx / distance; az = dz / distance; }
        else
        {
            var length = MathF.Sqrt(forwardX * forwardX + forwardZ * forwardZ);
            if (length * length > 1e-4f) { ax = forwardX / length; az = forwardZ / length; }
            else { ax = 0f; az = 1f; }
        }
        var reach = MathF.Max(distance, NearReach);
        // right = up x away, with up = (0, 1, 0).
        var rx = az;
        var rz = -ax;
        var lift = Math.Clamp(eyeY - y - 0.15f, 1.0f, 2.4f) + reach * 0.02f;
        var side = MathF.Max(0.5f, reach * gap);
        return (eyeX + ax * reach + rx * side, y + lift, eyeZ + az * reach + rz * side);
    }
}
