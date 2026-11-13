using System;
using System.Collections.Generic;
using System.Text;

namespace ForgeUI;

/// <summary>
/// A player's name as the game hands it over, which may carry TextMeshPro tags the player typed into it (a Steam name such as
/// <c>&lt;#F70&gt;VanHoogie71&lt;/color&gt;</c>). Every place that shows a player name uses this: the colour tags are kept as the name's
/// own colours, every other tag (size, b, i, sprite, link ...) is dropped so a name cannot break a layout, and the text itself is shown as
/// written. Cutting, comparing and sorting use <see cref="Plain"/>.
/// </summary>
public static class PlayerName
{
    /// <summary>The name without any tag: for cutting to a width, comparing and sorting.</summary>
    public static string Plain(string? name) => RichText.StripTags(name);

    /// <summary>The name as rich text: its colour tags as <c>&lt;color=…&gt;</c> (only a valid colour: #RGB, #RGBA, #RRGGBB,
    /// #RRGGBBAA or a colour name), every other tag dropped, the text between them shown as written (not read as tags), and every colour
    /// it opens closed at its end.</summary>
    public static string Rich(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        if (name.IndexOf('<') < 0) return name;
        var b = new StringBuilder(name.Length + 16);
        var run = new StringBuilder(name.Length);
        var open = 0;
        Walk(name, run, (closing, tag) =>
        {
            if (!IsColourTag(tag)) return;
            if (run.Length > 0) { b.Append(RichText.Plain(run.ToString())); run.Clear(); }
            if (closing) { if (open > 0) { b.Append("</color>"); open--; } return; }
            var value = ColourOf(tag);
            if (value == null) return;
            b.Append("<color=").Append(value).Append('>');
            open++;
        });
        if (run.Length > 0) b.Append(RichText.Plain(run.ToString()));
        for (; open > 0; open--) b.Append("</color>");
        return b.ToString();
    }

    /// <summary>The first colour the name sets (#RRGGBB… or a colour name), or null when it sets none: for a name drawn in one colour,
    /// cut to a width from <see cref="Plain"/>.</summary>
    public static string? Colour(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOf('<') < 0) return null;
        string? found = null;
        Walk(name, null, (closing, tag) => { if (found == null && !closing && IsColourTag(tag)) found = ColourOf(tag); });
        return found;
    }

    // Each tag (as StripTags finds them: '<', a letter or '#', up to '>' on the same line) is handed to onTag; the text between goes to run.
    private static void Walk(string text, StringBuilder? run, Action<bool, string> onTag)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<')
            {
                var closing = i + 1 < text.Length && text[i + 1] == '/';
                var start = closing ? i + 2 : i + 1;
                if (start < text.Length && (char.IsLetter(text[start]) || text[start] == '#'))
                {
                    var end = start;
                    while (end < text.Length && text[end] != '>' && text[end] != '<' && text[end] != '\n') end++;
                    if (end < text.Length && text[end] == '>')
                    {
                        onTag(closing, text.Substring(start, end - start));
                        i = end;
                        continue;
                    }
                }
            }
            run?.Append(c);
        }
    }

    private static bool IsColourTag(string tag) => tag.Length > 0 && (tag[0] == '#' || tag.StartsWith("color", StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "black", "blue", "green", "orange", "purple", "red", "white", "yellow",
    };

    // The colour value of a colour tag in a form TextMeshPro reads, or null when it is not a colour (a closing tag has none).
    private static string? ColourOf(string tag)
    {
        var v = tag[0] == '#' ? tag : tag.IndexOf('=') is var eq and > 0 ? tag.Substring(eq + 1) : "";
        v = v.Trim().Trim('"', '\'');
        if (v.Length == 0) return null;
        if (v[0] != '#') return Names.Contains(v) ? v.ToLowerInvariant() : null;
        var hex = v.Substring(1);
        if (hex.Length is not (3 or 4 or 6 or 8)) return null;
        foreach (var h in hex) if (!Uri.IsHexDigit(h)) return null;
        return "#" + hex.ToUpperInvariant();
    }
}
