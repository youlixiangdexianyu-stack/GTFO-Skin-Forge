using System;
using Agents;
using HarmonyLib;

namespace ForgeCore;

/// <summary>What the damage observer's <see cref="PlayerHits.HitBefore"/> opened, handed back to its <c>after</c> or <c>faulted</c>
/// for the same call (a value, nothing allocated).</summary>
public readonly record struct HitState(bool Open, float Value);

/// <summary>The one place that patches the game's player hit path: <c>Dam_PlayerDamageBase.OnIncomingDamage</c> (every damage entry
/// funnels through it, on whichever peer receives the packet) and <c>Dam_PlayerDamageBase.ModifyInfection</c>. A mod changes or reads a hit
/// through the registrations here instead of patching these methods itself. Main thread only; one registration per owner (null removes
/// it). Each kind of registration puts on only its own patches, the first time it is made: a redirect its prefix, the observer or a
/// watcher the reading prefix, postfix and finalizer, an infection gate or observer the infection prefix and postfix.</summary>
public static class PlayerHits
{
    /// <summary>Before the game's body, after every <see cref="Redirect"/>; the damage may still be changed. The answer comes back to the
    /// observer's <c>after</c> (the body returned) or <c>faulted</c> (the body threw).</summary>
    public delegate HitState HitBefore(Dam_PlayerDamageBase receiver, ref float damage, Agent? source);

    private static (string Owner, Func<Dam_PlayerDamageBase, float, Agent?, float> Redirect)[] _redirects = [];
    private static (string Owner, Func<Dam_PlayerDamageBase, pInfection, bool> Allow)[] _gates = [];
    private static (string Owner, Action<Dam_PlayerDamageBase, float, Agent?> Watcher)[] _watchers = [];
    private static string? _hitOwner, _infectionOwner;
    internal static HitBefore? HitBeforeCall;
    internal static Action<Dam_PlayerDamageBase, Agent?, HitState>? HitAfter;
    internal static Action<HitState>? HitFaulted;
    internal static Func<Dam_PlayerDamageBase, float>? InfectionBefore;
    internal static Action<Dam_PlayerDamageBase, pInfection, float>? InfectionAfter;

    /// <summary>Changes the damage of a hit before anything else reads it: <paramref name="redirect"/> gets the receiver, the damage and
    /// the attacker and answers the damage to apply (0 = none). Redirects run in registration order, before the observer.</summary>
    public static void Redirect(string owner, Func<Dam_PlayerDamageBase, float, Agent?, float>? redirect)
    {
        if (redirect != null) Ensure(typeof(PlayerHitRedirectHook));
        _redirects = Replace(_redirects, owner, redirect);
    }

    /// <summary>Refuses an infection write: <paramref name="allow"/> answers false to skip the game's write. A write any gate refuses is
    /// skipped; the infection observer still sees it.</summary>
    public static void GateInfection(string owner, Func<Dam_PlayerDamageBase, pInfection, bool>? allow)
    {
        if (allow != null) Ensure(typeof(PlayerInfectionHooks));
        _gates = Replace(_gates, owner, allow);
    }

    [ThreadStatic] private static int _authored;

    /// <summary>True while an infection write a mod's own content asked for (<see cref="WriteAuthoredInfection"/>) is running, so a gate
    /// can tell it from the game's own infection sources (an enemy, fog).</summary>
    public static bool AuthoredInfection => _authored > 0;

    /// <summary>Writes infection through the game's own entry on behalf of authored content: gates see
    /// <see cref="AuthoredInfection"/> for exactly this call.</summary>
    public static void WriteAuthoredInfection(Dam_PlayerDamageBase damage, pInfection data, bool sync, bool updatePageMap)
    {
        _authored++;
        try { damage.ModifyInfection(data, sync, updatePageMap); }
        finally { _authored--; }
    }

    /// <summary>The one reader of hits (runs last, after every redirect, so it sees the damage the game will apply): <paramref name="before"/>
    /// before the game's body, then <paramref name="after"/> when the body returned or <paramref name="faulted"/> when it threw, each with
    /// what <paramref name="before"/> answered. A null <paramref name="before"/> removes the owner's registration; otherwise all three are
    /// required. A second owner is refused.</summary>
    public static void Observe(string owner, HitBefore? before, Action<Dam_PlayerDamageBase, Agent?, HitState>? after, Action<HitState>? faulted)
    {
        if (before != null)
        {
            if (after == null) throw new ArgumentNullException(nameof(after));
            if (faulted == null) throw new ArgumentNullException(nameof(faulted));
            Ensure(typeof(PlayerHitObserveHooks));
        }
        if (!Claim(ref _hitOwner, owner, before != null)) return;
        HitBeforeCall = before;
        HitAfter = before == null ? null : after;
        HitFaulted = before == null ? null : faulted;
    }

    /// <summary>Reads a hit without changing it: <paramref name="watcher"/> gets the receiver, the damage the game will apply and the attacker
    /// (after every redirect and the observer). Any number of owners; it cannot change the hit, so they cannot conflict.</summary>
    public static void Watch(string owner, Action<Dam_PlayerDamageBase, float, Agent?>? watcher)
    {
        if (watcher != null) Ensure(typeof(PlayerHitObserveHooks));
        _watchers = Replace(_watchers, owner, watcher);
    }

