using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ForgeVisual;

/// <summary>
/// A look object of this library (<see cref="BeamStyle"/>, <see cref="TrailStyle"/> with its <see cref="TrailPuff"/>s) as one line of text and back, so a
/// recorder can keep how a beam or a trail was drawn and a replay can draw it again without the mod that made the look running its code. Every public
/// read-write property goes in: numbers, flags, enums, colours, vectors, sprite sheets by name and arrays of looks; a property of any other kind (a particle
/// channel) is left out and stays at its default when read back. Both ways run once per look, not per frame.
/// </summary>
public static class LookText
{
    private static readonly Dictionary<Type, PropertyInfo[]> Properties = new();
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The look as text: <c>{Name=value;...}</c>.</summary>
    public static string Write(object look)
    {
        var text = new StringBuilder();
        Write(text, look ?? throw new ArgumentNullException(nameof(look)));
        return text.ToString();
    }

    /// <summary>A new <typeparamref name="T"/> with the values <paramref name="text"/> holds; null when the text is not a look's (a property it names that
    /// <typeparamref name="T"/> lacks is skipped, a sheet that is not registered is left empty).</summary>
    public static T? Read<T>(string text) where T : class, new()
    {
        try
        {
            var at = 0;
            return (T?)Read(typeof(T), text, ref at);
        }
        catch (Exception error) when (error is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            Plugin.Logger.ErrorOnce("visual-look-text-unreadable", "ForgeVisual: a " + typeof(T).Name + " look could not be read back: " + error.Message);
            return null;
        }
    }

    private static PropertyInfo[] Of(Type type)
    {
        if (Properties.TryGetValue(type, out var known)) return known;
        var list = new List<PropertyInfo>();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetSetMethod() != null) list.Add(p);
        return Properties[type] = list.ToArray();
    }

    private static void Write(StringBuilder text, object look)
    {
        text.Append('{');
        foreach (var p in Of(look.GetType()))
        {
            var value = p.GetValue(look);
            var start = text.Length;
            text.Append(p.Name).Append('=');
            if (!Value(text, value)) { text.Length = start; continue; }
            text.Append(';');
        }
        text.Append('}');
    }

    private static bool Value(StringBuilder text, object? value)
    {
        switch (value)
        {
            case null: return false;
            case float f: text.Append(f.ToString("R", Inv)); return true;
            case int i: text.Append(i.ToString(Inv)); return true;
            case bool b: text.Append(b ? '1' : '0'); return true;
            case Enum e: text.Append(Convert.ToInt32(e, Inv).ToString(Inv)); return true;
            case Color c: text.Append(c.r.ToString("R", Inv)).Append(',').Append(c.g.ToString("R", Inv)).Append(',').Append(c.b.ToString("R", Inv)).Append(',').Append(c.a.ToString("R", Inv)); return true;
            case Vector3 v: text.Append(v.x.ToString("R", Inv)).Append(',').Append(v.y.ToString("R", Inv)).Append(',').Append(v.z.ToString("R", Inv)); return true;
            case SpriteSheet s: text.Append('@').Append(s.Name); return true;
            case Array a when a.GetType().GetElementType() is { IsClass: true } element && element.Namespace == typeof(LookText).Namespace:
                text.Append('[');
                foreach (var item in a) if (item != null) Write(text, item);
                text.Append(']');
                return true;
            default: return false;
        }
    }

    private static object Read(Type type, string text, ref int at)
    {
        Expect(text, ref at, '{');
        var look = Activator.CreateInstance(type)!;
        var properties = Of(type);
        while (text[at] != '}')
        {
            var equals = text.IndexOf('=', at);
            var name = text.Substring(at, equals - at);
            at = equals + 1;
            var property = Array.Find(properties, p => p.Name == name);
            if (property != null && property.PropertyType.IsArray)
            {
                var element = property.PropertyType.GetElementType()!;
                Expect(text, ref at, '[');
                var items = new List<object>();
                while (text[at] != ']') items.Add(Read(element, text, ref at));
                at++;
                var array = Array.CreateInstance(element, items.Count);
                for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                property.SetValue(look, array);
            }
            else if (text[at] == '[')
            {
                // an array this type does not have: passed over whole
                for (var depth = 0; ; at++)
                {
                    if (text[at] == '[') depth++;
                    else if (text[at] == ']' && --depth == 0) { at++; break; }
                }
            }
            else
            {
                var end = text.IndexOf(';', at);
                var raw = text.Substring(at, end - at);
                at = end;
                if (property != null && Parse(property.PropertyType, raw) is { } value) property.SetValue(look, value);
            }
            Expect(text, ref at, ';');
        }
        at++;
        return look;
    }

    private static object? Parse(Type type, string raw)
    {
        if (type == typeof(float)) return float.Parse(raw, Inv);
        if (type == typeof(int)) return int.Parse(raw, Inv);
        if (type == typeof(bool)) return raw == "1";
        if (type.IsEnum) return Enum.ToObject(type, int.Parse(raw, Inv));
        if (type == typeof(SpriteSheet) && raw.StartsWith("@", StringComparison.Ordinal)) return SpriteSheet.Get(raw.Substring(1));
        var parts = raw.Split(',');
        if (type == typeof(Color) && parts.Length == 4) return new Color(float.Parse(parts[0], Inv), float.Parse(parts[1], Inv), float.Parse(parts[2], Inv), float.Parse(parts[3], Inv));
        if (type == typeof(Vector3) && parts.Length == 3) return new Vector3(float.Parse(parts[0], Inv), float.Parse(parts[1], Inv), float.Parse(parts[2], Inv));
        return null;
    }

    private static void Expect(string text, ref int at, char c)
    {
        if (text[at] != c) throw new FormatException("expected '" + c + "' at " + at);
        at++;
    }
}
