using System;
using Agents;
using ChainedPuzzles;
using Enemies;
using Gear;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeCore;

/// <summary>Which of a player's damage receive entries ran (<c>Dam_PlayerDamageBase.Receive*Damage</c>).</summary>
public enum PlayerReceiveKind { Bullet, Melee, Explosion, TentacleAttack, ShooterProjectile, Fall }

/// <summary>One receive on a player: the entry, the receiver, the damage the packet carries scaled to the receiver's maximum health
/// (<c>data.damage.Get(HealthMax)</c>) and the packet's source (<c>default</c> for the entries whose packet names none).</summary>
public readonly record struct PlayerReceive(PlayerReceiveKind Kind, Dam_PlayerDamageBase Receiver, float Amount, pAgent Source);

/// <summary>Which <c>SentryGunInstance</c> method a <see cref="GameHooks.SentryLifecycle"/> call is.</summary>
public enum SentryStep { GearSpawnComplete, PickUp, SyncedPickup, Despawn, Destroy, BackpackCleanup, CleanUp }

/// <summary>One call of a sentry's lifecycle: the sentry, the method, and the player who picks it up (<c>PickUp</c> and
/// <c>SyncedPickup</c>; null for the other steps).</summary>
public readonly record struct SentryCall(SentryGunInstance Sentry, SentryStep Step, PlayerAgent? Actor);

/// <summary>The game methods that more than one package reads, patched here once. Each member is a <see cref="Bracket{TCall}"/> over one
/// game method (or the same method of the families named): subscribe with <c>Before</c>, <c>After</c> or <c>Around</c>. ForgeCore
/// holds only how the game method is hung; what a subscriber does with the call is its own. Main thread only.</summary>
public static partial class GameHooks
{
    // ---- Level objects: the state callbacks of doors, terminals, generators, reactor, scans, containers and pickups, and noise.
    public static readonly Bracket<(LG_WeakDoor Door, pDoorState State)> WeakDoorState = new("GameHooks.WeakDoorState", typeof(WeakDoorStateHook));
    public static readonly Bracket<(LG_WardenObjective_Reactor Reactor, pReactorState Old, pReactorState New, bool Dropin)> ReactorState = new("GameHooks.ReactorState", typeof(ReactorStateHook));
    /// <summary>The reactor's text above its console, set every frame. A before handler may replace <see cref="ReactorMessageCall.Message"/>.</summary>
    public static readonly Bracket<ReactorMessageCall> ReactorMessage = new("GameHooks.ReactorMessage", typeof(ReactorMessageHook));
    public static readonly Bracket<(ChainedPuzzleInstance Puzzle, pChainedPuzzleState Old, pChainedPuzzleState New)> PuzzleState = new("GameHooks.PuzzleState", typeof(PuzzleStateHook));
    public static readonly Bracket<(LG_ResourceContainer_Sync Container, pResourceContainerItemState New)> ResourceContainerState = new("GameHooks.ResourceContainerState", typeof(ResourceContainerStateHook));
    public static readonly Bracket<(LG_PowerGenerator_Core Generator, pPowerGeneratorState New)> GeneratorState = new("GameHooks.GeneratorState", typeof(GeneratorStateHook));
    public static readonly Bracket<(LG_PickupItem_Sync Item, pPickupItemState Old, pPickupItemState New, bool Recall)> PickupItemState = new("GameHooks.PickupItemState", typeof(PickupItemStateHook));
    public static readonly Bracket<(LG_ComputerTerminalCommandInterpreter Interpreter, TERM_Command Command, string Line)> TerminalCommand = new("GameHooks.TerminalCommand", typeof(TerminalCommandHook));
    public static readonly Bracket<NM_NoiseData> NoiseMade = new("GameHooks.NoiseMade", typeof(NoiseMadeHook));

    // ---- Enemies.
    public static readonly Bracket<ES_ScoutScream> ScoutScreamActivated = new("GameHooks.ScoutScreamActivated", typeof(ScoutScreamActivatedHook));
    public static readonly Bracket<ES_ScoutDetection> ScoutDetectionEntered = new("GameHooks.ScoutDetectionEntered", typeof(ScoutDetectionEnteredHook));
    public static readonly Bracket<(Dam_EnemyDamageBase Receiver, pExplosionDamageData Data)> EnemyExplosionReceived = new("GameHooks.EnemyExplosionReceived", typeof(EnemyExplosionReceivedHook));
    public static readonly Bracket<SurvivalWave> WaveStopped = new("GameHooks.WaveStopped", typeof(WaveStoppedHook));
    /// <summary><c>ES_EnemyAttackBase.DoStartAttack</c> (strikers, tanks, shooters; the base body, no attack state overrides it): the attack state, where it
    /// aims and the animation it plays. Run on the host and, from the network, on a client.</summary>
    public static readonly Bracket<(ES_EnemyAttackBase State, Vector3 Target, int AnimIndex)> EnemyAttackStarted = new("GameHooks.EnemyAttackStarted", typeof(EnemyAttackStartedHook));
    /// <summary><c>ES_Hitreact.DoHitReact</c>: an enemy plays a hit reaction; the state's <c>m_data</c> holds its type, the side it was hit from and the animation index.</summary>
    public static readonly Bracket<ES_Hitreact> EnemyHitreacted = new("GameHooks.EnemyHitreacted", typeof(EnemyHitreactedHook));
    /// <summary>An animator was told to cross-fade to a state: every state an enemy enters goes through this one call. Costs a dictionary miss per cross-fade once someone subscribes.</summary>
    public static readonly Bracket<(Animator Animator, int Hash)> AnimatorCrossFaded = new("GameHooks.AnimatorCrossFaded", typeof(AnimatorCrossFadedHook));
    /// <summary><c>ES_HibernateWakeUp.ActivateState</c>: a sleeping enemy getting up, with the distance and delay the game gave it and whether another enemy's wake-up passed it on (<c>Skip</c> keeps the enemy asleep).</summary>
    public static readonly Bracket<(ES_HibernateWakeUp State, float Distance, float Delay, bool Propagated)> HibernateWakeActivated = new("GameHooks.HibernateWakeActivated", typeof(HibernateWakeActivatedHook));
    /// <summary><c>ES_Scream.ActivateState</c>: the host starts an enemy's scream.</summary>
    public static readonly Bracket<ES_Scream> EnemyScreamStarted = new("GameHooks.EnemyScreamStarted", typeof(EnemyScreamStartedHook));
    /// <summary><c>ES_Scream.OnStateShouldBeActivated</c>: an awake enemy's scream packet arrives (the entry other packages read).</summary>
    public static readonly Bracket<ES_Scream> EnemyScreamed = new("GameHooks.EnemyScreamed", typeof(EnemyScreamedHook));

