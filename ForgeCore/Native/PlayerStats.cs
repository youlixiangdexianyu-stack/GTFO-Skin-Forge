using System;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeCore;

/// <summary>The local player's walk speed, jump, stamina cost and hit flinch, changed through the game's own paths. Whoever needs one
/// subscribes (the first subscription puts that hook on, and it stays on); every subscriber is asked, so two packages combine: walk sources
/// chain, jump factors multiply, and any one source is enough for the yes-or-no ones. Each peer changes only its own local player and nothing
/// is sent.
/// <para>Walk: the velocity every locomotion state asks for (<c>PLOC_Base.GetHorizontalVelocityFromInput</c>) goes through
/// <see cref="Walk"/>, and <see cref="CarryUnslowed"/> lifts the game's carry slowdown (<c>PlayerAgent.moveSpeedMult</c>, which the states set
/// before asking). Jump: <c>PLOC_Jump.Enter</c> reads <c>PlayerDataBlock.jumpVelInitial</c> and every air step clamps the rise to
/// <c>jumpVerticalVelocityMax</c>; for the local player's own calls both are scaled by <see cref="Jump"/> for the duration of the game's call
/// and put back right after it (the clamp only while rising). Stamina: while <see cref="NoStamina"/> holds, <c>PlayerStamina.UseStamina</c> is
/// skipped. Flinch: while <see cref="NoFlinch"/> holds, <c>Dam_PlayerDamageLocal.Hitreact</c> is skipped.</para></summary>
public static class PlayerStats
{
    private static Func<PlayerAgent, Vector3, Vector3>[] _walk = [];
    private static Func<bool>[] _unslowed = [], _noFlinch = [];
    private static Func<PlayerAgent?, float>[] _jump = [];
    private static Func<PlayerAgent?, bool>[] _noStamina = [];

    /// <summary>Changes the velocity the local player's locomotion asked for. Puts the walk hook on.</summary>
    public static void Walk(Func<PlayerAgent, Vector3, Vector3> velocity)
    {
        Patches.Ensure(Modules.PlayerWalk, typeof(PlayerWalkHook));
        _walk = [.. _walk, velocity];
    }

    /// <summary>True lifts the game's carry slowdown for the local player. Puts the walk hook on.</summary>
    public static void CarryUnslowed(Func<bool> unslowed)
    {
        Patches.Ensure(Modules.PlayerWalk, typeof(PlayerWalkHook));
        _unslowed = [.. _unslowed, unslowed];
    }

    /// <summary>A factor on the jump speed of the player asked about (1 leaves it). Puts the two jump hooks on.</summary>
    public static void Jump(Func<PlayerAgent?, float> speed)
    {
        Patches.Ensure(Modules.PlayerJump, typeof(PlayerJumpHook));
        Patches.Ensure(Modules.PlayerRise, typeof(PlayerRiseHook));
        _jump = [.. _jump, speed];
    }

    /// <summary>True skips the stamina cost of the player asked about. Puts the stamina hook on.</summary>
    public static void NoStamina(Func<PlayerAgent?, bool> free)
    {
        Patches.Ensure(Modules.PlayerStamina, typeof(PlayerStaminaHook));
        _noStamina = [.. _noStamina, free];
    }

    /// <summary>True skips the local player's hit flinch. Puts the flinch hook on.</summary>
    public static void NoFlinch(Func<bool> calm)
    {
        Patches.Ensure(Modules.PlayerFlinch, typeof(PlayerFlinchHook));
        _noFlinch = [.. _noFlinch, calm];
    }

    internal static bool Unslowed() => AnyOf(_unslowed);

    internal static bool Calm() => AnyOf(_noFlinch);

    internal static bool StaminaFree(PlayerAgent? owner)
    {
        foreach (var free in _noStamina)
        {
            try { if (free(owner)) return true; }
            catch (Exception error) { Failed("stamina", error); }
        }
        return false;
    }

