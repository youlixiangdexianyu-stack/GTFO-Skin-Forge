using System;
using System.Collections.Generic;
using GameData;
using Gear;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Player;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>What is particular to one <see cref="GunMount"/>: the model and how to read it. Where it goes, how big it is and where the hands go
/// are the same calculation for every gun (<see cref="GunHold"/>, <c>Data/gun-hold.json</c>); there is no per-gun size or offset.</summary>
public sealed class GunMountOptions
{
    /// <summary>The model's rotation that turns its front to its +Z (<c>Quaternion.Euler(0, 180, 0)</c> for an export facing -Z).</summary>
    public Quaternion Front { get; set; } = Quaternion.identity;

    /// <summary>The name of the model's instance.</summary>
    public string Name { get; set; } = "ForgeVisual.GunModel";

    /// <summary>Reads the sockets of the spawned model; <see cref="GunSockets.Find"/> unless its parts and sockets share names.</summary>
    public Func<Transform, GunSockets> Sockets { get; set; } = GunSockets.Find;

    /// <summary>The package's hold table (<see cref="GunGrips"/>), looked up by the prefab's name: the row's preset must be one of
    /// <see cref="GunMount.Rules"/>' presets and gives the game hold a missing left hand is derived from (a model's own hand sockets are kept,
    /// and a missing right grip is the model's origin); a listed muzzle, sight, shell-eject or flashlight socket is moved to its point (or made
    /// there when the model has none) before the model is seated, and the row's hand adjustments fine-tune the hands. Null: the model's own
    /// sockets and no adjustment.</summary>
    public GunGrips? Grips { get; set; }

    /// <summary>The package's sights (<see cref="GunSights"/>), looked up by the prefab's name: a listed model gets that ForgeVisual sight in place of
    /// its replaced parts, its <c>SightLook</c> at the sight's look point and the sight's aim field of view. Null: the model's own sight.</summary>
    public GunSights? Sights { get; set; }

    /// <summary>The prefab is already a spawned instance another package owns (a placed model-library model): it is mounted itself, not copied, its
    /// grip row is looked up by <see cref="Name"/>, and a failed mount leaves it inactive for its owner to remove.</summary>
    public bool Adopt { get; set; }

    /// <summary>The box of the model's meshes drawn without a renderer (batched imported models: their mesh bounds under the placement's poses),
    /// in the model's own frame; measured with its renderers. Their draws follow the model's layer, so they show in first and third person.</summary>
    public Bounds? DrawnBounds { get; set; }

    /// <summary>Whether the magazine shows at first (a reload may already have taken it off).</summary>
    public bool MagazineVisible { get; set; } = true;

    /// <summary>Called with the model, its magazine and the layer when the view changes, to put surfaces of the caller's own on it. Null: each
    /// renderer takes a first-person copy of its materials in first person (<see cref="LitMaterial.FirstPersonCopy"/>) and casts shadows only in the world.</summary>
    public Action<Transform, Transform?, int>? ApplyView { get; set; }

    /// <summary>Called when the game's nodes point at the model on a weapon. A throw undoes the binding.</summary>
    public Action<ItemEquippable>? Bound { get; set; }

    /// <summary>Called with one line when the view changes (the layer the model draws on and whether the local first-person hand holds it), so a
    /// package can write it to its record.</summary>
    public Action<string>? Viewed { get; set; }

    /// <summary>Called with the weapon's pointer when the mount ends.</summary>
    public Action<IntPtr>? Released { get; set; }
}

/// <summary>
/// A gun model on a game gun, the one way every gun model is put on: the instance is a child of the gun's part holder (what the game and
/// tools move as the gun), <see cref="GunHold.Solve"/> sizes and seats it, the game's meshes are hidden and their animators keep running, its magazine stays with the gun and takes
/// what the game's magazine node does on its own (about the magazine itself), its first-person surfaces use the game's item camera, and the game's hand, bore and sight nodes point at the model, in
/// first and third person. The hands go to the model: the right hand's target is the game hand's wrist on the model's right grip, and the left hand's is the model's own
/// left grip (or, when the model has none, the game gun's own left hand relative to its right mapped onto that grip: <see cref="GunHold.LeftGrip"/>); each target is carried with the gun and takes what the game's node for that hand
/// does on its own, so a reload and a raise still move the hands, and the game's arm IK reaches it. In first person the item takes the rule's hold (a copy of its
/// first-person settings, never the shared block). The left target is pulled back along the gun until it is within the arm's reach at the hip (<see cref="GunHold.IntoReach"/>),
/// with one line for the model's author; the right hand, the model's anchor, is reported when it is out of reach.
/// The game's reload shows and hides the model's magazine by itself. The library steps every mount; the first-person pose goes in before the holder's LateUpdate reads the hand targets,
/// and a gun's assembly or wield looks at the mount at once, so a part the game adds never shows.
/// </summary>
public sealed class GunMount
{
    /// <summary>The layer the game draws first-person items on.</summary>
    public const int FirstPersonLayer = 12;

    private static readonly List<GunMount> All = new();
    private static readonly Dictionary<IntPtr, GunMount> ByHolder = new();
    private static bool _hooked;
    private static IntPtr _heldItem;
    private static GunMount? _held;
    // One held copy of each first-person settings block a mounted gun was given (the game's blocks are shared with its own guns).
    // Keyed with the sight's aim field of view: two guns on one block may carry different sights.
    private static readonly Dictionary<(IntPtr, int), ItemFPSSettingsDataBlock> HoldCopies = new();
    private static readonly HashSet<IntPtr> HoldCopyPointers = new();
    private static GunHoldRules? _rules;

    /// <summary>The rule every gun model is held by (<c>Data/gun-hold.json</c>, embedded in ForgeVisual).</summary>
    public static GunHoldRules Rules => _rules ??= ReadRules();

    private static GunHoldRules ReadRules()
    {
        using var stream = typeof(GunMount).Assembly.GetManifestResourceStream("ForgeVisual.Data.gun-hold.json")
            ?? throw new System.IO.InvalidDataException("ForgeVisual is built without Data/gun-hold.json.");
        using var reader = new System.IO.StreamReader(stream);
        return GunHoldRules.Parse(reader.ReadToEnd());
    }

    private sealed class Bindings
    {
        internal Transform? Right, Left, Muzzle, Sight, Shell, Flashlight;
        internal ItemEquippable? Weapon;
        internal Transform? WeaponRight, WeaponLeft, WeaponMuzzle, WeaponSight;
        internal PlayerSyncIK? SyncIK;
        internal Transform? SyncLeftTarget, SyncProxy;
        internal ItemFPSSettingsDataBlock? Settings;
        internal bool WritesAttempted;
    }

