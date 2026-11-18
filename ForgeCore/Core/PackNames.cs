namespace ForgeCore;

/// <summary>The character rule of a <c>Pack:item</c> name, shared by the icon names (ForgeUI) and the visual names (ForgeVisual): a pack
/// holds letters, digits, '_' and '-', an item also '.'. Which packs are reserved, and what an item is called in a message, is the caller's.</summary>
internal static class PackNames
{
    /// <summary>Null when <paramref name="pack"/> holds only pack characters, else why not.</summary>
    internal static string? CheckPack(string? pack)
    {
        if (string.IsNullOrEmpty(pack)) return "the pack name is empty";
        foreach (var c in pack)
            if (!IsNameChar(c) || c == '.') return "the pack name '" + pack + "' may only hold letters, digits, '_' and '-'";
        return null;
    }

    /// <summary>Null when <paramref name="item"/> holds only item characters, else why not, calling it a <paramref name="noun"/>.</summary>
    internal static string? CheckItem(string? item, string noun)
    {
        if (string.IsNullOrEmpty(item)) return "the " + noun + " name is empty";
        foreach (var c in item)
            if (!IsNameChar(c)) return "the " + noun + " name '" + item + "' may only hold letters, digits, '_', '-' and '.'";
        return null;
    }

    /// <summary>Splits <c>Pack:item</c> (<see cref="PackItem.TrySplit"/>) and checks both parts; false with the reason when it is not one.
    /// No pack is treated as reserved here.</summary>
    internal static bool TrySplit(string? name, string noun, out string pack, out string item, out string? why)
    {
        if (!PackItem.TrySplit(name, out pack, out item))
        {
            why = string.IsNullOrEmpty(name) ? "the " + noun + " name is empty" : "'" + name + "' is not Pack:item";
            return false;
        }
        why = CheckItem(item, noun) ?? CheckPack(pack);
        return why == null;
    }

    private static bool IsNameChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.';
}
