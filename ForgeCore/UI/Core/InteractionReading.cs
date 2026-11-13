using System;

namespace ForgeUI;

/// <summary>How the game shows its interaction prompt or its message line (the game's <c>ePUIMessageStyle</c>).</summary>
public enum InteractionStyle : byte
{
    /// <summary>A prompt the player can use now.</summary>
    Usable,
    /// <summary>A plain message: a reactor or uplink step, a terminal's or a door's note, being downed.</summary>
    Message,
    /// <summary>A scan in progress (a bioscan, a team scan).</summary>
    Scan,
    /// <summary>Something that cannot be used now (a key item missing, a locked door).</summary>
    Warning,
    /// <summary>A scan that sounds an alarm.</summary>
    ScanAlarm,
}

/// <summary>How many players a scan wants inside (the game's <c>PlayerRequirement</c>).</summary>
public enum ScanRequirement : byte
{
    /// <summary>Anyone inside moves it.</summary>
    Any,
    /// <summary>Every player has to be inside.</summary>
    All,
    /// <summary>Exactly one player inside.</summary>
    Solo,
}

/// <summary>The pure parts of reading the game's interaction layer (no Unity types), shared by <c>UIKit.Interaction</c> and
/// <c>UIKit.Scan</c>.</summary>
internal static class InteractionReading
{
    /// <summary>The game's <c>ePUIMessageStyle</c> number (Default, Message, Bioscan, Warning, BioscanAlarm) as an
    /// <see cref="InteractionStyle"/>; a number the game does not have reads as a plain message.</summary>
    internal static InteractionStyle Style(int game) => game switch
    {
        0 => InteractionStyle.Usable,
        1 => InteractionStyle.Message,
        2 => InteractionStyle.Scan,
        3 => InteractionStyle.Warning,
        4 => InteractionStyle.ScanAlarm,
        _ => InteractionStyle.Message,
    };

    /// <summary>The game's <c>PlayerRequirement</c> number (None, All, Solo); an unknown one reads as <see cref="ScanRequirement.Any"/>.</summary>
    internal static ScanRequirement Requirement(int game) => game switch
    {
        1 => ScanRequirement.All,
        2 => ScanRequirement.Solo,
        _ => ScanRequirement.Any,
    };

    /// <summary>The fill of the bar the game draws under a prompt or its message line (a sprite <paramref name="width"/> wide out of
    /// <paramref name="full"/>): 0..1, or −1 while the bar is not shown or has no width (the game then draws nothing).</summary>
    internal static float Fill(bool shown, float width, float full)
    {
        if (!shown || !(full > 0f) || !(width > 0f)) return -1f;
        return Math.Clamp(width / full, 0f, 1f);
    }
}
