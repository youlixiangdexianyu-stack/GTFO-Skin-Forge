namespace ForgeCore.Tests;

/// <summary>Local clear progress survives a restart per Rundown, and a damaged file is moved aside whole (detection): the Rundown starts
/// empty, the log says so, and the next clear is saved again.</summary>
internal static class ProgressionChecks
{
    internal static void Run(Action<bool, string> check, List<string> errors)
    {
        var folder = Path.Combine(Path.GetTempPath(), "forgecore-progression-" + Guid.NewGuid().ToString("N"));
        Store.Root = folder;
        try
        {
            const string key = "Local_7_TierA_0";
            ProgressionBook.Record(new ExpeditionClear(7, 1, 0, true, true, false, true, false), key, "Seven");
            ProgressionBook.Record(new ExpeditionClear(7, 1, 0, true, false, false, false, false), key, "Seven");
            Store.Flush();
            ProgressionBook.Forget();
            var read = ProgressionBook.Get(7, key);
            check(read == new ExpeditionProgress(2, 1, 0, 1, 0) && !ProgressionBook.Get(8, key).Cleared,
                "progression: clears are counted, read back after a restart, and another Rundown is untouched");

            var damaged = ProgressionBook.PathOf(9);
            File.WriteAllText(damaged, "{ not json");
            errors.Clear();
            var empty = ProgressionBook.Get(9, key);
            var aside = Directory.GetFiles(Path.GetDirectoryName(damaged)!, "9.forge.damaged-*");
            ProgressionBook.Record(new ExpeditionClear(9, 1, 0, true, false, false, false, false), key, "Nine");
            Store.Flush();
            ProgressionBook.Forget();
            check(!empty.Cleared && aside.Length == 1 && File.ReadAllText(aside[0]) == "{ not json"
                  && errors.Count == 1 && errors[0].Contains("core-progression-file-unreadable") && ProgressionBook.Get(9, key).Main == 1,
                "progression: a damaged file is moved aside untouched, the Rundown starts empty with one error, and the next clear is saved");
        }
        finally
        {
            ProgressionBook.Forget();
            Store.Root = "";
            Directory.Delete(folder, true);
        }
    }
}
