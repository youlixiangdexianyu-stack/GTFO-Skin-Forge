using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeSkin.Backend;
using ForgeSkin.Format;
using Xunit;

namespace ForgeSkin.Tests;

// Local-resource layer: needs the installed game, reported as skipped ("not run") without it. The game is read only;
// the data dir is the test output's GTFO_UserData (its cache makes later runs fast) and the profile is a temp folder.
public sealed class GameFactAttribute : FactAttribute
{
    public GameFactAttribute()
    {
        if (!Game.Locate().Found) Skip = "needs local GTFO install, not run";
    }
}

public class GameTests
{
    static JsonNode Call(string method, object? args = null) =>
        JsonSerializer.SerializeToNode(Methods.Call(method, JsonSerializer.SerializeToNode(args ?? new { }, Program.Json)!.AsObject(), 1), Program.Json)!;

    // locate -> catalog -> preview HEL -> pack with an albedo override, an ammo screen and a GLB attachment -> install.
    [GameFact]
    public void MainFlow()
    {
        Assert.True((bool)Call("game.locate")["found"]!);
        var items = Call("catalog.get")["items"]!.AsArray();
        var hel = items.Single(i => (string)i!["id"]! == "auto_pistol")!;
        Assert.Equal(45u, (uint)hel["gearId"]!);
        Assert.Equal(["front", "receiver", "stock", "mag", "flashlight", "ammo_bullet", "ammo_shell"], hel["parts"]!.AsArray().Select(p => (string)p!["id"]!));
        // Melee weapons are catalog gear like guns, with their own part ids and offline gear id.
        var hammer = items.Single(i => (string)i!["id"]! == "sledgehammer")!;
        Assert.Equal(("melee", 9u), ((string)hammer["slot"]!, (uint)hammer["gearId"]!));
        Assert.Equal(["melee_head", "melee_neck", "melee_handle", "melee_pommel"], hammer["parts"]!.AsArray().Select(p => (string)p!["id"]!));
        // Items with their own prefabs (packs, consumables, carry items) are one `body` part keyed by their item id.
        var medipack = items.Single(i => (string)i!["id"]! == "medipack")!;
        Assert.Equal(("pack", 102u, "body"), ((string)medipack["slot"]!, (uint)medipack["itemId"]!, (string)medipack["parts"]![0]!["id"]!));

        var preview = Call("preview.weapon", new { id = "auto_pistol" });
        Assert.True(File.Exists((string)preview["glb"]!));
        var albedo = (string)preview["textures"]!["Front_UZI_2"]!["albedo"]!;

        var dir = Directory.CreateTempSubdirectory("sf-flow-").FullName;
        File.Copy(albedo, Path.Combine(dir, "front.png"));
        var ammo = items.First(i => (string)i!["slot"]! == "ammo")!;
        File.Copy((string)Call("preview.weapon", new { id = (string)ammo["id"]! })["glb"]!, Path.Combine(dir, "bullet.glb"));
        Images.SavePng(GlyphMeshTests.Strip(), Path.Combine(Directory.CreateDirectory(Path.Combine(dir, ".frames")).FullName, "counter-glyphs.png"));
        var project = new SkinDocument
        {
            Id = "skin-flow-test",
            Name = "Flow test",
            Target = new SkinTarget { Weapon = "auto_pistol" },
            Materials = { new MaterialOverride { Material = "Front_UZI_2", Textures = { [TextureChannel.Albedo] = "front.png" } } },
            Attachments =
            {
                new Attachment { Id = "bullet", Name = "Bullet", Part = "receiver", Mesh = "bullet.glb" },
                new Attachment { Id = "counter", Name = "Ammo", Part = "receiver", Screen = new Screen { Digits = 3 } },
            },
        };
        var projectPath = Path.Combine(dir, "flow.forgeskinproj");
        Call("project.save", new { project, path = projectPath });
        var loaded = Call("project.load", new { path = projectPath });
        var pack = (string)Call("pack.build", new { project = loaded, projectPath, @out = Path.Combine(dir, "out"), sizeMode = "1k" })["pack"]!;

        using (var p = SkinPack.Open(pack))
        {
            Assert.Equal(45u, p.Manifest.Target.GearId);
            // The albedo compressed for the plugin to upload as it is, at the pack size, and no PNG of it beside.
            Assert.Equal("textures/Front_UZI_2_Albedo.dds", p.Manifest.Materials[0].Textures[TextureChannel.Albedo]);
            var packed = Dds.Read(p.ReadBytes("textures/Front_UZI_2_Albedo.dds"), TextureChannel.Albedo, "albedo");
            Assert.Equal(1024, Math.Max(packed.Width, packed.Height));
            Assert.Throws<SkinFormatException>(() => p.ReadBytes("textures/Front_UZI_2_Albedo.png"));
            Assert.True(ForgeSkinMesh.Read(p.ReadBytes(p.Manifest.Attachments[0].Mesh!)).Indices.Length > 0);
            Assert.Equal(Screen.Glyphs.Length - 1, p.Manifest.Attachments[1].Screen!.GlyphMeshes!.Count(g => g != null)); // all but the blank
        }

        var profile = Directory.CreateDirectory(Path.Combine(dir, "profile", "BepInEx")).Parent!.FullName;
        Call("pack.install", new { pack, profile, activate = true, pick = true }); // as 应用到游戏 does
        var installed = Call("pack.list", new { profile }).AsArray().Single()!;
        Assert.Equal("skin-flow-test", (string)installed["id"]!);
        Assert.True((bool)installed["active"]!);
        var skins = Path.Combine(Game.PluginDir(profile), "skins");
        Assert.Equal("skin-flow-test", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(skins, "active.json")))!["targets"]!["gear:45"]!);
        // The one-shot pick the plugin turns into the player's choice for that gear (contract with Native/Skins.ReadPicks).
        var pick = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(skins, "*" + SkinPack.PickExtension).Single()))!;
        Assert.Equal(("gear:45", "skin-flow-test"), ((string)pick["target"]!, (string)pick["skin"]!));
    }

    static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    static List<(string Kind, string? What)> Notes(JsonNode notes) => notes.AsArray().Select(n => ((string)n!["kind"]!, (string?)n["what"])).ToList();

    // Public contract with the Skins library (food-Skins): a hand-written group in its real format (several gears through
    // filterGroups and the legacy flat `filters`, a rundown condition, BCP 47 names, texture macros, variant conditions in
    // both keys, a mesh target and a Vector) imports as one skin per gear with a report; a group naming no gear asks for
    // one; built packs export as one gun-pack group and read back, an item skin is skipped and reported.
    [GameFact]
    public void SkinsLibraryFormat()
    {
        var items = Call("catalog.get")["items"]!.AsArray();
        var catalog = items.Deserialize<List<CatalogItem>>(Program.Json)!;
        var hel = catalog.Single(i => i.Id == "auto_pistol");
        var hammer = catalog.Single(i => i.Id == "sledgehammer");
        string mainTex = Methods.WithGame(g => SkinsFormat.Materials(g, Catalog.Item(Catalog.Get(g), "auto_pistol")))["Front_UZI_2"].Textures["_MainTex"];
        string hammerMaterial = hammer.Parts[0].Materials[0];

        var dir = Directory.CreateTempSubdirectory("sf-skins-").FullName;
        var group = Path.Combine(dir, "group");
        WriteJson(Path.Combine(group, "manifest.skingroup.json"), new Dictionary<string, object>
        {
            ["name"] = new Dictionary<string, string> { ["fr"] = "Exemple", ["en"] = "Sample", ["zh-Hans"] = "样例" },
            ["author"] = "Tester",
            ["desc"] = "A sample",
            ["guid"] = "test.sample",
            ["filterGroups"] = new object[]
            {
                new { filters = new object[] { new { type = "OfflineGearPersistentID", value = hammer.GearId.ToString() } } },
                new { filters = new object[] { new { type = "RundownName", value = "__vanilla__" } } },
            },
            ["filters"] = new object[] { new { type = 0, value = "45" } },
        });
        WriteJson(Path.Combine(group, "hel", "front.mattarget.json"), new Dictionary<string, object>
        {
            ["material_name"] = "Front_UZI_2",
            ["match_groups"] = new object[] { new { conditions = new object[] { new { slot = "_MainTex", value = mainTex } } } },
            ["properties"] = new Dictionary<string, object>
            {
                ["_MainTex"] = new { type = "Texture2D", data = new { src = "@RGBA(1,0,0,1)[2,2]" } },
                ["_MetallicGlossMap"] = new { type = "Texture2D", data = new { src = "@METALLIC_SMOOTHNESS(0.8,0.6)" } },
                ["_Glossiness"] = new { type = "Float", data = new { value = 0.4 } },
                ["_Offset"] = new { type = "Vector", data = new { x = 1, y = 0, z = 0, w = 0 } },
            },
        });
        // Same material, a variant no HEL has: not imported.
        WriteJson(Path.Combine(group, "hel", "front-other.mattarget.json"), new Dictionary<string, object>
        {
            ["material_name"] = "Front_UZI_2",
            ["matchGroups"] = new object[] { new { conditions = new object[] { new { slot = "_MainTex", value = "not-a-texture" } } } },
            ["properties"] = new Dictionary<string, object> { ["_Color"] = new { type = "Color", data = new { r = 0, g = 1, b = 0 } } },
        });
        WriteJson(Path.Combine(group, "hammer.mattarget.json"), new Dictionary<string, object>
        {
            ["material_name"] = hammerMaterial,
            ["properties"] = new Dictionary<string, object> { ["_Color"] = new { type = "Color", data = new { r = 0.5, g = 0.25, b = 1 } } },
        });
        WriteJson(Path.Combine(group, "barrel.meshtarget.json"), new { mesh_name = "x" });

        var root = Path.Combine(dir, "autosave");
        var result = Call("skins.import", new { manifest = Path.Combine(group, "manifest.skingroup.json"), root, lang = "zh" });
        Assert.False((bool)result["needsTarget"]!);
        var skins = result["skins"]!.AsArray();
        Assert.Equal(["auto_pistol", "sledgehammer"], skins.Select(s => (string)s!["weapon"]!).Order());
        var notes = Notes(result["notes"]!);
        foreach (var kind in new[] { "desc", "rundown", "meshTarget", "vector", "noGear" }) Assert.Contains(notes, n => n.Kind == kind);

        SkinDocument Load(string weapon) => Call("project.load", new { path = (string)skins.Single(s => (string)s!["weapon"]! == weapon)!["path"]! }).Deserialize<SkinDocument>(Program.Json)!;
        var helSkin = Load("auto_pistol");
        Assert.Equal(("样例", "Tester", 45u), (helSkin.Name, helSkin.Author, helSkin.Target.GearId!.Value));
        var front = Assert.Single(helSkin.Materials);
        Assert.Equal(("Front_UZI_2", "front", 0.4f, (float[]?)null), (front.Material, front.Part, front.Params.Smoothness, front.Params.Tint));
        using (var red = Images.Load(front.Textures[TextureChannel.Albedo]))
            Assert.Equal((2, 2, new SkiaSharp.SKColor(255, 0, 0, 255)), (red.Width, red.Height, red.GetPixel(1, 1)));
        using (var ms = Images.Load(front.Textures[TextureChannel.MetallicSmoothness]))
            Assert.Equal(new SkiaSharp.SKColor(204, 153, 0, 255), ms.GetPixel(0, 0));
        var hammerSkin = Load("sledgehammer");
        Assert.Equal([0.5f, 0.25f, 1f, 1f], Assert.Single(hammerSkin.Materials).Params.Tint!);

        // A group with only a rundown condition goes on no gear in Skins: nothing is written until a gear is picked.
        var loose = Path.Combine(dir, "loose");
        WriteJson(Path.Combine(loose, "manifest.skingroup.json"), new { name = "Loose", author = "Tester", guid = "test.loose", filterGroups = new object[] { new { filters = new object[] { new { type = 1, value = "x" } } } } });
        File.Copy(Path.Combine(group, "hammer.mattarget.json"), Path.Combine(loose, "hammer.mattarget.json"));
        var asked = Call("skins.import", new { manifest = Path.Combine(loose, "manifest.skingroup.json"), root, lang = "en" });
        Assert.True((bool)asked["needsTarget"]!);
        Assert.Empty(asked["skins"]!.AsArray());
        var placed = Call("skins.import", new { manifest = Path.Combine(loose, "manifest.skingroup.json"), root, lang = "en", weapon = "sledgehammer" });
        Assert.Equal("sledgehammer", (string)Assert.Single(placed["skins"]!.AsArray())!["weapon"]!);

        // Gun pack: both skins as one group, plus an item skin that Skins cannot take. The HEL's stock is GTFO/BlendHS (no
        // albedo slot): a picture on it is written with the gear shader swap and the slots the plugin fills.
        helSkin.Materials.Add(new MaterialOverride { Material = "Stock_Pistol_1", Textures = { [TextureChannel.Albedo] = front.Textures[TextureChannel.Albedo] } });
        // The HEL and the machine pistol share Receiver_UZI_1 with the same textures: the HEL's target names them, and the
        // machine pistol's, which Skins could not tell apart, is reported instead of written.
        helSkin.Materials.Add(new MaterialOverride { Material = "Receiver_UZI_1", Params = { Tint = [0, 1, 0, 1] } });
        var machine = new SkinDocument
        {
            Id = "skin-machine-test",
            Name = "Machine",
            Target = new SkinTarget { Weapon = "machine_pistol" },
            Materials = { new MaterialOverride { Material = "Receiver_UZI_1", Params = { Tint = [0, 0, 1, 1] } } }
        };
        string Build(SkinDocument doc, string projectPath) => (string)Call("pack.build", new { project = doc, projectPath, @out = Path.Combine(dir, "packs"), sizeMode = "keep" })["pack"]!;
        var medipack = catalog.Single(i => i.Id == "medipack");
        var itemSkin = new SkinDocument
        {
            Id = "skin-item-test",
            Name = "Item",
            Target = new SkinTarget { Weapon = "medipack" },
            Materials = { new MaterialOverride { Material = medipack.Parts[0].Materials[0], Params = { Tint = [1, 0, 0, 1] } } }
        };
        var packs = new[]
        {
            Build(helSkin, (string)skins.Single(s => (string)s!["weapon"]! == "auto_pistol")!["path"]!),
            Build(hammerSkin, (string)skins.Single(s => (string)s!["weapon"]! == "sledgehammer")!["path"]!),
            Build(itemSkin, Path.Combine(dir, "item.forgeskinproj")),
            Build(machine, Path.Combine(dir, "machine.forgeskinproj")),
        };
        var mod = Path.Combine(dir, "pack.zip");
        var exported = Call("pack.exportMod", new { packs, @out = mod, name = "Sample_Pack", version = "1.0.0", description = "", icon = front.Textures[TextureChannel.Albedo], format = "skins", gunPack = true });
        var dropped = Notes(exported["dropped"]!);
        Assert.Contains(dropped, n => n.Kind == "itemSkin");
        Assert.Contains(dropped, n => n.Kind == "conflict" && n.What!.StartsWith("Receiver_UZI_1"));
        System.IO.Compression.ZipFile.ExtractToDirectory(mod, Path.Combine(dir, "mod"));
        var manifest = Assert.Single(Directory.GetFiles(Path.Combine(dir, "mod", "Assets", "Skins"), "manifest.skingroup.json", SearchOption.AllDirectories));
        var written = JsonNode.Parse(File.ReadAllText(manifest))!;
        var machineGear = catalog.Single(i => i.Id == "machine_pistol").GearId.ToString()!;
        Assert.Equal(["45", hammer.GearId.ToString()!, machineGear], written["filterGroups"]!.AsArray().Select(g => (string)g!["filters"]![0]!["value"]!));
        var targets = Directory.GetFiles(Path.GetDirectoryName(manifest)!, "*.mattarget.json").Select(f => JsonNode.Parse(File.ReadAllText(f))!).ToList();
        Assert.Equal(4, targets.Count);
        var stock = targets.Single(x => (string)x["material_name"]! == "Stock_Pistol_1");
        Assert.Equal(("Cell/Player/CustomGearShader", "@NORMAL"), ((string)stock["shader"]!, (string)stock["properties"]!["_BumpMap"]!["data"]!["src"]!));
        var receiver = targets.Single(x => (string)x["material_name"]! == "Receiver_UZI_1");
        Assert.Contains(receiver["match_groups"]![0]!["conditions"]!.AsArray(), c => (string)c!["slot"]! == "_MainTex" && (string)c["value"]! == "Receiver_UZI_1_Albedo");
        var back = Call("skins.import", new { manifest, root = Path.Combine(dir, "back"), lang = "en" });
        Assert.Equal(["auto_pistol", "machine_pistol", "sledgehammer"], back["skins"]!.AsArray().Select(s => (string)s!["weapon"]!).Order());
    }

    // Resource integrity of previews: every opaque or cutout material of every catalog item has a readable albedo PNG, and
    // every material says how the game draws it.
    [GameFact]
    public void EveryPreviewMaterialHasAlbedo()
    {
        var missing = new List<string>();
        foreach (var item in Call("catalog.get")["items"]!.AsArray())
        {
            var id = (string)item!["id"]!;
            var preview = Call("preview.weapon", new { id });
            var textures = preview["textures"]!.AsObject();
            foreach (var material in item["parts"]!.AsArray().SelectMany(p => p!["materials"]!.AsArray()).Select(m => (string)m!).Distinct())
            {
                var kind = (string?)preview["params"]![material]?["kind"];
                if (kind == null) { missing.Add($"{id}/{material}: no params"); continue; }
                if (kind is not ("opaque" or "cutout")) continue;
                var png = (string?)textures[material]?["albedo"];
                if (png == null || !File.Exists(png)) missing.Add($"{id}/{material}");
                else Images.Size(png);
            }
        }
        Assert.Empty(missing);
    }
}