    // ---- Players.
    /// <summary>The six damage entries a hit can arrive through (bullet, melee, explosion, tentacle, shooter projectile, fall), on
    /// <c>Dam_PlayerDamageBase</c> (bots and remote players) and on the local player's own <c>Dam_PlayerDamageLocal</c> overrides, which
    /// do not call the base bodies; the rarer entries are not part of it. <c>Skip</c> keeps the entry's body from running, so the
    /// damage never reaches the player (the accept path, the health write and the hit reaction all stay out).</summary>
    public static readonly Bracket<PlayerReceive> PlayerReceived = new("GameHooks.PlayerReceived", typeof(PlayerReceiveHooks));
    public static readonly Bracket<PLOC_Downed> PlayerRevived = new("GameHooks.PlayerRevived", typeof(PlayerRevivedHook));
    /// <summary>The game's set-health receive (<c>ReceiveSetHealth</c>, the player and the enemy override): the
    /// master's health value arriving on a client, and the revive's. The receiver's <c>Health</c> after the call is the value; no source
    /// travels with it.</summary>
    public static readonly Bracket<Dam_SyncedDamageBase> HealthSetReceived = new("GameHooks.HealthSetReceived", typeof(HealthSetReceivedHook));

    // ---- Weapons: the four firing bodies are four methods and four brackets; a family is the one the game runs.
    public static class Fire
    {
        public static readonly Bracket<BulletWeapon> BulletWeapon = new("GameHooks.Fire.BulletWeapon", typeof(BulletWeaponFireHook));
        public static readonly Bracket<Shotgun> Shotgun = new("GameHooks.Fire.Shotgun", typeof(ShotgunFireHook));
        public static readonly Bracket<BulletWeaponSynced> BulletWeaponSynced = new("GameHooks.Fire.BulletWeaponSynced", typeof(BulletWeaponSyncedFireHook));
        public static readonly Bracket<ShotgunSynced> ShotgunSynced = new("GameHooks.Fire.ShotgunSynced", typeof(ShotgunSyncedFireHook));
    }

    /// <summary><c>Weapon.CastWeaponRay(Transform muzzle, ref WeaponHitData, int mask)</c>: the origin is the muzzle's own position, which
    /// is not read here (a native read); the subscriber reads it when it needs it.</summary>
    public static readonly RayBracket MuzzleRay = new("GameHooks.MuzzleRay", typeof(MuzzleRayHook));
    /// <summary><c>Weapon.CastWeaponRay(Transform alignTransform, ref WeaponHitData, Vector3 originPos, int mask)</c>: the origin is the caller's own.</summary>
    public static readonly RayBracket AlignedRay = new("GameHooks.AlignedRay", typeof(AlignedRayHook));

    // ---- Equipment in the world.
    /// <summary><c>MeleeWeaponFirstPerson.DoAttackDamage</c>: a swing's hit on one target (<c>Skip</c> keeps the hit from being dealt).</summary>
    public static readonly Bracket<(MeleeWeaponFirstPerson Weapon, MeleeWeaponDamageData Data, bool IsPush)> MeleeAttackDamage = new("GameHooks.MeleeAttackDamage", typeof(MeleeAttackDamageHook));
    public static readonly Bracket<MWS_Push> PushEntered = new("GameHooks.PushEntered", typeof(PushEnteredHook));
    /// <summary><c>SentryGunInstance_Firing_Bullets.FireBullet</c>: a sentry's one shot, on every peer that draws it (the bullet's damage is the game's;
    /// subscribe After to read the muzzle, <c>firing.MuzzleAlign</c>, for a flash or a tracer).</summary>
    public static readonly Bracket<SentryGunInstance_Firing_Bullets> SentryFired = new("GameHooks.SentryFired", typeof(SentryFiredHook));
    /// <summary>Every method that moves a sentry in or out of its owner's backpack and the world, in one bracket: <c>OnGearSpawnComplete</c>,
    /// <c>PickUp</c>, <c>SyncedPickup</c>, <c>OnDespawn</c>, <c>OnDestroy</c>, <c>OnBackpackCleanupCallback</c> and <c>CleanUp</c> (see
    /// <see cref="SentryStep"/>). A subscriber filters on the step it wants (one patch per method for every subscriber).</summary>
    public static readonly Bracket<SentryCall> SentryLifecycle = new("GameHooks.SentryLifecycle", typeof(SentryLifecycleHook));
    public static readonly Bracket<MineDeployerInstance> MineSpawned = new("GameHooks.MineSpawned", typeof(MineSpawnedHook));
    public static readonly Bracket<(MineDeployerInstance Mine, PlayerAgent Actor)> MinePickedUp = new("GameHooks.MinePickedUp", typeof(MinePickedUpHook));

    // ---- Items: the one entry every client builds an Item through (first person, third person, pickup, instance).
    public static readonly Bracket<ItemSpawn> ItemSpawned = new("GameHooks.ItemSpawned", typeof(ItemSpawnedHook));

    // ---- The local player's first-person view.
    /// <summary><c>FirstPersonItemHolder.LateUpdate</c>: the holder of the local player's first-person arms and gun has placed itself for this frame; a
    /// subscriber that moves it moves the arms and the gun with it (the body of the player stays where it is).</summary>
    public static readonly Bracket<FirstPersonItemHolder> FirstPersonHolderLateUpdate = new("GameHooks.FirstPersonHolderLateUpdate", typeof(FirstPersonHolderLateUpdateHook));
    /// <summary><c>PlayerSyncIK.UpdateIK</c>: a third-person body's arms have been put on its item's grips (FinalIK limbs) for this frame. The game runs it
    /// from a LateUpdate whose order against a mod's own is not fixed; read or copy a solved third-person pose after it.</summary>
    public static readonly Bracket<PlayerSyncIK> SyncIkSolved = new("GameHooks.SyncIkSolved", typeof(SyncIkSolvedHook));

