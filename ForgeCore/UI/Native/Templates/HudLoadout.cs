using System;
using Player;
using UnityEngine;

namespace ForgeUI;

/// <summary>Another gun's rounds for a slot's row in place of the game's figures (a gun the game does not count, or counts only in part): the magazine, the reserve, the magazine size and the most the reserve holds.</summary>
public readonly record struct LoadoutAmmo(int Clip, int Reserve, int ClipSize, int ReserveMax);

/// <summary>
/// The game's whole inventory redrawn (for when the game's HUD is hidden), in the game's slot order: the held item opens in place into
/// a card (its kind, name, picture, a large magazine count over the reserve, a cell per share of all the rounds it carries, magazine and reserve together, and the reserve share), every other item
/// is a compact row with what the game shows for its kind (rounds and reserve, a share, resource cells, a count, melee stamina), each
/// with its selection key. A carried object adds a card on top. Rows open and close smoothly when the held item changes; a row whose
/// reserve grows flashes. Values come from the game (<see cref="UIKit.Inventory"/>); nothing is allocated while they stay the same.
/// Sizes are 1080p pixels. Make it with <see cref="UIKitMod.AddLoadout"/>.
/// </summary>
public sealed class HudLoadout : HudWidget
{
    internal override UIKitFont DefaultFont => UIKitFont.Tech;

    private const int MaxRows = 12, SlotKinds = 16;
    private readonly float[] _height = new float[MaxRows];
    private readonly InventorySlot[] _slotOf = new InventorySlot[MaxRows];
    private readonly int[] _clip = new int[MaxRows], _reserve = new int[MaxRows], _percent = new int[MaxRows];
    private readonly string[] _clipText = new string[MaxRows], _reserveText = new string[MaxRows], _pairText = new string[MaxRows], _percentText = new string[MaxRows];
    private readonly float[] _flashAt = new float[MaxRows], _emptySince = new float[MaxRows];
    private readonly Sprite?[] _markIcon = new Sprite?[SlotKinds];
    private readonly UIText?[] _markKind = new UIText?[SlotKinds];
    private readonly bool[] _locked = new bool[SlotKinds];
    private HudCanvas? _canvas;
    private bool _primed;
    private int _count;
    private float _lastPrepare = -1f, _total, _carryHeight, _titleHeight;
    private const string Infinite = "∞";
    // Cards (the held item, a carried object) back darker and outline stronger than the compact rows, so the held item stays first.
    private const float CardPlate = 0.62f, CardFrame = 0.45f;

    internal HudLoadout()
    {
        for (var i = 0; i < MaxRows; i++)
        {
            _clip[i] = _reserve[i] = _percent[i] = int.MinValue;
            _clipText[i] = _reserveText[i] = _pairText[i] = _percentText[i] = "";
            _flashAt[i] = -10f;
            _emptySince[i] = -1f;
        }
    }

    /// <summary>A small title above the list (for example "LOADOUT") with a thin line; empty draws none.</summary>
    public UIText Title { get; set; } = UIText.Empty;
    public float CardWidth { get; set; } = 340f;
    public float CardHeight { get; set; } = 132f;
    public float RowWidth { get; set; } = CompactRow.Width;
    public float RowHeight { get; set; } = CompactRow.Height;
    public float RowGap { get; set; } = CompactRow.Gap;
    /// <summary>A cell per round along the card's bottom. Default true.</summary>
    public bool CellStrip { get; set; } = true;
    /// <summary>The game's kind names (beside each row's name, on the card's top). Default true.</summary>
    public bool KindLabels { get; set; } = true;
    /// <summary>Per slot, the rounds to show in place of the game's own figures (the row, its cells and its shares all follow them), or null for the game's; read every frame, so it must not allocate.</summary>
    public Func<InventorySlot, LoadoutAmmo?>? AmmoSource { get; set; }
    /// <summary>The held card's bar and the marks; default orange.</summary>
    public Color Accent { get; set; } = UIKitColors.Orange;
    /// <summary>At or below this share of the magazine the count and cells turn red; default 0.15.</summary>
    public float LowAt { get; set; } = 0.15f;

