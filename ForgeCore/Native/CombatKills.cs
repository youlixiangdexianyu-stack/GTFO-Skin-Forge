using System;
using Agents;
using Enemies;

namespace ForgeCore;

public static partial class Combat
{
    private static Action<EnemyAgent, Agent?>? _killed;
    private static bool _watching;

    /// <summary>Raised on the host after a bullet hit (<c>Dam_EnemyDamageBase.ReceiveBulletDamage</c>, the game's own receiver for every
    /// stock and Forge bullet, read through <see cref="GameHooks.EnemyBulletReceived"/>) took a living enemy to dead: the enemy and the
    /// packet's source agent (null when the packet names none or the game no longer resolves it). It fires on the same call stack as the hit,
    /// after the game has run it, so the enemy already reads dead. Only bullet hits are observed; melee, explosion and fire kills are other
    /// receivers and are not reported. A handler that throws is one <c>ErrorOnce</c> line and the others still run. Main thread only.</summary>
    public static event Action<EnemyAgent, Agent?>? Killed
    {
        add
        {
            if (!_watching)
            {
                GameHooks.EnemyBulletReceived.Around(call => Session.IsHost && call.Receiver.Owner != null && call.Receiver.Owner.Alive, BulletSettled);
                _watching = true;
            }
            _killed += value;
        }
        remove => _killed -= value;
    }

    private static void BulletSettled((Dam_EnemyDamageBase Receiver, pBulletDamageData Data) call, bool wasAlive, bool ran)
    {
        var enemy = call.Receiver.Owner;
        if (!wasAlive || enemy == null || enemy.Alive) return;
        Agent? source = null;
        if (call.Data.source.TryGet(out var found)) source = found;
        Plugin.Logger.RaiseEach(_killed, enemy, source, "core-combat-killed-handler-failed", "Combat.Killed");
    }
}
