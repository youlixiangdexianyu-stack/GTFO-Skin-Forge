using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace ForgeSkin.Backend;

// Read-only view of the installed game's Unity files: DataBlocks, the prefab index of the asset shards, and raw objects.
// Type trees are stripped in the build, so engine classes come from the embedded class database (classdata.tpk) and
// MonoBehaviour fields are read by hand where needed.
public sealed class GameAssets : IDisposable
{
    public readonly string DataDir; // <game>/GTFO_Data
    readonly AssetsManager _am = new() { UseTemplateFieldCache = true, UseQuickLookup = true };
    readonly Dictionary<string, AssetsFileInstance> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<uint, JsonObject>> _blocks = new();
    Dictionary<string, AssetRef>? _prefabs;

    public readonly record struct AssetRef(string File, long PathId);

    public readonly string BuildId, Revision;
    bool _classDb;
    // Game files this tool rewrote (AssetPatch) whose backup is intact, by file name: read from the backup, so previews
    // and bakes always start from the game's own content.
    readonly Dictionary<string, string> _originals;

    public GameAssets(string gameDir)
    {
        DataDir = Path.Combine(gameDir, "GTFO_Data");
        Revision = ReadRevision(gameDir);
        BuildId = ReadBuildId(gameDir);
        Cache = CacheDir(BuildId);
        _originals = AssetPatch.Originals(gameDir, BuildId);
    }

