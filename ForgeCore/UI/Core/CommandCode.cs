using System.Collections.Generic;

namespace ForgeUI;

/// <summary>The state of one entry of a command list, from the two numbers a mod knows: the uses left and the cooldown left.</summary>
public readonly struct CommandState
{
    public CommandState(int uses, int cooldownSeconds)
    {
        Uses = uses;
        CooldownSeconds = cooldownSeconds;
    }

    /// <summary>Uses left; negative means no limit, 0 means used up.</summary>
    public int Uses { get; }

    /// <summary>Whole seconds of cooldown left; 0 or less means not cooling.</summary>
    public int CooldownSeconds { get; }

    public bool Cooling => CooldownSeconds > 0;

    public bool Empty => Uses == 0;

    /// <summary>Not cooling and not used up.</summary>
    public bool Ready => !Cooling && !Empty;
}

/// <summary>Arithmetic behind <see cref="HudCommandList"/> that needs no game.</summary>
internal static class CommandCode
{
    /// <summary>True while <paramref name="code"/> still begins with everything typed so far (an empty input begins every code).</summary>
    public static bool StartsWith<T>(IReadOnlyList<T> code, IReadOnlyList<T> typed)
    {
        if (typed.Count > code.Count) return false;
        var same = EqualityComparer<T>.Default;
        for (var i = 0; i < typed.Count; i++)
            if (!same.Equals(code[i], typed[i])) return false;
        return true;
    }
}