    // ---- The game's event log.
    public static readonly Bracket<(string Log, eGameEventChatLogType Type)> GameLogAdded = new("GameHooks.GameLogAdded", typeof(GameLogAddedHook));
}

/// <summary>The ray cast of a weapon (<see cref="GameHooks.MuzzleRay"/>, <see cref="GameHooks.AlignedRay"/>): like <see cref="Bracket{TCall}"/>
/// but the hit data and the result are handed by reference, so a subscriber can change what the game reads back.</summary>
public sealed class RayBracket
{
    /// <summary>Before the cast; the state comes back to <see cref="RayExit{TState}"/> and <see cref="RayFaulted{TState}"/>.</summary>
    public delegate TState RayEnter<TState>(Transform transform, ref Weapon.WeaponHitData data, Vector3 origin);
    /// <summary>After the cast returned (<c>ran</c> false when a prefix skipped it); <c>result</c> is what the game returns.</summary>
    public delegate void RayExit<TState>(TState state, Transform transform, ref Weapon.WeaponHitData data, Vector3 origin, ref bool result, bool ran);
    /// <summary>The cast threw; the exception is still the game's to throw.</summary>
    public delegate void RayFaulted<TState>(TState state);

    private interface ITap
    {
        PatchKinds Kinds { get; }
        void Enter(Transform transform, ref Weapon.WeaponHitData data, Vector3 origin);
        void Exit(Transform transform, ref Weapon.WeaponHitData data, Vector3 origin, ref bool result, bool ran);
        void Faulted();
    }

    private sealed class Tap<TState> : ITap
    {
        private readonly RayBracket _owner;
        private readonly RayEnter<TState> _enter;
        private readonly RayExit<TState>? _exit;
        private readonly RayFaulted<TState>? _faulted;
        private readonly System.Collections.Generic.Stack<TState>? _states;

        internal Tap(RayBracket owner, RayEnter<TState> enter, RayExit<TState>? exit, RayFaulted<TState>? faulted)
        {
            _owner = owner;
            _enter = enter;
            _exit = exit;
            _faulted = faulted;
            // As Bracket: a state kept for an exit or a fault is taken back at either end, so both are patched while it is kept.
            if (exit != null || faulted != null) _states = new();
            Kinds = _states != null ? PatchKinds.Prefix | PatchKinds.Postfix | PatchKinds.Finalizer : PatchKinds.Prefix;
        }

        public PatchKinds Kinds { get; }

        public void Enter(Transform transform, ref Weapon.WeaponHitData data, Vector3 origin)
        {
            var state = default(TState)!;
            try { state = _enter(transform, ref data, origin); }
            catch (Exception error) { _owner.Failed("enter", error); }
            _states?.Push(state);
        }

        public void Exit(Transform transform, ref Weapon.WeaponHitData data, Vector3 origin, ref bool result, bool ran)
        {
            if (_states == null || !_states.TryPop(out var state)) return;
            try { _exit?.Invoke(state, transform, ref data, origin, ref result, ran); }
            catch (Exception error) { _owner.Failed("exit", error); }
        }

        public void Faulted()
        {
            if (_states == null || !_states.TryPop(out var state)) return;
            try { _faulted?.Invoke(state); }
            catch (Exception error) { _owner.Failed("faulted", error); }
        }
    }

    private readonly string _module;
    private readonly Type _patches;
    private ITap[] _taps = [];
    private PatchKinds _on;
    private long _calls;

    internal RayBracket(string module, Type patches)
    {
        _module = module;
        _patches = patches;
    }

    internal bool Active => _taps.Length != 0;

    /// <summary>The module name, as ForgeCore logs it.</summary>
    public string Module => _module;

    /// <summary>Casts seen since the process started (counted at the prefix; nothing while no one subscribes).</summary>
    public long Calls => _calls;

    /// <summary>Subscribes around the cast: a prefix that runs first and, when an exit or a faulted is given, a postfix and finalizer
    /// that run last, so the subscriber reads the cast around every other patch of it. Disposing the result ends the subscription and
    /// takes off what no other subscription needs.</summary>
    public IDisposable Around<TState>(RayEnter<TState> enter, RayExit<TState>? exit, RayFaulted<TState>? faulted = null)
    {
        ITap tap = new Tap<TState>(this, enter ?? throw new ArgumentNullException(nameof(enter)), exit, faulted);
        Apply([.. _taps, tap]);
        return new Subscription(() => Apply(Array.FindAll(_taps, other => !ReferenceEquals(other, tap))));
    }

    private void Apply(ITap[] taps)
    {
        var wanted = PatchKinds.None;
        foreach (var tap in taps) wanted |= tap.Kinds;
        if ((wanted & ~_on) != 0) _on = Patches.Set(_module, _patches, _on | wanted);
        _taps = taps;
        if ((_on & ~wanted) != 0) _on = Patches.Set(_module, _patches, wanted);
    }

    internal void RunEnter(Transform transform, ref Weapon.WeaponHitData data, Vector3 origin)
    {
        _calls++;
        foreach (var tap in _taps) tap.Enter(transform, ref data, origin);
    }

    internal void RunExit(Transform transform, ref Weapon.WeaponHitData data, Vector3 origin, ref bool result, bool ran)
    {
        var taps = _taps;
        for (var i = taps.Length - 1; i >= 0; i--) taps[i].Exit(transform, ref data, origin, ref result, ran);
    }

    internal void RunFaulted()
    {
        var taps = _taps;
        for (var i = taps.Length - 1; i >= 0; i--) taps[i].Faulted();
    }

    private void Failed(string end, Exception error)
        => Plugin.Logger.ErrorOnce("core-game-hooks-handler-failed", "A " + _module + " handler failed (" + end + "): " + error);
}

