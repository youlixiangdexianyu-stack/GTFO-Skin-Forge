namespace ForgeCore;

/// <summary>The one backslash-to-slash conversion: Windows paths and prefab names become forward-slash text. Mirrors
/// <c>toPosix</c> in the website's <c>posix-path.ts</c>. No game or Unity type, so any package may use it.</summary>
public static class PosixPath
{
    /// <summary>Returns <paramref name="value"/> with every backslash replaced by a forward slash.</summary>
    public static string ToPosix(string value) => value.Replace('\\', '/');
}
