using System;
using System.Collections.Generic;
using CellMenu;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace ForgeUI;

/// <summary>
/// Copies of the APPAREL button (<c>CM_PlayerLobbyBar.m_clothesButton</c>), one per registered button and bar, stacked under it.
/// Kept in step every frame: a copy exists for every bar, shows only when the APPAREL button shows and the registration's
/// visibility allows, and follows the APPAREL button's position, so players joining or leaving, a rebuilt page and a resolution
/// change all settle on their own. A copy keeps the button's click and size; its look is the lobby skin (<see cref="LobbyButtonSkin"/>):
/// a chamfered plate with a white outline, a white band along the cut corner, the title in the middle and the note at the right end.
/// </summary>
internal static class LobbyButtons
{
    private static readonly Registry<LobbyButton> Registered = new();
    /// <summary>Per bar, per button ID: the copy.</summary>
    private static readonly Dictionary<IntPtr, Dictionary<string, Copy>> Copies = new();
    private static readonly List<IntPtr> Gone = new();

    private sealed class Copy
    {
        internal GameObject Root = null!;
        internal IntPtr Route;
        internal LobbyButtonSkin? Skin;
        internal string? Title, Note, Tip;
        internal bool Hover, ShownHover, Drawn;
    }

    internal static LobbyButton? Add(LobbyButton button)
    {
        var problem = Registered.Add(button.Id, button);
        if (problem == null)
        {
            return button;
        }
        Plugin.Error("ui-lobby-button-not-added", "Lobby button not added: " + problem + ".");
        return null;
    }

    internal static void Remove(LobbyButton button)
    {
        if (!Registered.Remove(button.Id, button)) return;
        foreach (var perBar in Copies.Values)
            if (perBar.Remove(button.Id, out var copy)) Destroy(copy);
    }

    private static void Destroy(Copy copy)
    {
        Input.Unroute(copy.Route);
        if (copy.Root != null && !copy.Root.WasCollected)
        {
            copy.Root.SetActive(false);
            Object.Destroy(copy.Root);
        }
    }

    /// <summary>How many lobby buttons the bar shows now (lobby strips go under them).</summary>
    internal static int ShownOn(IntPtr bar)
    {
        if (!Copies.TryGetValue(bar, out var perBar)) return 0;
        var shown = 0;
        foreach (var copy in perBar.Values)
            if (copy.Root != null && !copy.Root.WasCollected && copy.Root.activeSelf) shown++;
        return shown;
    }
    internal static void Update()
    {
        // No button registered and no copy left to take down: nothing to look up.
        if (Copies.Count == 0 && Registered.Ordered.Count == 0) return;
        Gone.Clear();
        foreach (var (pointer, perBar) in Copies)
        {
            var alive = false;
            foreach (var copy in perBar.Values)
                if (copy.Root != null && !copy.Root.WasCollected) { alive = true; break; }
            if (!alive) Gone.Add(pointer);
        }
        foreach (var pointer in Gone)
        {
            foreach (var copy in Copies[pointer].Values) Destroy(copy);
            Copies.Remove(pointer);
        }
        var page = CM_PageLoadout.Current;
        if (page == null || page.WasCollected || Registered.Ordered.Count == 0) return;
        var bars = page.m_playerLobbyBars;
        if (bars == null) return;
        // Indexed: enumerating the native array would allocate an enumerator every frame.
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar == null || bar.WasCollected) continue;
            try { Sync(bar); }
            catch (Exception error) { Plugin.Error("ui-lobby-buttons-failed-update", "Lobby buttons failed to update: " + error); }
        }
    }

    private static void Sync(CM_PlayerLobbyBar bar)
    {
        var clothes = bar.m_clothesButton;
        if (clothes == null || clothes.WasCollected)
        {
            Plugin.Error("ui-lobby-buttons-cannot-be-shown-lobby", "Lobby buttons cannot be shown: a lobby bar has no APPAREL button to copy.");
            return;
        }
        if (!Copies.TryGetValue(bar.Pointer, out var perBar)) Copies[bar.Pointer] = perBar = new Dictionary<string, Copy>();
        var origin = clothes.transform.localPosition;
        var shown = clothes.gameObject.activeInHierarchy;
        var index = 0;
        var buttons = Registered.Ordered;
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            if (!Registered.Contains(button.Id, button)) continue;
            bool visible;
            try { visible = shown && (button.Visible?.Invoke(bar) ?? (bar.m_player != null && bar.m_player.IsLocal)); }
            catch (Exception error)
            {
                Plugin.Error("ui-lobby-button-visibility-check-failed", "Lobby button '" + button.Id + "' visibility check failed: " + error);
                visible = false;
            }
            if (!Registered.Contains(button.Id, button)) continue;
            string? note = null;
            if (visible) note = NoteThisFrame(button);
            // Both callbacks may remove this or a later registration, or replace an ID in our old snapshot.
            if (!Registered.Contains(button.Id, button)) continue;
            if (!perBar.TryGetValue(button.Id, out var copy) || copy.Root == null || copy.Root.WasCollected)
            {
                if (copy != null) Destroy(copy);
                perBar[button.Id] = copy = Create(bar, clothes, button);
            }
            if (copy.Root.activeSelf != visible) copy.Root.SetActive(visible);
            if (!visible) continue;
            var (x, y) = LobbyLayout.Offset(index++, Plugin.OffsetX, Plugin.OffsetY);
            var target = new Vector3(origin.x + x, origin.y + y, origin.z);
            if (copy.Root.transform.localPosition != target) copy.Root.transform.localPosition = target;
            var title = button.ShownTitle;
            var tip = copy.Hover ? button.ShownTooltip : null;
            if (copy.Drawn && copy.Title == title && copy.Note == note && copy.ShownHover == copy.Hover && copy.Tip == tip) continue;
            copy.Skin?.Draw(title, note, copy.Hover, tip);
            copy.Title = title;
            copy.Note = note;
            copy.Tip = tip;
            copy.ShownHover = copy.Hover;
            copy.Drawn = true;
        }
    }

    /// <summary>The note is read once per frame, however many bars show the button.</summary>
    private static string? NoteThisFrame(LobbyButton button)
    {
        if (button.Note == null) return null;
        if (button.NoteFrame == Time.frameCount) return button.NoteValue;
        button.NoteFrame = Time.frameCount;
        try { button.NoteValue = button.Note(); }
        catch (Exception error)
        {
            Plugin.Error("ui-lobby-button-note-failed", "Lobby button '" + button.Id + "' note failed: " + error);
            button.NoteValue = null;
        }
        return button.NoteValue;
    }

    private static Copy Create(CM_PlayerLobbyBar bar, CM_LobbyScrollItem clothes, LobbyButton button)
    {
        var root = Object.Instantiate(clothes.gameObject, clothes.transform.parent);
        root.name = "ForgeUI.LobbyButton." + button.Id;
        NativeUi.StripLocalization(root);
        var item = root.GetComponent<CM_LobbyScrollItem>() ?? throw new InvalidOperationException("the APPAREL button copy has no CM_LobbyScrollItem");
        item.m_onBtnPress = new UnityEvent();
        item.m_parentBar = clothes.m_parentBar;
        var copy = new Copy { Root = root };
        copy.Skin = LobbyButtonSkin.On(item);
        copy.Route = Input.Route(item, () =>
        {
            try { button.OnClick(bar); }
            catch (Exception error) { Plugin.Error("ui-lobby-button-click-failed", "Lobby button '" + button.Id + "' click failed: " + error); }
        }, inside => copy.Hover = inside);
        return copy;
    }
}

