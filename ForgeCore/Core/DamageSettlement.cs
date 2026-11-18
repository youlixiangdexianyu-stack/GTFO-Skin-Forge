namespace ForgeCore;

/// <summary>A damage request as the game settled it: the receiver's health just before and just after the call on this peer. On
/// the receiver's authority (the host for enemies) that is the settlement; elsewhere the game forwards the request and the
/// receiver's own peer settles it later.</summary>
public readonly record struct DamageSettlement(float HealthBefore, float HealthAfter)
{
    public float Dealt => HealthBefore - HealthAfter;
}
