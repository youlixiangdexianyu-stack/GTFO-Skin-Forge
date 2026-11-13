using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ForgeCore;

namespace ForgeUI;

/// <summary>
/// One set of language files: <c>&lt;code&gt;.forge</c> files in a folder (usually your plugin's <c>lang</c> folder), each a JSON
/// object of key → text, where a text may hold <c>{0}</c>, <c>{1}</c>… placeholders. All files are read and checked the first time
/// a text is asked for; after that a text is a dictionary lookup that returns the cached string. Take your mod's set with
/// <see cref="UIKitMod.Texts"/> (or use <see cref="UIKitMod.Text(string)"/>), a set in another folder with
/// <see cref="UIKit.Texts(string)"/>.
/// </summary>
public sealed class UITexts
{
    /// <summary>The language every set falls back to, and the one the others are checked against.</summary>
    public const string Reference = "en";

    private static readonly Dictionary<string, string> Empty = new(StringComparer.Ordinal);

    private readonly string _owner;
    private readonly Func<IReadOnlyList<KeyValuePair<string, string>>> _files;
    private readonly Action<string> _error;
    private Dictionary<string, Dictionary<string, string>>? _tables;
    private Dictionary<string, string> _current = Empty, _near = Empty, _reference = Empty;
    private readonly OnceReporter _once;
    private int _builtFor = -1;

    /// <param name="owner">Names the set in the log (mod ID and folder).</param>
    /// <param name="files">Returns every language file as (file name, contents); read once, on first use.</param>
    /// <param name="error">Logs a problem (a missing text, a bad file, a mismatch between languages).</param>
    internal UITexts(string owner, Func<IReadOnlyList<KeyValuePair<string, string>>> files, Action<string> error)
    {
        _owner = owner;
        _files = files;
        _error = error;
        _once = new OnceReporter(error);
    }

    // ---- The current language, shared by every set ------------------------------------------------------------------------

    /// <summary>The current language code (lower case, for example <c>zh</c> or <c>en</c>).</summary>
    public static string Language { get; private set; } = Reference;

    /// <summary>Changes every time <see cref="Language"/> changes; texts cached with an older value are stale.</summary>
    public static int Version { get; private set; }

    /// <summary>Raised on the main thread after <see cref="Language"/> changed.</summary>
    internal static event Action? Changed;

    /// <summary>Makes <paramref name="code"/> the current language; true when it changed.</summary>
    internal static bool SetLanguage(string code)
    {
        code = Normalize(code);
        if (code.Length == 0 || code == Language) return false;
        Language = code;
        Version++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>A language code as files and settings name it: trimmed, lower case.</summary>
    internal static string Normalize(string? code) => (code ?? "").Trim().ToLowerInvariant();

    /// <summary>The language code for the game's own language setting (the name of its <c>Localization.Language</c> value), or
    /// null for a value this list does not know.</summary>
    internal static string? GameLanguageCode(string gameLanguage) => gameLanguage switch
    {
        "English" => "en",
        "French" => "fr",
        "Italian" => "it",
        "German" => "de",
        "Spanish" => "es",
        "Russian" => "ru",
        "Portuguese_Brazil" => "pt-br",
        "Polish" => "pl",
        "Japanese" => "ja",
        "Korean" => "ko",
        "Chinese_Traditional" => "zh-hant",
        "Chinese_Simplified" => "zh",
        _ => null
    };

    // ---- Lookup -----------------------------------------------------------------------------------------------------------

    /// <summary>The languages this set has files for, after the first lookup.</summary>
    public IReadOnlyCollection<string> Languages => (IReadOnlyCollection<string>?)_tables?.Keys ?? Array.Empty<string>();

    /// <summary>The text of <paramref name="key"/> in the current language; the <c>en</c> text (and one logged error) when the
    /// current language lacks it; the key itself (and one logged error) when <c>en</c> lacks it too.</summary>
    public string Text(string key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (_builtFor != Version) Build();
        if (_current.TryGetValue(key, out var text)) return text;
        if (Fallback(key, out text)) return text;
        _once.Report(key, (o: _owner, k: key, l: Language, r: Reference), static s => s.o + ": '" + s.k + "' has no text in " + s.l + " or " + s.r + "; showing the key.");
        return key;
    }

    /// <summary><see cref="Text(string)"/> with its placeholders filled from <paramref name="args"/> (invariant culture). A text whose
    /// placeholders do not fit the arguments is shown unfilled, with one logged error.</summary>
    public string Text(string key, params object?[] args)
    {
        var text = Text(key);
        if (args == null || args.Length == 0) return text;
        try { return string.Format(CultureInfo.InvariantCulture, text, args); }
        catch (FormatException)
        {
            _once.Report("\u0001" + key, (o: _owner, k: key, l: Language, n: args.Length, t: text), static s => s.o + ": '" + s.k + "' (" + s.l + ") does not fit " + s.n + " argument(s): " + s.t);
            return text;
        }
    }

    /// <summary>Like <see cref="Text(string)"/>, but false (nothing logged) when no language of the set has <paramref name="key"/>:
    /// for optional texts such as translations of author-written names.</summary>
    public bool TryText(string key, out string text)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (_builtFor != Version) Build();
        if (_current.TryGetValue(key, out text!)) return true;
        return Fallback(key, out text);
    }

