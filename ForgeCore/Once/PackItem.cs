namespace ForgeCore;

/// <summary>The one reading of a <c>Pack:item</c> name, the way mods call a model, an effect or an icon by name: exactly one pack and one item,
/// both non-empty, joined by one colon. Which characters a pack or item may hold is the checking library's own rule (ForgeVisual, ForgeUI).</summary>
public static class PackItem
{
    /// <summary>False unless <paramref name="name"/> is exactly <c>pack:item</c>.</summary>
    public static bool TrySplit(string? name, out string pack, out string item)
    {
        pack = item = "";
        if (string.IsNullOrEmpty(name)) return false;
        var colon = name.IndexOf(':');
        if (colon <= 0 || colon == name.Length - 1 || name.IndexOf(':', colon + 1) >= 0) return false;
        pack = name.Substring(0, colon);
        item = name.Substring(colon + 1);
        return true;
    }
}
