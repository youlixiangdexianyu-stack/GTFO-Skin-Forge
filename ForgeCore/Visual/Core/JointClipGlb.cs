using System;
using System.Numerics;
using System.Text.Json;

namespace ForgeVisual;

public sealed partial class JointClip
{
    /// <summary>
    /// Reads a Forge animation file, the one action format of the site, its tools and the game (written only by the site's
    /// <c>Web/src/forge/animation-glb.ts</c>): a glTF 2.0 binary holding the canonical humanoid (<see cref="ClipRig"/>) as named nodes at its
    /// rest pose and one animation, keyframes as float32 at a fixed rate, linear. The hip drives its place and turn, every other joint only its
    /// turn; a clip drives all 20 body joints, with all 30 fingers or none. Anything else is refused, never guessed at or patched: another
    /// layout, an extension, a mesh, a hand layer, enemy nodes, a skeleton off the canonical rest, times that do not start at 0 or rise evenly, a
    /// turn that is not a unit quaternion. Throws <see cref="FormatException"/> naming the first fault. Reads once, at load; playing reads nothing.
    /// </summary>
    public static JointClip Read(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Need(file.Length >= 28 && U32(file, 0) == 0x46546C67 && U32(file, 4) == 2 && U32(file, 8) == (uint)file.Length, "not a glTF 2.0 binary");
        var jsonLength = (int)Math.Min(U32(file, 12), int.MaxValue);
        Need(U32(file, 16) == 0x4E4F534A && jsonLength % 4 == 0 && 28L + jsonLength <= file.Length, "JSON chunk");
        var binStart = 28 + jsonLength;
        var binLength = (long)U32(file, 20 + jsonLength);
        Need(U32(file, 24 + jsonLength) == 0x004E4942 && binStart + binLength == file.Length, "BIN chunk");
        JsonDocument document;
        try { document = JsonDocument.Parse(file.AsMemory(20, jsonLength)); }
        catch (JsonException) { throw Fault("JSON chunk"); }
        using (document) return Read(document.RootElement, file, binStart, (int)binLength);
    }

