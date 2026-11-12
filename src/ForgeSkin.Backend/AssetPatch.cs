using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SkiaSharp;
using ForgeSkin.Format;

namespace ForgeSkin.Backend;

// Asset replacement (PLAN §3.3 `assets.*`): a pack's material textures and parameters written straight into the game's own
// .assets files, for players without BepInEx. Each override gets a new Texture2D, with its image data inline, in the
// Material's file, and that Material's slot points at it; the game's own textures and .resS streams are never touched, so
// other materials that share a texture keep it and only the Material files change.
// State lives under <dataDir>/game-assets/<build>-<dir hash>/: originals/ (each file once, before its first change),
// packs/ (a copy of every applied pack) and state.json. Every patched file is rebuilt from its original with all applied
// skins in order (a later skin wins per channel), so applying, re-applying and removing never stack edits.
public static class AssetPatch
{
    // A Material object in a game file.
    public sealed record Target(string File, long PathId, string Material);
    public sealed record AppliedSkin(string Id, string Name, string Version, string Pack, DateTime AppliedAt, List<Target> Targets, List<string> Warnings);
    // Original: the game's file before the first change (its write time is put back on every patched copy, so the game's
    // build id, which includes resources.assets' time, stays the same). Patched: what this tool last wrote.
    public sealed record FileRecord(string OriginalSha, long OriginalSize, DateTime OriginalTime, string? PatchedSha);

    public sealed class State
    {
        public string GameDir { get; set; } = "";
        public string BuildId { get; set; } = "";
        public Dictionary<string, FileRecord> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AppliedSkin> Skins { get; set; } = new();
    }

    sealed record Edit(long PathId, MaterialOverride Override, SkinPack Pack, string Skin);

    static readonly TextureChannel[] Linear = [TextureChannel.Normal, TextureChannel.MetallicSmoothness];

    // ---- methods ----

    public static object Apply(string packPath, string? gameDir, long requestId)
    {
        var dir = GameDir(gameDir);
        NotRunning();
        using var pack = SkinPack.Open(packPath);
        var doc = pack.Manifest;
        var warnings = Unsupported(doc);
        var (buildId, targets) = Read(gameDir, g => (g.BuildId, Resolve(g, doc, warnings)));
        if (targets.Count == 0) throw new BackendException("nothing-to-apply", $"{doc.Id}: no material texture or parameter that game files can hold");

        var stateDir = StateDir(buildId, dir);
        var state = LoadState(stateDir) ?? new State { GameDir = dir, BuildId = buildId };
        var old = state.Skins.FirstOrDefault(s => s.Id == doc.Id);
        var files = targets.Select(t => t.File).Concat(old?.Targets.Select(t => t.File) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var copy = Path.Combine(stateDir, "packs", Preview.Safe(doc.Id) + SkinPack.Extension);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if (!Path.GetFullPath(packPath).Equals(copy, StringComparison.OrdinalIgnoreCase)) File.Copy(packPath, copy, true);
        var skin = new AppliedSkin(doc.Id, doc.Name, doc.Version, Path.GetFileName(copy), DateTime.UtcNow, targets, warnings);
        if (old != null) state.Skins[state.Skins.IndexOf(old)] = skin;
        else state.Skins.Add(skin);

        try
        {
            Rebuild(dir, stateDir, state, files, s => s == doc.Id ? warnings : [], requestId);
        }
        catch
        {
            // Nothing backed up yet: drop the folder. Else the record keeps its files but not this skin; a file already
            // rebuilt with it loses it on the next rebuild, since every rebuild starts from the original.
            if (LoadState(stateDir) is not { } saved) Directory.Delete(stateDir, true);
            else
            {
                saved.Skins.RemoveAll(s => s.Id == doc.Id);
                if (old != null) saved.Skins.Add(old);
                SaveState(stateDir, saved);
            }
            throw;
        }
        return new { skin = doc.Id, buildId, files, materials = targets.Select(t => t.Material).Distinct().ToList(), warnings };
    }

