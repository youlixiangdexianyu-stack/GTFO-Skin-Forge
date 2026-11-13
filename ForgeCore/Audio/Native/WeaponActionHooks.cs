using System;

namespace ForgeAudio;

/// <summary>Where the kit raises weapon actions to the mods that claimed a weapon (<see cref="AudioKit.ClaimWeaponActions"/>). The
/// game's weapon sounds themselves come from the weapon's animation events and are not touched; each action here is one hook on
/// the game's own native event, answered by one probe of the claim table on the weapon's native pointer, with no allocation, no
/// string and no lookup by name while playing. The reload start and end hang on <c>ItemEquippable</c> (the one body every reload
/// path reaches), the empty-magazine click on the empty-clip path of the automatic and the semi and burst archetypes; the start,
/// the end and the click are ForgeCore's patches, subscribed in <see cref="Subscribe"/>.</summary>
internal static class WeaponActions
{
    internal static readonly ClaimBook<WeaponActionClaim> Book = new();
    private const WeaponAction AllKinds = WeaponAction.ReloadStart | WeaponAction.ReloadDone | WeaponAction.DryFire;

    internal static WeaponActionClaim? Claim(string modId, Item item, WeaponAction kinds, IWeaponActionHandler handler, int priority)
    {
        string? problem = null;
        if (!AudioKit.ValidIdentity(modId)) problem = "invalid mod ID";
        else if (item == null || item.WasCollected) problem = "no weapon";
        else if (kinds == 0 || (kinds & ~AllKinds) != 0) problem = "invalid kinds " + (int)kinds;
        else if (handler == null) problem = "no handler";
        if (problem != null)
        {
            AudioLog.Report(AudioError.InvalidOptions, (modId ?? "?") + ": weapon action claim refused (" + problem + ").");
            return null;
        }
        if (!AudioKit.OnMainThread)
        {
            AudioLog.Report(AudioError.WrongThread, modId + ": weapon actions must be claimed on the Unity main thread.");
            return null;
        }
        Plugin.InstallHooks(HookGroup.Weapon);
        var claim = new WeaponActionClaim(modId, item!, kinds, priority, handler!);
        claim.Entry = Book.Add(claim, new[] { item!.Pointer }, (int)kinds, priority);

        return claim;
    }

    /// <summary>Subscribes to the game methods ForgeCore patches once for every package (<c>ForgeCore.GameHooks</c>), when the Weapon
    /// hook group goes on (the first claim; ForgeCore puts its own patch on at the first subscription, so a session with no claim
    /// pays for none of them). The reload start is the item's own flag rising across the body (the same body the game's reload paths
    /// all reach); the empty-magazine click is the automatic archetype's own empty-clip path and the one body semi-automatic and
    /// burst archetypes share (<c>BWA_Semi</c> and <c>BWA_Burst</c>).</summary>
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.ReloadTriggered.Around<bool>(static item => Book.Count != 0 && item.IsReloading, ReloadTriggered);
        ForgeCore.GameHooks.ReloadDone.After(static (item, ran) => { if (ran) Raise(item, WeaponAction.ReloadDone); });
        ForgeCore.GameHooks.AutoDryFired.After(static (archetype, _) => Raise(archetype.m_weapon, WeaponAction.DryFire));
        ForgeCore.GameHooks.SemiDryFired.After(static (archetype, _) => Raise(archetype.m_weapon, WeaponAction.DryFire));
    }

    private static void ReloadTriggered(ItemEquippable item, bool wasReloading, bool ran)
    {
        if (ran && Book.Count != 0 && !wasReloading && item.IsReloading) Raise(item, WeaponAction.ReloadStart);
    }

    /// <summary>Ends every claim (level exit, kit shutdown).</summary>
    internal static void Clear() => Book.Clear();

    internal static void Raise(Item? item, WeaponAction action)
    {
        if (Book.Count == 0 || item == null || item.WasCollected) return;
        var claim = Book.Find(item.Pointer, (int)action);
        if (claim == null) return;
        try { claim.Handler.Play(action); }
        catch (Exception error)
        {
            if (!claim.HandlerFailed) AudioLog.Report(AudioError.CallbackFailed, claim.ModId + ": weapon action handler failed (" + error + ").");
            claim.HandlerFailed = true;
        }
    }
}
