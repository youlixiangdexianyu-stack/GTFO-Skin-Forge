using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>Line breaking for HUD text by estimated width (<see cref="TextWidth.Estimate"/>); call it only when the text changes.</summary>
internal static class TextWrap
{
    /// <summary>Breaks <paramref name="text"/> into at most <paramref name="maxLines"/> lines no wider than <paramref name="width"/> at
    /// <paramref name="px"/>, written into <paramref name="lines"/> (cleared first). Breaks at spaces and after Chinese punctuation, or anywhere in text without them
    /// (Chinese); line breaks in the text are kept; the last line is cut with "…" when the text does not fit. Returns the line count.</summary>
    public static int Lines(string text, float px, float width, int maxLines, List<string> lines)
        => Lines(text, s => TextWidth.Estimate(s, px), width, maxLines, lines);

    /// <summary>As <see cref="Lines(string, float, float, int, List{string})"/>, measuring each candidate line with
    /// <paramref name="measure"/> (the font's real width) instead of the estimate.</summary>
    public static int Lines(string text, Func<string, float> measure, float width, int maxLines, List<string> lines)
    {
        lines.Clear();
        if (string.IsNullOrEmpty(text) || maxLines < 1) return 0;
        var start = 0;
        while (start < text.Length && lines.Count < maxLines)
        {
            var end = start;
            var lastBreak = -1;
            while (end < text.Length && text[end] != '\n')
            {
                if (measure(text.Substring(start, end - start + 1)) > width && end > start) break;
                if (text[end] == ' ') lastBreak = end;
                else if (text[end] is '、' or '，' or '：' or '；' or '。') lastBreak = end + 1;
                end++;
            }
            var hardEnd = end >= text.Length || text[end] == '\n';
            var cut = hardEnd || lastBreak <= start ? end : lastBreak;
            lines.Add(text.Substring(start, cut - start).TrimEnd());
            start = cut;
            if (start < text.Length && (text[start] == '\n' || text[start] == ' ')) start++;
            while (start < text.Length && text[start] == ' ') start++;
        }
        if (start < text.Length && lines.Count > 0)
        {
            var last = lines[lines.Count - 1];
            while (last.Length > 0 && measure(last + "…") > width) last = last.Substring(0, last.Length - 1);
            lines[lines.Count - 1] = last + "…";
        }
        return lines.Count;
    }
}
