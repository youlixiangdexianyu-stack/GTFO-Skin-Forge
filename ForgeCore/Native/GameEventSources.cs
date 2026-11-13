using System;
using GTFO.API;
using HarmonyLib;
using Localization;
using SNetwork;

namespace ForgeCore;

/// <summary>Where <see cref="GameEvents"/> hears the game. The host: <c>SNet_Events.OnMasterChanged</c> (the delegate the game's own
/// <c>SNet_GlobalManager</c> listens on), with the session reset and player join/leave delegates as the other moments the host can
/// change; each one reads <c>SNet.Master</c> once and tells <see cref="Net"/> to read the session's members. Level end and checkpoint reload: GTFO-API's <c>LevelAPI.OnLevelCleanup</c> and
/// <c>EventAPI.OnCheckpointReached</c> and <c>OnCheckpointReloaded</c>. The language and the local player's death are patches put on the first time something
/// subscribes (<see cref="LanguageHooks"/>, <see cref="LocalPlayerDownHooks"/>, <see cref="ExpeditionSelectedHooks"/>). The lobby is GTFO-API's
/// <c>EventAPI.OnGameStateChanged</c>: the game's own <c>GameStateManager.OnGameStateChanged</c> delegate added from a mod never fires
/// when the host creates a lobby, so the lobby state would stay false.</summary>
internal static class GameEventSources
{
    private static Il2CppSystem.Action? _session;

    internal static void Start()
    {
        GameEvents.NeedLanguage = static () => Patches.Ensure(Modules.Language, typeof(LanguageHooks));
        GameEvents.NeedLocalPlayerDown = static () => LocalPlayerDownHooks.Ensure();
        GameEvents.NeedExpeditionSelected = static () => Patches.Ensure(Modules.ExpeditionSelected, typeof(ExpeditionSelectedHooks));
        EventAPI.OnGameStateChanged += StateChanged;
        GameEvents.SeedLobby(GameStateManager.CurrentStateName == eGameStateName.Lobby);
        // The game's events are IL2CPP delegates: the system delegate is materialized first and the implicit conversion takes it.
        _session = (Action)ReadMaster;
        SNet_Events.OnMasterChanged += _session;
        SNet_Events.OnResetSessionEvent += _session;
        SNet_Events.OnPlayerJoin += _session;
        SNet_Events.OnPlayerLeave += _session;
        // The game's join events run before the session lists a player and none follows, so the member list is read when the session
        // lists or drops one (the events above still cover a host change or session reset).
        GameHooks.SessionPlayerAdded.After(static (player, _) => Listed(player, true));
        GameHooks.SessionPlayerRemoved.After(static (player, _) => Listed(player, false));
        // A new session's host is found after its join events, and a player alone gets no later event: read it here.
        GameHooks.MasterFound.After(static (_, _) => ReadMaster());
        GameHooks.MasterMigrationFound.After(static (_, _) => GameEvents.NoteMasterMigrated());
        LevelAPI.OnLevelCleanup += static () => { Net.Node!.NoteWorld(NetWorldReason.Cleanup); GameEvents.NoteLevelEnded(); };
        EventAPI.OnCheckpointReached += GameEvents.NoteCheckpointReached;
        EventAPI.OnCheckpointReloaded += static () => { Net.Node!.NoteWorld(NetWorldReason.Checkpoint); Checkpoint.NoteReloaded(); };
        // No read here: at plugin load there is no session yet, and the first session event reads the host.
    }

    /// <summary>The call's own player is handed to <see cref="Net"/>, so the list is right whatever <c>PlayersInSession</c> holds at that moment.</summary>
    private static void Listed(SNet_Player? player, bool added)
    {
        if (player != null) Players.NoteSession(new SessionPlayer(player.Lookup, player.IsBot, player.IsLocal), added);
        if (player == null || player.IsBot || player.IsLocal) { Net.Node!.NoteSession(); return; }
        if (added) Net.Node!.NotePlayerAdded(player.Lookup);
        else Net.Node!.NotePlayerRemoved(player.Lookup);
    }

    private static void StateChanged(eGameStateName oldState, eGameStateName newState)
    {
        GameEvents.NoteLobby(newState == eGameStateName.Lobby);
        GameEvents.NoteState();
    }

    private static void ReadMaster()
    {
        Net.Node!.NoteSession();
        try
        {
            var localChanged = Session.NoteLocal(SNet.LocalPlayer?.Lookup ?? 0);
            // The network reads the session now, not at the next frame: a host change moves the world after Session.IsHost flipped
            // and before any mod's MasterChanged handler runs.
            GameEvents.NoteMaster(SNet.Master?.Lookup ?? 0, localChanged, static () => Net.Node!.Settle());
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-reading-session-host-failed", "Reading the session host failed: " + error); }
    }
}

/// <summary>Put on the first time the language is asked for (<see cref="GameEvents.LanguageChanged"/>,
/// <see cref="GameEvents.LanguageRevision"/>). <c>Text.TextLocalizationService</c> is this class; the game sets its language only
/// through this method.</summary>
internal static class LanguageHooks
{
    [HarmonyPatch(typeof(GameDataTextLocalizationService), nameof(GameDataTextLocalizationService.SetCurrentLanguage))]
    [HarmonyPostfix]
    private static void Set() => GameEvents.NoteLanguage();
}

/// <summary>Put on the first time something needs <see cref="GameEvents.ExpeditionSelected"/>. The parameter positions are those of
/// <c>RundownManager.MasterSelectActiveExpedition(eRundownKey, eRundownTier, int, string, uint)</c>: the rundown key, the tier, the index and,
/// last, the rundown block id the Local key selects.</summary>
internal static class ExpeditionSelectedHooks
{
    [HarmonyPatch(typeof(RundownManager), nameof(RundownManager.MasterSelectActiveExpedition))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Selected(eRundownKey __0, eRundownTier __1, int __2, uint __4)
        => GameEvents.NoteExpeditionSelected(__0 == eRundownKey.Local ? __4 : null, (int)__1, __2);

    /// <summary>A client: the game stores the host's choice (<c>RundownManager.SetActiveExpedition(pActiveExpedition, ExpeditionInTierData,
    /// bool)</c>, which the host's choice reaches on every other peer), so the event is raised there with the stored rundown key, tier and
    /// index. The host raises from <see cref="Selected"/> and is skipped here, so no choice is raised twice.</summary>
    [HarmonyPatch(typeof(RundownManager), nameof(RundownManager.SetActiveExpedition))]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Stored(pActiveExpedition __0)
    {
        if (Session.IsHost) return;
        GameEvents.NoteExpeditionSelected(__0.rundownType == eRundownKey.Local ? Globals.Global.RundownIdToLoad : null, (int)__0.tier, __0.expeditionIndex);
    }
}

/// <summary>Subscribed the first time something needs the local player's death (<see cref="GameEvents.LocalPlayerDown"/>, an input
/// lease): <see cref="GameHooks.LocalPlayerDied"/>.</summary>
internal static class LocalPlayerDownHooks
{
    private static bool _on;

    internal static void Ensure()
    {
        if (_on) return;
        _on = true;
        GameHooks.LocalPlayerDied.After(static (_, _) => GameEvents.NoteLocalPlayerDown());
    }
}
