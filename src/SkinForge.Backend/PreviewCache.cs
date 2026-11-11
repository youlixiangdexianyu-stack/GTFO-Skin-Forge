namespace SkinForge.Backend;

// Reading the game is a one-time cost per game build: once the catalog of a build is ready, other builds' cache folders
// are deleted and every item's preview is built in the background (sequential, below-normal priority, `cache-progress`
// events). Requests waiting for the game reader go first: the worker yields between items while any are queued.
public static class PreviewCache
{
    static int _generation;

    public static string Dir(GameAssets g, string itemId) => Path.Combine(g.Cache, "preview", itemId);

    static bool Done(GameAssets g, string itemId) => File.Exists(Path.Combine(Dir(g, itemId), "preview.json"));

    // Reads only the cache folder, never waits for the game reader.
    public static object Status()
    {
        var build = GameAssets.ReadBuildId(Game.Dir);
        var dir = GameAssets.CacheDir(build);
        var catalog = Path.Combine(dir, "catalog.json");
        bool ready = File.Exists(catalog);
        var preview = Path.Combine(dir, "preview");
        return new
        {
            build,
            catalogReady = ready,
            previewsReady = Directory.Exists(preview) ? Directory.EnumerateFiles(preview, "preview.json", SearchOption.AllDirectories).Count() : 0,
            previewsTotal = ready ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(catalog))!.AsArray().Count : 0,
        };
    }

    public static void Ready(GameAssets g, List<CatalogItem> items)
    {
        foreach (var dir in Directory.EnumerateDirectories(Path.GetDirectoryName(g.Cache)!).Where(d => d != g.Cache))
            try { Directory.Delete(dir, true); }
            catch (IOException e) { Log.Write($"cache: could not delete {dir}: {e.Message}"); }

        int generation = Interlocked.Increment(ref _generation);
        var ids = items.Select(i => i.Id).Where(id => !Done(g, id)).ToList();
        if (ids.Count == 0) return;
        new Thread(() => Warm(ids, generation)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "preview-cache" }.Start();
    }

    static void Warm(List<string> ids, int generation)
    {
        for (int i = 0; i < ids.Count && generation == Volatile.Read(ref _generation); i++)
        {
            while (Methods.Waiting > 0) Thread.Sleep(20);
            try { Methods.WithGame(g => Preview.Weapon(g, ids[i], -1), background: true); }
            catch (Exception e) { Log.Write($"cache: preview {ids[i]} failed: {e.Message}"); }
            Program.Send(new { @event = "cache-progress", data = new { done = i + 1, total = ids.Count, text = ids[i] } });
        }
    }
}
