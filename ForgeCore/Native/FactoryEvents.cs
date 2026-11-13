using System;
using LevelGeneration;

namespace ForgeCore;

/// <summary>The factory's own build events (<c>LG_Factory.OnFactoryBuildStart</c>, <c>OnFactoryBuildDone</c>, <c>OnFactoryBatchDone</c>) for any
/// number of subscribers: the one native delegate of each goes on with its first subscriber and hands the call on at the same moment, inside
/// the factory's own call, in subscription order; a subscription lasts until it is disposed. No Harmony patch: GTFO-API's
/// <c>LevelAPI.OnFactoryStart</c> and <c>OnFactoryDone</c> are postfixes of other methods, so they are not the same moment as these. A handler
/// that throws is logged once and the others still run. Main thread only.</summary>
public static class FactoryEvents
{
    private static readonly Observers<Action> Started = new(), Finished = new();
    private static readonly Observers<Action<LG_Factory.BatchName>> Batches = new();
    private static Il2CppSystem.Action? _started, _finished;
    private static Il2CppSystem.Action<LG_Factory.BatchName>? _batch;

    /// <summary>Generation of a level begins.</summary>
    public static IDisposable OnBuildStart(Action handler)
    {
        if (_started == null)
        {
            _started = (Action)(() => Raise(Started, "build-start"));
            LG_Factory.add_OnFactoryBuildStart(_started);
        }
        return Started.Add(handler);
    }

    /// <summary>Generation finished; a failed generation never raises it.</summary>
    public static IDisposable OnBuildDone(Action handler)
    {
        if (_finished == null)
        {
            _finished = (Action)(() => Raise(Finished, "build-done"));
            LG_Factory.add_OnFactoryBuildDone(_finished);
        }
        return Finished.Add(handler);
    }

    /// <summary>One generation batch finished.</summary>
    public static IDisposable OnBatchDone(Action<LG_Factory.BatchName> handler)
    {
        if (_batch == null)
        {
            _batch = new Action<LG_Factory.BatchName>(RaiseBatch);
            LG_Factory.add_OnFactoryBatchDone(_batch);
        }
        return Batches.Add(handler);
    }

    private static void Raise(Observers<Action> handlers, string key)
    {
        foreach (var handler in handlers.Items)
        {
            try { handler(); }
            catch (Exception error) { Log.For("ForgeCore").ErrorOnce("A factory " + key + " handler failed: " + error); }
        }
    }

    private static void RaiseBatch(LG_Factory.BatchName batch)
    {
        foreach (var handler in Batches.Items)
        {
            try { handler(batch); }
            catch (Exception error) { Log.For("ForgeCore").ErrorOnce("A factory batch-done handler failed: " + error); }
        }
    }
}
