using System;
using System.Collections.Generic;
using System.IO;

namespace ForgeCore;

/// <summary>How often each layer of one level was cleared on this machine (<see cref="Main"/> is a clear of the level at all).
/// <see cref="NoBooster"/> counts full clears with no booster on any player and no checkpoint used.</summary>
public readonly record struct ExpeditionProgress(int Main, int Secondary, int Third, int AllClear, int NoBooster)
{
    public bool Cleared => Main > 0;
}

/// <summary>One clear this machine recorded: the Rundown block id, the tier (1 = A … 5 = E), the level's index in its tier and the layers it cleared.</summary>
public readonly record struct ExpeditionClear(uint RundownId, int Tier, int Index, bool Main, bool Secondary, bool Third, bool AllClear, bool NoBooster);

/// <summary>The local clear records, one store file per Rundown under <c>BepInEx/ForgeCore/Store/Progression/&lt;Rundown block id&gt;.forge</c>.
/// The block id is the Rundown's identity: the name and title an author shows can change, the id does not. A file that cannot be
/// read, or carries another format version, is moved aside whole (<c>.damaged-&lt;time&gt;</c>, its bytes untouched) and the Rundown starts
/// from no progress, with an error in the log. Main thread only.</summary>
internal static class ProgressionBook
{
    internal const string Version = "1.0.0";
    private const string ModId = "NAinfini.ForgeCore";

    /// <summary>The stored form: counts per native progression key (<c>RundownManager.GetRundownProgressionExpeditionKey</c>).</summary>
    internal sealed class Counts { public int main, secondary, third, allClear, noBooster; }
    internal sealed class File
    {
        public string schemaVersion = Version;
        public uint rundownId;
        public string rundownName = "";
        public Dictionary<string, Counts> expeditions = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<uint, File> Loaded = new();

    internal static string PathOf(uint rundownId) => Path.Combine(Store.Root, "Progression", rundownId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".forge");

    internal static File For(uint rundownId)
    {
        if (Loaded.TryGetValue(rundownId, out var known)) return known;
        var path = PathOf(rundownId);
        var read = Store.Read<File>(ModId, path, report: false);
        var file = read.Status == StoreStatus.Read && read.Value!.schemaVersion == Version && read.Value.expeditions != null ? read.Value : null;
        if (file == null && read.Status != StoreStatus.Missing) MoveAside(path, read.Status == StoreStatus.Read ? "format version " + read.Value!.schemaVersion : read.Detail);
        file ??= new File { rundownId = rundownId };
        return Loaded[rundownId] = file;
    }

    private static void MoveAside(string path, string why)
    {
        var aside = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            System.IO.File.Move(path, aside);
            Log.For(ModId).Error("core-progression-file-unreadable", "Progression file " + path + " cannot be read (" + why + "); it was moved to " + aside + " and this Rundown starts with no progress.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.For(ModId).Error("core-progression-file-unreadable", "Progression file " + path + " cannot be read (" + why + ") and could not be moved aside (" + error.Message + "); this Rundown shows no progress and none is saved this session.");
        }
    }

    internal static ExpeditionProgress Get(uint rundownId, string key)
        => For(rundownId).expeditions.TryGetValue(key, out var c) ? new(c.main, c.secondary, c.third, c.allClear, c.noBooster) : default;

    /// <summary>Adds one clear and saves the Rundown's file.</summary>
    internal static void Record(ExpeditionClear clear, string key, string rundownName)
    {
        var file = For(clear.RundownId);
        file.rundownName = rundownName;
        if (!file.expeditions.TryGetValue(key, out var c)) file.expeditions[key] = c = new Counts();
        if (clear.Main) c.main++;
        if (clear.Secondary) c.secondary++;
        if (clear.Third) c.third++;
        if (clear.AllClear) c.allClear++;
        if (clear.NoBooster) c.noBooster++;
        Store.Write(ModId, PathOf(clear.RundownId), file);
    }

    internal static void Reset(uint rundownId)
    {
        var file = For(rundownId);
        file.expeditions.Clear();
        Store.Write(ModId, PathOf(rundownId), file);
    }

    internal static IEnumerable<KeyValuePair<string, Counts>> Expeditions(uint rundownId) => For(rundownId).expeditions;

    /// <summary>Forgets what was read, so the next use reads the disk again (tests).</summary>
    internal static void Forget() => Loaded.Clear();
}
