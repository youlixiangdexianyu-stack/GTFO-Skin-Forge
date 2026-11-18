using System;
using System.Collections.Generic;

namespace ForgeUI;

/// <summary>One registered gear name: the text that stands for the gear in the game's own texts (its packet public name, or a text such
/// as a default description) and the language-file key that gives what this player's screens show instead.</summary>
internal sealed class GearNameRule
{
    private readonly Func<string> _text;
    private readonly List<string> _known = new();

    internal GearNameRule(object owner, string find, Func<string> text, bool byPacket)
    {
        Owner = owner;
        Find = find;
        _text = text;
        ByPacket = byPacket;
        _known.Add(find);
    }

    internal object Owner { get; }
    internal string Find { get; }

    /// <summary>The gear is also recognised by its packet public name (<see cref="Find"/>) in the local player's weapon slots.</summary>
    internal bool ByPacket { get; }

    /// <summary>The text in the current language, taken by <see cref="GearNameBook.Sync"/>.</summary>
    internal string Shown { get; private set; } = "";

    /// <summary>Every text that has meant this rule: <see cref="Find"/> and each language's text shown so far, so a screen drawn in an
    /// earlier language is translated too.</summary>
    internal IReadOnlyList<string> Known => _known;

    internal void AddKnown(string text) => _known.Add(text);

    internal void Take()
    {
        var text = _text();
        Shown = text;
        if (text.Length != 0 && !_known.Contains(text)) _known.Add(text);
    }
}

/// <summary>
/// The names mods registered for their gear (<see cref="UIKit.GearText"/>, <see cref="UIKit.GearName(UITexts, string, string)"/>) and the one rule for applying
/// them: every known text of a rule that a gear screen draws is replaced by the text of the rule's key in the current language. A
/// language change is noticed through <see cref="UITexts.Version"/>, so asking costs a number comparison and a text that holds no
/// registered text comes back as the same string without any allocation.
/// </summary>
internal sealed class GearNameBook
{
    private readonly List<GearNameRule> _rules = new();
    private readonly Dictionary<string, GearNameRule> _byFind = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GearNameRule> _byPacket = new(StringComparer.Ordinal);
    private int _version = -1;

    internal bool IsEmpty => _rules.Count == 0;

    /// <summary>True when a rule recognises gear by its packet public name.</summary>
    internal bool HasPackets => _byPacket.Count != 0;

    /// <summary>Registers <paramref name="find"/> to show <paramref name="key"/>'s text from <paramref name="texts"/>. The same owner
    /// registering the same text again keeps its rule (and what it has learnt of earlier languages); another owner's is refused.
    /// Returns the problem, or null when the rule is in.</summary>
    internal string? Add(object owner, string find, UITexts texts, string key, bool byPacket)
    {
        if (string.IsNullOrEmpty(key)) return "the language key is empty";
        return Add(owner, find, () => texts.Text(key), byPacket);
    }

    /// <summary>As the other overload, with the current-language text given by <paramref name="text"/>, which runs once per language change.</summary>
    internal string? Add(object owner, string find, Func<string> text, bool byPacket)
    {
        if (string.IsNullOrEmpty(find)) return "the text that stands for the gear is empty";
        if (_byFind.TryGetValue(find, out var held))
        {
            if (!ReferenceEquals(held.Owner, owner)) return "'" + find + "' is already registered by another mod";
            _rules.Remove(held);
            _byPacket.Remove(find);
        }
        var rule = new GearNameRule(owner, find, text, byPacket);
        if (held != null) foreach (var known in held.Known) if (known != find) rule.AddKnown(known);
        _rules.Add(rule);
        _byFind[find] = rule;
        if (byPacket) _byPacket[find] = rule;
        rule.Take();
        return null;
    }

    /// <summary>Takes every rule's text again when the language changed since the last call.</summary>
    internal void Sync()
    {
        if (_version == UITexts.Version) return;
        _version = UITexts.Version;
        foreach (var rule in _rules) rule.Take();
    }

    /// <summary><paramref name="text"/> with each registered text it holds replaced by the current language's; the same string when it
    /// holds none.</summary>
    internal string Display(string? text)
    {
        if (string.IsNullOrEmpty(text) || _rules.Count == 0) return text ?? "";
        Sync();
        foreach (var rule in _rules)
        {
            var now = rule.Shown;
            if (now.Length == 0 || text.Contains(now, StringComparison.Ordinal)) continue;
            var known = rule.Known;
            for (var i = 0; i < known.Count; i++)
                if (known[i] != now && text.Contains(known[i], StringComparison.Ordinal)) text = text.Replace(known[i], now, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>The rule for the gear whose packet public name is <paramref name="packetName"/>, or null.</summary>
    internal GearNameRule? ForPacket(string? packetName)
    {
        if (string.IsNullOrEmpty(packetName) || !_byPacket.TryGetValue(packetName, out var rule)) return null;
        Sync();
        return rule;
    }
}
