using System;
using System.Collections.Generic;
using UnityEngine;

namespace ForgeAudio;

/// <summary>
/// The emitters a game sound event played at a world point goes through, and the stop that has to reach them.
///
/// A Wwise stop event acts on the sound object it is posted to, so a loop posted at a point is not stopped by the same stop event posted
/// globally or on a level object: every positioned play goes through an emitter this pool keeps, and <see cref="Stop"/> posts the stop event
/// on every one of them. An emitter is reused for a new sound within <see cref="ReuseRadius"/> of where it stands, so one shooter's repeated
/// sounds and the loop it started share one emitter instead of pushing that loop out of the pool; when the pool is full the least recently
/// used emitter is recycled. The emitters are the game's own <c>CellSoundPlayer</c>: its constructor registers a Wwise sound object at
/// the point, <c>Post(eventID, pos)</c> moves it there and posts on it (answering the playing id, 0 when nothing was posted), and
/// <c>Recycle</c> releases it.
/// </summary>
internal sealed class PointSoundPool
{
    /// <summary>Emitters kept at once. A positioned loop's emitter is recycled only after this many other points were used since it was last touched.</summary>
    internal const int Capacity = 32;

    /// <summary>Metres within which a new sound reuses a kept emitter instead of taking another.</summary>
    internal const float ReuseRadius = 1f;

    private sealed class Entry
    {
        internal CellSoundPlayer Player = null!;
        internal Vector3 Position;
        internal long Used;
    }

    private readonly List<Entry> _entries = new();
    private CellSoundPlayer? _global;
    private long _clock;

    internal int Count => _entries.Count;

    /// <summary>Posts <paramref name="sound"/> at <paramref name="position"/>; the playing id, 0 when the engine posted nothing.</summary>
    internal uint Play(uint sound, Vector3 position)
    {
        var entry = Nearest(position);
        if (entry != null) entry.Position = position;
        else
        {
            if (_entries.Count >= Capacity)
            {
                var oldest = _entries[0];
                foreach (var candidate in _entries) if (candidate.Used < oldest.Used) oldest = candidate;
                oldest.Player.Recycle();
                _entries.Remove(oldest);
            }
            entry = new Entry { Player = new CellSoundPlayer(position), Position = position };
            _entries.Add(entry);
        }
        entry.Used = ++_clock;
        return entry.Player.Post(sound, position);
    }

    /// <summary>Posts <paramref name="sound"/> on the one emitter that has no place (the sound plays as a level-wide one).</summary>
    internal uint PlayGlobal(uint sound) => (_global ??= new CellSoundPlayer(default(Vector3))).Post(sound, true);

    /// <summary>Posts the stop event <paramref name="sound"/> on every kept emitter; how many it reached.</summary>
    internal int Stop(uint sound)
    {
        foreach (var entry in _entries) entry.Player.Post(sound, entry.Position);
        if (_global != null) _global.Post(sound, true);
        return _entries.Count + (_global != null ? 1 : 0);
    }

    /// <summary>Recycles every emitter (the world ended); every failure is reported together after all were tried.</summary>
    internal void Clear()
    {
        List<Exception>? failures = null;
        foreach (var entry in _entries.ToArray())
        {
            try { entry.Player.Recycle(); _entries.Remove(entry); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (_global != null)
        {
            try { _global.Recycle(); _global = null; }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures != null) throw new AggregateException("point-sound-cleanup-failed", failures);
    }

    private Entry? Nearest(Vector3 position)
    {
        Entry? best = null;
        var bestDistance = ReuseRadius * ReuseRadius;
        foreach (var entry in _entries)
        {
            var distance = ForgeCore.Vec.SqrLength(entry.Position - position);
            if (distance > bestDistance) continue;
            best = entry;
            bestDistance = distance;
        }
        return best;
    }
}
