using System;
using GameData;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>Why a screen-text slot could not be written.</summary>
public enum ScreenTextRefusal
{
    None,
    /// <summary>The local player's GUI layer, or the game object the slot writes into, does not exist.</summary>
    NoLayer,
    /// <summary>The game, or another writer, is using the slot.</summary>
    Busy,
    /// <summary>The slot's text field is absent, so a colour cannot be applied.</summary>
    ColorUnsupported
}

/// <summary>
/// The status-bar readout lost its text to another writer between two of this slot's writes. The slot has already
/// handed the field back and holds nothing.
/// </summary>
public sealed class ScreenTextLostException() : InvalidOperationException("The screen text was changed by another writer.");

internal static class ScreenTextWrite
{
    /// <summary>Writes the text and the colour and refuses a field that did not retain them.</summary>
    internal static void Set(TextMeshPro? text, string body, Color? color)
    {
        if (text == null) return;
        text.SetText(body);
        if (color is { } value) text.color = value;
        if (!string.Equals(text.text, body, StringComparison.Ordinal)
            || color is { } expected && !text.color.Equals(expected))
            throw new InvalidOperationException("HUD text did not retain the requested value.");
    }
}

/// <summary>
/// The centre-top readout of the screen: the game's objective timer, borrowed while the game is not using it. One
/// slot holds at most one borrow. The borrow is a receipt of what the game's title and text fields held and what
/// this slot wrote, so it is given back only while the fields still read what was written, and it is dropped
/// (never fought over) when the game starts using the timer or another writer changes it. A hide still takes down
/// the frame this slot activated, unless the game is showing the timer itself.
/// </summary>
public sealed class ScreenTimer
{
    private Borrow? _held;

    /// <summary>Whether this slot holds the timer.</summary>
    public bool Holding => _held != null;

    /// <summary>Checks, without writing, that the timer can be written now. <paramref name="owned"/> says the
    /// caller already holds it through this slot.</summary>
    public ScreenTextRefusal Check(bool owned)
    {
        var layer = GuiManager.Current?.m_playerLayer;
        var timer = layer?.m_objectiveTimer;
        if (timer == null) return ScreenTextRefusal.NoLayer;
        if (layer!.TimerOnMainScreenVisible || (timer.Visible && !owned)) return ScreenTextRefusal.Busy;
        return ScreenTextRefusal.None;
    }

    /// <summary>Drops the borrow when the game took the timer over or another writer changed its text. Returns
    /// true when a borrow was dropped. Existing host-only objective hooks cannot observe every client's timer
    /// activation, so this is checked at command entry and adds no frame poll.</summary>
    public bool Reconcile()
    {
        if (_held is not { } held) return false;
        if (held.Matches(GuiManager.Current?.m_playerLayer)) return false;
        _held = null;
        return true;
    }

    /// <summary>Shows a title and a text on the timer, borrowing it on first use. Call after <see cref="Check"/>.</summary>
    public ScreenTextRefusal Show(string title, string body, Color? color)
    {
        var timer = GuiManager.Current?.m_playerLayer?.m_objectiveTimer;
        if (timer == null) return ScreenTextRefusal.NoLayer;
        if (timer.m_timerText == null) return color.HasValue ? ScreenTextRefusal.ColorUnsupported : ScreenTextRefusal.NoLayer;
        _held ??= new Borrow(timer);
        timer.SetTimerActive(true, false);
        timer.UpdateTimerTitle(title);
        timer.SetTimerTextEnabled(true);
        // The timer has only a countdown formatter (UpdateTimerText(float, float, Color)); an arbitrary text goes to
        // its TMP field directly, without driving countdown sounds or counters.
        ScreenTextWrite.Set(timer.m_timerText, body, color);
        _held.Capture(title, body);
        return ScreenTextRefusal.None;
    }

