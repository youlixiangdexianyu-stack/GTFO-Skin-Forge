using System;
using System.Collections.Generic;
using CellMenu;
using UnityEngine;

namespace ForgeUI;

public sealed partial class UIKitMod
{
    /// <summary>
    /// Asks the player a yes-or-no question in one shared window: in a level a cockpit panel in the middle of the screen answered with
    /// ForgeUI's Confirm and Cancel keys (default Y and N, rebindable under Controls), in the lobby and menus the game's own pop-up
    /// window with two buttons (the same keys work there too; a click outside the window cancels). One question shows at a time; up to
    /// four wait behind it in the order asked, and one asked while four wait is refused at once (<see cref="ConfirmAnswer.Dismissed"/>).
    /// <paramref name="answered"/> runs exactly once.
    /// </summary>
    /// <param name="title">The question, short.</param>
    /// <param name="body">A line or two under it; empty for none.</param>
    /// <param name="answered">Runs once with the answer.</param>
    /// <param name="confirm">The confirm button's word; null for the kit's "CONFIRM".</param>
    /// <param name="cancel">The cancel button's word; null for the kit's "CANCEL".</param>
    /// <example><c>ui.Confirm(UIText.Key("reset.title"), UIText.Key("reset.body"), answer =&gt; { if (answer == ConfirmAnswer.Confirm) Reset(); });</c></example>
    public ConfirmTicket Confirm(UIText title, UIText body, Action<ConfirmAnswer> answered, UIText? confirm = null, UIText? cancel = null)
    {
        if (answered == null) throw new ArgumentNullException(nameof(answered));
        return ConfirmDialogs.Ask(this, new ConfirmQuestion(this, title, body, answered, confirm, cancel));
    }
}

/// <summary>One question asked with <see cref="UIKitMod.Confirm"/>.</summary>
public readonly struct ConfirmTicket
{
    private readonly int _ticket;
    internal ConfirmTicket(int ticket) => _ticket = ticket;

    /// <summary>False when the queue was full and the question was refused.</summary>
    public bool Asked => _ticket > 0;

    /// <summary>Withdraws the question (answered <see cref="ConfirmAnswer.Dismissed"/>); false when it already ended.</summary>
    public bool Withdraw() => _ticket > 0 && ConfirmDialogs.Queue.Withdraw(_ticket);
}

internal sealed class ConfirmQuestion
{
    internal ConfirmQuestion(UIKitMod mod, UIText title, UIText body, Action<ConfirmAnswer> answered, UIText? confirm, UIText? cancel)
    {
        Mod = mod;
        Title = title;
        Body = body;
        Answered = answered;
        Yes = confirm;
        No = cancel;
    }

    internal readonly UIKitMod Mod;
    internal readonly UIText Title, Body;
    internal readonly UIText? Yes, No;
    internal readonly Action<ConfirmAnswer> Answered;

    internal string TitleText => Title.In(Mod.Texts);
    internal string BodyText => Body.In(Mod.Texts);
    internal string YesText => Yes != null ? Yes.In(Mod.Texts) : UIKit.Kit.Text("confirm.yes");
    internal string NoText => No != null ? No.In(Mod.Texts) : UIKit.Kit.Text("confirm.no");
}

/// <summary>
/// The shared confirmation: the queue (<see cref="ConfirmQueue{T}"/>), the keys, and the two places a question shows: the HUD panel
/// (<see cref="ConfirmView"/>) while in a level, the game's pop-up window on the menu bar (<see cref="ConfirmWindow"/>) elsewhere.
/// </summary>
internal static class ConfirmDialogs
{
    internal static readonly ConfirmQueue<ConfirmQuestion> Queue = new(Answered);
    private static KeyAction? _yes, _no;
    private static ConfirmView? _view;
    private static bool _keysTried;

    private static void Answered(ConfirmQuestion question, ConfirmAnswer answer)
    {
        try { question.Answered(answer); }
        catch (Exception error) { Plugin.Error("ui-confirmation-callback-failed", "Confirmation callback of '" + question.Mod.Id + "' failed: " + error); }
    }

    internal static ConfirmTicket Ask(UIKitMod mod, ConfirmQuestion question)
    {
        Prepare();
        return new ConfirmTicket(Queue.Ask(mod.Id, question));
    }

