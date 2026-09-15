using MnmDamageParser.Core.Config;

namespace MnmDamageParser.Core.Parser;

/// <summary>A single running DoT/debuff timer, for the timers window.</summary>
public sealed record DebuffTimer(
    string Spell, string Target, string Kind,
    double AppliedAt, double ExpiresAt, double LastTickAt)
{
    public double DurationSeconds => Math.Max(ExpiresAt - AppliedAt, 0.001);
    public double RemainingSeconds(double now) => ExpiresAt - now;
    public double Fraction(double now) => Math.Clamp((ExpiresAt - now) / DurationSeconds, 0.0, 1.0);
    public bool Expired(double now) => now >= ExpiresAt;
}

/// <summary>
/// Tracks the DoTs / debuffs YOU have on things, keyed by (spell, target),
/// so the timers window can count down to expiry.
///
/// The game logs no cast line and no reliable "wears off" line, so a timer
/// is started/refreshed from the spell's damage ticks: the first tick of a
/// tracked spell on a target (or the first after a gap of &gt; ~2 tick
/// intervals, or after the previous timer already expired) is treated as a
/// fresh application and the countdown is set to the spell's configured
/// duration. Ticks in between don't extend it. A kill clears that target's
/// timers; expired timers linger a few seconds (greyed) then drop.
///
/// Not thread-safe -- MeterState owns one and calls it under its lock.
/// </summary>
public sealed class DebuffTracker
{
    private const double ExpiredGraceSeconds = 6.0;
    private const double RefreshGapTicks = 2.0;

    private readonly IReadOnlyDictionary<string, SpellInfo> _spells;

    private sealed class Entry
    {
        public string Spell = "";
        public string Target = "";
        public string Kind = "dot";
        public double AppliedAt;
        public double ExpiresAt;
        public double LastTickAt;
    }

    private readonly Dictionary<(string Spell, string Target), Entry> _active = new();

    public DebuffTracker(IReadOnlyDictionary<string, SpellInfo> spells) => _spells = spells;

    public bool IsEmpty => _active.Count == 0;

    /// <summary>Feed every damage event whose source is you.</summary>
    public void OnYourDamage(string? ability, string? target, double timestamp)
    {
        if (string.IsNullOrEmpty(ability) || string.IsNullOrEmpty(target) || target == "?") return;
        if (!_spells.TryGetValue(ability, out var info) || info.DurationSeconds <= 0) return;

        var key = (ability, target!);
        if (_active.TryGetValue(key, out var e))
        {
            var fresh = timestamp >= e.ExpiresAt
                        || timestamp - e.LastTickAt > info.TickSeconds * RefreshGapTicks;
            if (fresh)
            {
                e.AppliedAt = timestamp;
                e.ExpiresAt = timestamp + info.DurationSeconds;
            }
            e.LastTickAt = timestamp;
        }
        else
        {
            _active[key] = new Entry
            {
                Spell = ability!,
                Target = target!,
                Kind = string.IsNullOrWhiteSpace(info.Kind) ? "dot" : info.Kind,
                AppliedAt = timestamp,
                ExpiresAt = timestamp + info.DurationSeconds,
                LastTickAt = timestamp,
            };
        }
    }

    /// <summary>A target died -- its timers are gone.</summary>
    public void OnTargetDown(string? target)
    {
        if (string.IsNullOrEmpty(target)) return;
        var stale = _active.Keys.Where(k => string.Equals(k.Target, target, StringComparison.Ordinal)).ToList();
        foreach (var k in stale) _active.Remove(k);
    }

    public void Clear() => _active.Clear();

    /// <summary>Active timers, expiring-soonest first. Drops entries that
    /// have been expired longer than the grace period.</summary>
    public IReadOnlyList<DebuffTimer> Snapshot(double now)
    {
        var drop = _active.Where(kv => now - kv.Value.ExpiresAt > ExpiredGraceSeconds)
                          .Select(kv => kv.Key).ToList();
        foreach (var k in drop) _active.Remove(k);

        return _active.Values
            .Select(e => new DebuffTimer(e.Spell, e.Target, e.Kind, e.AppliedAt, e.ExpiresAt, e.LastTickAt))
            .OrderBy(t => t.ExpiresAt)
            .ToList();
    }
}
