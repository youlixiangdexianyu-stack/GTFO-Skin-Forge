using System;
using System.Globalization;

namespace ForgeUI;

/// <summary>The slider rows a mod adds to the game's settings pages (<see cref="SettingsPages.AddAudioSliders"/>): a copy of the game's slider row whose handle moves between the row's
/// two ends (<c>m_handleLocalXPosMinMax</c>) and whose value is the mod's value, 0 to 1. A drag or step moves the handle by
/// <c>-delta</c> as the game's own <c>DoScrollDelta</c> does.</summary>
internal static class SliderMath
{
    /// <summary>The handle position after a drag or step of <paramref name="delta"/>, kept between the ends.</summary>
    internal static float Step(float position, float delta, float left, float right)
        => float.IsFinite(position) && float.IsFinite(delta) ? Math.Clamp(position - delta, Math.Min(left, right), Math.Max(left, right)) : left;

    /// <summary>The value at handle position <paramref name="position"/>: 0 at the left end, 1 at the right.</summary>
    internal static float ValueAt(float position, float left, float right)
        => right - left is var span && span > 1e-6f && float.IsFinite(position) ? Math.Clamp((position - left) / span, 0, 1) : 0;

    /// <summary>The handle position showing <paramref name="value"/>.</summary>
    internal static float PositionOf(float value, float left, float right)
        => left + (right - left) * (float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0);

    /// <summary>The value written next to the handle: whole percent.</summary>
    internal static string Text(float value) => ((int)MathF.Round((float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0) * 100)).ToString(CultureInfo.InvariantCulture);
}
