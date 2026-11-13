using System;

namespace ForgeUI;

/// <summary>Author text inside the library's composed TextMeshPro strings.</summary>
internal static class RichText
{
    private const string Open = "<noparse>", Close = "</noparse>";

    /// <summary><paramref name="text"/> shown as written: a <c>&lt;</c> in it is not read as a TextMeshPro tag. Text without one
    /// is returned unchanged (no allocation); a literal closing tag inside it is broken with a zero-width space.</summary>
    internal static string Plain(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOf('<') < 0) return text;
        return Open + text.Replace(Close, "<\u200B/noparse>") + Close;
    }

    /// <summary>The game's own text (objectives, prompts, subtitles) without its TextMeshPro tags (<c>&lt;color=#ffffffa6&gt;</c>,
    /// <c>&lt;/indent&gt;</c>, <c>&lt;size=80%&gt;</c>\u2026): the kit draws it in its own style and breaks it into lines, and a tag cut by a
    /// line break would show as text. A <c>&lt;</c> that does not open a tag is kept. Text without one is returned unchanged.</summary>
    internal static string StripTags(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOf('<') < 0) return text;
        var b = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '<')
            {
                var start = i + 1 < text.Length && text[i + 1] == '/' ? i + 2 : i + 1;
                if (start < text.Length && (char.IsLetter(text[start]) || text[start] == '#'))
                {
                    var end = start;
                    while (end < text.Length && text[end] != '>' && text[end] != '<' && text[end] != '\n') end++;
                    if (end < text.Length && text[end] == '>') { i = end; continue; }
                }
            }
            b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>The game's text without its tags (as <see cref="StripTags"/>), with the stretches the game highlights written into
    /// <paramref name="spans"/> (cleared first) as [start, end) in the returned text. Highlighted is what sits inside a colour tag
    /// whose colour is not a grey (<c>&lt;color=orange&gt;HISEC_CARGO_386&lt;/color&gt;</c>); the dim grey the game wraps whole
    /// lines in is not.</summary>
    internal static string Highlights(string? text, System.Collections.Generic.List<(int Start, int End)> spans)
    {
        spans.Clear();
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOf('<') < 0) return text;
        var b = new System.Text.StringBuilder(text.Length);
        var colours = new System.Collections.Generic.List<bool>();
        var from = -1;
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
                        var tag = text.Substring(start, end - start);
                        if (tag.StartsWith("color", StringComparison.OrdinalIgnoreCase))
                        {
                            if (closing) { if (colours.Count > 0) colours.RemoveAt(colours.Count - 1); }
                            else colours.Add(Accent(tag.IndexOf('=') is var eq and > 0 ? tag.Substring(eq + 1) : ""));
                        }
                        else if (!closing && tag[0] == '#') colours.Add(Accent(tag));
                        i = end;
                        continue;
                    }
                }
            }
            var lit = colours.Count > 0 && colours[colours.Count - 1];
            if (lit && from < 0) from = b.Length;
            else if (!lit && from >= 0) { spans.Add((from, b.Length)); from = -1; }
            b.Append(c);
        }
        if (from >= 0) spans.Add((from, b.Length));
        return b.ToString();
    }

    /// <summary>The game's one-line text (an interaction prompt) without its tags, its coloured stretches kept in the kit's colours: what
    /// the game colours red (its warnings, "[WARNING:… DETECTED]") in <paramref name="warningHex"/>, any other colour that is not a grey
    /// (an item, a zone) in <paramref name="highlightHex"/> (both RRGGBB), the rest shown as written.</summary>
    internal static string Toned(string? text, string highlightHex, string warningHex)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOf('<') < 0) return text;
        var b = new System.Text.StringBuilder(text.Length + 32);
        var run = new System.Text.StringBuilder(text.Length);
        var tones = new System.Collections.Generic.List<byte>();
        byte shown = 0;
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
                        var tag = text.Substring(start, end - start);
                        if (tag.StartsWith("color", StringComparison.OrdinalIgnoreCase))
                        {
                            if (closing) { if (tones.Count > 0) tones.RemoveAt(tones.Count - 1); }
                            else tones.Add(Tone(tag.IndexOf('=') is var eq and > 0 ? tag.Substring(eq + 1) : ""));
                        }
                        else if (!closing && tag[0] == '#') tones.Add(Tone(tag));
                        i = end;
                        continue;
                    }
                }
            }
            var tone = tones.Count > 0 ? tones[tones.Count - 1] : (byte)0;
            if (tone != shown) { Flush(b, run, shown, highlightHex, warningHex); shown = tone; }
            run.Append(c);
        }
        Flush(b, run, shown, highlightHex, warningHex);
        return b.ToString();
    }

    private static void Flush(System.Text.StringBuilder into, System.Text.StringBuilder run, byte tone, string highlightHex, string warningHex)
    {
        if (run.Length == 0) return;
        var piece = Plain(run.ToString());
        run.Clear();
        if (tone == 0) { into.Append(piece); return; }
        into.Append("<color=#").Append(tone == 2 ? warningHex : highlightHex).Append('>').Append(piece).Append("</color>");
    }

    /// <summary>0 for a grey, 2 for a red, 1 for any other colour (see <see cref="Accent"/>).</summary>
    private static byte Tone(string value) => !Accent(value) ? (byte)0 : Red(value) ? (byte)2 : (byte)1;

    /// <summary>Whether a TextMeshPro colour that is not a grey is a red (the name, or a hue within 20 degrees of red).</summary>
    private static bool Red(string value)
    {
        var v = value.Trim().Trim('"', '\'');
        if (v.Length == 0) return false;
        if (v[0] != '#') return v.Equals("red", StringComparison.OrdinalIgnoreCase);
        if (!Rgb(v, out var r, out var g, out var bl)) return false;
        var max = Math.Max(r, Math.Max(g, bl));
        var min = Math.Min(r, Math.Min(g, bl));
        if (max != r || max - min <= 0f) return false;
        var hue = 60f * (g - bl) / (max - min);
        return hue <= 20f && hue >= -20f;
    }

    private static bool Rgb(string v, out float r, out float g, out float bl)
    {
        r = g = bl = 0f;
        var hex = v.Substring(1);
        if (hex.Length is 3 or 4) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length < 6 || !int.TryParse(hex.Substring(0, 6), System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
        r = (rgb >> 16) & 255; g = (rgb >> 8) & 255; bl = rgb & 255;
        return true;
    }

    /// <summary>Whether a TextMeshPro colour (a name or #RGB, #RRGGBB, #RRGGBBAA) is a colour rather than a grey.</summary>
    internal static bool Accent(string value)
    {
        var v = value.Trim().Trim('"', '\'');
        if (v.Length == 0) return false;
        if (v[0] != '#')
            return !(v.Equals("white", StringComparison.OrdinalIgnoreCase) || v.Equals("black", StringComparison.OrdinalIgnoreCase)
                || v.Equals("grey", StringComparison.OrdinalIgnoreCase) || v.Equals("gray", StringComparison.OrdinalIgnoreCase));
        if (!Rgb(v, out var r, out var g, out var bl)) return false;
        var max = Math.Max(r, Math.Max(g, bl));
        var min = Math.Min(r, Math.Min(g, bl));
        return max > 40f && (max - min) / max > 0.2f;
    }

    /// <summary><paramref name="line"/>, a piece of a text whose first character is character <paramref name="start"/> of the whole,
    /// with the whole text's highlighted <paramref name="spans"/> drawn in <paramref name="colourHex"/> (RRGGBB) and the rest shown as
    /// written.</summary>
    internal static string Mark(string line, int start, System.Collections.Generic.IReadOnlyList<(int Start, int End)> spans, string colourHex)
    {
        if (spans.Count == 0 || line.Length == 0) return Plain(line);
        var b = new System.Text.StringBuilder(line.Length + 32);
        var i = 0;
        while (i < line.Length)
        {
            var lit = Lit(start + i, spans);
            var j = i + 1;
            while (j < line.Length && Lit(start + j, spans) == lit) j++;
            var piece = Plain(line.Substring(i, j - i));
            if (lit) b.Append("<color=#").Append(colourHex).Append('>').Append(piece).Append("</color>");
            else b.Append(piece);
            i = j;
        }
        return b.ToString();
    }

    private static bool Lit(int index, System.Collections.Generic.IReadOnlyList<(int Start, int End)> spans)
    {
        for (var k = 0; k < spans.Count; k++)
            if (index >= spans[k].Start && index < spans[k].End) return true;
        return false;
    }
}
