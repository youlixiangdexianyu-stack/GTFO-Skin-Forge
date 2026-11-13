using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeAudio;

/// <summary>The kit's global sound categories, shared by every mod: each bus of each mod names one. A category has one voice limit
/// for all mods together (<see cref="AudioCategories.Limits"/>), one volume in the kit's config, follows one of the game's volume
/// sliders, and is what a bus ducks.</summary>
internal enum AudioCategory { Ui, Weapon, Explosion, Debris, Ambience, Voice, Vehicle, Device, Music }

internal static class AudioCategories
{
    internal const int Count = 9;
    /// <summary>The names cues.forge and the config use, in enum order.</summary>
    internal static readonly string[] Names = { "ui", "weapon", "explosion", "debris", "ambience", "voice", "vehicle", "device", "music" };
    /// <summary>How many sounds of the category may play at once across every mod (virtual loops included; fading sounds not).</summary>
    internal static readonly int[] Limits = { 8, 16, 10, 12, 8, 4, 48, 10, 2 };
    /// <summary>What each category is for, in the config descriptions and the level-start table.</summary>
    internal static readonly string[] Meanings =
    {
        "menus, prompts, confirmations and warnings", "gunfire, reloads and weapon handling", "blasts and impacts",
        "falling debris, shrapnel and settling rubble", "beds, hums and room tones", "speech and radio voices",
        "suits, drop pods and other vehicles: steps, servos, engines", "machines in the world: beacons, turrets, supply pods, terminals",
        "music",
    };

    internal static string Name(AudioCategory category) => Names[(int)category];
    internal static string Known => string.Join(", ", Names);
    internal static int Bit(AudioCategory category) => 1 << (int)category;

    internal static bool TryParse(string? name, out AudioCategory category)
    {
        var index = name == null ? -1 : Array.IndexOf(Names, name);
        category = index < 0 ? default : (AudioCategory)index;
        return index >= 0;
    }
}

/// <summary>How a sound is placed for one side (the local player's own sounds, or everyone else's): in the listener's head,
/// at a fixed world point, or following an object. Auto keeps what the caller supplied (nothing, a position or a transform).</summary>
internal enum Place { Auto, TwoD, World, Follow }

/// <summary>One bus of one mod: the group that cues share, mapped to a global <see cref="AudioCategory"/>. Its gain scales every cue
/// on it and its seats limit how many of its sounds play at once from one emitter (the mod's own limit; the weakest are faded out for
/// a stronger newcomer); the category's global limit applies on top. While any of its sounds plays, every sound of the categories it
/// ducks is lowered, whichever mod plays it.</summary>
internal sealed class BusDefinition
{
    internal string Name = "";
    internal AudioCategory Category;
    internal float Gain = 1;
    /// <summary>0 means unlimited.</summary>
    internal int Seats;
    internal AudioCategory[] Duck = Array.Empty<AudioCategory>();
    internal float DuckDb = 6;
    /// <summary><see cref="Duck"/> as category bits (<see cref="AudioCategories.Bit"/>).</summary>
    internal int DuckMask;
}

/// <summary>Loudness by distance. Linear and log fall from 1 at <c>near</c> to 0 at <c>far</c>; points are (metre, gain) pairs.</summary>
internal sealed class DistanceCurve
{
    internal enum Kind { Linear, Log, Points }
    internal Kind Shape = Kind.Linear;
    internal float[] Distances = Array.Empty<float>(), Gains = Array.Empty<float>();

    internal float Evaluate(float distance, float near, float far, bool cull)
    {
        if (cull && distance >= far) return 0;
        if (distance <= near) return Shape == Kind.Points ? PointGain(distance) : 1;
        switch (Shape)
        {
            case Kind.Log:
                {
                    var floor = near / far;
                    return Math.Clamp((near / distance - floor) / (1 - floor), 0, 1);
                }
            case Kind.Points: return PointGain(distance);
            default: return distance >= far ? 0 : 1 - (distance - near) / (far - near);
        }
    }

