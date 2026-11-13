using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// A mod's effect slots: one bundle of particle prefabs and one JSON file that says which prefab plays for which named moment ("pod.land",
/// "energy.beam.muzzle"). The mod resolves a slot once at load (<see cref="Find"/>), keeps the handle and plays it with <see cref="Fx"/> (<c>Play</c>,
/// <c>Hold</c>, <c>Line</c>); nothing looks a slot up by name while playing. A slot the file does not list, a prefab that cannot be registered and an install without the bundle log an error and draw nothing: no moment falls back to
/// another effect. The mechanism is generic: which prefab (and its licence, size and look) belongs to the mod that ships the bundle.
/// <code>
/// { "Slots": { "pod.land": { "Item": "Super_Magic_FX_1700_Uniq_b2172d21", "Size": 4, "Line": false, "Hold": 0, "Scale": 1 }, "pod.gravel": { "None": true } } }
/// </code>
/// <c>Item</c> is a prefab of the bundle; <c>Size</c> is the size (m) it is authored at (see <see cref="FxOptions.DefaultSize"/>, omit for an effect
/// that takes no size; for a line effect the length in metres it is authored along +Z); <c>Line</c> and <c>Hold</c> are <see cref="FxOptions.Line"/>
/// and <see cref="FxOptions.HoldSeconds"/>; <c>Scale</c> is <see cref="FxOptions.BaseScale"/> (a line effect's width); <c>Seconds</c>, <c>AxisUp</c>, <c>Spacing</c>, <c>Copies</c>
/// are <see cref="FxOptions.Seconds"/>, <see cref="FxOptions.AxisUp"/>, <see cref="FxOptions.Spacing"/> and <see cref="FxOptions.Copies"/>;
/// <c>LaserHead</c>, <c>LaserBodies</c> and <c>LaserTile</c> are <see cref="FxOptions.LaserHead"/>, <see cref="FxOptions.LaserBodies"/> and <see cref="FxOptions.LaserTile"/>: a laser line of any length, thin by its <c>Scale</c>; <c>EndFade</c> is <see cref="FxOptions.EndFade"/>; <c>Linger</c> is <see cref="FxOptions.Linger"/>;
/// <c>Light</c> (<c>{ "Colour": [r, g, b], "Range", "Peak", "Life", "Delay", "Flicker" }</c>) is <see cref="FxOptions.Light"/> and <see cref="FxOptions.LightDelay"/>;
/// <c>Centre</c> (<c>[x, y, z]</c>) is <see cref="FxOptions.Centre"/>; <c>Body</c> (<c>[min x, y, z, max x, y, z]</c>) is <see cref="FxOptions.Body"/>; <c>AlwaysDraw</c> and <c>CullDistance</c> are <see cref="FxOptions.AlwaysDraw"/> and <see cref="FxOptions.CullDistance"/> (<see cref="FxCulling"/>).
/// <c>{ "None": true }</c> makes the moment draw nothing at all (see <see cref="IsNone"/>); a slot with a <c>Light</c> and no <c>Item</c> is a moment that is only a light (the light flashes when it plays, so its <c>Life</c> is above 0).
/// </summary>
public sealed class FxSlots
{
    private readonly Dictionary<string, FxHandle> _slots;
    private readonly HashSet<string> _none;
    private readonly string _modId;
    /// <summary>Slots already reported: a held moment asks every frame, and building the message each time would allocate.</summary>
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private FxSlots(Dictionary<string, FxHandle> slots, HashSet<string> none, string modId) { _slots = slots; _none = none; _modId = modId; }

    /// <summary>A set that lists no slot, before <see cref="Load"/> or when the bundle or the file is not shipped or is damaged. Every moment logs its error and draws nothing.</summary>
    public static readonly FxSlots Empty = new(new Dictionary<string, FxHandle>(), new HashSet<string>(), "");

