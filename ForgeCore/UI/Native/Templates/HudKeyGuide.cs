using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeUI;

/// <summary>A line of a <see cref="HudKeyGuide"/>: its keys, each in its own key cap (a mouse button or a word like "wheel" too), and what they do.</summary>
public readonly record struct KeyGuideRow(string[] Keys, string Text);

/// <summary>A titled group of a <see cref="HudKeyGuide"/>.</summary>
public sealed record KeyGuideGroup(string Title, KeyGuideRow[] Rows);

/// <summary>
/// A list of keys by group, drawn on a <see cref="HudCanvas"/>: in each group the key caps in one column and what they do in another, the groups in
/// as few columns as the height allows (<see cref="KeyGuideLayout"/>), a long text broken over lines. <see cref="Set"/> when the keys change,
/// <see cref="Layout"/> for the room, then <see cref="Draw"/> each frame; the caller draws the plate under it.
/// </summary>
public sealed class HudKeyGuide
{
    private const float Px = 17f, TitlePx = 17f, CapPx = 16f, Row = 40f, Line = 26f, Title = 46f, CapHeight = 32f, CapPad = 20f, CapGap = 6f, GroupGap = 22f, ColumnGap = 56f, KeyGap = 20f, TextMax = 520f;

    private sealed class LaidGroup
    {
        internal string Title = "";
        internal readonly List<KeyGuideRow> Rows = new();
        // Per row: the text's lines.
        internal readonly List<List<string>> Lines = new();
        internal float Height, Keys, Text;
        internal int Column;
    }

    private IReadOnlyList<KeyGuideGroup> _groups = Array.Empty<KeyGuideGroup>();
    private readonly List<LaidGroup> _laid = new();
    private readonly List<(float Keys, float Text)> _columns = new();
    private bool _dirty = true;
    private float _laidWidth = -1f, _laidHeight = -1f;

    /// <summary>The size for the room given to <see cref="Layout"/>.</summary>
    public float Width { get; private set; }
    public float Height { get; private set; }

    /// <summary>What it lists.</summary>
    public void Set(IReadOnlyList<KeyGuideGroup> groups) { _groups = groups; _dirty = true; }

    /// <summary>Works out the columns for at most <paramref name="maxWidth"/> x <paramref name="maxHeight"/>; does nothing while the keys and the room are the same.</summary>
    public void Layout(HudCanvas c, float maxWidth, float maxHeight)
    {
        if (!_dirty && maxWidth == _laidWidth && maxHeight == _laidHeight) return;
        _dirty = false; _laidWidth = maxWidth; _laidHeight = maxHeight;
        _laid.Clear();
        var keysWide = 0f; var textWide = 0f;
        foreach (var group in _groups)
        {
            var laid = new LaidGroup { Title = group.Title, Height = Title };
            foreach (var row in group.Rows)
            {
                var lines = new List<string>();
                c.Wrap(row.Text, Px, false, TextMax, lines);
                laid.Rows.Add(row); laid.Lines.Add(lines);
                laid.Height += RowHeight(lines.Count);
                laid.Keys = Math.Max(laid.Keys, CapsWidth(c, row.Keys));
                foreach (var line in lines) laid.Text = Math.Max(laid.Text, c.TextWidth(line, Px));
            }
            _laid.Add(laid);
            keysWide = Math.Max(keysWide, laid.Keys); textWide = Math.Max(textWide, laid.Text);
        }
        var perColumn = keysWide + KeyGap + textWide + ColumnGap;
        var maxColumns = Math.Max(1, (int)((maxWidth + ColumnGap) / Math.Max(1f, perColumn)));
        var heights = new float[_laid.Count];
        for (var i = 0; i < heights.Length; i++) heights[i] = _laid[i].Height;
        var columns = KeyGuideLayout.Columns(heights, GroupGap, maxHeight, maxColumns, out var height);
        _columns.Clear();
        for (var i = 0; i < _laid.Count; i++)
        {
            _laid[i].Column = columns[i];
            while (_columns.Count <= columns[i]) _columns.Add((0f, 0f));
            var (keys, text) = _columns[columns[i]];
            _columns[columns[i]] = (Math.Max(keys, _laid[i].Keys), Math.Max(text, _laid[i].Text));
        }
        var width = 0f;
        foreach (var (keys, text) in _columns) width += keys + KeyGap + text;
        Width = width + Math.Max(0, _columns.Count - 1) * ColumnGap;
        Height = height;
    }

    /// <summary>Draws it with its top left corner at (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public void Draw(HudCanvas c, float left, float top, Color titleColour, Color text, Color capText, Color capFill, Color capLine)
    {
        var x = left;
        for (var column = 0; column < _columns.Count; column++)
        {
            var y = top;
            var (keys, wide) = _columns[column];
            foreach (var group in _laid)
            {
                if (group.Column != column) continue;
                c.Text(group.Title, x, y - Title / 2f + 2f, TitlePx, titleColour, HudAlign.Left, bold: true);
                c.Rect(x + (keys + KeyGap + wide) / 2f, y - Title + 6f, keys + KeyGap + wide, 1.5f, titleColour * new Color(1f, 1f, 1f, 0.45f));
                y -= Title;
                for (var r = 0; r < group.Rows.Count; r++)
                {
                    var lines = group.Lines[r];
                    var height = RowHeight(lines.Count);
                    var first = y - Row / 2f;
                    var at = x;
                    foreach (var key in group.Rows[r].Keys) at += Cap(c, key, at, first, capText, capFill, capLine) + CapGap;
                    for (var l = 0; l < lines.Count; l++) c.Text(lines[l], x + keys + KeyGap, first - l * Line, Px, text);
                    y -= height;
                }
                y -= GroupGap;
            }
            x += keys + KeyGap + wide + ColumnGap;
        }
    }

    private static float RowHeight(int lines) => Row + Math.Max(0, lines - 1) * Line;

    private static float CapsWidth(HudCanvas c, string[] keys)
    {
        var width = 0f;
        foreach (var key in keys) width += c.TextWidth(key, CapPx, true) + CapPad + CapGap;
        return Math.Max(0f, width - CapGap);
    }

    private static float Cap(HudCanvas c, string key, float left, float y, Color ink, Color fill, Color line)
    {
        var width = c.TextWidth(key, CapPx, true) + CapPad;
        c.Plate(left + width / 2f, y, width, CapHeight, 8f, fill);
        c.Frame(left + width / 2f, y, width, CapHeight, 8f, line);
        c.Text(key, left + width / 2f, y, CapPx, ink, HudAlign.Centre, bold: true);
        return width;
    }
}
