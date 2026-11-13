using System;
using System.Runtime.InteropServices;
using Feedback;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ForgeAudio;

/// <summary>The one place the kit replaces the game's own sounds. The game plays every player body sound through
/// <c>FeedbackPlayer.Play</c>: footsteps of every movement state and of the third-person walk animation through
/// <c>PlayerFPSUtils.PlayFootstep</c> to <c>Play(pos, rot, material)</c> (or the overload with a parent transform); the jump
/// (<c>PLOC_Jump.CommonEnter</c>), landing (<c>PLOC_Land.CommonExit</c>) and end of a fall (<c>PLOC_Fall.CommonExit</c>) through
/// <c>Play(material)</c>. Each of those feedback players is rooted (<c>FeedbackPlayer.SetRoot</c>) on a transform of its player,
/// so a play is matched to a claim by reading that root's native pointer straight from the feedback player.</summary>
internal static class BodySounds
{
    internal static readonly ClaimBook<BodySoundClaim> Book = new();
    private const BodySound AllKinds = BodySound.Footstep | BodySound.Movement;
    private static int _rootOffset = -1;

    internal static BodySoundClaim? Claim(string modId, GameObject root, BodySound kinds, IBodySoundHandler handler, int priority)
    {
        string? problem = null;
        if (!AudioKit.ValidIdentity(modId)) problem = "invalid mod ID";
        else if (root == null || root.WasCollected) problem = "no root";
        else if (kinds == 0 || (kinds & ~AllKinds) != 0) problem = "invalid kinds " + (int)kinds;
        else if (handler == null) problem = "no handler";
        if (problem != null)
        {
            AudioLog.Report(AudioError.InvalidOptions, (modId ?? "?") + ": body sound claim refused (" + problem + ").");
            return null;
        }
        if (!AudioKit.OnMainThread)
        {
            AudioLog.Report(AudioError.WrongThread, modId + ": body sounds must be claimed on the Unity main thread.");
            return null;
        }
        if (!Ready()) return null;
        Plugin.InstallHooks(HookGroup.Body);
        if (!Ready()) return null;
        var claim = new BodySoundClaim(modId, root!, kinds, priority, handler!);
        var pointers = Collect(root!);
        claim.Entry = Book.Add(claim, pointers, (int)kinds, priority);

        return claim;
    }

    internal static IntPtr[] Collect(GameObject root)
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        var pointers = new IntPtr[transforms.Length];
        for (var i = 0; i < transforms.Length; i++) pointers[i] = transforms[i].Pointer;
        return pointers;
    }

    /// <summary>Ends every claim (level exit, kit shutdown).</summary>
    internal static void Clear() => Book.Clear();

    private static bool Ready()
    {
        if (_rootOffset >= 0) return true;
        if (_rootOffset == int.MinValue) return false;
        try
        {
            var field = IL2CPP.GetIl2CppField(Il2CppClassPointerStore<FeedbackPlayer>.NativeClassPtr, "m_root");
            if (field == IntPtr.Zero) throw new InvalidOperationException("FeedbackPlayer has no m_root field");
            _rootOffset = (int)IL2CPP.il2cpp_field_get_offset(field);
            return true;
        }
        catch (Exception error)
        {
            Disable("the feedback player's root cannot be read: " + error);
            return false;
        }
    }

    /// <summary>Refuses every claim from now on (logged once); the game's body sounds stay.</summary>
    internal static void Disable(string reason)
    {
        if (_rootOffset == int.MinValue) return;
        _rootOffset = int.MinValue;
        AudioLog.Report(AudioError.EnvironmentUnavailable, "Body sounds cannot be replaced (" + reason + "); body sound claims are refused and the game's sounds stay.");
    }

    /// <summary>True when a claim took the sound (the game's own play is skipped).</summary>
    internal static bool Taken(FeedbackPlayer player, BodySound kind, Vector3 position, bool positioned)
    {
        if (Book.Count == 0 || _rootOffset < 0 || player == null) return false;
        var claim = Book.Find(Marshal.ReadIntPtr(player.Pointer + _rootOffset), (int)kind);
        if (claim == null) return false;
        try
        {
            if (!positioned) position = claim.RootTransform.position;
            return claim.Handler.Play(kind, position);
        }
        catch (Exception error)
        {
            if (!claim.HandlerFailed) AudioLog.Report(AudioError.CallbackFailed, claim.ModId + ": body sound handler failed; the game's sound plays (" + error + ").");
            claim.HandlerFailed = true;
            return false;
        }
    }
}

[HarmonyPatch(typeof(FeedbackPlayer), nameof(FeedbackPlayer.Play), new[] { typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(int) })]
[LazyHook(HookGroup.Body)]
internal static class BodySoundAttached
{
    [HarmonyPrefix]
    private static bool Prefix(FeedbackPlayer __instance, Vector3 __0) => !BodySounds.Taken(__instance, BodySound.Footstep, __0, true);
}

[HarmonyPatch(typeof(FeedbackPlayer), nameof(FeedbackPlayer.Play), new[] { typeof(Vector3), typeof(Quaternion), typeof(int) })]
[LazyHook(HookGroup.Body)]
internal static class BodySoundAt
{
    [HarmonyPrefix]
    private static bool Prefix(FeedbackPlayer __instance, Vector3 __0) => !BodySounds.Taken(__instance, BodySound.Footstep, __0, true);
}

[HarmonyPatch(typeof(FeedbackPlayer), nameof(FeedbackPlayer.Play), new[] { typeof(int) })]
[LazyHook(HookGroup.Body)]
internal static class BodySoundRooted
{
    [HarmonyPrefix]
    private static bool Prefix(FeedbackPlayer __instance) => !BodySounds.Taken(__instance, BodySound.Movement, default, false);
}
