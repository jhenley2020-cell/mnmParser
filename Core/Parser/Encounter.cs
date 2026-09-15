namespace MnmDamageParser.Core.Parser;

/// <summary>Read-only snapshot of one combatant's row in the combatant
/// table (damage or healing view).</summary>
public sealed record MeterRow(
    string Name, int TotalDamage, double Dps, double RollingDps,
    double Percent, int Crits, int Hits, int Misses, double Accuracy);

/// <summary>Header figures for the "Overall" / Totals window -- see
/// MeterState.GetLifetimeInfo.</summary>
public sealed record LifetimeInfo(
    DateTime Since, int Encounters, int Kills, double CombatSeconds, long TotalDamage, long TotalHealing);

/// <summary>Immutable, fully-copied snapshot of one Encounter for the
/// encounter-list UI -- see MeterState.GetEncounterSummaries.</summary>
public sealed record EncounterSummary(int Id, double StartTime, double? EndTime, bool IsActive, string Who, int TotalDamage)
{
    public double Duration(double? now = null)
    {
        var end = EndTime ?? now ?? Clock.NowSeconds();
        return Math.Max(end - StartTime, 0.0);
    }
}

/// <summary>One discrete fight: every combatant (you, party, enemies --
/// whoever the scanner attributed a damage/heal/miss line to) that was
/// active between StartTime and EndTime. EndTime is null while the
/// encounter is still live. Mutation is only ever done by MeterState,
/// which holds a lock around it -- Encounter itself is not thread-safe on
/// its own.</summary>
public sealed class Encounter
{
    private static int _nextId = 1;

    public int Id { get; }
    public double StartTime { get; }
    public double? EndTime { get; internal set; }

    /// <summary>When a "Stopped attacking" line was last seen with no
    /// combat activity after it -- MeterState closes the encounter a few
    /// seconds later instead of waiting out the full idle timeout.
    /// Transient live state; not persisted.</summary>
    internal double? DisengagedAt { get; set; }
    public Dictionary<string, ActorStats> Actors { get; } = new();
    public Dictionary<string, ActorStats> Healers { get; } = new();

    public Encounter(double startTime)
    {
        Id = System.Threading.Interlocked.Increment(ref _nextId) - 1;
        StartTime = startTime;
    }

    public bool IsActive => EndTime is null;

    public double LastActivity()
    {
        double? max = null;
        foreach (var a in Actors.Values)
            if (a.LastSeen is { } ls && (max is null || ls > max)) max = ls;
        foreach (var a in Healers.Values)
            if (a.LastSeen is { } ls && (max is null || ls > max)) max = ls;
        return max ?? StartTime;
    }

    public double Duration(double? now = null)
    {
        var end = EndTime ?? now ?? Clock.NowSeconds();
        return Math.Max(end - StartTime, 0.0);
    }

    public void Apply(CombatEvent evt)
    {
        switch (evt.EventType)
        {
            case EventType.Hit:
            case EventType.Crit:
                GetOrAdd(Actors, evt.Source).AddDamage(
                    evt.Amount, evt.Timestamp, evt.EventType == EventType.Crit, evt.Ability, evt.School);
                break;
            case EventType.Miss:
            case EventType.Resist: // a resist is a failed attack -- counted with misses for now
                GetOrAdd(Actors, evt.Source).AddMiss(evt.Timestamp, evt.Ability);
                break;
            case EventType.Heal:
                GetOrAdd(Healers, evt.Source).AddHeal(evt.Amount, evt.Timestamp, evt.Ability);
                break;
            // Kill / Engage / Disengage carry no stats -- MeterState
            // handles their side effects (close / open the encounter,
            // drive the voice).
        }
    }

    private static ActorStats GetOrAdd(Dictionary<string, ActorStats> bucket, string name)
    {
        if (!bucket.TryGetValue(name, out var actor))
        {
            actor = new ActorStats(name);
            bucket[name] = actor;
        }
        return actor;
    }

    /// <summary>
    /// onlySources: if given (e.g. {"You", "Rebel"}), restrict the
    /// snapshot to just those actors -- used for the "Just me (+ pet)"
    /// scope toggle. Percent is computed relative to just the filtered
    /// total in that case, so you-plus-pet reads as its own 100% rather
    /// than a small raid-relative share.
    /// </summary>
    public List<MeterRow> Snapshot(bool healing = false, double? now = null,
        IReadOnlyCollection<string>? onlySources = null, int maxRows = 50)
    {
        var nowVal = now ?? Clock.NowSeconds();
        var bucket = healing ? Healers : Actors;

        List<ActorStats> source;
        if (onlySources is not null)
            source = bucket.Values.Where(a => onlySources.Contains(a.Name)).ToList();
        else
            source = bucket.Values.ToList();

        var total = source.Sum(a => healing ? a.TotalHealing : a.TotalDamage);
        if (total == 0) total = 1;
        var duration = Math.Max(Duration(nowVal), 0.001);

        var rows = source.Select(actor =>
        {
            var amount = healing ? actor.TotalHealing : actor.TotalDamage;
            var dps = healing ? amount / duration : actor.EncounterDps(nowVal);
            return new MeterRow(
                actor.Name,
                amount,
                dps,
                healing ? 0.0 : actor.RollingDps(nowVal, 10.0),
                (double)amount / total * 100.0,
                healing ? 0 : actor.Crits,
                healing ? actor.Heals : actor.Hits,
                healing ? 0 : actor.Misses,
                healing ? 100.0 : actor.Accuracy());
        }).ToList();

        rows.Sort((a, b) => b.TotalDamage.CompareTo(a.TotalDamage));
        return rows.Count > maxRows ? rows.GetRange(0, maxRows) : rows;
    }

    /// <summary>A short human-readable list of combatants for the
    /// encounter-list UI, e.g. "You, a rattlesnake, a fire beetle".</summary>
    public string Label()
    {
        var seen = new HashSet<string>();
        var names = new List<string>();
        foreach (var actor in Actors.Values)
        {
            if (seen.Add(actor.Name))
                names.Add(actor.Name);
        }
        var who = string.Join(", ", names.Take(3));
        if (names.Count > 3) who += "...";
        return names.Count == 0 ? "(no combatants)" : who;
    }
}