    /// <summary>Marks a slot: <paramref name="icon"/> before its name in the accent colour and <paramref name="kind"/> in place of
    /// the game's kind name (for example a vehicle's own gun in the special weapon slot). Null for both clears it.</summary>
    public void Mark(InventorySlot slot, Sprite? icon, UIText? kind)
    {
        var i = (int)slot;
        if ((uint)i >= SlotKinds) return;
        _markIcon[i] = icon;
        _markKind[i] = kind;
    }

    /// <summary>Shows a slot as unavailable: its row at 30 % without its key.</summary>
    public void Lock(InventorySlot slot, bool locked)
    {
        var i = (int)slot;
        if ((uint)i < SlotKinds) _locked[i] = locked;
    }

    internal override void Build(HudElement hud)
    {
        _canvas = new HudCanvas(hud);
    }

    /// <summary>Before the layout: the rows' heights eased towards the held item's card, and the exact size this frame draws reported to
    /// the layout, so what stacks past the loadout or keeps clear of it follows it in the same frame, as smoothly as it opens.</summary>
    internal override void Prepare(HudElement hud, float now)
    {
        var dt = _lastPrepare < 0f ? 0f : Math.Clamp(now - _lastPrepare, 0f, 0.1f);
        _lastPrepare = now;
        if (_canvas == null || !hud.Visible) { _primed = false; return; }
        var u = hud.HalfHeight > 0f ? hud.HalfHeight / 540f : 1f;
        var inventory = UIKit.Inventory;
        var count = Math.Min(inventory.Count, MaxRows);
        var card = CardHeight * u;
        var row = RowHeight * u;
        var gap = RowGap * u;
        var ease = 1f - MathF.Exp(-dt * 28f);
        var total = 0f;
        for (var i = 0; i < count; i++)
        {
            var r = RowOf(inventory, i);
            if (_slotOf[i] != r.Slot) { _slotOf[i] = r.Slot; _clip[i] = _reserve[i] = _percent[i] = int.MinValue; _height[i] = r.Selected ? card : row; }
            var target = r.Selected ? card : row;
            _height[i] = _primed ? _height[i] + (target - _height[i]) * ease : target;
            total += _height[i] + (i > 0 ? gap : 0f);
            Track(i, r, now);
        }
        _primed = true;
        _count = count;
        var carry = inventory.Carrying;
        _carryHeight = carry ? 44f * u : 0f;
        _titleHeight = Show(Title).Length > 0 ? 24f * u : 0f;
        _total = _titleHeight + _carryHeight + (carry && count > 0 ? gap : 0f) + total;
        hud.SetDrawn(CardWidth * u, _total);
    }

    internal override void Tick(HudElement hud, float now, float dt)
    {
        if (_canvas == null || !_canvas.Begin()) return;
        var u = _canvas.Unit;
        var inventory = UIKit.Inventory;
        var count = Math.Min(_count, Math.Min(inventory.Count, MaxRows));
        var row = RowHeight * u;
        var card = CardHeight * u;
        var gap = RowGap * u;
        var title = Show(Title);
        var carry = inventory.Carrying && _carryHeight > 0f;
        var carryHeight = _carryHeight;
        var titleHeight = _titleHeight;
        var height = _total;
        var (fx, fy) = HudFlow.Of(hud);
        var right = ScreenMath.Span(fx, CardWidth * u) + CardWidth * u;
        var y = ScreenMath.Span(fy, height) + height;
        if (title.Length > 0)
        {
            var w = _canvas.Text(title, right, y - 8f * u, CompactRow.SmallPx, UIKitColors.Faded(UIKitColors.Cockpit, 0.7f), HudAlign.Right);
            var lineWidth = RowWidth * u - w - 10f * u;
            if (lineWidth > 0f) _canvas.Rect(right - w - 10f * u - lineWidth / 2f, y - 8f * u, lineWidth, u, UIKitColors.Faded(UIKitColors.Cockpit, 0.15f));
            y -= titleHeight;
        }
        if (carry)
        {
            var cw = CardWidth * u;
            var cy = y - carryHeight / 2f;
            _canvas.Plate(right - cw / 2f, cy, cw, carryHeight, 12f * u, UIKitColors.Faded(UIKitColors.Plate, CardPlate));
            _canvas.Frame(right - cw / 2f, cy, cw, carryHeight, 12f * u, UIKitColors.Faded(UIKitColors.Cockpit, CardFrame));
            _canvas.Rect(right - 1.5f * u, cy, 3f * u, carryHeight - 12f * u, Accent);
            _canvas.Text(UIKit.Kit.Text("loadout.carrying"), right - cw + 14f * u, cy + 9f * u, CompactRow.SmallPx, Accent);
            _canvas.Text(inventory.CarryName, right - 14f * u, cy - 6f * u, 21f, UIKitColors.Faded(UIKitColors.Cockpit, 0.95f), HudAlign.Right, true);
            y -= carryHeight + (count > 0 ? gap : 0f);
        }
        for (var i = 0; i < count; i++)
        {
            var r = RowOf(inventory, i);
            var h = _height[i];
            var cy = y - h / 2f;
            var open = Ease.Clamp01((h - row) / Math.Max(1f, card - row));
            if (open > 0.5f) Card(i, r, right, cy, h, open, now, u);
            else Compact(i, r, right, cy, h, now, u, inventory);
            y -= h + gap;
        }
        _canvas.End();
    }

