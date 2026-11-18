using System;
using Player;
using UnityEngine;

namespace ForgeVisual;

/// <summary>What the local player's ambient light shows with nobody holding it: <see cref="Ambient.Native"/>.</summary>
public readonly record struct AmbientValues(float Intensity, float Range, Color Color);

/// <summary>
/// The local player's ambient light, the one a level's global light sets (<c>LocalPlayerAgent.m_ambientLight</c> and
/// <c>m_ambientLightColor</c>), lent to any number of users at once. <see cref="Lease"/> takes brightness (the light's intensity), range and
/// colour for the caller; the latest lease on a field shows, a lease let go gives the field to the one before it, and the last one gives
/// back what the game had. A change runs as a straight fade over the seconds given. A field some other writer changed in the meantime
/// stays that writer's: the lease ends there and the original is not written over it (see <see cref="FieldLeases{T}"/>). A new level or a
/// new local player drops every lease without writing. Main thread only; the library steps the fades every frame.
/// </summary>
public static class Ambient
{
    private static LocalPlayerAgent? _player;
    private static EffectLight? _light;
    internal static readonly FieldLeases<float> Intensity = new(() => _light!.Intensity, v => _light!.Intensity = v, Mathf.Lerp);
    internal static readonly FieldLeases<float> Range = new(() => _light!.Range, v => _light!.Range = v, Mathf.Lerp);
    internal static readonly FieldLeases<Color> LightColor = new(() => _light!.Color, v => _light!.Color = v, Color.Lerp, SameColor);
    internal static readonly FieldLeases<Color> PlayerColor = new(() => _player!.m_ambientLightColor, v => _player!.m_ambientLightColor = v, Color.Lerp, SameColor);

    private static bool SameColor(Color a, Color b) => a.r.Equals(b.r) && a.g.Equals(b.g) && a.b.Equals(b.b) && a.a.Equals(b.a);

    /// <summary>Takes the ambient light for <paramref name="owner"/>: <paramref name="brightness"/> is the light's intensity,
    /// <paramref name="range"/> its reach in metres, <paramref name="color"/> its colour and the player's ambient colour; null leaves that field alone.
    /// The fields move there over <paramref name="seconds"/> (0: at once). The lease is not <see cref="AmbientLease.Active"/> when the local player has no ambient light yet.</summary>
    public static AmbientLease Lease(string owner, float? brightness = null, float? range = null, Color? color = null, float seconds = 0f)
    {
        var lease = new AmbientLease(owner);
        lease.Set(brightness, range, color, seconds);
        return lease;
    }

    /// <summary>The light's values with no lease on it (what the game had), or null when the local player has no ambient light.</summary>
    public static AmbientValues? Native => Bind() ? new AmbientValues(Intensity.Native, Range.Native, LightColor.Native) : null;

    /// <summary>True while any field is still moving to a leased value or back.</summary>
    public static bool Fading => Intensity.Moving || Range.Moving || LightColor.Moving || PlayerColor.Moving;

    internal static EffectLight? Light => Bind() ? _light : null;

    /// <summary>Follows the local player's ambient light; false when there is none. A different light or player than the last one means a new
    /// level: every lease ends without a write (the old light is gone).</summary>
    internal static bool Bind()
    {
        var player = ForgeCore.Players.Local?.TryCast<LocalPlayerAgent>();
        var light = player?.m_ambientLight;
        if (player == null || light == null || player.WasCollected || light.WasCollected) { Forget(); return false; }
        if (_light != null && _light.Pointer == light.Pointer && _player!.Pointer == player.Pointer) return true;
        Forget();
        _player = player;
        _light = light;
        return true;
    }

    private static void Forget()
    {
        if (_light == null && _player == null) return;
        Intensity.Abandon(); Range.Abandon(); LightColor.Abandon(); PlayerColor.Abandon();
        _light = null; _player = null;
    }

    /// <summary>One frame of every fade; the library calls it.</summary>
    internal static void Tick(float deltaSeconds)
    {
        if (!Intensity.Live && !Range.Live && !LightColor.Live && !PlayerColor.Live) return;
        if (!Bind()) return;
        Intensity.Tick(deltaSeconds); Range.Tick(deltaSeconds); LightColor.Tick(deltaSeconds); PlayerColor.Tick(deltaSeconds);
    }
}

/// <summary>One user's hold on the ambient light (<see cref="Ambient.Lease"/>). Change what it holds with <see cref="Set"/>, give it back with <see cref="Release"/>.</summary>
public sealed class AmbientLease
{
    private FieldLeases<float>.Entry? _intensity, _range;
    private FieldLeases<Color>.Entry? _color, _playerColor;
    private bool _released, _placed;
    private Vector3 _place;
    private IntPtr _placedLight;

    internal AmbientLease(string owner) => Owner = owner;

    public string Owner { get; }

    /// <summary>True while the lease still holds at least one field: false once released, after another writer took every field it held,
    /// after a new level, or when the local player had no ambient light to take.</summary>
    public bool Active => !_released && (_intensity is { Alive: true } || _range is { Alive: true } || _color is { Alive: true });

    /// <summary>The fields this lease holds and nobody else has changed since its last write, as bits: 1 brightness, 2 range, 4 colour, 8 the player's ambient colour.</summary>
    public int OwnedFields => _released ? 0 : Owns(Ambient.Intensity, _intensity, 1) | Owns(Ambient.Range, _range, 2) | Owns(Ambient.LightColor, _color, 4) | Owns(Ambient.PlayerColor, _playerColor, 8);

    private static int Owns<T>(FieldLeases<T> field, FieldLeases<T>.Entry? entry, int bit) where T : struct => entry != null && field.Owns(entry) ? bit : 0;

    /// <summary>Changes what the lease holds: the fields given move to their values over <paramref name="seconds"/>, a null field is let go (it goes to the
    /// previous lease's value, or back to the game's). A released lease holds nothing again.</summary>
    public void Set(float? brightness, float? range, Color? color, float seconds = 0f)
    {
        if (_released) return;
        Hold(Ambient.Intensity, ref _intensity, brightness, seconds);
        Hold(Ambient.Range, ref _range, range, seconds);
        Hold(Ambient.LightColor, ref _color, color, seconds);
        Hold(Ambient.PlayerColor, ref _playerColor, color, seconds);
    }

    private static void Hold<T>(FieldLeases<T> field, ref FieldLeases<T>.Entry? entry, T? value, float seconds) where T : struct
    {
        if (value is { } v) entry = Ambient.Bind() ? field.Hold(entry, v, seconds) : null;
        else if (entry != null) { field.Drop(entry, seconds); entry = null; }
    }

    /// <summary>Puts the light at <paramref name="world"/> (it rides on the local player otherwise); <see cref="Release"/> puts it back on the player.
    /// Call it every frame the light should stay there, after the game moved the player.</summary>
    public void Follow(Vector3 world)
    {
        if (!Active || Ambient.Light is not { } light) return;
        if (!_placed || _placedLight != light.Pointer) { _place = light.transform.localPosition; _placed = true; _placedLight = light.Pointer; }
        light.transform.position = world;
    }

    /// <summary>Gives every held field back over <paramref name="seconds"/> (0: at once); a field another writer changed is left as it is.</summary>
    public void Release(float seconds = 0f)
    {
        if (_released) return;
        Set(null, null, null, seconds);
        _released = true;
        if (_placed && Ambient.Light is { } light && light.Pointer == _placedLight) light.transform.localPosition = _place;
        _placed = false;
    }
}