// The patch classes. Each target is one class: a prefix at Priority.First opens the call, a postfix at Priority.Last closes it, a
// finalizer at Priority.Last closes a call whose body threw. Only the kinds the bracket's subscriptions need are on (Patches.Set), so
// each end builds the call from the method's own arguments; a class whose postfix or finalizer reads the prefix's __state (a call the
// prefix builds or a subscriber rewrites) always gets its prefix with them.

[HarmonyPatch(typeof(LG_WeakDoor), nameof(LG_WeakDoor.OnSyncDoorStateChange))]
internal static class WeakDoorStateHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_WeakDoor __instance, pDoorState state) => GameHooks.WeakDoorState.RunBefore((__instance, state));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(LG_WeakDoor __instance, pDoorState state, bool __runOriginal) => GameHooks.WeakDoorState.RunAfter((__instance, state), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, LG_WeakDoor __instance, pDoorState state) { if (__exception != null) GameHooks.WeakDoorState.RunFaulted((__instance, state), __exception); return __exception; }
}

/// <summary>One call of the reactor's text. The same object is reused for every call, so this per-frame hook allocates nothing;
/// a handler reads it during the call and does not keep it.</summary>
public sealed class ReactorMessageCall
{
    public LG_WardenObjective_Reactor Reactor { get; internal set; } = null!;
    public bool Visible { get; internal set; }
    public string Message { get; set; } = "";
    public string TimerPrefix { get; internal set; } = "";
    public string TimerSuffix { get; internal set; } = "";

    internal ReactorMessageCall Fill(LG_WardenObjective_Reactor reactor, bool visible, string message, string timerPrefix, string timerSuffix)
    {
        Reactor = reactor; Visible = visible; Message = message; TimerPrefix = timerPrefix; TimerSuffix = timerSuffix;
        return this;
    }
}

[HarmonyPatch(typeof(LG_WardenObjective_Reactor), nameof(LG_WardenObjective_Reactor.SetGUIMessage))]
internal static class ReactorMessageHook
{
    private static readonly ReactorMessageCall Call = new();

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_WardenObjective_Reactor __instance, bool visible, ref string msg, string timerPrefix, string timerSuffix)
    {
        GameHooks.ReactorMessage.RunBefore(Call.Fill(__instance, visible, msg, timerPrefix, timerSuffix));
        msg = Call.Message;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(LG_WardenObjective_Reactor __instance, bool visible, string msg, string timerPrefix, string timerSuffix, bool __runOriginal)
        => GameHooks.ReactorMessage.RunAfter(Call.Fill(__instance, visible, msg, timerPrefix, timerSuffix), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, LG_WardenObjective_Reactor __instance, bool visible, string msg, string timerPrefix, string timerSuffix) { if (__exception != null) GameHooks.ReactorMessage.RunFaulted(Call.Fill(__instance, visible, msg, timerPrefix, timerSuffix), __exception); return __exception; }
}

[HarmonyPatch(typeof(LG_WardenObjective_Reactor), nameof(LG_WardenObjective_Reactor.OnStateChange))]
internal static class ReactorStateHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_WardenObjective_Reactor __instance, pReactorState oldState, pReactorState newState, bool isDropinState) => GameHooks.ReactorState.RunBefore((__instance, oldState, newState, isDropinState));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(LG_WardenObjective_Reactor __instance, pReactorState oldState, pReactorState newState, bool isDropinState, bool __runOriginal) => GameHooks.ReactorState.RunAfter((__instance, oldState, newState, isDropinState), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, LG_WardenObjective_Reactor __instance, pReactorState oldState, pReactorState newState, bool isDropinState) { if (__exception != null) GameHooks.ReactorState.RunFaulted((__instance, oldState, newState, isDropinState), __exception); return __exception; }
}

[HarmonyPatch(typeof(ChainedPuzzleInstance), nameof(ChainedPuzzleInstance.OnStateChange))]
internal static class PuzzleStateHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ChainedPuzzleInstance __instance, pChainedPuzzleState oldState, pChainedPuzzleState newState) => GameHooks.PuzzleState.RunBefore((__instance, oldState, newState));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ChainedPuzzleInstance __instance, pChainedPuzzleState oldState, pChainedPuzzleState newState, bool __runOriginal) => GameHooks.PuzzleState.RunAfter((__instance, oldState, newState), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ChainedPuzzleInstance __instance, pChainedPuzzleState oldState, pChainedPuzzleState newState) { if (__exception != null) GameHooks.PuzzleState.RunFaulted((__instance, oldState, newState), __exception); return __exception; }
}

[HarmonyPatch(typeof(LG_ResourceContainer_Sync), nameof(LG_ResourceContainer_Sync.OnStateChange))]
internal static class ResourceContainerStateHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_ResourceContainer_Sync __instance, pResourceContainerItemState newState) => GameHooks.ResourceContainerState.RunBefore((__instance, newState));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(LG_ResourceContainer_Sync __instance, pResourceContainerItemState newState, bool __runOriginal) => GameHooks.ResourceContainerState.RunAfter((__instance, newState), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, LG_ResourceContainer_Sync __instance, pResourceContainerItemState newState) { if (__exception != null) GameHooks.ResourceContainerState.RunFaulted((__instance, newState), __exception); return __exception; }
}

[HarmonyPatch(typeof(LG_PowerGenerator_Core), nameof(LG_PowerGenerator_Core.OnStateChange))]
internal static class GeneratorStateHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_PowerGenerator_Core __instance, pPowerGeneratorState newState) => GameHooks.GeneratorState.RunBefore((__instance, newState));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(LG_PowerGenerator_Core __instance, pPowerGeneratorState newState, bool __runOriginal) => GameHooks.GeneratorState.RunAfter((__instance, newState), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, LG_PowerGenerator_Core __instance, pPowerGeneratorState newState) { if (__exception != null) GameHooks.GeneratorState.RunFaulted((__instance, newState), __exception); return __exception; }
}

