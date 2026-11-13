using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>A cell of a <see cref="HudTable"/>: its text and colour; bold draws it heavier.</summary>
public readonly record struct TableCell(string Text, Color Colour, bool Bold = false);

/// <summary>
/// A table drawn on a <see cref="HudCanvas"/>, with columns as wide as their heads and cells need. Where the room is too small the widest
/// columns are narrowed to one level (<see cref="ColumnFit"/>) and only their long texts are cut, so cells never run into each other. Rows
/// past the height scroll (<see cref="First"/>, <see cref="Scroll"/>), with a thumb at the right edge. Fill it when the data changes
/// (<see cref="Clear"/>, <see cref="SetColumns"/>, <see cref="AddRow"/>, <see cref="AddSection"/>), lay it out for the room
/// (<see cref="Layout"/>, which does nothing until the rows or the room change), then <see cref="Draw"/> it each frame.
/// </summary>
public sealed class HudTable
{
    private readonly record struct Row(TableCell[]? Cells, string Section, Color SectionColour);

    /// <summary>Sizes: the text in 1080p pixels, the rest in the element's units.</summary>
    public float Px { get; set; } = 17f;
    public float HeadPx { get; set; } = 16f;
    public float RowHeight { get; set; } = 32f;
    /// <summary>The heads' row; 0 draws no heads.</summary>
    public float HeadHeight { get; set; } = 38f;
    public float ColumnGap { get; set; } = 28f;
    public float Pad { get; set; } = 16f;

    /// <summary>The stripe on every other row (fully transparent: none) and the line under the heads.</summary>
    public Color Stripe { get; set; } = new(1f, 1f, 1f, 0.02f);
    public Color Rule { get; set; } = new(1f, 1f, 1f, 0.3f);

    private string[] _heads = Array.Empty<string>();
    private Color[] _headColours = Array.Empty<Color>();
    private Color?[] _underlines = Array.Empty<Color?>();
    private HudAlign[] _aligns = Array.Empty<HudAlign>();
    private float[] _widths = Array.Empty<float>(), _wants = Array.Empty<float>();
    private readonly List<Row> _rows = new();
    private bool _dirty = true;
    private float _laidWidth = -1f, _laidHeight = -1f, _laidMin;

    /// <summary>The table's size for the room given to <see cref="Layout"/>, padding included.</summary>
    public float Width { get; private set; }
    public float Height { get; private set; }

    /// <summary>The height the whole table wants, whatever the room.</summary>
    public float FullHeight => HeadHeight + _rows.Count * RowHeight;

    /// <summary>How many rows have room, and how many rows there are.</summary>
    public int Shown { get; private set; }
    public int Count => _rows.Count;

    /// <summary>The first row shown; <see cref="Draw"/> keeps it in range.</summary>
    public int First { get; set; }

    /// <summary>Moves the first row shown by <paramref name="rows"/>.</summary>
    public void Scroll(int rows) => First += rows;

    /// <summary>Removes the rows; the columns stay.</summary>
    public void Clear() { _rows.Clear(); _dirty = true; }

    /// <summary>The columns: their heads, colours and how each lines up. An underline in a player's colour goes under the head where one is given.</summary>
    public void SetColumns(string[] heads, Color headColour, HudAlign first = HudAlign.Left, HudAlign rest = HudAlign.Right)
    {
        _heads = heads;
        _headColours = new Color[heads.Length];
        _underlines = new Color?[heads.Length];
        _aligns = new HudAlign[heads.Length];
        _widths = new float[heads.Length];
        _wants = new float[heads.Length];
        for (var i = 0; i < heads.Length; i++) { _headColours[i] = headColour; _aligns[i] = i == 0 ? first : rest; }
        _dirty = true;
    }

    /// <summary>One column's head colour and the underline under it.</summary>
    public void SetHead(int column, Color colour, Color? underline = null)
    {
        _headColours[column] = colour;
        _underlines[column] = underline;
    }

    /// <summary>How a column lines up.</summary>
    public void SetAlign(int column, HudAlign align) => _aligns[column] = align;

    /// <summary>A row; it should have one cell for each column (a missing cell is left empty).</summary>
    public void AddRow(params TableCell[] cells) { _rows.Add(new Row(cells, "", default)); _dirty = true; }

    /// <summary>A heading across the table, for a group of rows.</summary>
    public void AddSection(string title, Color colour) { _rows.Add(new Row(null, title, colour)); _dirty = true; }

