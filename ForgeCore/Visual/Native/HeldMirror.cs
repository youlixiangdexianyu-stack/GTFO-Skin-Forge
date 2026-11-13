using System;
using System.Collections.Generic;
using Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeVisual;

/// <summary>
/// A copy of whatever a player holds, for another model's hand (<see cref="Models.MirrorHeld"/>): the item's mesh pieces (its shared
/// meshes, materials and per-renderer properties, a skinned piece baked once; nothing of the game's item), laid out as on the item and
/// scaled to the model. <see cref="Refresh"/> reads what the player wields (any slot but a carried heavy object), a few times a second;
/// one copy is made per item (a gear item once its parts are built) and the last four are kept. A gun with a <see cref="GunMount"/> is copied as its
/// model, without the game's hidden meshes (made again when a mount comes or goes after the copy), and every copy draws world surfaces, never the item camera's. <see cref="Place"/> puts the current
/// copy where the game holds the item against the body's hand, carried over to the model's hand: another peer's player by its
/// third-person item, this peer's own player by its first-person item at the third-person body's <c>ItemAlign</c>. An item that
/// cannot be copied is one error line and the hand holds nothing for it.
/// </summary>
public sealed class HeldMirror
{
    private const int Keep = 4;

    private readonly PlayerAgent _agent;
    private readonly bool _local;
    private readonly int _layer;
    private readonly string _label;
    private readonly List<HeldCopy> _copies = new();
    private HeldCopy? _current;
    private Transform? _source;
    private bool _shadowOnly;
    private float _scale = 1f;

    internal HeldMirror(PlayerAgent agent, int layer, string label)
    {
        _agent = agent;
        _local = BodyConvert.IsLocal(agent);
        _layer = layer;
        _label = label;
    }

    /// <summary>Whether a copy is showing now.</summary>
    public bool Shown => _current != null && _current.Shown;

    /// <summary>Re-reads what the player wields; makes its copy the first time (or, for a gear item still being built, at a later call).</summary>
    public void Refresh()
    {
        var inventory = _agent != null && !_agent.WasCollected ? _agent.Inventory : null;
        var slot = inventory != null ? inventory.WieldedSlot : InventorySlot.None;
        var item = slot is InventorySlot.None or InventorySlot.InLevelCarry || inventory == null ? null : inventory.WieldedItem;
        if (item != null && item.WasCollected) item = null;
        Select(item);
        if (item == null) { _source = null; return; }
        var model = _local ? _agent!.PlayerSyncModel : null;
        var source = _local ? model != null ? model.ItemAlign : null : item.transform;
        if (source == null) Plugin.Logger.ErrorOnce("visual-held-copy-players-third-person-body", "ForgeVisual held copy " + _label + ": the player's third-person body has no item align; the hand holds nothing.");
        _source = source;
    }

    private void Select(ItemEquippable? item)
    {
        if (_current != null && (item == null || _current.SourceId != item.GetInstanceID())) { _current.Show(false); _current = null; }
        if (item == null) return;
        var mount = GunMount.Of(item.GearPartHolder);
        for (var i = _copies.Count - 1; i >= 0; i--)
        {
            if (_copies[i].SourceId != item.GetInstanceID() || ReferenceEquals(_copies[i].Mount, mount)) continue;
            if (_current == _copies[i]) _current = null;
            _copies[i].Destroy();
            _copies.RemoveAt(i);
        }
        if (_current != null) return;
        for (var i = 0; i < _copies.Count; i++)
            if (_copies[i].SourceId == item.GetInstanceID()) { Use(_copies[i]); return; }
        var made = HeldCopy.Build(item, mount, _layer, !_local, _label);
        // Null: the item's parts are still being built; asked again at the next refresh.
        if (made == null) return;
        if (_copies.Count >= Keep) { _copies[0].Destroy(); _copies.RemoveAt(0); }
        _copies.Add(made);
        Use(made);
    }

    private void Use(HeldCopy copy)
    {
        if (copy.Failed) return;
        _current = copy;
        copy.ShadowOnly(_shadowOnly);
        copy.Resize(_scale);
    }

