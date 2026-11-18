namespace ForgeUI;

/// <summary>
/// A key that is on while held and stays on after a short tap: a tap (down and up within <see cref="TapSeconds"/>) flips the
/// latched state, holding it is on for as long as it is held. <see cref="HudPointer.Toggle"/> drives the HUD pointer with Control
/// this way; a mod can feed it any key of its own.
/// </summary>
public sealed class TapOrHold
{
    private bool _latched;
    private float _downAt = -1f;

    /// <summary>The longest press, in seconds, that counts as a tap.</summary>
    public float TapSeconds { get; set; } = 0.25f;

    /// <summary>On this frame: held, or latched by a tap.</summary>
    public bool On { get; private set; }

    /// <summary>Once per frame: whether the key is <paramref name="held"/>, went down (<paramref name="pressed"/>) or came up
    /// (<paramref name="released"/>) this frame, at <paramref name="now"/> seconds. Returns <see cref="On"/>.</summary>
    public bool Update(bool held, bool pressed, bool released, float now)
    {
        if (pressed) _downAt = now;
        if (released && _downAt >= 0f && now - _downAt < TapSeconds) _latched = !_latched;
        if (released) _downAt = -1f;
        On = held || _latched;
        return On;
    }

    /// <summary>Latches it on or off without the key (a button, a readback).</summary>
    public void Set(bool on)
    {
        _latched = on;
        _downAt = -1f;
        On = on;
    }
}
