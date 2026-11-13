using FX_EffectSystem;
using UnityEngine;

namespace ForgeVisual;

/// <summary>One light's colour, reach, peak intensity, lifetime, flicker and blink. A flash fades out over <see cref="Life"/>; a held
/// light ignores it. A light that <see cref="Blink"/>s (a warning lamp) is on for half of every blink and off for the other half.</summary>
public readonly struct PointLightSpec
{
    public readonly Color Color;
    public readonly float Range, Intensity, Life, Flicker;

    /// <summary>Blinks per second; 0 is steady.</summary>
    public readonly float Blink;

    /// <param name="color">Light colour.</param>
    /// <param name="range">Reach in metres.</param>
    /// <param name="intensity">Peak intensity.</param>
    /// <param name="life">Seconds a flash lasts (a held light ignores it).</param>
    /// <param name="flicker">Fraction of the intensity a noise flicker takes away, 0 to 1.</param>
    public PointLightSpec(Color color, float range, float intensity, float life = 0f, float flicker = 0.1f)
    {
        Color = color;
        Range = range;
        Intensity = intensity;
        Life = life;
        Flicker = flicker;
        Blink = 0f;
    }

    /// <param name="color">Light colour.</param>
    /// <param name="range">Reach in metres.</param>
    /// <param name="intensity">Peak intensity.</param>
    /// <param name="life">Seconds a flash lasts (a held light ignores it).</param>
    /// <param name="flicker">Fraction of the intensity a noise flicker takes away, 0 to 1.</param>
    /// <param name="blink">Blinks per second (on, then off, for half a blink each), for a warning lamp; 0 is steady.</param>
    public PointLightSpec(Color color, float range, float intensity, float life, float flicker, float blink)
        : this(color, range, intensity, life, flicker) => Blink = blink;
}

/// <summary>How <see cref="PointLights.Place"/> ended.</summary>
public enum PointLightResult
{
    Placed,
    /// <summary>Every light is held or leased, or the game has no free effect light.</summary>
    Full,
}

/// <summary>
/// The one pool of temporary effect lights every mod shares: flashes at blasts and muzzles, a light held on a burning or glowing thing,
/// a blinking warning lamp. Drawn with the game's own <c>FX_PointLight</c>: its clustered renderer only lights through those, never
/// through Unity particle lights or <see cref="Light"/>s, and they cast no shadow. <see cref="Capacity"/> lights are the budget for
/// every mod together. A <see cref="Flash"/> fades out over its life; a <see cref="Hold"/>ing caller keeps one light at a point every
/// frame (the light fades in and, once not held for a moment, out) and gives it back with <see cref="Release"/>. When every slot is busy
/// a flash takes over the one nearest its end and held or leased (<see cref="Place"/>) lights are never taken; when the game's own light pool is empty the light is
/// skipped and the particles go on. A handle carries a slot generation, so a stale one never moves somebody else's light. The player can
/// turn flashes off (<c>Effects / FlashLights</c>); held lights stay. Main thread only; nothing allocates per frame.
/// </summary>
public static class PointLights
{
    public const int Capacity = 32;
    private const float HoldFadeIn = 0.04f, HoldGrace = 0.06f, HoldFadeOut = 0.12f;

    private static readonly FX_PointLight?[] Lights = new FX_PointLight?[Capacity];
    private static readonly Vector3[] Positions = new Vector3[Capacity];
    private static readonly float[] Born = new float[Capacity];
    private static readonly float[] Seen = new float[Capacity];
    private static readonly float[] Life = new float[Capacity];
    private static readonly float[] Ends = new float[Capacity];
    private static readonly float[] Peak = new float[Capacity];
    private static readonly float[] Strength = new float[Capacity];
    private static readonly float[] Flicker = new float[Capacity];
    private static readonly float[] Blink = new float[Capacity];
    private static readonly bool[] Used = new bool[Capacity];
    private static readonly bool[] Held = new bool[Capacity];
    private static readonly bool[] Steady = new bool[Capacity];
    private static readonly bool[] Pinned = new bool[Capacity];
    private static readonly int[] Generation = new int[Capacity];

    /// <summary>A light at <paramref name="position"/> that fades out over the spec's life, starting <paramref name="delay"/> seconds
    /// from <paramref name="now"/> (<c>Time.time</c>); <paramref name="strength"/> scales its peak. Nothing while the player turned
    /// flashes off.</summary>
    public static void Flash(Vector3 position, in PointLightSpec spec, float now, float strength = 1f, float delay = 0f)
    {
        if (!Plugin.FlashLights.Value) return;
        var slot = Allocate(spec, now);
        if (slot < 0) return;
        Held[slot] = Steady[slot] = Pinned[slot] = false;
        Positions[slot] = position;
        Born[slot] = now + delay;
        Life[slot] = Mathf.Max(0.02f, spec.Life);
        Ends[slot] = Born[slot] + Life[slot];
        Strength[slot] = strength;
    }

    /// <summary>A light at <paramref name="position"/> that stays at the spec's full intensity for <paramref name="seconds"/> from
    /// <paramref name="now"/> and then goes: a lease for a caller that names the intensity and the duration itself (an authored step), not
    /// a flash that fades. It is never taken over by a flash and ignores the player's flash setting (it is not a flash).
    /// <see cref="PointLightResult.Full"/> when every slot is held or leased, or the game's own light pool is empty; nothing is taken from
    /// the lights already lit in that case.</summary>
    public static PointLightResult Place(Vector3 position, in PointLightSpec spec, float seconds, float now)
    {
        var slot = Allocate(spec, now);
        if (slot < 0) return PointLightResult.Full;
        Held[slot] = false;
        Steady[slot] = Pinned[slot] = true;
        Positions[slot] = position;
        Born[slot] = now;
        Life[slot] = seconds;
        Ends[slot] = now + seconds;
        Strength[slot] = 1f;
        return PointLightResult.Placed;
    }