    /// <summary>Shows the current copy where the game holds the item near <paramref name="bodyHand"/>, carried over to
    /// <paramref name="modelHand"/> by the body-to-model <paramref name="turn"/>, its offset from the hand and its size times
    /// <paramref name="scale"/> (a <see cref="PoseCopy"/> gives <see cref="PoseCopy.Turn"/> and <see cref="PoseCopy.Scale"/>).</summary>
    public void Place(Quaternion turn, Transform bodyHand, Transform modelHand, float scale)
    {
        if (_current == null) return;
        if (_source == null || _source.WasCollected) { _current.Show(false); return; }
        if (scale != _scale) { _scale = scale; _current.Resize(scale); }
        _current.Place(turn, _source, bodyHand.position, modelHand.position, scale);
        _current.Show(true);
    }

    /// <summary>Hides the copy (the model stopped following the body).</summary>
    public void Hide() => _current?.Show(false);

    /// <summary>Draws the copies as shadows only (a model the local player is inside), or whole.</summary>
    public void ShadowOnly(bool on)
    {
        _shadowOnly = on;
        _current?.ShadowOnly(on);
    }

    /// <summary>The held item's muzzle (its <c>MuzzleAlign</c>, else half a metre ahead), while a copy shows.</summary>
    public bool TryMuzzle(out Vector3 muzzle)
    {
        muzzle = default;
        if (_current == null || !_current.Shown) return false;
        muzzle = _current.Muzzle;
        return true;
    }

    /// <summary>Destroys every copy.</summary>
    public void Destroy()
    {
        for (var i = 0; i < _copies.Count; i++) _copies[i].Destroy();
        _copies.Clear();
        _current = null;
        _source = null;
    }
}

/// <summary>One copy of one item (<see cref="HeldMirror"/>).</summary>
internal sealed class HeldCopy
{
    private readonly GameObject? _root;
    private readonly Renderer[] _renderers;
    private readonly Mesh[] _baked;
    private readonly Vector3 _muzzle;
    private readonly float _itemScale;
    private bool _shadowOnly;

    private HeldCopy(int sourceId, GunMount? mount, GameObject? root, Renderer[] renderers, Mesh[] baked, Vector3 muzzle, float itemScale)
    {
        SourceId = sourceId;
        Mount = mount;
        _root = root;
        _renderers = renderers;
        _baked = baked;
        _muzzle = muzzle;
        _itemScale = itemScale;
    }

    /// <summary>The item's Unity instance id: unlike its native pointer it is never given to a later object, so a copy is never taken for another item's.</summary>
    internal int SourceId { get; }
    /// <summary>The gun model this copy was made of, if any.</summary>
    internal GunMount? Mount { get; }
    internal bool Failed => _root == null;
    internal bool Shown => _root != null && !_root.WasCollected && _root.activeSelf;
    internal Vector3 Muzzle => _root!.transform.TransformPoint(_muzzle);