    private float PointGain(float distance)
    {
        if (distance <= Distances[0]) return Gains[0];
        for (var i = 1; i < Distances.Length; i++)
        {
            if (distance > Distances[i]) continue;
            var t = (distance - Distances[i - 1]) / (Distances[i] - Distances[i - 1]);
            return Gains[i - 1] + (Gains[i] - Gains[i - 1]) * t;
        }
        return Gains[^1];
    }
}

/// <summary>How a cue is heard in space.</summary>
internal sealed class SpaceDefinition
{
    internal float Near = 2, Far = 30;
    internal DistanceCurve Curve = new();
    /// <summary>0 hears the sound flat (no distance loss, no panning), 1 fully positioned.</summary>
    internal float Blend = 1;
    /// <summary>0 to 180 degrees: how much the sound widens, which reduces panning.</summary>
    internal float Spread;
    /// <summary>0 to 5: how strongly relative motion shifts the pitch.</summary>
    internal float Doppler;
    /// <summary>Beyond <see cref="Far"/> the sound is silent and takes no voice.</summary>
    internal bool Cull = true;

    /// <summary>The loudness factor at <paramref name="distance"/> metres: the curve, softened by <see cref="Blend"/>; 0 when culled.</summary>
    internal float Attenuation(float distance)
    {
        var curve = Curve.Evaluate(distance, Near, Far, Cull);
        if (Cull && distance >= Far) return 0;
        return 1 + (curve - 1) * Blend;
    }
}

/// <summary>One audio layer of a cue. The clips are a variant pool (one is picked per variant); everything else shapes the
/// layer inside the cue's mix. A layer with a distance range plays only when the listener is inside it, so a far layer can
/// replace the near one.</summary>
internal sealed class LayerDefinition
{
    internal string[] Clips = Array.Empty<string>();
    internal float OffsetMs;
    internal float Gain = 1;
    /// <summary>Seconds of the clip to use; End below zero means to its end.</summary>
    internal float RangeStart, RangeEnd = -1;
    internal float FadeInMs, FadeOutMs;
    internal float PitchMin = 1, PitchMax = 1;
    internal float DistanceMin, DistanceMax = float.PositiveInfinity;
    /// <summary>Low-pass cutoff in Hz; 0 leaves the layer unfiltered.</summary>
    internal float LowpassHz;

    /// <summary>True when the layer plays its clip untouched.</summary>
    internal bool IsPlain => OffsetMs == 0 && Gain == 1 && RangeStart == 0 && RangeEnd < 0 && FadeInMs == 0 && FadeOutMs == 0
        && PitchMin == 1 && PitchMax == 1 && DistanceMin == 0 && float.IsPositiveInfinity(DistanceMax) && LowpassHz == 0;
}

/// <summary>One playable cue: its layers and how it plays.</summary>
internal sealed class CueDefinition
{
    internal string Name = "";
    internal LayerDefinition[] Layers = Array.Empty<LayerDefinition>();
    /// <summary>The cue's volume: <see cref="Gain"/> is the loudest, <see cref="GainMin"/> the quietest a play may pick (equal for a fixed
    /// volume); a play picks evenly in decibels between them (<see cref="GainPick"/>).</summary>
    internal float Gain = 1, GainMin = 1, PitchMin = 1, PitchMax = 1;
    internal bool Loop;
    internal int Priority = 100;
    internal int MaxVoices = 8;
    internal int MaxPerEmitter = 8;
    internal float MinInterval;
    internal bool Environment = true;
    /// <summary>The bus it plays on (every cue has one); its category is the bus's.</summary>
    internal string Bus = "";
    /// <summary>The cue's longest length in seconds (0 none): the mix is cut there and fades out.</summary>
    internal float MaxSeconds;
    internal Place SelfPlace = Place.Auto, OtherPlace = Place.Auto;
    internal SpaceDefinition Space = new();

    /// <summary>Distinct clip files of every layer, in order.</summary>
    internal IEnumerable<string> Clips => Layers.SelectMany(layer => layer.Clips).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>One plain layer and no cap: the clip file plays as it is, without a mix.</summary>
    internal bool IsDirect => Layers.Length == 1 && Layers[0].IsPlain && MaxSeconds == 0;
}
