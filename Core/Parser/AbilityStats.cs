namespace MnmDamageParser.Core.Parser;

/// <summary>One ability/spell's running totals within one actor's damage
/// (or healing) breakdown -- the equivalent of ACT's "damage by spell"
/// row for a single combatant.</summary>
public sealed class AbilityStats
{
    public string Name { get; }
    public string? School { get; private set; }
    public int Count { get; private set; }
    public int Crits { get; private set; }
    public int Misses { get; private set; }
    public int Total { get; private set; }
    public int? MinHit { get; private set; }
    public int? MaxHit { get; private set; }

    public AbilityStats(string name) => Name = name;

    public void AddHit(int amount, bool isCrit, string? school)
    {
        Count++;
        if (isCrit) Crits++;
        Total += amount;
        MinHit = MinHit is null ? amount : Math.Min(MinHit.Value, amount);
        MaxHit = MaxHit is null ? amount : Math.Max(MaxHit.Value, amount);
        if (!string.IsNullOrEmpty(school) && string.IsNullOrEmpty(School))
            School = school;
    }

    public void AddMiss() => Misses++;

    /// <summary>Accumulate another AbilityStats' totals into this one (used
    /// to build the lifetime "overall" aggregate from per-encounter data).</summary>
    internal void AddFrom(AbilityStats o)
    {
        Count += o.Count;
        Crits += o.Crits;
        Misses += o.Misses;
        Total += o.Total;
        if (o.MinHit is { } omin) MinHit = MinHit is null ? omin : Math.Min(MinHit.Value, omin);
        if (o.MaxHit is { } omax) MaxHit = MaxHit is null ? omax : Math.Max(MaxHit.Value, omax);
        if (string.IsNullOrEmpty(School) && !string.IsNullOrEmpty(o.School)) School = o.School;
    }

    public double Avg => Count > 0 ? (double)Total / Count : 0.0;

    /// <summary>Rehydrates an already-computed total loaded from disk
    /// (EncounterHistoryStore) -- bypasses AddHit/AddMiss since this is
    /// restoring a saved aggregate, not replaying individual events.</summary>
    internal void Restore(int count, int crits, int misses, int total, int? minHit, int? maxHit, string? school)
    {
        Count = count;
        Crits = crits;
        Misses = misses;
        Total = total;
        MinHit = minHit;
        MaxHit = maxHit;
        School = school;
    }
}

/// <summary>Read-only snapshot of one AbilityStats, for display.</summary>
public sealed record AbilityRow(
    string Name, string? School, int Count, int Crits, int Misses,
    int Total, double Avg, int MinHit, int MaxHit, double Percent);
