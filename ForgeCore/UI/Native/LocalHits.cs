using System;
using System.Collections.Generic;
using Agents;
using HarmonyLib;
using UnityEngine;

namespace ForgeUI;

/// <summary>
/// The local player's hits on enemies, handed to the enemy health bars (<see cref="EnemyHealthBars"/>) and the hit feedback
/// (<see cref="HitFeedbacks"/>), the same on host and client. Everything shows in the frame of the hit, from ForgeCore's prediction
/// of this machine's own hit request (<see cref="ForgeCore.Combat.OwnHitPredicted"/>: the marker, the damage number, the piece
/// the bar loses, a kill's mark) and the game's own weak-spot indicator (<c>Dam_EnemyDamageLimb.ShowHitIndicator</c>, which runs on
/// the shooter's machine). The host's settlement, about one round trip later on a client, only corrects it: the number changes by
/// the difference, a kill that was not predicted shows then, one that did not happen gives the enemy its bar back; a request the
/// host never settled is taken back after 3 s. Nothing is read while neither wants them.
/// </summary>
internal static class LocalHits
{
    // Where the local player last hit each enemy (the number and the kill mark show there), the enemy and the frame a weak spot
    // of it was last hit, by the enemy's key in ForgeCore's prediction book. An entry goes with the enemy's kill or the level's end.
    private struct Recent { public Vector3 At; public Dam_EnemyDamageBase? Enemy; public int CriticalFrame; }
    private static readonly Dictionary<long, Recent> Hits = new();

    // The local player's latest predicted hit, for the game's own hit indicator of the same call (IndicatorPatch).
    private static IntPtr _lastEnemy;
    private static int _lastFrame = -1;
    private static bool _lastDead;

    private static bool Wanted => EnemyHealthBars.WantsLocalHits || HitFeedbacks.WantsLocalHits;

    // The game's frame number, read in one place: a hit and its weak-spot indicator are of one frame.
    private static int CurrentFrame => Time.frameCount;

    private static void Predicted(ForgeCore.OwnPrediction hit)
    {
        var frame = CurrentFrame;
        _lastEnemy = hit.Enemy.Pointer;
        _lastFrame = frame;
        // A client's copy of the enemy never loses health, so the game's own indicator learns here that this hit takes the last.
        _lastDead = hit.Confirmable && !ForgeCore.Session.IsHost && hit.Before - hit.Damage <= 0f;
        if (!Wanted) return;
        Hits.TryGetValue(hit.Key, out var recent);
        recent.At = hit.Position;
        recent.Enemy = hit.Enemy;
        Hits[hit.Key] = recent;
        // Without a host that settles hits nobody knows what the enemy has left: no bar.
        if (hit.Confirmable) EnemyHealthBars.LocalHit(hit.Enemy, hit.Before, hit.Before - hit.Damage, hit.Enemy.HealthMax);
        HitFeedbacks.LocalHit(hit.Key, hit.Damage, hit.Position, recent.CriticalFrame == frame);
        if (hit.Kill) HitFeedbacks.LocalKill(true, hit.Position);
    }

    private static void Settled(ForgeCore.OwnHit hit)
    {
        if (!Wanted) return;
        var known = Hits.TryGetValue(hit.Key, out var recent);
        if (hit.Killed) Hits.Remove(hit.Key);
        var target = hit.Enemy;
        // A hit that was not predicted (an explosion, fire, one past the book) shows now, whole. A client hears it a round trip
        // late: an enemy another player killed meanwhile gets no bar again.
        if (hit.Unmatched > 0 && target != null && (hit.Killed || target.Owner is { Alive: true }))
            EnemyHealthBars.LocalHit(target, hit.Health + hit.Damage, hit.Health, hit.HealthMax);
        if (!HitFeedbacks.Adjust(hit.Key, hit.DamageDelta) && hit.Unmatched > 0 && known) HitFeedbacks.LocalHit(hit.Key, hit.DamageDelta, recent.At, false);
        if (hit.ShowKill) HitFeedbacks.LocalKill(known, recent.At);
        if (hit.KillDelta < 0 && known) Lives(hit.Key, recent.Enemy);
    }

