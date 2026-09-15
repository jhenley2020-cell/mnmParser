using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnmDamageParser.Core.GameTime;

/// <summary>The extrapolated in-world time right now.</summary>
public sealed record GameTimeSnapshot(int MinuteOfDay, double RealSecondsSinceSync)
{
    public int Hour24 => MinuteOfDay / 60;
    public int Minute => MinuteOfDay % 60;
    public int Hour12 => Hour24 % 12 == 0 ? 12 : Hour24 % 12;
    public string AmPm => Hour24 < 12 ? "AM" : "PM";
    public string Label => $"{Hour12}:{Minute:00} {AmPm}";

    /// <summary>Rough day / night for a sun/moon glyph -- night is 8pm..6am.</summary>
    public bool IsNight => Hour24 >= 20 || Hour24 < 6;
}

internal sealed record GameClockState(
    [property: JsonPropertyName("anchor_real_unix")] double AnchorRealUnix,
    [property: JsonPropertyName("anchor_minute_of_day")] int AnchorMinuteOfDay,
    [property: JsonPropertyName("real_seconds_per_game_day")] double RealSecondsPerGameDay,
    [property: JsonPropertyName("date_text")] string? DateText = null);

/// <summary>
/// Tracks M&amp;M's in-world clock. The game only reveals the time when you
/// type <c>/time</c> (it prints "Game Time: 3 AM" -- hour only -- plus the
/// calendar line and the real "Earth Time"), so this keeps the last reading
/// as an anchor and extrapolates forward. Jay confirmed a full M&amp;M day is
/// **72 real minutes** (the classic EverQuest ratio), so no calibration is
/// needed -- <c>config/gametime.json</c> can still override the rate if that
/// ever turns out slightly off.
///
/// Not thread-safe; MeterState owns one and touches it under its lock.
/// </summary>
public sealed class GameClock
{
    /// <summary>72 real minutes per in-world day (confirmed for M&amp;M).</summary>
    public const double DefaultRealSecondsPerGameDay = 72 * 60;
    private const int MinutesPerGameDay = 24 * 60;

    private double _anchorReal;
    private int _anchorMinuteOfDay;
    private double _realSecondsPerGameDay = DefaultRealSecondsPerGameDay;
    private bool _haveAnchor;

    public bool HaveAnchor => _haveAnchor;
    public double RealSecondsPerGameDay => _realSecondsPerGameDay;

    /// <summary>The last calendar line from /time, e.g. "Tilusten, the 4th
    /// of Harvesttide in the year 608 After the Reformation. It is Autumn".
    /// Null until one is seen. Held verbatim (it changes rarely).</summary>
    public string? DateText { get; private set; }

    public void SetDate(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text)) DateText = text.Trim();
    }

    /// <summary>Feed a fresh time reading: game minute-of-day (0..1439) and
    /// the real time it was seen. Becomes the new anchor to extrapolate from.</summary>
    public void Observe(int minuteOfDay, double realSeconds)
    {
        _anchorMinuteOfDay = ((minuteOfDay % MinutesPerGameDay) + MinutesPerGameDay) % MinutesPerGameDay;
        _anchorReal = realSeconds;
        _haveAnchor = true;
    }

    /// <summary>Override the tick rate (config). Clamped to something sane.</summary>
    public void SetRate(double realSecondsPerGameDay)
    {
        if (realSecondsPerGameDay is > 300 and < 86400)
            _realSecondsPerGameDay = realSecondsPerGameDay;
    }

    public GameTimeSnapshot? Now(double realSeconds)
    {
        if (!_haveAnchor) return null;
        var sinceSync = realSeconds - _anchorReal;
        var gameMinutes = _anchorMinuteOfDay + sinceSync / _realSecondsPerGameDay * MinutesPerGameDay;
        var mod = (int)Math.Floor(((gameMinutes % MinutesPerGameDay) + MinutesPerGameDay) % MinutesPerGameDay);
        return new GameTimeSnapshot(mod, sinceSync);
    }

    // -- persistence ---------------------------------------------------------

    public void LoadFrom(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            var s = JsonSerializer.Deserialize<GameClockState>(File.ReadAllText(path));
            if (s is null) return;
            _anchorReal = s.AnchorRealUnix;
            _anchorMinuteOfDay = ((s.AnchorMinuteOfDay % MinutesPerGameDay) + MinutesPerGameDay) % MinutesPerGameDay;
            if (s.RealSecondsPerGameDay is > 300 and < 86400) _realSecondsPerGameDay = s.RealSecondsPerGameDay;
            DateText = string.IsNullOrWhiteSpace(s.DateText) ? null : s.DateText;
            _haveAnchor = _anchorReal > 0;
        }
        catch { /* a bad file just means "no anchor yet" */ }
    }

    public void SaveTo(string? path)
    {
        if (string.IsNullOrEmpty(path) || !_haveAnchor) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new GameClockState(_anchorReal, _anchorMinuteOfDay, _realSecondsPerGameDay, DateText),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* never let a clock-save failure matter */ }
    }

    /// <summary>Parse a 12-hour time ("6:42 AM") into a game minute-of-day,
    /// or null if it doesn't look like one.</summary>
    public static int? ParseClock12(int hour, int minute, string amPm)
    {
        if (hour is < 1 or > 12 || minute is < 0 or > 59) return null;
        var pm = amPm.Trim().ToUpperInvariant() == "PM";
        var h24 = hour % 12 + (pm ? 12 : 0);
        return h24 * 60 + minute;
    }
}
