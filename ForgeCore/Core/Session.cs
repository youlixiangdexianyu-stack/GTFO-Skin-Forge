namespace ForgeCore;

/// <summary>Who this machine is in the network session: the host's lookup (<see cref="HostLookup"/>), this player's own
/// (<see cref="LocalLookup"/>) and whether they are the same (<see cref="IsHost"/>). All three are read from numbers ForgeCore keeps
/// current when the game says the session changed or found its host (<see cref="GameEvents.MasterChanged"/>), so asking costs no game call and, at a host
/// migration, <see cref="IsHost"/> flips in the same moment the game's own <c>SNet.IsMaster</c> does. Everything is 0 or false with no
/// session. Main thread only.</summary>
public static class Session
{
    /// <summary>The session host's player lookup, 0 with no session.</summary>
    public static ulong HostLookup => GameEvents.Master;

    /// <summary>This machine's own player lookup, 0 until the game has a local network player.</summary>
    public static ulong LocalLookup { get; private set; }

    /// <summary>True when this machine hosts the session (the game's <c>SNet.IsMaster</c>); false with no session.</summary>
    public static bool IsHost => GameEvents.Master != 0 && GameEvents.Master == LocalLookup;

    /// <summary>The game's local network player, read before the host is, so a <see cref="GameEvents.MasterChanged"/> handler sees both.
    /// True when it changed: <see cref="IsHost"/> depends on it, so the caller reports the session again.</summary>
    internal static bool NoteLocal(ulong lookup)
    {
        if (LocalLookup == lookup) return false;
        LocalLookup = lookup;
        return true;
    }

    internal static void Reset() => LocalLookup = 0;
}