[HarmonyPatch(typeof(LG_PickupItem_Sync), nameof(LG_PickupItem_Sync.OnStateChange))]
internal static class PickupItemStateHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_PickupItem_Sync __instance, pPickupItemState oldState, pPickupItemState newState, bool isRecall) => GameHooks.PickupItemState.RunBefore((__instance, oldState, newState, isRecall));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(LG_PickupItem_Sync __instance, pPickupItemState oldState, pPickupItemState newState, bool isRecall, bool __runOriginal) => GameHooks.PickupItemState.RunAfter((__instance, oldState, newState, isRecall), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, LG_PickupItem_Sync __instance, pPickupItemState oldState, pPickupItemState newState, bool isRecall) { if (__exception != null) GameHooks.PickupItemState.RunFaulted((__instance, oldState, newState, isRecall), __exception); return __exception; }
}

[HarmonyPatch(typeof(LG_ComputerTerminalCommandInterpreter), "ReceiveCommand")]
internal static class TerminalCommandHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(LG_ComputerTerminalCommandInterpreter __instance, TERM_Command cmd, string inputLine) => GameHooks.TerminalCommand.RunBefore((__instance, cmd, inputLine));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(LG_ComputerTerminalCommandInterpreter __instance, TERM_Command cmd, string inputLine, bool __runOriginal) => GameHooks.TerminalCommand.RunAfter((__instance, cmd, inputLine), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, LG_ComputerTerminalCommandInterpreter __instance, TERM_Command cmd, string inputLine) { if (__exception != null) GameHooks.TerminalCommand.RunFaulted((__instance, cmd, inputLine), __exception); return __exception; }
}

[HarmonyPatch(typeof(NoiseManager), nameof(NoiseManager.MakeNoise))]
internal static class NoiseMadeHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(NM_NoiseData noiseData) => GameHooks.NoiseMade.RunBefore(noiseData);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(NM_NoiseData noiseData, bool __runOriginal) => GameHooks.NoiseMade.RunAfter(noiseData, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, NM_NoiseData noiseData) { if (__exception != null) GameHooks.NoiseMade.RunFaulted(noiseData, __exception); return __exception; }
}

[HarmonyPatch(typeof(ES_ScoutScream), nameof(ES_ScoutScream.ActivateState))]
internal static class ScoutScreamActivatedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ES_ScoutScream __instance) => GameHooks.ScoutScreamActivated.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ES_ScoutScream __instance, bool __runOriginal) => GameHooks.ScoutScreamActivated.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ES_ScoutScream __instance) { if (__exception != null) GameHooks.ScoutScreamActivated.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(ES_ScoutDetection), nameof(ES_ScoutDetection.CommonEnter))]
internal static class ScoutDetectionEnteredHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ES_ScoutDetection __instance) => GameHooks.ScoutDetectionEntered.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ES_ScoutDetection __instance, bool __runOriginal) => GameHooks.ScoutDetectionEntered.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ES_ScoutDetection __instance) { if (__exception != null) GameHooks.ScoutDetectionEntered.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveExplosionDamage))]
internal static class EnemyExplosionReceivedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Dam_EnemyDamageBase __instance, pExplosionDamageData data) => GameHooks.EnemyExplosionReceived.RunBefore((__instance, data));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(Dam_EnemyDamageBase __instance, pExplosionDamageData data, bool __runOriginal) => GameHooks.EnemyExplosionReceived.RunAfter((__instance, data), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, Dam_EnemyDamageBase __instance, pExplosionDamageData data) { if (__exception != null) GameHooks.EnemyExplosionReceived.RunFaulted((__instance, data), __exception); return __exception; }
}

[HarmonyPatch(typeof(ES_EnemyAttackBase), nameof(ES_EnemyAttackBase.DoStartAttack))]
internal static class EnemyAttackStartedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ES_EnemyAttackBase __instance, Vector3 attackTargetPosition, int animIndex) => GameHooks.EnemyAttackStarted.RunBefore((__instance, attackTargetPosition, animIndex));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ES_EnemyAttackBase __instance, Vector3 attackTargetPosition, int animIndex, bool __runOriginal) => GameHooks.EnemyAttackStarted.RunAfter((__instance, attackTargetPosition, animIndex), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ES_EnemyAttackBase __instance, Vector3 attackTargetPosition, int animIndex) { if (__exception != null) GameHooks.EnemyAttackStarted.RunFaulted((__instance, attackTargetPosition, animIndex), __exception); return __exception; }
}

[HarmonyPatch(typeof(ES_Hitreact), nameof(ES_Hitreact.DoHitReact))]
internal static class EnemyHitreactedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ES_Hitreact __instance) => GameHooks.EnemyHitreacted.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ES_Hitreact __instance, bool __runOriginal) => GameHooks.EnemyHitreacted.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ES_Hitreact __instance) { if (__exception != null) GameHooks.EnemyHitreacted.RunFaulted(__instance, __exception); return __exception; }
}

internal static class EnemyScreamStartedHook
{
    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.ActivateState), new Type[0])]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void HostBefore(ES_Scream __instance) => GameHooks.EnemyScreamStarted.RunBefore(__instance);
    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.ActivateState), new Type[0])]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void HostAfter(ES_Scream __instance, bool __runOriginal) => GameHooks.EnemyScreamStarted.RunAfter(__instance, __runOriginal);
    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.ActivateState), new Type[0])]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? HostFaulted(Exception? __exception, ES_Scream __instance) { if (__exception != null) GameHooks.EnemyScreamStarted.RunFaulted(__instance, __exception); return __exception; }
}

internal static class EnemyScreamedHook
{
    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.OnStateShouldBeActivated))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void ClientBefore(ES_Scream __instance) => GameHooks.EnemyScreamed.RunBefore(__instance);
    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.OnStateShouldBeActivated))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void ClientAfter(ES_Scream __instance, bool __runOriginal) => GameHooks.EnemyScreamed.RunAfter(__instance, __runOriginal);
    [HarmonyPatch(typeof(ES_Scream), nameof(ES_Scream.OnStateShouldBeActivated))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? ClientFaulted(Exception? __exception, ES_Scream __instance) { if (__exception != null) GameHooks.EnemyScreamed.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(PlayerSyncIK), nameof(PlayerSyncIK.UpdateIK))]
