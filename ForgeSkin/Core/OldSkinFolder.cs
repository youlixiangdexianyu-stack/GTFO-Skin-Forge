namespace ForgeSkin.Format;

/// <summary>TEMPORARY, removed at ForgeSkin 1.4.0 together with Backend/OldSkinMove.cs (Docs/forge-skin/PLAN.md §6).
/// ForgeSkin 1.1.0 and earlier kept the skins in BepInEx/plugins/ForgeSkin/skins: the editor moves them to
/// <see cref="SkinFolders.Skins"/>, the plugin only says when skins are still left there.</summary>
public static class OldSkinFolder
{
    public static string Of(string bepInEx) => Path.Combine(SkinFolders.Plugin(bepInEx), "skins");

    public static bool HasSkins(string bepInEx) => Directory.Exists(Of(bepInEx)) && Directory.EnumerateFiles(Of(bepInEx), "*" + SkinPack.Extension).Any();
}