    // Without skinId: every original back and the record cleared. With skinId: that skin removed and its files rebuilt from
    // the originals with the remaining skins. A file the game changed since (update or Steam verify) is left alone and reported.
    public static object Restore(string? skinId, bool discardStale, string? gameDir)
    {
        var dir = GameDir(gameDir);
        NotRunning();
        var buildId = GameAssets.ReadBuildId(dir);
        var stateDir = StateDir(buildId, dir);
        var state = LoadState(stateDir);
        var restored = new List<string>();
        var skipped = new List<object>();
        if (skinId != null)
        {
            var skin = state?.Skins.FirstOrDefault(s => s.Id == skinId) ?? throw new BackendException("not-found", $"Skin {skinId} is not applied to the game files");
            state.Skins.Remove(skin);
            var files = skin.Targets.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            restored.AddRange(Rebuild(dir, stateDir, state!, files, _ => [], -1));
            if (Directory.Exists(stateDir)) File.Delete(Path.Combine(stateDir, "packs", skin.Pack));
        }
        else if (state != null)
        {
            var data = Path.Combine(dir, "GTFO_Data");
            foreach (var (file, rec) in state.Files.ToList())
            {
                var target = Path.Combine(data, file);
                var now = File.Exists(target) ? Sha(target) : null;
                if (now != rec.PatchedSha && now != rec.OriginalSha)
                {
                    skipped.Add(new { file, reason = "changed" });
                    continue;
                }
                PutOriginal(stateDir, data, file, rec);
                state.Files.Remove(file);
                restored.Add(file);
            }
            state.Skins.Clear();
            if (state.Files.Count == 0) Directory.Delete(stateDir, true);
            else SaveState(stateDir, state);
        }
        var stale = Stale(dir, buildId);
        if (discardStale)
            foreach (var (path, _) in stale) Directory.Delete(path, true);
        return new { restored, skipped, stale = discardStale ? [] : stale.Select(s => StaleInfo(s.State)).ToList() };
    }

    public static object Status(string? gameDir)
    {
        var dir = GameDir(gameDir);
        var buildId = GameAssets.ReadBuildId(dir);
        var stateDir = StateDir(buildId, dir);
        var state = LoadState(stateDir);
        var data = Path.Combine(dir, "GTFO_Data");
        var files = state?.Files.Select(kv =>
        {
            var target = Path.Combine(data, kv.Key);
            var now = File.Exists(target) ? Sha(target) : null;
            var backup = Path.Combine(stateDir, "originals", kv.Key);
            return new
            {
                file = kv.Key,
                // patched: holds what this tool wrote; original: the game put its own file back (Steam verify); changed: neither
                // (a game update or another tool); missing: the file is gone.
                state = now == null ? "missing" : now == kv.Value.PatchedSha ? "patched" : now == kv.Value.OriginalSha ? "original" : "changed",
                backupOk = File.Exists(backup) && Sha(backup) == kv.Value.OriginalSha,
                skins = state.Skins.Where(s => s.Targets.Any(t => t.File.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))).Select(s => s.Id).ToList(),
            };
        }).ToList() ?? [];
        return new
        {
            gameDir = dir,
            buildId,
            running = Game.Running,
            files,
            skins = state?.Skins.Select(s => new { s.Id, s.Name, s.Version, s.AppliedAt, materials = s.Targets.Select(t => t.Material).Distinct().ToList(), s.Warnings }).ToList() ?? [],
            // Records of older builds of this game dir: their backups are not restored onto the newer build.
            stale = Stale(dir, buildId).Select(s => StaleInfo(s.State)).ToList(),
        };
    }

    // A zip for players without the app: the patched copies of the files this pack changes, made from the originals of the
    // current game build (the backups where this game is patched), plus README.txt. The game folder is not touched. The new
    // textures are inline in the .assets files and the .resS streams are unchanged, so no .resS goes in the zip.
    public static object Export(string packPath, string outPath, string? gameDir, long requestId)
    {
        var dir = GameDir(gameDir);
        using var pack = SkinPack.Open(packPath);
        var doc = pack.Manifest;
        var warnings = Unsupported(doc);
        var (buildId, targets) = Read(gameDir, g => (g.BuildId, Resolve(g, doc, warnings)));
        if (targets.Count == 0) throw new BackendException("nothing-to-apply", $"{doc.Id}: no material texture or parameter that game files can hold");
        var stateDir = StateDir(buildId, dir);
        var state = LoadState(stateDir);
        var data = Path.Combine(dir, "GTFO_Data");

