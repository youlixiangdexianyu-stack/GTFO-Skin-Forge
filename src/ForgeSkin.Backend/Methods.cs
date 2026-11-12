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
    // waiting until they hold it, so the background preview cache lets them go first. `gameDir` (the assets.* override)
    // reads another game folder with a reader of its own, still under the gate.
    public static T WithGame<T>(Func<GameAssets, T> read, bool background = false, string? gameDir = null)
    {
        if (!background) Interlocked.Increment(ref _waiting);
        bool counted = !background;
        lock (AssetsGate)
        {
            if (counted) Interlocked.Decrement(ref _waiting);
            if (gameDir != null)
            {
                using var other = new GameAssets(Path.GetFullPath(gameDir));
                return read(other);
            }
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

    // A param of the wrong type is the request's fault (bad-request); errors inside the methods are not.
    static T? Opt<T>(JsonObject p, string name)
    {
        try { return p[name] is { } node ? node.Deserialize<T>(Program.Json) : default; }
        catch (JsonException e) { throw new BackendException("bad-request", $"{name}: {e.Message}"); }
    }

    static T Get<T>(JsonObject p, string name) => Opt<T>(p, name) ?? throw new BackendException("bad-request", $"{name} missing");

    static string Str(JsonObject p, string name) => Get<string>(p, name);

    // Requests run in parallel. Methods that read, change and write a file (settings.json, active.json, projects, plugin
    // files) take one gate; the long ones that write packs, mods and game files take another, so a build or an apply does
    // not hold up a project save.
    static readonly Lock FileGate = new(), JobGate = new();
    static readonly HashSet<string> FileWrites = ["game.setDir", "profiles.add", "profiles.remove", "project.save", "project.delete",
        "pack.install", "pack.remove", "pack.activate", "plugin.install", "plugin.uninstall", "plugin.setDevelopment"];
    static readonly HashSet<string> Jobs = ["pack.build", "pack.exportMod", "assets.apply", "assets.restore", "assets.export"];

    public static object? Call(string method, JsonObject p, long id)
    {
        var gate = FileWrites.Contains(method) ? FileGate : Jobs.Contains(method) ? JobGate : null;
        if (gate == null) return Dispatch(method, p, id);
        lock (gate) return Dispatch(method, p, id);
    }

    static object? Dispatch(string method, JsonObject p, long id) => method switch
    {
        "app.info" => new { version = Version, dataDir = Paths.Data },
        "game.locate" => Game.Locate(),
        "game.setDir" => Game.SetDir(Str(p, "dir")),
        "plugin.log" => Game.PluginLog(Str(p, "profile"), Opt<DateTime?>(p, "since")),
        "profiles.list" => Game.Profiles(),
        "profiles.add" => Game.AddProfile(Str(p, "path")),
        "profiles.remove" => Game.RemoveProfile(Str(p, "path")),
        "catalog.get" => new { items = WithGame(Catalog.Get) },
        "assembly.verify" => WithGame(g => Verify.Profile(g, Str(p, "profile"))),
        "preview.weapon" => WithGame(g => Preview.Weapon(g, Str(p, "id"), id)),
        "match.folder" => Match(Str(p, "weaponId"), Matching.ListImages(Str(p, "folder")), id),
        "match.files" => Match(Str(p, "weaponId"), Get<List<string>>(p, "files"), id),
        "project.new" => Projects.New(Opt<string>(p, "weaponId"), WithGame(Catalog.Get)),
        "project.load" => Projects.Load(Str(p, "path"), WithGame(Catalog.Get)),
        "project.save" => Projects.Save(Get<SkinDocument>(p, "project"), Str(p, "path")),
        "project.list" => Projects.List(Str(p, "dir")),
        "project.delete" => Projects.Delete(Str(p, "path")),
        "project.missing" => Projects.Missing(Get<SkinDocument>(p, "project")),
        "mesh.import" => MeshImport.Import(Str(p, "glb")),
        "pack.build" => Build(Get<SkinDocument>(p, "project"), Str(p, "projectPath"), Str(p, "out"), Opt<string>(p, "sizeMode") ?? "keep", id),
        "pack.install" => Install.Pack(Str(p, "pack"), Str(p, "profile"), Opt<bool?>(p, "activate") ?? true),
        "pack.list" => Install.List(Str(p, "profile")),
        "pack.remove" => Install.Remove(Str(p, "profile"), Str(p, "skinId")),
        "pack.activate" => Install.Activate(Str(p, "profile"), Str(p, "skinId"), Opt<bool?>(p, "active") ?? true),
        "pack.exportMod" => ModExport.Export(Get<string[]>(p, "packs"), Str(p, "out"), Str(p, "name"), Str(p, "version"), Opt<string>(p, "description") ?? "", Str(p, "icon"),
            Opt<string>(p, "format") ?? "forgeskin", () => WithGame(Catalog.Get)),
        "plugin.install" => Install.Plugin(Str(p, "profile")),
        "plugin.uninstall" => Install.PluginUninstall(Str(p, "profile")),
        "plugin.setDevelopment" => Install.SetDevelopment(Str(p, "profile"), Opt<bool?>(p, "on") ?? false),
        "export.obj" => WithGame(g => ObjExport.Export(g, Str(p, "weaponId"), Opt<List<string>>(p, "parts"), Opt<string>(p, "mode") ?? "separate", Str(p, "outDir"))),
        "assets.apply" => AssetPatch.Apply(Str(p, "pack"), Opt<string>(p, "gameDir"), id),
        "assets.restore" => AssetPatch.Restore(Opt<string>(p, "skinId"), Opt<bool?>(p, "discardStale") ?? false, Opt<string>(p, "gameDir")),
        "assets.status" => AssetPatch.Status(Opt<string>(p, "gameDir")),
        "assets.export" => AssetPatch.Export(Str(p, "pack"), Str(p, "out"), Opt<string>(p, "gameDir"), id),
        "schema" => Schema(),
        _ => throw new BackendException("bad-request", $"Unknown method {method}"),
    };

    // Only the catalog and the weapon's textures need the game reader; the user's images are decoded after it is released.
    static MatchResult Match(string weaponId, List<string> files, long id)
    {
        var (items, textures) = WithGame(g => (Catalog.Get(g), Preview.Weapon(g, weaponId, id).Textures));
        return Matching.Match(Catalog.Item(items, weaponId), items, textures, files);
    }

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
