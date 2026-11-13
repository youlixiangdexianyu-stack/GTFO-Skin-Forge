using System;
using Il2CppInterop.Runtime;

namespace ForgeCore;

/// <summary>The three world-interaction switches of the game (<c>PlayerInteraction</c>) an <see cref="InputLease"/> can hold off.</summary>
public enum InteractionSwitch { CameraRay, SphereCheck, Ladder }

/// <summary>A mod's hold on some of the game's input actions (movement axes, fire, weapon keys ...); it can also press buttons and\n/// drive one axis itself (<see cref="Press"/>, <see cref="SetAxis"/>). While held, the blocked
/// actions read as not pressed (axes as 0, or as <see cref="Steer"/> values for the movement axes) everywhere the game reads
/// them; everything else passes through. Leases stack: the blocked set is every held lease's together. When the last lease is
/// released the game's input is put back exactly as it was. Leases are released automatically at level cleanup and when the local
/// player goes down (<see cref="GameEvents.LocalPlayerDown"/>). A blocked action passes while one of the game's menu screens has the focus:
/// their cursor (<c>CM_PageBase.UpdateCursorPosition</c>) reads the look axes. The game's own answer stays readable through <see cref="Down"/>,
/// <see cref="Held"/> and <see cref="Axis"/>. Main thread only.</summary>
public sealed class InputLease : IDisposable
{
    private static readonly InputLeaseBook Book = new();
    private static InputMapper.DelGetButton? _button, _down, _up, _ownButton, _ownDown, _ownUp;
    private static InputMapper.DelGetAxis? _axis, _ownAxis;
    private static bool _wrapped;
    private readonly InputLeaseBook.Entry _entry;

    private InputLease(InputLeaseBook.Entry entry) => _entry = entry;

    /// <summary>A new lease for <paramref name="modId"/> that blocks nothing yet.</summary>
    public static InputLease Take(string modId)
    {
        WatchDown();
        return new(Book.Take(modId));
    }

    public string Owner => _entry.Owner;

    /// <summary>Not released yet (by <see cref="Dispose"/> or automatically).</summary>
    public bool IsHeld => Book.Holds(_entry);

    /// <summary>Blocks exactly <paramref name="actions"/> for this lease (replacing what it blocked before).</summary>
    public void Block(params InputAction[] actions)
    {
        Span<int> values = stackalloc int[actions.Length];
        for (var i = 0; i < actions.Length; i++) values[i] = (int)actions[i];
        Book.Block(_entry, values);
        Apply();
    }

    /// <summary>Holds exactly <paramref name="switches"/> off for this lease (replacing what it held off before). The game's own value is
    /// read when the first lease holds a switch and written back when the last one lets go, unless something else wrote the switch in
    /// between: that latest write is what comes back, so a lease never undoes another system's change.</summary>
    public void BlockInteractions(params InteractionSwitch[] switches)
    {
        Span<int> values = stackalloc int[switches.Length];
        for (var i = 0; i < switches.Length; i++) values[i] = (int)switches[i];
        Book.HoldOff(_entry, values);
        SyncInteractions();
    }

    /// <summary>While the movement axes are blocked, the game reads these values for them instead of 0 (sideways and forward,
    /// -1..1), so its own locomotion moves the player. The newest steering lease wins.</summary>
    public void Steer(float sideways, float forward)
    {
        Book.Steer(_entry, true, sideways, forward);
        Apply();
    }

    /// <summary>Holds <paramref name="action"/> as pressed everywhere the game reads it and arms a fresh press edge, also when the
    /// same button was held or released before. The press edge belongs to the first query frame of the game's input layer.</summary>
    public void Press(InputAction action)
    {
        Book.Press(_entry, (int)action, UnityEngine.Time.frameCount);
        Apply();
    }

    /// <summary>Lets go of a pressed button (its release edge is answered like the press edge). The hold is dropped even when the
    /// game never queried the press; with <paramref name="requireConsumed"/> that is then an error.</summary>
    public void Release(InputAction action, bool requireConsumed = true)
    {
        if (Book.ReleaseButton(_entry, (int)action, UnityEngine.Time.frameCount) && requireConsumed)
            throw new InvalidOperationException("input.press-was-not-consumed-by-game");
    }

    /// <summary>Answers <paramref name="action"/> (one axis per lease) with <paramref name="value"/> everywhere the game reads it.</summary>
    public void SetAxis(InputAction action, float value)
    {
        Book.SetAxis(_entry, (int)action, value);
        Apply();
    }

