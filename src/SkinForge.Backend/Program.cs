using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkinForge.Format;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace SkinForge.Backend;

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
