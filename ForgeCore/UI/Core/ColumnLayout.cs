using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>Column widths from what each column wants: all of it when the room allows, otherwise the widest columns are narrowed to one level
/// until the total fits, so a short column of numbers keeps its width and only the long texts are cut.</summary>
internal static class ColumnFit
{
    /// <summary>Writes the width of each column to <paramref name="into"/>; their sum never passes <paramref name="room"/>.</summary>
    public static void Widths(IReadOnlyList<float> wants, float room, float[] into)
    {
        var count = wants.Count;
        var left = Math.Max(0f, room);
        var open = count;
        var sum = 0f;
        for (var i = 0; i < count; i++) { into[i] = -1f; sum += wants[i]; }
        if (sum <= left) { for (var i = 0; i < count; i++) into[i] = wants[i]; return; }
        var changed = true;
        while (open > 0 && changed)
        {
            changed = false;
            var level = left / open;
            for (var i = 0; i < count; i++)
            {
                if (into[i] >= 0f || wants[i] > level) continue;
                into[i] = wants[i]; left -= wants[i]; open--; changed = true;
            }
        }
        if (open == 0) return;
        var rest = left / open;
        for (var i = 0; i < count; i++) if (into[i] < 0f) into[i] = rest;
    }
}

/// <summary>How groups of a key guide stand in columns: in their order, in the fewest columns that fit the height, as level as those columns can be.</summary>
internal static class KeyGuideLayout
{
    /// <summary>The column of each group, for groups of the given heights with <paramref name="gap"/> between groups of a column. At most
    /// <paramref name="maxColumns"/> columns; the height of the tallest column comes back in <paramref name="height"/>. A height no column
    /// can keep to (too many groups for the columns allowed) gives the level layout of <paramref name="maxColumns"/> columns.</summary>
    public static int[] Columns(IReadOnlyList<float> heights, float gap, float maxHeight, int maxColumns, out float height)
    {
        var count = heights.Count;
        var columns = new int[count];
        height = 0f;
        if (count == 0) return columns;
        maxColumns = Math.Clamp(maxColumns, 1, count);
        var used = maxColumns;
        for (var k = 1; k <= maxColumns; k++)
            if (Level(heights, gap, k) <= maxHeight) { used = k; break; }
        height = Level(heights, gap, used);
        var column = 0; var y = 0f;
        for (var i = 0; i < count; i++)
        {
            if (y > 0f && y + gap + heights[i] > height) { column++; y = 0f; }
            columns[i] = column;
            y += (y > 0f ? gap : 0f) + heights[i];
        }
        return columns;
    }

    // The lowest column height that holds the groups in order in k columns: the fill goes over every height any split could give.
    private static float Level(IReadOnlyList<float> heights, float gap, int k)
    {
        var best = float.MaxValue;
        for (var start = 0; start < heights.Count; start++)
        {
            var y = 0f;
            for (var end = start; end < heights.Count; end++)
            {
                y += (end > start ? gap : 0f) + heights[end];
                if (y < best && Holds(heights, gap, k, y)) best = y;
            }
        }
        return best;
    }

    private static bool Holds(IReadOnlyList<float> heights, float gap, int k, float limit)
    {
        var used = 1; var y = 0f;
        foreach (var h in heights)
        {
            if (h > limit) return false;
            if (y > 0f && y + gap + h > limit) { used++; y = 0f; }
            y += (y > 0f ? gap : 0f) + h;
        }
        return used <= k;
    }
}