    internal static void RegisterKeys()
    {
        if (_keysTried) return;
        _keysTried = true;
        MenuBar.PopupsHidden += ConfirmWindow.BarHidPopups;
        _yes = UIKit.Kit.AddKeyAction("Confirm", "keys.confirm", "keys.group", "Y", KeyMode.Press, "confirm");
        _no = UIKit.Kit.AddKeyAction("Cancel", "keys.cancel", "keys.group", "N", KeyMode.Press, "confirm");
    }

    private static void Prepare()
    {
        RegisterKeys();
        _view ??= Hud.Add(ModIds.Join(Plugin.Id, "Confirm"), new ConfirmView(), new HudSlot(HudRegion.World));
    }

    internal static bool InLevel => GameStateManager.CurrentStateName == eGameStateName.InLevel;

    internal static string YesKey => _yes?.Name ?? "";
    internal static string NoKey => _no?.Name ?? "";

    /// <summary>Per frame: the keys answer the shown question where it is visible; the lobby window follows the queue.</summary>
    internal static void Update()
    {
        if (!Queue.Showing) { ConfirmWindow.Close(); return; }
        var inLevel = InLevel;
        if (inLevel) ConfirmWindow.Close();
        else ConfirmWindow.Follow();
        var visible = inLevel ? ConfirmView.Drawn && !HudFocus.Away : ConfirmWindow.Open;
        if (!visible) return;
        if (_yes != null && _yes.Pressed) Queue.Answer(ConfirmAnswer.Confirm);
        else if (_no != null && _no.Pressed) Queue.Answer(ConfirmAnswer.Cancel);
    }
}

/// <summary>The question in a level: a cockpit panel a little below the middle of the screen, the question, the lines under it and the
/// two keys. Sizes are 1080p pixels.</summary>
internal sealed class ConfirmView : HudWidget
{
    private const float Width = 520f, TitlePx = 20f, BodyPx = 15f, KeyPx = 14f, Line = 21f, Pad = 18f;
    private readonly List<string> _lines = new();
    private HudCanvas? _canvas;
    private int _version = -1;
    private string _language = "", _title = "", _keys = "";
    private int _keyVersion = -1;

    private static int _drawnFrame = -10;

    /// <summary>Drawn this frame or the last (the keys are read before the HUD draws).</summary>
    internal static bool Drawn => Time.frameCount - _drawnFrame <= 1;

    internal override void Build(HudElement hud) => _canvas = new HudCanvas(hud);

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null) return;
        var queue = ConfirmDialogs.Queue;
        if (!queue.Showing || HudFocus.Away || !ConfirmDialogs.InLevel)
        {
            if (_canvas.Begin()) _canvas.End();
            return;
        }
        if (!_canvas.Begin()) return;
        var u = _canvas.Unit;
        var question = queue.Current;
        if (_version != queue.Version || _language != UIKit.Language || _keyVersion != KeyAction.Version)
        {
            _version = queue.Version;
            _language = UIKit.Language;
            _keyVersion = KeyAction.Version;
            _title = question.TitleText;
            TextWrap.Lines(question.BodyText, BodyPx, Width - 2f * Pad, 6, _lines);
            _keys = "[" + ConfirmDialogs.YesKey + "] " + question.YesText + "      [" + ConfirmDialogs.NoKey + "] " + question.NoText;
        }
        var height = (Pad * 2f + Line + 8f + _lines.Count * Line + 12f + Line) * u;
        var width = Width * u;
        var top = -40f * u + height / 2f;
        _canvas.Plate(0f, top - height / 2f, width, height, 10f * u, UIKitColors.Faded(UIKitColors.Plate, 0.88f));
        _canvas.Frame(0f, top - height / 2f, width, height, 10f * u, UIKitColors.Faded(UIKitColors.Cockpit, 0.5f));
        _canvas.Rect(-width / 2f + 3f * u, top - height / 2f, 2f * u, height - 20f * u, UIKitColors.Accent);
        var left = -width / 2f + Pad * u;
        var y = top - (Pad + Line / 2f) * u;
        _canvas.Text(_title, left, y, TitlePx, UIKitColors.Bright, HudAlign.Left, true);
        y -= (Line + 8f) * u;
        for (var i = 0; i < _lines.Count; i++, y -= Line * u) _canvas.Text(_lines[i], left, y, BodyPx, UIKitColors.Faded(UIKitColors.Cockpit, 0.85f));
        y -= 12f * u;
        _canvas.Rect(0f, y + Line * 0.75f * u, width - 2f * Pad * u, u, UIKitColors.Faded(UIKitColors.Cockpit, 0.2f));
        _canvas.Text(_keys, width / 2f - Pad * u, y, KeyPx, UIKitColors.Accent, HudAlign.Right, true);
        _canvas.End();
        _drawnFrame = Time.frameCount;
    }
}

