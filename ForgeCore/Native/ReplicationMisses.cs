using System.Collections.Generic;
using System.Text;
using GTFO.API;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SNetwork;

namespace ForgeCore;

/// <summary>What the game's "Bad packet sent by player" line does not say: which replicator key the packet named. The game writes that line
/// when the receiver's slot for the key is empty. Off by default (<c>NAinfini.ForgeCore.cfg</c>, [Diagnostics] ReplicationMisses); off, nothing is
/// patched. On, each key's first miss is one line and the level's cleanup writes the counts, the keys assigned in play and a fingerprint at build
/// done: two machines with different fingerprints built their slots differently. GTFO-API's own prefix ends a NetworkAPI message before this one.</summary>
internal static class ReplicationMisses
{
    private const int MaximumAssignmentsWritten = 512;
    private static readonly Dictionary<ushort, int> Misses = new();
    private static readonly Dictionary<ushort, int> Assigned = new();
    private static readonly List<string> InPlay = new();
    private static System.Action<string> _write = _ => { };

    internal static void Start(System.Action<string> write)
    {
        if (!Settings.Bind(Plugin.Id, "Diagnostics", "ReplicationMisses", false,
                "Writes which replicator key each of the game's Bad packet lines named. For a multiplayer investigation; leave off otherwise.").Value) return;
        _write = write;
        Patches.Ensure(Modules.ReplicationMisses, typeof(ReplicationMissHook));
        LevelAPI.OnBuildDone += WriteFingerprint;
        LevelAPI.OnLevelCleanup += Flush;
    }

    private static string Range(ushort key)
        => key >= SNet_Replication.DYNAMIC_REPLICATOR_INDEX_START ? "dynamic" : key >= SNet_Replication.SELFMANAGED_REPLICATOR_INDEX_START ? "self-managed" : "manager";

    internal static void Received(Il2CppStructArray<byte> bytes, uint size, ulong sender)
    {
        if (size < 3 || bytes.Length < 3) return;
        var key = (ushort)(bytes[0] | bytes[1] << 8);
        var slots = SNet_Replication.s_replicatorSlots;
        if (slots == null || key < slots.Length && slots[key] != null) return;
        Misses.TryGetValue(key, out var count);
        Misses[key] = count + 1;
        if (count != 0) return;
        Assigned.TryGetValue(key, out var assigned);
        _write("replication.miss key=" + key + " range=" + Range(key) + " packet=" + bytes[2] + " size=" + size + " from-master=" + (SNet.Master?.Lookup == sender)
            + " assigned-here=" + assigned + " state=" + GameStateManager.CurrentStateName);
    }

    internal static void KeyAssigned(IReplicator replicator, ushort key, bool isRecall)
    {
        Assigned.TryGetValue(key, out var count);
        Assigned[key] = count + 1;
        if (GameStateManager.CurrentStateName == eGameStateName.InLevel && InPlay.Count < MaximumAssignmentsWritten)
            InPlay.Add(key + ":" + replicator.Type + ":" + replicator.name + (isRecall ? ":recall" : ""));
    }

    private static void WriteFingerprint()
    {
        var slots = SNet_Replication.s_replicatorSlots;
        if (slots == null) return;
        var hash = Fnv.Offset32;
        var used = 0;
        for (var key = 0; key < slots.Length; key++)
        {
            if (slots[key] is not { } slot) continue;
            used++;
            hash = Fnv.MixUtf8(Fnv.Mix32(hash, key), slot.Type + ":" + slot.name);
        }
        _write("replication.build-fingerprint slots=" + used + " hash=" + hash.ToString("x8") + " next-manager=" + SNet_Replication.s_highestSlotUsed_Manager
            + " next-self-managed=" + SNet_Replication.s_highestSlotUsed_SelfManaged + " next-dynamic=" + SNet_Replication.s_highestSlotUsed_Dynamic);
    }

    private static void Flush()
    {
        var line = new StringBuilder("replication.level-summary misses=");
        foreach (var (key, count) in Misses) line.Append(key).Append('x').Append(count).Append(',');
        line.Append(" assigned-in-play=").Append(string.Join(",", InPlay));
        _write(line.ToString());
        Misses.Clear();
        Assigned.Clear();
        InPlay.Clear();
    }
}

internal static class ReplicationMissHook
{
    [HarmonyPatch(typeof(SNet_Replication), nameof(SNet_Replication.RecieveBytes))]
    [HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void Received(Il2CppStructArray<byte> bytes, uint size, ulong messagerID) => ReplicationMisses.Received(bytes, size, messagerID);

    [HarmonyPatch(typeof(SNet_Replication), nameof(SNet_Replication.AssignReplicatorKey))]
    [HarmonyPostfix]
    private static void Assigned(IReplicator replicator, ushort key, bool isRecall) => ReplicationMisses.KeyAssigned(replicator, key, isRecall);
}
