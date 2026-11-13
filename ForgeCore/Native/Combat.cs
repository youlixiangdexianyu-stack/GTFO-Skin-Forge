using System;
using AIGraph;
using Agents;
using Gear;
using UnityEngine;

namespace ForgeCore;

/// <summary>The game's own damage and explosion requests, so replication, health authority, armour, weak spots and hit reactions
/// stay the game's, plus which receiver a collider belongs to. No rules here: who is hit and how hard is the caller's.</summary>
public static partial class Combat
{
    /// <summary>The game's explosion: damage falls off to the edge and is blocked by level geometry; enemies, players (friendly fire
    /// as with any explosive) and bodies. <paramref name="force"/> 0 applies no push.</summary>
    public static void Explode(Vector3 position, float radius, float damage, float force)
    {
        if (damage <= 0f) return;
        DamageUtil.DoExplosionDamage(position, radius, damage, LayerManager.MASK_EXPLOSION_TARGETS, LayerManager.MASK_EXPLOSION_BLOCKERS, force > 0f, force);
    }

    /// <summary>The game's explosion limited to the receivers in <paramref name="targetMask"/>, with the damage type and the
    /// source ID (for example a gear category) the game records with it.</summary>
    public static void Explode(Vector3 position, float radius, float damage, float force, int targetMask, bool applyForce,
        DamageUtil.ExplosionDamageType type, uint sourceId)
    {
        DamageUtil.DoExplosionDamage_Capsule(position, Vec.Zero, radius, 0f, radius, 0f, damage, targetMask,
            LayerManager.MASK_EXPLOSION_BLOCKERS, applyForce, force, null, type, sourceId);
    }

    /// <summary>The game's world noise as its own loud events make it (a mine's explosion, a sentry's shot): no noise maker, from 0 to
    /// <paramref name="radius"/>, y scale 1, <c>InstaDetect</c>, spreading into neighbouring areas, no first-node raycast. Sleeping
    /// enemies within the radius wake as they do for those events. Call it where the event is authoritative (the host). Returns false
    /// (and makes nothing) for a radius of 0 or less or without a course node.</summary>
    public static bool Noise(Vector3 position, float radius, AIG_CourseNode? node)
        => Noise(position, 0f, radius, 1f, node, NM_NoiseType.InstaDetect, true, false);

    /// <summary><see cref="Noise(Vector3, float, AIG_CourseNode?)"/> at <paramref name="position"/> with the area that holds it found here
    /// (<see cref="Level.CourseNodeAt"/>, one metre search, <paramref name="preferredDimension"/> tried first; -1: none preferred). Returns false
    /// (and makes nothing) outside every area, where there is no enemy to alert, as well as for a radius of 0 or less.</summary>
    public static bool Noise(Vector3 position, float radius, int preferredDimension = -1)
        => radius > 0f && Level.CourseNodeAt(position, 1f, out var node, out _, preferredDimension) && Noise(position, radius, node);

    /// <summary>The game's one noise entry (<c>NoiseManager.MakeNoise</c>) with every field of the request given: no noise maker, a
    /// loudness band from <paramref name="radiusMin"/> to <paramref name="radiusMax"/>, how much the vertical distance counts
    /// (<paramref name="verticalScale"/>), the noise <paramref name="type"/>, whether it spreads into neighbouring areas and whether
    /// the first node is raycast. Returns false (and makes nothing) for a radius of 0 or less or without a course node. A request
    /// the game rejects because the node or maker was collected in between throws the game's own exception.</summary>
    public static bool Noise(Vector3 position, float radiusMin, float radiusMax, float verticalScale, AIG_CourseNode? node,
        NM_NoiseType type, bool includeNeighbours, bool raycastFirstNode)
    {
        if (radiusMax <= 0f || node == null || node.WasCollected) return false;
        // The parameterless constructor boxes the native value correctly (the generated nine-argument one reads this.Pointer
        // before boxing), so every field is set here.
        NoiseManager.MakeNoise(new NM_NoiseData
        {
            noiseMaker = null,
            node = node,
            position = position,
            radiusMin = radiusMin,
            radiusMax = radiusMax,
            yScale = verticalScale,
            type = type,
            includeToNeightbourAreas = includeNeighbours,
            raycastFirstNode = raycastFirstNode,
        });
        return true;
    }

