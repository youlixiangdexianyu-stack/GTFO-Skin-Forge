using System;
using Gear;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using UnityEngine;

namespace ForgeCore;

public static partial class Combat
{
    /// <summary>What a shot hands the game's bullet resolution: the damage, the stagger and precision multipliers and the range the damage
    /// falloff is flat to (no falloff within the range).</summary>
    public readonly record struct BulletProfile(float Damage, float Stagger, float Precision, float Range);

    /// <summary>One shot from a point at a target, settled as a stock bullet: find where it lands, check that it lands on the receiver that
    /// was meant, then hand the hit to the game's own bullet resolution (<see cref="Bullet"/>: limb, weak spot, armour, falloff,
    /// replication, the shooter's hit marker). Nothing here applies damage by itself. Keep one instance per call site: it owns its
    /// <c>WeaponHitData</c> and ray buffer, and nothing is allocated per shot. Main thread only.
    /// <para>The shooter's own colliders are stepped over (at most 32 of them; a ray that crosses more throws and submits nothing). A ray
    /// is cast against <c>LayerManager.MASK_BULLETWEAPON_RAY</c> with the global trigger setting, nearest hit only, so a long ray never
    /// meets more colliders than a fixed buffer holds.</para></summary>
    public class Shot
    {
        private const int RayBuffer = 64;
        private readonly Weapon.WeaponHitData _hit = new();
        private readonly Il2CppStructArray<RaycastHit> _rays = new(RayBuffer);

        /// <summary>Where the last trace or resolved hit began.</summary>
        public Vector3 Start { get; private set; }
        /// <summary>Where it ended: the hit point, or the end of the range when nothing was hit.</summary>
        public Vector3 End { get; private set; }
        public bool HasHit { get; private set; }
        /// <summary>The damage receiver the hit landed on, or null.</summary>
        public IDamageable? Receiver => HasHit && _hit.rayHit.collider != null
            ? _hit.rayHit.collider.GetComponentInParent<IDamageable>() : null;

        /// <summary>Traces the held gun's shot along the player's own camera: from the look camera's position along its forward.</summary>
        public void Trace(BulletWeapon weapon, in BulletProfile profile)
        {
            var camera = weapon.Owner.FPItemHolder.m_LookCamera;
            if (camera == null) throw new InvalidOperationException("The held gun has no camera.");
            var look = camera.transform;
            Trace(weapon, profile, look.position, look.forward);
        }

        /// <summary>Traces from <paramref name="origin"/> along <paramref name="direction"/> to find what the aim lands on, then again from the
        /// gun's visible muzzle to that point, so cover blocks both the effect and the damage. The first-person model has its own field of
        /// view: its muzzle is put at the same screen point in the world camera's space, and pulled in front of any level surface between
        /// the camera and it.</summary>
        public void Trace(BulletWeapon weapon, in BulletProfile profile, Vector3 origin, Vector3 direction)
        {
            // The owner, holder, camera and muzzle are read once, and what Prepare writes into the hit data is kept here rather than
            // read back: each of those is a native call (contract P8, hot-path getters).
            var owner = weapon.Owner;
            var holder = owner.FPItemHolder;
            var camera = holder.m_LookCamera;
            var muzzle = weapon.MuzzleAlign;
            if (camera == null || muzzle == null) throw new InvalidOperationException("The held gun has no camera or muzzle.");
            direction.Normalize();
            Prepare(owner, profile, origin, direction, profile.Range);
            var target = Cast(origin, direction, profile.Range, owner, out var aimed) ? aimed.point : origin + direction * profile.Range;

            var start = ShownMuzzle(holder, camera, muzzle, origin);

            var toTarget = target - start;
            Start = start;
            var squared = toTarget.x * toTarget.x + toTarget.y * toTarget.y + toTarget.z * toTarget.z;
            if (squared < 0.0001f)
            {
                End = start;
                HasHit = false;
                return;
            }
            var distance = MathF.Sqrt(squared);
            var along = toTarget / distance;
            var range = MathF.Min(profile.Range, distance + 0.025f);
            Prepare(owner, profile, start, along, range);
            HasHit = Cast(start, along, range, owner, out var hit);
            End = HasHit ? hit.point : start + along * range;
        }

        /// <summary>Traces from <paramref name="start"/> along <paramref name="direction"/> for a shooter whose camera is not this peer's
        /// (a remote player, a bot, a projectile's owner).</summary>
        public void Trace(PlayerAgent owner, in BulletProfile profile, Vector3 start, Vector3 direction)
        {
            direction.Normalize();
            Prepare(owner, profile, start, direction, profile.Range);
            HasHit = Cast(start, direction, profile.Range, owner, out var hit);
            Start = start;
            End = HasHit ? hit.point : start + direction * profile.Range;
        }

