using System.Text.RegularExpressions;
using MnmDamageParser.Core.Config;

namespace MnmDamageParser.Core.CombatLog;

/// <summary>
/// Routes a non-combat chat line to a category from <c>config/chat.json</c>.
/// First category (in file order) with any of its regexes matching wins;
/// no match -> <see cref="Other"/>. With no configured categories every
/// line is <see cref="Other"/> and callers collapse to one combined window.
/// </summary>
public sealed class ChatCategorizer
{
    /// <summary>Where lines that match no configured category go. Always
    /// the last entry in <see cref="CategoryNames"/> when any category is
    /// configured, so the "what am I missing?" window is always present.</summary>
    public const string Other = "Other";

    private readonly (string Name, Regex[] Patterns)[] _rules;
    private readonly Regex[] _deny;

    /// <summary>Category window names, in the order they should be shown:
    /// config order, then "Other". Empty config -> just ["Other"].</summary>
    public IReadOnlyList<string> CategoryNames { get; }

    /// <summary>True when chat.json actually configured categories -- i.e.
    /// the caller should open per-category windows rather than one combined
    /// window.</summary>
    public bool IsSplit => _rules.Length > 0;

    public ChatCategorizer(ChatConfig config)
    {
        _deny = config.Deny
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(SafeRegex).Where(r => r is not null).Select(r => r!).ToArray();

        _rules = config.Categories
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && c.Match.Count > 0)
            .Select(c => (
                c.Name.Trim(),
                c.Match
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => SafeRegex(p))
                    .Where(r => r is not null)
                    .Select(r => r!)
                    .ToArray()))
            .Where(r => r.Item2.Length > 0)
            .ToArray();

        var names = _rules.Select(r => r.Name).ToList();
        if (_rules.Length > 0) names.Add(Other);
        else names.Add(Other); // combined case still resolves everything to "Other"
        CategoryNames = names;
    }

    /// <summary>True if the line should not appear in any Chat window
    /// (matched a <c>deny</c> regex). Checked before <see cref="Classify"/>.</summary>
    public bool IsDenied(string text)
    {
        foreach (var rx in _deny)
            if (rx.IsMatch(text))
                return true;
        return false;
    }

    public string Classify(string text)
    {
        foreach (var (name, patterns) in _rules)
            foreach (var rx in patterns)
                if (rx.IsMatch(text))
                    return name;
        return Other;
    }

    private static Regex? SafeRegex(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return null; } // a typo'd rule shouldn't crash the app
    }
}