    /// <summary>Works out the columns for at most <paramref name="maxWidth"/> x <paramref name="maxHeight"/>, at least <paramref name="minWidth"/>
    /// wide (the spare room goes to the first column). Does nothing while the rows and the room are the same.</summary>
    public void Layout(HudCanvas c, float maxWidth, float maxHeight, float minWidth = 0f)
    {
        if (!_dirty && maxWidth == _laidWidth && maxHeight == _laidHeight && minWidth == _laidMin) return;
        _dirty = false; _laidWidth = maxWidth; _laidHeight = maxHeight; _laidMin = minWidth;
        var columns = _heads.Length;
        var sections = 0f;
        for (var i = 0; i < columns; i++) _wants[i] = HeadHeight > 0f ? c.TextWidth(_heads[i], HeadPx, true) : 0f;
        foreach (var row in _rows)
        {
            if (row.Cells == null) { sections = Math.Max(sections, c.TextWidth(row.Section, HeadPx, true)); continue; }
            for (var i = 0; i < columns && i < row.Cells.Length; i++) _wants[i] = Math.Max(_wants[i], c.TextWidth(row.Cells[i].Text, Px, row.Cells[i].Bold));
        }
        var chrome = 2f * Pad + Math.Max(0, columns - 1) * ColumnGap;
        ColumnFit.Widths(_wants, maxWidth - chrome, _widths);
        var used = chrome;
        foreach (var w in _widths) used += w;
        if (columns > 0 && used < minWidth) { _widths[0] += minWidth - used; used = minWidth; }
        if (columns > 0 && used < sections + 2f * Pad) { _widths[0] += sections + 2f * Pad - used; used = sections + 2f * Pad; }
        Width = used;
        Shown = Math.Clamp((int)((maxHeight - HeadHeight) / RowHeight), 0, _rows.Count);
        Height = HeadHeight + Shown * RowHeight;
    }

    /// <summary>Draws the laid-out table with its top left corner at (<paramref name="left"/>, <paramref name="top"/>); the caller draws the plate under it.</summary>
    public void Draw(HudCanvas c, float left, float top)
    {
        First = Math.Clamp(First, 0, Math.Max(0, _rows.Count - Shown));
        var y = top - HeadHeight / 2f;
        var x = left + Pad;
        for (var i = 0; i < _heads.Length && HeadHeight > 0f; i++)
        {
            var at = Across(x, _widths[i], _aligns[i]);
            c.Text(c.FitText(_heads[i], HeadPx, true, _widths[i]), at, y, HeadPx, _headColours[i], _aligns[i], bold: true);
            if (_underlines[i] is { } line) c.Rect(x + _widths[i] / 2f, top - HeadHeight + 3f, _widths[i], 3f, line);
            x += _widths[i] + ColumnGap;
        }
        if (HeadHeight > 0f) c.Rect(left + Width / 2f, top - HeadHeight, Width - 2f * Pad, 1.5f, Rule);
        y = top - HeadHeight - RowHeight / 2f;
        var stripe = 0;
        for (var r = First; r < First + Shown && r < _rows.Count; r++, y -= RowHeight)
        {
            var row = _rows[r];
            if (row.Cells == null)
            {
                c.Text(c.FitText(row.Section, HeadPx, true, Width - 2f * Pad), left + Pad, y - 2f, HeadPx, row.SectionColour, HudAlign.Left, bold: true);
                c.Rect(left + Width / 2f, y - RowHeight / 2f + 2f, Width - 2f * Pad, 1f, row.SectionColour * new Color(1f, 1f, 1f, 0.35f));
                stripe = 0;
                continue;
            }
            if (stripe++ % 2 == 0 && Stripe.a > 0f) c.Rect(left + Width / 2f, y, Width - 2f * Pad + 8f, RowHeight - 2f, Stripe);
            x = left + Pad;
            for (var i = 0; i < _heads.Length; i++)
            {
                if (i < row.Cells.Length && row.Cells[i].Text is { Length: > 0 } text)
                {
                    var cell = row.Cells[i];
                    c.Text(c.FitText(text, Px, cell.Bold, _widths[i]), Across(x, _widths[i], _aligns[i]), y, Px, cell.Colour, _aligns[i], bold: cell.Bold);
                }
                x += _widths[i] + ColumnGap;
            }
        }
        ScrollThumb(c, left + Width - 5f, top - HeadHeight, Shown * RowHeight, First, Shown, _rows.Count, Rule);
    }

    /// <summary>A thin thumb saying which part of a long list shows, with its top at <paramref name="top"/>: nothing when all of it shows.</summary>
    public static void ScrollThumb(HudCanvas c, float x, float top, float height, int first, int shown, int count, Color colour)
    {
        if (count <= shown || count <= 0 || height <= 0f) return;
        var thumb = Math.Max(16f, height * shown / count);
        var y = top - thumb / 2f - (height - thumb) * first / Math.Max(1, count - shown);
        c.Rect(x, y, 4f, thumb, colour);
    }

    private static float Across(float x, float width, HudAlign align) => align == HudAlign.Left ? x : align == HudAlign.Centre ? x + width / 2f : x + width;
}
