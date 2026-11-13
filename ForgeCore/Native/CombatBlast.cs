using System;
using AIGraph;
using Enemies;
using UnityEngine;

namespace ForgeCore;

/// <summary>Where the line from a blast's centre to an enemy starts when <see cref="Combat.AreaDamage"/> asks whether the level is in the way.</summary>
public enum BlastLine
{
    /// <summary>The centre is a point on the floor: the line starts above it (<see cref="Level.HasSight"/>), under the ceiling when that is lower.</summary>
    FromFloor,
    /// <summary>The centre is where the blast burst, in the air or on a surface: the line starts at it.</summary>
    FromBurst,
}

public static partial class Combat
{
    /// <summary>True damage in a radius, on this peer, for the host: every living enemy inside the sphere whose aim point the centre can see
    /// (<paramref name="line"/> says how the line is drawn; only the level blocks it) takes <see cref="ExplosionDamage(Dam_SyncedDamageBase, float, Vector3, Vector3)"/>
    /// at its base receiver, the body's own damage request, never a limb's, so the limb steps that read armour and weak spots do not run and no
    /// enemy field is written. The damage falls off in a straight line from <paramref name="damage"/> at the centre to <paramref name="edgeShare"/>
    /// (0 to 1) of it at <paramref name="radius"/>; <paramref name="force"/> is the death-push magnitude along the line from the centre. Players
    /// are not hurt. With <paramref name="room"/> given, only enemies whose course node is that room are hurt. <paramref name="struck"/>, the enemy
    /// a projectile hit, takes the full damage whatever the line to it says. A living enemy that takes no health is logged once.</summary>
    public static void AreaDamage(Vector3 centre, float radius, float damage, float edgeShare, float force, BlastLine line,
        AIG_CourseNode? room = null, EnemyAgent? struck = null)
    {
        if (radius <= 0f || damage <= 0f || edgeShare < 0f || edgeShare > 1f) throw new ArgumentOutOfRangeException(nameof(radius));
        if (struck != null && struck.Alive) HurtByBlast(struck, damage, centre, force);
        foreach (var enemy in Enemies.Near(centre, radius))
        {
            if (enemy == null || !enemy.Alive || enemy == struck || (room != null && enemy.CourseNode != room)) continue;
            var point = Enemies.AimPoint(enemy);
            if (line == BlastLine.FromFloor ? !Level.HasSight(centre, point)
                : Physics.Linecast(centre, point, LayerManager.MASK_WORLD, QueryTriggerInteraction.Ignore)) continue;
            HurtByBlast(enemy, damage * (1f + (edgeShare - 1f) * Mathf.Clamp01((point - centre).magnitude / radius)), centre, force);
        }
    }

    private static void HurtByBlast(EnemyAgent enemy, float damage, Vector3 centre, float force)
    {
        var offset = Enemies.AimPoint(enemy) - centre;
        var distance = offset.magnitude;
        var health = enemy.Damage.Health;
        ExplosionDamage(enemy.Damage, damage, centre, (distance > 0.01f ? offset / distance : Vec.Up) * force);
        if (enemy.Alive && enemy.Damage.Health == health)
            Plugin.Logger.ErrorOnce("core-area-damage-no-damage", "a " + damage + " area blast left a " + enemy.EnemyData?.name + " at " + health + " health.");
    }
}
