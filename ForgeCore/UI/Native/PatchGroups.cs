using System;

namespace ForgeUI;

/// <summary>Puts the Harmony patches of a <see cref="PatchGroup"/> on the first time the feature it serves is used (the installer is
/// ForgeCore's <see cref="ForgeCore.Patches.Groups{TGroup}"/>). Once on they stay. A class that cannot be patched is reported once and its
/// feature stays unhooked; the other groups are not affected.</summary>
internal static class PatchGroups
{
    private static readonly ForgeCore.LazyGroups<PatchGroup> Groups = ForgeCore.Patches.Groups<PatchGroup>(Plugin.Id,
        type => type.GetCustomAttributes(typeof(PatchGroupAttribute), false) is { Length: > 0 } found ? ((PatchGroupAttribute)found[0]).Group : null,
        (group, type, error) => Plugin.Error("ui-hooks-could-not-be-put-feature", "Hooks of " + group + " (" + type.Name + ") could not be put on; the feature stays unhooked: " + error));

    /// <summary>The group's feature has been used (its patches are on, or were attempted).</summary>
    internal static bool Has(PatchGroup group) => Groups.Has(group);

    /// <summary>Puts the group's patches on the first time; every later call is one comparison.</summary>
    internal static void Ensure(PatchGroup group)
    {
        if (!Groups.Ensure(group)) return;
        try { Subscribe(group); }
        catch (Exception error) { Plugin.Error("ui-hooks-could-not-be-set-up", "Hooks of " + group + " could not be set up; the feature stays unhooked: " + error); }
    }

    /// <summary>What a group needs besides patches, once.</summary>
    private static void Subscribe(PatchGroup group)
    {
        switch (group)
        {
            case PatchGroup.Objectives: GameObjectives.Subscribe(); UIKit.ObjectivesReading.ReadInFull(); break;
            case PatchGroup.Inventory: UIKit.InventoryReading.ReadInFull(); Ensure(PatchGroup.SlotNames); break;
            case PatchGroup.GearNames: Ensure(PatchGroup.SlotNames); break;
            case PatchGroup.Intel: GameWardenIntel.Subscribe(); break;
        }
    }
}
