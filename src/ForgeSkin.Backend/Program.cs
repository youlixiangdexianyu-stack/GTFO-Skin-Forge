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
    public static string Cache => Path.Combine(Data, "cache");
    public static string Logs => Path.Combine(Data, "logs");
    public static string Projects => Path.Combine(Data, "projects");
    public static string Settings => Path.Combine(Data, "settings.json");

    // Moves pre-rename data (GTFO Skin Forge -> ForgeSkin): .sfproj, .gtfskin and .sfmesh files under the data dir get the
    // new extensions, and the projects and asset-patch records (state.json, packs/ beside it) that name a moved file are
    // pointed at its new name. Once moved nothing old is left, so later starts find nothing to do.
    public static void MovePreRenameData()
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { [".sfproj"] = ".forgeskinproj", [".gtfskin"] = SkinPack.Extension, [".sfmesh"] = ".forgeskinmesh" };
        var moved = 0;
        foreach (var file in Directory.GetFiles(Data, "*", SearchOption.AllDirectories).Where(f => renames.ContainsKey(Path.GetExtension(f))))
        {
            var to = Path.ChangeExtension(file, renames[Path.GetExtension(file)]);
            if (!File.Exists(to)) File.Move(file, to);
            else if (File.ReadAllBytes(to).AsSpan().SequenceEqual(File.ReadAllBytes(file))) File.Delete(file);
            else { Log.Write($"pre-rename {file} kept: {to} exists with other content"); continue; }
            moved++;
        }
        if (moved == 0) return;
        var refs = new System.Text.RegularExpressions.Regex(@"""((?:[^""\\]|\\.)*)(\.sfproj|\.gtfskin|\.sfmesh)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var doc in Directory.GetFiles(Data, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".forgeskinproj", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == "state.json"))
        {
            // Project references are relative to the project file; a state.json names its packs by file name in packs/.
            var baseDir = Path.GetFileName(doc) == "state.json" ? Path.Combine(Path.GetDirectoryName(doc)!, "packs") : Path.GetDirectoryName(doc)!;
            var text = File.ReadAllText(doc);
            var updated = refs.Replace(text, m =>
            {
                var stem = JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"")!;
                var to = Path.Combine(baseDir, stem + renames[m.Groups[2].Value]);
                return File.Exists(to) && !File.Exists(Path.Combine(baseDir, stem + m.Groups[2].Value))
                    ? $"\"{m.Groups[1].Value}{renames[m.Groups[2].Value]}\"" : m.Value;
            });
            if (updated != text) File.WriteAllText(doc, updated);
        }
        Log.Write($"moved {moved} pre-rename files to the new extensions");
    }
}

public static class Log
{
    static readonly Lock Gate = new();
    public static void Write(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}";
        lock (Gate)
        {
            Console.Error.WriteLine(line);
            Directory.CreateDirectory(Paths.Logs);
            File.AppendAllText(Path.Combine(Paths.Logs, "backend.log"), line + Environment.NewLine);
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
        Directory.CreateDirectory(Paths.Data);
        Paths.MovePreRenameData();
        Log.Write($"backend {Methods.Version} data {Paths.Data}");

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var running = new List<Task>();
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            running.Add(Task.Run(() => Handle(line)));
        }
        await Task.WhenAll(running);
    }

    static void Handle(string line)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(line)!.AsObject();
            id = request["id"]?.DeepClone();
            var method = (string?)request["method"] ?? throw new BackendException("bad-request", "method missing");
            var result = Methods.Call(method, request["params"] as JsonObject ?? new JsonObject(), id is null ? 0 : (long)id);
            Send(new { id, result });
        }
        catch (Exception e)
        {
            var (code, message) = e switch
            {
                BackendException b => (b.Code, b.Message),
                SkinFormatException f => (f.Code, f.Message),
                JsonException or InvalidOperationException or FormatException => ("bad-request", e.Message),
                _ => ("internal", e.Message),
            };
            if (code == "internal") Log.Write($"error {e}");
            else Log.Write($"{code}: {message}");
            Send(new { id, error = new { code, message } });
        }
    }
}