        if (!outPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) outPath = Path.Combine(outPath, Preview.Safe(doc.Id) + "-game-assets.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var work = Directory.CreateDirectory(Path.Combine(Paths.Cache, "assets-export", Guid.NewGuid().ToString("N"))).FullName;
        var written = new List<(string File, string Path, string OriginalSha)>();
        try
        {
            var byFile = targets.GroupBy(t => t.File, StringComparer.OrdinalIgnoreCase).ToList();
            int done = 0;
            foreach (var group in byFile)
            {
                Program.Progress(requestId, done++, byFile.Count, group.Key);
                string source, originalSha;
                if (state?.Files.TryGetValue(group.Key, out var rec) == true)
                    (source, originalSha) = (Path.Combine(stateDir, "originals", group.Key), rec.OriginalSha);
                else
                {
                    source = Path.Combine(data, group.Key);
                    originalSha = NotStale(dir, buildId, group.Key, source);
                }
                var outFile = Path.Combine(work, group.Key);
                Patch(source, data, group.Select(t => new Edit(t.PathId, Override(doc, t), pack, doc.Id)).ToList(), outFile, _ => warnings);
                written.Add((group.Key, outFile, originalSha));
            }
            var temp = outPath + ".tmp";
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var (file, path, _) in written) zip.CreateEntryFromFile(path, "GTFO_Data/" + file, CompressionLevel.Optimal);
                using var readme = new StreamWriter(zip.CreateEntry("README.txt").Open(), new UTF8Encoding(true));
                readme.Write(Readme(doc, dir, buildId, written.Select(w => (w.File, w.OriginalSha)).ToList(), warnings));
            }
            File.Move(temp, outPath, true);
        }
        finally
        {
            Directory.Delete(work, true);
        }
        return new { zip = outPath, buildId, files = written.Select(w => w.File).ToList(), warnings };
    }

    // ---- planning ----

    static bool Writable(MaterialOverride m) =>
        m.Textures.Count > 0 || m.Params is { EmissiveEnabled: not null } or { EmissiveColor: not null } or { EmissiveIntensity: not null }
            or { Metallic: not null } or { Smoothness: not null } or { Tint: not null };

    static MaterialOverride Override(SkinDocument doc, Target t) => doc.Materials.Last(m => m.Material == t.Material && Writable(m));

    // What a game file cannot hold; reported, never silently dropped.
    static List<string> Unsupported(SkinDocument doc)
    {
        var w = new List<string>();
        foreach (var a in doc.Attachments)
            w.Add($"attachment {Label(a.Name, a.Id)}: {(a.Screen != null ? "screens" : "attached models")} need the plugin; not written to game files");
        foreach (var m in doc.Materials)
            foreach (var l in m.Layers) w.Add($"{m.Material}: dynamic layer {Label(l.Name, l.Id)} needs the plugin; not written to game files");
        foreach (var s in doc.Sounds) w.Add($"sound {Label(s.Name, s.Id)} needs the plugin; not written to game files");
        if (doc.Parts.Count > 0) w.Add($"part layout ({doc.Parts.Count} moved, rotated, scaled or hidden parts) needs the plugin; not written to game files");
        return w;
    }

    static string Label(string name, string id) => string.IsNullOrWhiteSpace(name) ? id : name;

    // The Material objects each override writes: the target gear's renderers, or for a skin without a gear every catalog
    // item that uses the material (as the plugin applies it). A Material object is shared by every gear that uses it, so the
    // other catalog items that list the same material are reported.
    static List<Target> Resolve(GameAssets g, SkinDocument doc, List<string> warnings)
    {
        var items = Catalog.Get(g);
        var wanted = doc.Materials.Where(Writable).Select(m => m.Material).ToHashSet();
        var sources = doc.Target.Weapon is { } weapon ? [Catalog.Item(items, weapon)]
            : items.Where(i => i.Parts.Any(p => p.Materials.Any(wanted.Contains))).ToList();
        var found = new Dictionary<(string, long), Target>();
        foreach (var item in sources)
            foreach (var (node, _) in Catalog.Renderers(item.Build(g)))
                foreach (var m in node.Materials)
                {
                    if (m.PathId == 0 || !wanted.Contains(m.Name)) continue;
                    if (!Path.GetDirectoryName(Path.GetFullPath(m.File.path))!.Equals(Path.GetFullPath(g.DataDir), StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add($"{m.Name}: lives in the engine's built-in resources; not written");
                        continue;
                    }
                    found.TryAdd((Path.GetFileName(m.File.path), m.PathId), new Target(Path.GetFileName(m.File.path), m.PathId, m.Name));
                }
        foreach (var name in wanted)
        {
            if (!found.Values.Any(t => t.Material == name))
                warnings.Add($"{name}: no renderer of {doc.Target.Weapon ?? "any catalog item"} uses this material; skipped");
            else if (doc.Target.Weapon != null && items.Where(i => i.Id != doc.Target.Weapon && i.Parts.Any(p => p.Materials.Contains(name))).Select(i => i.Name).ToList() is { Count: > 0 } others)
                warnings.Add($"{name}: the game shares this material, so the change also shows on {string.Join(", ", others)}");
        }
        return found.Values.OrderBy(t => t.File, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.PathId).ToList();
    }

    // ---- applying ----

    // Rewrites `files` from their originals with every applied skin that touches them; a file no skin touches any more gets
    // its original back. Returns the files given back their original.
    static List<string> Rebuild(string dir, string stateDir, State state, List<string> files, Func<string, List<string>> warningsOf, long requestId)
    {
        var data = Path.Combine(dir, "GTFO_Data");
        var packs = new Dictionary<string, SkinPack>();
        var restored = new List<string>();
        try
        {
            foreach (var s in state.Skins) packs[s.Id] = SkinPack.Open(Path.Combine(stateDir, "packs", s.Pack));
            int done = 0;
            foreach (var file in files)
            {
                Program.Progress(requestId, done++, files.Count, file);
                var target = Path.Combine(data, file);
                if (!File.Exists(target)) throw new BackendException("not-found", $"{target} missing");
                if (state.Files.TryGetValue(file, out var rec))
                {
                    var now = Sha(target);
                    if (now != rec.PatchedSha && now != rec.OriginalSha)
                        throw new BackendException("game-changed", $"{file} changed since it was patched (game update or file check); run assets.status");
                }
                else
                {
                    var sha = NotStale(dir, state.BuildId, file, target);
                    var backup = Path.Combine(stateDir, "originals", file);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup + ".tmp", true);
                    if (Sha(backup + ".tmp") != sha) throw new BackendException("game-changed", $"{file} changed while it was backed up");
                    File.Move(backup + ".tmp", backup, true);
                    state.Files[file] = rec = new FileRecord(sha, new FileInfo(target).Length, File.GetLastWriteTimeUtc(target), null);
                    SaveState(stateDir, state);
                }

                var edits = state.Skins.SelectMany(s => s.Targets.Where(t => t.File.Equals(file, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new Edit(t.PathId, Override(packs[s.Id].Manifest, t), packs[s.Id], s.Id))).ToList();
                if (edits.Count == 0)
                {
                    PutOriginal(stateDir, data, file, rec);
                    state.Files.Remove(file);
                    restored.Add(file);
                }
                else
                {
                    var temp = target + ".forgeskin.tmp";
                    try
                    {
                        Patch(Path.Combine(stateDir, "originals", file), data, edits, temp, warningsOf);
                        var patched = Sha(temp);
                        Replace(temp, target);
                        File.SetLastWriteTimeUtc(target, rec.OriginalTime);
                        state.Files[file] = rec with { PatchedSha = patched };
                    }
                    finally
                    {
                        File.Delete(temp);
                    }
                }
                SaveState(stateDir, state);
            }
        }
        finally
        {
            foreach (var p in packs.Values) p.Dispose();
        }
        if (state.Files.Count == 0 && state.Skins.Count == 0) Directory.Delete(stateDir, true);
        return restored;
    }

    static void PutOriginal(string stateDir, string data, string file, FileRecord rec)
    {
        var backup = Path.Combine(stateDir, "originals", file);
        if (!File.Exists(backup) || Sha(backup) != rec.OriginalSha) throw new BackendException("bad-backup", $"The backup of {file} is missing or damaged");
        var target = Path.Combine(data, file);
        var temp = target + ".forgeskin.tmp";
        try
        {
            File.Copy(backup, temp, true);
            Replace(temp, target);
            File.SetLastWriteTimeUtc(target, rec.OriginalTime);
        }
        finally
        {
            File.Delete(temp);
        }
        File.Delete(backup);
    }

    // Moves a finished copy over a game file, with the shared reader closed.
    static void Replace(string temp, string target)
    {
        try
        {
            Methods.WithoutGame(() => File.Move(temp, target, true));
        }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) // sharing or lock violation
        {
            throw new BackendException("game-running", $"{Path.GetFileName(target)} is in use (close GTFO and tools that hold its files): {e.Message}");
        }
    }

    // `source` (an original) with the edits applied, written to `outPath`. Edits come in apply order; per Material a later
    // skin's texture wins per channel and parameters are applied in order.
    static void Patch(string source, string data, List<Edit> edits, string outPath, Func<string, List<string>> warningsOf)
    {
        var am = new AssetsManager { UseTemplateFieldCache = true };
        try
        {
            var inst = am.LoadAssetsFile(source, false);
            using (var tpk = typeof(AssetPatch).Assembly.GetManifestResourceStream("classdata.tpk")!) am.LoadClassPackage(tpk);
            am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
            long next = inst.file.AssetInfos.Max(i => i.PathId) + 1;
            foreach (var group in edits.GroupBy(e => e.PathId))
            {
                var info = inst.file.GetAssetInfo(group.Key) ?? throw new BackendException("not-found", $"{Path.GetFileName(source)}: object {group.Key} missing");
                var mat = am.GetBaseField(inst, info);
                var name = mat["m_Name"].AsString;
                var envs = mat["m_SavedProperties.m_TexEnvs.Array"];
                var textures = new Dictionary<TextureChannel, Edit>();
                foreach (var e in group)
                    foreach (var c in e.Override.Textures.Keys) textures[c] = e;
                foreach (var (channel, e) in textures)
                {
                    var prop = Preview.ChannelProps.First(kv => kv.Value == channel).Key;
                    var env = Env(envs, prop);
                    var own = env != null && env["second.m_Texture.m_PathID"].AsLong != 0;
                    var template = (own ? env : Env(envs, "_MainTex") is { } main && main["second.m_Texture.m_PathID"].AsLong != 0 ? main
                        : envs.Children.FirstOrDefault(c => c["second.m_Texture.m_PathID"].AsLong != 0)) is { } from ? Texture(am, inst, data, from["second.m_Texture"]) : null;
                    if (template == null)
                    {
                        warningsOf(e.Skin).Add($"{name}: has no texture to take a format from; {channel} not written");
                        continue;
                    }
                    var tex = Encode(template, e.Pack.ReadBytes(e.Override.Textures[channel]), channel, $"{name} {channel}", warningsOf(e.Skin));
                    tex["m_Name"].AsString = $"{name}_{channel}_{e.Skin}";
                    if (!own) tex["m_ColorSpace"].AsInt = Linear.Contains(channel) ? 0 : 1;
                    var texInfo = AssetFileInfo.Create(inst.file, next, (int)AssetClassID.Texture2D, am.ClassDatabase)
                        ?? throw new BackendException("internal", "Texture2D type missing from the class database");
                    texInfo.SetNewData(tex);
                    inst.file.Metadata.AddAssetInfo(texInfo);
                    if (env == null)
                    {
                        env = envs.Children[0].Clone();
                        env["first"].AsString = prop;
                        env["second.m_Scale.x"].AsFloat = env["second.m_Scale.y"].AsFloat = 1;
                        env["second.m_Offset.x"].AsFloat = env["second.m_Offset.y"].AsFloat = 0;
                        envs.Children.Add(env);
                        envs.AsArray = new AssetTypeArrayInfo(envs.Children.Count);
                    }
                    env["second.m_Texture.m_FileID"].AsInt = 0;
                    env["second.m_Texture.m_PathID"].AsLong = next++;
                }
                foreach (var e in group) Params(mat, e.Override, name, warningsOf(e.Skin));
                info.SetNewData(mat);
            }
            using var w = new AssetsFileWriter(outPath);
            inst.file.Write(w);
        }
        finally
        {
            am.UnloadAll(true);
        }
    }

    static AssetTypeValueField? Env(AssetTypeValueField envs, string prop) => envs.Children.FirstOrDefault(c => c["first"].AsString == prop);

    static AssetTypeValueField Texture(AssetsManager am, AssetsFileInstance inst, string data, AssetTypeValueField pptr)
    {
        int fileId = pptr["m_FileID"].AsInt;
        var file = fileId == 0 ? inst : am.LoadAssetsFile(Path.Combine(data, Path.GetFileName(inst.file.Metadata.Externals[fileId - 1].PathName)), false);
        return am.GetBaseField(file, pptr["m_PathID"].AsLong);
    }

    // The plugin's parameter rules (SkinnedGear.Params): a property the Material does not hold is reported, not added.
    static void Params(AssetTypeValueField mat, MaterialOverride o, string name, List<string> warnings)
    {
        var props = mat["m_SavedProperties"];
        var p = o.Params;
        AssetTypeValueField? Entry(string list, string key) => props[list]["Array"].Children.FirstOrDefault(c => c["first"].AsString == key)?["second"];
        void Float(string key, float value, string what)
        {
            if (Entry("m_Floats", key) is { } f) f.AsFloat = value;
            else warnings.Add($"{name}: the material has no {what}; not written");
        }
        void Color(AssetTypeValueField c, float[] v)
        {
            c["r"].AsFloat = v[0]; c["g"].AsFloat = v[1]; c["b"].AsFloat = v[2]; c["a"].AsFloat = v[3];
        }
        void Emissive(bool on)
        {
            if (Entry("m_Floats", "_EnableEmissive") is { } f) f.AsFloat = on ? 1 : 0;
            var keywords = mat["m_ShaderKeywords"].AsString.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(k => k != "ENABLE_EMISSIVE").ToList();
            if (on) keywords.Add("ENABLE_EMISSIVE");
            mat["m_ShaderKeywords"].AsString = string.Join(' ', keywords);
        }

        if (o.Textures.ContainsKey(TextureChannel.Emissive) && p.EmissiveEnabled != false) Emissive(true);
        if (p.EmissiveEnabled is bool on) Emissive(on);
        if (p.EmissiveColor != null || p.EmissiveIntensity != null)
        {
            var current = Entry("m_Colors", "_EmissiveColor");
            float k = p.EmissiveIntensity ?? 1;
            float[]? c = p.EmissiveColor is { } e ? [e[0], e[1], e[2], 1] : current is { } cur ? [cur["r"].AsFloat, cur["g"].AsFloat, cur["b"].AsFloat, cur["a"].AsFloat] : null;
            if (c != null)
            {
                c = c.Select(x => x * k).ToArray();
                if (current != null) Color(current, c);
                else warnings.Add($"{name}: the material has no emissive colour; not written");
                if (Entry("m_Colors", "_EmissionColor") is { } emission) Color(emission, c);
            }
            else warnings.Add($"{name}: the material has no emissive colour; not written");
        }
        if (p.Metallic is float metal) Float("_Metallic", metal, "metallic");
        if (p.Smoothness is float smooth) Float(Entry("m_Floats", "_Glossiness") != null ? "_Glossiness" : "_Smoothness", smooth, "smoothness");
        if (p.Tint is { } t)
        {
            if (Entry("m_Colors", "_Color") is { } c) Color(c, t);
            else warnings.Add($"{name}: the material has no tint; not written");
        }
    }

    // ---- textures ----

    // A copy of `template` (the texture the slot had, else the material's albedo) holding `png` in the template's format
    // (crunched formats as their plain block format; formats without an encoder as RGBA32), with a full mip chain when the
    // template had mips. The image keeps its pack size; block formats need a multiple of 4, other sizes are rounded to one.
    // Normal maps go in the game's DXT5nm layout (x in A, y in G) when the format has alpha, else as RG.
    static AssetTypeValueField Encode(AssetTypeValueField template, byte[] png, TextureChannel channel, string what, List<string> warnings)
    {
        var tex = template;
        int format = tex["m_TextureFormat"].AsInt;
        (CompressionFormat cf, int unity) = format switch
        {
            10 or 28 => (CompressionFormat.Bc1, 10),
            12 or 29 => (CompressionFormat.Bc3, 12),
            25 => (CompressionFormat.Bc7, 25),
            26 => (CompressionFormat.Bc4, 26),
            27 => (CompressionFormat.Bc5, 27),
            1 or 3 or 4 or 5 or 14 or 63 => (CompressionFormat.Rgba, format),
            _ => (CompressionFormat.Rgba, 4),
        };
        if (unity == 4 && format != 4) warnings.Add($"{what}: the game's texture format {format} has no encoder; written as RGBA32");
        bool block = cf != CompressionFormat.Rgba;

        using var codec = SKCodec.Create(new MemoryStream(png)) ?? throw new SkinFormatException("bad-image", $"{what}: not a readable image");
        using var decoded = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        int w = decoded.Width, h = decoded.Height;
        if (block && (w % 4 != 0 || h % 4 != 0))
        {
            (w, h) = (Math.Max(4, (w + 2) / 4 * 4), Math.Max(4, (h + 2) / 4 * 4));
            warnings.Add($"{what}: {decoded.Width}x{decoded.Height} resized to {w}x{h} (block formats need a multiple of 4)");
        }
        using var bmp = Images.Resize(decoded, w, h);
        var rgba = bmp.Bytes;
        if (channel != TextureChannel.Normal && unity is 10 or 3 or 26 or 27 or 63 && Enumerable.Range(0, rgba.Length / 4).Any(i => rgba[i * 4 + 3] < 250))
            warnings.Add($"{what}: the game stores this texture without alpha (format {unity}); the image's alpha is dropped");
        if (channel == TextureChannel.Normal && unity is 12 or 25 or 4 or 5 or 14)
            for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2], rgba[i + 3]) = (255, 255, rgba[i]);
        // Unity stores rows bottom-up.
        var flipped = new byte[rgba.Length];
        for (int y = 0, row = w * 4; y < h; y++) Buffer.BlockCopy(rgba, (h - 1 - y) * row, flipped, y * row, row);

        bool mips = tex["m_MipCount"].AsInt > 1;
        var encoder = new BcEncoder(cf);
        encoder.OutputOptions.GenerateMipMaps = mips;
        encoder.OutputOptions.MaxMipMapLevel = -1;
        encoder.OutputOptions.Quality = cf == CompressionFormat.Bc7 ? CompressionQuality.Fast : CompressionQuality.Balanced;
        var levels = encoder.EncodeToRawBytes(flipped, w, h, PixelFormat.Rgba32);
        if (!block) levels = levels.Select(l => Raw(l, unity)).ToArray();
        var image = levels.SelectMany(l => l).ToArray();

        tex["m_Width"].AsInt = w;
        tex["m_Height"].AsInt = h;
        tex["m_TextureFormat"].AsInt = unity;
        tex["m_MipCount"].AsInt = levels.Length;
        tex["m_CompleteImageSize"].AsInt = image.Length;
        tex["m_ImageCount"].AsInt = 1;
        if (!tex["m_StreamingMipmaps"].IsDummy) tex["m_StreamingMipmaps"].AsBool = false;
        tex["image data"].AsByteArray = image;
        var stream = tex["m_StreamData"];
        stream["offset"].AsULong = 0;
        stream["size"].AsUInt = 0;
        stream["path"].AsString = "";
        return tex;
    }

    // RGBA32 bytes -> an uncompressed Unity format.
    static byte[] Raw(byte[] rgba, int format)
    {
        int n = rgba.Length / 4;
        switch (format)
        {
            case 4: return rgba;
            case 1: return Enumerable.Range(0, n).Select(i => rgba[i * 4 + 3]).ToArray();          // Alpha8
            case 63: return Enumerable.Range(0, n).Select(i => rgba[i * 4]).ToArray();             // R8
            case 3: return Enumerable.Range(0, n * 3).Select(i => rgba[i / 3 * 4 + i % 3]).ToArray(); // RGB24
        }
        var o = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (format == 5) (o[i], o[i + 1], o[i + 2], o[i + 3]) = (rgba[i + 3], rgba[i], rgba[i + 1], rgba[i + 2]); // ARGB32
            else (o[i], o[i + 1], o[i + 2], o[i + 3]) = (rgba[i + 2], rgba[i + 1], rgba[i], rgba[i + 3]);           // BGRA32
        }
        return o;
    }

    // ---- state ----

    static string GameDir(string? gameDir)
    {
        var dir = gameDir == null ? Game.Dir : Path.GetFullPath(gameDir);
        if (!File.Exists(Path.Combine(dir, "GTFO_Data", "globalgamemanagers")))
            throw new BackendException("game-not-found", $"{dir} is not a GTFO install (GTFO_Data expected)");
        return dir;
    }

    static void NotRunning()
    {
        if (Game.Running) throw new BackendException("game-running", "Close GTFO first: it reads these files while it runs");
    }

    static T Read<T>(string? gameDir, Func<GameAssets, T> read)
    {
        if (gameDir == null) return Methods.WithGame(read);
        using var g = new GameAssets(Path.GetFullPath(gameDir));
        return read(g);
    }

    static string Root => Path.Combine(Paths.Data, "game-assets");

    static string StateDir(string buildId, string dir) =>
        Path.Combine(Root, $"{buildId}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..8]}");

    static State? LoadState(string stateDir)
    {
        var file = Path.Combine(stateDir, "state.json");
        if (!File.Exists(file)) return null;
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(file), Program.Json)!;
        state.Files = new Dictionary<string, FileRecord>(state.Files, StringComparer.OrdinalIgnoreCase);
        return state;
    }

    static void SaveState(string stateDir, State state)
    {
        Directory.CreateDirectory(stateDir);
        var file = Path.Combine(stateDir, "state.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(state, Program.Json));
        File.Move(file + ".tmp", file, true);
    }

    // Records of this game dir made on another game build.
    static List<(string Path, State State)> Stale(string dir, string buildId)
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.GetDirectories(Root).Select(d => (d, LoadState(d))).Where(s => s.Item2 is { } st
                && st.GameDir.Equals(dir, StringComparison.OrdinalIgnoreCase) && st.BuildId != buildId)
            .Select(s => (s.d, s.Item2!)).ToList();
    }

    static object StaleInfo(State s) => new { s.BuildId, files = s.Files.Keys.ToList(), skins = s.Skins.Select(k => k.Id).ToList() };

    // SHA-256 of a game file about to be taken as an original; refuses one that still holds a patch written on an older build.
    static string NotStale(string dir, string buildId, string file, string path)
    {
        var sha = Sha(path);
        foreach (var (_, s) in Stale(dir, buildId))
            if (s.Files.TryGetValue(file, out var rec) && rec.PatchedSha == sha)
                throw new BackendException("stale-patch", $"{file} still holds skins written on game build {s.BuildId}; verify the game files in Steam, then try again");
        return sha;
    }

    static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    static string Readme(SkinDocument doc, string dir, string buildId, List<(string File, string OriginalSha)> files, List<string> warnings)
    {
        var revision = File.Exists(Path.Combine(dir, "revision.txt")) ? File.ReadAllText(Path.Combine(dir, "revision.txt")).Trim() : "unknown";
        var list = string.Join("\r\n", files.Select(f => $"  GTFO_Data/{f.File}  (original SHA-256 {f.OriginalSha})"));
        var limits = warnings.Count == 0 ? "  -" : string.Join("\r\n", warnings.Select(w => "  - " + w));
        return $"""
            {doc.Name} {doc.Version}{(string.IsNullOrWhiteSpace(doc.Author) ? "" : " by " + doc.Author)}
            GTFO game assets / GTFO 游戏材质包
            Game build / 游戏版本: revision {revision} (build id {buildId})

            [中文]
            这是直接替换游戏原版资源的材质包，不需要 BepInEx。只适用于上面这个游戏版本；游戏更新后不要再用，请向作者要新版本。
            安装：
              1. 退出 GTFO。
              2. 在 Steam 里右键 GTFO > 管理 > 浏览本地文件，打开游戏文件夹。
              3. 先备份下面列出的原文件（复制到别处）。
              4. 把压缩包里的 GTFO_Data 文件夹复制到游戏文件夹，覆盖同名文件。
            恢复原版：把备份的文件放回去，或在 Steam 里右键 GTFO > 属性 > 已安装文件 > 验证游戏文件的完整性。
            注意：游戏更新或验证文件后，这些文件会被换回原版，需要重新安装对应版本的材质包。联机时其他玩家看到的仍是原版。
            替换的文件：
            {list}
            没有包含的内容（游戏文件放不下，需要 ForgeSkin 插件；或游戏材质本身没有这项参数）：
            {limits}

            [English]
            This pack replaces GTFO's own asset files; it needs no BepInEx. It only fits the game build above; after a game update
            do not use it, ask the author for a new version.
            Install:
              1. Quit GTFO.
              2. In Steam, right-click GTFO > Manage > Browse local files to open the game folder.
              3. Back up the original files listed below (copy them elsewhere).
              4. Copy the GTFO_Data folder from this zip into the game folder, replacing the files.
            Restore vanilla: put the backed-up files back, or in Steam right-click GTFO > Properties > Installed Files > Verify
            integrity of game files.
            Note: a game update or a file check puts the original files back; install the pack made for that build again.
            Other players in your lobby still see vanilla gear.
            Files replaced:
            {list}
            Not included (game files cannot hold it and it needs the ForgeSkin plugin, or the game's material has no such parameter):
            {limits}
            """;
    }
}
