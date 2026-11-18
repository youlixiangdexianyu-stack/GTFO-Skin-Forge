using System;
using System.Collections.Generic;
using Player;
using UnityEngine;

namespace ForgeVisual;

/// <summary>What <see cref="Persons.Hide(Player.PlayerAgent, string, PersonHideOptions?)"/> hides.</summary>
public sealed class PersonHideOptions
{
    /// <summary>Hide the third-person person (default true): a remote player's whole agent, the local player's third-person model (the one that draws its shadow).</summary>
    public bool ThirdPerson { get; set; } = true;

    /// <summary>Hide the first-person arms and item of the local player too (default false). Another peer's player has none.</summary>
    public bool FirstPerson { get; set; }

    /// <summary>Hide the item in the player's hands with the person (default true). False leaves the wielded item as the game draws it.</summary>
    public bool Held { get; set; } = true;

    /// <summary>Renderers this names stay as they are; asked again at every refresh, so it may follow what the caller shows on the person.</summary>
    public Func<Renderer, bool>? Keep { get; set; }

    /// <summary>The hold is a look replacing the person (the provider of <see cref="PlayerLooks"/>, default false): it leaves the models looks hung
    /// on the body (<see cref="PlayerLooks.Hang"/>) drawn, and a recording takes the look itself from <see cref="PlayerLooks.Describe"/>, so this hold
    /// is not reported to it as hidden renderers. Any other hold stands in for the whole person shown now (a suit worn over it): it hides the
    /// person and the look worn on it alike, and while it lasts no look shows, whichever came first; released, the look shows again.</summary>
    public bool Look { get; set; }
}

/// <summary>One hold on a player's person (<see cref="Persons.Hide(Player.PlayerAgent, string, PersonHideOptions?)"/>). <see cref="Release"/> (or <see cref="Dispose"/>) lets go; the person is drawn again when every holder has let go.</summary>
public sealed class PersonHide : IDisposable
{
    private const float RefreshSeconds = 0.5f;

    private readonly PlayerAgent? _agent;
    private readonly string _label;
    private readonly PersonHideOptions _options;
    private readonly List<Renderer> _held = new();
    private readonly HashSet<IntPtr> _seen = new();
    private readonly List<Transform> _items = new();
    private readonly Transform? _third, _first, _reported;
    // A hold over a player's whole person (not a look): the animator the player's looks hang on (it need not sit under the person's root), and those looks.
    private readonly IntPtr _lookBody;
    private readonly List<Transform> _looks = new();
    private float _next;
    // The person's ghost (PlayerSyncModelData.m_ghostRenderers: the copy of the person the game draws in its ghost material while
    // m_ghostEnabled is on: a teammate in the level, off in the elevator ride and for the local player), part of the person wherever it
    // hangs; and for a look, the look's own ghost and the ghost material it wears now.
    private readonly PlayerSyncModelData? _model;
    private readonly Renderer[] _ghosts = Array.Empty<Renderer>();
    private readonly List<(GameObject Node, SkinnedMeshRenderer Renderer)> _lookGhosts = new();
    private Material? _ghostMaterial;
    private bool _ghostOn, _ghostMissing;