    private readonly GearPartHolder _holder;
    private readonly IntPtr _holderPointer;
    private readonly Transform _holderTransform;
    private readonly GunMountOptions _options;
    private readonly GameObject _modelObject;
    private readonly Transform _model, _rightProxy, _leftProxy;
    private readonly Transform? _muzzle, _sight;
    private Transform? _shell, _flashlight;
    // Other models that move with a node of the game's gun (FollowNode): carried with the gun, plus what that node does on its own.
    private readonly List<(Transform Part, Transform Node, GunFrame NodeRest, GunFrame PartRest)> _followers = new();
    private readonly int _worldLayer;
    private readonly List<Renderer> _hidden = new();
    private readonly List<(Animator Animator, AnimatorCullingMode Mode)> _animators = new();
    private (Renderer Renderer, Il2CppReferenceArray<Material> World, Il2CppReferenceArray<Material> FirstPerson)[]? _surfaces;
    private ItemEquippable? _owner;
    private Bindings _native = null!;
    private Transform _donorGrip, _donorLeft;
    private Transform? _donorMagazine, _magazine;
    // A ForgeVisual sight stood on the model, and its aim field of view (0: the game gun's own).
    private Transform? _sightPart;
    private int _sightLookFov;
    private GameObject? _magazineObject;
    private GunFrame _gripRest, _leftRest, _rightTargetRest, _leftTargetRest, _modelRest, _magazineNodeRest, _magazineRest;
    private GunHoldResult _hold;
    private ItemFPSSettingsDataBlock? _holdOriginal;
    private int _viewLayer = -1;
    private float _hipSince = -1f;
    private float _nodesLostSince = -1f;
    // How long the game's gun may stand without a hand node (it clears them while taking the gun apart) before that is reported.
    private const float NodeLossGraceSeconds = 3f;
    // Longer than the game's raise and aim transitions (well under a second).
    private const float HipSettleSeconds = 1.5f;
    private const float HandShapeFade = 0.15f;
    // The most the first-person holder may stand from the arm shoulders and the hand targets for the reach to be read (the arm itself reaches 0.62).
    private const float SameViewMetres = 3f;
    private bool _boundFirstPerson, _magazineVisible, _reachRead;
    // The local first-person holder wields this gun (seen in its LateUpdate): the game's own IsFirstPerson may still read false at the wield call.
    private bool _inHand;
    private IntPtr _missItem;

    private bool FirstPerson => _inHand || (_owner != null && _owner.IsFirstPerson);

    private GunMount(GearPartHolder holder, GunMountOptions options, GameObject model, Transform rightProxy, Transform leftProxy,
        Transform? muzzle, Transform? sight, int worldLayer, Transform donorGrip, Transform donorLeft)
    {
        _holder = holder;
        _holderPointer = holder.Pointer;
        _holderTransform = holder.transform;
        _options = options;
        _modelObject = model;
        _model = model.transform;
        _rightProxy = rightProxy;
        _leftProxy = leftProxy;
        _muzzle = muzzle;
        _sight = sight;
        _worldLayer = worldLayer;
        _donorGrip = donorGrip;
        _donorLeft = donorLeft;
        _magazineVisible = options.MagazineVisible;
    }

    /// <summary>The model's instance.</summary>
    public Transform Model => _model;

    /// <summary>The scale the rule gave the model (1 = the metres it was built in).</summary>
    public float Scale => _hold.Scale;

    /// <summary>Where the rule put the model and the two wrist targets at rest, in the game gun's frame.</summary>
    public GunHoldResult Hold => _hold;

    /// <summary>True while the gun, the model and the hand targets exist.</summary>
    public bool Alive => _holder != null && !_holder.WasCollected && _model != null && !_model.WasCollected
        && _rightProxy != null && !_rightProxy.WasCollected && _leftProxy != null && !_leftProxy.WasCollected;

    /// <summary>A part of the game's own gun under this mount's holder, hidden while the model stands for it (a copy of the gun leaves it out).</summary>
    internal bool IsHiddenPart(Transform part)
        => part.IsChildOf(_holderTransform) && !part.IsChildOf(_model) && (_magazine == null || !part.IsChildOf(_magazine));