internal static class SyncIkSolvedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(PlayerSyncIK __instance) => GameHooks.SyncIkSolved.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(PlayerSyncIK __instance, bool __runOriginal) => GameHooks.SyncIkSolved.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, PlayerSyncIK __instance) { if (__exception != null) GameHooks.SyncIkSolved.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(FirstPersonItemHolder), "LateUpdate")]
internal static class FirstPersonHolderLateUpdateHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(FirstPersonItemHolder __instance) => GameHooks.FirstPersonHolderLateUpdate.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(FirstPersonItemHolder __instance, bool __runOriginal) => GameHooks.FirstPersonHolderLateUpdate.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, FirstPersonItemHolder __instance) { if (__exception != null) GameHooks.FirstPersonHolderLateUpdate.RunFaulted(__instance, __exception); return __exception; }
}


[HarmonyPatch(typeof(SurvivalWave), nameof(SurvivalWave.StopEvent))]
internal static class WaveStoppedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(SurvivalWave __instance) => GameHooks.WaveStopped.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(SurvivalWave __instance, bool __runOriginal) => GameHooks.WaveStopped.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, SurvivalWave __instance) { if (__exception != null) GameHooks.WaveStopped.RunFaulted(__instance, __exception); return __exception; }
}

/// <summary>The six receive entries share one class: one bracket, six targets on the base class and the same six on the local
/// player's overrides (which never call the base bodies, so one hit runs the bracket once).</summary>
internal static class PlayerReceiveHooks
{
    /// <summary>Opens the call and answers whether the body runs: false when a <c>Skip</c> subscriber keeps it out.</summary>
    private static bool Open(PlayerReceiveKind kind, Dam_PlayerDamageBase receiver, UFloat16 damage, pAgent source, out PlayerReceive call)
    {
        var amount = 0f;
        try { amount = damage.Get(receiver.HealthMax); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-reading-damage-player-hit-failed", "Reading the damage of a player hit failed: " + error); }
        call = new PlayerReceive(kind, receiver, amount, source);
        var skip = GameHooks.PlayerReceived.Skipped(call);
        GameHooks.PlayerReceived.RunBefore(call);
        return !skip;
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveBulletDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool LocalBulletBefore(Dam_PlayerDamageLocal __instance, pBulletDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.Bullet, __instance, data.damage, data.source, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveBulletDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void LocalBulletAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveBulletDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? LocalBulletFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveMeleeDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool LocalMeleeBefore(Dam_PlayerDamageLocal __instance, pFullDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.Melee, __instance, data.damage, data.source, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveMeleeDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void LocalMeleeAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveMeleeDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? LocalMeleeFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveExplosionDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool LocalExplosionBefore(Dam_PlayerDamageLocal __instance, pExplosionDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.Explosion, __instance, data.damage, default, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveExplosionDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void LocalExplosionAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveExplosionDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? LocalExplosionFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveTentacleAttackDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool LocalTentacleBefore(Dam_PlayerDamageLocal __instance, pMediumDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.TentacleAttack, __instance, data.damage, data.source, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveTentacleAttackDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void LocalTentacleAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveTentacleAttackDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? LocalTentacleFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveShooterProjectileDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool LocalProjectileBefore(Dam_PlayerDamageLocal __instance, pMediumDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.ShooterProjectile, __instance, data.damage, data.source, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveShooterProjectileDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void LocalProjectileAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveShooterProjectileDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? LocalProjectileFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveFallDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool LocalFallBefore(Dam_PlayerDamageLocal __instance, pMiniDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.Fall, __instance, data.damage, default, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveFallDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void LocalFallAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageLocal), nameof(Dam_PlayerDamageLocal.ReceiveFallDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? LocalFallFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveBulletDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool BulletBefore(Dam_PlayerDamageBase __instance, pBulletDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.Bullet, __instance, data.damage, data.source, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveBulletDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void BulletAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveBulletDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? BulletFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveMeleeDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool MeleeBefore(Dam_PlayerDamageBase __instance, pFullDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.Melee, __instance, data.damage, data.source, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveMeleeDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void MeleeAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveMeleeDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? MeleeFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveExplosionDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool ExplosionBefore(Dam_PlayerDamageBase __instance, pExplosionDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.Explosion, __instance, data.damage, default, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveExplosionDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void ExplosionAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveExplosionDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? ExplosionFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveTentacleAttackDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool TentacleBefore(Dam_PlayerDamageBase __instance, pMediumDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.TentacleAttack, __instance, data.damage, data.source, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveTentacleAttackDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void TentacleAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveTentacleAttackDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? TentacleFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveShooterProjectileDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool ProjectileBefore(Dam_PlayerDamageBase __instance, pMediumDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.ShooterProjectile, __instance, data.damage, data.source, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveShooterProjectileDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void ProjectileAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveShooterProjectileDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? ProjectileFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveFallDamage))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool FallBefore(Dam_PlayerDamageBase __instance, pMiniDamageData data, out PlayerReceive __state) => Open(PlayerReceiveKind.Fall, __instance, data.damage, default, out __state);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveFallDamage))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void FallAfter(PlayerReceive __state, bool __runOriginal) => GameHooks.PlayerReceived.RunAfter(__state, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveFallDamage))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? FallFaulted(Exception? __exception, PlayerReceive __state) => Faulted(__exception, __state);

    private static Exception? Faulted(Exception? error, PlayerReceive call)
    {
        if (error != null) GameHooks.PlayerReceived.RunFaulted(call, error);
        return error;
    }
}

// Both damage bases override the receive (vtable slot 41), so the base method alone never runs for a live sync: the player
// and the enemy overrides are patched, and both run the one bracket.
internal static class HealthSetReceivedHook
{
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveSetHealth))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void PlayerBefore(Dam_PlayerDamageBase __instance) => GameHooks.HealthSetReceived.RunBefore(__instance);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveSetHealth))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void PlayerAfter(Dam_PlayerDamageBase __instance, bool __runOriginal) => GameHooks.HealthSetReceived.RunAfter(__instance, __runOriginal);
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveSetHealth))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? PlayerFaulted(Exception? __exception, Dam_PlayerDamageBase __instance) => Faulted(__exception, __instance);

    [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveSetHealth))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void EnemyBefore(Dam_EnemyDamageBase __instance) => GameHooks.HealthSetReceived.RunBefore(__instance);
    [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveSetHealth))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void EnemyAfter(Dam_EnemyDamageBase __instance, bool __runOriginal) => GameHooks.HealthSetReceived.RunAfter(__instance, __runOriginal);
    [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveSetHealth))]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? EnemyFaulted(Exception? __exception, Dam_EnemyDamageBase __instance) => Faulted(__exception, __instance);

    private static Exception? Faulted(Exception? error, Dam_SyncedDamageBase receiver)
    {
        if (error != null) GameHooks.HealthSetReceived.RunFaulted(receiver, error);
        return error;
    }
}
[HarmonyPatch(typeof(PLOC_Downed), nameof(PLOC_Downed.OnPlayerRevived))]
internal static class PlayerRevivedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(PLOC_Downed __instance) => GameHooks.PlayerRevived.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(PLOC_Downed __instance, bool __runOriginal) => GameHooks.PlayerRevived.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, PLOC_Downed __instance) { if (__exception != null) GameHooks.PlayerRevived.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.Fire))]
internal static class BulletWeaponFireHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(BulletWeapon __instance) => GameHooks.Fire.BulletWeapon.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BulletWeapon __instance, bool __runOriginal) => GameHooks.Fire.BulletWeapon.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BulletWeapon __instance) { if (__exception != null) GameHooks.Fire.BulletWeapon.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(Shotgun), nameof(Shotgun.Fire))]
internal static class ShotgunFireHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Shotgun __instance) => GameHooks.Fire.Shotgun.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(Shotgun __instance, bool __runOriginal) => GameHooks.Fire.Shotgun.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, Shotgun __instance) { if (__exception != null) GameHooks.Fire.Shotgun.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(BulletWeaponSynced), nameof(BulletWeaponSynced.Fire))]
internal static class BulletWeaponSyncedFireHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(BulletWeaponSynced __instance) => GameHooks.Fire.BulletWeaponSynced.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(BulletWeaponSynced __instance, bool __runOriginal) => GameHooks.Fire.BulletWeaponSynced.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, BulletWeaponSynced __instance) { if (__exception != null) GameHooks.Fire.BulletWeaponSynced.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(ShotgunSynced), nameof(ShotgunSynced.Fire))]
internal static class ShotgunSyncedFireHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(ShotgunSynced __instance) => GameHooks.Fire.ShotgunSynced.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ShotgunSynced __instance, bool __runOriginal) => GameHooks.Fire.ShotgunSynced.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ShotgunSynced __instance) { if (__exception != null) GameHooks.Fire.ShotgunSynced.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(Weapon), nameof(Weapon.CastWeaponRay), new[] { typeof(Transform), typeof(Weapon.WeaponHitData), typeof(int) },
    new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal })]
