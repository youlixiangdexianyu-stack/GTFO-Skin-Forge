using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SkinForge.Backend;

public sealed record CatalogPart(string Id, string Role, string Label, List<string> Materials, bool DefaultEnabled);

public sealed record CatalogItem(string Id, string Name, string? NameZh, string Category, string Slot, uint? GearId,
    string? Calibre, List<CatalogPart> Parts)
{
    public string? CategoryZh { get; init; }
    // ItemDataBlock persistentID of an item built from its own first-person prefabs (packs, consumables, carry items,
    // the hacking tool); null for gear built from parts, which has GearId.
    public uint? ItemId { get; init; }
    [JsonIgnore] public Recipe Source { get; init; } = null!;
    [JsonIgnore] public int Order { get; init; }

    // The item's node tree under a "Gear" holder; part roots carry Node.Part.
    public Node Build(GameAssets g) => Catalog.Build(g, Source);
}

// How to spawn an item, stored with the cached catalog: a weapon's gear components (+ ejected shell prefab), prefabs
// spawned together as part `Slot` (an attachment, or an item's first-person prefabs), or the ammo parts of a weapon.
public sealed record Recipe(Dictionary<int, uint>? Comps = null, string? Shell = null, string[]? Prefabs = null, string? Slot = null, Recipe? AmmoOf = null);

// Guns, melee weapons, tools and attachments from the game's gear DataBlocks; names and official order from naming.json.
// Every renderer belongs to exactly one part: the slot it spawned in, or an ammo part (bullets, shells and speed loaders
// inside slots, plus the weapon's ejected shell casing).
public static class Catalog
{
    static readonly JsonObject Naming = JsonNode.Parse(typeof(Catalog).Assembly.GetManifestResourceStream("naming.json")!)!.AsObject();
    static List<CatalogItem>? _items;
    static string? _build;

    sealed record Cached(CatalogItem Item, Recipe Source);