    /// <summary>Writes a new title and text into the borrow already held (a language refresh). False when the
    /// borrow was dropped instead.</summary>
    public bool Retext(string title, string body)
    {
        if (Reconcile() || _held is not { } held) return false;
        held.Timer.UpdateTimerTitle(title);
        ScreenTextWrite.Set(held.Timer.m_timerText, body, null);
        held.Capture(title, body);
        return true;
    }

    /// <summary>Gives the timer back and holds nothing. A frame this slot activated is taken down even when the
    /// game or another writer has since changed the fields it wrote, because the activation is still this slot's;
    /// a timer the game is showing itself is left alone exactly as a write is.</summary>
    public void Hide()
    {
        if (_held is not { } held) return;
        _held = null;
        var layer = GuiManager.Current?.m_playerLayer;
        if (held.Matches(layer)) held.RestoreText();
        else if (held.Timer.WasCollected || layer == null || layer.m_objectiveTimer != held.Timer
            || layer.TimerOnMainScreenVisible || held.Timer.Visible) return;
        held.Timer.SetTimerActive(false, false);
    }

    /// <summary>The title the game's field shows now, or null when this slot no longer holds the timer.</summary>
    public string? ReadTitle()
        => _held is { } held && held.Matches(GuiManager.Current?.m_playerLayer) ? held.Timer.m_titleText.text : null;

    private sealed class Borrow
    {
        internal readonly PUI_ObjectiveTimer Timer;
        private readonly string _originalTitle, _originalBody;
        private float _lastTimer;
        private int _seconds;
        private string _title = "", _body = "";

        internal Borrow(PUI_ObjectiveTimer timer)
        {
            Timer = timer;
            _lastTimer = timer.m_lastSetTimer;
            _seconds = timer.m_secondsCounter;
            _originalTitle = timer.m_titleText?.text ?? "";
            _originalBody = timer.m_timerText?.text ?? "";
        }

        internal void Capture(string title, string body)
        {
            _title = title; _body = body;
            _lastTimer = Timer.m_lastSetTimer; _seconds = Timer.m_secondsCounter;
        }

        // `Visible` is not read: it is the layer's own `SetVisible` flag, which `SetTimerActive` never sets, so a
        // borrow showing on screen reads false there and would be dropped before it could be given back.
        internal bool Matches(PlayerGuiLayer? layer)
            => layer != null && Timer != null && !Timer.WasCollected && layer.m_objectiveTimer == Timer
                && !layer.TimerOnMainScreenVisible
                && Timer.m_lastSetTimer == _lastTimer && Timer.m_secondsCounter == _seconds
                && Timer.m_titleText != null && !Timer.m_titleText.WasCollected
                && Timer.m_timerText != null && !Timer.m_timerText.WasCollected
                && string.Equals(Timer.m_titleText.text, _title, StringComparison.Ordinal)
                && string.Equals(Timer.m_timerText.text, _body, StringComparison.Ordinal);

        internal void RestoreText()
        {
            if (!Matches(GuiManager.Current?.m_playerLayer)) return;
            Timer.UpdateTimerTitle(_originalTitle);
            Timer.m_timerText!.SetText(_originalBody);
        }
    }
}

/// <summary>
/// The game's interaction message line with its progress bar (the one a scan or a reactor step writes with
/// <c>SetMessage</c> and <c>SetMessageTimer</c>), borrowed while the game is not showing it. One slot holds at most
/// one claim. The game's own hold-to-use prompt is a different piece of the same layer and is never touched.
/// </summary>
public sealed class ScreenProgress
{
    private bool _held;

    /// <summary>Whether this slot holds the message line.</summary>
    public bool Holding => _held;

    /// <summary>Checks, without writing, that the line can be written now. <paramref name="owned"/> says the caller
    /// already holds it through this slot.</summary>
    public ScreenTextRefusal Check(bool owned)
    {
        var layer = GuiManager.InteractionLayer;
        if (layer == null || layer.WasCollected) return ScreenTextRefusal.NoLayer;
        if (_held && !layer.MessageVisible) _held = false;
        return !owned && (layer.MessageVisible || layer.MessageTimerVisible) ? ScreenTextRefusal.Busy : ScreenTextRefusal.None;
    }

