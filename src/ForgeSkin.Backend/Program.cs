using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Format;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace ForgeSkin.Backend;

public sealed class BackendException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Portable data directory: GTFO_UserData next to the exe, or --data-dir.
public static class Paths
{
    public static string Data { get; set; } = Path.Combine(AppContext.BaseDirectory, "GTFO_UserData");
    // Folders the editor's data lived in before it was moved (--former-data-dir, one each): a project saved outside the
    // data folder names data files relative to itself, so after a move they are found again here (Projects.Load).
    public static List<string> FormerData { get; } = new();
    public static string Cache => Path.Combine(Data, "cache");
    // What is read out of the game (catalog, weapon previews) depends only on the game build, so every copy of the app on
    // this machine (installed, reinstalled, run from the repository) shares it instead of reading the game again.
    // FORGESKIN_GAME_CACHE puts it elsewhere (tests and measurements that must leave the shared cache alone).
    public static string GameCache { get; set; } = Environment.GetEnvironmentVariable("FORGESKIN_GAME_CACHE") is { Length: > 0 } dir ? Path.GetFullPath(dir) :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeSkinCache", "game");
    public static string Logs => Path.Combine(Data, "logs");
    public static string Projects => Path.Combine(Data, "projects");
    public static string Settings => Path.Combine(Data, "settings.json");

    // Written once the data folder holds no pre-rename file: later starts skip the scan. It moves with the folder (the editor
    // copies the whole folder), and a folder without it (an old one adopted, a fresh one) is scanned once.
    static string PreRenameDone => Path.Combine(Data, "pre-rename-moved");