internal static class MuzzleRayHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Transform __0, ref Weapon.WeaponHitData __1) { if (GameHooks.MuzzleRay.Active) GameHooks.MuzzleRay.RunEnter(__0, ref __1, default); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(Transform __0, ref Weapon.WeaponHitData __1, ref bool __result, bool __runOriginal) { if (GameHooks.MuzzleRay.Active) GameHooks.MuzzleRay.RunExit(__0, ref __1, default, ref __result, __runOriginal); }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception) { if (__exception != null && GameHooks.MuzzleRay.Active) GameHooks.MuzzleRay.RunFaulted(); return __exception; }
}

[HarmonyPatch(typeof(Weapon), nameof(Weapon.CastWeaponRay), new[] { typeof(Transform), typeof(Weapon.WeaponHitData), typeof(Vector3), typeof(int) },
    new[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal })]
internal static class AlignedRayHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(Transform __0, ref Weapon.WeaponHitData __1, Vector3 __2) { if (GameHooks.AlignedRay.Active) GameHooks.AlignedRay.RunEnter(__0, ref __1, __2); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(Transform __0, ref Weapon.WeaponHitData __1, Vector3 __2, ref bool __result, bool __runOriginal) { if (GameHooks.AlignedRay.Active) GameHooks.AlignedRay.RunExit(__0, ref __1, __2, ref __result, __runOriginal); }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception) { if (__exception != null && GameHooks.AlignedRay.Active) GameHooks.AlignedRay.RunFaulted(); return __exception; }
}

[HarmonyPatch(typeof(MWS_Push), nameof(MWS_Push.Enter))]
internal static class PushEnteredHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(MWS_Push __instance) => GameHooks.PushEntered.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(MWS_Push __instance, bool __runOriginal) => GameHooks.PushEntered.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, MWS_Push __instance) { if (__exception != null) GameHooks.PushEntered.RunFaulted(__instance, __exception); return __exception; }
}


[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.FireBullet))]
internal static class SentryFiredHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(SentryGunInstance_Firing_Bullets __instance) => GameHooks.SentryFired.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(SentryGunInstance_Firing_Bullets __instance, bool __runOriginal) => GameHooks.SentryFired.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, SentryGunInstance_Firing_Bullets __instance) { if (__exception != null) GameHooks.SentryFired.RunFaulted(__instance, __exception); return __exception; }
}


/// <summary>The seven sentry lifecycle methods behind <see cref="GameHooks.SentryLifecycle"/>: one patch class over all of them, the step read
/// from the patched method and the acting player from the first argument of the two pick-up methods (the other five take none the bracket
/// reports).</summary>
[HarmonyPatch]
internal static class SentryLifecycleHook
{
    private static readonly string[] Methods =
    {
        nameof(SentryGunInstance.OnGearSpawnComplete), nameof(SentryGunInstance.PickUp), nameof(SentryGunInstance.SyncedPickup),
        nameof(SentryGunInstance.OnDespawn), nameof(SentryGunInstance.OnDestroy), nameof(SentryGunInstance.OnBackpackCleanupCallback),
        nameof(SentryGunInstance.CleanUp),
    };

