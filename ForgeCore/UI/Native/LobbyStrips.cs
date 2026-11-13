using System;
using System.Collections.Generic;
using CellMenu;
using SNetwork;
using TMPro;
using UnityEngine;

namespace ForgeUI;

/// <summary>One cell of a <see cref="LobbyStrip"/>: a picture (drawn in its own colours), else a short text. <see cref="Empty"/> is a
/// free slot: a dim outline only.</summary>
public readonly struct LobbyStripItem
{
    private readonly UIText? _text;

    public LobbyStripItem(Sprite? icon, UIText? text = null)
    {
        Icon = icon;
        _text = text;
        IsEmpty = false;
    }

    /// <summary>A free slot.</summary>
    public static LobbyStripItem Empty => new(true);

    private LobbyStripItem(bool empty)
    {
        Icon = null;
        _text = null;
        IsEmpty = empty;
    }

    public Sprite? Icon { get; }

    public UIText Text => _text ?? UIText.Empty;

    /// <summary>True for <see cref="Empty"/>.</summary>
    public bool IsEmpty { get; }
}

public sealed partial class UIKitMod
{
    /// <summary>A row of slot cells on every lobby bar with a player (the local one and everyone else's), one button step under the
    /// APPAREL button and the lobby buttons and exactly as wide as they are: each cell a chamfered tile in one muted dark tone with a
    /// light outline and a light band along its cut corner, the picture in its own colours. <paramref name="items"/> gives each player's
    /// cells in slot order (null or empty shows no row); pass <see cref="LobbyStripItem.Empty"/> for a free slot. Called once per bar per
    /// frame; it may return the same list it keeps, and the row is redrawn only when that content changes. Null (logged) when the name
    /// cannot be used or is taken.</summary>
    /// <example><c>ui.AddLobbyStrip("Loadouts", player =&gt; LobbySync.ItemsOf(player.Lookup), 8);</c></example>
    public LobbyStrip? AddLobbyStrip(string name, Func<SNet_Player, IReadOnlyList<LobbyStripItem>?> items, int maxItems = 6)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        var id = Full(name, "lobby strip");
        if (id == null) return null;
        var strip = new LobbyStrip(id, this, items, Math.Clamp(maxItems, 1, 12));
        return LobbyStrips.Add(strip) ? strip : null;
    }
}

/// <summary>A row of slot cells on every lobby bar (see <see cref="UIKitMod.AddLobbyStrip"/>).</summary>
public sealed class LobbyStrip
{
    internal LobbyStrip(string id, UIKitMod mod, Func<SNet_Player, IReadOnlyList<LobbyStripItem>?> items, int max)
    {
        Id = id;
        Mod = mod;
        Items = items;
        Max = max;
    }

    public string Id { get; }
    internal UIKitMod Mod { get; }
    internal Func<SNet_Player, IReadOnlyList<LobbyStripItem>?> Items { get; }
    internal int Max { get; }

    public void Remove() => LobbyStrips.Remove(this);
}

/// <summary>
/// Draws every <see cref="LobbyStrip"/> on every lobby bar with a <see cref="MenuCanvas"/> on the APPAREL button's parent (the button's
/// sprite material and sorting, a copy of its label for text cells). A row sits one button step under the last lobby button shown on that
/// bar, spans the APPAREL button's clickable width and is as tall as it; cells share that width (<see cref="LobbyStripLayout"/>). Pictures
/// are cropped copies in their own colours (<see cref="IconSilhouette.Trimmed"/>). A row is redrawn only when its content or the button's
/// size changed.
/// </summary>
internal static class LobbyStrips
{
    /// <summary>Cut corner, picture margins and text size, in the lobby bar's units (the APPAREL box is 55 tall).</summary>
    private const float Cut = 12f, PadX = 10f, PadY = 7f, TextShare = 0.3f;

    private static readonly Color Fill = new(0.14f, 0.16f, 0.18f, 0.88f);
    private static readonly Color Line = new(0.92f, 0.94f, 0.96f, 0.85f), Band = new(0.92f, 0.94f, 0.96f, 0.45f), FreeLine = new(0.92f, 0.94f, 0.96f, 0.28f);
    private static readonly Registry<LobbyStrip> Registered = new();
    private static readonly Dictionary<IntPtr, Dictionary<string, Row>> Rows = new();
    private static readonly List<IntPtr> Gone = new();
    private static (float Left, float Width, float Height) _box;

