namespace ForgeSkin.Backend;

// The game cache of a build: an item's preview is built when it is first asked for. Each backend holds `in-use` open in the
// cache folder it uses; once this build's catalog is ready, every other folder (another game build, another format version)
// that no running copy holds is deleted.
public static class PreviewCache
{
    static FileStream? _inUse;

    public static string Dir(GameAssets g, string itemId) => Path.Combine(g.Cache, "preview", itemId);

    // Game textures as PNG, shared by every item and named by content (Preview.TextureKey).
    public static string Textures(GameAssets g) => Path.Combine(g.Cache, "tex");

    public static void Ready(GameAssets g)
    {
        _inUse?.Dispose();
        Directory.CreateDirectory(g.Cache);
        _inUse = new FileStream(InUse(g.Cache), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        foreach (var dir in Directory.EnumerateDirectories(Paths.GameCache).Where(d => d != g.Cache))
            try
            {
                if (File.Exists(InUse(dir))) using (new FileStream(InUse(dir), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Directory.Delete(dir, true);
            }
            catch (IOException e) { Log.Write($"cache: kept {dir}: {e.Message}"); } // in use by another copy, or a file held open
    }

    static string InUse(string dir) => Path.Combine(dir, "in-use");
}
