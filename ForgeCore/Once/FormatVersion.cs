using System.Text.Json;

namespace ForgeCore;

/// <summary>The version every Forge-owned data format, protocol and manifest declares. A reader takes it from here and refuses any other value; the website reads it from <c>Web/src/forge/forge-version.ts</c>.</summary>
public static class FormatVersion
{
    public const string Current = "1.0.0";

    /// <summary>Whether <paramref name="root"/> declares <see cref="Current"/> as a string in <paramref name="field"/>.</summary>
    public static bool Matches(JsonElement root, string field = "schemaVersion")
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var value)
           && value.ValueKind == JsonValueKind.String && value.GetString() == Current;
}
