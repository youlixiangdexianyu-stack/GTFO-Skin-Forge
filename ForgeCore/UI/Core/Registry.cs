using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeUI;

/// <summary>The registered entries of one kind (lobby buttons, menu bar buttons), kept in the fixed order they are laid out in:
/// by ID, ordinal. Two mods that register buttons therefore get the same order on every machine, whatever their load order.</summary>
internal sealed class Registry<T> where T : class
{
    private readonly SortedDictionary<string, T> _entries = new(StringComparer.Ordinal);

    /// <summary>The entries in layout order.</summary>
    internal IReadOnlyList<T> Ordered { get; private set; } = Array.Empty<T>();

    /// <summary>Adds <paramref name="entry"/> under <paramref name="id"/>; returns why it was refused, or null.</summary>
    internal string? Add(string id, T entry)
    {
        var problem = Validate(id);
        if (problem != null) return problem;
        if (_entries.ContainsKey(id)) return "the ID '" + id + "' is already registered";
        _entries.Add(id, entry);
        Ordered = _entries.Values.ToArray();
        return null;
    }

    internal bool Remove(string id, T entry)
    {
        if (!Contains(id, entry)) return false;
        _entries.Remove(id);
        Ordered = _entries.Values.ToArray();
        return true;
    }

    internal bool Contains(string id, T entry) => _entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry);

    internal int IndexOf(string id)
    {
        var index = 0;
        foreach (var key in _entries.Keys)
        {
            if (key == id) return index;
            index++;
        }
        return -1;
    }

    /// <summary>IDs are non-empty and printable without spaces, so they read well in logs; <c>Author.Mod.Button</c> is the
    /// suggested form.</summary>
    internal static string? Validate(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "the ID is empty";
        if (id.Length > 128) return "the ID '" + id + "' is longer than 128 characters";
        foreach (var c in id)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return "the ID '" + id + "' contains a space or a control character";
        return null;
    }
}

/// <summary>Namespaced IDs: every registration is <c>mod/element</c>, the mod's own ID (its plugin GUID, for example
/// <c>NAinfini.ForgeOrdnance</c>) and the element's name inside it, so two mods can never take each other's names.</summary>
internal static class ModIds
{
    public const char Separator = '/';

    /// <summary>Why <paramref name="part"/> cannot be a mod ID or an element name, or null: non-empty, at most 64 characters, no
    /// space, control character or <see cref="Separator"/>.</summary>
    public static string? Problem(string? part, string what)
    {
        if (string.IsNullOrEmpty(part)) return "the " + what + " is empty";
        if (part.Length > 64) return "the " + what + " '" + part + "' is longer than 64 characters";
        foreach (var c in part)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == Separator) return "the " + what + " '" + part + "' contains a space, a control character or '/'";
        return null;
    }

    public static string Join(string mod, string name) => mod + Separator + name;

    /// <summary>The mod and element parts of a namespaced ID.</summary>
    public static (string Mod, string Name) Split(string id)
    {
        var at = id.IndexOf(Separator);
        return at < 0 ? ("", id) : (id.Substring(0, at), id.Substring(at + 1));
    }
}

/// <summary>Where on a lobby bar a lobby button goes (<c>UIKitMod.AddLobbyButton</c>). Every area holds as many buttons as
/// fit on screen beside the player's panel; a button with no room in its own area goes, in order, to the first other area with room.</summary>
public enum LobbyArea
{
    /// <summary>A column under the APPAREL button (and above the lobby strips); where the screen's bottom is near, its rows and the
    /// strips' close up, then get lower (not below 0.6 of their height, always as wide as APPAREL), so they stay under APPAREL.</summary>
    Below,

    /// <summary>A column right of the panel (weapons, boosters, APPAREL), from its top down, in the space before the next player's
    /// panel; buttons shrink (not below 0.6) to fit that space.</summary>
    Right,

    /// <summary>A row above the panel, under the player's name and status, from the panel's left edge rightwards.</summary>
    Top,
}

/// <summary>How wide a lobby button is (<c>UIKitMod.AddLobbyButton</c>): a whole row (the APPAREL button's width), half a row or a
/// third, so up to three share a row with <see cref="LobbyLayout.Gap"/> between them.</summary>
public enum LobbyWidth
{
    /// <summary>The whole row; it has a row to itself.</summary>
    Full,

