namespace MnmDamageParser.Core;

public enum EventType
{
    Hit,
    Crit,
    Miss,
    Heal,

    /// <summary>A kill/death message ("You have slain a large rat!").
    /// Carries no damage of its own -- Encounter.Apply ignores it -- it
    /// only tells MeterState to close the current encounter immediately
    /// instead of waiting for the idle timeout.</summary>
    Kill,

    /// <summary>A spell resisted by the target ("a wolf resists your Blast
    /// of Sleet!"). Carries no damage. Encounter.Apply currently folds it
    /// into the miss count (a resist is a failed attack); the voice
    /// announcer distinguishes it from a plain miss by this type. The
    /// wording is not yet confirmed -- see the _unconfirmed_*_resist
    /// patterns in offsets.json.</summary>
    Resist,

    /// <summary>"Starting to attack X" -- you entered combat with a
    /// target. Carries no damage (Encounter.Apply ignores it); it opens
    /// the encounter and drives the "you enter combat" voice callout.</summary>
    Engage,

    /// <summary>"Stopped attacking X" -- combat ended (or you switched
    /// targets). Carries no damage; MeterState uses it to close the live
    /// encounter promptly instead of waiting out the full idle timeout,
    /// and it can drive a "combat over" voice callout.</summary>
    Disengage,

    /// <summary>The in-world time-of-day, from the game's /time command
    /// ("... 6:42 AM ..."). Not a combat event -- <see cref="CombatEvent.Amount"/>
    /// carries the game minute-of-day (0..1439) and MeterState feeds it to
    /// its GameClock; Encounter.Apply ignores it.</summary>
    WorldTime,
}

/// <summary>
/// One parsed line of combat text. Direct port of combat_log.py's
/// CombatEvent dataclass.
/// </summary>
public sealed class CombatEvent
{
    public required double Timestamp { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public required int Amount { get; init; }
    public required EventType EventType { get; init; }
    public string? School { get; init; }
    public string? RawLine { get; init; }

    /// <summary>
    /// Ability/spell name, e.g. "Blast of Sleet" -- captured by the
    /// line pattern's named "ability" (or "ability2") regex group. Null
    /// for lines with no named ability (plain melee "X hits YOU for N",
    /// the generic miss line).
    /// </summary>
    public string? Ability { get; init; }
}