    /// <summary>Releases every pressed button and zeroes the axis; throws after doing so when a press was never queried.</summary>
    public void ReleasePressed(bool requireConsumed = true)
    {
        var unconsumed = false;
        if (_entry.AxisAction >= 0) Book.SetAxis(_entry, _entry.AxisAction, 0f);
        for (var action = 1; action < InputLeaseBook.Actions; action++)
            if (_entry.Pressed[action]) unconsumed |= Book.ReleaseButton(_entry, action, UnityEngine.Time.frameCount);
        if (unconsumed && requireConsumed) throw new InvalidOperationException("input.press-was-not-consumed-by-game");
    }

    /// <summary>The state of a button this lease pressed (all zero frames and flags when it never did).</summary>
    public InputButtonState Button(InputAction action) => _entry.Buttons[(int)action];

    /// <summary>The lease ends by itself when <paramref name="invalid"/> returns a reason: checked before every query this lease would
    /// answer, so a lease whose owner is gone stops answering at once. <paramref name="ended"/> then receives the reason and the query.</summary>
    public void Guard(Func<string?> invalid, Action<string> ended)
        => Book.Guarded(_entry, invalid ?? throw new ArgumentNullException(nameof(invalid)), ended ?? throw new ArgumentNullException(nameof(ended)));

    public void Dispose()
    {
        if (Book.Release(_entry)) Apply();
    }

    /// <summary>The game's own "pressed this frame", ignoring every lease.</summary>
    public static bool Down(InputAction action) => (_wrapped ? _down : InputMapper.GetButtonDown)?.Invoke(action, eFocusState.FPS) ?? false;

    /// <summary>The game's own "held", ignoring every lease.</summary>
    public static bool Held(InputAction action) => (_wrapped ? _button : InputMapper.GetButton)?.Invoke(action, eFocusState.FPS) ?? false;

    /// <summary>The game's own axis value, ignoring every lease.</summary>
    public static float Axis(InputAction action) => (_wrapped ? _axis : InputMapper.GetAxis)?.Invoke(action, eFocusState.FPS) ?? 0f;

    internal static void ReleaseAll()
    {
        Book.ReleaseAll();
        Apply();
    }

    private static bool _watchingDown;

    /// <summary>A local player who goes down holds no input; one who leaves the level loses every lease at level cleanup.</summary>
    private static void WatchDown()
    {
        if (_watchingDown) return;
        GameEvents.LocalPlayerDown += static () => ReleaseAll();
        _watchingDown = true;
    }

    private static bool Blocks(InputAction action)
        => _wrapped && Book.Blocks((int)action) && FocusStateManager.CurrentState is not (eFocusState.MainMenu or eFocusState.Map or eFocusState.GlobalPopupMessage);

    /// <summary>Ends every lease whose guard says its owner is gone, before the query is answered.</summary>
    private static void Admit(string member)
    {
        if (!Book.HasGuard()) return;
        foreach (var entry in Book.Snapshot())
        {
            if (entry.Guard == null) continue;
            string? reason;
            try { reason = entry.Guard(); }
            catch (Exception error) { reason = "lifecycle-guard-threw:" + error; }
            if (reason == null) continue;
            if (Book.Release(entry)) Apply();
            try { entry.Invalidated?.Invoke(reason + ";query=" + member); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("core-input-lease-guards-end-callback-threw", "An input lease guard's end callback threw: " + error); }
        }
    }

    private static bool Pressed(InputAction action, string member, out bool value)
    {
        value = false;
        var frame = UnityEngine.Time.frameCount;
        if (!_wrapped || !Book.Presses((int)action, frame)) return false;
        Admit(member);
        if (!Book.Presses((int)action, frame)) return false;
        value = member switch
        {
            "GetButton" => Book.Held((int)action),
            "GetButtonDown" => Book.Down((int)action, UnityEngine.Time.frameCount),
            _ => Book.Up((int)action, UnityEngine.Time.frameCount)
        };
        return true;
    }

    private static bool Drives(InputAction action, out float value)
    {
        value = 0f;
        if (!_wrapped || !Book.Drives((int)action)) return false;
        Admit("GetAxis");
        if (!Book.Drives((int)action)) return false;
        value = Book.Axis((int)action);
        return true;
    }

    private static float Steered(InputAction action)
        => action == InputAction.MoveHorizontal ? Book.Steered(true) : action == InputAction.MoveVertical ? Book.Steered(false) : 0f;

    private static readonly bool[] Off = new bool[InputLeaseBook.Interactions], Intent = new bool[InputLeaseBook.Interactions];
    private static bool _writing;

    private static bool Read(int index) => index switch
    {
        0 => PlayerInteraction.CameraRayInteractionEnabled,
        1 => PlayerInteraction.SphereCheckInteractionEnabled,
        _ => PlayerInteraction.LadderInteractionEnabled
    };