    private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var name in Methods)
            yield return AccessTools.DeclaredMethod(typeof(SentryGunInstance), name) ?? throw new MissingMethodException(nameof(SentryGunInstance), name);
    }

    internal static SentryStep StepOf(string method) => method switch
    {
        nameof(SentryGunInstance.OnGearSpawnComplete) => SentryStep.GearSpawnComplete,
        nameof(SentryGunInstance.PickUp) => SentryStep.PickUp,
        nameof(SentryGunInstance.SyncedPickup) => SentryStep.SyncedPickup,
        nameof(SentryGunInstance.OnDespawn) => SentryStep.Despawn,
        nameof(SentryGunInstance.OnDestroy) => SentryStep.Destroy,
        nameof(SentryGunInstance.OnBackpackCleanupCallback) => SentryStep.BackpackCleanup,
        nameof(SentryGunInstance.CleanUp) => SentryStep.CleanUp,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Not a sentry lifecycle method."),
    };

    internal static SentryCall Call(SentryGunInstance sentry, System.Reflection.MethodBase method, object[] args)
    {
        var step = StepOf(method.Name);
        return new(sentry, step, step is SentryStep.PickUp or SentryStep.SyncedPickup && args.Length > 0 ? args[0] as PlayerAgent : null);
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(SentryGunInstance __instance, System.Reflection.MethodBase __originalMethod, object[] __args)
        => GameHooks.SentryLifecycle.RunBefore(Call(__instance, __originalMethod, __args));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(SentryGunInstance __instance, System.Reflection.MethodBase __originalMethod, object[] __args, bool __runOriginal)
        => GameHooks.SentryLifecycle.RunAfter(Call(__instance, __originalMethod, __args), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, SentryGunInstance __instance, System.Reflection.MethodBase __originalMethod, object[] __args)
    { if (__exception != null) GameHooks.SentryLifecycle.RunFaulted(Call(__instance, __originalMethod, __args), __exception); return __exception; }
}

[HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.OnSpawn))]
internal static class MineSpawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(MineDeployerInstance __instance) => GameHooks.MineSpawned.RunBefore(__instance);
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(MineDeployerInstance __instance, bool __runOriginal) => GameHooks.MineSpawned.RunAfter(__instance, __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, MineDeployerInstance __instance) { if (__exception != null) GameHooks.MineSpawned.RunFaulted(__instance, __exception); return __exception; }
}

[HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.SyncedPickup))]
internal static class MinePickedUpHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(MineDeployerInstance __instance, PlayerAgent __0) => GameHooks.MinePickedUp.RunBefore((__instance, __0));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(MineDeployerInstance __instance, PlayerAgent __0, bool __runOriginal) => GameHooks.MinePickedUp.RunAfter((__instance, __0), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, MineDeployerInstance __instance, PlayerAgent __0) { if (__exception != null) GameHooks.MinePickedUp.RunFaulted((__instance, __0), __exception); return __exception; }
}

// dump.cs:565768, RVA 0x12A2000. SpawnItem assembles the mode's prefabs and runs setup before returning; the After handlers read the
// returned Item (ItemSpawn.Item), which the postfix stores before they run.
[HarmonyPatch(typeof(ItemSpawnManager), nameof(ItemSpawnManager.SpawnItem))]
internal static class ItemSpawnedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(uint persistentID, ItemMode mode, out ItemSpawn __state) { __state = new ItemSpawn(persistentID, mode); GameHooks.ItemSpawned.RunBefore(__state); }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ItemSpawn __state, Item __result, bool __runOriginal) { __state.Item = __result; GameHooks.ItemSpawned.RunAfter(__state, __runOriginal); }
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ItemSpawn __state) { if (__exception != null) GameHooks.ItemSpawned.RunFaulted(__state, __exception); return __exception; }
}

[HarmonyPatch(typeof(PUI_GameEventLog), nameof(PUI_GameEventLog.AddLogItem))]
internal static class GameLogAddedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Before(string log, eGameEventChatLogType type) => GameHooks.GameLogAdded.RunBefore((log, type));
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(string log, eGameEventChatLogType type, bool __runOriginal) => GameHooks.GameLogAdded.RunAfter((log, type), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, string log, eGameEventChatLogType type) { if (__exception != null) GameHooks.GameLogAdded.RunFaulted((log, type), __exception); return __exception; }
}

[HarmonyPatch(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.DoAttackDamage))]
internal static class MeleeAttackDamageHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Before(MeleeWeaponFirstPerson __instance, MeleeWeaponDamageData data, bool isPush)
    {
        var call = (__instance, data, isPush);
        var skip = GameHooks.MeleeAttackDamage.Skipped(call);
        GameHooks.MeleeAttackDamage.RunBefore(call);
        return !skip;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(MeleeWeaponFirstPerson __instance, MeleeWeaponDamageData data, bool isPush, bool __runOriginal) => GameHooks.MeleeAttackDamage.RunAfter((__instance, data, isPush), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, MeleeWeaponFirstPerson __instance, MeleeWeaponDamageData data, bool isPush) { if (__exception != null) GameHooks.MeleeAttackDamage.RunFaulted((__instance, data, isPush), __exception); return __exception; }
}

[HarmonyPatch(typeof(Animator), nameof(Animator.CrossFadeInFixedTime), new[] { typeof(int), typeof(float) })]
internal static class AnimatorCrossFadedHook
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(Animator __instance, int stateHashName, bool __runOriginal) => GameHooks.AnimatorCrossFaded.RunAfter((__instance, stateHashName), __runOriginal);
}

[HarmonyPatch(typeof(ES_HibernateWakeUp), nameof(ES_HibernateWakeUp.ActivateState))]
internal static class HibernateWakeActivatedHook
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool Before(ES_HibernateWakeUp __instance, float distance, float delay, bool isPropagatedWakeup)
    {
        var call = (__instance, distance, delay, isPropagatedWakeup);
        var skip = GameHooks.HibernateWakeActivated.Skipped(call);
        GameHooks.HibernateWakeActivated.RunBefore(call);
        return !skip;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void After(ES_HibernateWakeUp __instance, float distance, float delay, bool isPropagatedWakeup, bool __runOriginal) => GameHooks.HibernateWakeActivated.RunAfter((__instance, distance, delay, isPropagatedWakeup), __runOriginal);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, ES_HibernateWakeUp __instance, float distance, float delay, bool isPropagatedWakeup) { if (__exception != null) GameHooks.HibernateWakeActivated.RunFaulted((__instance, distance, delay, isPropagatedWakeup), __exception); return __exception; }
}
