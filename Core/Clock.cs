namespace MnmDamageParser.Core;

/// <summary>Unix-epoch-seconds-as-double, matching Python's time.time() --
/// every timestamp in this codebase (CombatEvent.Timestamp, Encounter
/// start/end times, etc.) is in these units so C# and the old Python
/// version's logs/config stay directly comparable.</summary>
public static class Clock
{
    public static double NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
