using System;

namespace ForgeUI;

/// <summary>A camera's world-to-clip transform (row-major, Unity's <c>m</c>row<c>col</c>), multiplied and applied in plain float maths.</summary>
internal readonly struct ClipTransform
{
    private readonly float _00, _01, _02, _03, _10, _11, _12, _13, _20, _21, _22, _23, _30, _31, _32, _33;

    public ClipTransform(float m00, float m01, float m02, float m03, float m10, float m11, float m12, float m13,
        float m20, float m21, float m22, float m23, float m30, float m31, float m32, float m33)
    {
        _00 = m00; _01 = m01; _02 = m02; _03 = m03; _10 = m10; _11 = m11; _12 = m12; _13 = m13;
        _20 = m20; _21 = m21; _22 = m22; _23 = m23; _30 = m30; _31 = m31; _32 = m32; _33 = m33;
    }

    /// <summary><paramref name="projection"/> × <paramref name="view"/>.</summary>
    public static ClipTransform Combine(in ClipTransform view, in ClipTransform projection)
    {
        static float Dot(float a0, float a1, float a2, float a3, float b0, float b1, float b2, float b3) => a0 * b0 + a1 * b1 + a2 * b2 + a3 * b3;
        var p = projection; var v = view;
        return new ClipTransform(
            Dot(p._00, p._01, p._02, p._03, v._00, v._10, v._20, v._30), Dot(p._00, p._01, p._02, p._03, v._01, v._11, v._21, v._31),
            Dot(p._00, p._01, p._02, p._03, v._02, v._12, v._22, v._32), Dot(p._00, p._01, p._02, p._03, v._03, v._13, v._23, v._33),
            Dot(p._10, p._11, p._12, p._13, v._00, v._10, v._20, v._30), Dot(p._10, p._11, p._12, p._13, v._01, v._11, v._21, v._31),
            Dot(p._10, p._11, p._12, p._13, v._02, v._12, v._22, v._32), Dot(p._10, p._11, p._12, p._13, v._03, v._13, v._23, v._33),
            Dot(p._20, p._21, p._22, p._23, v._00, v._10, v._20, v._30), Dot(p._20, p._21, p._22, p._23, v._01, v._11, v._21, v._31),
            Dot(p._20, p._21, p._22, p._23, v._02, v._12, v._22, v._32), Dot(p._20, p._21, p._22, p._23, v._03, v._13, v._23, v._33),
            Dot(p._30, p._31, p._32, p._33, v._00, v._10, v._20, v._30), Dot(p._30, p._31, p._32, p._33, v._01, v._11, v._21, v._31),
            Dot(p._30, p._31, p._32, p._33, v._02, v._12, v._22, v._32), Dot(p._30, p._31, p._32, p._33, v._03, v._13, v._23, v._33));
    }

    /// <summary>The viewport point (0..1 from the bottom left) of a world point; false when it is behind the camera (or on its plane).</summary>
    public bool ToViewport(float x, float y, float z, out float viewportX, out float viewportY)
    {
        var w = _30 * x + _31 * y + _32 * z + _33;
        viewportX = viewportY = 0f;
        if (w <= 1e-4f) return false;
        viewportX = ((_00 * x + _01 * y + _02 * z + _03) / w + 1f) * 0.5f;
        viewportY = ((_10 * x + _11 * y + _12 * z + _13) / w + 1f) * 0.5f;
        return true;
    }

    /// <summary>The viewport point of a world point behind the camera, mirrored through the middle of the view the way the camera's own
    /// viewport projection gives it (divided by the negative depth); false for a point in front of the camera or on its plane.</summary>
    public bool ToViewportBehind(float x, float y, float z, out float viewportX, out float viewportY)
    {
        var w = _30 * x + _31 * y + _32 * z + _33;
        viewportX = viewportY = 0f;
        if (w >= -1e-4f) return false;
        viewportX = ((_00 * x + _01 * y + _02 * z + _03) / w + 1f) * 0.5f;
        viewportY = ((_10 * x + _11 * y + _12 * z + _13) / w + 1f) * 0.5f;
        return true;
    }
}

/// <summary>What the enemy health bars show (<see cref="UIKitMod"/>'s <c>AddEnemyHealthBars</c>). Each part is the caller's switch.</summary>
public sealed class EnemyHealthBarStyle
{
    /// <summary>A thin bar over each enemy the local player hit, with the lost piece flashing and shrinking away.</summary>
    public bool Bars { get; init; } = true;

    /// <summary>How many bars may show at once (1..<see cref="EnemyBarBook.Capacity"/>). A hit on another enemy when all are in use
    /// takes the place of the bar hit longest ago, and the kit's log says so.</summary>
    public int MaxBars { get; init; } = 6;

    /// <summary>Seconds a bar stays after the last hit before it fades (0.5..30).</summary>
    public float Linger { get; init; } = 3f;

    /// <summary>Metres from the camera beyond which no bar is drawn (5..200).</summary>
    public float Range { get; init; } = 45f;

    internal string? Problem()
    {
        if (MaxBars < 1 || MaxBars > EnemyBarBook.Capacity) return "MaxBars must be 1.." + EnemyBarBook.Capacity;
        if (!(Linger >= 0.5f && Linger <= 30f)) return "Linger must be 0.5..30 seconds";
        if (!(Range >= 5f && Range <= 200f)) return "Range must be 5..200 metres";
        return null;
    }
}

