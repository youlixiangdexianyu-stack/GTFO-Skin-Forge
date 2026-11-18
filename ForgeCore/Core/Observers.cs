using System;
using System.Collections.Generic;

namespace ForgeCore;

/// <summary>Any number of observers of one kind of report. Adding or removing one copies the array, so a report walks a stable array and
/// nothing allocates; <see cref="Any"/> is one field read, so a library's report site costs nothing while nobody observes. Main thread only.</summary>
internal sealed class Observers<T> where T : class
{
    private T[] _items = Array.Empty<T>();

    /// <summary>True while at least one observer is added.</summary>
    public bool Any { get; private set; }

    /// <summary>The observers in the order they were added; the array is replaced, never changed, so it may be walked while one is added or removed.</summary>
    public T[] Items => _items;

    /// <summary>Adds <paramref name="observer"/>; disposing the result removes it (once). The same observer added twice is told twice.</summary>
    public IDisposable Add(T observer)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        _items = [.. _items, observer];
        Any = true;
        return new Subscription(() => Remove(observer));
    }

    private void Remove(T observer)
    {
        var at = Array.IndexOf(_items, observer);
        if (at < 0) return;
        var left = new T[_items.Length - 1];
        Array.Copy(_items, 0, left, 0, at);
        Array.Copy(_items, at + 1, left, at, left.Length - at);
        _items = left;
        Any = left.Length != 0;
    }
}

/// <summary>Numbers for the names of what a library shows (an effect, a model, a sound cue, a sprite sheet), one per distinct name for the life of the
/// process: a library takes the number once when it makes its handle, so a report carries an int, never a string, and a recorder turns it into its own
/// short table index with an array, not a dictionary. 0 is no name. Main thread only.</summary>
public static class RecordingNames
{
    private static readonly Dictionary<string, int> Ids = new(StringComparer.Ordinal);
    private static readonly List<string> Names = new() { "" };

    /// <summary>The number of <paramref name="name"/>, given out the first time it is asked for; 0 for null or empty.</summary>
    internal static int Of(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (Ids.TryGetValue(name!, out var id)) return id;
        id = Names.Count;
        Names.Add(name!);
        Ids.Add(name!, id);
        return id;
    }

    /// <summary>The name behind <paramref name="id"/>; empty for 0 or a number never given out.</summary>
    public static string Name(int id) => (uint)id < (uint)Names.Count ? Names[id] : "";

    /// <summary>How many numbers were given out, plus one (the highest number is one less): a recorder sizes its index array by it.</summary>
    public static int Count => Names.Count;
}

/// <summary>A note a mod writes into the recording being made (<c>Recording.Note</c>): <see cref="ModId"/> and <see cref="Kind"/> name whose and what, <see cref="Payload"/> is the mod's own text
/// (JSON, a number, a word), and <see cref="Mark"/> asks the replay to show it on its timeline. A recorder cuts <see cref="ModId"/> and <see cref="Kind"/> to 200 and <see cref="Payload"/> to 1024 UTF-8 bytes.</summary>
public readonly record struct RecordingNote(string ModId, string Kind, string Payload, bool Mark);

/// <summary>The listeners of <see cref="RecordingNote"/>s; the walk is isolated so a listener that throws is reported and the others still hear. Main thread only.</summary>
internal static class RecordingNotes
{
    private static readonly Observers<Action<RecordingNote>> All = new();

    internal static IDisposable Add(Action<RecordingNote> listener) => All.Add(listener);

    internal static void Send(in RecordingNote note, Action<Exception> failed)
    {
        var all = All.Items;
        for (var i = 0; i < all.Length; i++)
        {
            try { all[i](note); }
            catch (Exception error) { failed(error); }
        }
    }
}
