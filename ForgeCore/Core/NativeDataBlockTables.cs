using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace ForgeCore;

/// <summary>The answer of <c>DataBlocks.Check</c> and <c>DataBlocks.AddAll</c>.</summary>
public enum DataBlockResult
{
    /// <summary>The ID is free (<c>Check</c> only).</summary>
    Free,
    /// <summary>Added and read back.</summary>
    Added,
    /// <summary>This mod added the same block (same ID and name) before; nothing changed.</summary>
    AlreadyAdded,
    /// <summary>The ID or name belongs to the game or another mod (or this mod under another name); nothing changed.</summary>
    Conflict,
    /// <summary>Publishing or reading the block back failed; every block of the call was removed again.</summary>
    ReadbackFailed,
}

/// <summary>Which mod added which data block: blocks are only added, never changed, and an ID taken by anyone else is refused. One
/// ledger for the process, written by every successful publication of <see cref="NativeDataBlockTables"/>.</summary>
internal sealed class DataBlockLedger
{
    internal static readonly DataBlockLedger Shared = new();

    private readonly Dictionary<(string Table, uint Id), (string Mod, string Name)> _owners = new();

    /// <summary>Whether <paramref name="modId"/> may add block <paramref name="id"/> named <paramref name="name"/> to
    /// <paramref name="table"/>, given the name of the block the table already has under that ID (null: none).</summary>
    internal DataBlockResult Decide(string table, uint id, string modId, string name, string? existingName)
    {
        if (id == 0) throw new ArgumentOutOfRangeException(nameof(id), "Data block ID 0 is never valid.");
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name the data block.", nameof(name));
        if (existingName == null) return DataBlockResult.Free;
        return _owners.TryGetValue((table, id), out var owner) && owner.Mod == modId && owner.Name == name && existingName == name
            ? DataBlockResult.AlreadyAdded
            : DataBlockResult.Conflict;
    }

    internal void Owns(string table, uint id, string modId, string name) => _owners[(table, id)] = (modId, name);

    internal string? OwnerOf(string table, uint id) => _owners.TryGetValue((table, id), out var owner) ? owner.Mod : null;
}

/// <summary>One already-resolved native definition, registered through <see cref="NativeDataBlockTables.Register"/>.</summary>
public readonly record struct NativeDataBlockRegistration(string Type, string Name, uint PersistentId, string Json,
    uint? SourceId = null);

public sealed class NativeDataBlockRollbackException : Exception
{
    internal NativeDataBlockRollbackException(Exception error) : base("Native table rollback failed.", error) { }
}

public sealed class NativeDataBlockConflictException : Exception
{
    internal NativeDataBlockConflictException(string message) : base(message) { }
}

/// <summary>The startup-only table transaction used by Rundowns and domain content. CellJSON creates native
/// objects; managed JSON deserialization cannot create a usable IL2CPP DataBlock. No tick path calls this type.</summary>
public static partial class NativeDataBlockTables
{
    /// <summary>A failed rollback makes native table state unknown for this process. There is deliberately no
    /// reset or retry API: only a fresh game process establishes a complete new table baseline.</summary>
    public static Exception? LastRollbackFailure { get; private set; }
    public static bool HasUnknownState => LastRollbackFailure != null;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    private const BindingFlags Fields = BindingFlags.Public | BindingFlags.Instance;

    private sealed class Prepared
    {
        internal Prepared(Type table, NativeDataBlockRegistration definition, object value, JsonElement? supplied)
        { Table = table; Definition = definition; Value = value; Supplied = supplied; }
        internal Type Table;
        internal NativeDataBlockRegistration Definition;
        internal object Value;
        internal JsonElement? Supplied;
        internal object Blocks = null!, ById = null!, ByName = null!;
        internal bool BlockAttempted, IdAttempted, NameAttempted, NameAdded, ReadbackFailed;
    }

    /// <summary>Verify an original native row and the unchanged links a compiled clone claims it came from.
    /// Zero links are valid only when the source itself has zero; referenced nonzero rows are checked separately.</summary>
    public static void RequireSourceLinks(string type, uint sourceId, IReadOnlyDictionary<string, uint> expected)
    {
        var source = RequireBlock(type, sourceId);
        foreach (var link in expected)
            if (Get(source, link.Key) is not uint value || value != link.Value)
                throw new InvalidOperationException("Native source link differs: " + type + "/" + sourceId + "." + link.Key);
    }

