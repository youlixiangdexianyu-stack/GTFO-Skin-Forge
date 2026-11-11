using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkinForge.Backend;

// Rebuilds a gear the way GearPartHolder does: every part prefab is spawned with its root reset under the holder (or
// under the align transform it mounts on), aligns are fetched by name from the parts' DataBlock Aligns, and sight, mag
// and flashlight mount on the Sight, Magazine and Flashlight aligns. Result is GTFO weapon space (1.0x, Unity axes).
public static class Assembly
{
    // eGearComponent -> (part id, DataBlock, align it mounts on or null for the holder origin).
    public static readonly (int Comp, string Id, string Block, int? Align)[] Slots =
    [
        (12, "front", "GearFrontPart", null),
        (16, "receiver", "GearReceiverPart", null),
        (19, "stock", "GearStockPart", null),
        (21, "sight", "GearSightPart", 4),
        (23, "mag", "GearMagPart", 2),
        (25, "flashlight", "GearFlashlightPart", 5),
        (13, "front_attachment_a", "GearPartAttachment", null),
        (14, "front_attachment_b", "GearPartAttachment", null),
        (17, "receiver_attachment", "GearPartAttachment", null),
    ];

    public sealed record Part(string Id, int Comp, JsonObject Block, Node Root);

    public static Dictionary<int, uint> Comps(JsonObject offlineGear)
    {
        var packet = JsonNode.Parse((string)offlineGear["GearJSON"]!)!["Packet"]!["Comps"]!.AsObject();
        return packet.Where(kv => kv.Key != "Length").ToDictionary(kv => (int)kv.Value!["c"]!, kv => (uint)kv.Value!["v"]!);
    }

    public static string PublicName(JsonObject offlineGear) => (string)JsonNode.Parse((string)offlineGear["GearJSON"]!)!["Name"]!;

    // Spawns the parts of a gear. The returned root is the holder; part roots are its descendants.
    public static (Node Holder, List<Part> Parts) Build(GameAssets g, Dictionary<int, uint> comps)
    {
        var holder = new Node { Name = "Gear" };
        var parts = new List<Part>();
        foreach (var slot in Slots)
        {
            if (!comps.TryGetValue(slot.Comp, out var partId) || partId == 0) continue;
            var block = g.Blocks(slot.Block)[partId];
            var root = Spawn(g, block, slot.Id);
            if (root == null) continue;
            parts.Add(new Part(slot.Id, slot.Comp, block, root));
        }

        var category = g.Blocks("GearCategory")[comps[2]];
        var prio = category["PartAlignPriority"]?.AsArray()
            .ToDictionary(p => (int)p!["AlignType"]!, p => p!["PartPrio"]!.AsArray().Select(c => (int)c!).ToList()) ?? new();

        foreach (var part in parts)
        {
            var slot = Slots.First(s => s.Id == part.Id);
            Node parent = holder;
            if (slot.Align is { } align && FindAlign(parts, align, prio) is { } at) parent = at;
            else if (slot.Comp is 13 or 14) parent = parts.FirstOrDefault(p => p.Comp == 12)?.Root ?? holder;
            else if (slot.Comp == 17) parent = parts.FirstOrDefault(p => p.Comp == 16)?.Root ?? holder;
            part.Root.Attach(parent);
        }
        return (holder, parts);
    }

    // Prefab plus its General.Children prefabs, root transform reset (the spawner parents with identity local pose).
    static Node? Spawn(GameAssets g, JsonObject block, string part)
    {
        var general = block["General"]!;
        var model = (string?)general["Model"];
        if (string.IsNullOrEmpty(model)) return null;
        var root = Unity.LoadPrefab(g, g.Prefab(model));
        root.ResetRoot(part);
        foreach (var child in general["Children"]?.AsArray().Select(c => (string)c!) ?? [])
        {
            if (!g.TryPrefab(child, out var at)) continue;
            var node = Unity.LoadPrefab(g, at);
            node.ResetRoot(part);
            node.Attach(root);
        }
        return root;
    }

    // The align transform of a type. Parts spawn in slot order and a later part that names the align overrides an
    // earlier one (Receiver_Revolver_2's Front_Flash wins over the front's); a category PartAlignPriority listing
    // spawned components goes first.
    static Node? FindAlign(List<Part> parts, int alignType, Dictionary<int, List<int>> prio)
    {
        var order = Enumerable.Reverse(parts).ToList();
        if (prio.TryGetValue(alignType, out var comps))
            order = order.OrderBy(p => comps.IndexOf(p.Comp) is var i && i >= 0 ? i : int.MaxValue).ToList();
        foreach (var part in order)
            foreach (var a in part.Block["Aligns"]?.AsArray() ?? [])
                if ((int)a!["AlignType"]! == alignType && part.Root.Find((string)a["AlignName"]!) is { } node)
                    return node;
        return null;
    }
}
