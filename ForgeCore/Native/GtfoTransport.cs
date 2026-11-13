using System;
using GTFO.API;
using SNetwork;

namespace ForgeCore;

/// <summary>ForgeCore's network over GTFO-API's NetworkAPI: every message is a free-sized event sent to one player,
/// <see cref="NetDelivery.Reliable"/> on the reliable ordered channel the game uses for orders, <see cref="NetDelivery.Lossy"/> on its
/// non-critical (unreliable) channel. A message for several players is sent to each in turn: GTFO-API's player-list overload builds
/// the packet once and hands the list to the game's own <c>SNet_Core.SendBytes</c> list overload, whose body is native code that
/// could not be read, so what it does with a list is not known.</summary>
internal sealed class GtfoTransport : INetTransport
{
    public void Register(string name, Action<ulong, byte[]> receive) => NetworkAPI.RegisterFreeSizedEvent(name, receive);

    public void Send(string name, byte[] data, ulong to, NetDelivery delivery)
    {
        if (SNet.TryGetPlayer(to, out var player) && player != null)
            NetworkAPI.InvokeFreeSizedEvent(name, data, player, delivery == NetDelivery.Lossy ? SNet_ChannelType.GameNonCritical : SNet_ChannelType.GameOrderCritical);
    }

    public ulong Local => SNet.LocalPlayer?.Lookup ?? 0;

    public ulong Host => SNet.Master?.Lookup ?? 0;

    /// <summary>The session's members, not the Steam lobby's: the lobby also holds players the session hub has not accepted (a
    /// different game revision, still joining), who are not part of the game and would be greeted for nothing.</summary>
    public int Others(ulong[] into)
    {
        var players = SNet.SessionHub?.PlayersInSession;
        if (players == null) return 0;
        var local = Local;
        var count = 0;
        for (var i = 0; i < players.Count && count < into.Length; i++)
        {
            var player = players[i];
            if (player == null || player.IsBot || player.Lookup == local) continue;
            into[count++] = player.Lookup;
        }
        return count;
    }

    public bool IsHuman(ulong lookup) => SNet.TryGetPlayer(lookup, out var player) && player != null && !player.IsBot;
}
