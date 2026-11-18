namespace ForgeVisual;

/// <summary>What a provider says a player looks like: the provider's id and a description only the provider can read (a recorder writes
/// both into its file and hands them back).</summary>
public readonly record struct PlayerLook(string ProviderId, byte[] Data);

/// <summary>The rule of <c>PlayerLooks</c> without the game: one provider at a time (a second is refused), and a look goes only to the provider
/// that described it. Anything else answers null, and the user draws the game's own body.</summary>
internal sealed class LookRouter<TProvider> where TProvider : class
{
    /// <summary>The id of the provider registered now, null when there is none.</summary>
    public string? Id { get; private set; }

    /// <summary>The provider registered now, null when there is none.</summary>
    public TProvider? Provider { get; private set; }

    /// <summary>Registers <paramref name="provider"/> as <paramref name="id"/>; false (and nothing changes) while another is registered.</summary>
    public bool Provide(string id, TProvider provider)
    {
        if (Provider != null) return false;
        Id = id;
        Provider = provider;
        return true;
    }

    /// <summary>Withdraws <paramref name="provider"/> if it is the one registered.</summary>
    public void Withdraw(TProvider provider)
    {
        if (!ReferenceEquals(Provider, provider)) return;
        Provider = null;
        Id = null;
    }

    /// <summary>The provider that can read <paramref name="look"/>: the registered one when it described it, else null.</summary>
    public TProvider? For(PlayerLook look) => Provider != null && look.Data != null && Id == look.ProviderId ? Provider : null;
}
