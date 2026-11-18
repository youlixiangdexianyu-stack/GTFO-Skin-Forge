using System;

namespace ForgeUI;

/// <summary>The features of ForgeUI that hook the game. Each group's Harmony patches go on the first time the feature is used (a
/// registration, or the first read of a game readout), never at startup.</summary>
internal enum PatchGroup : byte
{
    /// <summary>Presses and hovers on the items ForgeUI copies (lobby buttons, menu bar buttons, picker rows).</summary>
    Clicks,
    MenuBar,
    MenuScreen,
    Picker,
    /// <summary>Rows added to the game's settings pages (key actions, audio sliders).</summary>
    Settings,
    GearNames,
    /// <summary>The weapon list row's two name calls, which <see cref="Inventory"/> and <see cref="GearNames"/> share; on with either.</summary>
    SlotNames,
    GearIcons,
    /// <summary>A claim hiding a game HUD part.</summary>
    HudHides,
    WeaponListHidden,
    /// <summary>The local player's enemy hits (health bars and hit feedback).</summary>
    LocalHits,
    // The game readouts: used from the first read of the matching UIKit property.
    Vitals,
    Inventory,
    Objectives,
    Interaction,
    Subtitles,
    Intel,
    Boosters,
    Pickups,
    /// <summary>A teammate tag drawn above the teammate's name marker.</summary>
    Markers,
}

/// <summary>Marks a Harmony patch class that goes on with its <see cref="PatchGroup"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class PatchGroupAttribute : Attribute
{
    public PatchGroupAttribute(PatchGroup group) { Group = group; }
    internal PatchGroup Group { get; }
}