    private InventoryRow RowOf(GameInventory inventory, int i)
    {
        var row = inventory.Row(i);
        return AmmoSource?.Invoke(row.Slot) is { } ammo ? row.WithAmmo(ammo) : row;
    }

    /// <summary>Remembers the row's figures, building their texts only when they change, and when its reserve grew.</summary>
    private void Track(int i, in InventoryRow r, float now)
    {
        var clip = r.Clip;
        var reserve = r.Reserve;
        var percent = r.ReserveRel >= 0f ? (int)MathF.Round(r.ReserveRel * 100f) : -1;
        if (clip != _clip[i] || reserve != _reserve[i])
        {
            if (_reserve[i] != int.MinValue && reserve > _reserve[i]) _flashAt[i] = now;
            _clipText[i] = clip >= 0 ? NumberText.Tabular(NumberText.Count(clip)) : "";
            _reserveText[i] = reserve >= 0 ? "/" + NumberText.Tabular(NumberText.Count(reserve)) : "";
            _pairText[i] = r.Slot is InventorySlot.Consumable or InventorySlot.ConsumableHeavy or InventorySlot.InPocket or InventorySlot.Pickup
                ? (reserve >= 0 ? "×" + NumberText.Count(reserve) : clip >= 0 ? "×" + NumberText.Count(clip) : "")
                : clip >= 0 && reserve >= 0 ? NumberText.Tabular(NumberText.Count(clip) + " | " + NumberText.Count(reserve))
                : clip >= 0 ? NumberText.Tabular(NumberText.Count(clip)) : reserve >= 0 ? NumberText.Tabular(NumberText.Count(reserve)) : "";
            _clip[i] = clip;
            _reserve[i] = reserve;
        }
        if (percent != _percent[i])
        {
            _percentText[i] = percent >= 0 ? NumberText.Count(percent) + "%" : "";
            _percent[i] = percent;
        }
        var low = r.ClipSize > 0 && r.Clip >= 0 && !r.Infinite && r.Clip == 0;
        if (low) { if (_emptySince[i] < 0f) _emptySince[i] = now; }
        else _emptySince[i] = -1f;
    }

