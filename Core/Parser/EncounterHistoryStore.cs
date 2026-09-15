using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnmDamageParser.Core.Parser;

internal sealed record PersistedAbility(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("school")] string? School,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("crits")] int Crits,
    [property: JsonPropertyName("misses")] int Misses,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("min_hit")] int? MinHit,
    [property: JsonPropertyName("max_hit")] int? MaxHit);

internal sealed record PersistedActor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("total_damage")] int TotalDamage,
    [property: JsonPropertyName("total_healing")] int TotalHealing,
    [property: JsonPropertyName("hits")] int Hits,
    [property: JsonPropertyName("crits")] int Crits,
    [property: JsonPropertyName("misses")] int Misses,
    [property: JsonPropertyName("heals")] int Heals,
    [property: JsonPropertyName("first_seen")] double? FirstSeen,
    [property: JsonPropertyName("last_seen")] double? LastSeen,
    [property: JsonPropertyName("abilities")] List<PersistedAbility> Abilities,
    [property: JsonPropertyName("heal_abilities")] List<PersistedAbility> HealAbilities);

internal sealed record PersistedEncounter(
    [property: JsonPropertyName("start_time")] double StartTime,
    [property: JsonPropertyName("end_time")] double? EndTime,
    [property: JsonPropertyName("actors")] List<PersistedActor> Actors,
    [property: JsonPropertyName("healers")] List<PersistedActor> Healers);

/// <summary>
/// Saves/loads MeterState's closed-encounter history to a JSON file so
/// past fights survive closing and reopening the app -- previously
/// history was purely in-memory (a LinkedList&lt;Encounter&gt;) and reset
/// to empty on every restart.
///
/// Only CLOSED encounters (EndTime set) are ever written -- the live
/// Current encounter is intentionally excluded and gets picked up on the
/// next save once it closes (on a kill or the idle timeout), so if the
/// app is killed mid-fight that one fight's data is lost, same as it
/// always effectively was for a fight that never got the chance to close.
///
/// Encounter.Id is NOT preserved across a restart -- the id counter
/// always starts fresh at process start, so restored encounters get new
/// ids from the normal constructor. Nothing outside a single run depends
/// on ids being stable.
///
/// The rolling (last-10-seconds) per-hit damage window used for
/// RollingDps is also not preserved (see ActorStats.RestoreTotals) --
/// only the aggregate totals are. A reloaded encounter's overall DPS is
/// correct; its "recent window" DPS reads as 0 since that needs per-hit
/// timestamps this format doesn't keep.
/// </summary>
public static class EncounterHistoryStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>Writes historyNewestFirst (only its closed entries) to
    /// path, atomically (write to a temp file, then rename over the
    /// real one) so a crash or power loss mid-write can't corrupt the
    /// history file itself.</summary>
    public static void Save(string path, IEnumerable<Encounter> historyNewestFirst)
    {
        var dtos = historyNewestFirst.Where(e => e.EndTime is not null).Select(ToDto).ToList();
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmpPath = fullPath + ".tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(dtos, Opts));
        File.Move(tmpPath, fullPath, overwrite: true);
    }

    /// <summary>Returns the saved encounters in the same newest-first
    /// order they were saved in, or an empty list if the file doesn't
    /// exist yet (first run for this character).</summary>
    public static List<Encounter> Load(string path)
    {
        if (!File.Exists(path)) return new List<Encounter>();
        var json = File.ReadAllText(path);
        var dtos = JsonSerializer.Deserialize<List<PersistedEncounter>>(json, Opts) ?? new List<PersistedEncounter>();
        return dtos.Select(FromDto).ToList();
    }

    private static PersistedEncounter ToDto(Encounter e) => new(
        e.StartTime, e.EndTime,
        e.Actors.Values.Select(ToDto).ToList(),
        e.Healers.Values.Select(ToDto).ToList());

    internal static PersistedActor ToDto(ActorStats a) => new(
        a.Name, a.TotalDamage, a.TotalHealing, a.Hits, a.Crits, a.Misses, a.Heals,
        a.FirstSeen, a.LastSeen,
        a.Abilities.Values.Select(ToDto).ToList(),
        a.HealAbilities.Values.Select(ToDto).ToList());

    private static PersistedAbility ToDto(AbilityStats a) => new(
        a.Name, a.School, a.Count, a.Crits, a.Misses, a.Total, a.MinHit, a.MaxHit);

    private static Encounter FromDto(PersistedEncounter dto)
    {
        var enc = new Encounter(dto.StartTime) { EndTime = dto.EndTime };
        foreach (var a in dto.Actors) enc.Actors[a.Name] = FromDto(a);
        foreach (var h in dto.Healers) enc.Healers[h.Name] = FromDto(h);
        return enc;
    }

    internal static ActorStats FromDto(PersistedActor dto)
    {
        var actor = new ActorStats(dto.Name);
        actor.RestoreTotals(dto.TotalDamage, dto.TotalHealing, dto.Hits, dto.Crits, dto.Misses, dto.Heals, dto.FirstSeen, dto.LastSeen);
        foreach (var ab in dto.Abilities) actor.Abilities[ab.Name] = FromDto(ab);
        foreach (var ab in dto.HealAbilities) actor.HealAbilities[ab.Name] = FromDto(ab);
        return actor;
    }

    private static AbilityStats FromDto(PersistedAbility dto)
    {
        var ability = new AbilityStats(dto.Name);
        ability.Restore(dto.Count, dto.Crits, dto.Misses, dto.Total, dto.MinHit, dto.MaxHit, dto.School);
        return ability;
    }
}
