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
        System.IO.Directory.CreateDirectory(Directory);
        var root = holder.transform;
        var inverse = Quaternion.Inverse(root.rotation);
        var rootScale = root.lossyScale;
        using (var stream = File.Create(file))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("gearId", gearId);
            json.WriteString("gear", holder.GearIDRange?.PublicGearName ?? "");
            json.WriteStartArray("nodes");
            foreach (var (id, get) in SkinnedGear.Parts)
            {
                var part = get(holder);
                if (part != null) Node(json, id, null, part.transform);
            }
            json.WriteEndArray();
            json.WriteEndObject();

            void Node(Utf8JsonWriter w, string part, string? path, Transform t)
            {
                var p = root.InverseTransformPoint(t.position);
                var r = inverse * t.rotation;
                var s = t.lossyScale;
                w.WriteStartObject();
                w.WriteString("part", part);
                if (path == null) w.WriteNull("path");
                else w.WriteString("path", path);
                Floats(w, "position", p.x, p.y, p.z);
                Floats(w, "rotation", r.x, r.y, r.z, r.w);
                Floats(w, "scale", s.x / rootScale.x, s.y / rootScale.y, s.z / rootScale.z);
                w.WriteEndObject();
                for (int i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i);
                    Node(w, part, path == null ? child.name : path + "/" + child.name, child);
                }
            }
        }
        ForgeSkinPlugin.Info($"readback {holder.GearIDRange?.PublicGearName}: wrote {file}");
    }

    // Utf8JsonWriter formats numbers invariantly.
    static void Floats(Utf8JsonWriter w, string name, params float[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }
}