    private static JointClip Read(JsonElement root, byte[] file, int binStart, int binLength)
    {
        Only(root, "root", "asset", "scene", "scenes", "nodes", "animations", "accessors", "bufferViews", "buffers");
        var asset = Only(Field(root, "asset"), "asset", "version", "generator");
        Need(Field(asset, "version").ValueKind == JsonValueKind.String && Field(asset, "version").GetString() == "2.0", "asset version");
        var buffers = Items(Field(root, "buffers"), "buffers");
        Need(buffers.GetArrayLength() == 1 && Int(Field(Only(buffers[0], "buffer", "byteLength"), "byteLength"), "buffer") == binLength, "buffer length");

        var viewRows = Items(Field(root, "bufferViews"), "bufferViews");
        var views = new (int Start, int Length)[viewRows.GetArrayLength()];
        for (var v = 0; v < views.Length; v++)
        {
            var row = Only(viewRows[v], "bufferView", "buffer", "byteOffset", "byteLength");
            int start = Optional(row, "byteOffset", "bufferView"), length = Int(Field(row, "byteLength"), "bufferView");
            Need(Int(Field(row, "buffer"), "bufferView") == 0 && start >= 0 && length >= 0 && (long)start + length <= binLength, "bufferView");
            views[v] = (start, length);
        }
        var accessors = Items(Field(root, "accessors"), "accessors");
        // An accessor of float32 values, checked to lie inside its buffer view: where its bytes start in the file, and how many elements it has.
        (int From, int Count) Accessor(int at, int size, string where)
        {
            Need(at >= 0 && at < accessors.GetArrayLength(), where);
            var row = Only(accessors[at], where, "bufferView", "byteOffset", "componentType", "count", "type", "min", "max");
            var view = Int(Field(row, "bufferView"), where);
            int start = Optional(row, "byteOffset", where), count = Int(Field(row, "count"), where);
            var type = Field(row, "type");
            Need(view >= 0 && view < views.Length && Int(Field(row, "componentType"), where) == 5126 && count >= 1, where);
            Need(type.ValueKind == JsonValueKind.String && type.GetString() == (size == 1 ? "SCALAR" : size == 3 ? "VEC3" : "VEC4"), where);
            Need(start >= 0 && start % 4 == 0 && start + (long)count * size * 4 <= views[view].Length, where);
            return (binStart + views[view].Start + start, count);
        }
        float Float(int at)
        {
            var value = BitConverter.ToSingle(file, at);
            if (!float.IsFinite(value)) throw Fault("a value is not finite");
            return value;
        }
        // The nodes: exactly the canonical joints, in ClipRig order (the body, then all the fingers or none), each under its canonical parent at its canonical rest.
        var nodes = Items(Field(root, "nodes"), "nodes");
        var joints = nodes.GetArrayLength();
        if (joints != ClipRig.BodyCount && joints != ClipRig.Count) throw Fault("the file holds " + joints + " nodes, not the " + ClipRig.BodyCount + " body joints or all " + ClipRig.Count + " joints");
        var parent = new int[joints];
        Array.Fill(parent, -1);
        for (var j = 0; j < joints; j++)
        {
            var name = ClipRig.Names[j];
            var node = Only(nodes[j], name, "name", "children", "translation", "rotation", "scale");
            Need(Field(node, "name").ValueKind == JsonValueKind.String && Field(node, "name").GetString() == name, name);
            if (node.TryGetProperty("children", out var children))
                foreach (var child in Items(children, name).EnumerateArray())
                {
                    var c = Int(child, name);
                    Need(c > j && c < joints && parent[c] < 0, name);
                    parent[c] = j;
                }
            if (node.TryGetProperty("scale", out var scale)) Need(Near(Numbers(scale, 3, name), Vector3.One, 0f), name);
            var t = node.TryGetProperty("translation", out var tv) ? Numbers(tv, 3, name) : new float[3];
            var r = node.TryGetProperty("rotation", out var rv) ? Numbers(rv, 4, name) : new float[] { 0f, 0f, 0f, 1f };
            var pose = ClipRig.Pose[j];
            Need(Near(t, ClipRig.Offset[j], RestTolerance) && Math.Abs(r[0] - pose.X) <= RestTolerance && Math.Abs(r[1] - pose.Y) <= RestTolerance
                && Math.Abs(r[2] - pose.Z) <= RestTolerance && Math.Abs(r[3] - pose.W) <= RestTolerance, name);
        }
        for (var j = 0; j < joints; j++) Need(parent[j] == ClipRig.Parent[j], ClipRig.Names[j]);
        var scenes = Items(Field(root, "scenes"), "scenes");
        Need(scenes.GetArrayLength() == 1 && Int(Field(root, "scene"), "scene") == 0, "scenes");
        var roots = Items(Field(Only(scenes[0], "scene", "nodes"), "nodes"), "scene");
        Need(roots.GetArrayLength() == 1 && Int(roots[0], "scene") == 0, "scene roots");

        // The animation: one, every channel with its own linear sampler on one shared time accessor.
        var animations = Items(Field(root, "animations"), "animations");
        Need(animations.GetArrayLength() == 1, "animations");
        var animation = Only(animations[0], "animation", "name", "channels", "samplers");
        Need(Field(animation, "name").ValueKind == JsonValueKind.String, "animation");
        var channels = Items(Field(animation, "channels"), "animation channels");
        var samplers = Items(Field(animation, "samplers"), "animation samplers");
        Need(channels.GetArrayLength() > 0 && channels.GetArrayLength() == samplers.GetArrayLength(), "animation channels");
        // Checked first, read after: every channel's target and accessor, then the times, then the values straight into the clip's own arrays.
        var outputs = new int[joints];
        Array.Fill(outputs, -1);
        int hipOutput = -1, input = -1;
        for (var c = 0; c < channels.GetArrayLength(); c++)
        {
            var channel = Only(channels[c], "channel", "sampler", "target");
            var target = Only(Field(channel, "target"), "channel target", "node", "path");
            Need(Int(Field(channel, "sampler"), "channel") == c, "channel sampler");
            var sampler = Only(samplers[c], "sampler", "input", "output", "interpolation");
            Need(!sampler.TryGetProperty("interpolation", out var interpolation) || interpolation.ValueKind == JsonValueKind.String && interpolation.GetString() == "LINEAR", "sampler interpolation");
            var node = Int(Field(target, "node"), "channel node");
            var path = Field(target, "path");
            Need(node >= 0 && node < joints && path.ValueKind == JsonValueKind.String, "channel target");
            var rotation = path.GetString() == "rotation";
            Need(rotation || path.GetString() == "translation" && node == 0, "only the hip moves");
            Need(rotation ? outputs[node] < 0 : hipOutput < 0, "duplicate channel");
            var at = Int(Field(sampler, "input"), "sampler");
            Need(input < 0 || at == input, "channels share one time accessor");
            input = at;
            if (rotation) outputs[node] = Int(Field(sampler, "output"), "sampler");
            else hipOutput = Int(Field(sampler, "output"), "sampler");
        }
        Need(hipOutput >= 0, "the hip does not move");
        for (var j = 0; j < joints; j++) if (outputs[j] < 0) throw Fault("the clip does not turn " + ClipRig.Names[j]);

        // The times: from 0, rising evenly (the player plays at one fixed rate), bounded as the accessor says.
        var (timesAt, frames) = Accessor(input, 1, "keyframe times");
        Need(frames >= 2, "fewer than two keyframes");
        Need(Float(timesAt) == 0f, "keyframe times start at 0");
        var rate = Float(timesAt + 4);
        Need(rate > 0f, "keyframe times increase");
        for (var k = 2; k < frames; k++)
        {
            var time = Float(timesAt + k * 4);
            if (Math.Abs(time - k * rate) > 1e-4f * rate * k + 1e-6f) throw Fault("keyframes are not evenly spaced");
            if (!(time > Float(timesAt + (k - 1) * 4))) throw Fault("keyframe times increase");
        }
        var timeRow = accessors[input];
        Need(timeRow.TryGetProperty("min", out var min) && timeRow.TryGetProperty("max", out var max)
            && Numbers(min, 1, "time bounds")[0] == 0f && Numbers(max, 1, "time bounds")[0] == Float(timesAt + (frames - 1) * 4), "time bounds");

        var place = new Vector3[frames];
        var (hipAt, hipCount) = Accessor(hipOutput, 3, "hip place");
        Need(hipCount == frames, "hip place");
        for (var k = 0; k < frames; k++) place[k] = new Vector3(Float(hipAt + k * 12), Float(hipAt + k * 12 + 4), Float(hipAt + k * 12 + 8));
        var local = new Quaternion[frames * joints];
        for (var j = 0; j < joints; j++)
        {
            var (turnAt, count) = Accessor(outputs[j], 4, "joint turns");
            Need(count == frames, "joint turns");
            for (var k = 0; k < frames; k++)
            {
                var q = new Quaternion(Float(turnAt + k * 16), Float(turnAt + k * 16 + 4), Float(turnAt + k * 16 + 8), Float(turnAt + k * 16 + 12));
                if (Math.Abs(q.Length() - 1f) > 1e-3f) throw Fault("rotation is not a unit quaternion: " + ClipRig.Names[j]);
                local[k * joints + j] = Quaternion.Normalize(q);
            }
        }
        return new JointClip(rate, joints, local, place);
    }