    /// <summary>(row - one gap) / 2: two fill a row.</summary>
    Half,

    /// <summary>(row - two gaps) / 3: three fill a row.</summary>
    Third,
}

/// <summary>Where one lobby area puts its buttons: <see cref="Rows"/> rows, each <see cref="Length"/> long (at scale 1), the first row's
/// start at (<see cref="X"/>, <see cref="Y"/>) and each next row <see cref="StepY"/> on, all drawn at <see cref="Scale"/>. Positions are
/// of a copy's pivot, relative to the APPAREL button's pivot, in its parent's units.</summary>
internal readonly struct LobbySpots
{
    internal LobbySpots(int rows, float length, float x, float y, float stepY, float scale, float squash = 1f)
    {
        Rows = Math.Max(0, rows);
        Length = length;
        X = x;
        Y = y;
        StepY = stepY;
        Scale = scale;
        Squash = squash;
    }

    internal int Rows { get; }
    internal float Length { get; }
    internal float X { get; }
    internal float Y { get; }
    internal float StepY { get; }
    internal float Scale { get; }

    /// <summary>How much of its height a row keeps (1: all), on top of <see cref="Scale"/>: rows under APPAREL get lower where the screen's
    /// bottom is near and keep their width; what is drawn in them sizes to the lower height, never squeezed.</summary>
    internal float Squash { get; }

    /// <summary>The pivot of a button whose left edge is <paramref name="offset"/> (at scale 1) into row <paramref name="row"/>.</summary>
    internal (float X, float Y) At(int row, float offset) => (X + offset * Scale, Y + StepY * row);
}

/// <summary>Where lobby buttons go. All boxes are relative to the APPAREL button's pivot, in its parent's units: <c>button</c> is
/// its clickable box (a whole row at scale 1), <c>panel</c> the player's loadout panel around it (weapon and booster slots, their
/// headers, APPAREL), <c>floor</c> the lowest a button may reach (the screen's bottom edge), <c>ceiling</c> the highest (the bottom of
/// the player's name or status over the panel, else the screen's top edge) and <c>rightEdge</c> the furthest right (the next player's
/// panel or name less <see cref="Gap"/>, else the screen's right edge).</summary>
internal static class LobbyLayout
{
    /// <summary>The APPAREL box is 55 units tall; each row sits one box and an 18-unit gap below the one above.</summary>
    internal const float Step = 73f;

    /// <summary>The gap between two buttons and between a button and the panel, in the lobby bar's units.</summary>
    internal const float Gap = 18f;

    /// <summary>The smallest scale a button is shrunk to; an area with less room is not used.</summary>
    internal const float MinScale = 0.6f;

    /// <summary>The width of a <paramref name="width"/> button when a whole row is <paramref name="row"/> long.</summary>
    internal static float Width(LobbyWidth width, float row)
    {
        var parts = width == LobbyWidth.Third ? 3 : width == LobbyWidth.Half ? 2 : 1;
        return (row - Gap * (parts - 1)) / parts;
    }

    /// <summary>The narrowest gap rows under APPAREL close to before they shrink.</summary>
    internal const float MinGap = 6f;

    /// <summary>The spots under APPAREL for <paramref name="rows"/> rows (the buttons' rows, then <paramref name="reserved"/> rows kept
    /// for the lobby strips, which come after the buttons there): rows as long as the APPAREL button, left edges on its left edge, going
    /// down from it <see cref="Gap"/> apart. Where they do not all fit above <paramref name="floor"/>, the gaps close down to
    /// <see cref="MinGap"/>, then the rows get lower (not below <see cref="MinScale"/> of their height, <see cref="LobbySpots.Squash"/>), always
    /// as wide as APPAREL, so the buttons stay right under APPAREL with the strips under them; where even that is too little, rows keep full
    /// height and only those that fit are given.</summary>
    internal static LobbySpots Below(HudBox button, float floor, int rows, int reserved)
    {
        if (button.Width <= 0f || button.Height <= 0f) return default;
        var room = button.Bottom - floor;
        var height = button.Height;
        float gap = Gap, squash = 1f;
        if (rows > 0 && rows * (height + Gap) > room)
        {
            gap = room / rows - height;
            if (gap < MinGap) (gap, squash) = (MinGap, (room / rows - MinGap) / height);
            if (squash < MinScale) (gap, squash, rows) = (Gap, 1f, Whole(room / Step));
        }
        else rows = Math.Max(rows, Whole(room / Step));
        // Row 0's top is gap under APPAREL's bottom and the row is height * squash tall about the box's middle; each next row that and a gap lower.
        var low = height * squash;
        var y = button.Bottom - gap - low / 2f - (button.Bottom + button.Top) / 2f;
        return new LobbySpots(rows - reserved, button.Width, 0f, y, -(low + gap), 1f, squash);
    }