    // A request the host never settled, taken back; key 0 is the world's end, which clears everything by itself.
    private static void TakenBack(ForgeCore.OwnCorrection change)
    {
        if (change.Key == 0 || !Wanted) return;
        HitFeedbacks.Adjust(change.Key, change.DamageDelta);
        if (change.KillDelta < 0 && Hits.TryGetValue(change.Key, out var recent)) Lives(change.Key, recent.Enemy);
    }

    // A predicted kill did not happen: the enemy gets its bar back, and the game's death indicator may show again when it does die.
    private static void Lives(long key, Dam_EnemyDamageBase? enemy)
    {
        if (ForgeCore.NativeObjects.Gone(enemy) || enemy.Owner is not { Alive: true }) return;
        enemy.DeathIndicatorShown = false;
        var health = ForgeCore.Combat.EnemyHealth(key, enemy);
        if (health > 0f) EnemyHealthBars.LocalHit(enemy, health, health, enemy.HealthMax);
    }

    private static bool _watching;

    /// <summary>Puts the hit reads on: the predictions of this machine's hit requests, the settlements and the indicator patch.</summary>
    internal static void Ensure()
    {
        PatchGroups.Ensure(PatchGroup.LocalHits);
        if (_watching) return;
        _watching = true;
        ForgeCore.Combat.HearOwnPredictions(static hit =>
        {
            try { Predicted(hit); }
            catch (Exception error) { Plugin.Error("ui-reading-local-players-hit-failed", "Reading the local player's hit failed: " + error); }
        });
        ForgeCore.Combat.HearOwnHits(static hit =>
        {
            try { Settled(hit); }
            catch (Exception error) { Plugin.Error("ui-reading-local-players-hit-failed", "Reading the local player's hit failed: " + error); }
        });
        ForgeCore.Combat.HearOwnCorrections(static change =>
        {
            try { TakenBack(change); }
            catch (Exception error) { Plugin.Error("ui-reading-local-players-hit-failed", "Reading the local player's hit failed: " + error); }
        });
    }

    [PatchGroup(PatchGroup.LocalHits)]
    [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.ShowHitIndicator))]
    private static class IndicatorPatch
    {
        // The limb asks its indicator with "will die" worked out from this machine's copy of the enemy's health, which never drops
        // on a client: the prediction of the same call (the limb sends the request first) says it instead, so the game's own death
        // indicator shows with the hit that is predicted to kill and, as in the game, nothing shows for a hit on the dead.
        private static void Prefix(Dam_EnemyDamageLimb __instance, ref bool __1)
        {
            if (!_lastDead || _lastFrame != CurrentFrame) return;
            try
            {
                var target = __instance.m_base;
                if (target != null && target.Pointer == _lastEnemy) __1 = true;
            }
            catch (Exception error) { Plugin.Error("ui-reading-local-players-weak-spot-hit", "Predicting the game's death indicator failed: " + error); }
        }

        private static void Postfix(Dam_EnemyDamageLimb __instance, bool __0)
        {
            if (!__0 || !HitFeedbacks.WantsLocalHits) return;
            try
            {
                var target = __instance.m_base;
                if (target == null) return;
                var key = ForgeCore.Combat.EnemyKey(target);
                Hits.TryGetValue(key, out var recent);
                Hits[key] = recent with { CriticalFrame = CurrentFrame };
                HitFeedbacks.Critical(key);
            }
            catch (Exception error) { Plugin.Error("ui-reading-local-players-weak-spot-hit", "Reading the local player's weak-spot hit failed: " + error); }
        }
    }

    /// <summary>The level ended (<see cref="ForgeCore.GameEvents.LevelEnded"/>).</summary>
    internal static void LevelEnded()
    {
        Hits.Clear();
        _lastFrame = -1;
        if (EnemyHealthBars.Used) EnemyHealthBars.Clear();
        HitFeedbacks.Clear();
    }
}
