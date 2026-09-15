using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnmDamageParser.Core.Config;

/// <summary>One tracked spell: how long its effect lasts and how often it
/// ticks. The meter has no access to the game's spell data, so these are
/// filled in by hand (from the in-game spellbook) in
/// <c>config/spells.json</c>. A <c>duration_seconds</c> of 0 means "don't
/// track" (e.g. a plain nuke).</summary>
public sealed class SpellInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>How long the DoT / debuff lasts on the target, in seconds.
    /// 0 = not tracked.</summary>
    [JsonPropertyName("duration_seconds")] public double DurationSeconds { get; set; }

    /// <summary>Seconds between damage ticks -- used to tell a "still
    /// ticking" line from a fresh re-cast. Ignored for pure (no-damage)
    /// debuffs. Default 6.</summary>
    [JsonPropertyName("tick_seconds")] public double TickSeconds { get; set; } = 6.0;

    /// <summary>Display label only: "dot" or "debuff".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "dot";
}

/// <summary><c>config/spells.json</c>: the DoT / debuff durations the
/// timers window counts down. Edit it by hand; a missing file just means
/// nothing is tracked.</summary>
public sealed class SpellConfig
{
    [JsonPropertyName("_readme")] public string? Readme { get; set; }
    [JsonPropertyName("spells")] public List<SpellInfo> Spells { get; set; } = new();

    /// <summary>Name → info, only the entries worth tracking
    /// (duration &gt; 0), case-insensitive on the spell name.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, SpellInfo> Tracked =>
        _tracked ??= Spells
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && s.DurationSeconds > 0)
            .GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, SpellInfo>? _tracked;

    public static SpellConfig Load(string path)
    {
        if (!File.Exists(path)) return new SpellConfig();
        try
        {
            return JsonSerializer.Deserialize<SpellConfig>(File.ReadAllText(path), JsonOpts.Default)
                   ?? new SpellConfig();
        }
        catch
        {
            return new SpellConfig();
        }
    }
}
