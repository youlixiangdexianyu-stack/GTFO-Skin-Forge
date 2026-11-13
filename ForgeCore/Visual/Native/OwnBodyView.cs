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
    // The game's effect meshes on the rig's bones (FX_NanoSwarm_Looping_Head_01 and so on, a cloud of black shards round each body part): not body, the game alone decides when they show.
    private const string RigEffect = "FX_";

    private readonly List<(Renderer Renderer, ShadowCastingMode Mode, int Layer, bool Off, bool Enabled)> _drawn = new();
    private readonly List<(GameObject Part, bool Active)> _parts = new();
    private readonly List<Renderer> _first = new();
    private bool _showing;

    internal OwnBodyView(PlayerAgent agent, OwnBodyOptions options)
    {
        var model = agent.PlayerSyncModel;
        var mask = options.Camera != null && !options.Camera.WasCollected ? options.Camera.cullingMask : ~0;
        if (model != null)
        {
            var head = new HashSet<IntPtr>();
            if (model.m_gfxHead != null)
                foreach (var part in model.m_gfxHead)
                {
                    if (part == null) continue;
                    if (options.KeepHead) foreach (var r in part.GetComponentsInChildren<Renderer>(true)) head.Add(r.Pointer);
                    else if (options.HeadAndArms) Part(part);
                }
            if (options.HeadAndArms && model.m_gfxArms != null)
                foreach (var part in model.m_gfxArms)
                    if (part != null) Part(part);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (head.Contains(r.Pointer) || Skipped(r.name, options.Skip) || r.name.StartsWith(RigEffect, StringComparison.Ordinal)) continue;
                _drawn.Add((r, r.shadowCastingMode, r.gameObject.layer, r.forceRenderingOff, r.enabled));
                r.shadowCastingMode = ShadowCastingMode.On;
                r.forceRenderingOff = false;
                r.enabled = true;
                if ((mask & (1 << r.gameObject.layer)) == 0) r.gameObject.layer = 0;
            }
        }
        HeldOff.HoldAll(agent.FPItemHolder, _first);
        // A view from inside the body (BodyClip, no head and arms) keeps the game's scale: its framing is made for that.
        if (options.HeadAndArms && agent.AnimatorBody != null)
        {
            _body = agent.AnimatorBody.transform;
            _bodyScale = _body.localScale;
            _body.localScale = Vector3.one;
        }
        _showing = true;
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
        foreach (var (r, mode, layer, off, enabled) in _drawn)
        {
            if (r == null || r.WasCollected) continue;
            r.shadowCastingMode = mode;
            r.gameObject.layer = layer;
            r.forceRenderingOff = off;
            r.enabled = enabled;
        }
        foreach (var (part, active) in _parts) if (part != null && !part.WasCollected) part.SetActive(active);
        HeldOff.ReleaseAll(_first);
    }
}
