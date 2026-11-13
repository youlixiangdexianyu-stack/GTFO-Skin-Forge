using System;
using System.Collections.Generic;

namespace ForgeAudio;

/// <summary>What one silent play of a prewarm did.</summary>
internal enum PrewarmStep { Started, Waiting, Refused }

/// <summary>The progress of silently starting every one-shot cue of a library once, one per call: the cue that is next and when to
/// give up on a cue the engine keeps refusing. The caller plays <see cref="Current"/> and reports the outcome;
/// <see cref="Report"/> returns the error line when it gives up on a cue.</summary>
internal sealed class PrewarmProgress
{
    /// <summary>A cue refused this many times in a row (one try per call, so seconds of frames) ends the run with an error line.</summary>
    internal const int MaxRefused = 600;

    private readonly string _identity;
    private readonly string[] _cues;
    private int _next, _refused;


    internal PrewarmProgress(string identity, string[] cues)
    {
        _identity = identity;
        _cues = cues;
        Done = cues.Length == 0;
    }

    /// <summary>The names of the cues that do not loop, in the given order: a loop starts with the thing it sounds on and is not prewarmed.</summary>
    internal static string[] OneShots(IEnumerable<CueDefinition> cues)
    {
        var names = new List<string>();
        foreach (var cue in cues)
            if (!cue.Loop) names.Add(cue.Name);
        return names.ToArray();
    }

    internal bool Done { get; private set; }

    /// <summary>The cue to start silently now; null once <see cref="Done"/>.</summary>
    internal string? Current => Done ? null : _cues[_next];

    /// <summary>Records what the silent play of <see cref="Current"/> did. <see cref="PrewarmStep.Waiting"/> (no listener yet: the level
    /// is still building) tries the same cue again without counting; <see cref="PrewarmStep.Refused"/> counts and ends the run after
    /// <see cref="MaxRefused"/> tries. Returns the error line that ends the run, or null while it goes on.</summary>
    internal string? Report(PrewarmStep step, string detail = "")
    {
        if (Done) return null;
        switch (step)
        {
            case PrewarmStep.Waiting:
                return null;
            case PrewarmStep.Refused:
                if (++_refused < MaxRefused) return null;
                Done = true;
                return _identity + " sounds could not be prewarmed: " + _cues[_next] + " was refused " + _refused + " times (" + detail + ").";
            default:
                _refused = 0;
                if (++_next >= _cues.Length) Done = true;
                return null;
        }
    }
}

/// <summary>Which of the cues a mod needs a library does not have.</summary>
internal static class CueRequirement
{
    /// <summary>The missing cue names joined with ", ", or null when every cue is there.</summary>
    internal static string? Missing(Func<string, bool> has, IEnumerable<string> cues)
    {
        List<string>? missing = null;
        foreach (var cue in cues)
            if (!has(cue)) (missing ??= new List<string>()).Add(cue);
        return missing == null ? null : string.Join(", ", missing);
    }
}