    private void Card(int i, in InventoryRow r, float right, float cy, float h, float open, float now, float u)
    {
        var w = CardWidth * u;
        var cx = right - w / 2f;
        var alpha = open;
        _canvas!.Plate(cx, cy, w, h, 14f * u, UIKitColors.Faded(UIKitColors.Plate, CardPlate * alpha));
        _canvas.Frame(cx, cy, w, h, 14f * u, UIKitColors.Faded(UIKitColors.Cockpit, CardFrame * alpha));
        _canvas.Rect(right - 1.5f * u, cy, 3f * u, h - 16f * u, UIKitColors.Faded(Accent, alpha));
        var top = cy + h / 2f;
        var slot = (int)r.Slot;
        var marked = (uint)slot < SlotKinds && (_markIcon[slot] != null || _markKind[slot] != null);
        var kind = KindLabels ? Kind(r, slot) : "";
        if (kind.Length > 0 && !string.Equals(kind, r.Name, StringComparison.OrdinalIgnoreCase)) _canvas.Text(kind, right - w + 14f * u, top - 15f * u, CompactRow.SmallPx, UIKitColors.Faded(Accent, alpha));
        // First line: key, name, mark.
        var x = right - 14f * u;
        var locked = (uint)slot < SlotKinds && _locked[slot];
        if (!locked && r.Key.Length > 0)
        {
            var kw = KeyCap.Width(_canvas, r.Key);
            KeyCap.Draw(_canvas, x - kw / 2f, top - 18f * u, kw, KeyCap.Height * u, r.Key, UIKitColors.Faded(Accent, 0.9f * alpha), UIKitColors.Faded(UIKitColors.Bright, alpha), 0f, default);
            x -= kw + 10f * u;
        }
        var nameWidth = _canvas.Text(r.Name, x, top - 18f * u, 21f, UIKitColors.Faded(UIKitColors.Cockpit, alpha), HudAlign.Right, true);
        if (marked && _markIcon[slot] != null) _canvas.Icon(_markIcon[slot]!, x - nameWidth - 16f * u, top - 18f * u, 22f * u, 22f * u, UIKitColors.Faded(Accent, alpha));
        // Picture at the left, figures at the right.
        var mid = cy - 4f * u;
        // The weapon's picture, brightened (IconSilhouette) and drawn once at full strength so it reads at a glance.
        if (r.Icon != null && IconSilhouette.Of(r.Icon) is { } icon)
            _canvas.Icon(icon, right - w + 14f * u + 96f * u, mid + 2f * u, 192f * u, 62f * u, UIKitColors.Faded(UIKitColors.Bright, alpha));
        var share = r.ClipSize > 0 && r.Clip >= 0 ? r.Clip / (float)r.ClipSize : 1f;
        var low = !r.Infinite && r.Clip >= 0 && r.ClipSize > 0 && share <= LowAt;
        var flash = GaugeMath.CriticalAlpha(MeterPulse.FlashThenSteady, _emptySince[i] < 0f ? -1f : now - _emptySince[i], now);
        var figure = low ? UIKitColors.Faded(UIKitColors.Red, 0.95f * alpha * flash) : UIKitColors.Faded(UIKitColors.Cockpit, 0.95f * alpha);
        var fx = right - 14f * u;
        if (r.Infinite) _canvas.Text(Infinite, fx, mid, 54f, figure, HudAlign.Right, true);
        else if (r.Clip >= 0)
        {
            var rw = _reserveText[i].Length > 0 ? _canvas.Text(_reserveText[i], fx, mid - 10f * u, 20f, UIKitColors.Faded(UIKitColors.Cockpit, 0.72f * alpha), HudAlign.Right) : 0f;
            if (rw > 0f) _canvas.Text("RSV", fx, mid + 12f * u, CompactRow.SmallPx, UIKitColors.Faded(UIKitColors.Cockpit, 0.6f * alpha), HudAlign.Right);
            _canvas.Text(_clipText[i], fx - rw - 6f * u, mid, 54f, figure, HudAlign.Right, true);
        }
        else if (_pairText[i].Length > 0 || _percentText[i].Length > 0)
            _canvas.Text(_pairText[i].Length > 0 ? _pairText[i] : _percentText[i], fx, mid, 34f, figure, HudAlign.Right, true);
        // Bottom: a cell per round, then the reserve share.
        var bottom = cy - h / 2f;
        var stripLeft = right - w + 14f * u;
        var stripWidth = w - 28f * u;
        // The cells are all the rounds the gun carries (magazine and reserve) against all it can hold; the magazine alone is the big figure above.
        var total = r.TotalRel;
        if (CellStrip && r.ClipSize > 0 && r.Clip >= 0)
        {
            var cells = GaugeMath.ArcCells(r.ClipSize, 60);
            var cell = stripWidth / cells;
            var lit = (int)MathF.Round((total >= 0f ? total : share) * cells);
            var stripLow = !r.Infinite && (total >= 0f ? total : share) <= LowAt;
            for (var c = 0; c < cells; c++)
            {
                var colour = c < lit ? (stripLow ? UIKitColors.Faded(UIKitColors.Red, 0.95f * alpha * flash) : UIKitColors.Faded(UIKitColors.Cockpit, 0.86f * alpha)) : UIKitColors.Faded(UIKitColors.Cockpit, 0.13f * alpha);
                _canvas.Rect(stripLeft + cell * (c + 0.5f), bottom + 16f * u, Math.Max(u, cell - 2f * u), 5f * u, colour);
            }
        }
        if (r.ReserveRel >= 0f)
        {
            var p = Ease.Clamp01(r.ReserveRel);
            _canvas.Rect(stripLeft + stripWidth / 2f, bottom + 9f * u, stripWidth, 1.5f * u, UIKitColors.Faded(UIKitColors.Cockpit, 0.13f * alpha));
            if (p > 0f) _canvas.Rect(stripLeft + stripWidth * p / 2f, bottom + 9f * u, stripWidth * p, 1.5f * u, UIKitColors.Faded(UIKitColors.Cockpit, 0.62f * alpha));
        }
    }