        // The nearest hit only. The shooter's own colliders are stepped over; the reported distance stays measured from the start.
        private bool Cast(Vector3 start, Vector3 direction, float range, PlayerAgent owner, out RaycastHit hit)
        {
            if (!ShooterRay(owner, start, direction, range, out hit)) return false;
            _hit.rayHit = hit;
            return true;
        }

        /// <summary>Whether a ray from <paramref name="start"/> toward <paramref name="point"/> first lands on the same receiver as
        /// <paramref name="target"/> (the hit is in <paramref name="hit"/>). False when it lands on another receiver, on nothing, or on
        /// level geometry first.</summary>
        public bool VisibleTarget(PlayerAgent owner, Vector3 start, Vector3 point, Collider target, out RaycastHit hit)
        {
            var offset = point - start;
            if (Vec.SqrLength(offset) < 0.000001f) return RayTarget(start, point, target, out hit);
            hit = default;
            if (!Cast(start, offset.normalized, Vec.Length(offset) + 0.025f, owner, out var found)) return false;
            var actual = found.collider.GetComponentInParent<IDamageable>();
            var wanted = target.GetComponentInParent<IDamageable>();
            if (actual == null || wanted == null || ReceiverIdentity(actual) != ReceiverIdentity(wanted)) return false;
            hit = found;
            return true;
        }

        /// <summary>For an area query, which selects actors and not damage limbs: the first surface of <paramref name="target"/>'s actor along
        /// the radial path from <paramref name="origin"/> to <paramref name="point"/>. A collider that contains the origin is omitted by
        /// Unity's rays, so its exit surface is found by a ray from outside on the same path (the hit distance is then 0: damage travels from
        /// the centre to that surface). Each caller keeps its own policy for blockers between the two.</summary>
        public bool RayTarget(Vector3 origin, Vector3 point, Collider target, out RaycastHit hit)
        {
            var offset = point - origin;
            if (Vec.SqrLength(offset) < 0.000001f) offset = target.bounds.center - origin;
            var direction = Vec.SqrLength(offset) > 0.000001f ? offset.normalized : Vec.Up;
            if (Vec.SqrLength(target.ClosestPoint(origin) - origin) >= 0.00000001f)
                return ReceiverRay(origin, direction, Vec.Length(offset) + 0.025f, target, out hit);

            var reach = ReceiverBounds(target, origin, out _).size.magnitude + 0.05f;
            var forward = ReceiverRay(origin, direction, reach, target, out hit);
            var exit = ReceiverRay(origin + direction * reach, -direction, reach, target, out var surface, exitsContainingColliders: true);
            if (exit && (!forward || surface.distance < hit.distance)) hit = surface;
            if (forward || exit) hit.distance = 0f;
            return forward || exit;
        }

        /// <summary>For a sphere sweep or overlap, which only names a contact candidate: a parallel incoming ray through the contact point,
        /// so the velocity, the first limb and its native modifiers agree. The sweep's travel distance (an overlap's 0) is kept in
        /// <paramref name="hit"/> for the game's falloff.</summary>
        public bool ContactTarget(Vector3 origin, Vector3 direction, RaycastHit contact, out RaycastHit hit)
        {
            var collider = contact.collider;
            if (collider == null) { hit = default; return false; }
            direction = direction.normalized;
            var point = contact.distance > 0f ? contact.point : collider.ClosestPoint(origin);
            var along = Vector3.Dot(point - origin, direction);
            var start = point - direction * along;
            var bounds = ReceiverBounds(collider, start, out var inside);
            var reach = inside ? Vec.Length(bounds.size) + 0.05f : 0f;
            var found = ReceiverRay(start - direction * reach, direction, MathF.Max(0f, along) + reach + 0.025f, collider, out hit);
            if (found) hit.distance = contact.distance;
            return found;
        }

        private static Bounds ReceiverBounds(Collider collider, Vector3 point, out bool inside)
        {
            var bounds = collider.bounds;
            inside = Vec.SqrLength(collider.ClosestPoint(point) - point) < 0.00000001f;
            var limbs = collider.GetComponentInParent<IDamageable>()?.GetBaseDamagable()?.TryCast<Dam_EnemyDamageBase>()?.DamageLimbs;
            if (limbs != null)
                for (var index = 0; index < limbs.Length; index++)
                    if (limbs[index]?.GetComponent<Collider>() is { } limb && limb.enabled && limb.gameObject.activeInHierarchy)
                    {
                        bounds.Encapsulate(limb.bounds);
                        inside |= Vec.SqrLength(limb.ClosestPoint(point) - point) < 0.00000001f;
                    }
            return bounds;
        }

