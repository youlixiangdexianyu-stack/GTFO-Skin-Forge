using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ForgeAudio;

/// <summary>Plays a mod's own sound for a reserved event (<see cref="AudioKit.ReserveGameEvent"/>). Called on the main
/// thread from inside the game's post, once per post; the game's post is dropped whatever the handler does.</summary>
public interface IReservedEventHandler
{
    /// <summary>The game posted <paramref name="eventId"/>. <paramref name="soundPlayer"/> is the native pointer of the
    /// game sound player it was posted on (zero when the post has none, as the game's static posts). When
    /// <paramref name="positioned"/> is true the post named <paramref name="position"/>; otherwise it plays where
    /// its sound player is, which the game keeps inside the sound engine and no script can read back.</summary>
    void Play(uint eventId, IntPtr soundPlayer, Vector3 position, bool positioned);
}

public static partial class AudioKit
{
    /// <summary>Gives a mod its own sound event ID to write into a place the game reads an event ID from (a data block
    /// field, say). The game then posts it like any event of its own, on every path that reads that place, and instead of
    /// reaching the sound engine the post calls <paramref name="handler"/>. Nothing else posting other IDs is touched:
    /// this replaces one place, unlike matching a game event ID, which many places share. The ID is never one of the
    /// game's events and stays reserved until the game closes. The hooks go on the game at the first reservation. Main
    /// thread only. Returns 0 (and logs why) on an invalid argument, off the main thread, when all IDs are taken or when
    /// the game's posts cannot be hooked; write nothing then, and the game's own sound stays.</summary>
    public static uint ReserveGameEvent(string modId, IReservedEventHandler handler) => ReservedEvents.Reserve(modId, handler);
}

/// <summary>The range of reserved IDs and who answers each. The hooks ask only whether an ID falls in the range, so
/// every other post of the game passes through without a lookup.</summary>
internal static class ReservedEvents
{
    private const uint Capacity = 4096, FirstCandidate = 0xF0A00000;
    private static readonly IReservedEventHandler[] Handlers = new IReservedEventHandler[Capacity];
    private static readonly bool[] Failed = new bool[Capacity];
    private static readonly string[] Owners = new string[Capacity];
    private static uint _first;
    private static uint _count;
    private static bool _disabled;

    internal static uint Reserve(string modId, IReservedEventHandler handler)
    {
        string? problem = null;
        if (!AudioKit.ValidIdentity(modId)) problem = "invalid mod ID";
        else if (handler == null) problem = "no handler";
        if (problem != null)
        {
            AudioLog.Report(AudioError.InvalidOptions, (modId ?? "?") + ": game event reservation refused (" + problem + ").");
            return 0;
        }
        if (!AudioKit.OnMainThread)
        {
            AudioLog.Report(AudioError.WrongThread, modId + ": game events must be reserved on the Unity main thread.");
            return 0;
        }
        if (_first == 0 && !_disabled) _first = FreeRange();
        if (!_disabled) Plugin.InstallHooks(HookGroup.Reserved);
        if (_disabled) return 0;
        if (_count == Capacity)
        {
            AudioLog.Report(AudioError.InvalidOptions, modId + ": all " + Capacity + " reserved game events are taken.");
            return 0;
        }
        Handlers[_count] = handler!;
        Owners[_count] = modId!;
        return _first + _count++;
    }

    /// <summary>The first range of IDs with none of the game's events in it. Wwise event IDs are hashes spread over the
    /// whole 32 bits, so this is one read of the event list in practice.</summary>
    private static uint FreeRange()
    {
        try
        {
            var events = typeof(AK.EVENTS).GetProperties(BindingFlags.Public | BindingFlags.Static);
            for (var candidate = FirstCandidate; candidate < uint.MaxValue - Capacity; candidate += Capacity)
            {
                var free = true;
                foreach (var property in events)
                {
                    if (property.PropertyType == typeof(uint) && property.GetValue(null) is uint id && id - candidate < Capacity) { free = false; break; }
                }
                if (free) return candidate;
            }
            Disable("no free range of event IDs");
        }
        catch (Exception error) { Disable("the game's event list cannot be read: " + error); }
        return 0;
    }

