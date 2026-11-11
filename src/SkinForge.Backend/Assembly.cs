using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkinForge.Backend;

// Rebuilds a gear the way GearPartHolder does: every part prefab is spawned with its root reset under the holder (or
// under the align transform it mounts on), aligns are fetched by name from the parts' DataBlock Aligns, and sight, mag
// and flashlight mount on the Sight, Magazine and Flashlight aligns. Result is GTFO weapon space (1.0x, Unity axes).
public static class Assembly
{
    // eGearComponent -> (part id, DataBlock, eGearPartAlign it mounts on when a part names it (else the holder origin; only
    // the sentry names Front and Receiver, its turret mount), and for tool parts
    // whose model depends on a type the component holding that type: its GeneralDatas entry of that type is spawned).
    public static readonly (int Comp, string Id, string Block, int? Align, int? Type)[] Slots =
    [
        (12, "front", "GearFrontPart", 10, null),
        (16, "receiver", "GearReceiverPart", 9, null),
        (19, "stock", "GearStockPart", null, null),
        (21, "sight", "GearSightPart", 4, null),
        (23, "mag", "GearMagPart", 2, null),
        (25, "flashlight", "GearFlashlightPart", 5, null),
        (13, "front_attachment_a", "GearPartAttachment", null, null),
        (14, "front_attachment_b", "GearPartAttachment", null, null),
        (17, "receiver_attachment", "GearPartAttachment", null, null),
        (27, "tool_main", "GearToolMainPart", null, null),
        (29, "tool_main_attachment", "GearPartAttachment", null, null),
        (30, "tool_grip", "GearToolGripPart", 12, null),
        (33, "tool_delivery", "GearToolDeliveryPart", 13, 32),
        (35, "tool_delivery_attachment", "GearPartAttachment", null, null),
        (37, "tool_payload", "GearToolPayloadPart", 14, 36),
        (40, "tool_targeting", "GearToolTargetingPart", 15, 39),
        (42, "tool_screen", "GearToolScreenPart", 16, null),
        (44, "melee_head", "GearMeleeHeadPart", 20, null),
        (46, "melee_neck", "GearMeleeNeckPart", null, null),
        (48, "melee_handle", "GearMeleeHandlePart", null, null),
        (50, "melee_pommel", "GearMeleePommelPart", null, null),
    ];

    // Attachment components mount on their base part's root.
    static readonly Dictionary<int, int> AttachedTo = new() { [13] = 12, [14] = 12, [17] = 16, [29] = 27, [35] = 33 };

    public sealed record Part(string Id, int Comp, JsonObject Block, Node Root);

    // Every transform of every part with its child path from the part root as Transform.Find takes it (null = the root).
    // A part mounted on another (a mag on the stock's a_mag) owns its own subtree.
    public static IEnumerable<(string Part, string? Path, Node Node)> PartNodes(Node holder, bool activeOnly)
    {
        foreach (var root in holder.All().Where(n => n.Part != null))
        {
            var stack = new Stack<(Node Node, string? Path)>();
            stack.Push((root, null));
            while (stack.Count > 0)
            {
                var (n, path) = stack.Pop();
                if (activeOnly && !n.Active) continue;
                yield return (root.Part!, path, n);
                for (int i = n.Children.Count - 1; i >= 0; i--)
                    if (n.Children[i].Part == null) stack.Push((n.Children[i], path == null ? n.Children[i].Name : $"{path}/{n.Children[i].Name}"));
            }
        }
    }

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
            var root = Spawn(g, General(block, slot.Type is { } type ? comps.GetValueOrDefault(type) : null), slot.Id);
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
            else if (AttachedTo.TryGetValue(slot.Comp, out var baseComp)) parent = parts.FirstOrDefault(p => p.Comp == baseComp)?.Root ?? holder;
            part.Root.Attach(parent);
        }
        return (holder, parts);
    }

    // A part's model data: `General`, or for typed tool parts the GeneralDatas entry of the gear's type (0 when unset);
    // null when the part has no model for that type.
    static JsonNode? General(JsonObject block, uint? type) => type == null ? block["General"]
        : block["GeneralDatas"]?.AsArray().FirstOrDefault(d => (uint)d!["Type"]! == type)?["Data"];

    // Prefab plus its General.Children prefabs, root transform reset (the spawner parents with identity local pose).
    static Node? Spawn(GameAssets g, JsonNode? general, string part)
    {
        var model = (string?)general?["Model"];
        if (string.IsNullOrEmpty(model)) return null;
        var root = Unity.LoadPrefab(g, g.Prefab(model));
        root.ResetRoot(part);
        foreach (var child in general!["Children"]?.AsArray().Select(c => (string)c!) ?? [])
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