    internal PersonHide(PlayerAgent? agent, Transform? body, string label, PersonHideOptions options)
    {
        _agent = agent;
        _label = label;
        _options = options;
        // Read once: the roots do not change while the player lives.
        var local = agent != null && agent.IsLocallyOwned;
        if (options.ThirdPerson) _third = agent == null ? body : local ? agent.PlayerSyncModel?.transform : agent.transform;
        if (options.FirstPerson && local) _first = agent!.FPItemHolder?.transform;
        if (options.ThirdPerson && !options.Look && agent != null && agent.AnimatorBody is { } animator) _lookBody = animator.Pointer;
        // Reported on the player itself when its person is hidden (the local player's third-person model too): a replay's stand-in is that
        // whole person, whatever node holds the model here.
        _reported = agent != null && _third != null ? agent.transform : _third ?? _first;
        Switched = options.ThirdPerson && agent != null && !local;
        Active = true;
        if (!options.ThirdPerson) return;
        _model = agent != null ? agent.PlayerSyncModel : body != null ? body.GetComponentInParent<PlayerSyncModelData>() : null;
        var ghosts = _model != null ? _model.m_ghostRenderers : null;
        if (ghosts == null || ghosts.Length == 0) return;
        _ghosts = new Renderer[ghosts.Length];
        for (var i = 0; i < _ghosts.Length; i++) _ghosts[i] = ghosts[i];
        if (options.Look) LookGhosts(agent != null ? agent.transform : body!, _ghosts[0] != null ? _ghosts[0].gameObject.layer : -1);
    }

    // A look hung on the body is drawn in the ghost as the person is: each of its skinned meshes gets a twin on the same bones, on the layer
    // the game draws that ghost on, shown while the game shows the person's ghost and then wearing the game's ghost material of this person
    // as it is at that moment (Dress).
    private void LookGhosts(Transform root, int layer)
    {
        foreach (var source in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (source == null || !PlayerLooks.Worn(source.transform)) continue;
            var twin = new GameObject("ForgeVisual.LookGhost");
            twin.layer = layer >= 0 ? layer : source.gameObject.layer;
            twin.transform.SetParent(source.transform, false);
            var ghost = twin.AddComponent<SkinnedMeshRenderer>();
            ghost.sharedMesh = source.sharedMesh;
            ghost.bones = source.bones;
            ghost.rootBone = source.rootBone;
            ghost.localBounds = source.localBounds;
            ghost.sharedMaterials = source.sharedMaterials; // one slot per submesh; Dress puts the ghost material in each
            ghost.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ghost.receiveShadows = false;
            ghost.enabled = false;
            _lookGhosts.Add((twin, ghost));
        }
    }

    /// <summary>Every frame: the look's ghost shows while the game shows the person's, in the person's ghost material of now.</summary>
    internal void Ghost()
    {
        if (_lookGhosts.Count == 0 || _model == null || _model.WasCollected) return;
        var on = _model.m_ghostEnabled;
        if (!on) _ghostMissing = false;
        // The game makes the ghost material anew, destroying the old one, each time it dresses the person (EquipVanityItemsFromBackpack:
        // the agent's setup and every level's generation, after the look was worn): a twin left on the destroyed one draws Unity's flat
        // missing-material colour over the look and misses the game's colour changes (downed red).
        else if (_ghostMissing) on = false;
        else if (!_ghostOn || _ghostMaterial == null) on = Dress();
        if (on == _ghostOn) return;
        _ghostOn = on;
        foreach (var (_, ghost) in _lookGhosts) if (ghost != null && !ghost.WasCollected) ghost.enabled = on;
    }

    // Puts the person's current ghost material on every twin; false (the twins stay off, said once) when the game has none.
    private bool Dress()
    {
        var material = _model!.m_ghostMaterial;
        if (material == null)
        {
            _ghostMissing = true;
            Plugin.Logger.ErrorOnce("person-ghost-material-missing", "The game shows the ghost of a person " + _label + " holds but has no ghost material for it; the look's ghost is not drawn.");
            return false;
        }
        _ghostMaterial = material;
        foreach (var (_, ghost) in _lookGhosts)
        {
            if (ghost == null || ghost.WasCollected) continue;
            var slots = ghost.sharedMaterials;
            for (var i = 0; i < slots.Length; i++) slots[i] = material;
            ghost.sharedMaterials = slots;
        }
        return true;
    }

    private void DropLookGhosts()
    {
        foreach (var (node, _) in _lookGhosts) if (node != null && !node.WasCollected) UnityEngine.Object.Destroy(node);
        _lookGhosts.Clear();
    }

    /// <summary>True until released, or until the level ended.</summary>
    public bool Active { get; private set; }

