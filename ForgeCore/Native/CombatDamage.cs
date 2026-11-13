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
