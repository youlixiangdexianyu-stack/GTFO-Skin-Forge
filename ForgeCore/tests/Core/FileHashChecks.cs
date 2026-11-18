namespace ForgeCore.Tests;

/// <summary>The cross-launch file hash cache never hands back a stale hash (detection): a file whose content changed is read again,
/// and a damaged cache file is discarded whole instead of trusted.</summary>
internal static class FileHashChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "forgecore-filehash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "image.bin");
            FileHashes.CacheFile = Path.Combine(folder, "cache", "file-sha256.txt");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            var first = FileHashes.Sha256Hex(file);
            check(first == Hash.Sha256Hex(new byte[] { 1, 2, 3 }) && File.Exists(FileHashes.CacheFile), "the file is hashed and the cache written");

            File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
            check(FileHashes.Sha256Hex(file) == Hash.Sha256Hex(new byte[] { 1, 2, 3, 4 }), "a changed file is read again, not answered from the cache");

            // A later launch reading a cache whose hash field was damaged: the whole cache is dropped and the file read again.
            File.WriteAllText(FileHashes.CacheFile, File.ReadAllText(FileHashes.CacheFile).Replace(Hash.Sha256Hex(new byte[] { 1, 2, 3, 4 }), new string('A', 64)));
            typeof(FileHashes).GetField("_known", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
            check(FileHashes.Sha256Hex(file) == Hash.Sha256Hex(new byte[] { 1, 2, 3, 4 }), "a damaged cache line discards the cache");
        }
        finally
        {
            FileHashes.CacheFile = null;
            Directory.Delete(folder, true);
        }
    }
}