    /// <summary>Shows <paramref name="text"/> with the bar filled to <paramref name="fraction"/> (0..1). Call after <see cref="Check"/>.</summary>
    public ScreenTextRefusal Show(string text, float fraction)
    {
        var layer = GuiManager.InteractionLayer;
        if (layer == null || layer.WasCollected) return ScreenTextRefusal.NoLayer;
        layer.SetMessage(text, ePUIMessageStyle.Default);
        layer.MessageVisible = true;
        layer.SetMessageTimer(Mathf.Clamp01(fraction));
        layer.MessageTimerVisible = true;
        _held = true;
        return ScreenTextRefusal.None;
    }

    /// <summary>Takes the line down if this slot put it up; leaves the game's own line alone otherwise.</summary>
    public void Hide()
    {
        if (!_held) return;
        _held = false;
        var layer = GuiManager.InteractionLayer;
        if (layer == null || layer.WasCollected) return;
        layer.MessageTimerVisible = false;
        layer.MessageVisible = false;
    }
}

/// <summary>
/// The local player's shield readout beside the health bar: its bar and its text field. A slot holds at most one
/// receipt of the text field (its original text, colour, size, wrapping, auto-sizing and overflow), writes the one
/// line the caller gives, and gives the field back only while it still reads what was written.
/// </summary>
public sealed class ScreenStatus
{
    private Borrow? _held;

    /// <summary>Whether this slot holds the text field.</summary>
    public bool Holding => _held != null;

    /// <summary>Whether the local player's status object exists.</summary>
    public static bool LayerPresent => GuiManager.Current?.m_playerLayer?.m_playerStatus != null;

    /// <summary>Whether the status object has a shield text field.</summary>
    public static bool TextPresent => GuiManager.Current?.m_playerLayer?.m_playerStatus?.m_shieldText != null;

    /// <summary>Shows or hides the shield readout. The bar is always given the value (the game's own
    /// normalisation); a non-null <paramref name="line"/> is also written to the text field, a null one leaves the
    /// text field to the game.</summary>
    /// <exception cref="ScreenTextLostException">The text field was changed by another writer; the slot gave it back.</exception>
    public void Write(bool visible, double value, string? line, Color? color)
    {
        var status = GuiManager.Current?.m_playerLayer?.m_playerStatus
            ?? throw new InvalidOperationException("The local player status is unavailable.");
        if (_held != null && _held.Text == status.m_shieldText && !_held.OwnsCurrent())
        {
            _held.Restore();
            _held = null;
            throw new ScreenTextLostException();
        }
        if (_held != null && (_held.Text != status.m_shieldText || !visible || line == null))
        {
            _held.Restore();
            _held = null;
        }
        if (status.m_shieldUIParent != null) status.m_shieldUIParent.SetActive(visible);
        if (!visible) return;
        status.UpdateShield((float)value);
        if (line == null) return;
        var text = status.m_shieldText;
        if (text == null) return;
        if (_held != null && _held.Text != text)
        {
            _held.Restore();
            _held = null;
        }
        else _held?.AcceptNativeUpdate();
        _held ??= new Borrow(text);
        _held.Apply(line, Available(status), color);
        ScreenTextWrite.Set(text, line, color);
        _held.Capture(line, color);
    }

    /// <summary>Writes a new line into the field already held (a language refresh). False when the field no longer
    /// reads what was written; the slot has then given it back and holds nothing.</summary>
    public bool Retext(string line)
    {
        if (_held is not { } held) return false;
        var status = GuiManager.Current?.m_playerLayer?.m_playerStatus;
        if (status?.m_shieldText != held.Text || !held.OwnsCurrent())
        {
            held.Restore();
            _held = null;
            return false;
        }
        held.Apply(line, Available(status), null);
        ScreenTextWrite.Set(held.Text, line, null);
        held.Capture(line, null);
        return true;
    }