    internal PlayerAgent? Agent => _agent;
    internal PersonHideOptions Options => _options;

    /// <summary>This hold counts in the player's own visibility switch (a remote player hidden as a third-person person).</summary>
    internal bool Switched { get; }

    /// <summary>Holds the renderers that are new since the last call (a newly drawn weapon of a remote player). Nothing the game switched is switched back here.</summary>
    internal void Refresh(float now)
    {
        _next = now + RefreshSeconds;
        if (_agent is { WasCollected: true }) return;
        var count = _held.Count;
        if (_third != null && !_third.WasCollected) Take(_third, true);
        if (_lookBody != IntPtr.Zero)
        {
            PlayerLooks.HungOn(_lookBody, _looks);
            foreach (var look in _looks) Take(look, true);
        }
        if (_first != null && !_first.WasCollected) Take(_first, false);
        foreach (var ghost in _ghosts)
            if (ghost != null && !ghost.WasCollected && _seen.Add(ghost.Pointer)) { HeldOff.Hold(ghost); _held.Add(ghost); }
        if (_held.Count > count && !_options.Look && ForgeCore.Recording.Watched)
            ForgeCore.Recording.Model(new ForgeCore.ShownModel(ForgeCore.ShownModelKind.Hidden, 0, null, _reported, _held));
    }

    internal bool Due(float now) => now >= _next;

    // The person's renderers, without what it holds (Held off): every item under the person, the one in its hands and those put away (an
    // inactive weapon is still an item: found by its component, not by an active-only parent search).
    private void Take(Transform root, bool person)
    {
        _items.Clear();
        if (!_options.Held && person)
            foreach (var item in root.GetComponentsInChildren<ItemEquippable>(true)) if (item != null) _items.Add(item.transform);
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || !_seen.Add(renderer.Pointer)) continue;
            var place = renderer.transform;
            if (Holds(place) || _options.Keep != null && _options.Keep(renderer) || _options.Look && PlayerLooks.Worn(place)) { _seen.Remove(renderer.Pointer); continue; }
            HeldOff.Hold(renderer);
            _held.Add(renderer);
        }
    }

    private bool Holds(Transform place)
    {
        foreach (var item in _items) if (place.IsChildOf(item)) return true;
        return false;
    }

    /// <summary>Lets go of the person; drawn again when no other holder has it. Releasing twice does nothing.</summary>
    public void Release()
    {
        if (!Active) return;
        Active = false;
        Persons.Remove(this);
        if (_held.Count > 0 && !_options.Look && ForgeCore.Recording.Watched) ForgeCore.Recording.Model(new ForgeCore.ShownModel(ForgeCore.ShownModelKind.Shown, 0, null, null, _held));
        HeldOff.ReleaseAll(_held);
        _seen.Clear();
        DropLookGhosts();
    }

    public void Dispose() => Release();

    internal string Label => _label;

    /// <summary>The level ended or a checkpoint reloaded: the registry let go of the renderers already (<see cref="HeldOff.Forget"/>).</summary>
    internal void Forget()
    {
        Active = false;
        _held.Clear();
        _seen.Clear();
        DropLookGhosts();
    }
}

/// <summary>Hiding a player's original person (the model other players see, or the local player's shadow), for every mod together, and the one
/// rule for which body model a player shows: a hold that is not a look (<see cref="PersonHideOptions.Look"/>, a suit worn over the person) above
/// a look's model (<see cref="PlayerLooks.Hang"/>) above the game's person. Several holders of one player do not fight: the person comes back
/// when the last of them lets go, and only the renderers this library turned off are turned on.</summary>
public static class Persons
{
    private static readonly List<PersonHide> Holds = new();
    // The remote players whose own visibility switch (PlayerSyncModelData.SetAllVisible) is held off, and how many holds ask for it.
    private static readonly Dictionary<IntPtr, (PlayerAgent Agent, int Count)> Switched = new();

