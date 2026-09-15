namespace MnmDamageParser.Core.Parser;

/// <summary>One combatant's running totals within one encounter -- hits,
/// misses, heals, and a per-ability breakdown of each. Not thread-safe on
/// its own; callers (Encounter/MeterState) are responsible for
/// synchronizing access.</summary>
public sealed class ActorStats
{
    public const string UnknownAbility = "(melee/unknown)";

    public string Name { get; }
    public int TotalDamage { get; private set; }
    public int TotalHealing { get; private set; }
    public int Hits { get; private set; }
    public int Crits { get; private set; }
    public int Misses { get; private set; }
    public int Heals { get; private set; }
    public double? FirstSeen { get; private set; }
    public double? LastSeen { get; private set; }

    // (timestamp, amount) pairs for damage, pruned to the rolling window
    private readonly Queue<(double Timestamp, int Amount)> _damageWindow = new();

    public Dictionary<string, AbilityStats> Abilities { get; } = new();
    public Dictionary<string, AbilityStats> HealAbilities { get; } = new();

    public ActorStats(string name) => Name = name;

    public void AddDamage(int amount, double timestamp, bool isCrit, string? ability, string? school)
    {
        TotalDamage += amount;
        Hits++;
        if (isCrit) Crits++;
        _damageWindow.Enqueue((timestamp, amount));
        GetOrAddAbility(Abilities, ability).AddHit(amount, isCrit, school);
        Touch(timestamp);
    }

    public void AddMiss(double timestamp, string? ability)
    {
        Misses++;
        GetOrAddAbility(Abilities, ability).AddMiss();
        Touch(timestamp);
    }

    public void AddHeal(int amount, double timestamp, string? ability)
    {
        TotalHealing += amount;
        Heals++;
        GetOrAddAbility(HealAbilities, ability).AddHit(amount, false, null);
        Touch(timestamp);
    }

    private static AbilityStats GetOrAddAbility(Dictionary<string, AbilityStats> bucket, string? ability)
    {
        var key = string.IsNullOrEmpty(ability) ? UnknownAbility : ability;
        if (!bucket.TryGetValue(key, out var stats))
        {
            stats = new AbilityStats(key);
            bucket[key] = stats;
        }
        return stats;
    }

    private void Touch(double timestamp)
    {
        FirstSeen ??= timestamp;
        LastSeen = timestamp;
    }

    /// <summary>Rehydrates already-computed totals loaded from disk
    /// (EncounterHistoryStore) -- bypasses AddDamage/AddMiss/AddHeal since
    /// this is restoring a saved aggregate, not replaying individual
    /// events. Note the rolling (last-10-seconds) damage window used by
    /// RollingDps is NOT restored -- that's per-event timestamp data this
    /// format doesn't keep, so a reloaded encounter's rolling DPS reads as
    /// 0 rather than a real recent-window rate. EncounterDps (the
    /// overall-average figure) is unaffected since it's derived from
    /// FirstSeen/LastSeen/TotalDamage, all of which ARE restored.</summary>
    internal void RestoreTotals(int totalDamage, int totalHealing, int hits, int crits, int misses, int heals,
        double? firstSeen, double? lastSeen)
    {
        TotalDamage = totalDamage;
        TotalHealing = totalHealing;
        Hits = hits;
        Crits = crits;
        Misses = misses;
        Heals = heals;
        FirstSeen = firstSeen;
        LastSeen = lastSeen;
    }

    /// <summary>Accumulate another actor's per-encounter totals into this
    /// one -- the building block for the lifetime "overall" aggregate. The
    /// rolling damage window is NOT merged (per-hit timestamps aren't kept
    /// across encounters); overall DPS in that view is TotalDamage over the
    /// summed combat time, set by the caller via <see cref="SetWindow"/>.</summary>
    internal void AddFrom(ActorStats o)
    {
        TotalDamage += o.TotalDamage;
        TotalHealing += o.TotalHealing;
        Hits += o.Hits;
        Crits += o.Crits;
        Misses += o.Misses;
        Heals += o.Heals;
        foreach (var (key, ab) in o.Abilities) GetOrAddAbility(Abilities, key).AddFrom(ab);
        foreach (var (key, ab) in o.HealAbilities) GetOrAddAbility(HealAbilities, key).AddFrom(ab);
    }

    /// <summary>Pin FirstSeen/LastSeen so <see cref="EncounterDps"/> divides
    /// TotalDamage by a caller-supplied span (the lifetime combat time)
    /// rather than a real per-hit interval.</summary>
    internal void SetWindow(double? firstSeen, double? lastSeen)
    {
        FirstSeen = firstSeen;
        LastSeen = lastSeen;
    }

    public void PruneWindow(double now, double windowSeconds)
    {
        while (_damageWindow.Count > 0 && now - _damageWindow.Peek().Timestamp > windowSeconds)
            _damageWindow.Dequeue();
    }

    public double RollingDps(double now, double windowSeconds)
    {
        PruneWindow(now, windowSeconds);
        if (_damageWindow.Count == 0) return 0.0;
        var oldest = _damageWindow.Peek().Timestamp;
        var span = Math.Min(windowSeconds, now - oldest);
        if (span <= 0) span = windowSeconds;
        var total = 0;
        foreach (var (_, amount) in _damageWindow) total += amount;
        return total / Math.Max(span, 0.001);
    }

    public double EncounterDps(double now)
    {
        if (FirstSeen is null) return 0.0;
        var duration = Math.Max(now - FirstSeen.Value, 0.001);
        return TotalDamage / duration;
    }

    public double Accuracy()
    {
        var attempts = Hits + Misses;
        return attempts > 0 ? (double)Hits / attempts * 100.0 : 0.0;
    }

    public List<AbilityRow> AbilityRows(bool healing = false)
    {
        var source = healing ? HealAbilities : Abilities;
        var total = healing ? TotalHealing : TotalDamage;
        if (total == 0) total = 1;
        var rows = source.Values.Select(a => new AbilityRow(
            a.Name, a.School, a.Count, a.Crits, a.Misses, a.Total, a.Avg,
            a.MinHit ?? 0, a.MaxHit ?? 0, (double)a.Total / total * 100.0
        )).ToList();
        rows.Sort((x, y) => y.Total.CompareTo(x.Total));
        return rows;
    }
}