    // Built once per game build and kept in that build's cache folder; later launches read only that file.
    public static List<CatalogItem> Get(GameAssets g)
    {
        if (_items != null && _build == g.BuildId) return _items;
        var file = Path.Combine(g.Cache, "catalog.json");
        List<CatalogItem> items;
        try
        {
            items = JsonSerializer.Deserialize<List<Cached>>(File.ReadAllText(file), Program.Json)!.Select(c => c.Item with { Source = c.Source }).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            items = Load(g);
            Directory.CreateDirectory(g.Cache);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(items.Select(i => new Cached(i, i.Source)), Program.Json));
            File.Move(file + ".tmp", file, true);
        }
        (_items, _build) = (items, g.BuildId);
        PreviewCache.Ready(g, items);
        return items;
    }

    public static Node Build(GameAssets g, Recipe r)
    {
        var holder = Holder();
        if (r.AmmoOf != null)
        {
            // Speed loader, shell and bullet of the weapon side by side.
            float x = -0.04f;
            foreach (var (node, part) in Renderers(Build(g, r.AmmoOf)).Where(n => n.Part.StartsWith("ammo")).DistinctBy(n => n.Part)
                .OrderBy(n => n.Part == "ammo_speedloader" ? 0 : n.Part == "ammo_shell" ? 1 : 2))
            {
                Matrix4x4.Decompose(node.World, out var scale, out var rotation, out _);
                var copy = new Node { Name = node.Name, Mesh = node.Mesh, Materials = node.Materials };
                copy.ResetRoot(part);
                (copy.Scale, copy.Rotation, copy.Position) = (scale, rotation, new Vector3(x, 0, 0));
                copy.Attach(holder);
                x += 0.04f;
            }
        }
        else if (r.Comps != null)
        {
            (holder, _) = Assembly.Build(g, r.Comps);
            // The ejected casing, shown at the shell-eject align.
            if (r.Shell != null && g.TryPrefab("shell/" + r.Shell, out var at))
            {
                var shell = Unity.LoadPrefab(g, at);
                shell.ResetRoot("ammo_shell");
                if (holder.Find("Front_SE") is { } eject) shell.Position = eject.World.Translation;
                shell.Attach(holder);
            }
        }
        else
        {
            foreach (var prefab in r.Prefabs!)
            {
                var part = Unity.LoadPrefab(g, g.Prefab(prefab));
                part.ResetRoot(r.Slot!);
                part.Attach(holder);
            }
        }
        return holder;
    }

    public static CatalogItem Item(List<CatalogItem> items, string id) =>
        items.FirstOrDefault(i => i.Id == id) ?? throw new BackendException("not-found", $"Catalog item {id} not found");

    static readonly Regex AmmoMaterial = new("^(Casing_|Revolver_Shell|Revolver_Speedloader|Shotgun_Shell)", RegexOptions.IgnoreCase);

    // Visible renderer nodes with their part ids (inactive subtrees and decal projectors skipped).
    public static IEnumerable<(Node Node, string Part)> Renderers(Node root)
    {
        var stack = new Stack<(Node, string)>();
        stack.Push((root, root.Part ?? ""));
        while (stack.Count > 0)
        {
            var (n, slot) = stack.Pop();
            if (!n.Active) continue;
            slot = n.Part ?? slot;
            if (n.Mesh != null && n.Materials.Count > 0 && !n.Name.StartsWith("DECAL_", StringComparison.OrdinalIgnoreCase))
                yield return (n, PartOf(n, slot));
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push((n.Children[i], slot));
        }
    }

    static string PartOf(Node n, string slot)
    {
        if (slot.StartsWith("ammo") || !AmmoMaterial.IsMatch(n.Materials[0].Name)) return slot;
        var mesh = n.Mesh!.Name;
        if (mesh.Contains("speedloader", StringComparison.OrdinalIgnoreCase)) return "ammo_speedloader";
        // Rounds inside the gun are loaded rounds, whatever the mesh is called (shotgun tubes reuse the "Shotgun_Shell_Emty"
        // mesh); only a revolver's two-piece round has its own casing. Empty casings are the ejected shell prefab (Build).
        return mesh.StartsWith("Revolver_Shell", StringComparison.OrdinalIgnoreCase) ? "ammo_shell" : "ammo_bullet";
    }

    static readonly List<string> PartOrder = Assembly.Slots.Select(s => s.Id).Concat(["body", "ammo_speedloader", "ammo_bullet", "ammo_shell", "lens"]).ToList();

    static List<CatalogPart> Parts(Node root)
    {
        var byPart = new Dictionary<string, List<string>>();
        foreach (var (node, part) in Renderers(root))
        {
            if (!byPart.TryGetValue(part, out var list)) byPart[part] = list = new();
            foreach (var m in node.Materials) if (m.Name != "" && !list.Contains(m.Name)) list.Add(m.Name);
        }
        return byPart.OrderBy(kv => PartOrder.IndexOf(kv.Key))
            .Select(kv => new CatalogPart(kv.Key, kv.Key.StartsWith("ammo") ? "ammo" : kv.Key,
                (string?)Naming["partsZh"]![kv.Key] ?? kv.Key, kv.Value, kv.Key != "flashlight"))
            .ToList();
    }

    static string? CategoryZh(string category) => (string?)Naming["categoriesZh"]![category];

    static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    static Node Holder() => new() { Name = "Gear" };

    static List<CatalogItem> Load(GameAssets g)
    {
        var items = new List<CatalogItem>();
        var categories = g.Blocks("GearCategory");
        var calibres = new Dictionary<string, (JsonObject Calibre, Recipe Weapon)>();
        var attachments = new SortedSet<(int Order, string Slot, string Block, uint Id)>();

        foreach (var gear in g.Blocks("PlayerOfflineGear").Values.Where(b => (int)b["Type"]! == 1 && (bool)b["internalEnabled"]!))
        {
            var comps = Assembly.Comps(gear);
            if (!comps.TryGetValue(2, out var catId) || !categories.TryGetValue(catId, out var cat)) continue;
            // InventorySlot: guns, the class tool and the melee weapon; the hacking tool is not built from gear parts.
            int slotNo = comps.TryGetValue(3, out var itemId) && g.Blocks("Item").TryGetValue(itemId, out var it) ? (int)it["inventorySlot"]! : 0;
            var slotName = slotNo switch { 1 => "main", 2 => "special", 3 => "tool", 10 => "melee", _ => null };
            if (slotName == null) continue;

            var english = Assembly.PublicName(gear);
            var naming = Naming["weapons"]![english];
            var catName = (string)cat["name"]!;
            var calibre = catName.Contains("Revolver") ? Naming["calibres"]!["revolver"]!.AsObject()
                : comps.TryGetValue(7, out var shellId) && g.Blocks("WeaponShellCasing").TryGetValue(shellId, out var sc)
                    ? Naming["calibres"]![((int)sc["ShellCasingType"]!).ToString()]?.AsObject() : null;
            var recipe = new Recipe(comps, (string?)calibre?["shellPrefab"]);
            var parts = Parts(Build(g, recipe));
            if (parts.Count == 0) continue;

            items.Add(new CatalogItem((string?)naming?["id"] ?? Slug((string)gear["name"]!), english, (string?)naming?["nameZh"], catName,
                slotName, (uint)gear["persistentID"]!, (string?)calibre?["label"], parts)
            { Source = recipe, Order = (int?)naming?["order"] ?? 500, CategoryZh = (string?)naming?["categoryZh"] });

            if (calibre != null) calibres.TryAdd((string)calibre["id"]!, (calibre, recipe));
            foreach (var (comp, slot, block, order) in new[] { (23, "mag", "GearMagPart", 0), (25, "flashlight", "GearFlashlightPart", 1), (21, "sight", "GearSightPart", 2) })
                if (comps.TryGetValue(comp, out var pid) && pid != 0) attachments.Add((order, slot, block, pid));
        }
        // Items with their own prefabs: resource packs, consumables, large carry items and the hacking tool. The first-person
        // prefabs (the equippable and its model) spawn together as one `body` part. Key items and commodities have no
        // first-person prefab.
        foreach (var row in g.Blocks("Item").Values.Where(r => (bool)r["internalEnabled"]!))
        {
            var slot = (int)row["inventorySlot"]! switch { 4 => "pack", 5 => "consumable", 8 => "carry", 11 => "tool", _ => null };
            if (slot == null) continue;
            var prefabs = row["FirstPersonPrefabs"]!.AsArray().Select(p => (string)p!).Where(p => g.TryPrefab(p, out _)).ToArray();
            if (prefabs.Length == 0) continue;
            var recipe = new Recipe(Prefabs: prefabs, Slot: "body");
            var parts = Parts(Build(g, recipe));
            if (parts.Count == 0) continue;
            var name = (string)row["name"]!;
            // Unreleased items: every visible renderer is placeholder geometry with Unity's built-in material.
            if (parts.SelectMany(p => p.Materials).All(m => m == "Default-Material"))
            {
                Log.Write($"catalog: skipped item {row["persistentID"]} {name}: only placeholder geometry");
                continue;
            }
            var naming = Naming["items"]![name];
            items.Add(new CatalogItem((string?)naming?["id"] ?? Slug(name), (string?)naming?["name"] ?? (string)row["publicName"]!, (string?)naming?["nameZh"],
                slot, slot, null, null, parts)
            { ItemId = (uint)row["persistentID"]!, Source = recipe, Order = (int?)naming?["order"] ?? 900, CategoryZh = (string?)naming?["categoryZh"] });
        }
        items.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Name, b.Name));

        // Attachments: every mag, flashlight and sight the weapons use, spawned alone; lenses from their glass materials.
        var lenses = new Dictionary<string, Recipe>();
        foreach (var (_, slot, block, pid) in attachments)
        {
            var row = g.Blocks(block)[pid];
            var model = (string?)row["General"]!["Model"];
            if (string.IsNullOrEmpty(model) || !g.TryPrefab(model, out _)) continue;
            var recipe = new Recipe(Prefabs: [model], Slot: slot);
            var parts = Parts(Build(g, recipe));
            var name = (string)row["name"]!;
            items.Add(new CatalogItem(Slug(name), name.Replace('_', ' '), null, slot, "attachment", null, null, parts) { Source = recipe, CategoryZh = CategoryZh(slot) });
            foreach (var m in parts.SelectMany(p => p.Materials).Where(m => Regex.IsMatch(m, "glass|lens", RegexOptions.IgnoreCase)))
                lenses.TryAdd(m, recipe);
        }
        foreach (var (material, recipe) in lenses)
            items.Add(new CatalogItem("lens_" + Slug(material), material.Replace('_', ' '), null, "lens", "attachment", null, null,
                [new CatalogPart("lens", "lens", (string)Naming["partsZh"]!["lens"]!, [material], true)]) { Source = recipe, CategoryZh = CategoryZh("lens") });

        // Ammo per calibre, taken from the first weapon that uses it: speed loader, shell, bullet side by side.
        foreach (var (calId, (calibre, weapon)) in calibres.OrderBy(kv => kv.Key))
        {
            var recipe = new Recipe(AmmoOf: weapon);
            items.Add(new CatalogItem("ammo_" + calId, (string)calibre["label"]!, (string?)calibre["nameZh"], "ammo", "ammo", null,
                (string)calibre["label"]!, Parts(Build(g, recipe))) { Source = recipe, CategoryZh = CategoryZh("ammo") });
        }
        return items;
    }
}