    /// <summary>The text the game's field shows now, or null when this slot no longer holds it.</summary>
    public string? ReadText()
    {
        if (_held is not { } held) return null;
        var native = GuiManager.Current?.m_playerLayer?.m_playerStatus;
        return native?.m_shieldText != held.Text || !held.OwnsCurrent() ? null : native.m_shieldText.text;
    }

    /// <summary>Gives the text field back and holds nothing.</summary>
    public void Release()
    {
        _held?.Restore();
        _held = null;
    }

    // Measure native glyphs, not character counts. Use the shield group's explicit width when present; otherwise
    // size to the text. TMP handles overflow, without a custom renderer or font-size heuristic.
    private static float Available(PUI_LocalPlayerStatus status)
        => status.m_shieldUIParent?.GetComponent<RectTransform>()?.sizeDelta.x ?? 0;

    private static float Width(float preferredWidth, float availableWidth)
    {
        float width = MathF.Ceiling(preferredWidth);
        return availableWidth > 0 ? MathF.Min(width, availableWidth) : width;
    }

    private sealed class Borrow
    {
        internal readonly TextMeshPro Text;
        private ForgeVisual.FieldReceipt<Vector2> _size;
        private ForgeVisual.FieldReceipt<bool> _wrap, _autoSize;
        private ForgeVisual.FieldReceipt<TextOverflowModes> _overflow;
        private ForgeVisual.FieldReceipt<Color> _color;
        private readonly string _originalText;
        private string _lastText;
        private bool _textWritten, _textLost;

        internal Borrow(TextMeshPro text)
        {
            Text = text;
            _originalText = _lastText = text.text;
            _size = new(text.rectTransform.sizeDelta, static (a, b) => a.x.Equals(b.x) && a.y.Equals(b.y));
            _wrap = new(text.enableWordWrapping);
            _autoSize = new(text.enableAutoSizing);
            _overflow = new(text.overflowMode);
            _color = new(text.color);
        }

        internal bool OwnsCurrent()
        {
            if (Text == null || Text.WasCollected) return false;
            if (!string.Equals(Text.text, _lastText, StringComparison.Ordinal)) _textLost = true;
            return !_textLost;
        }

        /// <summary>The text the game's own shield updater writes is part of this write, so it becomes the
        /// receipt's new accepted value before the caller's line replaces it. A language refresh never calls this.</summary>
        internal void AcceptNativeUpdate()
        {
            if (Text == null || Text.WasCollected || _textLost) return;
            _lastText = Text.text;
        }

        internal void Apply(string line, float available, Color? color)
        {
            if (!OwnsCurrent() || color.HasValue && !_color.Accepts(Text.color))
                throw new ScreenTextLostException();
            if (_wrap.Accepts(Text.enableWordWrapping)) { Text.enableWordWrapping = false; _wrap.Written(Text.enableWordWrapping); }
            if (_autoSize.Accepts(Text.enableAutoSizing)) { Text.enableAutoSizing = false; _autoSize.Written(Text.enableAutoSizing); }
            if (_size.Accepts(Text.rectTransform.sizeDelta))
            {
                Text.rectTransform.sizeDelta = new Vector2(Width(Text.GetPreferredValues(line).x, available), Text.rectTransform.sizeDelta.y);
                _size.Written(Text.rectTransform.sizeDelta);
            }
            if (_overflow.Accepts(Text.overflowMode)) { Text.overflowMode = TextOverflowModes.Truncate; _overflow.Written(Text.overflowMode); }
        }

        internal void Capture(string line, Color? color)
        {
            _lastText = line; _textWritten = true;
            if (color.HasValue && Text.color.Equals(color.Value)) _color.Written(Text.color);
        }

        internal void Restore()
        {
            if (Text == null || Text.WasCollected) return;
            if (OwnsCurrent() && _textWritten) Text.SetText(_originalText);
            if (_color.Release(Text.color)) Text.color = _color.Original;
            if (_size.Release(Text.rectTransform.sizeDelta)) Text.rectTransform.sizeDelta = _size.Original;
            if (_wrap.Release(Text.enableWordWrapping)) Text.enableWordWrapping = _wrap.Original;
            if (_autoSize.Release(Text.enableAutoSizing)) Text.enableAutoSizing = _autoSize.Original;
            if (_overflow.Release(Text.overflowMode)) Text.overflowMode = _overflow.Original;
        }
    }
}