    /// <summary>The effect for <paramref name="slot"/>: its handle, or <see cref="FxHandle.IsNothing"/>'s empty handle when the file lists the slot as
    /// <c>{ "None": true }</c> (playing it draws nothing), and that same empty handle, logged once, when this set does not list the slot (the moment
    /// draws nothing), so what it returns can always be played.</summary>
    public FxHandle Find(string slot)
    {
        if (slot == null) return FxHandle.Nothing;
        if (_slots.TryGetValue(slot, out var handle)) return handle;
        if (_none.Contains(slot)) return FxHandle.Nothing;
        if (_reported.Add(slot)) ForgeCore.Log.For(_modId.Length == 0 ? Plugin.Id : _modId).Error("visual-slot-missing", "effect slot " + slot + " is not in the loaded slot table: the moment draws nothing.");
        return FxHandle.Nothing;
    }

    /// <summary>True when the file lists <paramref name="slot"/> as <c>{ "None": true }</c>: the moment draws nothing at all.</summary>
    public bool IsNone(string slot) => slot != null && _none.Contains(slot);

    /// <summary>The prefab of <paramref name="slot"/> as a model, for a mod that plays its effects from ForgeVisual's model pools
    /// (<c>FxInstancePool</c>) instead of <see cref="Fx"/>; null when the slot is empty or lists no prefab (a pool cannot draw nothing: check
    /// <see cref="IsNone"/> first).</summary>
    public ModelHandle? Model(string slot) => slot != null && _slots.TryGetValue(slot, out var handle) ? handle.Prefab?.Model : null;

    /// <summary>Slots this set fills (the explicitly empty ones are <see cref="IsNone"/>).</summary>
    public IReadOnlyCollection<string> Names => _slots.Keys;

