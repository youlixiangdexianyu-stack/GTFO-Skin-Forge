using System.Text.Json;
using Gear;
using UnityEngine;

namespace ForgeSkin.Plugin;

// Config `Readback`: the vanilla pose of every part and descendant, written before any skin touches the gear,
// local to the holder transform (Unity space). One file per gear (PlayerOfflineGearDataBlock id) per session.
static class Readback
{
    public static string? Directory;
    static readonly HashSet<uint> Written = new();

    public static void Write(GearPartHolder holder, uint gearId)
    {
        if (Directory == null || gearId == 0 || !Written.Add(gearId)) return;
        var file = Path.Combine(Directory, gearId + ".json");
        var root = holder.transform;
        var inverse = Quaternion.Inverse(root.rotation);
        var rootScale = root.lossyScale;
        var nodes = new List<object>();
        foreach (var (id, get) in SkinnedGear.Parts)
        {
            var part = get(holder);
            if (part != null) Collect(id, null, part.transform);
        }
        ForgeCore.Store.WriteText(file, JsonSerializer.Serialize(
            new { gearId, gear = holder.GearIDRange?.PublicGearName ?? "", nodes }, new JsonSerializerOptions { WriteIndented = true }));
        ForgeSkinPlugin.Info($"readback {holder.GearIDRange?.PublicGearName}: wrote {file}");

        void Collect(string part, string? path, Transform t)
        {
            var p = root.InverseTransformPoint(t.position);
            var r = inverse * t.rotation;
            var s = t.lossyScale;
            nodes.Add(new
            {
                part,
                path,
                position = new[] { p.x, p.y, p.z },
                rotation = new[] { r.x, r.y, r.z, r.w },
                scale = new[] { s.x / rootScale.x, s.y / rootScale.y, s.z / rootScale.z }
            });
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                Collect(part, path == null ? child.name : path + "/" + child.name, child);
            }
        }
    }
}