    internal static void RunWatchers(Dam_PlayerDamageBase receiver, float damage, Agent? source)
    {
        foreach (var (_, watcher) in _watchers) watcher(receiver, damage, source);
    }

    /// <summary>The one reader of infection writes: <paramref name="before"/> reads the player before the write and its answer comes back
    /// to <paramref name="after"/> once the write ran (NaN = nothing read). A null <paramref name="before"/> removes the owner's registration;
    /// otherwise both are required. A second owner is refused.</summary>
    public static void ObserveInfection(string owner, Func<Dam_PlayerDamageBase, float>? before, Action<Dam_PlayerDamageBase, pInfection, float>? after)
    {
        if (before != null)
        {
            if (after == null) throw new ArgumentNullException(nameof(after));
            Ensure(typeof(PlayerInfectionHooks));
        }
        if (!Claim(ref _infectionOwner, owner, before != null)) return;
        InfectionBefore = before;
        InfectionAfter = before == null ? null : after;
    }

    private static void Ensure(Type hooks) => Patches.Ensure(Modules.PlayerHits, hooks);

    private static (string, T)[] Replace<T>((string Owner, T Handler)[] list, string owner, T? handler) where T : class
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        var rest = Array.FindAll(list, entry => entry.Owner != owner);
        return handler == null ? rest : [.. rest, (owner, handler)];
    }

    /// <summary>True when <paramref name="owner"/> may write the slot <paramref name="holder"/> guards: a registration by another owner is
    /// refused, a removal by another owner leaves the slot alone.</summary>
    private static bool Claim(ref string? holder, string owner, bool registering)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (holder != null && holder != owner)
        {
            if (registering) throw new InvalidOperationException("PlayerHits already has an observer from " + holder + ".");
            return false;
        }
        holder = registering ? owner : null;
        return true;
    }

    internal static float RedirectDamage(Dam_PlayerDamageBase receiver, float damage, Agent? source)
    {
        foreach (var (_, redirect) in _redirects) damage = redirect(receiver, damage, source);
        return damage;
    }

    internal static bool AllowInfection(Dam_PlayerDamageBase receiver, pInfection data)
    {
        foreach (var (_, allow) in _gates)
            if (!allow(receiver, data)) return false;
        return true;
    }
}

/// <summary>Put on by the first <see cref="PlayerHits.Redirect"/>. Redirects run first (<see cref="Priority.First"/>).</summary>
internal static class PlayerHitRedirectHook
{
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Redirect(Dam_PlayerDamageBase __instance, ref float damage, Agent sourceAgent)
    {
        try { damage = PlayerHits.RedirectDamage(__instance, damage, sourceAgent); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-player-hit-redirect-failed", "A player hit redirect failed: " + error); }
    }
}

/// <summary>Put on by the first <see cref="PlayerHits.Observe"/> or <see cref="PlayerHits.Watch"/>. The observer and the watchers bracket
/// the game's body last (<see cref="Priority.Last"/>), so they read what the earlier patches left.</summary>
internal static class PlayerHitObserveHooks
{
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Observe(Dam_PlayerDamageBase __instance, ref float damage, Agent sourceAgent, out HitState __state)
    {
        __state = default;
        var before = PlayerHits.HitBeforeCall;
        if (before != null)
        {
            try { __state = before(__instance, ref damage, sourceAgent); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("core-player-hit-observer-failed", "The player hit observer failed: " + error); }
        }
        try { PlayerHits.RunWatchers(__instance, damage, sourceAgent); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-player-hit-watcher-failed", "A player hit watcher failed: " + error); }
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Observed(Dam_PlayerDamageBase __instance, Agent sourceAgent, HitState __state)
    {
        var after = PlayerHits.HitAfter;
        if (after == null) return;
        try { after(__instance, sourceAgent, __state); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-player-hit-observer-failed", "The player hit observer failed: " + error); }
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), "OnIncomingDamage")]
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Faulted(Exception? __exception, HitState __state)
    {
        if (__exception == null) return null;
        try { PlayerHits.HitFaulted?.Invoke(__state); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-player-hit-observer-failed", "The player hit observer failed: " + error); }
        return __exception;
    }
}

/// <summary>Put on by the first <see cref="PlayerHits.GateInfection"/> or <see cref="PlayerHits.ObserveInfection"/>: the gates and the
/// observer's read run first (<see cref="Priority.First"/>), the observer's answer last (<see cref="Priority.Last"/>).</summary>
internal static class PlayerInfectionHooks
{
    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ModifyInfection))]
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool InfectionBefore(Dam_PlayerDamageBase __instance, pInfection data, out float __state)
    {
        __state = float.NaN;
        try
        {
            var before = PlayerHits.InfectionBefore;
            if (before != null) __state = before(__instance);
            return PlayerHits.AllowInfection(__instance, data);
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("core-player-infection-hook-failed", "A player infection hook failed: " + error);
            return true;
        }
    }

    [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ModifyInfection))]
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void InfectionAfter(Dam_PlayerDamageBase __instance, pInfection data, float __state)
    {
        try { PlayerHits.InfectionAfter?.Invoke(__instance, data, __state); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("core-player-infection-observer-failed", "The player infection observer failed: " + error); }
    }
}