    private sealed class Row
    {
        internal MenuCanvas Canvas = null!;
        internal Sprite?[] Icons = null!;
        internal string?[] Texts = null!;
        internal bool[] Free = null!;
        internal int Count = -1;
        internal float Left, Width, Height;
    }

    internal static bool Add(LobbyStrip strip)
    {
        var problem = Registered.Add(strip.Id, strip);
        if (problem == null)
        {
            return true;
        }
        Plugin.Error("ui-lobby-strip-not-added", "Lobby strip not added: " + problem + ".");
        return false;
    }

    internal static void Remove(LobbyStrip strip)
    {
        if (!Registered.Remove(strip.Id, strip)) return;
        foreach (var perBar in Rows.Values)
            if (perBar.Remove(strip.Id, out var row)) row.Canvas.Destroy();
    }

    internal static void Update()
    {
        if (Registered.Ordered.Count == 0) return;
        Gone.Clear();
        foreach (var (pointer, perBar) in Rows)
        {
            var alive = false;
            foreach (var row in perBar.Values)
                if (row.Canvas.Alive) { alive = true; break; }
            if (!alive) Gone.Add(pointer);
        }
        foreach (var pointer in Gone) Rows.Remove(pointer);
        var page = CM_PageLoadout.Current;
        if (page == null || page.WasCollected) return;
        var bars = page.m_playerLobbyBars;
        if (bars == null) return;
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            if (bar == null || bar.WasCollected) continue;
            try { Sync(bar); }
            catch (Exception error) { Plugin.Error("ui-lobby-strips-failed-update", "Lobby strips failed to update: " + error); }
        }
    }

    private static void Sync(CM_PlayerLobbyBar bar)
    {
        var clothes = bar.m_clothesButton;
        if (clothes == null || clothes.WasCollected) return;
        if (!Rows.TryGetValue(bar.Pointer, out var perBar)) Rows[bar.Pointer] = perBar = new Dictionary<string, Row>();
        var player = bar.m_player;
        var show = player != null && bar.gameObject.activeInHierarchy;
        var index = LobbyButtons.ShownOn(bar.Pointer);
        var origin = clothes.transform.localPosition;
        var strips = Registered.Ordered;
        for (var s = 0; s < strips.Count; s++)
        {
            var strip = strips[s];
            IReadOnlyList<LobbyStripItem>? items = null;
            if (show)
            {
                try { items = strip.Items(player!); }
                catch (Exception error) { Plugin.Error("ui-lobby-strip-items-failed", "Lobby strip '" + strip.Id + "' items failed: " + error); }
            }
            if (!Registered.Contains(strip.Id, strip)) continue;
            var count = items == null ? 0 : Math.Min(items.Count, strip.Max);
            perBar.TryGetValue(strip.Id, out var row);
            if (count == 0 || !ButtonBox(clothes, origin, out var left, out var width, out var height))
            {
                if (row != null && row.Canvas.Alive && row.Canvas.Root.activeSelf) row.Canvas.Root.SetActive(false);
                continue;
            }
            if (row == null || !row.Canvas.Alive)
            {
                var made = Create(clothes, strip);
                if (made == null) continue;
                perBar[strip.Id] = row = made;
            }
            var root = row.Canvas.Root;
            if (!root.activeSelf) root.SetActive(true);
            var (x, y) = LobbyLayout.Offset(index++, Plugin.OffsetX, Plugin.OffsetY);
            var target = new Vector3(origin.x + x, origin.y + y, origin.z);
            if (root.transform.localPosition != target) root.transform.localPosition = target;
            if (Changed(row, strip, items!, count, left, width, height)) Draw(row, count);
        }
    }

    /// <summary>The APPAREL button's clickable box in its parent's units: its left edge relative to the button's own position, its
    /// width and height. Every bar is the same prefab, so the box measured on a bar whose button is shown (the local player's) is
    /// used on bars whose button is hidden (other players'). False until one has been measured.</summary>
    private static bool ButtonBox(CM_LobbyScrollItem clothes, Vector3 origin, out float left, out float width, out float height)
    {
        var collider = clothes.m_collider;
        if (collider != null && collider.enabled && clothes.gameObject.activeInHierarchy)
        {
            var parent = clothes.transform.parent;
            var bounds = collider.bounds;
            var min = parent.InverseTransformPoint(bounds.min);
            var max = parent.InverseTransformPoint(bounds.max);
            var w = Math.Abs(max.x - min.x);
            var h = Math.Abs(max.y - min.y);
            if (w > 1f && h > 1f)
            {
                _box = (Math.Min(min.x, max.x) - origin.x, w, h);
            }
        }
        (left, width, height) = _box;
        return width > 1f;
    }

    /// <summary>Takes in this frame's cells; true when anything shown differs from what was drawn.</summary>
    private static bool Changed(Row row, LobbyStrip strip, IReadOnlyList<LobbyStripItem> items, int count, float left, float width, float height)
    {
        var changed = count != row.Count || left != row.Left || width != row.Width || height != row.Height;
        row.Count = count;
        row.Left = left;
        row.Width = width;
        row.Height = height;
        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            var words = item.IsEmpty ? "" : item.Text.In(strip.Mod.Texts);
            if (ReferenceEquals(item.Icon, row.Icons[i]) && words == row.Texts[i] && item.IsEmpty == row.Free[i]) continue;
            row.Icons[i] = item.Icon;
            row.Texts[i] = words;
            row.Free[i] = item.IsEmpty;
            changed = true;
        }
        return changed;
    }

    private static void Draw(Row row, int count)
    {
        var c = row.Canvas;
        var cellWidth = LobbyStripLayout.CellWidth(row.Width, count);
        var h = row.Height;
        c.Begin();
        for (var i = 0; i < count; i++)
        {
            var centre = LobbyStripLayout.CellCentre(row.Left, cellWidth, i);
            var cell = new Rect(centre - cellWidth / 2f, -h / 2f, cellWidth, h);
            if (row.Free[i])
            {
                c.Frame(cell, Cut, FreeLine);
                c.Rect(new Rect(centre - 8f, -0.75f, 16f, 1.5f), FreeLine);
                c.Rect(new Rect(centre - 0.75f, -8f, 1.5f, 16f), FreeLine);
                continue;
            }
            c.Plate(cell, Cut, Fill);
            c.Frame(cell, Cut, Line);
            c.Picture(UIKitSprites.CornerBand, new Rect(cell.XMin(), cell.YMax() - 2f * Cut, 2f * Cut, 2f * Cut), Band);
            var icon = row.Icons[i];
            var picture = icon == null ? null : IconSilhouette.Trimmed(icon);
            if (picture != null) c.Icon(picture, Rect.MinMaxRect(cell.XMin() + PadX, cell.YMin() + PadY, cell.XMax() - PadX, cell.YMax() - PadY), Color.white);
            else if (!string.IsNullOrEmpty(row.Texts[i])) c.Text(row.Texts[i]!, centre, 0f, h * TextShare, MenuLook.Line, HudAlign.Centre, cellWidth - 2f * PadX);
        }
        c.End();
    }

    private static Row? Create(CM_LobbyScrollItem clothes, LobbyStrip strip)
    {
        var like = clothes.GetComponentInChildren<SpriteRenderer>(true);
        var label = clothes.GetComponentInChildren<TextMeshPro>(true);
        if (like == null || label == null)
        {
            Plugin.Error("ui-lobby-strip-cannot-be-drawn-apparel", "Lobby strip '" + strip.Id + "' cannot be drawn: the APPAREL button has no sprite or text to copy.");
            return null;
        }
        var canvas = new MenuCanvas(clothes.transform.parent, "ForgeUI.LobbyStrip." + strip.Id, like, label, MenuCanvas.TopOrder(clothes) + 1);
        return new Row { Canvas = canvas, Icons = new Sprite?[strip.Max], Texts = new string?[strip.Max], Free = new bool[strip.Max] };
    }
}
