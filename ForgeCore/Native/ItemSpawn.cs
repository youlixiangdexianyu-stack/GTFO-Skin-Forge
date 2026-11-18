namespace ForgeCore;

/// <summary>One call of the game's <c>ItemSpawnManager.SpawnItem</c> as <see cref="GameHooks.ItemSpawned"/> hands it over: the
/// requested item id and mode, and the <see cref="Item"/> the game built once its body returned.</summary>
public sealed class ItemSpawn
{
    public ItemSpawn(uint id, ItemMode mode)
    {
        Id = id;
        Mode = mode;
    }

    /// <summary>The requested <c>ItemDataBlock</c> persistent id (known before the body runs, also when <c>doSetup</c> is false).</summary>
    public uint Id { get; }

    public ItemMode Mode { get; }

    /// <summary>What the game returned; null in <c>Before</c>, and after the body when it was skipped, returned none or threw.</summary>
    public Item? Item { get; internal set; }
}