    /// <summary>Refuses every later reservation (logged once); an ID already written keeps working if its hook went on.</summary>
    internal static void Disable(string reason)
    {
        if (_disabled) return;
        _disabled = true;
        AudioLog.Report(AudioError.EnvironmentUnavailable, "Game events cannot be reserved (" + reason + "); the game's sounds stay.");
    }

    internal static bool Holds(uint id) => id - _first < _count;

    /// <summary>True when <paramref name="id"/> is reserved, so the game's post must not go on.</summary>
    internal static bool Taken(uint id, IntPtr soundPlayer, Vector3 position, bool positioned)
    {
        var index = id - _first;
        if (index >= _count) return false;
        try { Handlers[index].Play(id, soundPlayer, position, positioned); }
        catch (Exception error)
        {
            if (!Failed[index]) AudioLog.Report(AudioError.CallbackFailed, Owners[index] + ": reserved game event handler failed (" + error + ").");
            Failed[index] = true;
        }
        return true;
    }
}

/// <summary>Every entry an event ID read from a data block reaches the sound engine through: the enemy voice (which posts
/// directly, not through the point emitter), the point emitter's two overloads, and the two static posts. The point emitter's two
/// overloads also tell the observer of <see cref="AudioKit.ObserveGameEvents"/>, so each overload has one prefix.</summary>
[HarmonyPatch]
[LazyHook(HookGroup.Reserved)]
internal static class ReservedEventHooks
{
    [HarmonyPatch(typeof(EnemyVoice), nameof(EnemyVoice.PlayVoiceEvent))]
    [HarmonyPrefix]
    private static bool Voice(EnemyVoice __instance, uint __0)
    {
        if (!ReservedEvents.Holds(__0)) return true;
        // The game's own voice posts nothing while its voice is switched off; a reserved event keeps that rule.
        if (!__instance.m_enabled) return false;
        var owner = __instance.m_owner;
        var player = owner != null ? owner.Sound : null;
        ReservedEvents.Taken(__0, player != null ? player.Pointer : IntPtr.Zero, default, false);
        return false;
    }

    [HarmonyPatch(typeof(CellSoundPlayer), nameof(CellSoundPlayer.Post), typeof(uint), typeof(bool))]
    [HarmonyPrefix]
    private static bool Global(CellSoundPlayer __instance, uint __0, ref uint __result)
    {
        AudioKit.RaiseGameEvent(__0);
        if (!ReservedEvents.Taken(__0, __instance.Pointer, default, false)) return true;
        __result = 0;
        return false;
    }

    [HarmonyPatch(typeof(CellSoundPlayer), nameof(CellSoundPlayer.Post), typeof(uint), typeof(Vector3))]
    [HarmonyPrefix]
    private static bool At(CellSoundPlayer __instance, uint __0, Vector3 __1, ref uint __result)
    {
        AudioKit.RaiseGameEvent(__0);
        if (!ReservedEvents.Taken(__0, __instance.Pointer, __1, true)) return true;
        __result = 0;
        return false;
    }

    [HarmonyPatch(typeof(CellSound), nameof(CellSound.Post), typeof(uint), typeof(Vector3))]
    [HarmonyPrefix]
    private static bool StaticAt(uint __0, Vector3 __1, ref ulong __result)
    {
        if (!ReservedEvents.Taken(__0, IntPtr.Zero, __1, true)) return true;
        __result = 0;
        return false;
    }

    /// <summary>The game delays a footstep by the distance to the listener; a reserved event plays at once.</summary>
    [HarmonyPatch(typeof(CellSound), nameof(CellSound.PostDelayed))]
    [HarmonyPrefix]
    private static bool Delayed(uint __0, Vector3 __1, ref uint __result)
    {
        if (!ReservedEvents.Taken(__0, IntPtr.Zero, __1, true)) return true;
        __result = 0;
        return false;
    }
}
