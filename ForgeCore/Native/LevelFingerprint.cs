using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LevelGeneration;
using UnityEngine;

namespace ForgeCore;

/// <summary>What a generated level looks like, boiled down to hashes: <see cref="Whole"/> is one hash over every section, and
/// <see cref="Sections"/> hold one hash each (the game's own checksums, zones, areas, doors, terminals, containers, generators and items; sleeping enemies are not part of
/// it: the game spawns them a moment after the build is done and, measured on one level, their positions are not decided by the three seeds) so two levels that differ show which part differs. <see cref="Lines"/> are the lines behind the structural sections, for
/// finding the exact difference.</summary>
public sealed class GenerationFingerprint
{
    public string Whole { get; init; } = "";
    public IReadOnlyDictionary<string, string> Sections { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Lines { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public ulong NativeChecksum { get; init; }
    public uint ExpeditionChecksum { get; init; }
}

public static partial class Level
{
    /// <summary>Fingerprints the level the game has just built. Call it after the build is done (<c>LevelAPI.OnBuildDone</c> or later);
    /// null when there is no built floor. Positions are rounded to centimetres and every list is sorted, so the result does not depend on the
    /// order the game happened to create things in.</summary>
    public static GenerationFingerprint? Fingerprint()
    {
        GenerationFingerprint? result = null;
        foreach (var _ in FingerprintSteps(found => result = found)) { }
        return result;
    }

    /// <summary><see cref="Fingerprint"/> in steps for a caller that spreads the work over frames: each step is one native scan of the level (about 80 ms on a large level),
    /// and the name it yields is the scan just done. Run the enumeration to its end, one <c>MoveNext</c> per frame; <paramref name="done"/> then gets the result (null when there is no built floor).</summary>
    public static IEnumerable<string> FingerprintSteps(Action<GenerationFingerprint?> done)
    {
        var floor = Builder.CurrentFloor;
        if (floor == null) { done(null); yield break; }
        var lines = new Dictionary<string, IReadOnlyList<string>>();
        var zones = new List<string>();
        var areas = new List<string>();
        foreach (var zone in floor.allZones)
        {
            if (zone == null) continue;
            var areaCount = zone.m_areas?.Count ?? 0;
            zones.Add(string.Join("|", zone.ID, (int)zone.DimensionIndex, (int)zone.LocalIndex, zone.AliasName, areaCount, Cm(zone.CenterPosition)));
            for (var a = 0; a < areaCount; a++)
            {
                var area = zone.m_areas![a];
                if (area == null) continue;
                areas.Add(string.Join("|", zone.ID, a, area.m_geoArea, area.m_geomorph != null ? area.m_geomorph.name : "", (int)area.m_size,
                    area.m_gates?.Count ?? 0, area.m_enemySpawnPoints?.Count ?? 0, Cm(area.Position)));
            }
        }
        lines["zones"] = Sorted(zones);
        lines["areas"] = Sorted(areas);
        yield return "zones";
        var security = Describe<LG_SecurityDoor>("security").ToList();
        yield return "security-doors";
        var weak = Describe<LG_WeakDoor>("weak").ToList();
        yield return "weak-doors";
        lines["doors"] = Sorted(security.Concat(weak));
        lines["terminals"] = Sorted(Describe<LG_ComputerTerminal>("terminal").ToList());
        yield return "terminals";
        lines["containers"] = Sorted(Describe<LG_ResourceContainer_Storage>("container").ToList());
        yield return "containers";
        lines["generators"] = Sorted(Describe<LG_PowerGenerator_Core>("generator").ToList());
        yield return "generators";
        lines["items"] = Sorted(Describe<ItemInLevel>("item").ToList());
        yield return "items";

        var native = Builder.CombinedCheckSum;
        var nativeChecksum = native != null ? native.Checksum : 0UL;
        var expeditionChecksum = 0U;
        try { expeditionChecksum = Builder.LevelGenExpedition?.GetChecksum() ?? 0U; } catch (Exception error) { Plugin.Logger.ErrorOnce("core-reading-expedition-checksum-failed-counts-as", "Reading the expedition checksum failed; it counts as zero in the level fingerprint: " + error); }

        var sections = new Dictionary<string, string>
        {
            ["native"] = new ForgeCore.Fingerprint().Add("combined", nativeChecksum).Add("expedition", expeditionChecksum).Hex(),
        };
        foreach (var (name, section) in lines)
        {
            var hash = new ForgeCore.Fingerprint();
            foreach (var line in section) hash.Add(name, line);
            sections[name] = hash.Hex();
        }
        var whole = new ForgeCore.Fingerprint();
        foreach (var name in sections.Keys.OrderBy(k => k, StringComparer.Ordinal)) whole.Add(name, sections[name]);
        done(new GenerationFingerprint
        {
            Whole = whole.Hex(),
            Sections = sections,
            Lines = lines,
            NativeChecksum = nativeChecksum,
            ExpeditionChecksum = expeditionChecksum,
        });
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static string Cm(Vector3 v) =>
        Math.Round(v.x * 100f).ToString(CultureInfo.InvariantCulture) + "," + Math.Round(v.y * 100f).ToString(CultureInfo.InvariantCulture)
        + "," + Math.Round(v.z * 100f).ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<string> Describe<T>(string label) where T : Component
    {
        var found = UnityEngine.Object.FindObjectsOfType<T>();
        for (var i = 0; i < found.Length; i++)
        {
            var item = found[i];
            if (item == null) continue;
            yield return label + "|" + item.gameObject.name + "|" + Cm(item.transform.position);
        }
    }
}