    private void Compact(int i, in InventoryRow r, float right, float cy, float h, float now, float u, GameInventory inventory)
    {
        var w = RowWidth * u;
        var slot = (int)r.Slot;
        var locked = (uint)slot < SlotKinds && _locked[slot];
        var dim = locked ? 0.3f : CompactRow.Text;
        var since = now - _flashAt[i];
        var flash = since < 0.3f ? 1f - since / 0.3f : 0f;
        var text = UIKitColors.Faded(UIKitColors.Cockpit, dim);
        CompactRow.Plate(_canvas!, right, cy, w, h, flash, UIKitColors.Faded(UIKitColors.Cockpit, 0.6f * dim));
        var keyWidth = !locked && r.Key.Length > 0 ? KeyCap.Width(_canvas!, r.Key) : 0f;
        if (keyWidth > 0f)
            KeyCap.Draw(_canvas!, CompactRow.KeyCentre(right, keyWidth, u), cy, keyWidth, (KeyCap.Height - 2f) * u, r.Key, UIKitColors.Line, text, 0f, default);
        // The figures at the left end, by kind; valueRight is where they end.
        var left = CompactRow.ValueLeft(right, w, u);
        var bar = 64f * u;
        var by = cy - h / 2f + 5f * u;
        var valueRight = left;
        switch (r.Slot)
        {
            case InventorySlot.GearMelee:
                if (r.Stamina >= 0f && r.Stamina < 0.999f) { Bar(left, cy, bar, 2f * u, r.Stamina, text); valueRight = left + bar; }
                break;
            case InventorySlot.GearClass:
                if (r.Infinite) valueRight = left + _canvas!.Text(Infinite, left, cy, CompactRow.ValuePx, text, HudAlign.Left, true);
                else if (_percentText[i].Length > 0)
                {
                    valueRight = left + Math.Max(bar, _canvas!.Text(_percentText[i], left, cy + 3f * u, CompactRow.ValuePx, text, HudAlign.Left, true));
                    Bar(left, by, bar, 2f * u, r.ReserveRel, text);
                }
                break;
            case InventorySlot.ResourcePack:
                {
                    var rel = Ease.Clamp01(r.ReserveRel);
                    for (var c = 0; c < 5; c++)
                    {
                        var level = GaugeMath.Segment(rel, c, 5);
                        _canvas!.Skew(left + c * 11f * u + 4.5f * u, cy, 9f * u, 10f * u, 3f * u, level > 0.5f ? text : UIKitColors.Faded(UIKitColors.Cockpit, 0.13f));
                    }
                    valueRight = left + 55f * u;
                    if (_percentText[i].Length > 0) valueRight = left + 58f * u + _canvas!.Text(_percentText[i], left + 58f * u, cy, CompactRow.ValuePx, text, HudAlign.Left, true);
                    break;
                }
            case InventorySlot.HackingTool:
                break;
            default:
                if (r.Infinite) valueRight = left + _canvas!.Text(Infinite, left, cy, CompactRow.ValuePx, text, HudAlign.Left, true);
                else if (_pairText[i].Length > 0)
                {
                    valueRight = left + _canvas!.Text(_pairText[i], left, cy + 3f * u, CompactRow.ValuePx, text, HudAlign.Left, true);
                    if (r.ReserveRel >= 0f && r.Slot is InventorySlot.GearStandard or InventorySlot.GearSpecial) { Bar(left, by, bar, 2f * u, r.ReserveRel, text); valueRight = Math.Max(valueRight, left + bar); }
                }
                break;
        }
        // Name, mark and kind right-aligned against the key column; the kind word is left out when it would run into the figures or
        // only repeats the name.
        var x = CompactRow.NameRight(right, keyWidth, u);
        var nameWidth = _canvas!.Text(r.Name, x, cy, CompactRow.NamePx, text, HudAlign.Right);
        x -= nameWidth + 6f * u;
        var marked = (uint)slot < SlotKinds && _markIcon[slot] != null;
        if (marked) { _canvas.Icon(_markIcon[slot]!, x - 8f * u, cy, 16f * u, 16f * u, UIKitColors.Faded(Accent, (locked ? 0.4f : 1f) * 0.9f)); x -= 20f * u; }
        var kind = KindLabels ? Kind(r, slot) : "";
        if (kind.Length > 0 && !string.Equals(kind, r.Name, StringComparison.OrdinalIgnoreCase) && x - _canvas.TextWidth(kind, CompactRow.SmallPx) >= valueRight + CompactRow.KindGap * u)
            _canvas.Text(kind, x, cy, CompactRow.SmallPx, UIKitColors.Faded(UIKitColors.Cockpit, CompactRow.Kind * (locked ? 0.4f : 1f)), HudAlign.Right);
    }

