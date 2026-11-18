using System;
using System.Collections.Generic;
using SNetwork;
using UnityEngine;

namespace ForgeVisual;

/// <summary>A look worn on a body. <see cref="Release"/> takes it off and gives the body back to the game; safe to call twice.</summary>
public interface ILookDress
{
    /// <summary>The body now holds <paramref name="item"/> (assembled by the caller, in its hands): it wears what the look says that gear wears
    /// (a weapon skin), or stays as the game built it.</summary>
    void Hold(ItemEquippable item);

    void Release();
}

/// <summary>Whoever decides how players look (a character-model mod, weapon skins). Both calls may answer null: not a look this provider knows.</summary>
public interface IPlayerLookProvider
{
    /// <summary>What <paramref name="player"/> looks like now (their body and their gear's skins), or null when the game's own look shows. Asked
    /// of the player, not of a body: a recorder asks while the level is still being built.</summary>
    byte[]? Describe(SNet_Player player);

    /// <summary>Wears the look <paramref name="description"/> on <paramref name="body"/> (a humanoid animator on a real player or on a replay stand-in);
    /// the body's pose comes from the caller's animator, the provider's model follows it (hung on the body through <see cref="PlayerLooks.Hang"/>)
    /// and hides the body's own person through <see cref="Persons.Hide(Animator, string, PersonHideOptions?)"/> (what the body holds stays drawn). A look that leaves
    /// the body as the game draws it (weapon skins only) still answers a dress, for <see cref="ILookDress.Hold"/>; null while its parts are still loading (asked again).</summary>
    ILookDress? Dress(byte[] description, Animator body);
}

/// <summary>The handle <see cref="PlayerLooks.Provide"/> returns: <see cref="Dispose"/> withdraws the provider.</summary>
public sealed class PlayerLookProvision : IDisposable
{
    internal IPlayerLookProvider Provider { get; }
    internal string Id { get; }

    internal PlayerLookProvision(string id, IPlayerLookProvider provider) { Id = id; Provider = provider; }

    /// <summary>Tells everyone watching <see cref="PlayerLooks.Changed"/> that <paramref name="player"/> looks different now. Ignored after <see cref="Dispose"/>.</summary>
    public void Changed(SNet_Player player)
    {
        if (PlayerLooks.Current == this) PlayerLooks.Raise(player);
    }

    public void Dispose() => PlayerLooks.Withdraw(this);
}

/// <summary>The one place that answers "what does this player look like" and "wear this look on this body". Users (a replay, an emote camera) ask here and never
/// know the provider; with no provider, or one that does not know the look, they get null and draw the game's own body, silently.</summary>
public static class PlayerLooks
{
    private static readonly LookRouter<PlayerLookProvision> Router = new();

    internal static PlayerLookProvision? Current => Router.Provider;

    /// <summary>A player's look changed (the provider says so).</summary>
    public static event Action<SNet_Player>? Changed;

    /// <summary>Registers the provider for <paramref name="id"/> (the mod's id). Only one at a time: a second is refused (null) with one log line.</summary>
    public static PlayerLookProvision? Provide(string id, IPlayerLookProvider provider)
    {
        var provision = new PlayerLookProvision(id, provider);
        if (Router.Provide(id, provision)) return provision;
        Plugin.Logger.ErrorOnce("visual-player-look-provider-taken", "ForgeVisual player looks: " + id + " is refused, " + Router.Id + " already provides them.");
        return null;
    }

    /// <summary>The provider's description of <paramref name="player"/>, or null (no provider, or the game's own look shows).</summary>
    public static PlayerLook? Describe(SNet_Player player)
    {
        var provision = Current;
        if (provision == null || player == null) return null;
        try { return provision.Provider.Describe(player) is { } data ? new PlayerLook(provision.Id, data) : null; }
        catch (Exception e) { Plugin.Logger.ErrorOnce("visual-player-look-describe", "ForgeVisual player looks: " + provision.Id + " failed to describe a player: " + e.Message); return null; }
    }

    /// <summary>Wears <paramref name="look"/> on <paramref name="body"/>, or null: no provider, a look from another provider, one it cannot read, or
    /// one whose parts the provider is still loading (it started; ask again a little later).</summary>
    public static ILookDress? Dress(PlayerLook look, Animator body)
    {
        if (Router.For(look) is not { } provision || body == null) return null;
        try { return provision.Provider.Dress(look.Data, body); }
        catch (Exception e) { Plugin.Logger.ErrorOnce("visual-player-look-dress", "ForgeVisual player looks: " + provision.Id + " failed to wear a look: " + e.Message); return null; }
    }

    // The models looks hang on bodies, by their root: not part of the game's body, so the look's own hold keeps them and re-dressing the body leaves them on.
    private static readonly Dictionary<IntPtr, (Transform Root, IntPtr Body)> Hung = new();

    /// <summary>Hangs a look's <paramref name="model"/> on <paramref name="body"/> (under the animator's object, keeping its local pose), so it moves,
    /// shows and goes with the body. What hangs this way is not the game's body: the look's own hold (<see cref="PersonHideOptions.Look"/>) leaves it drawn
    /// while any other <see cref="Persons.Hide(Player.PlayerAgent, string, PersonHideOptions?)"/> (a suit worn over the person) hides it with the person, <see cref="Models.ShowOwnBody"/>
    /// draws it with the body, and a body dressed again in the game's clothes (a replay stand-in) keeps it (<see cref="Worn"/>).</summary>
    public static void Hang(GameObject model, Animator body)
    {
        if (model == null || body == null) throw new ArgumentNullException(model == null ? nameof(model) : nameof(body));
        if (Hung.Count > 0)
        {
            Gone.Clear();
            foreach (var (key, hung) in Hung) if (hung.Root == null || hung.Root.WasCollected) Gone.Add(key);
            foreach (var key in Gone) Hung.Remove(key);
        }
        var place = model.transform;
        place.SetParent(body.transform, false);
        Hung[place.Pointer] = (place, body.Pointer);
    }

    private static readonly List<IntPtr> Gone = new();

    /// <summary>Whether <paramref name="node"/> is part of a model a look hung on a body (<see cref="Hang"/>).</summary>
    public static bool Worn(Transform node)
    {
        if (Hung.Count == 0) return false;
        for (var at = node; at != null; at = at.parent)
            if (Hung.TryGetValue(at.Pointer, out var hung) && hung.Root != null && !hung.Root.WasCollected) return true;
        return false;
    }

    /// <summary>The roots of the looks hung on the body whose animator is <paramref name="body"/>, into <paramref name="into"/> (cleared first):
    /// a hold over the whole person takes them wherever that animator sits against the person.</summary>
    internal static void HungOn(IntPtr body, List<Transform> into)
    {
        into.Clear();
        foreach (var hung in Hung.Values)
            if (hung.Body == body && hung.Root != null && !hung.Root.WasCollected) into.Add(hung.Root);
    }

    internal static void Withdraw(PlayerLookProvision provision) => Router.Withdraw(provision);

    internal static void Raise(SNet_Player player)
    {
        try { Changed?.Invoke(player); }
        catch (Exception e) { Plugin.Logger.ErrorOnce("visual-player-look-changed", "ForgeVisual player looks: a Changed handler threw: " + e.Message); }
    }
}
