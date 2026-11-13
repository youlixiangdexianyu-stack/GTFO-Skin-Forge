using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ForgeCore;

/// <summary>A JSON value a reader refused: <see cref="Field"/> names it (a path such as <c>Shield.Range</c>), the message says why.</summary>
public class StrictJsonException : Exception
{
    public StrictJsonException(string field, string message) : base(message) => Field = field;

    public string Field { get; }
}

/// <summary>Strict reading of a JSON object that a mod's data file or call-in payload is made of. Nothing is guessed or repaired:
/// an unknown or repeated member is refused, a value of the wrong type names its field, and a number outside its range is refused
/// rather than clamped. Every method takes the object's own <c>prefix</c> (empty at the root, <c>"Shield."</c> inside Shield) and
/// reports the field as prefix + name. A missing field returns the fallback; with no fallback it is an error. No game or Unity type.</summary>
public static class StrictJson
{
    /// <summary>The readable native name a package wrote for data block <paramref name="id"/> (the website's <c>nativeBlockName</c>:
    /// <c>&lt;author&gt;_&lt;work&gt;_&lt;number&gt;</c>). The game publishes the block under it and finds it by number; a block without
    /// one is refused, never renamed to its stable id.</summary>
    public static string BlockName(System.Text.Json.Nodes.JsonObject data, string id)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data["name"] is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name)
            || name.Length > 256)
            throw new System.IO.InvalidDataException("Block " + id + " has no readable name (1-256 characters).");
        return name;
    }

    /// <summary>Refuses a non-object, a member that is neither in <paramref name="known"/> nor in <paramref name="alsoAllowed"/>
    /// (such as <c>Type</c>), and a member written twice. <paramref name="owner"/> names what the object belongs to in the message.</summary>
    public static void OnlyKnown(JsonElement value, string prefix, string[] known, string[]? alsoAllowed = null, string? owner = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new StrictJsonException(prefix.TrimEnd('.'), "must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
        {
            if (Array.IndexOf(known, member.Name) < 0 && (alsoAllowed == null || Array.IndexOf(alsoAllowed, member.Name) < 0))
                throw new StrictJsonException(prefix + member.Name,
                    "unknown field" + (owner == null ? "" : " for " + owner) + " (known: " + string.Join(", ", known) + ").");
            if (!seen.Add(member.Name)) throw new StrictJsonException(prefix + member.Name, "duplicate field.");
        }
    }

    /// <summary>The first member of the object <paramref name="value"/> that is not in <paramref name="known"/> or is written twice, null when
    /// there is none. For a reader that keeps its own error codes and so cannot take <see cref="OnlyKnown"/>'s exception.</summary>
    public static string? FirstUnknownOrRepeated(JsonElement value, string[] known)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
            if (Array.IndexOf(known, member.Name) < 0 || !seen.Add(member.Name)) return member.Name;
        return null;
    }

    /// <summary>Refuses a member written twice anywhere inside <paramref name="value"/>, at any depth, naming it by path.</summary>
    public static void UniqueMembers(JsonElement value, string prefix = "")
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!seen.Add(member.Name)) throw new StrictJsonException(prefix + member.Name, "duplicate field.");
                UniqueMembers(member.Value, prefix + member.Name + ".");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) UniqueMembers(child, prefix);
    }
    /// <summary>A member that must be an object.</summary>
    public static JsonElement Object(JsonElement value, string prefix, string field)
        => value.TryGetProperty(field, out var found) && found.ValueKind == JsonValueKind.Object
            ? found : throw new StrictJsonException(prefix + field, "is required and must be an object.");

    /// <summary>True for a JSON number that is a finite double.</summary>
    public static bool Finite(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }

    /// <summary>A finite number from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static double Number(JsonElement value, string prefix, string field, double? fallback, double min, double max)
    {
        if (!value.TryGetProperty(field, out var found)) return fallback ?? throw new StrictJsonException(prefix + field, "is required.");
        if (!Finite(found, out var number)) throw new StrictJsonException(prefix + field, "must be a finite number.");
        return InRange(number, prefix + field, min, max);
    }

    /// <summary>Like <see cref="Number"/>, and the number must be whole.</summary>
    public static double Integer(JsonElement value, string prefix, string field, double? fallback, double min, double max)
    {
        if (!value.TryGetProperty(field, out var found)) return fallback ?? throw new StrictJsonException(prefix + field, "is required.");
        if (!Finite(found, out var number)) throw new StrictJsonException(prefix + field, "must be a finite number.");
        if (Math.Floor(number) != number) throw new StrictJsonException(prefix + field, "must be a whole number.");
        return InRange(number, prefix + field, min, max);
    }

    /// <summary>A string that is not empty or blank.</summary>
    public static string String(JsonElement value, string prefix, string field, string? fallback)
    {
        if (!value.TryGetProperty(field, out var found)) return fallback ?? throw new StrictJsonException(prefix + field, "is required.");
        if (found.ValueKind != JsonValueKind.String) throw new StrictJsonException(prefix + field, "must be a string.");
        var text = found.GetString()!;
        return string.IsNullOrWhiteSpace(text) ? throw new StrictJsonException(prefix + field, "must not be empty.") : text;
    }

    public static bool Bool(JsonElement value, string prefix, string field, bool? fallback)
    {
        if (!value.TryGetProperty(field, out var found)) return fallback ?? throw new StrictJsonException(prefix + field, "is required.");
        if (found.ValueKind is JsonValueKind.True or JsonValueKind.False) return found.GetBoolean();
        throw new StrictJsonException(prefix + field, "must be true or false.");
    }

    /// <summary>A required string naming exactly one member of <typeparamref name="T"/> (same spelling, no number).</summary>
    public static T Enum<T>(JsonElement value, string prefix, string field) where T : struct, System.Enum
    {
        var names = string.Join(", ", System.Enum.GetNames(typeof(T)));
        if (!value.TryGetProperty(field, out var found) || found.ValueKind != JsonValueKind.String)
            throw new StrictJsonException(prefix + field, "is required: one of " + names + ".");
        var text = found.GetString()!;
        if (!System.Enum.TryParse(text, false, out T parsed) || !System.Enum.IsDefined(typeof(T), parsed) || text != parsed.ToString())
            throw new StrictJsonException(prefix + field, "'" + text + "' is not one of " + names + ".");
        return parsed;
    }

    /// <summary>A required data-block reference: a persistentID (number) or a block name (non-empty string), exactly one of the two.</summary>
    public static (uint? Id, string? Name) Reference(JsonElement value, string prefix, string field)
    {
        if (!value.TryGetProperty(field, out var found)) throw new StrictJsonException(prefix + field, "is required (a persistentID number or a block name).");
        if (found.ValueKind == JsonValueKind.Number && found.TryGetUInt32(out var id)) return (id, null);
        if (found.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(found.GetString())) return (null, found.GetString());
        throw new StrictJsonException(prefix + field, "must be a persistentID number or a block name.");
    }

    private static double InRange(double number, string field, double min, double max)
        => number >= min && number <= max ? number
            : throw new StrictJsonException(field, number.ToString(CultureInfo.InvariantCulture) + " is outside "
                + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture) + ".");

    /// <summary>The value as JSON with object members sorted by name, no whitespace and every number in one spelling (the decimal
    /// coefficient and exponent are normalized without rounding), so equal content is equal text and hashes equal.</summary>
    public static string Canonical(JsonElement value)
    {
        var text = new StringBuilder();
        Write(text, value);
        return text.ToString();
    }

    private static void Write(StringBuilder text, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var members = new List<JsonProperty>();
                foreach (var member in value.EnumerateObject()) members.Add(member);
                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                text.Append('{');
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    text.Append(JsonSerializer.Serialize(members[i].Name)).Append(':');
                    Write(text, members[i].Value);
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (index++ > 0) text.Append(',');
                    Write(text, item);
                }
                text.Append(']');
                break;
            case JsonValueKind.Number:
                text.Append(CanonicalNumber(value.GetRawText()));
                break;
            case JsonValueKind.String:
                text.Append(JsonSerializer.Serialize(value.GetString()));
                break;
            default:
                text.Append(value.GetRawText());
                break;
        }
    }

    private static string CanonicalNumber(string raw)
    {
        var exponentIndex = raw.IndexOfAny(new[] { 'e', 'E' });
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var exponent = exponentIndex < 0 ? BigInteger.Zero
            : BigInteger.Parse(raw[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        if (point >= 0) exponent -= mantissa.Length - point - 1;
        var negative = mantissa[0] == '-';
        var digits = mantissa.Replace(".", "").TrimStart('-', '0');
        if (digits.Length == 0) return "0";
        var coefficient = digits.TrimEnd('0');
        exponent += digits.Length - coefficient.Length;
        return (negative ? "-" : "") + coefficient
            + (exponent.IsZero ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
    }
}
