using System;
using System.Collections.Generic;
using GameData;

namespace ForgeCore;

/// <summary>Adding data blocks (gear, archetypes, items ...) built in code to the game's tables. Blocks are only added, never
/// changed: an ID or name the game or another mod already uses is refused, so mods cannot overwrite each other. The write itself is
/// <see cref="NativeDataBlockTables"/>'s one transaction, the same one the JSON content path uses, with one ledger of owners.</summary>
public static class DataBlocks
{
    /// <summary>A shallow copy of a donor block under a new name and ID, enabled; nested reference data is shared with the donor,
    /// so change only a nested object that was cloned too. Add it with <see cref="Add{T}"/> or <see cref="AddAll"/>.</summary>
    public static T Clone<T>(T donor, string name, uint id) where T : GameDataBlockBase<T>
    {
        if (donor == null) throw new ArgumentNullException(nameof(donor));
        var copy = donor.MemberwiseClone().Cast<T>();
        copy.name = name;
        copy.persistentID = id;
        copy.internalEnabled = true;
        return copy;
    }

    /// <summary>Whether <paramref name="block"/> could be added by <paramref name="modId"/> now: Free, AlreadyAdded or Conflict
    /// (its ID or name is taken, or an item ID a built prefab table holds). Nothing changes; use it to check a whole set early.</summary>
    public static DataBlockResult Check<T>(string modId, T block) where T : GameDataBlockBase<T>
    {
        if (block == null) throw new ArgumentNullException(nameof(block));
        return NativeDataBlockTables.Classify(typeof(T), modId, block.persistentID, block.name, out _);
    }

    /// <summary>Adds one block; see <see cref="AddAll"/>.</summary>
    public static DataBlockResult Add<T>(string modId, T block) where T : GameDataBlockBase<T>
        => block == null ? throw new ArgumentNullException(nameof(block)) : AddAll(modId, new object[] { block });

    /// <summary>Adds every block, of any tables, in one transaction and reads each back through the game's three indexes. Blocks
    /// <paramref name="modId"/> added before (same table, ID and name) are skipped: AlreadyAdded when none was left to write, else
    /// Added. Any other block under an ID or name of the set is a Conflict and nothing changes; a failed write is removed again
    /// (ReadbackFailed). The error is logged. A rollback that itself fails throws and latches
    /// <see cref="NativeDataBlockTables.HasUnknownState"/>.</summary>
    public static DataBlockResult AddAll(string modId, IReadOnlyList<object> blocks)
    {
        try { return NativeDataBlockTables.Add(modId, blocks) == 0 ? DataBlockResult.AlreadyAdded : DataBlockResult.Added; }
        catch (NativeDataBlockConflictException error)
        {
            Log.For(modId).Error("core-data-blocks-refused", "Data blocks refused: " + error.Message + ".");
            return DataBlockResult.Conflict;
        }
        catch (NativeDataBlockRollbackException error)
        {
            Log.For(modId).Error("core-data-blocks-could-not-be-removed", "Data blocks could not be removed again after a failed add: " + error.InnerException?.Message);
            throw;
        }
        catch (Exception error)
        {
            Log.For(modId).Error("core-data-blocks-were-not-added-all", "Data blocks were not added (all removed again): " + error.Message);
            return DataBlockResult.ReadbackFailed;
        }
    }
}