    /// <summary>The texts after the current language: its near relative (<c>zh</c> for <c>zh-hant</c>, <c>pt</c> for <c>pt-br</c>), then
    /// <c>en</c>; each fallback logged once per key.</summary>
    private bool Fallback(string key, out string text)
    {
        if (_near.TryGetValue(key, out text!))
        {
            _once.Report(key, (o: _owner, k: key, l: Language), static s => s.o + ": '" + s.k + "' has no " + s.l + " text; showing the " + Relative(s.l) + " text.");
            return true;
        }
        if (!_reference.TryGetValue(key, out text!)) return false;
        _once.Report(key, (o: _owner, k: key, l: Language, r: Reference), static s => s.o + ": '" + s.k + "' has no " + s.l + " text; showing the " + s.r + " text.");
        return true;
    }

    /// <summary>The same language without its region or script (<c>zh</c> for <c>zh-hant</c>, <c>pt</c> for <c>pt-br</c>); empty when the
    /// code has none.</summary>
    internal static string Relative(string code)
    {
        var dash = code.IndexOf('-');
        return dash > 0 ? code.Substring(0, dash) : "";
    }

    private void Build()
    {
        if (_tables == null) Load();
        _builtFor = Version;
        _once.Reset();
        _reference = _tables!.TryGetValue(Reference, out var reference) ? reference : Empty;
        var relative = Relative(Language);
        _near = relative.Length > 0 && relative != Reference && _tables.TryGetValue(relative, out var near) ? near : Empty;
        if (_tables.TryGetValue(Language, out var current)) { _current = current; return; }
        if (_near.Count > 0)
        {
            _current = _near;
            _near = Empty;
            return;
        }
        _current = _reference;
    }

    // ---- Loading and checking ---------------------------------------------------------------------------------------------

    private void Load()
    {
        _tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        IReadOnlyList<KeyValuePair<string, string>> files;
        try { files = _files(); }
        catch (Exception error)
        {
            _error(_owner + ": language files could not be read: " + error);
            return;
        }
        foreach (var (file, contents) in files)
        {
            var code = Normalize(System.IO.Path.GetFileNameWithoutExtension(file));
            if (code.Length == 0) continue;
            var table = Parse(file, contents);
            if (table != null) _tables[code] = table;
        }
        Check();
    }

