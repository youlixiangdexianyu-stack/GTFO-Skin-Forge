using System;
using System.Collections.Generic;
using System.Globalization;
using GameData;
using Localization;
using GameLanguage = Localization.Language;

namespace ForgeUI;

/// <summary>
/// The library's text service: one original Text data block, addressed by its `persistentID`, turned into the text
/// a screen shows in a chosen language. A text a mod hands to the screen is either a plain literal or a reference
/// (<c>forge:text:{id}@{PrimaryLanguage}</c>); the reference is
/// resolved against the game's own Text block where it is shown, so each machine reads its own language column and
/// no Forge-side copy of the text exists. The caller chooses the language; this service does not read the
/// player's language setting.
///
/// An empty or absent column falls back only to the reference's own declared primary language, never to the
/// game's own fallback chain, so the author's choice of primary column is what a missing translation shows.
/// </summary>
public static class GameText
{
    /// <summary>The prefix every Text-block reference starts with.</summary>
    public const string ReferencePrefix = "forge:text:";

    /// <summary>Whether the text is a reference to be resolved per language rather than a literal.</summary>
    public static bool IsReference(string? text)
        => text?.StartsWith(ReferencePrefix, StringComparison.Ordinal) == true;

    /// <summary>Renders a literal as itself and a reference as its Text-block column in <paramref name="language"/>.
    /// False when the reference is malformed, the block is absent, or no column (the language's, then the declared
    /// primary's) holds text.</summary>
    public static bool TryRender(string literal, GameLanguage language, out string rendered)
        => TryRender(literal, language, false, out rendered, out _, out _);

    /// <summary>As <see cref="TryRender(string, GameLanguage, out string)"/>, and the exception a native read threw
    /// (null when the reference was simply malformed or absent) so the caller can report it.</summary>
    public static bool TryRender(string literal, GameLanguage language, out string rendered, out Exception? error)
        => TryRender(literal, language, false, out rendered, out _, out error);

    /// <summary>Reads an optional text without turning an authored empty column into a missing resource: an empty
    /// result is true with <paramref name="empty"/> set. A malformed reference or an absent block is still false, and
    /// <paramref name="error"/> carries the exception a native read threw (null when the reference was simply
    /// malformed or absent) so the caller can report it.</summary>
    public static bool TryRenderOptional(string literal, GameLanguage language, out string rendered, out bool empty, out Exception? error)
        => TryRender(literal, language, true, out rendered, out empty, out error);

    /// <summary>The native localized literal that carries an already rendered text and, by default, no translation
    /// id. A caller whose consumer identifies the content by its id alone (a replication packet that carries only
    /// <c>Id</c>) passes that id; it is written as given and read back. The interop string constructor invokes the
    /// value-type constructor on an unallocated wrapper, so only the parameterless constructor is used and all
    /// three fields are written and read back explicitly.</summary>
    /// <exception cref="InvalidOperationException">The native value did not retain the text, naming the phase.</exception>
    public static LocalizedText Localized(string rendered, uint id = 0)
    {
        string phase = "allocate";
        try
        {
            var localized = new LocalizedText();
            phase = "write-text";
            localized.UntranslatedText = rendered;
            phase = "write-ids";
            localized.Id = id;
            localized.OldId = 0;
            phase = "readback";
            if (localized.UntranslatedText != rendered || localized.Id != id || localized.OldId != 0)
                throw new InvalidOperationException("The native localized literal did not retain its text and translation ids.");
            return localized;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("LocalizedText " + phase, error);
        }
    }

    private static bool TryRender(string literal, GameLanguage language, bool allowEmpty,
        out string rendered, out bool empty, out Exception? error)
    {
        empty = false;
        error = null;
        rendered = literal;
        if (!IsReference(literal)) return true;
        rendered = "";
        if (!TryParsed(literal, out var reference, out var primary)) return false;
        try
        {
            var block = GameDataBlockBase<TextDataBlock>.GetBlock(reference.Id);
            if (block == null || block.WasCollected) return false;
            string? text = Column(block, language);
            if (string.IsNullOrEmpty(text)) text = Column(block, primary);
            if (string.IsNullOrEmpty(text))
            {
                empty = true;
                return allowEmpty;
            }
            rendered = text;
            return true;
        }
        catch (Exception contained) { error = contained; return false; }
    }

    private readonly record struct Reference(uint Id, GameLanguage Primary);

    // A reference literal is immutable text, so its parse is cached per literal. Only the parse is cached: the
    // Text block and its columns are read on every call. The table is bounded and simply restarted when it fills.
    private const int ParsedCapacity = 2048;
    private static readonly object ParsedGate = new();
    private static readonly Dictionary<string, (bool Valid, Reference Reference)> Parsed = new(StringComparer.Ordinal);

    private static bool TryParsed(string literal, out Reference reference, out GameLanguage primary)
    {
        lock (ParsedGate)
        {
            if (!Parsed.TryGetValue(literal, out var known))
            {
                bool valid = TryParse(literal, out var parsed);
                if (Parsed.Count >= ParsedCapacity) Parsed.Clear();
                Parsed[literal] = known = (valid, parsed);
            }
            reference = known.Reference;
            primary = reference.Primary;
            return known.Valid;
        }
    }

    private static bool TryParse(string literal, out Reference parsed)
    {
        parsed = default;
        var header = literal.AsSpan(ReferencePrefix.Length);
        // A template's packed values are the caller's to fill; this service reads one block column.
        if (header.IndexOf(':') >= 0) return false;
        int languageSeparator = header.IndexOf('@');
        if (languageSeparator <= 0 || languageSeparator == header.Length - 1) return false;
        if (!uint.TryParse(header[..languageSeparator], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0)
            return false;
        string name = header[(languageSeparator + 1)..].ToString();
        if (Array.IndexOf(Enum.GetNames<GameLanguage>(), name) < 0
            || !Enum.TryParse(name, false, out GameLanguage primary)) return false;
        parsed = new Reference(id, primary);
        return true;
    }

    // Read the filled column itself. Native localization's own fallback must not override the author's primary.
    private static string? Column(TextDataBlock block, GameLanguage language) => language switch
    {
        GameLanguage.English => block.English,
        GameLanguage.French => block.French?.Translation,
        GameLanguage.Italian => block.Italian?.Translation,
        GameLanguage.German => block.German?.Translation,
        GameLanguage.Spanish => block.Spanish?.Translation,
        GameLanguage.Russian => block.Russian?.Translation,
        GameLanguage.Portuguese_Brazil => block.Portuguese_Brazil?.Translation,
        GameLanguage.Polish => block.Polish?.Translation,
        GameLanguage.Japanese => block.Japanese?.Translation,
        GameLanguage.Korean => block.Korean?.Translation,
        GameLanguage.Chinese_Traditional => block.Chinese_Traditional?.Translation,
        GameLanguage.Chinese_Simplified => block.Chinese_Simplified?.Translation,
        _ => null,
    };
}