    /// <summary>Registers the bundle at <paramref name="bundlePath"/> as <paramref name="pack"/> and every prefab the slot file at
    /// <paramref name="slotsPath"/> uses. <see cref="Empty"/>, logged once under <paramref name="modId"/>, when the bundle or the file is not shipped or is damaged; a slot whose prefab
    /// cannot be registered is left out and logged, the others still load. Relative paths are taken from the calling mod's DLL folder.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static FxSlots Load(string modId, string pack, string bundlePath, string slotsPath)
    {
        var log = ForgeCore.Log.For(modId);
        var folder = Path.GetDirectoryName(System.Reflection.Assembly.GetCallingAssembly().Location) ?? "";
        var bundleFile = Path.IsPathRooted(bundlePath) ? bundlePath : Path.Combine(folder, bundlePath);
        var slotsFile = Path.IsPathRooted(slotsPath) ? slotsPath : Path.Combine(folder, slotsPath);
        if (!File.Exists(bundleFile) || !File.Exists(slotsFile))
        {
            // The slot table is in Git and the media is not: a clean clone or a package built without the media has one and not the other.
            log.ErrorOnce("visual-slots-not-installed", "effect slots of " + modId + " are not installed (" + bundleFile + ", " + slotsFile + "): its library effects draw nothing (effect media stays out of Git and has to be put there).");
            return Empty;
        }
        Dictionary<string, FxHandle> slots;
        HashSet<string> none;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(slotsFile));
            var bundle = Bundles.Register(modId, pack, bundleFile);
            if (bundle == null) return Empty;
            (slots, none) = Read(bundle, doc.RootElement, log);
        }
        catch (Exception error) when (error is JsonException or ForgeCore.StrictJsonException or IOException)
        {
            log.ErrorOnce("visual-slots-refused", "ForgeVisual effect slots " + slotsFile + " were refused: " + error.Message);
            return Empty;
        }
        return new FxSlots(slots, none, modId);
    }

    private static string[] Strings(JsonElement array)
    {
        var names = new string[array.GetArrayLength()];
        var i = 0;
        foreach (var name in array.EnumerateArray()) names[i++] = name.GetString()!;
        return names;
    }

    /// <summary><c>{ "Colour": [r, g, b], "Range": m, "Peak": intensity, "Life": s, "Delay": s, "Flicker": 0..1 }</c>: the slot's light (<see cref="FxOptions.Light"/>). A one-shot slot's light fades over its life, so it needs one above 0.</summary>
    private static (PointLightSpec, float) Light(JsonElement light, string prefix, bool held)
    {
        ForgeCore.StrictJson.OnlyKnown(light, prefix, new[] { "Colour", "Range", "Peak", "Life", "Delay", "Flicker" });
        if (!light.TryGetProperty("Colour", out var colour) || colour.ValueKind != JsonValueKind.Array || colour.GetArrayLength() != 3)
            throw new ForgeCore.StrictJsonException(prefix + "Colour", "is required and must be [r, g, b].");
        var rgb = new float[3];
        var i = 0;
        foreach (var c in colour.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Number || c.GetDouble() < 0 || c.GetDouble() > 8) throw new ForgeCore.StrictJsonException(prefix + "Colour", "channels are numbers from 0 to 8.");
            rgb[i++] = (float)c.GetDouble();
        }
        var life = (float)ForgeCore.StrictJson.Number(light, prefix, "Life", 0, 0, 10);
        if (!held && life <= 0f) throw new ForgeCore.StrictJsonException(prefix + "Life", "is required above 0 for the light of a slot played once.");
        var spec = new PointLightSpec(new Color(rgb[0], rgb[1], rgb[2]),
            (float)ForgeCore.StrictJson.Number(light, prefix, "Range", null, 0.1, 50),
            (float)ForgeCore.StrictJson.Number(light, prefix, "Peak", null, 0.01, 100),
            life,
            (float)ForgeCore.StrictJson.Number(light, prefix, "Flicker", 0.1, 0, 1));
        return (spec, (float)ForgeCore.StrictJson.Number(light, prefix, "Delay", 0, 0, 10));
    }

    /// <summary><c>[min x, y, z, max x, y, z]</c>: the slot's measured body box (<see cref="FxOptions.Body"/>).</summary>
    private static Bounds Body(JsonElement box, string path)
    {
        if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 6) throw new ForgeCore.StrictJsonException(path, "must be [min x, y, z, max x, y, z].");
        var v = new float[6];
        var i = 0;
        foreach (var c in box.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Number || Math.Abs(c.GetDouble()) > 1000) throw new ForgeCore.StrictJsonException(path, "holds numbers from -1000 to 1000.");
            v[i++] = (float)c.GetDouble();
        }
        if (v[0] > v[3] || v[1] > v[4] || v[2] > v[5]) throw new ForgeCore.StrictJsonException(path, "has a minimum above its maximum.");
        var min = new Vector3(v[0], v[1], v[2]);
        var max = new Vector3(v[3], v[4], v[5]);
        return new Bounds((min + max) * 0.5f, max - min);
    }

    /// <summary><c>[x, y, z]</c>: where the slot's prefab draws its middle (<see cref="FxOptions.Centre"/>).</summary>
    private static Vector3 Centre(JsonElement point, string path)
    {
        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 3) throw new ForgeCore.StrictJsonException(path, "must be [x, y, z].");
        var v = new float[3];
        var i = 0;
        foreach (var c in point.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Number || Math.Abs(c.GetDouble()) > 1000) throw new ForgeCore.StrictJsonException(path, "holds numbers from -1000 to 1000.");
            v[i++] = (float)c.GetDouble();
        }
        return new Vector3(v[0], v[1], v[2]);
    }

    internal static (Dictionary<string, FxHandle> Slots, HashSet<string> None) Read(BundleHandle bundle, JsonElement root, ForgeCore.ModLog log)
    {
        ForgeCore.StrictJson.OnlyKnown(root, "", new[] { "Slots" });
        var table = ForgeCore.StrictJson.Object(root, "", "Slots");
        var byItem = new Dictionary<string, FxHandle?>(StringComparer.Ordinal);
        var slots = new Dictionary<string, FxHandle>(StringComparer.Ordinal);
        var none = new HashSet<string>(StringComparer.Ordinal);
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in table.EnumerateObject())
        {
            var prefix = "Slots." + member.Name + ".";
            ForgeCore.StrictJson.OnlyKnown(member.Value, prefix, new[] { "Item", "Size", "Line", "Hold", "Scale", "Seconds", "AxisUp", "Spacing", "Copies", "LaserHead", "LaserBodies", "LaserTile", "EndFade", "Linger", "Light", "Centre", "Body", "AlwaysDraw", "CullDistance", "None" });
            if (ForgeCore.StrictJson.Bool(member.Value, prefix, "None", false)) { none.Add(member.Name); continue; }
            if (!member.Value.TryGetProperty("Item", out _) && member.Value.TryGetProperty("Light", out var glowLight))
            {
                // A moment that is only a light: no prefab; playing the handle flashes the light. It is registered by name only so a replay of a recording that names it finds it.
                var (glowSpec, glowDelay) = Light(glowLight, prefix + "Light.", false);
                var glow = new FxHandle(VisualNames.Join(bundle.Pack, member.Name), glowSpec, glowDelay);
                slots[member.Name] = glow;
                Fx.RegisterLight(glow, bundle.ModId);
                continue;
            }
            if (!member.Value.TryGetProperty("Item", out var itemValue) || itemValue.ValueKind != JsonValueKind.String)
                throw new ForgeCore.StrictJsonException(prefix + "Item", "is required and must be a prefab name.");
            var item = itemValue.GetString()!;
            var options = new FxOptions
            {
                DefaultSize = member.Value.TryGetProperty("Size", out _) ? (float)ForgeCore.StrictJson.Number(member.Value, prefix, "Size", null, 0.01, 1000) : float.NaN,
                Line = ForgeCore.StrictJson.Bool(member.Value, prefix, "Line", false),
                HoldSeconds = (float)ForgeCore.StrictJson.Number(member.Value, prefix, "Hold", 0, 0, 3600),
                BaseScale = (float)ForgeCore.StrictJson.Number(member.Value, prefix, "Scale", 1, 0.0001, 100),
                Seconds = (float)ForgeCore.StrictJson.Number(member.Value, prefix, "Seconds", 0, 0, 3600),
                AxisUp = ForgeCore.StrictJson.Bool(member.Value, prefix, "AxisUp", false),
                Spacing = (float)ForgeCore.StrictJson.Number(member.Value, prefix, "Spacing", 0, 0, 100),
                Copies = (int)ForgeCore.StrictJson.Number(member.Value, prefix, "Copies", 16, 1, 256),
                LaserHead = member.Value.TryGetProperty("LaserHead", out var laserHead) ? laserHead.GetString() : null,
                LaserBodies = member.Value.TryGetProperty("LaserBodies", out var laserBodies) ? Strings(laserBodies) : null,
                LaserTile = (float)ForgeCore.StrictJson.Number(member.Value, prefix, "LaserTile", 0, 0, 100),
                EndFade = (float)ForgeCore.StrictJson.Number(member.Value, prefix, "EndFade", 0, 0, 100),
                Linger = (float)ForgeCore.StrictJson.Number(member.Value, prefix, "Linger", 8, 0, 100),
                AlwaysDraw = member.Value.TryGetProperty("AlwaysDraw", out _) ? ForgeCore.StrictJson.Bool(member.Value, prefix, "AlwaysDraw", false) : null,
                CullDistance = (float)ForgeCore.StrictJson.Number(member.Value, prefix, "CullDistance", 0, 0, 5000),
            };
            if (member.Value.TryGetProperty("Light", out var light)) (options.Light, options.LightDelay) = Light(light, prefix + "Light.", options.HoldSeconds > 0f);
            if (member.Value.TryGetProperty("Centre", out var centre)) options.Centre = Centre(centre, prefix + "Centre");
            if (member.Value.TryGetProperty("Body", out var body)) options.Body = Body(body, prefix + "Body");
            // One handle per prefab and way of playing it: two slots may share a prefab with different sizes or one held and one played once.
            var key = member.Value.GetRawText();
            if (!byItem.TryGetValue(key, out var handle))
            {
                handle = Fx.Register(bundle, item, registered.Add(item) ? item : item + "." + byItem.Count, options);
                byItem[key] = handle;
            }
            if (handle == null) { log.ErrorOnce("visual-slot-effect-unregistered", "ForgeVisual slot " + member.Name + " has no effect: prefab " + item + " could not be registered."); continue; }
            slots[member.Name] = handle;
        }
        return (slots, none);
    }
}