/// <summary>
/// The question outside a level: the game's pop-up window on the active menu bar (the one its exit questions use, made through
/// <see cref="MenuBar"/>), with the question as its heading, the body as text and two buttons copied from the bar's own item. A click
/// outside the window, or its close box, cancels.
/// </summary>
internal static class ConfirmWindow
{
    private static readonly Vector2 Size = new(760f, 300f);
    private static readonly Color Plate = new(0.05f, 0.06f, 0.07f, 1f);
    private static MenuBarPage? _page;
    private static CM_MenuBar? _bar;
    private static int _version = -1;
    private static bool _closing;

    internal static bool Open => _page != null && _page.IsOpen;

    /// <summary>Shows the current question on the active menu bar; rebuilt when the question changes.</summary>
    internal static void Follow()
    {
        var queue = ConfirmDialogs.Queue;
        if (_page != null && _version == queue.Version && _bar != null && !_bar.WasCollected && _bar.gameObject.activeInHierarchy) return;
        Close();
        var bar = MenuBar.ActiveBar();
        if (bar == null) return;
        try { Build(bar, queue.Current); }
        catch (Exception error)
        {
            Close();
            Plugin.Error("ui-confirmation-window-could-not-be-opened", "The confirmation window could not be opened (the question waits): " + error);
            return;
        }
        _version = queue.Version;
    }

    private static void Build(CM_MenuBar bar, ConfirmQuestion question)
    {
        _bar = bar;
        var window = MenuBar.CreateWindow(bar, RichText.Plain(question.TitleText), Size, out var root, out var size);
        _page = new MenuBarPage("ForgeUI/Confirm", question.Mod.Texts, window, root, size, Cancelled, null) { Bar = bar };
        // An opaque plate under the words (the game's window is see-through), and the whole window above any screen it opens over.
        _page.AddRect(-24f, 24f, size.x + 48f, size.y + 48f, Plate);
        var body = question.BodyText;
        if (body.Length > 0) _page.AddText(UIText.Plain(body));
        var noWidth = AddButton(question.NoText, size.x - 20f, -size.y + 10f, ConfirmAnswer.Cancel);
        AddButton(question.YesText, size.x - 20f - noWidth - 40f, -size.y + 10f, ConfirmAnswer.Confirm);
        _page.Show();
        _page.Raise(20);
    }

    /// <summary>A page button right-aligned at (<paramref name="right"/>, <paramref name="bottom"/>) in the content; returns its width.</summary>
    private static float AddButton(string text, float right, float bottom, ConfirmAnswer answer)
    {
        var item = _page!.AddButton(UIText.Plain(RichText.Plain(text)), 0f, 0f, () => Answer(answer), 320f);
        var width = item.GetSize().x;
        item.transform.localPosition = new Vector3(right - width, bottom + 30f, 0f);
        return width;
    }

    private static void Answer(ConfirmAnswer answer)
    {
        Close();
        ConfirmDialogs.Queue.Answer(answer);
    }

    /// <summary>The window closed without a button (its close box, or a click outside it): the question is cancelled.</summary>
    private static void Cancelled()
    {
        if (_closing) return;
        Close();
        ConfirmDialogs.Queue.Answer(ConfirmAnswer.Cancel);
    }

    /// <summary>The bar hid its pop-ups (a click outside the window, or the game opening another).</summary>
    internal static void BarHidPopups(CM_MenuBar bar)
    {
        if (_page == null || _bar == null || _bar.Pointer != bar.Pointer || !_page.IsOpen) return;
        _page.Close();
    }

    internal static void Close()
    {
        if (_page == null) return;
        _closing = true;
        try
        {
            _page?.Destroy();
        }
        catch (Exception error) { Plugin.Error("ui-closing-confirmation-window-failed", "Closing the confirmation window failed: " + error); }
        finally
        {
            _page = null;
            _bar = null;
            _version = -1;
            _closing = false;
        }
    }
}