    /// <summary>The spots of <paramref name="area"/> beside the panel (<see cref="Below"/> has its own): right of the panel the rows are
    /// as long as the APPAREL button and go downwards; above the panel there is one row, from the panel's left edge to
    /// <paramref name="rightEdge"/>.</summary>
    internal static LobbySpots Spots(LobbyArea area, HudBox button, HudBox panel, float floor, float ceiling, float rightEdge)
    {
        if (button.Width <= 0f || button.Height <= 0f) return default;
        switch (area)
        {
            case LobbyArea.Right:
                {
                    var left = panel.Right + Gap;
                    var scale = Math.Min(1f, (rightEdge - left) / button.Width);
                    if (panel.Width <= 0f || scale < MinScale) return default;
                    var height = button.Height * scale;
                    var step = Step * scale;
                    var room = panel.Top - height - floor;
                    var rows = room < 0f ? 0 : Whole(room / step) + 1;
                    return new LobbySpots(rows, button.Width, left - button.Left * scale, panel.Top - button.Top * scale, -step, scale);
                }
            case LobbyArea.Top:
                {
                    var bottom = panel.Top + Gap;
                    var scale = Math.Min(1f, (ceiling - Gap - bottom) / button.Height);
                    if (panel.Width <= 0f || scale < MinScale) return default;
                    var length = (rightEdge - panel.Left) / scale;
                    return new LobbySpots(length > 0f ? 1 : 0, length, panel.Left - button.Left * scale, bottom - button.Bottom * scale, 0f, scale);
                }
            default:
                return default;
        }
    }

    /// <summary>How many whole steps fit in <paramref name="steps"/>; an unbounded room (nothing measured) counts as a great many.</summary>
    private static int Whole(float steps) => !(steps > 0f) ? 0 : steps >= 1e6f ? 1_000_000 : (int)Math.Floor(steps);

    /// <summary>Places the first <paramref name="count"/> buttons (<paramref name="widths"/> at scale 1) in the areas' rows: each fills its
    /// area's rows left to right in order, <see cref="Gap"/> apart, starting a new row when it does not fit what is left of the current one.
    /// First every button goes in its own area (<paramref name="wanted"/>) while that has room, then each one left over, in order, in the
    /// first area with room, Below, Right, Top, after that area's own buttons. <paramref name="area"/> gets the area each went to (-1: no
    /// room anywhere), <paramref name="row"/> and <paramref name="offset"/> its row and its left edge in that row; <paramref name="rowsUsed"/>
    /// how many rows each area filled.</summary>
    internal static void Assign(LobbyArea[] wanted, float[] widths, int count, LobbySpots[] spots, int[] area, int[] row, float[] offset, int[] rowsUsed)
    {
        Span<int> current = stackalloc int[3] { -1, -1, -1 };
        Span<float> used = stackalloc float[3];
        for (var i = 0; i < count; i++)
        {
            area[i] = -1;
            Put(i, (int)wanted[i], widths, spots, current, used, area, row, offset);
        }
        for (var i = 0; i < count; i++)
            for (var a = 0; a < 3 && area[i] < 0; a++)
                Put(i, a, widths, spots, current, used, area, row, offset);
        for (var a = 0; a < 3; a++) rowsUsed[a] = current[a] + 1;
    }

    private static void Put(int i, int a, float[] widths, LobbySpots[] spots, Span<int> current, Span<float> used, int[] area, int[] row, float[] offset)
    {
        const float Slack = 0.5f;
        var w = widths[i];
        var length = spots[a].Length + Slack;
        if (w > length) return;
        if (current[a] >= 0 && used[a] + Gap + w <= length)
        {
            offset[i] = used[a] + Gap;
        }
        else
        {
            if (current[a] + 1 >= spots[a].Rows) return;
            current[a]++;
            offset[i] = 0f;
        }
        used[a] = offset[i] + w;
        area[i] = a;
        row[i] = current[a];
    }
}
