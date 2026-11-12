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
            Assert.Equal("textures/Front_UZI_2_Albedo.png", p.Manifest.Materials[0].Textures[TextureChannel.Albedo]);
            Assert.True(ForgeSkinMesh.Read(p.ReadBytes(p.Manifest.Attachments[0].Mesh!)).Indices.Length > 0);
            Assert.Equal(11, p.Manifest.Attachments[1].Screen!.GlyphMeshes!.Count(g => g != null));
        }

        var profile = Directory.CreateDirectory(Path.Combine(dir, "profile", "BepInEx")).Parent!.FullName;
        Call("pack.install", new { pack, profile, activate = true });
        var installed = Call("pack.list", new { profile }).AsArray().Single()!;
        Assert.Equal("skin-flow-test", (string)installed["id"]!);
        Assert.True((bool)installed["active"]!);
        Assert.Equal("skin-flow-test", (string)JsonNode.Parse(File.ReadAllText(Path.Combine(Game.PluginDir(profile), "skins", "active.json")))!["targets"]!["gear:45"]!);
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
