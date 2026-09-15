using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnmDamageParser.Core.Parser;

/// <summary>The persisted form of MeterState's running "overall" totals --
/// every closed encounter for a character folded into one aggregate, so it
/// survives the 50-encounter history cap and app restarts. Reset from the
/// Totals window ("Reset totals").</summary>
public sealed record LifetimeTotals(
    DateTime Since,
    int Encounters,
    int Kills,
    double CombatSeconds,
    Encounter Aggregate);

internal sealed record PersistedLifetime(
    [property: JsonPropertyName("since")] DateTime Since,
    [property: JsonPropertyName("encounters")] int Encounters,
    [property: JsonPropertyName("kills")] int Kills,
    [property: JsonPropertyName("combat_seconds")] double CombatSeconds,
    [property: JsonPropertyName("actors")] List<PersistedActor> Actors,
    [property: JsonPropertyName("healers")] List<PersistedActor> Healers);

/// <summary>Atomic JSON load/save for <see cref="LifetimeTotals"/>, one file
/// per character (<c>logs/totals_&lt;char&gt;.json</c>).</summary>
public static class LifetimeTotalsStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Save(string path, LifetimeTotals t)
    {
        var dto = new PersistedLifetime(
            t.Since, t.Encounters, t.Kills, t.CombatSeconds,
            t.Aggregate.Actors.Values.Select(EncounterHistoryStore.ToDto).ToList(),
            t.Aggregate.Healers.Values.Select(EncounterHistoryStore.ToDto).ToList());

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Opts));
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>Loads the saved totals, or null if the file doesn't exist
    /// yet (first run for this character).</summary>
    public static LifetimeTotals? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<PersistedLifetime>(File.ReadAllText(path), Opts);
            if (dto is null) return null;
            var agg = new Encounter(0) { EndTime = dto.CombatSeconds };
            foreach (var a in dto.Actors) agg.Actors[a.Name] = EncounterHistoryStore.FromDto(a);
            foreach (var h in dto.Healers) agg.Healers[h.Name] = EncounterHistoryStore.FromDto(h);
            return new LifetimeTotals(dto.Since, dto.Encounters, dto.Kills, dto.CombatSeconds, agg);
        }
        catch
        {
            return null; // a corrupt totals file shouldn't stop the meter -- start fresh
        }
    }
}
