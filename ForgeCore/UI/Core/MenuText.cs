
namespace ForgeUI;

/// <summary>The text colours of the game's own menu pages, for <see cref="MenuText.Tint"/>.</summary>
public enum MenuTone : byte
{
    /// <summary>Full white: a value, the main column.</summary>
    Bright,
    /// <summary>Half white: labels, headers, an empty list.</summary>
    Dim,
    /// <summary>The game's red: failed, cannot be used.</summary>
    Alert,
    /// <summary>The game's yellow: unfinished, a warning.</summary>
    Warn,
    /// <summary>The game's orange: starred, highlighted.</summary>
    Mark,
}

/// <summary>Rich text for the rows of a <c>MenuScreen</c>: the game's menu colours and author text shown as written.</summary>
public static class MenuText
{
    /// <summary><paramref name="text"/> in one of the game's menu colours.</summary>
    public static string Tint(string text, MenuTone tone) => "<color=#" + Hex(tone) + ">" + text + "</color>";

    /// <summary>The colour of <paramref name="tone"/> as RRGGBB or RRGGBBAA.</summary>
    public static string Hex(MenuTone tone) => tone switch
    {
        MenuTone.Dim => "FFFFFF80",
        MenuTone.Alert => "EB4538",
        MenuTone.Warn => "FAC740",
        MenuTone.Mark => "FF8F2B",
        _ => "FFFFFF",
    };

    /// <summary>Author text (a player name, a file name) shown as written: a <c>&lt;</c> in it is not read as a tag.</summary>
    public static string Plain(string? text) => RichText.Plain(text);
}