    /// <summary>How far a node's rest may sit from <see cref="ClipRig"/>: the file and the rig are the same constants, in float32 and in seven digits.</summary>
    private const float RestTolerance = 1e-5f;

    private static uint U32(byte[] bytes, int at) => BitConverter.ToUInt32(bytes, at);

    private static FormatException Fault(string detail) => new("not a Forge animation file: " + detail);

    private static void Need(bool ok, string detail)
    {
        if (!ok) throw Fault(detail);
    }

    private static JsonElement Only(JsonElement value, string where, params string[] names)
    {
        Need(value.ValueKind == JsonValueKind.Object, where);
        foreach (var property in value.EnumerateObject()) if (Array.IndexOf(names, property.Name) < 0) throw Fault("unexpected field " + property.Name + " in " + where);
        return value;
    }

    private static JsonElement Field(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field)) throw Fault("no " + name);
        return field;
    }

    private static JsonElement Items(JsonElement value, string where)
    {
        Need(value.ValueKind == JsonValueKind.Array, where);
        return value;
    }

    private static int Int(JsonElement value, string where)
    {
        var number = 0;
        Need(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number), where);
        return number;
    }

    private static int Optional(JsonElement row, string name, string where) => row.TryGetProperty(name, out var value) ? Int(value, where) : 0;

    private static float[] Numbers(JsonElement value, int size, string where)
    {
        Need(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == size, where);
        var numbers = new float[size];
        for (var i = 0; i < size; i++)
        {
            Need(value[i].ValueKind == JsonValueKind.Number && value[i].TryGetSingle(out numbers[i]) && float.IsFinite(numbers[i]), where);
        }
        return numbers;
    }

    private static bool Near(float[] value, Vector3 to, float tolerance)
        => Math.Abs(value[0] - to.X) <= tolerance && Math.Abs(value[1] - to.Y) <= tolerance && Math.Abs(value[2] - to.Z) <= tolerance;
}
