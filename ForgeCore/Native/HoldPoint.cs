using System;
using System.Collections.Generic;
using Player;
using UnityEngine;

namespace ForgeCore;

/// <summary>The game's own timed hold point (<c>Interact_Timed</c>): the prompt, the hold duration and the callbacks a player's hold
/// raises, set and subscribed the one way the game's own hold points are. A mod places the collider and the object; this sets the
/// component up and later takes the callbacks off again. Main thread only.</summary>
public static class HoldPoint
{
    private static readonly HashSet<IntPtr> Shut = new();

    /// <summary>Called by <see cref="Disable"/> before the first point is shut, so the patch that keeps it shut goes on only then (set by
    /// the plugin; offline nothing is patched).</summary>
    internal static Action NeedShutHook { get; set; } = static () => { };

    /// <summary>Whether <paramref name="interact"/> was shut by <see cref="Disable"/> and not yet opened by <see cref="Enable"/>.</summary>
    internal static bool IsShut(Interact_Timed interact) => Shut.Count != 0 && Shut.Contains(interact.Pointer);

    /// <summary>Forgets every shut point: the level they belonged to is gone.</summary>
    internal static void ForgetAll() => Shut.Clear();

    /// <summary>Owns <paramref name="collider"/>, holds for <paramref name="seconds"/>, shows <paramref name="prompt"/>, and only
    /// answers while looked at straight on, with a collision check and aborting when the look or distance drifts.</summary>
    public static void Configure(Interact_Timed interact, BoxCollider collider, float seconds, string prompt)
    {
        interact.m_colliderToOwn = collider;
        interact.InteractDuration = seconds;
        interact.InteractionMessage = prompt;
        interact.OnlyActiveWhenLookingStraightAt = true;
        interact.RequireCollisionCheck = true;
        interact.AbortOnDotOrDistanceDiff = true;
    }

    public static void Subscribe(Interact_Timed interact, Il2CppSystem.Action<PlayerAgent, bool> selected,
        Il2CppSystem.Func<PlayerAgent, bool> canInteract, Il2CppSystem.Action<float> timer, Il2CppSystem.Action aborted,
        Il2CppSystem.Action<PlayerAgent> triggered)
    {
        interact.add_OnInteractionSelected(selected);
        interact.add_ExternalPlayerCanInteract(canInteract);
        interact.add_TimerUpdated(timer);
        interact.add_OnInteractionEvaluationAbort(aborted);
        interact.add_OnInteractionTriggered(triggered);
    }

    public static void Unsubscribe(Interact_Timed interact, Il2CppSystem.Action<PlayerAgent, bool> selected,
        Il2CppSystem.Func<PlayerAgent, bool> canInteract, Il2CppSystem.Action<float> timer, Il2CppSystem.Action aborted,
        Il2CppSystem.Action<PlayerAgent> triggered)
    {
        interact.remove_OnInteractionSelected(selected);
        interact.remove_ExternalPlayerCanInteract(canInteract);
        interact.remove_TimerUpdated(timer);
        interact.remove_OnInteractionEvaluationAbort(aborted);
        interact.remove_OnInteractionTriggered(triggered);
    }

    public static void SetActive(Interact_Timed interact, bool active) => interact.SetActive(active);

    /// <summary>Shuts the point: no prompt and no hold, and it stays shut when the game switches it on again (placing, settling,
    /// culling) because the game's own <c>SetActive(true)</c> on it is turned into <c>SetActive(false)</c> until <see cref="Enable"/>.
    /// Event-driven: nothing runs per frame. A point that is destroyed while shut stays listed until the level ends.</summary>
    public static void Disable(Interact_Timed interact)
    {
        if (interact == null) throw new ArgumentNullException(nameof(interact));
        NeedShutHook();
        Shut.Add(interact.Pointer);
        if (interact.IsActive) interact.SetActive(false);
        if (interact.enabled) interact.enabled = false;
    }

    /// <summary>Opens a point <see cref="Disable"/> shut: the game's switch-on is let through again, and the point is switched on.</summary>
    public static void Enable(Interact_Timed interact)
    {
        if (interact == null) throw new ArgumentNullException(nameof(interact));
        Shut.Remove(interact.Pointer);
        interact.enabled = true;
        interact.SetActive(true);
    }

    public static void SetPrompt(Interact_Timed interact, string prompt) => interact.InteractionMessage = prompt;
}
