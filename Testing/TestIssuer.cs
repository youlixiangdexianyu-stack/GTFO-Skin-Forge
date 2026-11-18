using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeEngine.Framework;

/// <summary>Plays the website for a test: a stable id gets the number the site's allocator starts from. Two ids that start at one
/// number get it twice; a test that needs the site's collision handling writes the numbers itself.</summary>
internal static class TestIssuer
{
    internal static uint Seed(string id, string type)
    {
        uint hash = ForgeCore.Fnv.Hash32(id);
        // The website's start windows (`seedContentId`): both sides read them from the wire vocabulary.
        return ContentDataBlockIds.UsesUInt16(type)
            ? ContentDataBlockIds.SeedFirst16 + (hash & (ContentDataBlockIds.SeedLast16 - ContentDataBlockIds.SeedFirst16))
            : ContentDataBlockIds.SeedFirstWide + (hash & (ContentDataBlockIds.SeedLastWide - ContentDataBlockIds.SeedFirstWide));
    }

    internal static uint Issue(string id, string type) => Seed(id, type);
    /// <summary>The `ids` table a pack or rundown file carries for these blocks.</summary>
    internal static SortedDictionary<string, uint> Ids(IEnumerable<(string Id, string Type)> blocks)
    {
        var table = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (id, type) in blocks) table[id] = Issue(id, type);
        return table;
    }

    /// <summary>The `ids` table of a pack file that several fixture calls extend: what the file already holds plus this entry's blocks
    /// (every block the entry's content declares, or the entry alone when it declares none).</summary>
    internal static SortedDictionary<string, uint> PackIds(string packFile, string id, object content)
    {
        var table = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        if (File.Exists(packFile))
            using (var document = JsonDocument.Parse(File.ReadAllText(packFile)))
                if (document.RootElement.TryGetProperty("ids", out var ids))
                    foreach (var row in ids.EnumerateObject()) table[row.Name] = row.Value.GetUInt32();
        var blocks = JsonSerializer.SerializeToElement(content) is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("blocks", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Select(row => (row.GetProperty("id").GetString()!, row.GetProperty("type").GetString()!)).ToArray()
            : new[] { (id, "") };
        foreach (var (blockId, type) in blocks) table[blockId] = Issue(blockId, type);
        return table;
    }

    internal static SortedDictionary<string, uint> PackIds(string packFile, string id, string type)
        => PackIds(packFile, id, new { blocks = new[] { new { id, type } } });

    /// <summary>Plays the website for a hand-written fixture file: after a rundown.forge or pack.forge is written, or an entry
    /// beside a pack.forge, the identity file gets the numbers it lacks. Numbers already in the file are kept; a fixture that
    /// tests a missing number writes the file after this call.</summary>
    internal static void CompleteFile(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        string directory = Path.GetDirectoryName(path)!;
        if (name == "pack.forge" && directory.Replace('\\', '/').EndsWith("/forge/content", StringComparison.Ordinal)) CompletePack(path);
        else if (name == "rundown.forge" && directory.Replace('\\', '/').EndsWith("/forge", StringComparison.Ordinal)) CompleteRundown(path);
        else if (name.EndsWith(".forge", StringComparison.Ordinal))
        {
            string pack = Path.Combine(directory, "..", "pack.forge");
            if (File.Exists(pack) && directory.Replace('\\', '/').Contains("/forge/content/", StringComparison.Ordinal)) CompletePack(Path.GetFullPath(pack));
        }
    }

    private static JsonObject? ReadObject(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static void CompleteRundown(string path)
    {
        if (ReadObject(path) is not { } document || document["blocks"] is not JsonArray blocks) return;
        var ids = document["ids"] as JsonObject ?? new JsonObject();
        foreach (var row in blocks.OfType<JsonObject>())
            if (row["id"] is JsonValue id && row["type"] is JsonValue type && id.TryGetValue<string>(out var blockId) && type.TryGetValue<string>(out var blockType) && !ids.ContainsKey(blockId))
                ids[blockId] = Issue(blockId, blockType);
        document["ids"] = ids;
        File.WriteAllText(path, document.ToJsonString());
    }

    private static void CompletePack(string path)
    {
        if (ReadObject(path) is not { } document || document["format"]?.GetValue<string>() != "forge-content-pack") return;
        var ids = document["ids"] as JsonObject ?? new JsonObject();
        foreach (var entryPath in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.forge", SearchOption.AllDirectories))
        {
            if (entryPath == path || ReadObject(entryPath) is not { } entry || entry["format"]?.GetValue<string>() != "forge-content-entry" || entry["id"] is not JsonValue entryId
                || !entryId.TryGetValue<string>(out var id)) continue;
            var blocks = (entry["content"] as JsonObject)?["blocks"] as JsonArray;
            if (blocks == null || blocks.Count == 0) { if (!ids.ContainsKey(id)) ids[id] = Issue(id, ""); continue; }
            foreach (var row in blocks.OfType<JsonObject>())
                if (row["id"] is JsonValue blockId && row["type"] is JsonValue blockType && blockId.TryGetValue<string>(out var rowId) && blockType.TryGetValue<string>(out var rowType) && !ids.ContainsKey(rowId))
                    ids[rowId] = Issue(rowId, rowType);
        }
        document["ids"] = ids;
        File.WriteAllText(path, document.ToJsonString());
    }
    /// <summary>The issued number of every block an entry's content declares, as discovery hands them to a domain.</summary>
    internal static Dictionary<string, uint> BlockIds(string contentJson)
    {
        using var document = JsonDocument.Parse(contentJson);
        var ids = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("blocks", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
                if (row.TryGetProperty("id", out var id) && row.TryGetProperty("type", out var type)) ids[id.GetString()!] = Issue(id.GetString()!, type.GetString()!);
        return ids;
    }
}
