using Agents;
using UnityEngine;

namespace ForgeCore;

/// <summary>The damage requests that name a limb, carry the multipliers the game applies on top, or go to a player. Each is the
/// receiver's own request, so the game settles it (armour, weak spots, replication, hit reaction) exactly as for any other hit.</summary>
public static partial class Combat
{
    /// <summary>Bullet damage to an enemy through its own request, naming the limb it hits (<paramref name="limbIndex"/> is the position in the
    /// receiver's limb array, not the limb id; a negative index would arrive as byte 255 and overrun the array, so callers pass 0 for
    /// "no limb"), the directional bonus and the two multipliers. Gear category 0.</summary>
    public static DamageSettlement BulletDamage(Dam_EnemyDamageBase receiver, float damage, Agent? source, Vector3 position, Vector3 direction,
        Vector3 normal, bool directionalBonus, int limbIndex, float staggerMultiplier, float precisionMultiplier)
    {
        if (receiver == null) throw new System.ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.BulletDamage(damage, source, position, direction, normal, directionalBonus, limbIndex, staggerMultiplier, precisionMultiplier, 0u);
        return new(before, receiver.Health);
    }

    /// <summary>Bullet damage to one limb through the limb's own request (the limb forwards to its enemy). Nothing is read back here: the
    /// limb's health and the enemy's are the caller's to read.</summary>
    public static void BulletDamage(Dam_EnemyDamageLimb limb, float damage, Agent? source, Vector3 position, Vector3 direction,
        Vector3 normal, bool directionalBonus, float staggerMultiplier, float precisionMultiplier)
    {
        if (limb == null) throw new System.ArgumentNullException(nameof(limb));
        limb.BulletDamage(damage, source, position, direction, normal, directionalBonus, staggerMultiplier, precisionMultiplier, 0u);
    }

    /// <summary>A melee hit on an enemy naming the limb (position in the limb array) and both multipliers, through
    /// <c>Dam_EnemyDamageBase.MeleeDamage</c>'s short form.</summary>
    public static DamageSettlement Melee(Dam_EnemyDamageBase receiver, float damage, Agent? source, Vector3 position, Vector3 direction,
        int limbIndex, float staggerMultiplier, float precisionMultiplier)
    {
        if (receiver == null) throw new System.ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.MeleeDamage(damage, source, position, direction, limbIndex, staggerMultiplier, precisionMultiplier);
        return new(before, receiver.Health);
    }

    /// <summary>Ends an enemy's life whatever its health, resistance or immortal flag. <c>Dam_EnemyDamageBase.InstantDead</c> cannot:
    /// it is a 1000-point melee hit, and the melee receiver drops it for an immortal enemy, caps it at <c>DamageMax</c> and scales it
    /// by the melee resistance modifier before it reaches <c>ProcessReceivedDamage</c>. That funnel, which every received hit on an
    /// enemy ends in, checks none of them: it takes the health, raises the death hit reaction itself when the health reaches zero
    /// (which clears <c>Alive</c> before this returns), credits the source and runs the death the game runs for any kill (death
    /// state, drops and <c>EnemyAgent.OnDead</c> on a later frame). On the host this calls it directly with the hit a lethal melee
    /// hit hands it (a heavy, forced reaction), carrying <c>DamageMax</c> (twice <c>HealthMax</c>): the host's own melee request sends
    /// no packet either (<c>SendPacket</c> answers false on the master), and the enemy's death replicates as for any kill. Every
    /// observer of received damage sees one change from the health the enemy had to zero. The settlement is marked
    /// <see cref="EnemyDamageCall.Unruled"/>, so no damage rule rewrites its amount. Elsewhere it asks the host through the enemy's own
    /// melee request instead, which the host settles with its resistance and rules. Nothing is read back here: whether the life ended
    /// is the caller's to read from <c>Alive</c>.</summary>
    public static void Kill(Dam_EnemyDamageBase receiver)
    {
        if (receiver == null) throw new System.ArgumentNullException(nameof(receiver));
        // The funnel, unlike the receivers in front of it, does not ask whether the enemy still lives.
        if (receiver.Owner == null || !receiver.Owner.Alive) return;
        var position = receiver.transform.position;
        if (!Session.IsHost) { Melee(receiver, receiver.DamageMax, null, position, Vector3.up, 0, 1f, 1f); return; }
        _killing = receiver.Pointer;
        try { receiver.ProcessReceivedDamage(receiver.DamageMax, null, position, Vector3.up, ES_HitreactType.Heavy, true); }
        finally { _killing = System.IntPtr.Zero; }
    }

    /// <summary>The receiver <see cref="Kill"/> is settling right now, zero otherwise.</summary>
    private static System.IntPtr _killing;

    /// <summary>Whether this entry into the funnel is the kill's own: true once, for the first entry on the receiver being killed, so a
    /// hit the death itself sets off (on this or another enemy) is ruled as usual.</summary>
    internal static bool TakeKill(Dam_EnemyDamageBase receiver)
    {
        if (_killing == System.IntPtr.Zero || receiver.Pointer != _killing) return false;
        _killing = System.IntPtr.Zero;
        return true;
    }

    /// <summary>Explosion damage to an enemy naming the limb (position in the limb array).</summary>
    public static DamageSettlement ExplosionDamage(Dam_EnemyDamageBase receiver, float damage, Vector3 position, Vector3 force, int limbIndex)
    {
        if (receiver == null) throw new System.ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.ExplosionDamage(damage, position, force, limbIndex);
        return new(before, receiver.Health);
    }

    /// <summary>A melee hit on a player through its own request.</summary>
    public static DamageSettlement Melee(Dam_PlayerDamageBase receiver, float damage, Agent? source, Vector3 position, Vector3 direction)
    {
        if (receiver == null) throw new System.ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.MeleeDamage(damage, source, position, direction);
        return new(before, receiver.Health);
    }

    /// <summary>A tentacle attack on a player through its own request.</summary>
    public static DamageSettlement Tentacle(Dam_PlayerDamageBase receiver, float damage, Agent? source, Vector3 position)
    {
        if (receiver == null) throw new System.ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.TentacleAttackDamage(damage, source, position);
        return new(before, receiver.Health);
    }

    /// <summary>A shooter projectile hit on a player through its own request.</summary>
    public static DamageSettlement Projectile(Dam_PlayerDamageBase receiver, float damage, Vector3 position)
    {
        if (receiver == null) throw new System.ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.ShooterProjectileDamage(damage, position);
        return new(before, receiver.Health);
    }

    /// <summary>Fall damage to a player through its own request.</summary>
    public static DamageSettlement Fall(Dam_PlayerDamageBase receiver, float damage)
    {
        if (receiver == null) throw new System.ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.FallDamage(damage);
        return new(before, receiver.Health);
    }
}