/// <summary>
/// The lobby look of a button copy: its own sprites and labels are switched off and a <see cref="MenuCanvas"/> draws a chamfered plate
/// (top left and bottom right cut) with a white outline, a white band along the top left cut, the title in the middle and the note
/// small and grey at the right end; under the pointer the plate lightens. Texts are shown as written.
/// </summary>
internal sealed class LobbyButtonSkin
{
    private static readonly Color Plate = new(0.03f, 0.04f, 0.047f, 0.86f), PlateHover = new(0.09f, 0.115f, 0.13f, 0.92f);
    private readonly MenuCanvas _canvas;
    private readonly Rect _box;

    private LobbyButtonSkin(MenuCanvas canvas, Rect box)
    {
        _canvas = canvas;
        _box = box;
    }

    internal static LobbyButtonSkin? On(CM_LobbyScrollItem item)
    {
        var sprites = item.GetComponentsInChildren<SpriteRenderer>(true);
        var labels = item.GetComponentsInChildren<TextMeshPro>(true);
        if (sprites.Length == 0 || labels.Length == 0)
        {
            Plugin.Error("ui-lobby-button-copy-has-no-sprite", "A lobby button copy has no sprite or text to draw with; it shows as the APPAREL button.");
            return null;
        }
        var order = MenuCanvas.TopOrder(item) + 1;
        foreach (var label in labels) NativeUi.SetText(label, "");
        var box = MenuCanvas.BoxIn(item.transform, item.m_collider ?? item.GetComponent<BoxCollider2D>(), new Rect(-225f, -27.5f, 450f, 55f));
        var canvas = new MenuCanvas(item.transform, "ForgeUI.ButtonLook", sprites[0], labels[0], order);
        // forceRenderingOff, not enabled: the game turns the button's sprites on again when its page comes back (intro, show).
        canvas.VeilOriginals();
        return new LobbyButtonSkin(canvas, box);
    }

    internal void Draw(string title, string? note, bool hover, string? tip)
    {
        var r = _box;
        var h = r.Height();
        var cut = Math.Min(14f, h * 0.26f);
        var c = _canvas;
        c.Begin();
        c.Plate(r, cut, hover ? PlateHover : Plate);
        c.Frame(r, cut, hover ? MenuLook.Line : MenuLook.Faded(MenuLook.Line, 0.92f));
        c.Picture(UIKitSprites.CornerBand, new Rect(r.XMin(), r.YMax() - 2f * cut, 2f * cut, 2f * cut), MenuLook.Line);
        var noteWidth = 0f;
        if (!string.IsNullOrEmpty(note)) noteWidth = c.Text(note!, r.XMax() - h * 0.4f, r.CenterY(), h * 0.34f, MenuLook.Dim, HudAlign.Right, r.Width() * 0.3f) + h * 0.3f;
        var room = r.Width() - 2f * Math.Max(noteWidth + h * 0.4f, 2f * cut);
        c.Text(title, r.CenterX(), r.CenterY(), h * 0.5f, MenuLook.Line, HudAlign.Centre, room);
        // The tooltip over the button, inside a band two buttons wide either side and a few buttons high.
        if (tip != null) MenuTooltip.Draw(c, r, tip, h * 0.36f, new Rect(r.XMin() - r.Width(), r.YMin() - 4f * h, 3f * r.Width(), 9f * h));
        c.End();
    }
}
