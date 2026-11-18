using System;
using System.Collections.Generic;
using Gear;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// The facts of a model that stands in for one part of a game gun (a barrel): the game's muzzle, sight and left-hand nodes point at the points the model
/// declares (<see cref="GunPartPoint"/>), on the same terms as <see cref="GunMount"/>: a bare left point is where the palm touches, so the wrist goes back from it by the
/// game's own hand (<see cref="GunHold.WristFromTouch"/>) and takes what the game's own hand node does on its own, so a reload still moves the hand. The gun, its size, its
/// other parts and its hold stay the game's. The model belongs to its owner; <see cref="Release"/> gives the game's nodes back. A point left out changes nothing.
/// </summary>
public sealed class GunPartMount
{
    private static readonly Dictionary<IntPtr, GunPartMount> ByHolder = new();
    private static readonly List<GunPartMount> All = new();

    private readonly GearPartHolder _holder;
    private readonly IntPtr _holderPointer;
    private readonly Transform _holderTransform, _model;
    private readonly Transform? _muzzle, _sight, _leftProxy;
    private ItemEquippable? _weapon;
    private Transform? _nativeMuzzle, _nativeSight, _nativeLeft, _weaponMuzzle, _weaponSight, _weaponLeft, _syncLeft;
    private PlayerSyncIK? _sync;
    private Transform? _donorGrip, _donorLeft;
    private GunFrame _gripRest, _leftRest, _targetRest;

    private GunPartMount(GearPartHolder holder, Transform model, Transform? muzzle, Transform? sight, Transform? leftProxy)
    {
        _holder = holder;
        _holderPointer = holder.Pointer;
        _holderTransform = holder.transform;
        _model = model;
        _muzzle = muzzle;
        _sight = sight;
        _leftProxy = leftProxy;
    }

    private bool Alive => _holder != null && !_holder.WasCollected && _model != null && !_model.WasCollected;

    /// <summary>The live mount on <paramref name="holder"/>, or null.</summary>
    public static GunPartMount? Of(GearPartHolder? holder)
    {
        if (holder == null || !ByHolder.TryGetValue(holder.Pointer, out var mount)) return null;
        return mount.Alive ? mount : null;
    }

    /// <summary>Points the nodes of the assembled gun on <paramref name="holder"/> at <paramref name="points"/> of <paramref name="model"/>, its spawned instance on the part
    /// (points in its frame). Throws, with the gun as it was, when the gun lacks a node a point takes over.</summary>
    public static GunPartMount Create(GearPartHolder holder, Transform model, GunPartPoint points, string name)
    {
        if (holder == null || holder.WasCollected) throw new ArgumentNullException(nameof(holder));
        if (model == null) throw new ArgumentNullException(nameof(model));
        Of(holder)?.Release();
        var weapon = holder.GetComponentInParent<ItemEquippable>();
        var nodes = GunNodes.Of(holder, weapon);
        if (nodes.RightHand == null || (points.LeftHand != null && nodes.LeftHand == null) || (points.Muzzle != null && nodes.Muzzle == null))
            throw new System.IO.InvalidDataException("The game gun has no node for the points of " + name + " (it is not assembled yet).");
        // Sockets look along the gun whichever way the model faces, as the whole gun's do.
        var muzzle = GunMount.PlaceSocket(model, null, GunNodes.MuzzleSocket, points.Muzzle, name);
        var sight = GunMount.PlaceSocket(model, null, GunNodes.SightSocket, points.Sight, name);
        if (muzzle != null) muzzle.rotation = nodes.Muzzle!.rotation;
        if (sight != null && nodes.Sight != null) sight.rotation = nodes.Sight.rotation;
        Transform? proxy = null;
        if (points.LeftHand != null)
        {
            proxy = new GameObject(name + ".LeftHandIK").transform;
            proxy.SetParent(holder.transform, false);
        }
        var mount = new GunPartMount(holder, model, muzzle, sight, proxy);
        if (points.LeftHand is { } left)
        {
            mount._donorGrip = nodes.RightHand;
            mount._donorLeft = nodes.LeftHand;
            mount._gripRest = GunMount.Frame(holder.transform, nodes.RightHand);
            mount._leftRest = GunMount.Frame(holder.transform, nodes.LeftHand!);
            var touch = holder.transform.InverseTransformPoint(model.TransformPoint(new Vector3(left.X, left.Y, left.Z)));
            var wrist = GunHold.WristFromTouch(GunMount.Rules.LeftPalm, mount._leftRest.Rotation);
            mount._targetRest = new GunFrame(new System.Numerics.Vector3(touch.x, touch.y, touch.z) + wrist, mount._leftRest.Rotation);
        }
        mount.Capture(weapon);
        try { mount.Bind(); mount.Step(); }
        catch { mount.Restore(); mount.DestroySockets(); throw; }
        ByHolder[holder.Pointer] = mount;
        All.Add(mount);
        GunMount.Hook();
        return mount;
    }

    /// <summary>Gives the game's nodes back and removes the sockets.</summary>
    public void Release()
    {
        All.Remove(this);
        if (ByHolder.TryGetValue(_holderPointer, out var listed) && ReferenceEquals(listed, this)) ByHolder.Remove(_holderPointer);
        if (_holder != null && !_holder.WasCollected) Restore();
        DestroySockets();
    }