    /// <summary>Keeps the light <paramref name="handle"/> (0 = none) at <paramref name="position"/> this frame, taking a slot the first
    /// time; its colour, range and intensity follow the spec every frame. A light not held again for a moment fades out and its handle goes stale. <paramref name="strength"/> scales the spec's
    /// intensity this frame (a caller's own pulse or fade).</summary>
    public static void Hold(ref int handle, Vector3 position, in PointLightSpec spec, float now, float strength = 1f)
    {
        var slot = Resolve(handle);
        if (slot < 0 || !Held[slot])
        {
            slot = Allocate(spec, now);
            if (slot < 0) { handle = 0; return; }
            Held[slot] = Pinned[slot] = true;
            Steady[slot] = false;
            Born[slot] = now;
            handle = ((Generation[slot] & 0x7fffff) << 8) | (slot + 1);
        }
        else
        {
            // a held light follows its caller's colour and reach every frame, as its position
            var light = Lights[slot]!;
            light.SetColor(spec.Color);
            light.SetRange(spec.Range);
            Peak[slot] = spec.Intensity;
        }
        Positions[slot] = position;
        Seen[slot] = now;
        Strength[slot] = strength;
    }

    /// <summary>Lets a held light go (it fades out) and zeroes the handle.</summary>
    public static void Release(ref int handle)
    {
        var slot = Resolve(handle);
        // Seen goes back to where the fade-out starts, so a released light dims over HoldFadeOut like one nobody holds any more.
        if (slot >= 0 && Held[slot]) Seen[slot] = Mathf.Min(Seen[slot], Time.time - HoldGrace);
        handle = 0;
    }

    /// <summary>Applies this frame's positions and fades. The library calls it every frame; a mod whose effects move later in the
    /// frame may call it again after they moved (it is safe to call more than once a frame).</summary>
    public static void Tick(float now)
    {
        if (_live == 0) return;
        for (var slot = 0; slot < Capacity; slot++)
        {
            var light = Lights[slot];
            if (light is null) continue;
            if (light == null || !light.m_isInUse) { Forget(slot); continue; }
            float envelope;
            if (Steady[slot])
            {
                if (now >= Ends[slot]) { Free(slot); continue; }
                envelope = 1f;
            }
            else if (Held[slot])
            {
                var silent = now - Seen[slot];
                envelope = Mathf.Clamp01((now - Born[slot]) / HoldFadeIn)
                    * (silent <= HoldGrace ? 1f : 1f - Mathf.Clamp01((silent - HoldGrace) / HoldFadeOut));
                if (silent > HoldGrace + HoldFadeOut) { Free(slot); continue; }
            }
            else
            {
                var age = now - Born[slot];
                if (age >= Life[slot]) { Free(slot); continue; }
                var left = age < 0f ? 0f : 1f - age / Life[slot];
                envelope = age < 0f ? 0f : left * left;
            }
            var flicker = 1f - Flicker[slot] * Mathf.PerlinNoise(now * 14f, slot * 3.7f);
            light.m_intensity = Peak[slot] * Strength[slot] * envelope * flicker * LightSlots.Blink(now - Born[slot], Blink[slot]);
            light.m_position = Positions[slot];
            light.UpdateData();
            light.UpdateTransform();
        }
    }

    /// <summary>Gives every light back to the game at once (level end, a mod's reset).</summary>
    public static void Clear()
    {
        for (var slot = 0; slot < Capacity; slot++) if (Lights[slot] is not null) Free(slot);
    }

    private static int Resolve(int handle)
    {
        if (handle == 0) return -1;
        var slot = (handle & 0xff) - 1;
        if (slot < 0 || slot >= Capacity || Lights[slot] is null || (Generation[slot] & 0x7fffff) != handle >> 8) return -1;
        return slot;
    }

    private static int Allocate(in PointLightSpec spec, float now)
    {
        var slot = LightSlots.Pick(Used, Pinned, Ends, now);
        if (slot < 0) return -1;
        var light = Lights[slot];
        if (light is null || light == null || !light.m_isInUse)
        {
            Forget(slot);
            if (!FX_Manager.TryAllocateFXLight(out var allocated, false) || allocated == null) return -1;
            Lights[slot] = light = allocated;
            _live++;
            Used[slot] = true;
        }
        Generation[slot]++;
        light.SetColor(spec.Color);
        light.SetRange(spec.Range);
        light.m_intensity = 0f;
        Peak[slot] = spec.Intensity;
        Flicker[slot] = spec.Flicker;
        Blink[slot] = spec.Blink;
        return slot;
    }

    private static int _live;

    /// <summary>The slot no longer has a light (the game took it back, or it was freed).</summary>
    private static void Forget(int slot)
    {
        if (Lights[slot] is not null) _live--;
        Lights[slot] = null;
        Used[slot] = Held[slot] = Steady[slot] = Pinned[slot] = false;
        Generation[slot]++;
    }

    private static void Free(int slot)
    {
        var light = Lights[slot];
        Forget(slot);
        if (light is null || light == null || !light.m_isInUse) return;
        light.m_intensity = 0f;
        light.UpdateData();
        FX_Manager.DeallocateFXLight(light);
    }
}
