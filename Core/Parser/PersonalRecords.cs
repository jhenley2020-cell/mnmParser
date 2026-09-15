using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnmDamageParser.Core.Parser;

public enum RecordKind { Ability, DamageType }

/// <summary>One personal best that was just beaten.</summary>
public sealed record RecordBreak(RecordKind Kind, string Name, int PreviousBest, int NewBest);

/// <summary>Read-only view of all current bests, for the UI.</summary>
public sealed record RecordsSnapshot(
    IReadOnlyList<(string Name, int Best)> Abilities,
    IReadOnlyList<(string Name, int Best)> DamageTypes);

/// <summary>
/// Per-character personal bests: the largest single hit you have ever
/// landed with each ability/spell, and with each damage type (school).
/// Only YOUR outgoing damage feeds this. Persisted to
/// logs/records_&lt;character&gt;.json (atomic write) so it survives restarts;
/// reloaded on a mid-session relog.
/// </summary>
public sealed class PersonalRecords
{
    public const string MeleeAbilityKey = "(melee)";
    public const string MeleeTypeKey = "melee";

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _byAbility;
    private readonly Dictionary<string, int> _byType;
    private readonly string? _path;

    private PersonalRecords(string? path, Dictionary<string, int> byAbility, Dictionary<string, int> byType)
    {
        _path = path;
        _byAbility = byAbility;
        _byType = byType;
    }

    private static Dictionary<string, int> NewMap() => new(StringComparer.OrdinalIgnoreCase);

    public static PersonalRecords Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new PersonalRecords(path, NewMap(), NewMap());
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path)) ?? new Dto();
            return new PersonalRecords(path,
                new Dictionary<string, int>(dto.ByAbility, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(dto.ByDamageType, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return new PersonalRecords(path, NewMap(), NewMap());
        }
    }

    /// <summary>Feed one outgoing hit. Returns the record(s) it broke
    /// (its ability and/or its damage type), or an empty list.</summary>
    public IReadOnlyList<RecordBreak> Register(string? ability, string? school, int amount)
    {
        if (amount <= 0) return Array.Empty<RecordBreak>();
        var abilityKey = string.IsNullOrWhiteSpace(ability) ? MeleeAbilityKey : ability.Trim();
        var typeKey = string.IsNullOrWhiteSpace(school) ? MeleeTypeKey : school.Trim();

        lock (_lock)
        {
            List<RecordBreak>? broke = null;
            Beat(_byAbility, abilityKey, amount, RecordKind.Ability, ref broke);
            Beat(_byType, typeKey, amount, RecordKind.DamageType, ref broke);
            if (broke is null) return Array.Empty<RecordBreak>();
            SaveLocked();
            return broke;
        }
    }

    private static void Beat(Dictionary<string, int> map, string key, int amount, RecordKind kind, ref List<RecordBreak>? acc)
    {
        var prev = map.TryGetValue(key, out var v) ? v : 0;
        if (amount <= prev) return;
        map[key] = amount;
        (acc ??= new List<RecordBreak>()).Add(new RecordBreak(kind, key, prev, amount));
    }

    public RecordsSnapshot Snapshot()
    {
        lock (_lock)
        {
            static List<(string, int)> Sorted(Dictionary<string, int> m) =>
                m.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
            return new RecordsSnapshot(Sorted(_byAbility), Sorted(_byType));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _byAbility.Clear();
            _byType.Clear();
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        if (_path is null) return;
        try
        {
            var full = Path.GetFullPath(_path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var dto = new Dto
            {
                ByAbility = new Dictionary<string, int>(_byAbility),
                ByDamageType = new Dictionary<string, int>(_byType),
            };
            var tmp = full + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, Opts));
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            // never let a records-save failure take down the meter
        }
    }

    private sealed class Dto
    {
        [JsonPropertyName("by_ability")] public Dictionary<string, int> ByAbility { get; set; } = new();
        [JsonPropertyName("by_damage_type")] public Dictionary<string, int> ByDamageType { get; set; } = new();
    }
}
