using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// Protocol methods (PLAN §3.3). Each takes the request params and returns a JSON-serialisable result.
public static class Methods
{
    public static string Version => typeof(Methods).Assembly.GetName().Version!.ToString(3);

    static GameAssets? _assets;
    static string? _assetsDir;
    static readonly Lock AssetsGate = new();

    static int _waiting;
    public static int Waiting => Volatile.Read(ref _waiting);

    // One reader per game dir; AssetsTools is not thread safe, so callers hold the gate while reading. Requests count as
    // waiting until they hold it, so the background preview cache lets them go first.
    public static T WithGame<T>(Func<GameAssets, T> read, bool background = false)
    {
        if (!background) Interlocked.Increment(ref _waiting);
        bool counted = !background;
        lock (AssetsGate)
        {
            if (counted) Interlocked.Decrement(ref _waiting);
            var dir = Game.Dir;
            if (_assetsDir != dir)
            {
                _assets?.Dispose();
                _assets = new GameAssets(dir);
                _assetsDir = dir;
            }
            return read(_assets!);
        }
    }

    // Closes the shared reader (it holds the game files open) while `write` replaces game files; the next read reopens it.
    public static void WithoutGame(Action write)
    {
        lock (AssetsGate)
        {
            _assets?.Dispose();
            (_assets, _assetsDir) = (null, null);
            write();
        }
    }

    static string Str(JsonObject p, string name) =>
        (string?)p[name] ?? throw new BackendException("bad-request", $"{name} missing");

    static T Get<T>(JsonObject p, string name) =>
        (p[name] ?? throw new BackendException("bad-request", $"{name} missing")).Deserialize<T>(Program.Json)
        ?? throw new BackendException("bad-request", $"{name} missing");

    public static object? Call(string method, JsonObject p, long id) => method switch
    {
        "app.info" => new { version = Version, dataDir = Paths.Data },
        "game.locate" => Game.Locate(),
        "game.setDir" => Game.SetDir(Str(p, "dir")),
        "plugin.log" => Game.PluginLog(Str(p, "profile"), (DateTime?)p["since"]),
        "profiles.list" => Game.Profiles(),
        "profiles.add" => Game.AddProfile(Str(p, "path")),
        "profiles.remove" => Game.RemoveProfile(Str(p, "path")),
        "catalog.get" => new { items = WithGame(Catalog.Get) },
        "cache.status" => PreviewCache.Status(),
        "assembly.verify" => WithGame(g => Verify.Profile(g, Str(p, "profile"))),
        "preview.weapon" => WithGame(g => Preview.Weapon(g, Str(p, "id"), id)),
        "match.folder" => Match(Str(p, "weaponId"), Matching.ListImages(Str(p, "folder")), id),
        "match.files" => Match(Str(p, "weaponId"), Get<List<string>>(p, "files"), id),
        "project.new" => Projects.New((string?)p["weaponId"], WithGame(Catalog.Get)),
        "project.load" => Projects.Load(Str(p, "path"), WithGame(Catalog.Get)),
        "project.save" => Projects.Save(Get<SkinDocument>(p, "project"), Str(p, "path")),
        "project.list" => Projects.List(Str(p, "dir")),
        "project.delete" => Projects.Delete(Str(p, "path")),
        "mesh.import" => MeshImport.Import(Str(p, "glb")),
        "pack.build" => Build(Get<SkinDocument>(p, "project"), Str(p, "projectPath"), Str(p, "out"), (string?)p["sizeMode"] ?? "keep", id),
        "pack.install" => Install.Pack(Str(p, "pack"), Str(p, "profile"), (bool?)p["activate"] ?? true),
        "pack.list" => Install.List(Str(p, "profile")),
        "pack.remove" => Install.Remove(Str(p, "profile"), Str(p, "skinId")),
        "pack.activate" => Install.Activate(Str(p, "profile"), Str(p, "skinId"), (bool?)p["active"] ?? true),
        "pack.exportMod" => ModExport.Export(Get<string[]>(p, "packs"), Str(p, "out"), Str(p, "name"), Str(p, "version"), (string?)p["description"] ?? "", Str(p, "icon")),
        "plugin.install" => Install.Plugin(Str(p, "profile")),
        "plugin.uninstall" => Install.PluginUninstall(Str(p, "profile")),
        "plugin.setDevelopment" => Install.SetDevelopment(Str(p, "profile"), (bool?)p["on"] ?? false),
        "export.obj" => WithGame(g => ObjExport.Export(g, Str(p, "weaponId"), p["parts"]?.Deserialize<List<string>>(), (string?)p["mode"] ?? "separate", Str(p, "outDir"))),
        "assets.apply" => AssetPatch.Apply(Str(p, "pack"), (string?)p["gameDir"], id),
        "assets.restore" => AssetPatch.Restore((string?)p["skinId"], (bool?)p["discardStale"] ?? false, (string?)p["gameDir"]),
        "assets.status" => AssetPatch.Status((string?)p["gameDir"]),
        "assets.export" => AssetPatch.Export(Str(p, "pack"), Str(p, "out"), (string?)p["gameDir"], id),
        "schema" => Schema(),
        _ => throw new BackendException("bad-request", $"Unknown method {method}"),
    };

    static MatchResult Match(string weaponId, List<string> files, long id) => WithGame(g =>
    {
        var items = Catalog.Get(g);
        return Matching.Match(Catalog.Item(items, weaponId), items, Preview.Weapon(g, weaponId, id).Textures, files);
    });

    static object Build(SkinDocument project, string projectPath, string outPath, string sizeMode, long id)
    {
        // Each step's time goes to backend.log, so a slow apply on another machine shows where it waits.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (catalog, baseline) = WithGame(g => (Catalog.Get(g),
            project.Target.Weapon is { } w ? Preview.Weapon(g, w, id).Textures : new Dictionary<string, Dictionary<TextureChannel, string>>()));
        long game = clock.ElapsedMilliseconds;
        var result = PackBuilder.Build(project, projectPath, outPath, sizeMode, catalog, baseline, id);
        Log.Write($"pack.build {project.Target.Weapon}: game files {game} ms, pack {clock.ElapsedMilliseconds - game} ms");
        return result;
    }

    static JsonNode Schema()
    {
        var options = new JsonSerializerOptions(SkinJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        return options.GetJsonSchemaAsNode(typeof(SkinDocument));
    }
}