    private static void Write(int index, bool value)
    {
        _writing = true;
        try
        {
            switch (index)
            {
                case 0: PlayerInteraction.CameraRayInteractionEnabled = value; break;
                case 1: PlayerInteraction.SphereCheckInteractionEnabled = value; break;
                default: PlayerInteraction.LadderInteractionEnabled = value; break;
            }
        }
        finally { _writing = false; }
    }

    /// <summary>Puts each interaction switch in the state the leases ask for: off while any lease holds it, back to the latest value
    /// the game or another system gave it (<see cref="InteractionWritten"/>) once none does.</summary>
    private static void SyncInteractions()
    {
        for (var index = 0; index < InputLeaseBook.Interactions; index++)
        {
            var wanted = Book.InteractionOff(index);
            if (wanted == Off[index]) continue;
            if (wanted)
            {
                Patches.Ensure(Modules.InteractionSwitches, typeof(InteractionSwitchHook));
                Intent[index] = Read(index);
                Off[index] = true;
                Write(index, false);
            }
            else
            {
                Off[index] = false;
                Write(index, Intent[index]);
            }
        }
    }

    /// <summary>A write to a switch from anywhere but this lease: while the switch is held off it is remembered as the value to restore and
    /// the game keeps reading false.</summary>
    internal static bool InteractionWritten(int index, bool value)
    {
        if (_writing || !Off[index]) return value;
        Intent[index] = value;
        return false;
    }

    private static void Apply()
    {
        SyncInteractions();
        if (Book.Engaged == _wrapped) return;
        if (Book.Engaged) Wrap();
        else Unwrap();
    }

    private static void Wrap()
    {
        _button = InputMapper.GetButton;
        _down = InputMapper.GetButtonDown;
        _up = InputMapper.GetButtonUp;
        _axis = InputMapper.GetAxis;
        if (_button == null || _down == null || _up == null || _axis == null) throw new InvalidOperationException("The game's input delegates are not available.");
        var button = _button; var down = _down; var up = _up; var axis = _axis;
        _ownButton = DelegateSupport.ConvertDelegate<InputMapper.DelGetButton>(new Func<InputAction, eFocusState, bool>((a, f) => Pressed(a, "GetButton", out var v) ? v : !Blocks(a) && button.Invoke(a, f)));
        _ownDown = DelegateSupport.ConvertDelegate<InputMapper.DelGetButton>(new Func<InputAction, eFocusState, bool>((a, f) => Pressed(a, "GetButtonDown", out var v) ? v : !Blocks(a) && down.Invoke(a, f)));
        _ownUp = DelegateSupport.ConvertDelegate<InputMapper.DelGetButton>(new Func<InputAction, eFocusState, bool>((a, f) => Pressed(a, "GetButtonUp", out var v) ? v : !Blocks(a) && up.Invoke(a, f)));
        _ownAxis = DelegateSupport.ConvertDelegate<InputMapper.DelGetAxis>(new Func<InputAction, eFocusState, float>((a, f) => Drives(a, out var v) ? v : Blocks(a) ? Steered(a) : axis.Invoke(a, f)));
        InputMapper.GetButton = _ownButton;
        InputMapper.GetButtonDown = _ownDown;
        InputMapper.GetButtonUp = _ownUp;
        InputMapper.GetAxis = _ownAxis;
        _wrapped = true;
    }

    /// <summary>The wrappers forward everything once unwrapped, so one left in place because another mod wrapped over it is harmless.</summary>
    private static void Unwrap()
    {
        _wrapped = false;
        Restore(InputMapper.GetButton, _ownButton, _button, "GetButton", v => InputMapper.GetButton = v);
        Restore(InputMapper.GetButtonDown, _ownDown, _down, "GetButtonDown", v => InputMapper.GetButtonDown = v);
        Restore(InputMapper.GetButtonUp, _ownUp, _up, "GetButtonUp", v => InputMapper.GetButtonUp = v);
        Restore(InputMapper.GetAxis, _ownAxis, _axis, "GetAxis", v => InputMapper.GetAxis = v);
        _button = _down = _up = _ownButton = _ownDown = _ownUp = null;
        _axis = _ownAxis = null;
    }

    private static void Restore<T>(T? current, T? owned, T? original, string name, Action<T?> set) where T : Il2CppSystem.Object
    {
        if (Same(current, owned)) set(original);
        else if (!Same(current, original))
            Plugin.Logger.ErrorOnce("core-input-delegate-wrapped-over-by-another", "Input delegate " + name + " was wrapped over by another mod while input was leased; left as that mod set it (ForgeCore's wrapper now forwards everything).");
    }

    private static bool Same(Il2CppSystem.Object? left, Il2CppSystem.Object? right)
        => ReferenceEquals(left, right) || left != null && right != null && left.Pointer == right.Pointer;
}