/// <summary>
/// One line on the game's warden-intel feed, put there through the level-event entry the game itself uses (so the
/// feed's own queue, delay and duration apply). The returned handle is a receipt of the exact feed run the line
/// started: it stays valid only while the same widget still shows the same disable coroutine, so a later vanilla
/// line, or any other writer, revokes it.
/// </summary>
public sealed class ScreenMessage
{
    /// <summary>The seconds the feed keeps a line up when the event names no duration:
    /// <c>PlayerGuiLayer.ShowWardenIntel(string, float delay = 0, float duration = 6)</c>.</summary>
    public const float DefaultSeconds = 6f;

    private readonly IntPtr _widget, _disableCoroutine;
    private string _text;

    private ScreenMessage(string text, IntPtr widget, IntPtr disableCoroutine)
    { _text = text; _widget = widget; _disableCoroutine = disableCoroutine; }

    /// <summary>The line the handle last wrote.</summary>
    public string Text => _text;

    /// <summary>Runs the game's level event that shows <paramref name="text"/> on the feed. A positive
    /// <paramref name="seconds"/> sets the duration; null leaves the game's own. Returns the handle when the feed
    /// now visibly shows exactly this line, otherwise null (the event ran but the run cannot be tracked).
    /// The game's call is made once, so a throw from it is the caller's to contain.</summary>
    public static ScreenMessage? Show(string text, float? seconds)
    {
        var data = new WardenObjectiveEventData { Type = eWardenObjectiveEventType.None };
        data.Condition = new WorldEventConditionPair { ConditionIndex = -1 };
        data.WardenIntel = GameText.Localized(text);
        if (seconds is { } duration) data.Duration = duration;
        WorldEventManager.ExecuteEvent(data, 0f);
        return Capture(text);
    }

    /// <summary>Whether the feed's widget exists to take a line.</summary>
    public static bool Available => WorldEventManager.Current is not null;

    private static ScreenMessage? Capture(string text)
    {
        var widget = GuiManager.Current?.m_playerLayer?.m_wardenIntel;
        var label = widget?.m_intelText;
        var disable = widget?.m_disableCoroutine;
        if (widget == null || widget.WasCollected || !widget.IsVisible || label == null || label.WasCollected
            || !string.Equals(label.text, text, StringComparison.Ordinal)
            || disable == null || disable.WasCollected) return null;
        return new ScreenMessage(text, widget.Pointer, disable.Pointer);
    }

    /// <summary>The label the feed shows now, or null when the run is no longer this handle's.</summary>
    public string? ReadText() => Widget(out _, out var label) ? label!.text : null;

    /// <summary>Changes the label of the same run (a language refresh): unlike showing a new line, it takes no
    /// delay or duration and does not replace the run's disable coroutine. False when the run is no longer this
    /// handle's or its label was changed by another writer; the handle is then dead.</summary>
    public bool TrySetText(string text)
    {
        if (!Widget(out var widget, out var label)
            || !string.Equals(label!.text, _text, StringComparison.Ordinal)) return false;
        if (!string.Equals(text, _text, StringComparison.Ordinal)) widget!.SetIntelText(text);
        _text = text;
        return true;
    }

    private bool Widget(out PUI_WardenIntel? widget, out TextMeshPro? label)
    {
        widget = GuiManager.Current?.m_playerLayer?.m_wardenIntel;
        label = widget?.m_intelText;
        var disable = widget?.m_disableCoroutine;
        return widget != null && !widget.WasCollected && widget.Pointer == _widget && widget.IsVisible
            && label != null && !label.WasCollected && disable != null && !disable.WasCollected
            && disable.Pointer == _disableCoroutine;
    }
}
