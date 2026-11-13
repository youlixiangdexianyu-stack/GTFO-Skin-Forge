using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ForgeSkin.Backend;

public sealed record CatalogPart(string Id, string Role, string Label, List<string> Materials, bool DefaultEnabled);

public sealed record CatalogItem(string Id, string Name, string? NameZh, string Category, string Slot, uint? GearId,
    string? Calibre, List<CatalogPart> Parts)
{
    public string? CategoryZh { get; init; }
    // ItemDataBlock persistentID of an item built from its own first-person prefabs (packs, consumables, carry items,
    // the hacking tool); null for gear built from parts, which has GearId.
    public uint? ItemId { get; init; }
    // Guns: magazine size and the most bullets the reserve holds (the screen's automatic digit count); null otherwise.
    public int? ClipSize { get; init; }
    public int? MaxReserve { get; init; }
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
    static bool _warmed;

    sealed record Cached(CatalogItem Item, Recipe Source);

    // Built once per game build and kept in that build's cache folder; later launches read only that file.
    // Only the located game's reader warms the preview cache (which drops other builds' caches); an assets.* gameDir
    // override reads its catalog and touches no cache folder of the located game.
    public static List<CatalogItem> Get(GameAssets g)
    {
        if (_items == null || _build != g.BuildId)
        {
            var file = Path.Combine(g.Cache, "catalog-2.json");
            List<CatalogItem> items;
            try
            {
                items = JsonSerializer.Deserialize<List<Cached>>(File.ReadAllText(file), Program.Json)!.Select(c => c.Item with { Source = c.Source }).ToList();
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
                items = Load(g);
                Directory.CreateDirectory(g.Cache);
                AtomicFile.WriteText(file, JsonSerializer.Serialize(items.Select(i => new Cached(i, i.Source)), Program.Json));
            }
            (_items, _build, _warmed) = (items, g.BuildId, false);
        }
        if (!_warmed && Path.GetDirectoryName(g.DataDir)!.Equals(Path.TrimEndingDirectorySeparator(Game.Locate().GameDir ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            _warmed = true;
            PreviewCache.Ready(g, _items);
        }
        return _items;
    }

    public static Node Build(GameAssets g, Recipe r)
    {
        var holder = Holder();
        if (r.AmmoOf != null)
        {
            // One piece per ammo part of the weapon, turned as in the gun and laid out in a row by AmmoLayout. The copies
            // sit directly under the holder, so the layout's world offsets are their positions.
            foreach (var (node, part) in Renderers(Build(g, r.AmmoOf)).Where(n => n.Part.StartsWith("ammo")).DistinctBy(n => n.Part))
            {
                Matrix4x4.Decompose(node.World, out var scale, out var rotation, out _);
                var copy = new Node { Name = node.Name, Mesh = node.Mesh, Materials = node.Materials };
                copy.ResetRoot(part);
                (copy.Scale, copy.Rotation) = (scale, rotation);
                copy.Attach(holder);
            }
            foreach (var piece in AmmoLayout(holder, n => Unity.ReadMesh(g, n.Mesh!))) piece.Shown.Position += piece.Offset;
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

    public sealed record AmmoPiece(string Part, Node Shown, Vector3 Offset, List<Node> Hidden);

    // How ammo is shown (editor preview and OBJ export): ammo parts sit inside the gun, and some of their nodes rest
    // wherever the game's animator leaves them (an ejected shell's bone can rest well away from the gun). Each ammo part
    // shows one piece (a whole round, an empty casing, a speed loader), the first outermost node: the part's own root
    // transform when it has one (the ejected casing), else a renderer node. The other rounds of the magazine are hidden;
    // they keep their part, so a skin still applies to all of them in game. The pieces line up in one row just below the
    // gun (or below the origin when there is no gun), from the muzzle backwards in AmmoParts order. Offsets are world
    // translations in Unity space; display only, never stored in the document and never applied in game.
    static readonly string[] AmmoParts = ["ammo_speedloader", "ammo_bullet", "ammo_shell"];

    public static List<AmmoPiece> AmmoLayout(Node root, Func<Node, MeshData> mesh)
    {
        var renderers = Renderers(root).ToList();
        static bool Under(Node n, Node top) { for (Node? p = n; p != null; p = p.Parent) if (p == top) return true; return false; }
        (Vector3 Min, Vector3 Max) Bounds(Func<(Node Node, string Part), bool> include)
        {
            var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            foreach (var r in renderers.Where(include))
            {
                var world = r.Node.World;
                foreach (var p in mesh(r.Node).Positions) { var w = Vector3.Transform(p, world); (min, max) = (Vector3.Min(min, w), Vector3.Max(max, w)); }
            }
            return (min, max);
        }
        var gun = Bounds(r => !r.Part.StartsWith("ammo"));
        if (gun.Min.X > gun.Max.X) gun = (Vector3.Zero, Vector3.Zero);
        float gap = Math.Clamp((gun.Max - gun.Min).Length() * 0.04f, 0.01f, 0.03f);

        var items = new List<(string Part, Node Node, (Vector3 Min, Vector3 Max) Box, List<Node> Hidden)>();
        foreach (var part in AmmoParts)
        {
            var own = renderers.Where(r => r.Part == part).Select(r => r.Node).ToList();
            var top = own.Select(n => { Node? t = null; for (Node? p = n; p != null; p = p.Parent) if (p.Part == part) t = p; return t; }).FirstOrDefault(t => t != null);
            var nodes = (top == null ? own : own.Where(n => !Under(n, top)).Prepend(top)).ToList();
            var pieces = nodes.Where(n => !nodes.Any(m => m != n && Under(n, m)))
                .Select(n => (Node: n, Box: Bounds(r => Under(r.Node, n)))).Where(x => x.Box.Min.X <= x.Box.Max.X).ToList();
            if (pieces.Count > 0) items.Add((part, pieces[0].Node, pieces[0].Box, pieces.Skip(1).Select(x => x.Node).ToList()));
        }
        float height = items.Select(i => i.Box.Max.Y - i.Box.Min.Y).DefaultIfEmpty(0).Max();
        float bottom = gun.Min.Y - gap - height, centerX = (gun.Min.X + gun.Max.X) / 2, cursor = gun.Max.Z; // Unity +Z is the muzzle
        var result = new List<AmmoPiece>();
        foreach (var (part, node, box, hidden) in items)
        {
            result.Add(new AmmoPiece(part, node, new Vector3(centerX - (box.Min.X + box.Max.X) / 2, bottom - box.Min.Y, cursor - box.Max.Z), hidden));
            cursor -= box.Max.Z - box.Min.Z + gap / 2;
        }
        return result;
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

    // A gun's magazine and reserve maximum in bullets, as the game derives them: the archetype of its fire mode (gear
    // component 1) in its category gives DefaultClipSize (times the mag part's ClipSizeMultiplier) and CostOfBullet; the
    // slot's ammo cap in PlayerDataBlock divided by that cost is the reserve. Melee weapons and tools get none.
    static (int? Clip, int? Reserve) Ammo(GameAssets g, Dictionary<int, uint> comps, JsonObject category, string slot)
    {
        if (slot is not ("main" or "special")) return (null, null);
        var key = (comps.TryGetValue(1, out var mode) ? mode : 0) switch
        {
            0 => "SemiArchetype",
            1 => "BurstArchetype",
            2 => "AutoArchetype",
            3 => "SemiBurstArchetype",
            _ => null,
        };
        if (key == null || (uint?)category[key] is not { } id || !g.Blocks("Archetype").TryGetValue(id, out var a)) return (null, null);
        float multiplier = comps.TryGetValue(23, out var mag) && g.Blocks("GearMagPart").TryGetValue(mag, out var m) ? (float)m["ClipSizeMultiplier"]! : 1f;
        int clip = (int)((int)a["DefaultClipSize"]! * multiplier);
        float cost = (float)a["CostOfBullet"]!;
        float cap = (float)g.Blocks("Player").Values.First()[slot == "main" ? "AmmoStandardMaxCap" : "AmmoSpecialMaxCap"]!;
        return (clip, cost > 0 ? (int)(cap / cost) : null);
    }

    static List<CatalogItem> Load(GameAssets g)
    {
        var items = new List<CatalogItem>();
        var categories = g.Blocks("GearCategory");
        var calibres = new Dictionary<string, (JsonObject Calibre, Recipe Weapon, int AmmoParts)>();
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

            var (clip, reserve) = Ammo(g, comps, cat, slotName);
            items.Add(new CatalogItem((string?)naming?["id"] ?? Slug((string)gear["name"]!), english, (string?)naming?["nameZh"], catName,
                slotName, (uint)gear["persistentID"]!, (string?)calibre?["label"], parts)
            { Source = recipe, Order = (int?)naming?["order"] ?? 500, CategoryZh = (string?)naming?["categoryZh"], ClipSize = clip, MaxReserve = reserve });

            int ammoParts = parts.Count(p => p.Role == "ammo");
            if (calibre != null && (!calibres.TryGetValue((string)calibre["id"]!, out var had) || had.AmmoParts < ammoParts))
                calibres[(string)calibre["id"]!] = (calibre, recipe, ammoParts);
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
                [new CatalogPart("lens", "lens", (string)Naming["partsZh"]!["lens"]!, [material], true)])
            { Source = recipe, CategoryZh = CategoryZh("lens") });

        // Ammo per calibre, taken from the first weapon that uses it with the most ammo parts (a gun without a magazine has
        // no whole rounds): one piece per ammo part, laid out as in Build.
        foreach (var (calId, (calibre, weapon, _)) in calibres.OrderBy(kv => kv.Key))
        {
            var recipe = new Recipe(AmmoOf: weapon);
            items.Add(new CatalogItem("ammo_" + calId, (string)calibre["label"]!, (string?)calibre["nameZh"], "ammo", "ammo", null,
                (string)calibre["label"]!, Parts(Build(g, recipe)))
            { Source = recipe, CategoryZh = CategoryZh("ammo") });
        }
        return items;
    }
}
