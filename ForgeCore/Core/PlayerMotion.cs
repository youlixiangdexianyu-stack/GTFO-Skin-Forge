namespace ForgeCore;

/// <summary>What a player's body is doing: a one-to-one mirror of the game's <c>PlayerLocomotion.PLOC_State</c>, by value (<see cref="Players.Motion"/>).</summary>
public enum PlayerMotion : byte
{
    Stand = 0,
    Crouch = 1,
    Run = 2,
    Jump = 3,
    Fall = 4,
    Land = 5,
    Stunned = 6,
    Downed = 7,
    ClimbLadder = 8,
    OnTerminal = 9,
    Melee = 10,
    Empty = 11,
    GrabbedByTrap = 12,
    GrabbedByTank = 13,
    Testing = 14,
    InElevator = 15,
    GrabbedByPouncer = 16,
    StandStill = 17,
}