    // Moves pre-rename data (GTFO Skin Forge -> ForgeSkin): .sfproj, .gtfskin and .sfmesh files under the data dir get the
    // new extensions, and the projects and asset-patch records (state.json, packs/ beside it) that name a moved file are
    // pointed at its new name. A file kept because its new name holds other content keeps the folder unmarked, so it is
    // reported again at every start until it is resolved.
    public static void MovePreRenameData()
    {
        if (File.Exists(PreRenameDone)) return;
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { [".sfproj"] = ".forgeskinproj", [".gtfskin"] = SkinPack.Extension, [".sfmesh"] = ".forgeskinmesh" };
        var moved = 0;
        var kept = 0;
        foreach (var file in Directory.GetFiles(Data, "*", SearchOption.AllDirectories).Where(f => renames.ContainsKey(Path.GetExtension(f))))
        {
            var to = Path.ChangeExtension(file, renames[Path.GetExtension(file)]);
            if (!File.Exists(to)) File.Move(file, to);
            else if (File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(file))) File.Delete(file);
            else { Log.Write($"pre-rename {file} kept: {to} exists with other content"); kept++; continue; }
            moved++;
        }
        // The references are pointed at the new names even when nothing moved this time: an earlier start may have stopped
        // after moving the files and before this pass.
        var refs = new System.Text.RegularExpressions.Regex(@"""((?:[^""\\]|\\.)*)(\.sfproj|\.gtfskin|\.sfmesh)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var doc in Directory.GetFiles(Data, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".forgeskinproj", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "state.json"))
        {
            // Project references are relative to the project file; a state.json names its packs by file name in packs/.
            var baseDir = Path.GetFileName(doc) == "state.json" ? Path.Combine(Path.GetDirectoryName(doc)!, "packs") : Path.GetDirectoryName(doc)!;
            // A document that cannot be read or written now is left as it is and keeps the folder unmarked.
            try
            {
                var text = File.ReadAllText(doc);
                var updated = refs.Replace(text, m =>
                {
                    var stem = JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"")!;
                    var to = Path.Combine(baseDir, stem + renames[m.Groups[2].Value]);
                    return File.Exists(to) && !File.Exists(Path.Combine(baseDir, stem + m.Groups[2].Value))
                        ? $"\"{m.Groups[1].Value}{renames[m.Groups[2].Value]}\"" : m.Value;
                });
                if (updated != text) AtomicFile.WriteText(doc, updated);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Write($"pre-rename references in {doc} not updated: {e.Message}");
                kept++;
            }
        }
        if (moved > 0) Log.Write($"moved {moved} pre-rename files to the new extensions");
        if (kept > 0) return;
        try { AtomicFile.WriteText(PreRenameDone, "No file in this folder has a pre-rename extension (.sfproj, .gtfskin, .sfmesh).\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"{PreRenameDone} could not be written ({e.Message}); the folder is scanned again at the next start"); }
    }
}

public static class Log
{
    static readonly Lock Gate = new();
    // `echo` false keeps the line out of stderr (the editor's log panel): a request error, which the editor reports itself.
    public static void Write(string text, bool echo = true)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}";
        lock (Gate)
        {
            if (echo) Console.Error.WriteLine(line);
            Directory.CreateDirectory(Paths.Logs);
            // Past 2 MB the log moves to backend.1.log (the older ones to .2 and .3, the oldest dropped), as the editor's app.log.
            var file = Path.Combine(Paths.Logs, "backend.log");
            if (File.Exists(file) && new FileInfo(file).Length > 2 << 20)
                for (int i = 3; i >= 1; i--)
                {
                    var from = i == 1 ? file : Path.Combine(Paths.Logs, $"backend.{i - 1}.log");
                    if (File.Exists(from)) File.Move(from, Path.Combine(Paths.Logs, $"backend.{i}.log"), true);
                }
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }
}

// stdio protocol (PLAN §3.3): one JSON request per line in, one response per line out; progress events interleave.
public static class Program
{
    static readonly Lock Out = new();
    static readonly TextWriter Stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

    // Protocol JSON: the document conventions, but dictionary keys kept as given (material names) and nulls written.
    public static readonly JsonSerializerOptions Json = new(SkinJson.Options)
    {
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    static readonly JsonSerializerOptions Line = new(Json) { WriteIndented = false };

    public static void Send(object message)
    {
        var line = JsonSerializer.Serialize(message, Line);
        lock (Out) Stdout.WriteLine(line);
    }

    // Negative ids are background work (the preview cache) and report no progress.
    public static void Progress(long id, int done, int total, string text)
    {
        if (id >= 0) Send(new { @event = "progress", data = new { id, done, total, text } });
    }

    public static async Task Main(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--data-dir") Paths.Data = Path.GetFullPath(args[i + 1]);
            else if (args[i] == "--former-data-dir") Paths.FormerData.Add(Path.GetFullPath(args[i + 1]));
        // Release step: the keys of the located game's own textures, for its revision (GameAssets.IsVanilla).
        if (Array.IndexOf(args, "--write-vanilla-textures") is var at and >= 0 && at + 1 < args.Length)
        {
            var list = args[at + 1];
            var (revision, keys) = Methods.WithGame(g => (g.Revision, Preview.TextureKeys(g)));
            AtomicFile.WriteText(Path.GetFullPath(list), JsonSerializer.Serialize(new Dictionary<string, SortedSet<string>> { [revision] = keys },
                new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n"); // LF: the file is eol=lf in Git
            Console.WriteLine($"{keys.Count} textures of game revision {revision} -> {list}");
            return;
        }
        Directory.CreateDirectory(Paths.Data);
        Paths.MovePreRenameData();
        PackBuilder.PruneCache();
        Log.Write($"backend {Methods.Version} data {Paths.Data}");

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var running = new List<Task>();
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            running.RemoveAll(t => t.IsCompleted); // only the unfinished ones are awaited at exit
            running.Add(Task.Run(() => Handle(line)));
        }
        await Task.WhenAll(running);
    }

    static void Handle(string line)
    {
        JsonNode? id = null;
        try
        {
            JsonObject request;
            string method;
            long number;
            try
            {
                request = JsonNode.Parse(line)!.AsObject();
                id = request["id"]?.DeepClone();
                number = id is null ? 0 : (long)id;
                method = (string?)request["method"] ?? throw new BackendException("bad-request", "method missing");
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NullReferenceException)
            {
                throw new BackendException("bad-request", $"Not a request: {e.Message}");
            }
            var result = Methods.Call(method, request["params"] as JsonObject ?? new JsonObject(), number);
            Send(new { id, result });
        }
        catch (Exception e)
        {
            // Request errors are BackendException("bad-request") from the request and param readers; anything else
            // unexpected is internal and logged with its stack trace.
            var (code, message) = e switch
            {
                BackendException b => (b.Code, b.Message),
                SkinFormatException f => (f.Code, f.Message),
                FileNotFoundException or DirectoryNotFoundException => ("not-found", e.Message),
                IOException when (e.HResult & 0xFFFF) is 32 or 33 => ("file-in-use", e.Message), // sharing or lock violation
                _ => ("internal", e.Message),
            };
            if (code == "internal") Log.Write($"error {e}");
            else Log.Write($"{code}: {message}", echo: false);
            Send(new { id, error = new { code, message } });
        }
    }
}
