using System;

namespace ForgeUI;

/// <summary>The geometry of a radial wheel, without Unity. Positions are relative to the wheel's centre in units of its outer radius
/// (x right, y up); slot 0 is at the top and slots go clockwise, each the same share of the circle.</summary>
internal static class WheelMath
{
    /// <summary>The slot under (<paramref name="x"/>, <paramref name="y"/>) by its angle alone, or -1 inside the centre's
    /// <paramref name="deadZone"/> (a radius, 0..1) or with no slots.</summary>
    public static int Slot(float x, float y, int count, float deadZone)
    {
        if (count <= 0 || x * x + y * y <= deadZone * deadZone) return -1;
        var degrees = MathF.Atan2(x, y) * 57.29578f;
        if (degrees < 0f) degrees += 360f;
        var width = 360f / count;
        return (int)MathF.Floor((degrees + width / 2f) / width) % count;
    }

    /// <summary>The unit direction of slot <paramref name="slot"/>'s middle.</summary>
    public static (float X, float Y) Direction(int slot, int count)
    {
        var radians = slot * MathF.PI * 2f / Math.Max(1, count);
        return (MathF.Sin(radians), MathF.Cos(radians));
    }

    /// <summary>(<paramref name="x"/>, <paramref name="y"/>) pulled back onto the circle of <paramref name="radius"/> when it is
    /// outside it.</summary>
    public static (float X, float Y) Clamp(float x, float y, float radius)
    {
        var length = MathF.Sqrt(x * x + y * y);
        if (length <= radius || length <= 0f) return (x, y);
        return (x / length * radius, y / length * radius);
    }

    /// <summary>Moves <paramref name="value"/> towards <paramref name="target"/> (0 or 1) so a full change takes
    /// <paramref name="seconds"/>.</summary>
    public static float Approach(float value, float target, float dt, float seconds)
    {
        if (seconds <= 0f) return target;
        var step = dt / seconds;
        return value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
    }
}

/// <summary>What one frame of a wheel did (<see cref="WheelControl.Step"/>).</summary>
internal enum WheelStep : byte
{
    None,
    Opened,
    /// <summary>An item was chosen (<see cref="WheelControl.ChosenPage"/>, <see cref="WheelControl.ChosenSlot"/>); the wheel closed.</summary>
    Chosen,
    /// <summary>The wheel closed without a choice.</summary>
    Closed,
}

/// <summary>One frame of input for a wheel.</summary>
internal struct WheelInput
{
    /// <summary>The key opened it (the key action started while the wheel was closed).</summary>
    public bool Open;
    /// <summary>The key ended it: released for a <see cref="KeyMode.Hold"/> key, started again for any other mode.</summary>
    public bool End;
    /// <summary>The left button went down.</summary>
    public bool Click;
    /// <summary>The right button or Esc went down.</summary>
    public bool Cancel;
    /// <summary>A number key 1..8 went down (0 for none).</summary>
    public int Number;
    /// <summary>The scroll wheel moved by this many notches (positive towards the next page).</summary>
    public int Scroll;
    /// <summary>The pointer, relative to the centre in units of the outer radius.</summary>
    public float X, Y;
    /// <summary>The wheel cannot stay open (the game's own screen, the player down); it closes without a choice.</summary>
    public bool Lost;
}

/// <summary>
/// The rules of a radial wheel, without Unity: the key opens it; the slot under the pointer is the hovered one; ending the key (see
/// <see cref="WheelInput.End"/>) or a left click chooses the hovered slot and closes the wheel; ending the key in the centre's dead
/// zone closes it without a choice, a left click there does nothing; the right button or Esc closes it without a choice; a number
/// key chooses that slot at once; the scroll wheel turns the page. A slot that is empty or not usable is never chosen: a click on it
/// does nothing and ending the key on it closes without a choice.
/// </summary>
internal sealed class WheelControl
{
    private readonly Func<int, int> _count;
    private readonly Func<int, int, bool> _usable;

    /// <param name="count">How many items page p has (0..8).</param>
    /// <param name="usable">Whether item (page, slot) can be chosen now.</param>
    public WheelControl(Func<int, int> count, Func<int, int, bool> usable)
    {
        _count = count ?? throw new ArgumentNullException(nameof(count));
        _usable = usable ?? throw new ArgumentNullException(nameof(usable));
    }

    public const int MaxSlots = 8;

    /// <summary>The centre's dead zone, a radius in units of the outer radius.</summary>
    public float DeadZone { get; set; } = 0.35f;

    public int Pages { get; set; } = 1;

    public bool IsOpen { get; private set; }

    /// <summary>The page shown; kept from one opening to the next.</summary>
    public int Page { get; set; }

    /// <summary>The slot under the pointer, or -1.</summary>
    public int Hovered { get; private set; } = -1;

    public int ChosenPage { get; private set; } = -1;
    public int ChosenSlot { get; private set; } = -1;

    public int Count => Pages > 0 ? Math.Clamp(_count(Page), 0, MaxSlots) : 0;

    public WheelStep Step(in WheelInput input)
    {
        if (!IsOpen)
        {
            if (!input.Open || input.Lost) return WheelStep.None;
            IsOpen = true;
            Page = Pages > 0 ? (Page % Pages + Pages) % Pages : 0;
            Hovered = -1;
            return WheelStep.Opened;
        }
        if (input.Lost || input.Cancel) return Close();
        if (input.Scroll != 0 && Pages > 1) Page = ((Page + input.Scroll) % Pages + Pages) % Pages;
        var count = Count;
        Hovered = WheelMath.Slot(input.X, input.Y, count, DeadZone);
        if (input.Number is >= 1 and <= MaxSlots)
        {
            var slot = input.Number - 1;
            if (slot < count && _usable(Page, slot)) return Choose(slot);
        }
        if (input.Click && Hovered >= 0 && _usable(Page, Hovered)) return Choose(Hovered);
        if (input.End) return Hovered >= 0 && _usable(Page, Hovered) ? Choose(Hovered) : Close();
        return WheelStep.None;
    }

    /// <summary>Closes it without a choice (also from outside, for example when the mod's own state ends).</summary>
    public WheelStep Close()
    {
        if (!IsOpen) return WheelStep.None;
        IsOpen = false;
        Hovered = -1;
        return WheelStep.Closed;
    }

    private WheelStep Choose(int slot)
    {
        ChosenPage = Page;
        ChosenSlot = slot;
        IsOpen = false;
        return WheelStep.Chosen;
    }
}
