using System;

namespace ForgeVisual;

/// <summary>The sprite pool's overdraw budget, as plain numbers. Many big translucent sprites in front of the camera cost the pixels
/// drawn over each other, not the sprite count; the budget is counted in screens (one sprite covering the whole screen is 1). Sprites
/// of a sheddable sheet (smoke) are the part given up past the budget, the lowest of their stable draws first, fading over a short band;
/// fire, flashes and sparks are never shed.</summary>
internal static class Overdraw
{
    /// <summary>Screens of translucent overdraw the pooled sprites may add together.</summary>
    internal const float ScreenBudget = 5f;

    /// <summary>The most of the sheddable coverage that is given up.</summary>
    internal const float MaxShed = 0.85f;

    private const float MinDistance = 0.3f, Aspect = 16f / 9f, FadeBand = 0.15f;

    /// <summary>The fraction of the screen a camera-facing square of <paramref name="size"/> metres covers at <paramref name="distance"/>
    /// metres for a camera whose vertical half field of view has tangent <paramref name="tanHalfFov"/>; at most 1.</summary>
    internal static float Coverage(float size, float distance, float tanHalfFov)
    {
        var height = 2f * Math.Max(distance, MinDistance) * Math.Max(tanHalfFov, 0.01f);
        var fraction = size * size / (height * height * Aspect);
        return fraction > 1f ? 1f : fraction;
    }

    /// <summary>The fraction of the sheddable coverage to give up so the total comes down to <paramref name="budget"/>.</summary>
    internal static float Shed(float total, float sheddable, float budget)
    {
        if (total <= budget || sheddable <= 0f) return 0f;
        return Math.Min(MaxShed, (total - budget) / sheddable);
    }

    /// <summary>The opacity factor of a sheddable sprite with the stable draw <paramref name="cut"/> in [0, 1).</summary>
    internal static float Fade(float cut, float shed)
    {
        if (shed <= 0f) return 1f;
        var k = (cut - shed) / FadeBand;
        return k <= 0f ? 0f : k >= 1f ? 1f : k;
    }
}

/// <summary>A small fast random source (xorshift): effects draw thousands of numbers a second and need no quality beyond looking
/// random; nothing allocates.</summary>
internal sealed class FxRandom
{
    private uint _state;

    internal FxRandom(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

    /// <summary>In [0, 1).</summary>
    internal float Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return (_state >> 8) * (1f / 16777216f);
    }

    internal float Range(float low, float high) => low + (high - low) * Next();
}

/// <summary>An emission rate kept as a debt, so a rate per second gives the right count over any frame times without a timer
/// per emitter: each frame adds rate x seconds and the whole part is what to emit now.</summary>
internal struct RateDebt
{
    private float _debt;

    /// <summary>How many to emit for a frame of <paramref name="seconds"/> at <paramref name="perSecond"/>.</summary>
    internal int Take(float perSecond, float seconds)
    {
        if (perSecond <= 0f || seconds <= 0f) return 0;
        _debt += perSecond * seconds;
        var whole = (int)_debt;
        _debt -= whole;
        return whole;
    }

    internal void Reset() => _debt = 0f;
}