    private string Kind(in InventoryRow r, int slot)
    {
        if ((uint)slot < SlotKinds && _markKind[slot] != null) return Show(_markKind[slot]);
        return r.Kind;
    }

    private void Bar(float left, float y, float width, float height, float value, Color colour)
    {
        value = Ease.Clamp01(value);
        _canvas!.Rect(left + width / 2f, y, width, height, UIKitColors.Faded(UIKitColors.Cockpit, 0.13f));
        if (value > 0f) _canvas.Rect(left + width * value / 2f, y, width * value, height, colour);
    }
}

/// <summary>
/// The loadout's compact row: a dark plate (lighter than the held item's card) with a bottom line and a left edge, light text, the value
/// column at the left (bold, tabular), a fixed key column at the right and the names right-aligned against it, kind words after the
/// name. Sizes are 1080p pixels.
/// </summary>
internal static class CompactRow
{
    internal const float Width = 300f, Height = 26f, Gap = 6f, ValuePx = 16f, NamePx = 16f, SmallPx = 14f, KeyColumn = 30f, Inset = 6f, KindGap = 10f;
    /// <summary>The row's plate, its text and its kind word, as shares of the plate and ice colours.</summary>
    internal const float Backing = 0.5f, Text = 0.92f, Kind = 0.78f;

    /// <summary>The plate, lit towards ice by <paramref name="flash"/> (0..1) while the reserve has just grown.</summary>
    internal static void Plate(HudCanvas canvas, float right, float cy, float w, float h, float flash, Color edge)
    {
        var u = canvas.Unit;
        canvas.Plate(right - w / 2f, cy, w, h, 0f, Color.Lerp(UIKitColors.Faded(UIKitColors.Plate, Backing), UIKitColors.Faded(UIKitColors.Cockpit, 0.3f), flash));
        canvas.Rect(right - w / 2f, cy - h / 2f + 0.5f * u, w, u, UIKitColors.Faded(UIKitColors.Cockpit, 0.22f));
        canvas.Rect(right - w + 0.6f * u, cy, 1.2f * u, h - 6f * u, edge);
    }

    internal static float KeyCentre(float right, float keyWidth, float u) => right - Inset * u - Math.Max(KeyColumn * u, keyWidth) / 2f;

    /// <summary>Where the names end: left of the key column, or of a key wider than it.</summary>
    internal static float NameRight(float right, float keyWidth, float u) => right - Inset * u - Math.Max(KeyColumn * u, keyWidth) - 8f * u;

    internal static float ValueLeft(float right, float w, float u) => right - w + 10f * u;
}

public sealed partial class UIKitMod
{
    /// <summary>The game's whole inventory in the cockpit style (see <see cref="HudLoadout"/>). Default slot: bottom right, above a
    /// moved chat. It stands in for the game's weapon list: while a mod hides that list, every mod's <see cref="HudRegion.WeaponList"/>
    /// elements stack on past the loadout.</summary>
    /// <example><c>var loadout = ui.AddLoadout("Loadout"); loadout.Mark(InventorySlot.GearSpecial, frameMark, UIText.Key("loadout.frame"));</c></example>
    public HudLoadout? AddLoadout(string name, HudSlot? slot = null)
    {
        var id = Full(name, "HUD element");
        var loadout = id == null ? null : Hud.Add(id, new HudLoadout(), slot ?? new HudSlot(HudRegion.BottomRight, 10, 340f, 330f));
        if (loadout != null) loadout.Element.Entry.WeaponListStandIn = true;
        return loadout;
    }
}
