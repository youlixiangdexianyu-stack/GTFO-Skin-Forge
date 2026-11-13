using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgeAudio;

/// <summary>A mixed cue variant: interleaved float samples.</summary>
internal sealed record MixedAudio(float[] Samples, int Channels, int SampleRate)
{
    internal int Frames => Samples.Length / Channels;
    internal double Seconds => (double)Frames / SampleRate;
}

/// <summary>One stretch of listener distance, [<see cref="Min"/>, <see cref="Max"/>), over which the same layers play.
/// <see cref="Mask"/> has a bit per cue layer; 0 means nothing plays there.</summary>
internal readonly record struct MixProfile(float Min, float Max, int Mask)
{
    internal bool Silent => Mask == 0;
}

/// <summary>Turns a cue's layers into audio. The game mixes every layered cue once at load, one variant per clip of its largest
/// pool and one profile per distance stretch, so playback is a single clip; the preview tool runs the same code, so what a
/// preview plays is exactly what the game plays. All mixing is deterministic: random pitches come from a hash of the cue,
/// variant and layer.</summary>
internal static class CueMix
{
    internal const double MaxMixSeconds = 60;
    internal const float CapFadeRatio = .12f, CapFadeMin = .04f, CapFadeMax = .15f;

    /// <summary>The cue's distance stretches in ascending order, neighbours with the same layers merged.</summary>
    internal static MixProfile[] Profiles(CueDefinition cue)
    {
        var cuts = new SortedSet<float> { 0 };
        foreach (var layer in cue.Layers)
        {
            if (layer.DistanceMin > 0) cuts.Add(layer.DistanceMin);
            if (float.IsFinite(layer.DistanceMax)) cuts.Add(layer.DistanceMax);
        }
        var starts = cuts.ToArray();
        var profiles = new List<MixProfile>();
        for (var i = 0; i < starts.Length; i++)
        {
            var mask = 0;
            for (var l = 0; l < cue.Layers.Length; l++)
                if (cue.Layers[l].DistanceMin <= starts[i] && starts[i] < cue.Layers[l].DistanceMax) mask |= 1 << l;
            var end = i + 1 < starts.Length ? starts[i + 1] : float.PositiveInfinity;
            if (profiles.Count > 0 && profiles[^1].Mask == mask) profiles[^1] = profiles[^1] with { Max = end };
            else profiles.Add(new MixProfile(starts[i], end, mask));
        }
        return profiles.ToArray();
    }

    internal static int ProfileAt(MixProfile[] profiles, float distance)
    {
        for (var i = 0; i < profiles.Length; i++) if (distance < profiles[i].Max) return i;
        return profiles.Length - 1;
    }

    /// <summary>How many variants the cue has: the clip count of its largest layer pool.</summary>
    internal static int Variants(CueDefinition cue) => cue.Layers.Max(layer => layer.Clips.Length);

    /// <summary>The pitch a layer takes in a variant: fixed, or a reproducible pick inside its range.</summary>
    internal static float LayerPitch(CueDefinition cue, LayerDefinition layer, int variant, int layerIndex)
        => layer.PitchMin == layer.PitchMax ? layer.PitchMin : layer.PitchMin + (layer.PitchMax - layer.PitchMin) * Unit(cue.Name, variant, layerIndex);

