using System;

namespace ForgeCore;

/// <summary>
/// The game's discrete state changes as events, so a mod subscribes once instead of reading the game every frame to notice them:
/// the session's host (<see cref="MasterChanged"/>), the game's text language (<see cref="LanguageChanged"/>), the end of a level
/// (<see cref="LevelEnded"/>), a checkpoint reload (<see cref="CheckpointReloaded"/>) and the local player going down
/// (<see cref="LocalPlayerDown"/>), the host lobby opening or closing (<see cref="LobbyChanged"/>) and the expedition being chosen
/// (<see cref="ExpeditionSelected"/>). Each is raised on the main thread from the game's own entry for that change; ForgeCore polls none
/// of them. A handler that throws is logged once and the other handlers still run. Main thread only.
/// </summary>
public static class GameEvents
{
    private static Action? _master, _migrated, _language, _levelEnded, _checkpointReached, _checkpointReloaded, _localPlayerDown, _state;
    private static Action<bool>? _lobby;
    private static Action<uint?, int, int>? _expeditionSelected;

    /// <summary>Called the first time something needs the language, player-down or expedition-selection entry, so its patch goes on only
    /// then (set by the plugin; offline nothing is patched).</summary>
    internal static Action NeedLanguage { get; set; } = static () => { };
    internal static Action NeedLocalPlayerDown { get; set; } = static () => { };
    internal static Action NeedExpeditionSelected { get; set; } = static () => { };

    /// <summary>True while the game is in its lobby state. Updated before <see cref="LobbyChanged"/> is raised.</summary>
    public static bool InLobby { get; private set; }

    /// <summary>Raised when the game enters (true) or leaves (false) its lobby state.</summary>
    public static event Action<bool>? LobbyChanged { add => _lobby += value; remove => _lobby -= value; }

    /// <summary>Raised after the game changed state, also when the host creates a lobby (the game's own <c>GameStateManager.OnGameStateChanged</c>
    /// delegate added from a mod misses that one). For a cache of something a scene owns; it says where the game is, not that a world was built.</summary>
    public static event Action? StateChanged { add => _state += value; remove => _state -= value; }

    /// <summary>Raised after the expedition was chosen, on every player: on the host from <c>RundownManager.MasterSelectActiveExpedition</c>
    /// (after the game stored the choice), on a client from <c>RundownManager.SetActiveExpedition</c> (the game storing the host's choice,
    /// also when it stores the same one again). The Local rundown's block id (null for any other rundown key), the tier (the game's
    /// <c>eRundownTier</c> value) and the zero-based index inside the tier.</summary>
    public static event Action<uint?, int, int>? ExpeditionSelected
    {
        add { NeedExpeditionSelected(); _expeditionSelected += value; }
        remove => _expeditionSelected -= value;
    }

    /// <summary>The host of the current session (its player lookup), 0 when there is no session. Updated before
    /// <see cref="MasterChanged"/> is raised.</summary>
    public static ulong Master { get; private set; }

    /// <summary>Raised when <see cref="Master"/> changes: a session starts or ends, or the host migrates.</summary>
    public static event Action? MasterChanged { add => _master += value; remove => _master -= value; }

    /// <summary>Raised when the game has chosen and recalled a replacement host after a migration, also when the same player stays host
    /// (<see cref="MasterChanged"/> does not fire then).</summary>
    public static event Action? MasterMigrated { add => _migrated += value; remove => _migrated -= value; }

    /// <summary>Goes up by one every time the game sets its text language (also when it sets the same one again). A reader that caches
    /// text compares this one number instead of reading the language. Counting starts when the first reader asks; 0 until a change.</summary>
    public static int LanguageRevision
    {
        get { NeedLanguage(); return _languageRevision; }
    }
    private static int _languageRevision;

    /// <summary>Raised after the game set its text language, with <see cref="LanguageRevision"/> already raised.</summary>
    public static event Action? LanguageChanged
    {
        add { NeedLanguage(); _language += value; }
        remove => _language -= value;
    }

    /// <summary>Raised when the level is cleaned up (the expedition ended, failed or was left).</summary>
    public static event Action? LevelEnded { add => _levelEnded += value; remove => _levelEnded -= value; }

    /// <summary>Raised when the game stores a checkpoint, after ForgeCore's own modules have captured their state.</summary>
    public static event Action? CheckpointReached { add => _checkpointReached += value; remove => _checkpointReached -= value; }

    /// <summary>Raised when the game reloads the last checkpoint, after ForgeCore's own modules have recalled their state.</summary>
    public static event Action? CheckpointReloaded { add => _checkpointReloaded += value; remove => _checkpointReloaded -= value; }

    /// <summary>Raised when the local player goes down (the game's own death of the local player's damage base).</summary>
    public static event Action? LocalPlayerDown
    {
        add { NeedLocalPlayerDown(); _localPlayerDown += value; }
        remove => _localPlayerDown -= value;
    }

    /// <summary>The game says the session may have changed: <paramref name="master"/> is the host it reads now.
    /// <paramref name="localChanged"/>: this player's own lookup changed too, which can flip <see cref="Session.IsHost"/> with the same host.
    /// <paramref name="settled"/> runs once <see cref="Master"/> holds the new host and before <see cref="MasterChanged"/> is raised:
    /// the network moves its world there, so its world-change handlers and every mod's host-change handler read the new host alike.</summary>
    internal static void NoteMaster(ulong master, bool localChanged = false, Action? settled = null)
    {
        var changed = master != Master || localChanged && master != 0;
        Master = master;
        settled?.Invoke();
        if (changed) Raise(_master, "master-changed");
    }

    internal static void NoteMasterMigrated() => Raise(_migrated, "master-migrated");

    internal static void NoteLanguage()
    {
        _languageRevision++;
        Raise(_language, "language-changed");
    }

    internal static void NoteLevelEnded() => Raise(_levelEnded, "level-ended");
    internal static void NoteCheckpointReached() => Raise(_checkpointReached, "checkpoint-reached");
    internal static void NoteCheckpointReloaded() => Raise(_checkpointReloaded, "checkpoint-reloaded");
    internal static void NoteLocalPlayerDown() => Raise(_localPlayerDown, "local-player-down");

    /// <summary>Records the lobby state without raising anything, for the state the game is already in when ForgeCore starts.</summary>
    internal static void SeedLobby(bool inLobby) => InLobby = inLobby;

    internal static void NoteState() => Raise(_state, "state-changed");

    internal static void NoteLobby(bool inLobby)
    {
        if (inLobby == InLobby) return;
        InLobby = inLobby;
        Raise(_lobby, inLobby, "lobby-changed");
    }

    internal static void NoteExpeditionSelected(uint? rundownId, int tier, int index)
        => Log.For("ForgeCore").RaiseEach(_expeditionSelected, rundownId, tier, index, "core-expedition-selected-handler-failed", "expedition-selected");

    private static void Raise(Action<bool>? handlers, bool value, string key) => Log.For("ForgeCore").RaiseEach(handlers, value, "core-handler-failed", key);

    private static void Raise(Action? handlers, string key) => Log.For("ForgeCore").RaiseEach(handlers, "core-handler-failed", key);

    /// <summary>Offline tests start from no session and no subscriber.</summary>
    internal static void Reset()
    {
        _master = _migrated = _language = _levelEnded = _checkpointReached = _checkpointReloaded = _localPlayerDown = _state = null;
        _lobby = null; _expeditionSelected = null;
        Master = 0; InLobby = false;
        Session.Reset();
        _languageRevision = 0;
    }
}