    // The game's revision.txt, else the Steam appmanifest build id.
    static string ReadRevision(string gameDir)
    {
        var revisionFile = Path.Combine(gameDir, "revision.txt");
        var revision = File.Exists(revisionFile) ? File.ReadAllText(revisionFile).Trim() : null;
        var manifest = Path.GetFullPath(Path.Combine(gameDir, "..", "..", "appmanifest_493520.acf"));
        if (string.IsNullOrEmpty(revision) && File.Exists(manifest))
            revision = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), @"""buildid""\s+""(\d+)""").Groups[1].Value;
        return string.IsNullOrEmpty(revision) ? "unknown" : revision;
    }

    // Game build identity for cache keys, read without opening asset files: the revision + the timestamp of resources.assets.
    public static string ReadBuildId(string gameDir) =>
        $"{ReadRevision(gameDir)}-{File.GetLastWriteTimeUtc(Path.Combine(gameDir, "GTFO_Data", "resources.assets")).Ticks}";

    // Cache folder of this game build (PreviewCache deletes the folders no running copy uses once this one is ready).
    public readonly string Cache;
    // Bump when the catalog or preview output changes, so caches written by older backends are rebuilt.
    const int CacheVersion = 15;
    public static string CacheDir(string buildId) => Path.Combine(Paths.GameCache, $"{buildId}-v{CacheVersion}");

    // The game's own textures per game revision a release was made against (tools/build-editor-release.ps1 writes the list
    // with `--write-vanilla-textures`): the Preview.TextureKey of every texture a preview reads.
    static readonly Dictionary<string, HashSet<string>> Vanilla = LoadVanilla();
    static Dictionary<string, HashSet<string>> LoadVanilla()
    {
        using var s = typeof(GameAssets).Assembly.GetManifestResourceStream("vanilla-textures.json");
        return s == null ? new() : JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(s)!;
    }

    // Whether a texture is the game's own; null when this game revision is not on the list (a newer game: not checked).
    public bool? IsVanilla(string textureKey) => Vanilla.TryGetValue(Revision, out var keys) ? keys.Contains(textureKey) : null;

    // Where a file's .resS streams are: beside the game file, also when the file itself is read from a backup.
    public string StreamRoot(AssetsFileInstance inst) => _originals.ContainsKey(Path.GetFileName(inst.path)) ? DataDir : Path.GetDirectoryName(inst.path)!;

    public AssetsFileInstance Load(string name)
    {
        if (!_classDb)
        {
            // Type trees are stripped: engine classes come from the class database, loaded on the first asset read.
            _classDb = true;
            using var tpk = typeof(GameAssets).Assembly.GetManifestResourceStream("classdata.tpk")!;
            _am.LoadClassPackage(tpk);
            _am.LoadClassDatabaseFromPackage(Load("globalgamemanagers").file.Metadata.UnityVersion);
        }
        if (!_files.TryGetValue(name, out var inst))
        {
            var path = _originals.TryGetValue(name, out var original) ? original : Path.Combine(DataDir, name);
            if (!File.Exists(path)) path = Path.Combine(DataDir, "Resources", name); // "unity default resources", "unity_builtin_extra"
            _files[name] = inst = _am.LoadAssetsFile(path, false);
        }
        return inst;
    }

    public AssetTypeValueField Field(AssetsFileInstance inst, long pathId) => _am.GetBaseField(inst, Info(inst, pathId));

    // An object a reference names. Missing only when a game file was rewritten outside the game (an older way of
    // skinning edits the .assets files): every reader stops with this code, never a null reference.
    public const string Damaged = "damaged-game-file";
    public static AssetFileInfo Info(AssetsFileInstance inst, long pathId) => inst.file.GetAssetInfo(pathId)
        ?? throw new BackendException(Damaged, $"{Path.GetFileName(inst.path)}: object {pathId} is missing (the file was changed outside the game)");

    // Follows a PPtr from an object in `from`.
    public (AssetsFileInstance File, long PathId) Resolve(AssetsFileInstance from, AssetTypeValueField pptr)
    {
        int fileId = pptr["m_FileID"].AsInt;
        long pathId = pptr["m_PathID"].AsLong;
        return (fileId == 0 ? from : Load(Path.GetFileName(from.file.Metadata.Externals[fileId - 1].PathName)), pathId);
    }

    public AssetTypeValueField Field((AssetsFileInstance File, long PathId) at) => Field(at.File, at.PathId);

    // DataBlock rows by persistentID. `name` without suffix, e.g. "GearFrontPart".
    public Dictionary<uint, JsonObject> Blocks(string name)
    {
        if (_blocks.TryGetValue(name, out var rows)) return rows;
        var res = Load("resources.assets");
        var target = $"GameData.{name}DataBlock_bin";
        foreach (var info in res.file.GetAssetsOfType(AssetClassID.TextAsset))
        {
            var f = _am.GetBaseField(res, info);
            if (f["m_Name"].AsString != target) continue;
            var json = JsonNode.Parse(DecodeDataBlock(f["m_Script"].AsByteArray))!;
            rows = json["Blocks"]!.AsArray().Select(b => b!.AsObject()).ToDictionary(b => (uint)b["persistentID"]!);
            return _blocks[name] = rows;
        }
        throw new BackendException("not-found", $"DataBlock {name} not in resources.assets");
    }

    // The game's DataBlock TextAssets are a byte permutation of JSON (BinaryEncoder seed, applied backwards).
    static byte[] DecodeDataBlock(byte[] data)
    {
        var s = new uint[4];
        s[0] = 0x0F921568;
        for (int i = 1; i < 4; i++) s[i] = 1812433253u * s[i - 1] + 1;
        uint Next()
        {
            uint t = s[0] ^ (s[0] << 11);
            s[0] = s[1]; s[1] = s[2]; s[2] = s[3];
            return s[3] = s[3] ^ (s[3] >> 19) ^ t ^ (t >> 8);
        }
        int size = (int)(50 + Next() % 20), upper = (int)(47 + Next() % 3);
        var codes = new int[size];
        for (int i = 0; i < size; i++) codes[i] = (int)(2 + Next() % (uint)(upper - 2));
        for (int i = data.Length - 1; i >= 0; i--)
        {
            int o = Math.Min(i + codes[i % size], data.Length - 1);
            (data[i], data[o]) = (data[o], data[i]);
        }
        return data;
    }

    // Prefab path (e.g. "Assets/AssetPrefabs/Items/Gear/Parts/Fronts/Front_UZI_2.prefab") -> GameObject.
    // Built from every AssetShardManifest in the asset-shard scenes; cached per game build. Every build scene has its
    // sharedassets file, and the cache is shared by every game folder of the build, so a missing file stops the read
    // instead of leaving an index without that shard's prefabs (children skipped by TryPrefab) for every reader after it.
    public AssetRef Prefab(string path)
    {
        _prefabs ??= LoadPrefabIndex();
        return _prefabs.TryGetValue(path, out var r) ? r : throw new BackendException("not-found", $"Prefab {path} not in the game's asset shards");
    }

    public bool TryPrefab(string path, out AssetRef at)
    {
        _prefabs ??= LoadPrefabIndex();
        return _prefabs.TryGetValue(path, out at);
    }

    Dictionary<string, AssetRef> LoadPrefabIndex()
    {
        var cache = Path.Combine(Cache, "prefab-index.json");
        Dictionary<string, AssetRef>? cached = null;
        try { cached = JsonSerializer.Deserialize<Dictionary<string, AssetRef>>(File.ReadAllText(cache)); }
        catch (Exception e) when (e is IOException or JsonException) { cached = null; } // no usable cache: rebuild below
        if (cached is not null) return cached;

        var gm = Load("globalgamemanagers");
        var settings = Field(gm, gm.file.GetAssetsOfType(AssetClassID.BuildSettings)[0].PathId);
        var scenes = settings["scenes.Array"].Children.Select(c => c.AsString).ToList();
        var index = new Dictionary<string, AssetRef>();
        for (int i = 0; i < scenes.Count; i++)
        {
            var name = $"sharedassets{i}.assets";
            var inst = Load(name);
            if (scenes[i].StartsWith("Assets/AssetShards/Scenes/"))
            {
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    var mb = _am.GetBaseField(inst, info);
                    if (ScriptName(inst, mb) != "AssetShardManifest") continue;
                    foreach (var (key, file, pathId) in ReadManifest(inst, info))
                        index.TryAdd(key, new AssetRef(file, pathId));
                }
            }
            else
            {
                // Ejected shell casings (WeaponShellManager pools) are scene objects named Shell_*; indexed as "shell/<name>".
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
                {
                    var go = _am.GetBaseField(inst, info);
                    if (go["m_Name"].AsString is var n && n.StartsWith("Shell_"))
                        index.TryAdd("shell/" + n, new AssetRef(name, info.PathId));
                }
            }
            _am.UnloadAssetsFile(inst);
            _files.Remove(name);
        }
        Directory.CreateDirectory(Cache);
        AtomicFile.WriteText(cache, JsonSerializer.Serialize(index));
        return index;
    }

    public string ScriptName(AssetsFileInstance inst, AssetTypeValueField mb)
    {
        var script = Resolve(inst, mb["m_Script"]);
        return script.PathId == 0 ? "" : Field(script)["m_ClassName"].AsString;
    }

    // AssetShardManifest (ScriptableObject): MonoBehaviour header, then SerializableDictionary<string, Object>
    // as List<string> keys + List<PPtr> values.
    IEnumerable<(string Key, string File, long PathId)> ReadManifest(AssetsFileInstance inst, AssetFileInfo info)
    {
        var r = MonoReader(inst, info);
        int n = r.ReadInt32();
        var keys = new string[n];
        for (int i = 0; i < n; i++) { keys[i] = r.ReadCountStringInt32(); r.Align(); }
        int m = r.ReadInt32();
        for (int i = 0; i < m && i < n; i++)
        {
            int fileId = r.ReadInt32();
            long pathId = r.ReadInt64();
            if (pathId == 0) continue;
            var file = fileId == 0 ? Path.GetFileName(inst.path) : Path.GetFileName(inst.file.Metadata.Externals[fileId - 1].PathName);
            yield return (keys[i], file, pathId);
        }
    }

    // Positions a reader after the MonoBehaviour header (m_GameObject, m_Enabled, m_Script, m_Name).
    public static AssetsFileReader MonoReader(AssetsFileInstance inst, AssetFileInfo info)
    {
        var r = inst.file.Reader;
        r.Position = info.GetAbsoluteByteOffset(inst.file);
        r.Position += 12;
        r.ReadByte(); r.Align();
        r.Position += 12;
        r.ReadCountStringInt32(); r.Align();
        return r;
    }

    public void Dispose() => _am.UnloadAll(true);
}
