namespace SkinForge.Backend;

// Reading the game is a one-time cost per game build: once the catalog of a build is ready, other builds' cache folders
// are deleted and every item's preview is built in the background (sequential, below-normal priority, `cache-progress`
// events). Requests waiting for the game reader go first: the worker yields between items while any are queued.
public static class PreviewCache
{
    static int _generation;

    public static string Dir(GameAssets g, string itemId) => Path.Combine(g.Cache, "preview", itemId);

    static bool Done(GameAssets g, string itemId) => File.Exists(Path.Combine(Dir(g, itemId), "preview.json"));

    public static void Ready(GameAssets g, List<CatalogItem> items)
    {
        foreach (var dir in Directory.EnumerateDirectories(Path.GetDirectoryName(g.Cache)!).Where(d => Path.GetFileName(d) != g.BuildId))
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

    public static object Status(GameAssets g)
    {
        bool ready = Catalog.Ready(g);
        var items = ready ? Catalog.Get(g) : [];
        return new { build = g.BuildId, catalogReady = ready, previewsReady = items.Count(i => Done(g, i.Id)), previewsTotal = items.Count };
    }
}