    /// <summary>Whether a Forge publication (<see cref="Register"/> or a code-built add) put row <paramref name="id"/> into the
    /// <paramref name="type"/> table: a row is found by its number, and this tells a package's own row from a vanilla or foreign
    /// one without relying on its name.</summary>
    public static bool IsPublished(string type, uint id) => DataBlockLedger.Shared.OwnerOf(type + "DataBlock", id) != null;

    public static object RequireBlock(string type, uint id)
    {
        var reader = BlockReader(NativeType("GameData." + type + "DataBlock"));
        var source = reader.Get(id);
        if (source == null || reader.PersistentId(source) != id)
            throw new InvalidOperationException("Missing native block " + type + "/" + id);
        return source;
    }

    /// <summary>Prepares every row before changing any table, rejects occupied IDs/names, publishes all three
    /// indexes and verifies the result. A failed publication removes only entries still identifiable as ours,
    /// in reverse order. Ambiguous ownership or unsafe index shifts preserve the shared data and latch unknown
    /// state rather than advertise a clean refusal.</summary>
    public static void Register(IReadOnlyList<NativeDataBlockRegistration> definitions, bool strictFields = false,
        string owner = "Forge content")
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (LastRollbackFailure is { } previous) throw new NativeDataBlockRollbackException(previous);
        var rows = new List<Prepared>();
        var ids = new HashSet<(string, uint)>(); var names = new HashSet<(string, string)>();
        foreach (var definition in definitions)
        {
            if (definition.PersistentId == 0 || string.IsNullOrEmpty(definition.Name)
                || !ids.Add((definition.Type, definition.PersistentId)) || !names.Add((definition.Type, definition.Name)))
                throw new InvalidOperationException("Duplicate or invalid native block identity: " + definition.Name);
            var type = NativeType("GameData." + definition.Type + "DataBlock");
            if (definition.SourceId is { } sourceId)
            {
                var source = Call(type, "GetBlock", sourceId);
                if (source == null || (uint)Get(source, "persistentID")! != sourceId)
                    throw new InvalidOperationException("Missing native source " + definition.Type + "/" + sourceId);
            }
            using var document = JsonDocument.Parse(definition.Json);
            if (strictFields) Validate(document.RootElement, type, definition.Name);
            var value = Deserialize(type, definition.Json);
            if (strictFields) Validate(document.RootElement, type, definition.Name, value, compare: true);
            if ((uint)Get(value, "persistentID")! != definition.PersistentId
                || (string)Get(value, "name")! != definition.Name)
                throw new InvalidOperationException("Native block identity changed during deserialization: " + definition.Name);
            // Register is never idempotent: a row any owner published before is a collision too.
            if (Classify(type, owner, definition.PersistentId, definition.Name, out var reason) != DataBlockResult.Free)
                throw new NativeDataBlockConflictException(reason);
            rows.Add(new Prepared(type, definition, value, strictFields ? document.RootElement.Clone() : null));
        }
        Publish(rows, owner);
    }

    /// <summary>Adds blocks built in code (a donor <c>DataBlocks.Clone</c> with changed fields) through the same transaction as
    /// <see cref="Register"/>: every block is checked before the first table changes, a failed publication is removed again. A block
    /// <paramref name="owner"/> published before (same table, ID and name) is skipped; any other occupant of its ID or name is a
    /// <see cref="NativeDataBlockConflictException"/> and nothing changes. Returns how many blocks were written.</summary>
    internal static int Add(string owner, IReadOnlyList<object> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (LastRollbackFailure is { } previous) throw new NativeDataBlockRollbackException(previous);
        var rows = new List<Prepared>();
        var ids = new HashSet<(Type, uint)>(); var names = new HashSet<(Type, string)>();
        foreach (var block in blocks)
        {
            var table = block.GetType();
            uint id = (uint)Get(block, "persistentID")!; var name = (string?)Get(block, "name") ?? "";
            if (id == 0 || name.Length == 0 || !ids.Add((table, id)) || !names.Add((table, name)))
                throw new InvalidOperationException("Duplicate or invalid native block identity: " + name);
            var decision = Classify(table, owner, id, name, out var reason);
            if (decision == DataBlockResult.Conflict) throw new NativeDataBlockConflictException(reason);
            if (decision == DataBlockResult.Free)
                rows.Add(new Prepared(table, new NativeDataBlockRegistration(Label(table), name, id, ""), block, null));
        }
        if (rows.Count != 0) Publish(rows, owner);
        return rows.Count;
    }

    private static string Label(Type table) => table.Name.EndsWith("DataBlock", StringComparison.Ordinal) ? table.Name[..^9] : table.Name;

    /// <summary>The one occupancy check of every way into the tables. Free: the ID and name are unused (and, for an item, no prefab
    /// table holds the ID). AlreadyAdded: <paramref name="owner"/> published this very block. Conflict: anything else, with the
    /// occupant, and its owner when the ledger knows it, in <paramref name="reason"/>.</summary>
    internal static DataBlockResult Classify(Type table, string owner, uint id, string name, out string reason)
    {
        reason = "";
        var byId = Get(table, "s_blockByID")!; var byName = Get(table, "s_blockIDByName")!;
        var occupant = Has(byId, id) ? (string?)Get(Call(byId, "get_Item", id)!, "name") ?? "" : null;
        var decision = DataBlockLedger.Shared.Decide(table.Name, id, owner, name, occupant);
        if (decision != DataBlockResult.Free)
        {
            var holder = DataBlockLedger.Shared.OwnerOf(table.Name, id);
            reason = "Native ID collision: " + name + " and loaded:" + Label(table) + "/" + occupant + " / " + id
                + (holder == null ? "" : " (added by " + holder + ")");
            return decision;
        }
        if (Has(byName, name))
        {
            reason = "Native name collision: " + name + " / " + Call(byName, "get_Item", name);
            return DataBlockResult.Conflict;
        }
        if (PrefabCollision(table, id) is { } mode)
        {
            reason = "Native item prefab collision: " + name + " / " + id + " in mode " + mode;
            return DataBlockResult.Conflict;
        }
        return DataBlockResult.Free;
    }

    // The game builds its item prefab tables from the item blocks once; an item ID a built table already holds belongs to another
    // block and would hand this item that block's prefabs. No such table (not built yet, or no such game class): nothing collides.
    private static int? PrefabCollision(Type table, uint id)
    {
        if (table.Name != "ItemDataBlock" || FindNativeType("ItemSpawnManager") is not { } manager) return null;
        var member = (object?)manager.GetProperty("m_loadedPrefabsPerItemMode", Members) ?? manager.GetField("m_loadedPrefabsPerItemMode", Members);
        if (member == null || Get(manager, "m_loadedPrefabsPerItemMode") is not { } modes) return null;
        int count = modes is Array array ? array.Length : (int)Get(modes, "Length")!;
        for (int mode = 0; mode < count; mode++)
        {
            var prefabs = modes is Array plain ? plain.GetValue(mode) : Call(modes, "get_Item", mode);
            if (prefabs != null && Has(prefabs, id)) return mode;
        }
        return null;
    }

    // Publishes every prepared row into the three native indexes, reads each back and, on any failure, removes only what is provably
    // ours. Success records the rows in the ledger under <paramref name="owner"/>.
    private static void Publish(List<Prepared> rows, string owner)
    {
        try
        {
            foreach (var row in rows)
            {
                // Keep the exact containers used by this publication for validation and undo.
                row.Blocks = Get(Get(row.Table, "Wrapper")!, "Blocks")!;
                row.ById = Get(row.Table, "s_blockByID")!;
                row.ByName = Get(row.Table, "s_blockIDByName")!;
                if (Has(row.ById, row.Definition.PersistentId) || Has(row.ByName, row.Definition.Name))
                    throw new NativeDataBlockConflictException("Native identity became occupied: " + row.Definition.Name);
                Set(row.Value, "Index", (int)Get(row.Blocks, "Count")!);
                row.BlockAttempted = true; Call(row.Blocks, "Add", row.Value);
                row.IdAttempted = true; Call(row.ById, "Add", row.Definition.PersistentId, row.Value);
                row.NameAttempted = true; Call(row.ByName, "Add", row.Definition.Name, row.Definition.PersistentId);
                row.NameAdded = true;
            }
            foreach (var row in rows)
            {
                try
                {
                    var byId = Call(Get(row.Table, "s_blockByID")!, "get_Item", row.Definition.PersistentId)!;
                    var byName = Call(Get(row.Table, "s_blockIDByName")!, "get_Item", row.Definition.Name)!;
                    var blocks = Get(Get(row.Table, "Wrapper")!, "Blocks")!;
                    var indexed = Call(blocks, "get_Item", (int)Get(row.Value, "Index")!)!;
                    var resolved = Call(row.Table, "GetBlock", row.Definition.PersistentId);
                    if ((uint)Get(byId, "persistentID")! != row.Definition.PersistentId
                        || (uint)byName != row.Definition.PersistentId
                        || (uint)Get(indexed, "persistentID")! != row.Definition.PersistentId
                        || (string)Get(indexed, "name")! != row.Definition.Name
                        || resolved == null || (uint)Get(resolved, "persistentID")! != row.Definition.PersistentId
                        || (string)Get(resolved, "name")! != row.Definition.Name)
                        throw new InvalidOperationException("Native table readback failed: " + row.Definition.Name);
                    if (row.Supplied is { } supplied)
                    {
                        Validate(supplied, row.Table, row.Definition.Name + ".byID", byId, compare: true);
                        Validate(supplied, row.Table, row.Definition.Name + ".Wrapper", indexed, compare: true);
                        Validate(supplied, row.Table, row.Definition.Name + ".GetBlock", resolved, compare: true);
                    }
                    if (!SameObject(byId, row.Value) || !SameObject(indexed, row.Value) || !SameObject(resolved, row.Value)
                        || !SameObject(Get(row.Table, "s_blockByID"), row.ById)
                        || !SameObject(Get(row.Table, "s_blockIDByName"), row.ByName)
                        || !SameObject(blocks, row.Blocks))
                        throw new InvalidOperationException("Native publication was replaced: " + row.Definition.Name);
                }
                catch { row.ReadbackFailed = true; throw; }
            }
            foreach (var row in rows) Published(row.Value);
            foreach (var row in rows) DataBlockLedger.Shared.Owns(row.Table.Name, row.Definition.PersistentId, owner, row.Definition.Name);
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            void Undo(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
            foreach (var row in rows.AsEnumerable().Reverse())
            {
                // IDs and names do not establish object identity. Undo only a proven publication.
                if (row.NameAttempted) Undo(() =>
                {
                    if (!Has(row.ByName, row.Definition.Name)
                        || (uint)Call(row.ByName, "get_Item", row.Definition.Name)! != row.Definition.PersistentId) return;
                    if (!OwnsId(row)) return;
                    // The name map stores only uints. A throwing Add has no completion receipt;
                    // preserve an unproven mapping and report the incomplete publication.
                    if (!row.NameAdded)
                        throw new InvalidOperationException("Uncertain native name publication: " + row.Definition.Name);
                    Call(row.ByName, "Remove", row.Definition.Name);
                    if (Has(row.ByName, row.Definition.Name)
                        && (uint)Call(row.ByName, "get_Item", row.Definition.Name)! == row.Definition.PersistentId)
                        throw new InvalidOperationException("Rollback left native name: " + row.Definition.Name);
                });
                if (row.IdAttempted) Undo(() =>
                {
                    if (OwnsId(row)) Call(row.ById, "Remove", row.Definition.PersistentId);
                    if (OwnsId(row)) throw new InvalidOperationException("Rollback left native ID: " + row.Definition.Name);
                });
                if (row.BlockAttempted) Undo(() =>
                {
                    int ownedIndex = -1;
                    int count = (int)Get(row.Blocks, "Count")!;
                    for (int i = 0; i < count; i++)
                        if (SameObject(Call(row.Blocks, "get_Item", i), row.Value)) { ownedIndex = i; break; }
                    if (ownedIndex < 0) return;
                    // Without a verified native reindex operation, removing a middle row would
                    // shift an existing Index. Keep the row and fail closed instead.
                    if (ownedIndex != count - 1)
                        throw new InvalidOperationException("Rollback would shift another native row: " + row.Definition.Name);
                    Call(row.Blocks, "Remove", row.Value);
                    for (int i = 0; i < (int)Get(row.Blocks, "Count")!; i++)
                        if (SameObject(Call(row.Blocks, "get_Item", i), row.Value))
                            throw new InvalidOperationException("Rollback left native wrapper row: " + row.Definition.Name);
                });
            }
            // Validate the final transaction state after all removals; intermediate checks alone
            // do not establish that every publication was undone.
            foreach (var row in rows)
            {
                if (row.IdAttempted) Undo(() =>
                {
                    if (OwnsId(row) || (row.ReadbackFailed && Has(row.ById, row.Definition.PersistentId)))
                        throw new InvalidOperationException("Rollback left an owned or unreadable native ID: " + row.Definition.Name);
                });
                if (row.NameAttempted) Undo(() =>
                {
                    if (Has(row.ByName, row.Definition.Name)
                        && (uint)Call(row.ByName, "get_Item", row.Definition.Name)! == row.Definition.PersistentId
                        && (!Has(row.ById, row.Definition.PersistentId) || OwnsId(row)))
                        throw new InvalidOperationException("Rollback left an uncertain native name: " + row.Definition.Name);
                });
                if (row.BlockAttempted) Undo(() =>
                {
                    for (int i = 0; i < (int)Get(row.Blocks, "Count")!; i++)
                    {
                        var value = Call(row.Blocks, "get_Item", i)!;
                        if (SameObject(value, row.Value) || (row.ReadbackFailed
                            && (uint)Get(value, "persistentID")! == row.Definition.PersistentId))
                            throw new InvalidOperationException("Rollback left an owned or unreadable native wrapper row: " + row.Definition.Name);
                    }
                });
            }
            if (failures.Count != 0)
            {
                LastRollbackFailure = new AggregateException(new[] { original }.Concat(failures));
                throw new NativeDataBlockRollbackException(LastRollbackFailure);
            }
            throw;
        }
    }

    /// <summary>A game cache built from a table before this publication learns the new row (Native: Text).</summary>
    static partial void Published(object block);

    private static bool Has(object map, object key) => (bool)Call(map, "ContainsKey", key)!;
    private static bool OwnsId(Prepared row) => Has(row.ById, row.Definition.PersistentId)
        && SameObject(Call(row.ById, "get_Item", row.Definition.PersistentId), row.Value);

    private static bool SameObject(object? left, object right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null) return false;
        // IL2CPP may hand out distinct managed wrappers for one native object. Only the actual
        // interop base's Pointer is an identity; arbitrary content fields named Pointer are not.
        static IntPtr Pointer(object value)
        {
            for (var type = value.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase")
                    return type.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value)
                        is IntPtr pointer ? pointer : IntPtr.Zero;
            return IntPtr.Zero;
        }
        var pointer = Pointer(left);
        return pointer != IntPtr.Zero && pointer == Pointer(right);
    }

    // This deliberately follows actual native member types, including nested structures and collection elements.
    // Strict callers also compare supplied values before publication and through the native indexes: a writable
    // member alone does not prove that CellJSON applied it. Float comparisons use the declared native precision,
    // not JSON text equality or an epsilon. Non-strict Rundown materialization retains its established behavior.
    private static void Validate(JsonElement value, Type type, string path, object? actual = null, bool compare = false)
    {
        void Mismatch() => throw new InvalidOperationException("Native field value changed: " + path);
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null)
        {
            if (value.ValueKind == JsonValueKind.Null) { if (compare && actual != null) Mismatch(); return; }
            type = nullable;
        }
        bool valid; object? expected;
        // CellJSON's LocalizedText converter reads a Text ID or inline text, not an object.
        // Check the converted native value as well: successful deserialization alone is insufficient.
        if (type.FullName == "Localization.LocalizedText")
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint textId))
            {
                if (compare && (actual == null || !Equals(Get(actual, "Id"), textId))) Mismatch();
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                if (compare && (actual == null || !Equals(Get(actual, "UntranslatedText"), value.GetString()))) Mismatch();
            }
            else throw new InvalidOperationException("Invalid native localized text: " + path);
            return;
        }
        if (type == typeof(string))
        { valid = value.ValueKind is JsonValueKind.String or JsonValueKind.Null; expected = valid ? value.GetString() : null; }
        else if (type == typeof(bool))
        { valid = value.ValueKind is JsonValueKind.True or JsonValueKind.False; expected = valid && value.GetBoolean(); }
        else if (type.IsEnum)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                string name = value.GetString()!;
                if (!Enum.GetNames(type).Contains(name, StringComparer.Ordinal))
                    throw new InvalidOperationException("Invalid native enum name: " + path);
                expected = Enum.Parse(type, name);
                if (compare && !Equals(expected, actual)) Mismatch();
                return;
            }
            var underlying = Enum.GetUnderlyingType(type);
            Validate(value, underlying, path, compare && actual != null ? Convert.ChangeType(actual, underlying, CultureInfo.InvariantCulture) : actual, compare);
            return;
        }
        else if (type == typeof(byte)) { valid = value.TryGetByte(out var number); expected = number; }
        else if (type == typeof(sbyte)) { valid = value.TryGetSByte(out var number); expected = number; }
        else if (type == typeof(short)) { valid = value.TryGetInt16(out var number); expected = number; }
        else if (type == typeof(ushort)) { valid = value.TryGetUInt16(out var number); expected = number; }
        else if (type == typeof(int)) { valid = value.TryGetInt32(out var number); expected = number; }
        else if (type == typeof(uint)) { valid = value.TryGetUInt32(out var number); expected = number; }
        else if (type == typeof(long)) { valid = value.TryGetInt64(out var number); expected = number; }
        else if (type == typeof(ulong)) { valid = value.TryGetUInt64(out var number); expected = number; }
        else if (type == typeof(float)) { valid = value.TryGetSingle(out float number) && float.IsFinite(number); expected = number; }
        else if (type == typeof(double)) { valid = value.TryGetDouble(out double number) && double.IsFinite(number); expected = number; }
        else
        {
            var element = ListElementType(type);
            if (element != null)
            {
                if (value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Expected native list: " + path);
                var item = type.IsArray ? null : type.GetProperty("Item", Fields, null, element, new[] { typeof(int) }, null);
                if (compare)
                {
                    if (actual == null) Mismatch();
                    int count = type.IsArray ? ((Array)actual!).Length
                        : (int)Get(actual!, type.GetProperty("Count", Fields) != null ? "Count" : "Length")!;
                    if (count != value.GetArrayLength()) Mismatch();
                }
                int index = 0;
                foreach (var child in value.EnumerateArray())
                {
                    object? observed = !compare ? null : type.IsArray ? ((Array)actual!).GetValue(index) : item!.GetValue(actual, new object[] { index });
                    Validate(child, element, path + "[" + index++ + "]", observed, compare);
                }
                return;
            }
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Expected native object: " + path);
            if (compare && actual == null) Mismatch();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in value.EnumerateObject())
            {
                if (!seen.Add(child.Name)) throw new InvalidOperationException("Duplicate native field: " + path + "." + child.Name);
                var property = type.GetProperty(child.Name, Fields);
                var field = type.GetField(child.Name, Fields);
                Type? memberType = property is { CanRead: true, CanWrite: true } && property.GetIndexParameters().Length == 0
                    ? property.PropertyType : field is { IsInitOnly: false } ? field.FieldType : null;
                if (memberType == null) throw new InvalidOperationException("Unknown or unwritable native field: " + path + "." + child.Name);
                object? observed = !compare ? null : property != null ? property.GetValue(actual) : field!.GetValue(actual);
                Validate(child.Value, memberType, path + "." + child.Name, observed, compare);
            }
            return;
        }
        if (!valid) throw new InvalidOperationException("Invalid native field type: " + path + " (" + type.Name + ")");
        if (compare && !Equals(expected, actual)) Mismatch();
    }

    // Unity vectors, colors and matrices also have integer indexers. Only actual list/array types
    // use the collection path; value types are checked through their native writable fields.
    private static Type? ListElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        for (var collection = type; collection != null; collection = collection.BaseType)
            if (collection.IsGenericType && collection.GetGenericTypeDefinition().FullName is
                "System.Collections.Generic.List`1" or "Il2CppSystem.Collections.Generic.List`1"
                or "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1")
                return collection.GetGenericArguments()[0];
        return null;
    }

    // A loaded type never changes within the process, so each name is searched across every loaded assembly once
    // per process instead of once per published row. A missing type is not remembered: it is searched and refused again.
    private static readonly Dictionary<string, Type> NativeTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, MethodInfo> Deserializers = new();
    private sealed record NativeBlockReader(Func<uint, object?> Get, Func<object, uint> PersistentId);
    private static readonly Dictionary<Type, NativeBlockReader> BlockReaders = new();
    private static NativeBlockReader BlockReader(Type type)
    {
        if (BlockReaders.TryGetValue(type, out var cached)) return cached;
        var method = type.GetMethod("GetBlock", Members, null, new[] { typeof(uint) }, null)
            ?? throw new MissingMethodException(type.FullName, "GetBlock(uint)");
        if (!method.IsStatic) throw new MissingMethodException(type.FullName, "static GetBlock(uint)");
        var property = type.GetProperty("persistentID", Members);
        var field = type.GetField("persistentID", Members);
        if (property == null && field == null) throw new MissingMemberException(type.FullName, "persistentID");
        var id = Expression.Parameter(typeof(uint), "id");
        var value = Expression.Parameter(typeof(object), "value");
        var typed = Expression.Convert(value, type);
        var member = property != null ? Expression.Property(typed, property) : Expression.Field(typed, field!);
        if (member.Type != typeof(uint)) throw new InvalidOperationException("Native persistentID is not UInt32: " + type.FullName);
        var reader = new NativeBlockReader(
            Expression.Lambda<Func<uint, object?>>(Expression.Convert(Expression.Call(method, id), typeof(object)), id).Compile(),
            Expression.Lambda<Func<object, uint>>(Expression.Convert(member, typeof(uint)), value).Compile());
        BlockReaders.Add(type, reader);
        return reader;
    }
    private static Type NativeType(string name) => FindNativeType(name) ?? throw new InvalidOperationException("Native type unavailable: " + name);
    private static Type? FindNativeType(string name)
    {
        if (NativeTypes.TryGetValue(name, out var cached)) return cached;
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);
        if (type != null) NativeTypes.Add(name, type);
        return type;
    }
    private static object? Get(object target, string name)
    {
        var type = target as Type ?? target.GetType(); var instance = target is Type ? null : target;
        var property = type.GetProperty(name, Members);
        return property != null ? property.GetValue(instance) : (type.GetField(name, Members)
            ?? throw new MissingMemberException(type.FullName, name)).GetValue(instance);
    }
    private static void Set(object target, string name, object? value)
    {
        var type = target.GetType(); var property = type.GetProperty(name, Members);
        if (property != null) property.SetValue(target, value);
        else (type.GetField(name, Members) ?? throw new MissingMemberException(type.FullName, name)).SetValue(target, value);
    }
    private static object? Call(object target, string name, params object?[] args)
    {
        var type = target as Type ?? target.GetType();
        var method = type.GetMethods(Members).Single(candidate => candidate.Name == name && !candidate.IsGenericMethodDefinition
            && candidate.GetParameters().Length == args.Length && candidate.GetParameters().Select((parameter, index) =>
                args[index] == null || parameter.ParameterType.IsInstanceOfType(args[index])).All(match => match));
        return method.Invoke(target is Type ? null : target, args);
    }
    private static object Deserialize(Type type, string json)
    {
        var cellJson = NativeType("CellJSON");
        if (!Deserializers.TryGetValue(type, out var method))
            Deserializers.Add(type, method = cellJson.GetMethods(Members).Single(candidate => candidate.Name == "Deserialize"
                && candidate.IsGenericMethodDefinition).MakeGenericMethod(type));
        return method.Invoke(null, new[] { (object)json, Get(cellJson, "JSONSettings_GameData") })
            ?? throw new InvalidOperationException("Native block deserialization returned null.");
    }
}
