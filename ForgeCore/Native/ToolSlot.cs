using System;
using System.Collections.Generic;
using Player;

namespace ForgeCore;

public static partial class Gear
{
    private static readonly List<Func<string?, bool>> ToolSlotMarks = new();
    private static readonly HashSet<IntPtr> ToolSlotSentries = new();
    private static int _toolSlotDepth;

    /// <summary>Keeps the sentries a mod stands itself off the owner's class tool slot. The game's sentry code treats every sentry
    /// instance as the one its owner carries: its spawn, pick-up, despawn and cleanup mark the owner's class slot deployed or free
    /// again, so the owner's HUD shows "Deploy" and the carried tool cannot be used. A sentry spawned with a public gear name
    /// <paramref name="carriesMark"/> accepts (the name is replicated to every peer with the spawn, so every peer recognises it) has
    /// every step of its life run with that write refused, and the slot is read before and after each step; a slot that changed
    /// anyway is logged as an error and put back. Register once, on every peer, before the first such sentry spawns; several marks
    /// may be registered. Main thread only.</summary>
    public static void KeepToolSlot(Func<string?, bool> carriesMark)
    {
        if (carriesMark == null) throw new ArgumentNullException(nameof(carriesMark));
        if (ToolSlotMarks.Count == 0)
        {
            GameHooks.BackpackDeployed.Before(call =>
            {
                if (_toolSlotDepth > 0 && call.Slot == InventorySlot.GearClass) call.Skip();
            });
            GameHooks.SentrySpawned.Around(StartToolSlotSpawn, (_, watch, _) => EndToolSlotWatch(watch), (_, watch, _) => EndToolSlotWatch(watch));
            GameHooks.SentryLifecycle.Around(StartToolSlotStep, (call, watch, _) => EndToolSlotStep(call, watch),
                (_, watch, _) => EndToolSlotWatch(watch));
        }
        ToolSlotMarks.Add(carriesMark);
    }

    /// <summary>True for a sentry instance (by its native pointer) a mod stood with a name a <see cref="KeepToolSlot"/> mark accepts, from its spawn until its destroy.
    /// Such a sentry is not the one its owner carries, so code that attributes world sentries to a carried tool must skip it. Main thread only.</summary>
    public static bool IsToolSlotKept(IntPtr sentry) => ToolSlotSentries.Contains(sentry);

    /// <summary>Level cleanup: the level's sentries are gone whether or not their destroy step ran, so no pointer is kept for the next
    /// level, whose objects may reuse the same native addresses.</summary>
    internal static void ForgetToolSlotSentries() => ToolSlotSentries.Clear();

    private sealed class ToolSlotWatch
    {
        internal PlayerBackpack? Backpack;
        internal string Owner = "", Step = "";
        internal string Item = "";
        internal bool Deployed;
        internal int Status;
    }

    private static ToolSlotWatch? StartToolSlotSpawn((SentryGunInstance Sentry, pGearSpawnData Data) call)
    {
        try
        {
            var name = call.Data.gearIDRange.publicName.data;
            var marked = false;
            foreach (var mark in ToolSlotMarks) if (mark(name)) { marked = true; break; }
            if (!marked) return null;
            ToolSlotSentries.Add(call.Sentry.Pointer);
            call.Data.owner.TryGetPlayer(out var player);
            return BeginToolSlotWatch(player != null && PlayerBackpackManager.TryGetBackpack(player, out var backpack) ? backpack : null,
                player?.NickName ?? "(unknown)", "spawn");
        }
        catch (Exception error) { Plugin.Logger.Error("core-could-not-guard-tool-slot-spawning", "Could not guard the tool slot for a spawning sentry: " + error); return null; }
    }

    private static ToolSlotWatch? StartToolSlotStep(SentryCall call)
    {
        try
        {
            if (!ToolSlotSentries.Contains(call.Sentry.Pointer)) return null;
            var backpack = call.Sentry.m_belongsToBackpack;
            return BeginToolSlotWatch(backpack, backpack?.Owner?.NickName ?? "(unknown)", call.Step.ToString());
        }
        catch (Exception error) { Plugin.Logger.Error("core-could-not-guard-tool-slot-sentry", "Could not guard the tool slot for a sentry: " + error); return null; }
    }

    private static void EndToolSlotStep(SentryCall call, ToolSlotWatch? watch)
    {
        EndToolSlotWatch(watch);
        if (call.Step == SentryStep.Destroy) ToolSlotSentries.Remove(call.Sentry.Pointer);
    }

    private static ToolSlotWatch BeginToolSlotWatch(PlayerBackpack? backpack, string owner, string step)
    {
        _toolSlotDepth++;
        var watch = new ToolSlotWatch { Owner = owner, Step = step };
        if (backpack != null && backpack.Pointer != IntPtr.Zero)
        {
            watch.Backpack = backpack;
            ReadToolSlot(backpack, watch);
        }
        return watch;
    }

    private static void EndToolSlotWatch(ToolSlotWatch? watch)
    {
        if (watch == null) return;
        if (_toolSlotDepth > 0) _toolSlotDepth--;
        var backpack = watch.Backpack;
        if (backpack == null || backpack.Pointer == IntPtr.Zero) return;
        try
        {
            var item = watch.Item;
            var deployed = watch.Deployed;
            var status = watch.Status;
            ReadToolSlot(backpack, watch);
            if (watch.Item == item && watch.Deployed == deployed && watch.Status == status) return;
            Plugin.Logger.Error("core-sentry-changed-tool-slot-during-before", "A sentry changed the tool slot of " + watch.Owner + " during " + watch.Step + " (before " + item + ", deployed "
                + deployed + ", status " + status + "; after " + watch.Item + ", deployed " + watch.Deployed + ", status " + watch.Status + "); restoring it.");
            backpack.SetDeployed(InventorySlot.GearClass, deployed);
            backpack.UpdateItemStatus(InventorySlot.GearClass, (eInventoryItemStatus)status);
        }
        catch (Exception error) { Plugin.Logger.Error("core-could-not-verify-tool-slot", "Could not verify the tool slot after a sentry " + watch.Step + ": " + error); }
    }

    private static void ReadToolSlot(PlayerBackpack backpack, ToolSlotWatch into)
    {
        into.Deployed = backpack.IsDeployed(InventorySlot.GearClass);
        if (!backpack.TryGetBackpackItem(InventorySlot.GearClass, out var item) || item == null) { into.Item = "(empty)"; into.Status = 0; return; }
        into.Item = item.Name ?? "(unnamed)";
        into.Status = (int)item.Status;
    }
}