    /// <summary>One file's keys and texts, or null (logged) when it is not a JSON object of strings. A repeated key is logged and
    /// its first text kept.</summary>
    private Dictionary<string, string>? Parse(string file, string contents)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(contents.TrimStart('﻿')),
                new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("the file must be one JSON object of key → text");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = reader.GetString()!;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new JsonException("'" + key + "' must be a text (a JSON string)");
                var text = reader.GetString()!;
                if (!table.TryAdd(key, text)) _error(_owner + " " + file + ": '" + key + "' appears more than once; the first text is used.");
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("the object is not closed");
            return table;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            _error(_owner + " " + file + ": not a valid language file, so it is not used: " + error);
            return null;
        }
    }

    /// <summary>Logs every language's keys missing from, or extra to, <c>en</c>, and texts whose placeholders differ from the
    /// <c>en</c> text of the same key.</summary>
    private void Check()
    {
        var tables = _tables!;
        if (tables.Count == 0) return;
        if (!tables.TryGetValue(Reference, out var reference))
        {
            _error(_owner + ": no " + Reference + ".json, the language every other falls back to; a missing text shows its key.");
            return;
        }
        foreach (var (code, table) in tables)
        {
            if (code == Reference) continue;
            var missing = new List<string>();
            var extra = new List<string>();
            foreach (var key in reference.Keys) if (!table.ContainsKey(key)) missing.Add(key);
            foreach (var (key, text) in table)
            {
                if (!reference.TryGetValue(key, out var english)) { extra.Add(key); continue; }
                var ours = Placeholders(text);
                var theirs = Placeholders(english);
                if (ours != theirs)
                    _error(_owner + " " + code + ".json: '" + key + "' has placeholders " + Describe(ours) + " but " + Reference + ".json has " + Describe(theirs) + ".");
            }
            if (missing.Count > 0) _error(_owner + " " + code + ".json: missing " + missing.Count + " text(s) that " + Reference + ".json has: " + string.Join(", ", missing) + ".");
            if (extra.Count > 0) _error(_owner + " " + code + ".json: " + extra.Count + " text(s) that " + Reference + ".json does not have: " + string.Join(", ", extra) + ".");
        }
    }

    /// <summary>The placeholder numbers used in <paramref name="text"/> as bits (<c>{0}</c> is bit 0); <c>{{</c> and <c>}}</c> are
    /// literal braces. Format and alignment parts (<c>{0:0.0}</c>, <c>{1,5}</c>) count as their number.</summary>
    internal static long Placeholders(string text)
    {
        long used = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{') continue;
            if (i + 1 < text.Length && text[i + 1] == '{') { i++; continue; }
            var j = i + 1;
            var number = 0;
            while (j < text.Length && char.IsDigit(text[j]) && number < 63) number = number * 10 + (text[j++] - '0');
            if (j > i + 1 && j < text.Length && (text[j] == '}' || text[j] == ':' || text[j] == ',')) used |= 1L << number;
            i = j - 1;
        }
        return used;
    }

    private static string Describe(long placeholders)
    {
        if (placeholders == 0) return "none";
        var parts = new List<string>();
        for (var i = 0; i < 64; i++) if ((placeholders & (1L << i)) != 0) parts.Add("{" + i + "}");
        return string.Join(" ", parts);
    }
}

/// <summary>
/// A text given to ForgeUI: either plain (shown as written; a <see cref="string"/> converts to one) or a key from your language
/// files (<see cref="Key"/>), which ForgeUI looks up in the language files of the mod that registered it and sets again whenever
/// the language changes.
/// </summary>
public sealed class UIText
{
    private readonly string? _plain;
    private readonly string? _key;
    private readonly object?[] _args;
    private UITexts? _cachedFor;
    private int _cachedVersion = -1;
    private string _cached = "";

    private UIText(string? plain, string? key, object?[] args)
    {
        _plain = plain;
        _key = key;
        _args = args;
    }

    /// <summary>The empty plain text.</summary>
    public static readonly UIText Empty = new("", null, Array.Empty<object?>());

    /// <summary>The text of <paramref name="key"/> in the current language, its placeholders filled from <paramref name="args"/>.</summary>
    public static UIText Key(string key, params object?[] args)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("A text key must not be empty.", nameof(key));
        return new UIText(null, key, args ?? Array.Empty<object?>());
    }

    /// <summary><paramref name="text"/> shown as written.</summary>
    public static UIText Plain(string? text) => string.IsNullOrEmpty(text) ? Empty : new UIText(text, null, Array.Empty<object?>());

    public static implicit operator UIText(string? text) => Plain(text);

    /// <summary>True for a key from the language files.</summary>
    public bool IsKey => _key != null;

    /// <summary>The key, or the plain text.</summary>
    public override string ToString() => _key ?? _plain!;

    /// <summary>The text to show, looked up in <paramref name="texts"/> for a key. The result is cached until the language changes,
    /// so calling this every frame costs a comparison.</summary>
    internal string In(UITexts? texts)
    {
        if (_plain != null) return _plain;
        if (texts == null) return _key!;
        if (ReferenceEquals(texts, _cachedFor) && _cachedVersion == UITexts.Version) return _cached;
        _cached = _args.Length == 0 ? texts.Text(_key!) : texts.Text(_key!, _args);
        _cachedFor = texts;
        _cachedVersion = UITexts.Version;
        return _cached;
    }
}
