using System;

namespace ForgeUI;

/// <summary>
/// The flashes of a <see cref="HitEdge"/>, without Unity: at most <see cref="Slots"/> at once, each an azimuth and a strength
/// that rises quickly and fades. A new hit within <see cref="MergeDegrees"/> of a running one joins it (the stronger
/// strength, its clock restarted); otherwise it takes a free slot, or the weakest one. No allocation after construction.
/// </summary>
internal sealed class EdgeFlashes
{
    public const int Slots = 6;
    public const float MergeDegrees = 25f, Rise = 0.06f;

    private readonly float[] _azimuth = new float[Slots], _strength = new float[Slots], _start = new float[Slots], _length = new float[Slots];
    private readonly bool[] _used = new bool[Slots];

    /// <summary>The full length of a flash at strength 1; weaker flashes are shorter (down to 40 %).</summary>
    public float Seconds { get; set; } = 0.7f;

    /// <summary>Starts (or joins) a flash; a NaN azimuth or a strength of 0 or less does nothing.</summary>
    public void Hit(float azimuth, float strength, float now)
    {
        if (!float.IsFinite(azimuth) || !(strength > 0f)) return;
        strength = Math.Min(1f, strength);
        azimuth = Bearing.Wrap(azimuth);
        var slot = -1;
        for (var i = 0; i < Slots; i++)
            if (Live(i, now) && Bearing.Between(_azimuth[i], azimuth) <= MergeDegrees) { slot = i; break; }
        if (slot >= 0)
        {
            var current = Level(slot, now);
            _strength[slot] = Math.Max(current, strength);
            _azimuth[slot] = strength >= current ? azimuth : _azimuth[slot];
        }
        else
        {
            var weakest = float.MaxValue;
            for (var i = 0; i < Slots; i++)
            {
                if (!Live(i, now)) { slot = i; break; }
                var level = Level(i, now);
                if (level < weakest) { weakest = level; slot = i; }
            }
            _azimuth[slot] = azimuth;
            _strength[slot] = strength;
        }
        _used[slot] = true;
        _start[slot] = now;
        _length[slot] = Seconds * (0.4f + 0.6f * _strength[slot]);
    }

    public void Clear()
    {
        for (var i = 0; i < Slots; i++) _used[i] = false;
    }

    /// <summary>True while slot <paramref name="i"/> shows something.</summary>
    public bool Live(int i, float now) => _used[i] && now - _start[i] < _length[i];

    /// <summary>How bright slot <paramref name="i"/> is now, 0..1: a quick rise, then an eased fade.</summary>
    public float Level(int i, float now)
    {
        if (!Live(i, now)) return 0f;
        var age = now - _start[i];
        var shape = age < Rise ? age / Rise : 1f - Ease.InCubic((age - Rise) / Math.Max(1e-3f, _length[i] - Rise));
        return _strength[i] * Ease.Clamp01(shape);
    }

    public float Azimuth(int i) => _azimuth[i];

    /// <summary>True when any slot shows something.</summary>
    public bool Any(float now)
    {
        for (var i = 0; i < Slots; i++) if (Live(i, now)) return true;
        return false;
    }
}

/// <summary>Which screen corner a <see cref="Visor"/>'s cracks start from.</summary>
public enum ScreenCorner : byte
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>How a <see cref="Visor"/> state turns into what is drawn, without Unity. Points are in the unit square of the screen (0, 0
/// bottom left, 1, 1 top right); a 1080p layout point (x from the left, y from the top, on a 1920 x 1080 screen) is placed by its
/// distance from the middle in screen heights, so it keeps its place on any aspect and the middle window keeps its width.</summary>
public static class VisorMath
{
    /// <summary>Crack layers show from these armour shares down: the first at 40 %, the second at 20 %, the third at 10 %.</summary>
    public static readonly float[] CrackSteps = { 0.4f, 0.2f, 0.1f };

    /// <summary>How far the struts and brackets are moved up (in screen heights) at <paramref name="slide"/> 0..1: a quarter screen
    /// above at 0, in place at 1.</summary>
    public static float StrutDrop(float slide) => 0.25f * (1f - Ease.OutCubic(Ease.Clamp01(slide)));

    /// <summary>The <see cref="VisorState.Cracks"/> value for an armour share: 0 above 40 %, a third at 40 %, two thirds at 20 %, all
    /// at 10 % and below.</summary>
    public static float CracksFor(float armour)
    {
        var layers = 0;
        foreach (var step in CrackSteps) if (armour <= step) layers++;
        return layers / 3f;
    }

    /// <summary>How strongly crack layer <paramref name="layer"/> (0..2) shows at <paramref name="cracks"/> 0..1: each third of the
    /// value brings in the next layer.</summary>
    public static float CrackLayer(float cracks, int layer) => Ease.Clamp01(Ease.Clamp01(cracks) * 3f - layer + 0.001f);

    /// <summary>A 1080p layout point in the unit square of a screen <paramref name="aspect"/> wide per unit of height.</summary>
    public static (float U, float V) At1080(float x, float y, float aspect)
    {
        aspect = Math.Max(0.5f, aspect);
        return ((aspect / 2f + (x - 960f) / 1080f) / aspect, 0.5f + (540f - y) / 1080f);
    }

    /// <summary>The window struts: from the top bar's ends down to the middle nodes and on to the bottom edge, both sides, and the two
    /// short top pieces. Segments are (u0, v0, u1, v1).</summary>
    public static float[][] Struts(float aspect)
    {
        var list = new System.Collections.Generic.List<float[]>();
        void Add(float x0, float y0, float x1, float y1)
        {
            var (u0, v0) = At1080(x0, y0, aspect);
            var (u1, v1) = At1080(x1, y1, aspect);
            list.Add(new[] { u0, v0, u1, v1 });
        }
        foreach (var side in new[] { 1f, -1f })
        {
            float X(float x) => 960f + side * (x - 960f);
            Add(X(500f), 128f, X(392f), 420f);
            Add(X(392f), 420f, X(452f), 1080f);
            Add(X(500f), 128f, X(560f), 128f);
        }
        return list.ToArray();
    }

    /// <summary>The four corner brackets on the physical screen edge: 24 px in, a 90 px horizontal arm, a 70 px vertical arm and a
    /// 12 px chamfer between them (1080p pixels, scaled with the screen height). Segments are (u0, v0, u1, v1).</summary>
    public static float[][] Brackets(float aspect)
    {
        aspect = Math.Max(0.5f, aspect);
        const float m = 24f / 1080f, arm = 90f / 1080f, leg = 70f / 1080f, cut = 12f / 1080f;
        var list = new System.Collections.Generic.List<float[]>();
        for (var corner = 0; corner < 4; corner++)
        {
            var sx = (corner & 1) == 0 ? 1f : -1f;
            var sy = (corner & 2) == 0 ? 1f : -1f;
            var ox = sx > 0 ? m : aspect - m;
            var oy = sy > 0 ? m : 1f - m;
            void Add(float x0, float y0, float x1, float y1) => list.Add(new[] { x0 / aspect, y0, x1 / aspect, y1 });
            Add(ox, oy + sy * leg, ox, oy + sy * cut);
            Add(ox, oy + sy * cut, ox + sx * cut, oy);
            Add(ox + sx * cut, oy, ox + sx * arm, oy);
        }
        return list.ToArray();
    }

    /// <summary>The frame's colour channel <paramref name="tint"/> moved toward the heat red by <paramref name="heat"/>.</summary>
    public static float HeatChannel(float tint, float red, float heat) => Ease.Lerp(tint, red, Ease.Clamp01(heat) * 0.8f);
}
