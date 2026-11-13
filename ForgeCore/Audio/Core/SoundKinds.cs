using System;

namespace ForgeAudio;

/// <summary>The game's own sounds of a player's body that a mod can take over (<see cref="AudioKit.ClaimBodySounds"/>).</summary>
[Flags]
public enum BodySound
{
    /// <summary>Every footstep: the movement states and the third-person walk animation alike.</summary>
    Footstep = 1,
    /// <summary>The body's other movement sounds: jump take-off, landing, end of a fall.</summary>
    Movement = 2,
}

/// <summary>The weapon actions a mod can add its own sound to (<see cref="AudioKit.ClaimWeaponActions"/>).</summary>
[Flags]
public enum WeaponAction
{
    /// <summary>The reload animation sequence really started (the weapon's own reloading flag rose).</summary>
    ReloadStart = 1,
    /// <summary>The reload animation finished and cleared the weapon's reloading flag.</summary>
    ReloadDone = 2,
    /// <summary>A trigger pull on an empty magazine.</summary>
    DryFire = 4,
}
