using System;
using System.Collections.Generic;
using Player;
using UnityEngine;
using UnityEngine.Rendering;

namespace ForgeVisual;

/// <summary>What <see cref="Models.ShowOwnBody"/> shows.</summary>
public sealed class OwnBodyOptions
{
    /// <summary>Draw the head and arms too (default true): the game turns them off on the local body, so seen from behind it has neither.</summary>
    public bool HeadAndArms { get; set; } = true;

    /// <summary>The camera that must see the body; its renderers on a layer it does not draw move to the default layer meanwhile. Default: every layer stays.</summary>
    public Camera? Camera { get; set; }

    /// <summary>Renderers whose name contains any of these (ignoring case) stay as they are.</summary>
    public IReadOnlyList<string>? Skip { get; set; }

    /// <summary>Also leave the head as it is, for a view from inside the body.</summary>
    public bool KeepHead { get; set; }
}

/// <summary>The local player's third-person body drawn in the view (seen from outside at the size other players' bodies have), and the first-person arms and item hidden, until <see cref="Dispose"/> puts everything back as it was.</summary>
public sealed class OwnBodyView : IDisposable
{
    // The local body's animator object is at scale 1.2 and the same body of a bot at 1 (upper arm 39.6 against 33 cm, forearm 34.8 against 29, read in the game): normally only its shadow shows, so nobody sees it. Drawn beside other players it would stand a fifth taller than they do.
    private readonly Transform? _body;
    private readonly Vector3 _bodyScale;
    // The local body's animator moves its own object by the clips' root motion: the game turns that off only on the bodies it places from the
    // network (PlayerLocomotion.UpdateMovementFromSync), and nothing in it handles OnAnimatorMove. So the local body's object drifts from the
    // player's feet with crouch and jump clips (down while crouched, up after a jump), which only its shadow ever showed. While the body is
    // shown it stands on the player's feet with root motion off, as other players' bodies do; both come back as they were.
    private readonly Animator? _animator;
    private readonly Transform? _root;
    private readonly Vector3 _rootAt;
    private readonly bool _rootMotion;
    // The game's effect meshes on the rig's bones (FX_NanoSwarm_Looping_Head_01 and so on, a cloud of black shards round each body part): not body, the game alone decides when they show.
    private const string RigEffect = "FX_";

    private readonly List<(Renderer Renderer, ShadowCastingMode Mode, int Layer, bool Off, bool Enabled)> _drawn = new();
    private readonly List<(GameObject Part, bool Active)> _parts = new();
    private readonly List<Renderer> _first = new();
    private bool _showing, _whole;

    internal OwnBodyView(PlayerAgent agent, OwnBodyOptions options)
    {
        var model = agent.PlayerSyncModel;
        var mask = options.Camera != null && !options.Camera.WasCollected ? options.Camera.cullingMask : ~0;
        if (model != null)
        {
            var head = new HashSet<IntPtr>();
            var heads = model.m_gfxHead;
            if (heads != null)
                foreach (var part in heads)
                {
                    if (part == null) continue;
                    if (options.KeepHead) foreach (var r in part.GetComponentsInChildren<Renderer>(true)) head.Add(r.Pointer);
                    else if (options.HeadAndArms) Part(part);
                }
            var arms = options.HeadAndArms ? model.m_gfxArms : null;
            if (arms != null)
                foreach (var part in arms)
                    if (part != null) Part(part);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                if (!head.Contains(r.Pointer)) Draw(r, options, mask);
        }
        var animator = agent.AnimatorBody;
        var body = animator != null ? animator.transform : null;
        // A look hung on the body (PlayerLooks.Hang) is drawn with it, wherever the animator sits against the model.
        if (body != null && (model == null || !body.IsChildOf(model.transform)))
            foreach (var r in animator!.GetComponentsInChildren<Renderer>(true))
                if (PlayerLooks.Worn(r.transform)) Draw(r, options, mask);
        if (body != null)
        {
            _animator = animator;
            _root = body;
            _rootAt = body.localPosition;
            _rootMotion = animator!.applyRootMotion;
            animator.applyRootMotion = false;
            var at = body.position;
            at.y = agent.Position.y;
            body.position = at;
        }
        HeldOff.HoldAll(agent.FPItemHolder, _first);
        // A look cut for first person is whole again, as the game's head and arm parts are.
        _whole = options.HeadAndArms;
        if (_whole) OwnBodyCut.ShowWhole(true);
        // A view from inside the body (BodyClip, no head and arms) keeps the game's scale: its framing is made for that.
        if (options.HeadAndArms && body != null)
        {
            _body = body;
            _bodyScale = _body.localScale;
            _body.localScale = new Vector3(1f, 1f, 1f);
        }
        _showing = true;
    }

    // Drawn and casting a shadow. Its "off" is set under its holders (HeldOff): a person another mod hides (Persons.Hide, a look replacing it)
    // stays hidden, and what comes back at Dispose is what the renderer had apart from its holders.
    private void Draw(Renderer r, OwnBodyOptions options, int mask)
    {
        var name = r.name;
        if (Skipped(name, options.Skip) || name.StartsWith(RigEffect, StringComparison.Ordinal)) return;
        var holder = r.gameObject;
        var layer = holder.layer;
        _drawn.Add((r, r.shadowCastingMode, layer, HeldOff.IsOff(r), r.enabled));
        r.shadowCastingMode = ShadowCastingMode.On;
        HeldOff.SetUnder(r, false);
        r.enabled = true;
        if ((mask & (1 << layer)) == 0) holder.layer = 0;
    }

    private void Part(GameObject part)
    {
        _parts.Add((part, part.activeSelf));
        part.SetActive(true);
    }

    private static bool Skipped(string name, IReadOnlyList<string>? skip)
    {
        if (skip == null) return false;
        foreach (var piece in skip) if (name.IndexOf(piece, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>Everything goes back as it was.</summary>
    public void Dispose()
    {
        if (!_showing) return;
        _showing = false;
        if (_body != null && !_body.WasCollected) _body.localScale = _bodyScale;
        if (_root != null && !_root.WasCollected) _root.localPosition = _rootAt;
        if (_animator != null && !_animator.WasCollected) _animator.applyRootMotion = _rootMotion;
        foreach (var (r, mode, layer, off, enabled) in _drawn)
        {
            if (r == null || r.WasCollected) continue;
            r.shadowCastingMode = mode;
            r.gameObject.layer = layer;
            HeldOff.SetUnder(r, off);
            r.enabled = enabled;
        }
        foreach (var (part, active) in _parts) if (part != null && !part.WasCollected) part.SetActive(active);
        HeldOff.ReleaseAll(_first);
        if (_whole) OwnBodyCut.ShowWhole(false);
    }
}