        private bool ReceiverRay(Vector3 origin, Vector3 direction, float distance, Collider target, out RaycastHit hit,
            bool exitsContainingColliders = false)
        {
            hit = default;
            var receiver = target.GetComponentInParent<IDamageable>();
            if (receiver == null) return false;
            var identity = ReceiverIdentity(receiver);
            var count = Physics.RaycastNonAlloc(origin, direction, _rays, distance, LayerManager.MASK_BULLETWEAPON_RAY, QueryTriggerInteraction.UseGlobal);
            if (count == _rays.Length) throw new InvalidOperationException("The limb ray buffer is full; a truncated hit is refused.");
            var nearest = float.PositiveInfinity;
            for (var index = 0; index < count; index++)
            {
                var candidate = _rays[index];
                var collider = candidate.collider;
                var actual = collider?.GetComponentInParent<IDamageable>();
                if (collider == null || actual == null || ReceiverIdentity(actual) != identity) continue;
                if (exitsContainingColliders)
                {
                    var centre = origin + direction * distance;
                    if (Vec.SqrLength(collider.ClosestPoint(centre) - centre) >= 0.00000001f) continue;
                    candidate.distance = distance - candidate.distance;
                }
                if (candidate.distance >= nearest) continue;
                nearest = candidate.distance;
                hit = candidate;
            }
            return float.IsFinite(nearest);
        }

        private void Prepare(PlayerAgent owner, in BulletProfile profile, Vector3 origin, Vector3 direction, float range)
        {
            _hit.owner = owner;
            _hit.fireAtPos = origin;
            _hit.fireDir = direction;
            _hit.hasFireDir = true;
            _hit.angOffsetX = 0f;
            _hit.angOffsetY = 0f;
            _hit.randomSpread = 0f;
            _hit.maxRayDist = range;
            _hit.damage = profile.Damage;
            _hit.staggerMulti = profile.Stagger;
            _hit.precisionMulti = profile.Precision;
            _hit.damageFalloff = new Vector2(profile.Range, profile.Range);
            _hit.vfxBulletHit = null;
            _hit.gearCategoryId = 0;
        }

        /// <summary>Takes <paramref name="hit"/> (found by <see cref="RayTarget"/>, <see cref="ContactTarget"/> or
        /// <see cref="VisibleTarget"/>) as the shot from <paramref name="start"/> along <paramref name="direction"/>, ready to
        /// <see cref="Apply()"/>.</summary>
        public void ResolveHit(PlayerAgent owner, in BulletProfile profile, Vector3 start, Vector3 direction, RaycastHit hit)
        {
            if (!float.IsFinite(Vec.SqrLength(direction)) || Vec.SqrLength(direction) < 0.00000001f)
                throw new InvalidOperationException("The hit has no travel direction.");
            Prepare(owner, profile, start, direction.normalized, profile.Range);
            _hit.rayHit = hit;
            Start = start;
            End = hit.point;
            HasHit = true;
        }

        /// <summary>Submits the traced or resolved hit, if there is one, through <see cref="Submit"/>.</summary>
        public void Apply()
        {
            if (HasHit) Submit(_hit);
        }

        /// <summary>Submits the hit with another damage and stagger.</summary>
        public void Apply(float damage, float stagger)
        {
            _hit.damage = damage; _hit.staggerMulti = stagger;
            Apply();
        }

        /// <summary>Deals one hit through the game's own bullet resolution (<see cref="Bullet"/>, the stock packet category 0), as a stock
        /// bullet's: nothing is submitted when the collider belongs to the shooter. A subclass wraps this where the shooter's own hit must
        /// be told from other bullets.</summary>
        protected virtual void Submit(Weapon.WeaponHitData hit)
        {
            var shooter = hit.owner ?? throw new InvalidOperationException("A hit requires its shooter.");
            var collider = hit.rayHit.collider;
            if (collider == null || IsPartOf(collider, shooter)) return;
            Bullet(hit);
        }

        /// <summary>A contact with only its collider, point and direction filled: query metadata for <see cref="ContactTarget"/>, which
        /// replaces it with a physics ray before any damage.</summary>
        public static RaycastHit OverlapContact(Collider collider, Vector3 origin, Vector3 direction)
            => new() { m_Collider = collider.GetInstanceID(), point = collider.ClosestPoint(origin), normal = -direction, distance = 0f };
    }
}