    private static float Unit(string name, int variant, int layer)
    {
        uint hash = 2166136261;
        foreach (var c in name) hash = (hash ^ c) * 16777619;
        hash = (hash ^ (uint)variant) * 16777619; hash = (hash ^ (uint)layer) * 16777619;
        hash ^= hash >> 15; hash *= 2246822519; hash ^= hash >> 13;
        return (hash & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>The mix of one variant for one distance stretch; null for a stretch where nothing plays.</summary>
    /// <param name="cue">The cue.</param>
    /// <param name="profile">The distance stretch to mix.</param>
    /// <param name="variant">The variant number, below <see cref="Variants"/>.</param>
    /// <param name="source">Decoded PCM of a clip file of the cue.</param>
    internal static MixedAudio? Render(CueDefinition cue, MixProfile profile, int variant, Func<string, DecodedAudio> source)
    {
        if (profile.Silent) return null;
        var first = source(cue.Layers[0].Clips[0]);
        var rate = first.SampleRate;
        var channels = cue.Clips.Any(file => source(file).Channels > 1) ? 2 : 1;
        var parts = new List<(int Offset, float[] Samples)>();
        var frames = 0L;
        var cap = cue.MaxSeconds > 0 ? (long)Math.Round(cue.MaxSeconds * rate) : (long)(MaxMixSeconds * rate) + 1;
        var reachesCap = false;
        for (var l = 0; l < cue.Layers.Length; l++)
        {
            if ((profile.Mask & (1 << l)) == 0) continue;
            var layer = cue.Layers[l];
            var offset = (int)Math.Round(layer.OffsetMs * rate / 1000.0);
            // A layer is rendered only as far as the mix keeps it: what lies past the cap is never read, so it is never allocated.
            var samples = RenderLayer(cue, layer, l, variant, source(layer.Clips[variant % layer.Clips.Length]), rate, channels, Math.Max(0L, cap - offset), out var full);
            parts.Add((offset, samples));
            frames = Math.Max(frames, offset + full);
            reachesCap |= offset + full > cap;
        }
        if (cue.MaxSeconds > 0) frames = Math.Min(frames, cap);
        if (frames <= 0 || frames > MaxMixSeconds * rate) throw new InvalidDataException("the mix is empty or longer than " + MaxMixSeconds + " seconds.");
        var mix = new float[frames * channels];
        foreach (var (offset, samples) in parts)
        {
            var count = (int)Math.Min(samples.Length, (frames - offset) * channels);
            for (var i = 0; i < count; i++) mix[offset * channels + i] += samples[i];
        }
        var capped = cue.MaxSeconds > 0 && reachesCap;
        if (capped)
        {
            var fade = (int)Math.Min(frames / 2, Math.Round(Math.Clamp(cue.MaxSeconds * CapFadeRatio, CapFadeMin, CapFadeMax) * rate));
            for (var i = 0; i < fade; i++)
            {
                var gain = (fade - 1 - i) / (float)Math.Max(1, fade - 1);
                for (var c = 0; c < channels; c++) mix[(frames - fade + i) * channels + c] *= gain;
            }
        }
        for (var i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);
        return new MixedAudio(mix, channels, rate);
    }

    /// <summary>The layer's samples for its first <paramref name="room"/> frames at most (its fades still work from its full length, <paramref name="fullLength"/>).</summary>
    private static float[] RenderLayer(CueDefinition cue, LayerDefinition layer, int index, int variant, DecodedAudio clip, int rate, int channels, long room, out long fullLength)
    {
        var frames = clip.Frames;
        var from = Math.Min(frames, (int)Math.Round(layer.RangeStart * clip.SampleRate));
        var to = layer.RangeEnd < 0 ? frames : Math.Min(frames, (int)Math.Round(layer.RangeEnd * clip.SampleRate));
        if (from >= to) throw new InvalidDataException("layer range is outside the clip (" + (frames / (double)clip.SampleRate).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " s long).");
        var step = (double)clip.SampleRate / rate * LayerPitch(cue, layer, variant, index);
        var total = (long)Math.Floor((to - from - 1) / step) + 1;
        fullLength = total;
        var length = (int)Math.Min(total, room);
        var output = new float[length * channels];
        for (var i = 0; i < length; i++)
        {
            var position = from + i * step;
            var i0 = (int)position; var fraction = (float)(position - i0);
            var i1 = Math.Min(i0 + 1, to - 1);
            for (var c = 0; c < channels; c++)
            {
                var source = clip.Channels == 1 ? 0 : c;
                var a = clip.Samples[i0 * clip.Channels + source]; var b = clip.Samples[i1 * clip.Channels + source];
                output[i * channels + c] = a + (b - a) * fraction;
            }
        }
        if (layer.LowpassHz > 0)
        {
            var pole = (float)Math.Exp(-2 * Math.PI * layer.LowpassHz / rate);
            for (var c = 0; c < channels; c++)
            {
                float one = 0, two = 0;
                for (var i = 0; i < length; i++)
                {
                    one = output[i * channels + c] * (1 - pole) + one * pole;
                    two = one * (1 - pole) + two * pole;
                    output[i * channels + c] = two;
                }
            }
        }
        var fadeIn = (int)Math.Min(total, (long)Math.Round(layer.FadeInMs * rate / 1000.0));
        var fadeOut = (int)Math.Min(total, (long)Math.Round(layer.FadeOutMs * rate / 1000.0));
        for (var i = 0; i < length; i++)
        {
            var gain = layer.Gain;
            if (i < fadeIn) gain *= i / (float)Math.Max(1, fadeIn);
            var tail = total - 1 - i;
            if (tail < fadeOut) gain *= tail / (float)Math.Max(1, fadeOut);
            if (gain == 1) continue;
            for (var c = 0; c < channels; c++) output[i * channels + c] *= gain;
        }
        return output;
    }

    /// <summary>True when keeping this mix still fits the decoded-audio budget: <paramref name="used"/> already counts the decoded PCM
    /// waiting to be mixed and every clip made so far; the mix is held twice while Unity takes its copy.</summary>
    internal static bool Fits(long used, MixedAudio mix, long limit) => used + 2L * mix.Samples.Length * sizeof(float) <= limit;
    /// <summary>The loudness of a cue heard at <paramref name="distance"/>: cue gain, its bus gain and the distance curve.</summary>
    internal static float Loudness(CueDefinition cue, float busGain, float distance) => cue.Gain * busGain * cue.Space.Attenuation(distance);
}