    /// <summary>Stops drawing <paramref name="agent"/>'s person until the returned hold is released. A remote player: the game's own switch
    /// (<c>PlayerSyncModelData.SetAllVisible</c>), then <c>forceRenderingOff</c> on every renderer of its agent, which the game never writes, so a
    /// refresh by the game cannot bring the person back; renderers the game makes later (a newly drawn weapon) are hidden within half a second. The local player: only its
    /// third-person model unless <see cref="PersonHideOptions.FirstPerson"/> is set. <paramref name="label"/> names the holder in the log.</summary>
    public static PersonHide Hide(PlayerAgent agent, string label, PersonHideOptions? options = null)
    {
        if (agent == null || agent.WasCollected) throw new ArgumentNullException(nameof(agent));
        return Add(new PersonHide(agent, null, Labelled(label), options ?? new PersonHideOptions()));
    }

    /// <summary>Stops drawing the person of a player body that has no agent (the lobby's figure, a replay's stand-in): every renderer under
    /// <paramref name="body"/> (its animator) the same way as an agent's, what it holds left drawn unless <see cref="PersonHideOptions.Held"/>,
    /// renderers it gets later taken within half a second. <see cref="PersonHideOptions.FirstPerson"/> does not apply.</summary>
    public static PersonHide Hide(Animator body, string label, PersonHideOptions? options = null)
    {
        if (body == null || body.WasCollected) throw new ArgumentNullException(nameof(body));
        return Add(new PersonHide(null, body.transform, Labelled(label), options ?? new PersonHideOptions()));
    }

    private static string Labelled(string label) => string.IsNullOrEmpty(label) ? throw new ArgumentException("A label is required.", nameof(label)) : label;

    private static PersonHide Add(PersonHide hold)
    {
        var agent = hold.Agent;
        var label = hold.Label;
        Holds.Add(hold);
        try
        {
            if (hold.Switched) Switch(agent!, +1);
            hold.Refresh(Scene.Now);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("person-hide-failed", "Hiding a person for " + label + " failed: " + error); }
        return hold;
    }

    private static void Switch(PlayerAgent agent, int by)
    {
        var key = agent.Pointer;
        Switched.TryGetValue(key, out var state);
        var count = state.Count + by;
        var model = agent.WasCollected ? null : agent.PlayerSyncModel;
        if (count <= 0) { Switched.Remove(key); model?.SetAllVisible(true); }
        else
        {
            Switched[key] = (agent, count);
            if (state.Count == 0) model?.SetAllVisible(false);
        }
    }

    internal static void Remove(PersonHide hold)
    {
        Holds.Remove(hold);
        try { if (hold.Switched) Switch(hold.Agent!, -1); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("person-show-failed", "Showing a person for " + hold.Label + " failed: " + error); }
    }

    /// <summary>Every frame, from <see cref="Scene.Tick"/>: each hold takes the renderers that are new on its person every half second.</summary>
    internal static void Tick(float now)
    {
        for (var i = Holds.Count - 1; i >= 0; i--)
        {
            var hold = Holds[i];
            try { hold.Ghost(); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("person-ghost-failed", "Showing a look's ghost for " + hold.Label + " failed: " + error); }
            if (!hold.Due(now)) continue;
            try { hold.Refresh(now); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("person-hide-failed", "Hiding a person for " + hold.Label + " failed: " + error); }
        }
    }

    /// <summary>The level ended or a checkpoint reloaded: the players are gone or new, so every hold ends here (their renderers were given back by <see cref="HeldOff.Forget"/>).</summary>
    internal static void Clear()
    {
        foreach (var hold in Holds) hold.Forget();
        Holds.Clear();
        foreach (var (agent, _) in Switched.Values)
            if (agent != null && !agent.WasCollected && agent.PlayerSyncModel != null) agent.PlayerSyncModel.SetAllVisible(true);
        Switched.Clear();
    }
}