    /// <summary>On assembly and wield: a new weapon or changed hand node is taken over, and a node the game pointed back is pointed at the model again.</summary>
    internal void Refresh()
    {
        var weapon = _holder.GetComponentInParent<ItemEquippable>();
        if (weapon != _weapon) Capture(weapon);
        if (_leftProxy != null)
        {
            var left = _holder.m_aLeftHand;
            if (left != null && left != _leftProxy && left != _donorLeft) { _donorLeft = left; _nativeLeft = left; _leftRest = GunMount.Frame(_holderTransform, left); }
            if (_holder.m_aRightHand != null && _holder.m_aRightHand != _donorGrip) { _donorGrip = _holder.m_aRightHand; _gripRest = GunMount.Frame(_holderTransform, _donorGrip); }
        }
        Bind();
    }

    /// <summary>Puts the left wrist target where the game's hand has it this frame.</summary>
    internal void Step()
    {
        if (_leftProxy == null || _donorGrip == null || _donorLeft == null) return;
        GunMount.ApplyFrame(_holderTransform, _leftProxy, GunPose.FollowHand(_gripRest, GunMount.Frame(_holderTransform, _donorGrip), _leftRest, GunMount.Frame(_holderTransform, _donorLeft), _targetRest));
    }

    internal static void TickAll()
    {
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var mount = All[i];
            if (!mount.Alive) { mount.Release(); continue; }
            // The local first-person gun steps just before the holder reads it.
            if (mount._weapon == null || !mount._weapon.IsFirstPerson) mount.Step();
        }
    }

    private void Capture(ItemEquippable? weapon)
    {
        _weapon = weapon;
        _nativeMuzzle = _holder.m_aMuzzle != _muzzle ? _holder.m_aMuzzle : _nativeMuzzle;
        _nativeSight = _holder.m_aSightLook != _sight ? _holder.m_aSightLook : _nativeSight;
        _nativeLeft = _holder.m_aLeftHand != _leftProxy ? _holder.m_aLeftHand : _nativeLeft;
        if (weapon != null)
        {
            _weaponMuzzle = weapon.MuzzleAlign != _muzzle ? weapon.MuzzleAlign : _weaponMuzzle;
            _weaponSight = weapon.SightLookAlign != _sight ? weapon.SightLookAlign : _weaponSight;
            _weaponLeft = weapon.m_leftHandGripAlign != _leftProxy ? weapon.m_leftHandGripAlign : _weaponLeft;
        }
    }

    private void Bind()
    {
        if (_muzzle != null) _holder.m_aMuzzle = _muzzle;
        if (_sight != null) _holder.m_aSightLook = _sight;
        if (_leftProxy != null) _holder.m_aLeftHand = _leftProxy;
        if (_weapon == null) return;
        if (_muzzle != null) _weapon.MuzzleAlign = _muzzle;
        if (_sight != null) _weapon.SightLookAlign = _sight;
        if (_leftProxy == null) return;
        _weapon.m_leftHandGripAlign = _leftProxy;
        // The third-person solver caches its target at the wield; a mount made later replaces that too.
        var sync = GunMount.WieldedSyncIK(_weapon);
        if (sync != null && sync.m_leftArmSolver.target != _leftProxy)
        {
            _sync = sync;
            _syncLeft = sync.m_leftArmSolver.target;
            sync.SetLeftArmTarget(_leftProxy);
        }
    }

    private void Restore()
    {
        if (_muzzle != null && _holder.m_aMuzzle == _muzzle) _holder.m_aMuzzle = Live(_nativeMuzzle);
        if (_sight != null && _holder.m_aSightLook == _sight) _holder.m_aSightLook = Live(_nativeSight);
        if (_leftProxy != null && _holder.m_aLeftHand == _leftProxy) _holder.m_aLeftHand = Live(_nativeLeft);
        if (_weapon != null && !_weapon.WasCollected)
        {
            if (_muzzle != null && _weapon.MuzzleAlign == _muzzle) _weapon.MuzzleAlign = Live(_weaponMuzzle);
            if (_sight != null && _weapon.SightLookAlign == _sight) _weapon.SightLookAlign = Live(_weaponSight);
            if (_leftProxy != null && _weapon.m_leftHandGripAlign == _leftProxy) _weapon.m_leftHandGripAlign = Live(_weaponLeft);
        }
        // A weapon switch may already have put another item's target there; only this mount's binding is undone.
        if (_sync != null && !_sync.WasCollected && _sync.m_leftArmSolver != null && _sync.m_leftArmSolver.target == _leftProxy) _sync.SetLeftArmTarget(Live(_syncLeft));
    }

    private void DestroySockets()
    {
        foreach (var socket in new[] { _muzzle, _sight, _leftProxy })
            if (socket != null && !socket.WasCollected) Object.Destroy(socket.gameObject);
    }

    // Unity's null comparison rejects destroyed wrappers: a dead game node is never put back.
    private static Transform Live(Transform? value) => value != null ? value : null!;
}
