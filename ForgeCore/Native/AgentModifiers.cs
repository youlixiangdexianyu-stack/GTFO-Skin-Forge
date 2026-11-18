using System;
using Agents;

namespace ForgeCore;

/// <summary>The game's modifier table of one agent, kept by a caller that must later check removal against exactly the table the
/// modification was added to, even if the agent's current table has since been replaced.</summary>
public sealed class ModifierTable
{
    private readonly ModifierInstance _table;
    internal ModifierTable(ModifierInstance table) { _table = table; Pointer = table.Pointer; }

    public IntPtr Pointer { get; }

    /// <summary>True while this is still the same live table.</summary>
    public bool IsLive => _table.Pointer == Pointer;

    /// <summary>Whether the table still holds modification <paramref name="id"/>. Throws when the table is gone or unreadable,
    /// because then nothing can be said about the id.</summary>
    public bool Holds(uint id)
    {
        if (!IsLive) throw new InvalidOperationException("The game's modifier table was replaced.");
        var values = _table.m_idToModificationInstanceValue ?? throw new InvalidOperationException("The game's modifier table is unavailable.");
        return values.ContainsKey(id);
    }
}

/// <summary>What the game's modifier table holds for one synced modification: the value as submitted, the value the game cached
/// after its own limits and the time stamp.</summary>
public readonly record struct ModifierReading(float Original, float Cached, float TimeStamp);

/// <summary>Agent attribute modifiers through the game's own entries, <c>AgentModifierManager.AddSyncedModifierValue</c> and
/// <c>ClearSyncedModifierChange</c> (the local <c>AddModifierValue</c> and <c>ClearModifierChange</c> while the synced packet
/// does not exist), for players and enemies alike. A modification is a contribution to the agent's aggregate, so
/// it is read back after it is added and cleared by its id; this class only calls and reads, the caller decides amounts, caps and
/// when to clear. Main thread only.</summary>
public static class AgentModifiers
{
    /// <summary>Whether the game's modifier manager exists. Without it neither entry can work.</summary>
    public static bool Ready => AgentModifierManager.Current != null;

    /// <summary>The synced entry sends through a network packet the manager creates in its own setup; the game raises a null
    /// reference when that packet is missing, which is what every call of the synced entry did in the hand-test logs. While the
    /// packet is missing the game's local entry, the one its own boosters use on every machine, carries the modification instead.</summary>
    private static bool Synced => AgentModifierManager.Current?.m_modificationPacket != null;

    /// <summary>Adds <paramref name="amount"/> to <paramref name="modifier"/> of <paramref name="agent"/> with no decay and returns
    /// the modification's id, or 0 when the game registered nothing (its ids ran out).</summary>
    public static uint Add(Agent agent, AgentModifier modifier, float amount)
    {
        if (agent == null) throw new ArgumentNullException(nameof(agent));
        var synced = Synced;
        var id = synced
            ? AgentModifierManager.AddSyncedModifierValue(agent, modifier, amount, 0f)
            : AgentModifierManager.AddModifierValue(agent, modifier, amount, 0f);
        // This read came with a diagnostic line that is gone; whether the game needs it is not confirmed in game yet.
        AgentModifierManager.ApplyModifier(agent, modifier, 1f);
        return id;
    }

    /// <summary>The agent's modifier table, or null when it has none yet.</summary>
    public static ModifierTable? TableOf(Agent agent)
        => AgentModifierManager.TryGetModifierInstance(agent, out var table) && table != null && table.Pointer != IntPtr.Zero ? new ModifierTable(table) : null;

    /// <summary>The game's record of modification <paramref name="id"/>: false when the agent has no table or the table holds no such
    /// id. Throws when the entry is not what <see cref="Add"/> made (another modifier, or a decay), because then the id no longer
    /// names our contribution.</summary>
    public static bool TryRead(Agent agent, uint id, AgentModifier modifier, out ModifierReading reading)
    {
        reading = default;
        if (!AgentModifierManager.TryGetModifierInstance(agent, out var table) || table == null || table.Pointer == IntPtr.Zero)
            return false;
        var values = table.m_idToModificationInstanceValue ?? throw new InvalidOperationException("The game's modifier table is unavailable.");
        // Not TryGetValue: the interop builds its `out` value with a pointer constructor this struct does not have
        // (MissingMethodException); the indexer copies the struct out of the native slot.
        if (!values.ContainsKey(id)) return false;
        var value = values[id];
        if (value.Modifier != modifier || value.DeltaPerSec != 0f)
            throw new InvalidOperationException("The game's modifier entry " + id + " is not the contribution that was added.");
        reading = new ModifierReading(value.OriginalValue, value.CachedValue, value.TimeStamp);
        return true;
    }

    /// <summary>The sum of what the agent's table holds for <paramref name="modifier"/> among the modifications that do not decay (the
    /// values the game cached after its own limits): the table's own total for one member, whoever added each entry. False when the
    /// agent has no table yet, which a reader takes as zero. Throws when the table exists but cannot be read.</summary>
    public static bool TryTotal(Agent agent, AgentModifier modifier, out float total)
    {
        total = 0f;
        if (!AgentModifierManager.TryGetModifierInstance(agent, out var table) || table == null || table.Pointer == IntPtr.Zero)
            return false;
        var values = table.m_idToModificationInstanceValue ?? throw new InvalidOperationException("The game's modifier table is unavailable.");
        foreach (var pair in values)
            if (pair.Value is { } value && value.Modifier == modifier && value.DeltaPerSec == 0f) total += value.CachedValue;
        return true;
    }

    /// <summary>Clears modification <paramref name="id"/> through the entry that added it: the synced one (peers clear it too) or
    /// the local one.</summary>
    public static void Clear(uint id)
    {
        if (Synced) AgentModifierManager.ClearSyncedModifierChange(id);
        else AgentModifierManager.ClearModifierChange(id);
    }
}