    /// <summary>Fire (area) damage through the receiver's own request: enemies settle it on the host, players on their own peer.</summary>
    public static DamageSettlement Fire(Dam_SyncedDamageBase receiver, float damage, Agent? source)
    {
        if (receiver == null) throw new ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.FireDamage(damage, source);
        return new(before, receiver.Health);
    }

    /// <summary>The game's shove (<c>Dam_SyncedDamageBase.PushDamage</c>) of <paramref name="receiver"/> along <paramref name="direction"/>
    /// from <paramref name="position"/>. The enemy receiver takes no health for it (it registers the source, tells the enemy's abilities
    /// it was hit and starts a hitreact on its locomotion: Heavy when <paramref name="strength"/> is above 0, Micro when the enemy is glued
    /// past its tolerance) and does nothing for an immortal enemy or one that cannot be pushed, so the settlement's <see cref="DamageSettlement.Dealt"/>
    /// is expected to stay 0 and says only that no health was lost; whether the hitreact moved the enemy is not readable here.</summary>
    public static DamageSettlement Push(Dam_SyncedDamageBase receiver, float strength, Agent? source, Vector3 position, Vector3 direction)
    {
        if (receiver == null) throw new ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.PushDamage(strength, source, position, direction);
        return new(before, receiver.Health);
    }

    /// <summary>A melee hit through the enemy receiver's own request (<c>Dam_EnemyDamageBase.MeleeDamage</c>): <paramref name="damage"/>, the
    /// stagger multiplier the game applies on top of the hit, and no other multiplier (the sleeper, precision and the other two are 1), no
    /// directional bonus, normal noise, gear category 0. The game applies it on this peer, on a client too (an enemy receiver's gates answer true on every machine), and
    /// sends its own packet, so the settlement read here is this peer's; host-only submission is the caller's.</summary>
    public static DamageSettlement Melee(Dam_EnemyDamageBase receiver, float damage, Agent? source, Vector3 position, Vector3 direction, float staggerMultiplier)
    {
        if (receiver == null) throw new ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.MeleeDamage(damage, source, position, direction, staggerMultiplier, 1f, 1f, 1f, 1f, false, DamageNoiseLevel.Normal, 0u);
        return new(before, receiver.Health);
    }

    /// <summary>Explosion damage to one receiver through its own request (<c>Dam_SyncedDamageBase.ExplosionDamage(damage, position, force)</c>),
    /// not the radius query of <see cref="Explode(Vector3, float, float, float)"/>: no falloff, no blockers, no other target, and
    /// <paramref name="force"/> is the vector the game hands the receiver for its death push.</summary>
    public static DamageSettlement ExplosionDamage(Dam_SyncedDamageBase receiver, float damage, Vector3 position, Vector3 force)
    {
        if (receiver == null) throw new ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.ExplosionDamage(damage, position, force);
        return new(before, receiver.Health);
    }

    /// <summary>Bullet damage to one receiver through its own request (<c>Dam_SyncedDamageBase.BulletDamage(damage, source, position,
    /// direction, normal)</c>): no limb, weak spot, falloff or hit marker (those belong to <see cref="Bullet"/>, which resolves a whole
    /// hit), so the receiver's base multipliers apply.</summary>
    public static DamageSettlement BulletDamage(Dam_SyncedDamageBase receiver, float damage, Agent? source, Vector3 position, Vector3 direction, Vector3 normal)
    {
        if (receiver == null) throw new ArgumentNullException(nameof(receiver));
        var before = receiver.Health;
        receiver.BulletDamage(damage, source, position, direction, normal);
        return new(before, receiver.Health);
    }