    /// <summary>The hidden copy of <paramref name="item"/>; a failed copy (logged) when the item has no mesh or copying fails, or null
    /// while its gear parts are still being built. <paramref name="thirdPerson"/>: the item is the game's third-person item (its own
    /// scale is kept).</summary>
    internal static HeldCopy? Build(ItemEquippable item, GunMount? mount, int layer, bool thirdPerson, string label)
    {
        var name = item.name;
        GameObject? root = null;
        try
        {
            if (item.GearPartHolder != null && !item.m_gearSpawnComplete) return null;
            var source = item.transform;
            var inverse = source.worldToLocalMatrix;
            var pieces = new List<(Renderer Renderer, SkinnedMeshRenderer? Skinned, Mesh? Mesh)>();
            foreach (var renderer in item.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || (mount != null && mount.IsHiddenPart(renderer.transform))) continue;
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                if (skinned != null) { if (skinned.sharedMesh != null) pieces.Add((renderer, skinned, null)); continue; }
                if (renderer.TryCast<MeshRenderer>() == null) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) pieces.Add((renderer, null, filter.sharedMesh));
            }
            if (pieces.Count == 0)
            {
                Plugin.Logger.ErrorOnce("visual-held-copy-item-has-no-mesh", "ForgeVisual held copy " + label + ": the item " + name + " has no mesh to copy; the hand holds nothing.");
                return Failure(item, mount);
            }
            var renderers = new List<Renderer>();
            var baked = new List<Mesh>();
            root = new GameObject("ForgeVisual.HeldItem");
            root.layer = layer;
            root.SetActive(false);
            var block = new MaterialPropertyBlock();
            foreach (var (renderer, skinned, shared) in pieces)
            {
                var mesh = shared;
                if (skinned != null)
                {
                    mesh = new Mesh();
                    skinned.BakeMesh(mesh);
                    baked.Add(mesh);
                }
                var m = inverse * renderer.transform.localToWorldMatrix;
                var part = new GameObject(renderer.gameObject.name);
                part.layer = layer;
                var t = part.transform;
                t.SetParent(root.transform, false);
                t.localPosition = m.GetColumn(3);
                Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
                t.localRotation = Quaternion.LookRotation(z, y);
                // A baked mesh already carries its renderer's scale.
                t.localScale = skinned != null ? Vector3.one : new Vector3(x.magnitude, y.magnitude, z.magnitude);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var copy = part.AddComponent<MeshRenderer>();
                var surfaces = renderer.sharedMaterials;
                for (var i = 0; i < surfaces.Length; i++)
                    if (surfaces[i] != null) surfaces[i] = LitMaterial.ForWorld(surfaces[i]);
                copy.sharedMaterials = surfaces;
                renderer.GetPropertyBlock(block);
                copy.SetPropertyBlock(block);
                renderers.Add(copy);
            }
            var muzzleAlign = item.MuzzleAlign;
            var muzzle = muzzleAlign != null && !muzzleAlign.WasCollected ? source.InverseTransformPoint(muzzleAlign.position) : Vector3.forward * 0.5f;
            return new HeldCopy(item.GetInstanceID(), mount, root, renderers.ToArray(), baked.ToArray(), muzzle, thirdPerson ? source.lossyScale.x : 1f);
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("visual-held-copy-copying-item-failed-hand", "ForgeVisual held copy " + label + ": copying the item " + name + " failed; the hand holds nothing: " + error);
            if (root != null && !root.WasCollected) Object.Destroy(root);
            return Failure(item, mount);
        }
    }

    private static HeldCopy Failure(ItemEquippable item, GunMount? mount) => new(item.GetInstanceID(), mount, null, Array.Empty<Renderer>(), Array.Empty<Mesh>(), Vector3.zero, 1f);

    internal void Resize(float scale)
    {
        if (_root != null && !_root.WasCollected) _root.transform.localScale = Vector3.one * (_itemScale * scale);
    }

    internal void Place(Quaternion turn, Transform source, Vector3 bodyHand, Vector3 modelHand, float scale)
    {
        if (_root == null || _root.WasCollected) return;
        var at = HeldPlacement.Position(BodyConvert.N(turn), BodyConvert.N(source.position), BodyConvert.N(bodyHand), BodyConvert.N(modelHand), scale);
        _root.transform.SetPositionAndRotation(BodyConvert.U(at), turn * source.rotation);
    }

    internal void Show(bool on)
    {
        if (_root != null && !_root.WasCollected && _root.activeSelf != on) _root.SetActive(on);
    }

    internal void ShadowOnly(bool on)
    {
        if (on == _shadowOnly) return;
        _shadowOnly = on;
        for (var i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && !_renderers[i].WasCollected) SurfaceLook.SetShadowOnly(_renderers[i], on);
    }

    internal void Destroy()
    {
        if (_root != null && !_root.WasCollected) Object.Destroy(_root);
        for (var i = 0; i < _baked.Length; i++)
            if (_baked[i] != null && !_baked[i].WasCollected) Object.Destroy(_baked[i]);
    }
}