    internal static Vector3 Walked(PlayerAgent owner, Vector3 velocity)
    {
        foreach (var walk in _walk)
        {
            try { velocity = walk(owner, velocity); }
            catch (Exception error) { Failed("walk", error); }
        }
        return velocity;
    }

    internal static float JumpSpeed(PlayerAgent? owner)
    {
        var speed = 1f;
        foreach (var factor in _jump)
        {
            try { speed *= factor(owner); }
            catch (Exception error) { Failed("jump", error); }
        }
        return speed;
    }

    private static bool AnyOf(Func<bool>[] sources)
    {
        foreach (var source in sources)
        {
            try { if (source()) return true; }
            catch (Exception error) { Failed("player stat", error); }
        }
        return false;
    }

    private static void Failed(string what, Exception error) => Plugin.Logger.ErrorOnce("core-source-failed", "A " + what + " source failed: " + error);
}

[HarmonyPatch(typeof(PLOC_Base), "GetHorizontalVelocityFromInput")]
internal static class PlayerWalkHook
{
    [HarmonyPrefix]
    private static void Before(PLOC_Base __instance)
    {
        if (!PlayerStats.Unslowed()) return;
        var owner = __instance.m_owner;
        if (owner != null && owner.IsLocallyOwned && owner.moveSpeedMult < 1f) owner.moveSpeedMult = 1f;
    }

    [HarmonyPostfix]
    private static void After(PLOC_Base __instance, ref Vector3 __result)
    {
        var owner = __instance.m_owner;
        if (owner != null && owner.IsLocallyOwned) __result = PlayerStats.Walked(owner, __result);
    }
}

[HarmonyPatch(typeof(PLOC_Base), "UpdateVerticalVelocityInAir")]
internal static class PlayerRiseHook
{
    [HarmonyPrefix]
    private static void Before(PLOC_Base __instance, out float __state)
    {
        __state = 0f;
        var owner = __instance.m_owner;
        var speed = PlayerStats.JumpSpeed(owner);
        if (speed == 1f || owner!.Locomotion == null || owner.Locomotion.VerticalVelocity.y <= 0f) return;
        var data = owner.PlayerData;
        if (data == null) return;
        __state = data.jumpVerticalVelocityMax;
        data.jumpVerticalVelocityMax = __state * speed;
    }

    [HarmonyPostfix]
    private static void After(PLOC_Base __instance, float __state)
    {
        if (__state <= 0f) return;
        var data = __instance.m_owner?.PlayerData;
        if (data != null) data.jumpVerticalVelocityMax = __state;
    }
}

[HarmonyPatch(typeof(PLOC_Jump), nameof(PLOC_Jump.Enter))]
internal static class PlayerJumpHook
{
    [HarmonyPrefix]
    private static void Before(PLOC_Jump __instance, out float __state)
    {
        __state = 0f;
        var owner = __instance.m_owner;
        var speed = PlayerStats.JumpSpeed(owner);
        if (speed == 1f) return;
        var data = owner!.PlayerData;
        if (data == null) return;
        __state = data.jumpVelInitial;
        data.jumpVelInitial = __state * speed;
    }

    [HarmonyPostfix]
    private static void After(PLOC_Jump __instance, float __state)
    {
        if (__state <= 0f) return;
        var data = __instance.m_owner?.PlayerData;
        if (data != null) data.jumpVelInitial = __state;
    }
}

[HarmonyPatch(typeof(PlayerStamina), nameof(PlayerStamina.UseStamina))]
internal static class PlayerStaminaHook
{
    [HarmonyPrefix]
    private static bool Before(PlayerStamina __instance) => !PlayerStats.StaminaFree(__instance.m_owner);
}

[HarmonyPatch(typeof(Dam_PlayerDamageLocal), "Hitreact")]
internal static class PlayerFlinchHook
{
    [HarmonyPrefix]
    private static bool Before() => !PlayerStats.Calm();
}