    /// <summary>Which of the game's own grip animations the hands of the gun on <paramref name="holder"/> take (the animation state's name; null or empty
    /// keeps the one its parts gave). The game reads the holder's names when the gun is wielded and the item's own fields do nothing (G0), so these are what
    /// is written; a gun already in the local first-person hands changes its grip at once. Only the first-person arms play these: third person keeps its pose.</summary>
    public static void SetHandShapes(GearPartHolder holder, string? left, string? right)
    {
        if (holder == null || holder.WasCollected) throw new ArgumentNullException(nameof(holder));
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)) return;
        if (!string.IsNullOrEmpty(left)) holder.m_animLeftHand = left;
        if (!string.IsNullOrEmpty(right)) holder.m_animRightHand = right;
        var weapon = holder.GetComponentInParent<ItemEquippable>();
        if (weapon == null || !weapon.IsFirstPerson || weapon.Owner == null) return;
        var hands = weapon.Owner.FPItemHolder;
        if (hands == null || hands.WieldedItem == null || hands.WieldedItem.Pointer != weapon.Pointer || hands.FPSArms == null) return;
        var animator = hands.FPSArms.m_anim;
        if (!string.IsNullOrEmpty(left)) animator.CrossFadeInFixedTime(left, HandShapeFade, PlayerFPSBody.ANIM_LAYER_LEFT_HAND);
        if (!string.IsNullOrEmpty(right)) animator.CrossFadeInFixedTime(right, HandShapeFade, PlayerFPSBody.ANIM_LAYER_RIGHT_HAND);
    }

    /// <summary>Another model (a spawned instance another package owns) moves with <paramref name="node"/> of the game's gun as the mount's magazine moves with
    /// the game's magazine node: carried with the gun, plus what the node does on its own. It stands in the mounted model's frame, shown at the model's size and
    /// on its view; its owner removes it. A batched imported model moves as one placement, so each piece that moves on its own is its own placement.</summary>
    public void FollowNode(Transform part, Transform node)
    {
        if (part == null) throw new ArgumentNullException(nameof(part));
        if (node == null) throw new ArgumentNullException(nameof(node));
        part.SetParent(_model, false);
        _followers.Add((part, node, Frame(_holderTransform, node), Frame(_holderTransform, part)));
        // The new part takes the mount's layer and first-person surfaces with the rest of the model.
        _surfaces = null;
        _viewLayer = -1;
        RefreshView();
    }

    /// <summary>The live mount on <paramref name="holder"/>, or null: the gun whose look a package has taken over, which no other package
    /// re-dresses.</summary>
    public static GunMount? Of(GearPartHolder? holder)
    {
        if (holder == null || !ByHolder.TryGetValue(holder.Pointer, out var mount)) return null;
        return mount.Alive ? mount : null;
    }

    /// <summary>Raised with the holder when <see cref="Create"/> is about to put a model on it, before it samples the gun's rest poses and
    /// hides the game's meshes: a package that changed this gun's own parts, materials or objects gives them back here and leaves the gun
    /// alone while <see cref="Of"/> names a mount. A handler's failure is logged and does not stop the mount.</summary>
    public static event Action<GearPartHolder>? Mounting;

    /// <summary>Spawns <paramref name="prefab"/> on the assembled gun of <paramref name="holder"/> and points the game's nodes at it.
    /// Its materials must be ready (<see cref="ModelHandle.Ready"/>). Throws, with the gun as it was, when the game gun lacks a hand or bore node,
    /// a position is not a number, or the model has no mesh to measure (its hand sockets are optional: a missing left hand is derived and a missing
    /// right grip is the model's origin).</summary>
    public static GunMount Create(GearPartHolder holder, GameObject prefab, GunMountOptions options)
    {
        if (holder == null || holder.WasCollected) throw new ArgumentNullException(nameof(holder));
        if (prefab == null) throw new ArgumentNullException(nameof(prefab));
        if (options == null) throw new ArgumentNullException(nameof(options));
        try { Mounting?.Invoke(holder); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-gun-look-failed", "a package could not give a gun's look back before its model was mounted: " + error); }
        // Dead mounts give the game's nodes back before this one samples their rest poses.
        Prune();
        var bindings = Capture(holder, null);
        var nodes = GunNodes.Of(holder, bindings.Weapon);
        var right = nodes.RightHand;
        var left = nodes.LeftHand;
        var nativeMuzzle = nodes.Muzzle;
        if (right == null || left == null || nativeMuzzle == null)
            throw new System.IO.InvalidDataException("The game gun has no hand or muzzle node for " + options.Name + " (it is not assembled yet).");
        var rules = Rules;
        var layer = holder.gameObject.layer;
        if (holder.ReceiverPart != null)
        {
            var source = holder.ReceiverPart.GetComponentInChildren<MeshRenderer>(true);
            if (source != null) layer = source.gameObject.layer;
        }
        Transform? rightProxy = null, leftProxy = null;
        GunMount? mount = null;
        var shown = false;
        GameObject instance;
        if (options.Adopt) { instance = prefab; instance.transform.SetParent(holder.transform, false); }
        else instance = Object.Instantiate(prefab, holder.transform);
        try
        {
            instance.name = options.Name;
            var root = instance.transform;
            // A socket carries only a position: the bore and the sight look along the gun whichever way the export faces.
            var along = Quaternion.LookRotation(nativeMuzzle.forward, holder.transform.up);
            root.rotation = along * options.Front;
            var sockets = options.Sockets(root);
            GunGripEntry? grip = null;
            if (options.Grips != null && options.Grips.TryGet(options.Adopt ? options.Name : prefab.name, out var row))
            {
                if (!rules.Presets.ContainsKey(row.Preset)) throw new System.IO.InvalidDataException("The grip table row of " + options.Name + " names hold preset " + row.Preset + ", which ForgeVisual does not have.");
                grip = row;
                // The hands are Seat's: the model's own hand sockets stay where they are, and only its fact points move here.
                sockets.Muzzle = PlaceSocket(root, sockets.Muzzle, GunNodes.MuzzleSocket, row.Muzzle, options.Name);
                sockets.Sight = PlaceSocket(root, sockets.Sight, GunNodes.SightSocket, row.Sight, options.Name);
                sockets.ShellEject = PlaceSocket(root, sockets.ShellEject, GunNodes.ShellEjectSocket, row.ShellEject, options.Name);
                sockets.Flashlight = PlaceSocket(root, sockets.Flashlight, GunNodes.FlashlightSocket, row.Flashlight, options.Name);
            }
            GameObject? sightPart = null;
            SightKind? sightKind = null;
            if (options.Sights != null && options.Sights.TryGet(prefab.name, out var fit))
            {
                sightPart = ForgeVisual.Sights.Attach(root, sockets, fit, options.Front, options.Name, out var kind);
                if (sightPart != null) sightKind = kind;
            }
            var hold = Seat(root, holder.transform, nodes, sockets, grip, options, rules);
            if (sockets.Muzzle != null) sockets.Muzzle.rotation = along;
            if (sockets.Sight != null) sockets.Sight.rotation = along;
            // Shells and the light leave the way the game gun's own nodes point.
            if (sockets.ShellEject != null && nodes.ShellEject != null) sockets.ShellEject.rotation = nodes.ShellEject.rotation;
            if (sockets.Flashlight != null && nodes.Flashlight != null) sockets.Flashlight.rotation = nodes.Flashlight.rotation;
            rightProxy = CreateProxy(holder.transform, options.Name + ".RightHandIK");
            leftProxy = CreateProxy(holder.transform, options.Name + ".LeftHandIK");
            mount = new GunMount(holder, options, instance, rightProxy, leftProxy, sockets.Muzzle, sockets.Sight, layer, right, left)
            {
                _owner = bindings.Weapon,
                _native = bindings,
                _shell = sockets.ShellEject,
                _flashlight = sockets.Flashlight,
                _donorMagazine = nodes.Magazine,
                _hold = hold,
                _gripRest = Frame(holder.transform, right),
                _modelRest = Frame(holder.transform, root),
                _leftRest = Frame(holder.transform, left),
                _rightTargetRest = hold.Right,
                _leftTargetRest = hold.Left,
                _sightPart = sightPart != null ? sightPart.transform : null,
                _sightLookFov = sightKind != null ? GunHold.SightLookFov(sightKind.Magnification, rules) : 0,
            };
            mount._magazine = sockets.Magazine;
            if (sockets.Magazine != null && nodes.Magazine != null)
            {
                // Follow carries the magazine with the gun and adds what the game's magazine node does on its own. Hung on that node, it would
                // swing about the game's magazine, not its own, and part from a gun that moves with the right hand node.
                mount._magazineNodeRest = Frame(holder.transform, nodes.Magazine);
                mount._magazineRest = Frame(holder.transform, sockets.Magazine);
                sockets.Magazine.SetParent(holder.transform, true);
                mount._magazineObject = sockets.Magazine.gameObject;
                mount._magazineObject.SetActive(options.MagazineVisible);
            }
            mount.RefreshView();
            mount.Follow();
            mount.BindChecked(bindings);
            instance.SetActive(true);
            shown = true;
            mount.KeepDonorAnimating();
            mount.HideDonor();
            All.Add(mount);
            ByHolder[holder.Pointer] = mount;
            _heldItem = IntPtr.Zero;
            Hook();
            return mount;
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            if (bindings.WritesAttempted) RestoreBindings(holder, bindings, failures);
            if (mount != null)
            {
                if (shown) mount.ShowDonor(failures);
                if (mount._magazineObject != null) Attempt(() => Object.Destroy(mount._magazineObject), failures);
            }
            DestroyProxy(rightProxy, failures);
            DestroyProxy(leftProxy, failures);
            // Destroy is deferred in Unity: out of every lookup at once.
            Attempt(() => instance.SetActive(false), failures);
            if (!options.Adopt)
            {
                Attempt(() => instance.transform.SetParent(null, true), failures);
                Attempt(() => Object.Destroy(instance), failures);
            }
            if (failures.Count == 0) throw;
            failures.Insert(0, error);
            throw new AggregateException("The gun model mount failed and its rollback was incomplete.", failures);
        }
    }

    /// <summary>The game's calls every mount follows (<see cref="GunPartMount"/> too), installed once.</summary>
    internal static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        // The holder's LateUpdate reads both hand targets in its native body.
        ForgeCore.GameHooks.FirstPersonHolderLateUpdate.Before(BeforeHolderLateUpdate);
        ForgeCore.GameHooks.MagazineVisibility.After((call, _) => Of(call.Weapon.GearPartHolder)?.SetMagazineVisible(call.Visible));
        ForgeCore.GameHooks.GearPartsSpawned.After((parts, _) => LookAgain(parts));
        ForgeCore.GameHooks.BulletWeaponWielded.After((weapon, _) => LookAgain(weapon.GearPartHolder));
    }

    /// <summary>Looks at the gun again (call on assembly, wield, reload or every so often): a changed hand or magazine node is taken over,
    /// meshes the game added since are hidden, the view is re-read and a game node that no longer points at the model is pointed at it again.</summary>
    public void Refresh()
    {
        var holder = _holder;
        // Assembly, wield and reload are the lifecycle points; a new owner is resolved here, never in the frame loop.
        var owner = holder.GetComponentInParent<ItemEquippable>();
        if (_owner != owner) _boundFirstPerson = false;
        _owner = owner;
        var right = holder.m_aRightHand;
        var left = holder.m_aLeftHand;
        var magazine = holder.m_aMag;
        // The targets must never become inputs or be sampled as a new rest pose.
        if (IsProxy(right, _rightProxy)) right = _donorGrip;
        if (IsProxy(left, _leftProxy)) left = _donorLeft;
        // The game's LeftHandGripTrans falls back the same way when a gun has no holder left-hand node.
        if (left is null)
        {
            left = owner != null ? owner.m_leftHandGripAlign : null;
            if (IsProxy(left, _leftProxy)) left = _donorLeft;
        }
        var changed = right != _donorGrip || left != _donorLeft || magazine != _donorMagazine;
        if (right == null || left == null)
        {
            // The game clears a gun's hand nodes while it takes the gun apart (the slot's gun swapped, the gun assembled again) before the holder goes
            // and the mount with it; that is not a fault. A gun that keeps no node is reported once it has stayed so.
            var now = ForgeCore.Frame.UnscaledTime;
            if (_nodesLostSince < 0f) _nodesLostSince = now;
            if (now - _nodesLostSince < NodeLossGraceSeconds) return;
            throw new System.IO.InvalidDataException("The game gun lost a hand node of " + _options.Name + " (right " + (right == null ? "missing" : "present")
                + ", left " + (left == null ? "missing" : "present") + ", owner " + (owner == null ? "missing" : "present") + ").");
        }
        _nodesLostSince = -1f;
        if (right != _donorGrip)
        {
            var rest = Frame(_holderTransform, right);
            _modelRest = GunPose.Follow(_gripRest, rest, _modelRest);
            _rightTargetRest = GunPose.Follow(_gripRest, rest, _rightTargetRest);
            _leftTargetRest = GunPose.Follow(_gripRest, rest, _leftTargetRest);
            _magazineRest = GunPose.Follow(_gripRest, rest, _magazineRest);
            _gripRest = rest;
        }
        if (left != _donorLeft) _leftRest = Frame(_holderTransform, left);
        if (magazine != _donorMagazine && magazine != null) _magazineNodeRest = Frame(_holderTransform, magazine);
        _donorGrip = right;
        _donorLeft = left;
        _donorMagazine = magazine;
        // A game gun with no magazine node has no reload to take the model's magazine off: it stays shown and follows the hand (see Follow).
        if (_magazineObject != null) _magazineObject.SetActive(_magazineVisible || magazine == null);
        HideDonor();
        if (changed)
        {
            KeepDonorAnimating();
            Follow();
        }
        RefreshView();
        var weapon = _owner;
        var sync = WieldedSyncIK(weapon);
        // A changed gun needs a fresh snapshot of the game's nodes even when the targets survived assembly.
        if (!changed && holder.m_aRightHand == _rightProxy && holder.m_aLeftHand == _leftProxy && Points(holder.m_aMuzzle, _muzzle) && Points(holder.m_aSightLook, _sight) && Points(holder.m_aShellEject, _shell) && Points(holder.m_aFlashlight, _flashlight) &&
            (weapon == null || (weapon.m_rightHandGripAlign == _rightProxy && weapon.m_leftHandGripAlign == _leftProxy && Held(weapon) &&
                Points(weapon.MuzzleAlign, _muzzle) && Points(weapon.SightLookAlign, _sight) && (!FirstPerson || _boundFirstPerson))) &&
            (sync == null || IsProxy(sync.m_leftArmSolver.target, _leftProxy))) return;
        Bind();
    }

    /// <summary>Shows or hides the model's magazine (the game's reload visibility).</summary>
    public void SetMagazineVisible(bool visible)
    {
        if (_magazineObject == null || _magazineVisible == visible) return;
        _magazineVisible = visible;
        _magazineObject.SetActive(visible || _donorMagazine == null);
    }

    private static bool Points(Transform? node, Transform? socket) => socket == null || node == socket;

    private static ItemFPSSettingsDataBlock? Settings(ItemEquippable weapon) => weapon.m_itemFPSSettingsForGear ?? weapon.m_itemFPSSettingsForItem;

    // The item wears the rule's hold: its first-person settings are a held copy.
    private static bool Held(ItemEquippable weapon) => Settings(weapon) is { } settings && HoldCopyPointers.Contains(settings.Pointer);

    /// <summary>The item's first-person settings become a copy with the rule's hold (hip and relaxed position, hip item field of view); the game's
    /// block is shared with its own guns and is never written. Aiming keeps the game's settings: the game lines the model's sight up with the camera.</summary>
    private void ApplyHold(ItemEquippable weapon)
    {
        var current = Settings(weapon);
        if (current == null || HoldCopyPointers.Contains(current.Pointer)) return;
        if (!HoldCopies.TryGetValue((current.Pointer, _sightLookFov), out var copy))
        {
            var rules = Rules;
            copy = current.MemberwiseClone().Cast<ItemFPSSettingsDataBlock>();
            var hold = new Vector3(rules.Hold.X, rules.Hold.Y, rules.Hold.Z);
            copy.localPosHip = current.localPosHip + hold;
            copy.localPosRelaxed = current.localPosRelaxed + hold;
            copy.ItemCameraFOVDefault = GunHold.HipItemFov(current.ItemCameraFOVDefault, rules);
            if (_sightLookFov != 0) copy.LookCameraFOVZoom = _sightLookFov;
            HoldCopies[(current.Pointer, _sightLookFov)] = copy;
            HoldCopyPointers.Add(copy.Pointer);
        }
        _holdOriginal = current;
        // The native setter resolves the recoil animation again from the copy, which names the same one.
        weapon.SetItemFPSSettingsForGear(copy);
        Resettle(weapon);
    }

    /// <summary>The game reads the hip position and item field of view once, when the hip state takes the wielded item (<c>FPIS_Hip.SetupWieldedItem</c>).
    /// Settings changed while the local first-person gun already sits at the hip (a model mounted after the wield, or a mount released) would wait for the
    /// next state change, so the hip state takes the item again.</summary>
    private static void Resettle(ItemEquippable weapon)
    {
        if (!weapon.IsFirstPerson) return;
        var holder = weapon.Owner != null ? weapon.Owner.FPItemHolder : null;
        if (holder == null || holder.WieldedItem == null || holder.WieldedItem.Pointer != weapon.Pointer || holder.m_currentState != FPItemStateName.Hip) return;
        holder.m_itemStates[(int)FPItemStateName.Hip].Cast<FirstPersonItem.FPIS_Hip>().SetupWieldedItem();
    }

    /// <summary>Once per wield, after the gun has sat at the hip for <see cref="HipSettleSeconds"/>: shoulder (upper arm bone) to each hand target against
    /// the arm's reach. The left hand is pulled back along the gun into the reach (the rule's clamp; one line for the model's author) so the hand stays
    /// under the gun wherever the arm ends; the right hand is the model's anchor and is reported when it is out. While the gun is raised, relaxed,
    /// lowered or aimed the hands are wherever the game's transition has them, so those frames are not read.</summary>
    private void ReadReach(FirstPersonItemHolder holder)
    {
        if (_reachRead) return;
        var arms = holder.FPSArms;
        if (arms == null || holder.m_currentState != FPItemStateName.Hip || holder.ItemAimTrigger) { _hipSince = -1f; return; }
        var now = Time.time;
        if (_hipSince < 0f) _hipSince = now;
        if (now - _hipSince < HipSettleSeconds) return;
        var rules = Rules;
        var origin = holder.transform.position;
        // The arms and the gun stand in one rig only while each arm can reach the game's own hand node it holds. A view that moves
        // the holder away from the arms (the replay's first-person eye puts it at the recorded player's eye; on a jump's first frame,
        // or with two players close, the observer's arms are still at its body) leaves them apart: the shoulder-to-target distance
        // is then the view's, not the model's, and is read again once the arm holds its own hand node again.
        bool Together(LimbIK ik, Transform target, Transform? hand)
        {
            var shoulder = ik == null ? null : ik.solver?.bone1?.transform;
            if (shoulder == null) return false;
            var reach = hand != null ? Vector3.Distance(shoulder.position, hand.position) : Vector3.Distance(shoulder.position, origin);
            return reach <= rules.MaxReach * 1.02f && Vector3.Distance(target.position, origin) <= SameViewMetres;
        }
        if (!Together(arms.m_leftHandIK, _leftProxy, _donorLeft) || !Together(arms.m_rightHandIK, _rightProxy, _donorGrip)) { _hipSince = -1f; return; }
        _reachRead = true;
        void One(LimbIK ik, Transform target, bool pull)
        {
            if (ik == null) return;
            var shoulder = ik.solver?.bone1?.transform;
            if (shoulder == null) return;
            var distance = Vector3.Distance(shoulder.position, target.position);
            // Only an error is written; a nearly straight arm and the aim pose are read back by ForgeDevelopment's grip probe.
            if (GunHold.Reach(distance, rules) != GunReach.Out) return;
            if (pull)
            {
                // The model's left grip (or the rule's, when it has none) may stand farther from the shoulder than the arm reaches.
                // Move the target back along the bore until it is in reach, and keep it there.
                var bore = _muzzle != null ? _muzzle.forward : _model.forward;
                System.Numerics.Vector3 Numeric(Vector3 value) => new(value.x, value.y, value.z);
                var near = GunHold.IntoReach(Numeric(shoulder.position), Numeric(target.position), Numeric(bore), rules);
                var shift = new Vector3(near.X, near.Y, near.Z) - target.position;
                if (shift.sqrMagnitude > 0f)
                {
                    var local = _holderTransform.InverseTransformVector(shift);
                    _leftTargetRest = new GunFrame(_leftTargetRest.Position + new System.Numerics.Vector3(local.x, local.y, local.z), _leftTargetRest.Rotation);
                    _hold = _hold with { Left = _leftTargetRest };
                    Follow();
                    Plugin.Logger.ErrorOnce("visual-grip-pulled-into-reach", "the gun model " + _options.Name + " puts the left hand " + ForgeCore.Log.Num(distance * 100f)
                        + " cm from the shoulder at the hip; the mount pulls it back " + ForgeCore.Log.Num(shift.magnitude * 100f) + " cm along the gun to the arm's "
                        + ForgeCore.Log.Num(rules.MaxReach * 100f) + " cm (move the model's right grip back if the hand no longer meets it).");
                    return;
                }
            }
            Plugin.Logger.ErrorOnce("visual-grip-out-of-reach", "the gun model " + _options.Name + " puts the " + (pull ? "left" : "right") + " hand " + ForgeCore.Log.Num(distance * 100f)
                + " cm from the shoulder at the hip; the arm reaches " + ForgeCore.Log.Num(rules.MaxReach * 100f) + " cm. Its " + (pull ? "left" : "right") + " grip point has to come back by "
                + ForgeCore.Log.Num((distance - rules.MaxReach) * 100f) + " cm or more (the hand stays where the arm ends).");
        }
        One(arms.m_leftHandIK, _leftProxy, true);
        One(arms.m_rightHandIK, _rightProxy, false);
    }

    private void KeepDonorAnimating()
    {
        foreach (var animator in _holder.GetComponentsInChildren<Animator>(true))
        {
            if (animator.transform.IsChildOf(_model) || (_magazine != null && animator.transform.IsChildOf(_magazine))) continue;
            // Hidden meshes must still evaluate the animation that moves the hand and magazine nodes.
            if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate) _animators.Add((animator, animator.cullingMode));
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
    }

    private void HideDonor() => Models.HideRenderers(_holderTransform,
        renderer => renderer.transform.IsChildOf(_model) || (_magazine != null && renderer.transform.IsChildOf(_magazine)), _hidden);

    private void ShowDonor(List<Exception> failures)
    {
        Attempt(() => Models.ShowRenderers(_hidden), failures);
        foreach (var (animator, mode) in _animators)
            if (animator != null && !animator.WasCollected) Attempt(() => animator.cullingMode = mode, failures);
        _animators.Clear();
    }

    private void RefreshView()
    {
        var layer = FirstPerson ? FirstPersonLayer : _worldLayer;
        if (_viewLayer == layer) return;
        Models.SetLayer(_model, layer);
        _options.Viewed?.Invoke("layer=" + layer + " inHand=" + _inHand + " magazine=" + (_magazine != null));
        if (_magazine != null) Models.SetLayer(_magazine, layer);
        // A caller's own surfaces cover its model; a ForgeVisual sight on it still takes the library's.
        if (_options.ApplyView != null) _options.ApplyView(_model, _magazine, layer);
        if (_options.ApplyView == null || _sightPart != null) ApplySurfaces(layer == FirstPersonLayer);
        _viewLayer = layer;
    }

    private void ApplySurfaces(bool firstPerson)
    {
        _surfaces ??= ReadSurfaces();
        foreach (var (renderer, world, first) in _surfaces)
        {
            if (renderer == null || renderer.WasCollected) continue;
            renderer.sharedMaterials = firstPerson ? first : world;
            var shadows = !firstPerson;
            renderer.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = shadows;
        }
    }

    private (Renderer, Il2CppReferenceArray<Material>, Il2CppReferenceArray<Material>)[] ReadSurfaces()
    {
        var read = new List<(Renderer, Il2CppReferenceArray<Material>, Il2CppReferenceArray<Material>)>();
        void Add(Transform root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!Models.IsMesh(renderer)) continue;
                var world = renderer.sharedMaterials;
                var first = new Il2CppReferenceArray<Material>(world.Length);
                for (var i = 0; i < world.Length; i++) first[i] = LitMaterial.FirstPerson(world[i])!;
                read.Add((renderer, world, first));
            }
        }
        if (_options.ApplyView != null) Add(_sightPart!);
        else
        {
            Add(_model);
            if (_magazine != null) Add(_magazine);
        }
        return read.ToArray();
    }

    private void Bind()
    {
        var snapshot = Capture(_holder, this);
        try
        {
            BindChecked(snapshot);
            _native = snapshot;
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            if (snapshot.WritesAttempted) RestoreBindings(_holder, snapshot, failures);
            if (failures.Count == 0) throw;
            failures.Insert(0, error);
            throw new AggregateException("The gun model binding failed and its rollback was incomplete.", failures);
        }
    }

    private void BindChecked(Bindings snapshot)
    {
        var holder = _holder;
        var weapon = _owner;
        if (weapon != null && (snapshot.Weapon == null || weapon.Pointer != snapshot.Weapon.Pointer))
            throw new InvalidOperationException("The weapon changed during the gun model binding.");
        // The flag precedes the first write: a native setter may commit before it throws.
        snapshot.WritesAttempted = true;
        snapshot.SyncProxy = _leftProxy;
        holder.m_aRightHand = _rightProxy;
        holder.m_aLeftHand = _leftProxy;
        if (_muzzle != null) holder.m_aMuzzle = _muzzle;
        // The game aims by lining its sight node up with the camera: it must see the model's optic, not the hidden one.
        if (_sight != null) holder.m_aSightLook = _sight;
        if (_shell != null) holder.m_aShellEject = _shell;
        if (_flashlight != null) holder.m_aFlashlight = _flashlight;
        if (weapon == null) return;
        weapon.m_rightHandGripAlign = _rightProxy;
        weapon.m_leftHandGripAlign = _leftProxy;
        if (_muzzle != null) weapon.MuzzleAlign = _muzzle;
        if (_sight != null) weapon.SightLookAlign = _sight;
        ApplyHold(weapon);
        _reachRead = false;
        _hipSince = -1f;
        // The third-person solver caches its target at the wield; a model mounted later replaces that too.
        if (snapshot.SyncIK != null) snapshot.SyncIK.SetLeftArmTarget(_leftProxy);
        _options.Bound?.Invoke(weapon);
        _boundFirstPerson = FirstPerson;
    }

    /// <summary>Takes the model off and gives the gun back as it was. The library does this by itself when the gun or the model is gone.</summary>
    public void Release()
    {
        var failures = new List<Exception>();
        All.Remove(this);
        if (ByHolder.TryGetValue(_holderPointer, out var listed) && ReferenceEquals(listed, this)) ByHolder.Remove(_holderPointer);
        _heldItem = IntPtr.Zero;
        _held = null;
        if (_holder != null && !_holder.WasCollected)
        {
            var snapshot = Capture(_holder, this);
            if (snapshot.Weapon != null) Attempt(() => _options.Released?.Invoke(snapshot.Weapon.Pointer), failures);
            RestoreBindings(_holder, snapshot, failures);
            ShowDonor(failures);
        }
        else RestoreSyncTarget(_native, failures);
        DestroyProxy(_rightProxy, failures);
        DestroyProxy(_leftProxy, failures);
        if (_magazineObject != null && !_magazineObject.WasCollected) Attempt(() => Object.Destroy(_magazineObject), failures);
        if (_modelObject != null && !_modelObject.WasCollected)
        {
            Attempt(() => _modelObject.SetActive(false), failures);
            // An adopted model is its owner's to remove.
            if (!_options.Adopt)
            {
                Attempt(() => _model.SetParent(null, true), failures);
                Attempt(() => Object.Destroy(_modelObject), failures);
            }
        }
        if (failures.Count != 0) Plugin.Logger.ErrorOnce("visual-could-not-fully-take-gun-model", "ForgeVisual could not fully take the gun model " + _options.Name + " off: " + failures[0]);
    }

    private static void Prune()
    {
        for (var i = All.Count - 1; i >= 0; i--)
            if (!All[i].Alive) All[i].Release();
    }

    /// <summary>Once a frame: dead mounts go, and every model not in the local player's first-person hand follows its gun.</summary>
    internal static void TickAll()
    {
        GunPartMount.TickAll();
        if (All.Count == 0) return;
        Prune();
        for (var i = 0; i < All.Count; i++)
        {
            var mount = All[i];
            // A replaced node can be destroyed before the assembly callback refreshes this mount.
            if (!mount._modelObject.activeInHierarchy || mount._donorGrip == null || mount._donorLeft == null || (mount._owner != null && mount._owner.IsFirstPerson)) continue;
            mount.Follow();
        }
    }

    private static void LookAgain(GearPartHolder? parts)
    {
        try { Of(parts)?.Refresh(); GunPartMount.Of(parts)?.Refresh(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-gun-look-failed", "could not look at a gun model again: " + error); }
    }

    private static void BeforeHolderLateUpdate(FirstPersonItemHolder holder)
    {
        try
        {
            var item = holder.WieldedItem;
            if (item == null) { Drop(); _heldItem = IntPtr.Zero; return; }
            GunPartMount.Of(item.GearPartHolder)?.Step();
            if (item.Pointer != _heldItem)
            {
                Drop();
                _heldItem = item.Pointer;
                _held = Find(item);
            }
            if (_held == null || !_held.Alive || !_held._modelObject.activeInHierarchy || _held._donorGrip == null || _held._donorLeft == null) return;
            // The wield call can come before the game marks the item first person, and nothing looks at the gun again after it: the first frame
            // the first-person holder wields this gun, the mount takes its first-person view and binding.
            if (!_held._inHand || !_held._boundFirstPerson)
            {
                var first = !_held._inHand;
                _held._inHand = true;
                _held.Refresh();
                if (first) _held.Say("in-hand layer=" + _held._viewLayer + " bound=" + _held._boundFirstPerson + " model=" + Local(_held._holderTransform, _held._model)
                    + " rightTarget=" + Local(_held._holderTransform, _held._rightProxy) + " magazineOn=" + (_held._magazineObject != null && _held._magazineObject.activeSelf));
            }
            _held.Follow();
            _held.ReadReach(holder);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("visual-gun-hand-pose-update-failed", "ForgeVisual gun hand pose update failed: " + error); }
    }

    /// <summary>The mount of the gun the first-person holder wields. The game's gun the holder wields is not always the part holder the mount was
    /// made on (it spawns copies for the menu, the world and the hands), so a holder that is not listed is matched by its item: the mount's item,
    /// or a mount whose gun sits under it. A wielded gun with models mounted and none matched writes one line per mount.</summary>
    private static GunMount? Find(ItemEquippable item)
    {
        var parts = item.GearPartHolder;
        if (parts != null && ByHolder.TryGetValue(parts.Pointer, out var found)) { found.Say("held via=holder item=" + item.Pointer.ToString("x")); return found; }
        var itemTransform = item.transform;
        foreach (var mount in All)
        {
            if (!mount.Alive) continue;
            var viaOwner = mount._owner != null && mount._owner.Pointer == item.Pointer;
            if (!viaOwner && !mount._holderTransform.IsChildOf(itemTransform)) continue;
            mount.Say("held via=" + (viaOwner ? "owner" : "child") + " item=" + item.Pointer.ToString("x") + " itemParts=" + (parts != null ? parts.Pointer.ToString("x") : "none")
                + " mountHolder=" + mount._holderPointer.ToString("x"));
            return mount;
        }
        foreach (var mount in All)
            if (mount.Alive && mount._missItem != item.Pointer)
            {
                mount._missItem = item.Pointer;
                mount.Say("held-miss item=" + item.Pointer.ToString("x") + " itemParts=" + (parts != null ? parts.Pointer.ToString("x") : "none")
                    + " mountHolder=" + mount._holderPointer.ToString("x") + " mountOwner=" + (mount._owner != null ? mount._owner.Pointer.ToString("x") : "none"));
            }
        return null;
    }

    private void Say(string line) { try { _options.Viewed?.Invoke(line); } catch (Exception error) { Plugin.Logger.ErrorOnce("visual-gun-view-note-failed", "ForgeVisual gun view note failed: " + error.Message); } }

    private static string Local(Transform holder, Transform part)
    {
        var p = holder.InverseTransformPoint(part.position);
        return ForgeCore.Log.Pos(p.x, p.y, p.z);
    }

    /// <summary>The first-person holder no longer wields the mount it held: the model goes back to its world view.</summary>
    private static void Drop()
    {
        var held = _held;
        _held = null;
        if (held == null || !held._inHand) return;
        held._inHand = false;
        held._boundFirstPerson = false;
        if (held.Alive) held.RefreshView();
    }

    private void Follow()
    {
        var holder = _holderTransform;
        var grip = Frame(holder, _donorGrip);
        var left = Frame(holder, _donorLeft);
        // Every input is read before any output moves; the targets never feed back into the game's nodes.
        ApplyFrame(holder, _model, GunPose.Follow(_gripRest, grip, _modelRest));
        ApplyFrame(holder, _rightProxy, GunPose.Follow(_gripRest, grip, _rightTargetRest));
        // The wrist target stays attached to the gun; the game's wrist rotation must not orbit its offset.
        ApplyFrame(holder, _leftProxy, GunPose.FollowHand(_gripRest, grip, _leftRest, left, _leftTargetRest));
        // With no game magazine node the magazine is part of the gun: it moves with the hand like the model.
        if (_magazineObject is not null) ApplyFrame(holder, _magazine!, Carried(grip, _donorMagazine, _magazineNodeRest, _magazineRest));
        foreach (var (part, node, nodeRest, partRest) in _followers)
            if (part != null) ApplyFrame(holder, part, Carried(grip, node, nodeRest, partRest));
    }

    /// <summary>A part carried with the gun, plus what its game node does on its own; with no node left, carried only.</summary>
    private GunFrame Carried(in GunFrame grip, Transform? node, in GunFrame nodeRest, in GunFrame partRest)
        => node != null ? GunPose.FollowHand(_gripRest, grip, nodeRest, Frame(_holderTransform, node), partRest) : GunPose.Follow(_gripRest, grip, partRest);

    /// <summary>Reads the model's geometry (at its own size, origin on the gun's, facing the gun's +Z) and the game gun's nodes, and puts the model
    /// where <see cref="GunHold.Solve"/> says. The right grip is the model's own <c>RightHand</c> socket (its origin, the authoring grip, when it has
    /// none); a model's own <c>LeftHand</c> socket is kept, and a missing one is derived from the game's own left hand relative to its right
    /// (<see cref="GunHold.LeftGrip"/>). A grip socket turned away from the model's own axes (more than half a degree) carries a wrist rotation.</summary>
    private static GunHoldResult Seat(Transform look, Transform native, GunNodes nodes, GunSockets sockets, GunGripEntry? grip, GunMountOptions options, GunHoldRules rules)
    {
        var lossy = native.lossyScale;
        var unit = new Vector3(Models.SafeInverse(lossy.x), Models.SafeInverse(lossy.y), Models.SafeInverse(lossy.z));
        look.localScale = unit;
        look.localPosition = Vector3.zero;
        var nativeRotation = Quaternion.Inverse(native.rotation);
        System.Numerics.Vector3 Local(Transform node) { var p = native.InverseTransformPoint(node.position); return new System.Numerics.Vector3(p.x, p.y, p.z); }
        System.Numerics.Quaternion Turn(Transform node) { var q = nativeRotation * node.rotation; return new System.Numerics.Quaternion(q.x, q.y, q.z, q.w); }
        GunGrip Grip(Transform? socket)
        {
            if (socket == null) return new GunGrip(System.Numerics.Vector3.Zero);
            var own = Quaternion.Inverse(look.rotation) * socket.rotation;
            return new GunGrip(Local(socket), Quaternion.Angle(own, Quaternion.identity) > 0.5f ? Turn(socket) : null);
        }
        var measured = Models.NativeBounds(look, null, out var bounds);
        if (options.DrawnBounds is { } drawn)
        {
            if (measured) bounds.Encapsulate(drawn);
            else bounds = drawn;
            measured = true;
        }
        if (!measured)
            throw new System.IO.InvalidDataException("The gun model " + options.Name + " has no mesh to measure.");
        // The length along the gun's bore: the model's own axes are the export's, turned to the front by Front.
        var along = Quaternion.Inverse(options.Front) * Vector3.forward;
        var size = bounds.size;
        var length = Mathf.Abs(along.x) * size.x + Mathf.Abs(along.y) * size.y + Mathf.Abs(along.z) * size.z;
        var nativeRight = Local(nodes.RightHand!);
        var nativeRightRotation = Turn(nodes.RightHand!);
        var nativeLeft = Local(nodes.LeftHand!);
        var nativeLeftRotation = Turn(nodes.LeftHand!);
        var modelRight = Grip(sockets.RightHand);
        // A model that ships its own hand sockets keeps them; a missing right grip is the origin and a missing left hand the rule's.
        var modelLeft = sockets.LeftHand != null
            ? Grip(sockets.LeftHand)
            : GunHold.LeftGrip(modelRight, nativeRight, nativeRightRotation, nativeLeft, nativeLeftRotation, rules);
        var hold = GunHold.Solve(new GunHoldInput
        {
            NativeRight = nativeRight,
            NativeRightRotation = nativeRightRotation,
            NativeLeft = nativeLeft,
            NativeLeftRotation = nativeLeftRotation,
            NativeMuzzle = nodes.Muzzle != null ? Local(nodes.Muzzle) : null,
            NativeSight = nodes.Sight != null ? Local(nodes.Sight) : null,
            ModelRight = modelRight,
            ModelLeft = modelLeft,
            ModelMuzzle = sockets.Muzzle != null ? Local(sockets.Muzzle) : null,
            ModelSight = sockets.Sight != null ? Local(sockets.Sight) : null,
            ModelLength = length,
            RightAdjust = grip?.Right ?? default,
            LeftAdjust = grip?.Left ?? default,
        }, rules);
        look.localScale = unit * hold.Scale;
        look.localPosition = new Vector3(hold.Origin.X, hold.Origin.Y, hold.Origin.Z);
        if (!GunHold.LengthInRange(hold.ShownLength, rules))
            Plugin.Logger.ErrorOnce("visual-gun-size-out-of-range", "the gun model " + options.Name + " is shown " + ForgeCore.Log.Num(hold.ShownLength * 100f)
                + " cm long; the game's own guns are " + ForgeCore.Log.Num(rules.LengthMin * 100f) + "-" + ForgeCore.Log.Num(rules.LengthMax * 100f)
                + " cm. Build the model at " + ForgeCore.Log.Num(rules.LengthMin / rules.ShownScale * 100f) + "-" + ForgeCore.Log.Num(rules.LengthMax / rules.ShownScale * 100f) + " cm (it keeps its size).");
        return hold;
    }

    internal static PlayerSyncIK? WieldedSyncIK(ItemEquippable? weapon)
    {
        var owner = weapon != null ? weapon.Owner : null;
        var sync = owner != null ? owner.SyncIK : null;
        return owner != null && owner.Inventory != null && owner.Inventory.WieldedItem == weapon &&
            sync != null && sync.m_leftArmSolver != null ? sync : null;
    }

    private static bool IsProxy(Transform? node, Transform proxy) => node is not null && node.Pointer == proxy.Pointer;

    private static Bindings Capture(GearPartHolder holder, GunMount? mount)
    {
        var weapon = holder.GetComponentInParent<ItemEquippable>();
        var snapshot = new Bindings
        {
            Right = holder.m_aRightHand,
            Left = holder.m_aLeftHand,
            Muzzle = holder.m_aMuzzle,
            Sight = holder.m_aSightLook,
            Shell = holder.m_aShellEject,
            Flashlight = holder.m_aFlashlight,
            Weapon = weapon,
            WeaponRight = weapon != null ? weapon.m_rightHandGripAlign : null,
            WeaponLeft = weapon != null ? weapon.m_leftHandGripAlign : null,
            WeaponMuzzle = weapon != null ? weapon.MuzzleAlign : null,
            WeaponSight = weapon != null ? weapon.SightLookAlign : null,
        };
        snapshot.SyncIK = WieldedSyncIK(weapon);
        snapshot.Settings = weapon != null ? Settings(weapon) : null;
        // A rollback gives the item the settings it had before the hold, never a held copy.
        if (mount != null && snapshot.Settings != null && HoldCopyPointers.Contains(snapshot.Settings.Pointer)) snapshot.Settings = mount._holdOriginal;
        if (snapshot.SyncIK != null) snapshot.SyncLeftTarget = snapshot.SyncIK.m_leftArmSolver.target;
        if (mount != null)
        {
            snapshot.SyncProxy = mount._leftProxy;
            // A rollback restores the game's nodes, never the targets an earlier bind put there.
            var native = mount._native;
            if (IsProxy(snapshot.Right, mount._rightProxy)) snapshot.Right = mount._donorGrip;
            if (IsProxy(snapshot.Left, mount._leftProxy)) snapshot.Left = native.Left;
            if (mount._muzzle != null && snapshot.Muzzle == mount._muzzle) snapshot.Muzzle = native.Muzzle;
            if (mount._sight != null && snapshot.Sight == mount._sight) snapshot.Sight = native.Sight;
            if (mount._shell != null && snapshot.Shell == mount._shell) snapshot.Shell = native.Shell;
            if (mount._flashlight != null && snapshot.Flashlight == mount._flashlight) snapshot.Flashlight = native.Flashlight;
            if (IsProxy(snapshot.WeaponRight, mount._rightProxy)) snapshot.WeaponRight = native.WeaponRight;
            if (IsProxy(snapshot.WeaponLeft, mount._leftProxy)) snapshot.WeaponLeft = native.WeaponLeft;
            if (mount._muzzle != null && snapshot.WeaponMuzzle == mount._muzzle) snapshot.WeaponMuzzle = native.WeaponMuzzle;
            if (mount._sight != null && snapshot.WeaponSight == mount._sight) snapshot.WeaponSight = native.WeaponSight;
            if (IsProxy(snapshot.SyncLeftTarget, mount._leftProxy)) snapshot.SyncLeftTarget = mount._donorLeft;
        }
        return snapshot;
    }

    private static void Attempt(Action action, List<Exception> failures)
    {
        try { action(); }
        catch (Exception error) { failures.Add(error); }
    }

    // Unity's null comparison rejects destroyed wrappers: a dead game node is never put back.
    private static Transform Live(Transform? value) => value != null ? value : null!;

    private static void RestoreSyncTarget(Bindings snapshot, List<Exception> failures)
    {
        if (snapshot.SyncIK == null || snapshot.SyncProxy is null) return;
        Attempt(() =>
        {
            // A weapon switch may already have put another item's target there; only this mount's binding is undone.
            if (snapshot.SyncIK.m_leftArmSolver != null && IsProxy(snapshot.SyncIK.m_leftArmSolver.target, snapshot.SyncProxy))
                snapshot.SyncIK.SetLeftArmTarget(Live(snapshot.SyncLeftTarget));
        }, failures);
    }

    private static void RestoreBindings(GearPartHolder holder, Bindings snapshot, List<Exception> failures)
    {
        if (holder != null)
        {
            Attempt(() => holder.m_aRightHand = Live(snapshot.Right), failures);
            Attempt(() => holder.m_aLeftHand = Live(snapshot.Left), failures);
            Attempt(() => holder.m_aMuzzle = Live(snapshot.Muzzle), failures);
            Attempt(() => holder.m_aSightLook = Live(snapshot.Sight), failures);
            Attempt(() => holder.m_aShellEject = Live(snapshot.Shell), failures);
            Attempt(() => holder.m_aFlashlight = Live(snapshot.Flashlight), failures);
        }
        var weapon = snapshot.Weapon;
        if (weapon != null)
        {
            Attempt(() => weapon.m_rightHandGripAlign = Live(snapshot.WeaponRight), failures);
            Attempt(() => weapon.m_leftHandGripAlign = Live(snapshot.WeaponLeft), failures);
            Attempt(() => weapon.MuzzleAlign = Live(snapshot.WeaponMuzzle), failures);
            Attempt(() => weapon.SightLookAlign = Live(snapshot.WeaponSight), failures);
            Attempt(() =>
            {
                if (snapshot.Settings == null || Settings(weapon) is not { } held || !HoldCopyPointers.Contains(held.Pointer)) return;
                weapon.SetItemFPSSettingsForGear(snapshot.Settings);
                Resettle(weapon);
            }, failures);
        }
        RestoreSyncTarget(snapshot, failures);
    }

    internal static GunFrame Frame(Transform holder, Transform part)
    {
        var p = holder.InverseTransformPoint(part.position);
        var q = Quaternion.Inverse(holder.rotation) * part.rotation;
        return new GunFrame(new System.Numerics.Vector3(p.x, p.y, p.z), new System.Numerics.Quaternion(q.x, q.y, q.z, q.w));
    }

    internal static void ApplyFrame(Transform holder, Transform part, in GunFrame frame)
    {
        var p = frame.Position;
        var q = frame.Rotation;
        part.position = holder.TransformPoint(new Vector3(p.X, p.Y, p.Z));
        part.rotation = holder.rotation * new Quaternion(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>Puts the socket at <paramref name="point"/> of the model's own frame, making an empty one named <paramref name="socketName"/> when the
    /// model has none; a socket that is a drawn part is never moved. No point: the socket as it is.</summary>
    internal static Transform? PlaceSocket(Transform root, Transform? socket, string socketName, System.Numerics.Vector3? point, string name)
    {
        if (point is not { } p) return socket;
        if (socket == null)
        {
            socket = new GameObject(socketName).transform;
            socket.SetParent(root, false);
            socket.localRotation = Quaternion.identity;
        }
        else if (socket.GetComponent<Renderer>() != null) throw new System.IO.InvalidDataException("The grip point of " + name + " names " + socket.name + ", which is a drawn part, not a socket.");
        socket.position = root.TransformPoint(new Vector3(p.X, p.Y, p.Z));
        return socket;
    }

    private static Transform CreateProxy(Transform holder, string name)
    {
        var proxy = new GameObject(name);
        try { proxy.transform.SetParent(holder, false); return proxy.transform; }
        catch { Object.Destroy(proxy); throw; }
    }

    private static void DestroyProxy(Transform? proxy, List<Exception> failures)
    {
        if (proxy == null || proxy.WasCollected) return;
        var instance = proxy.gameObject;
        Attempt(() => instance.SetActive(false), failures);
        Attempt(() => proxy.SetParent(null, true), failures);
        Attempt(() => Object.Destroy(instance), failures);
    }
}
