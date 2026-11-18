using Player;

namespace ForgeCore;

public static partial class Players
{
    /// <summary>True when the game's in-level agent list (<c>PlayerManager.PlayerAgentsInLevel</c>) can be read; false when it has none (no level).</summary>
    public static bool Readable => PlayerManager.PlayerAgentsInLevel != null;

    /// <summary>How many agents that list holds now; 0 when it cannot be read. It changes as players come and go, so read it again each pass.</summary>
    public static int Count => PlayerManager.PlayerAgentsInLevel?.Count ?? 0;

    /// <summary>The agent at <paramref name="index"/> (below <see cref="Count"/>) of that list, as the game holds it: it may be null or collected.</summary>
    public static PlayerAgent? At(int index) => PlayerManager.PlayerAgentsInLevel![index];
}
