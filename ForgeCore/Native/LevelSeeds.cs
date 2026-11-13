using System;
using System.Collections.Generic;
using System.Globalization;
using GameData;
using LevelGeneration;

namespace ForgeCore;

public static partial class Level
{
    /// <summary>The level now being generated or played: its identity and the three seeds generation drew on. Once generation has begun the
    /// seeds come from the game's own builder (what was actually used, whatever the expedition data said); before that, from the active
    /// expedition. False when no expedition is active or the game cannot say.</summary>
    public static bool TryCurrentLevel(out LevelId id, out LevelSeeds seeds)
    {
        id = default; seeds = default;
        try
        {
            if (!TryActiveExpedition(out var rundown, out var tier, out var index)) return false;
            var expedition = RundownManager.ActiveExpedition;
            if (expedition == null) return false;
            var host = Builder.HostIDSeedRandom;
            var session = Builder.SessionSeedRandom;
            if (host != null && session != null) seeds = new LevelSeeds(unchecked((int)Builder.BuildSeed), host.Seed, session.Seed);
            else
            {
                if (expedition.Seeds == null) return false;
                expedition.GetSeeds(out var hostSeed, out var sessionSeed);
                seeds = new LevelSeeds(expedition.Seeds.BuildSeed, hostSeed, sessionSeed);
            }
            id = new LevelId(rundown, tier, index);
            return id.IsValid;
        }
        catch (Exception) { id = default; seeds = default; return false; }
    }

    /// <summary>The enabled expeditions of the active rundowns that have a level layout, each with its identity and the official label the
    /// game shows (<c>R1A1</c>) when its name follows that pattern; the label is empty otherwise. Empty when no rundown is loaded.</summary>
    public static List<(LevelId Id, string Label)> Expeditions()
    {
        var result = new List<(LevelId, string)>();
        var active = Globals.Global.ActiveRundownIds;
        if (active == null) return result;
        foreach (var rundownId in active)
        {
            var rundown = RundownDataBlock.GetBlock(rundownId);
            if (rundown == null) continue;
            var tiers = new[] { rundown.TierA, rundown.TierB, rundown.TierC, rundown.TierD, rundown.TierE };
            for (var t = 0; t < tiers.Length; t++)
            {
                var entries = tiers[t];
                if (entries == null) continue;
                for (var i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e == null || !e.Enabled || e.LevelLayoutData == 0) continue;
                    var prefix = e.Descriptive?.Prefix ?? "";
                    var label = prefix + (e.Descriptive != null && e.Descriptive.SkipExpNumberInName ? "" : (i + 1).ToString(CultureInfo.InvariantCulture));
                    var official = label.Length >= 4 && label[0] == 'R' && char.IsDigit(label[1]) && label.Contains((char)('A' + t)) ? label : "";
                    result.Add((new LevelId(rundownId, t + 1, i), official));
                }
            }
        }
        return result;
    }

    /// <summary>Finds an expedition by its official label (<c>R1A1</c>, any letter case). False when no active rundown has it.</summary>
    public static bool TryFindLevel(string label, out LevelId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(label)) return false;
        foreach (var (candidate, name) in Expeditions())
            if (string.Equals(name, label.Trim(), StringComparison.OrdinalIgnoreCase)) { id = candidate; return true; }
        return false;
    }
}