/// <summary>
/// The enemy health bars' state, without the game: which enemies have a bar (a fixed set of slots, the bar hit longest ago giving its
/// place when they are all in use) and the trailing piece each hit leaves (damage numbers and kill marks are the hit feedback's,
/// <see cref="HitFeedbackBook"/>). Every array is made once; nothing is allocated per hit or per frame.
/// </summary>
internal sealed class EnemyBarBook
{
    public const int Capacity = 12;
    /// <summary>The lost piece is bright for this long, then shrinks away (<see cref="MeterTrail"/>).</summary>
    public const float FlashSeconds = 0.12f;
    /// <summary>A bar fades over this long after its <see cref="EnemyHealthBarStyle.Linger"/>.</summary>
    public const float FadeSeconds = 0.4f;

    public struct Bar
    {
        public long Key;
        public bool Used;
        /// <summary>Health now, 0..1.</summary>
        public float Fraction;
        public MeterTrail Trail;
        public float LastHit, LastDrop;
        /// <summary>The line of sight was clear at the last check (set by the caller).</summary>
        public bool InSight;
        public float NextSight;
    }

    public readonly Bar[] Bars = new Bar[Capacity];

    public int Find(long key)
    {
        for (var i = 0; i < Capacity; i++)
            if (Bars[i].Used && Bars[i].Key == key) return i;
        return -1;
    }

    /// <summary>A hit by the local player that took <paramref name="before"/> − <paramref name="after"/> health out of
    /// <paramref name="max"/>. Returns the bar's slot; <paramref name="evictedKey"/> is the enemy whose bar gave its place (0 if
    /// none). <paramref name="limit"/> is the style's <see cref="EnemyHealthBarStyle.MaxBars"/>.</summary>
    public int Hit(long key, float now, float before, float after, float max, int limit, out long evictedKey)
    {
        evictedKey = 0;
        limit = Math.Clamp(limit, 1, Capacity);
        var drop = Math.Max(0f, before - after);
        var slot = Find(key);
        if (slot < 0)
        {
            for (var i = 0; i < limit && slot < 0; i++) if (!Bars[i].Used) slot = i;
            if (slot < 0)
            {
                slot = 0;
                for (var i = 1; i < limit; i++) if (Bars[i].LastHit < Bars[slot].LastHit) slot = i;
                evictedKey = Bars[slot].Key;
            }
            var start = max > 0f ? Ease.Clamp01(before / max) : 0f;
            Bars[slot] = new Bar { Key = key, Used = true, Fraction = start, LastDrop = float.NegativeInfinity, InSight = true, NextSight = now + 0.1f };
            Bars[slot].Trail.Reset(start);
        }
        ref var bar = ref Bars[slot];
        bar.Fraction = max > 0f ? Ease.Clamp01(after / max) : 0f;
        bar.LastHit = now;
        if (drop > 0f) bar.LastDrop = now;
        return slot;
    }

    /// <summary>The health the game holds now for a bar's enemy (every machine applies every player's hits), read each frame so
    /// teammates' damage shows too.</summary>
    public void Sync(int slot, float fraction)
    {
        if (Bars[slot].Used) Bars[slot].Fraction = Ease.Clamp01(fraction);
    }

    /// <summary>The enemy died or left the world: its bar goes at once.</summary>
    public void Remove(long key)
    {
        var slot = Find(key);
        if (slot >= 0) Bars[slot] = default;
    }

    /// <summary>Per frame: trails follow, bars past their linger and fade are freed.</summary>
    public void Tick(float now, float dt, float linger)
    {
        for (var i = 0; i < Capacity; i++)
        {
            ref var bar = ref Bars[i];
            if (!bar.Used) continue;
            if (now - bar.LastHit > linger + FadeSeconds) { bar = default; continue; }
            bar.Trail.Update(bar.Fraction, dt);
        }
    }

    /// <summary>A bar's opacity: full until <paramref name="linger"/> after its last hit, then fading out.</summary>
    public static float Alpha(in Bar bar, float now, float linger)
    {
        var age = now - bar.LastHit - linger;
        return age <= 0f ? 1f : Math.Max(0f, 1f - age / FadeSeconds);
    }

    /// <summary>The lost piece is bright right after a drop.</summary>
    public static bool Flashing(in Bar bar, float now) => now - bar.LastDrop < FlashSeconds;

    /// <summary>Everything off (level change, switch off).</summary>
    public void Clear() => Array.Clear(Bars);

    /// <summary>The next bars whose line of sight is due (at most <paramref name="max"/>, written to <paramref name="due"/>), round
    /// robin from where the last call stopped, so every bar is checked about every <paramref name="every"/> seconds.</summary>
    public int DueSight(float now, int[] due, int max, ref int cursor, float every)
    {
        var count = 0;
        var start = cursor;
        for (var k = 0; k < Capacity && count < max; k++)
        {
            var i = (start + k) % Capacity;
            if (!Bars[i].Used || now < Bars[i].NextSight) continue;
            Bars[i].NextSight = now + every;
            due[count++] = i;
            cursor = (i + 1) % Capacity;
        }
        return count;
    }
}
