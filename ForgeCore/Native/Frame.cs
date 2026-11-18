using System;
using UnityEngine;

namespace ForgeCore;

/// <summary>Per-frame work for mods, so no mod injects a driver component of its own. The callbacks run from ForgeCore's one driver,
/// in registration order: <see cref="OnUpdate"/> in the game's <c>Update</c>, <see cref="OnLateUpdate"/> in <c>LateUpdate</c> (after
/// the game's animation), <see cref="OnFixedUpdate"/> in <c>FixedUpdate</c>, <see cref="OnQuit"/> when the driver is destroyed at shutdown. A callback that throws is reported once
/// under the mod's ID and never stops the others or the next frame. Nothing here allocates on an ordinary frame; keep the callback a
/// static method or a delegate stored once.</summary>
public static class Frame
{
    internal static readonly FrameBook Updates = new("update"), LateUpdates = new("late update"), FixedUpdates = new("fixed update"), Quits = new("quit");

    /// <summary><c>Time.unscaledTime</c> and <c>Time.unscaledDeltaTime</c> as of the start of the driver's last <c>Update</c>: one native read per frame for every
    /// package that times a fade, a wait or a hold, in place of one read at each of its call sites. A reader that runs before the driver in a frame sees the previous
    /// frame's (at most one frame old); anything that must tell whether two reads are of the same frame keeps <c>Time.frameCount</c>.</summary>
    public static float UnscaledTime { get; private set; }

    public static float UnscaledDeltaTime { get; private set; }

    /// <summary><c>Time.time</c> (scaled game time), <c>Screen.width</c> and <c>Screen.height</c> as of the same moment, by the same rule.</summary>
    public static float GameTime { get; private set; }

    public static int ScreenWidth { get; private set; }

    public static int ScreenHeight { get; private set; }

    /// <summary><c>Time.frameCount</c> as of the same moment: a per-frame cache keyed on it is read again once per driver frame. Input edges, which must
    /// match the game's own input frame, keep <c>Time.frameCount</c>.</summary>
    internal static int Count { get; private set; }

    /// <summary>The game's focus state (<c>FocusStateManager.CurrentState</c>) for the HUD, the input leases and the wheel: read from the game by the
    /// first reader of a frame and kept for that frame (keyed on <c>Time.frameCount</c>), so a reader before the driver sees this frame's focus too.</summary>
    internal static eFocusState Focus
    {
        get
        {
            var frame = Time.frameCount;
            if (frame == _focusFrame) return _focus;
            _focusFrame = frame;
            try { _focus = FocusStateManager.CurrentState; }
            catch (Exception error)
            {
                if (!_focusFailed)
                {
                    _focusFailed = true;
                    Plugin.Logger.ErrorOnce("core-reading-focus-state-failed", "Reading the game's focus state failed: " + error);
                }
            }
            return _focus;
        }
    }

    private static eFocusState _focus;
    private static int _focusFrame = -1;
    private static bool _focusFailed;

    /// <summary>Read once at the start of the driver's <c>Update</c>, and once when ForgeCore loads so a reader before the first frame sees real values.</summary>
    internal static void Stamp()
    {
        UnscaledTime = Time.unscaledTime; UnscaledDeltaTime = Time.unscaledDeltaTime; GameTime = Time.time;
        ScreenWidth = Screen.width; ScreenHeight = Screen.height;
        Count = Time.frameCount;
    }

    /// <summary>Runs in the game's <c>FixedUpdate</c>, before the physics step.</summary>
    public static void OnFixedUpdate(string modId, Action callback) => FixedUpdates.Add(modId, callback);

    public static void OnUpdate(string modId, Action callback) => Updates.Add(modId, callback);

    public static void OnLateUpdate(string modId, Action callback) => LateUpdates.Add(modId, callback);

    public static void OnQuit(string modId, Action callback) => Quits.Add(modId, callback);
}