    /// <summary>The game's explosion force on a dead enemy's ragdoll (<c>Dam_EnemyDamageBase.SendExplosionForce(origin, magnitude)</c>): the
    /// game replicates it to every peer and applies it to the body. It takes no health and nothing of it can be read back (the game keeps
    /// no value to read), so nothing is returned; the call throws the game's own exception when the request is refused.</summary>
    public static void Force(Dam_EnemyDamageBase receiver, Vector3 origin, float magnitude)
    {
        if (receiver == null) throw new ArgumentNullException(nameof(receiver));
        receiver.SendExplosionForce(origin, magnitude);
    }

    /// <summary>A bullet hit through the game's own bullet resolution (limb, weak spot, armour, falloff, the shooter's hit marker),
    /// exactly as a stock bullet. Returns the game's answer: true when it hit a damage receiver.</summary>
    public static bool Bullet(Weapon.WeaponHitData hit) => BulletWeapon.BulletHit(hit, true, 0f, 0u, true);

    private const int ShooterRaySteps = 32;

    /// <summary>One ray from <paramref name="start"/> along <paramref name="direction"/> (a unit vector) for <paramref name="range"/> metres
    /// against the game's bullet ray layers (<c>LayerManager.MASK_BULLETWEAPON_RAY</c>, the global trigger setting): the nearest hit that is
    /// not part of <paramref name="shooter"/> (<see cref="IsPartOf"/>). The shooter's own colliders are stepped over, at most 32 of them;
    /// a ray that crosses more throws. <c>hit.distance</c> is measured from <paramref name="start"/>. The same ray a <see cref="Shot"/>
    /// casts, for a caller that wants the point and not a bullet.</summary>
    public static bool ShooterRay(Agent shooter, Vector3 start, Vector3 direction, float range, out RaycastHit hit)
    {
        if (shooter == null) throw new ArgumentNullException(nameof(shooter));
        var travelled = 0f;
        for (var step = 0; step < ShooterRaySteps; step++)
        {
            if (!Physics.Raycast(start + direction * travelled, direction, out hit, range - travelled,
                    LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal)) { hit = default; return false; }
            if (hit.collider != null && !IsPartOf(hit.collider, shooter))
            {
                hit.distance += travelled;
                return true;
            }
            travelled += hit.distance + 0.01f;
            if (travelled >= range) { hit = default; return false; }
        }
        hit = default;
        throw new InvalidOperationException("A ray crossed too many shooter colliders; no hit found.");
    }

    /// <summary>One identity per damage receiver: every limb of an actor maps to the actor's base receiver, so a query that meets
    /// several limbs counts the actor once.</summary>
    public static long ReceiverIdentity(IDamageable receiver) => (receiver.GetBaseDamagable() ?? receiver).Pointer.ToInt64();

    /// <summary>The collider belongs to <paramref name="agent"/> itself (its body, its limbs).</summary>
    public static bool IsPartOf(Collider collider, Agent agent)
        => collider.GetComponentInParent<IDamageable>()?.GetBaseAgent()?.Pointer == agent.Pointer
            || collider.GetComponentInParent<Agent>()?.Pointer == agent.Pointer;

    /// <summary>A collider a hit from <paramref name="context"/> may damage: a damage receiver that is not part of the context
    /// actor, and whose actor (if any) is alive and in the context's dimension.</summary>
    public static bool DirectReceiver(Agent context, Collider collider, out long identity)
    {
        identity = 0;
        if (IsPartOf(collider, context)) return false;
        var receiver = collider.GetComponentInParent<IDamageable>();
        if (receiver == null) return false;
        var agent = receiver.GetBaseAgent();
        if (agent != null && (!agent.Alive || agent.DimensionIndex != context.DimensionIndex)) return false;
        identity = ReceiverIdentity(receiver);
        return true;
    }
}
