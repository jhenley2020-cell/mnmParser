namespace MnmDamageParser.Core.Xp;

/// <summary>A snapshot of XP-gain statistics. All "percent" figures are
/// percent of the current level (100 = one level). Direct port of the
/// Python reader's <c>Stats</c>.</summary>
public sealed record XpStats
{
    public double FillPercent { get; init; }          // current bar fill, 0..100
    public double DeltaPercent { get; init; }          // change since previous reading
    public double SessionPercent { get; init; }        // cumulative since reset (can exceed 100)
    public double SessionLevels { get; init; }         // same, in levels
    public double RecentRatePercentPerMin { get; init; }
    public double SessionRatePercentPerMin { get; init; }
    public double LevelsPerHour { get; init; }
    public double EtaNextLevelSeconds { get; init; }
    public double ElapsedSeconds { get; init; }
    public int LevelsGained { get; init; }
    public bool LeveledUp { get; init; }               // true only on the reading a level-up is seen
    public double LastGainPercent { get; init; }       // most recent non-zero gain (held)
    public double LastGainAgeSeconds { get; init; }
    public bool FreshGain { get; init; }               // true only on the reading a new gain lands

    public static readonly XpStats Empty = new();
}

/// <summary>
/// Turns a stream of bar fill-fractions (0..1) into XP-gain statistics.
/// Direct port of the Python reader's <c>XPTracker</c>: cumulative gain in
/// "levels" (1.0 == one full bar), level-up detected when the fill drops
/// by more than <c>levelUpDrop</c>, trailing-window rate + ETA.
///
/// Not thread-safe; the sampler calls <see cref="Update"/> from one thread.
/// Time comes from an injected function (defaults to
/// <see cref="Clock.NowSeconds"/>) so tests can drive it.
/// </summary>
public sealed class XpTracker
{
    private readonly double _rateWindowSeconds;
    private readonly double _levelUpDrop;
    private readonly double _noiseFloor;
    private readonly Func<double> _now;

    private double _t0;
    private double? _refFrac;           // last COMMITTED fill level -- small
                                        // sub-noise-floor gains accumulate
                                        // against this instead of being lost
    private double _lastFrac;           // most recent raw reading (for gain detection)
    private double _cumLevels;          // cumulative gain, 1.0 == one full bar
    private int _levels;
    private double _lastGain;           // most recent non-zero gain, in levels
    private double _lastGainT;
    private readonly LinkedList<(double T, double Cum)> _hist = new();

    // Display smoothing only -- keeps the panel from churning on bar
    // shimmer / anti-aliasing while the gain-detection maths above stays on
    // the raw readings.
    private readonly Queue<double> _recentFrac = new();   // last few raw fills -> median for display
    private double? _rateEma;                             // EMA of the recent-rate figure
    private const double RateEmaAlpha = 0.25;

    public XpTracker(double rateWindowSeconds = 120, double levelUpDrop = 0.12,
        double noiseFloor = 0.002, Func<double>? now = null)
    {
        _rateWindowSeconds = rateWindowSeconds;
        _levelUpDrop = levelUpDrop;
        _noiseFloor = noiseFloor;
        _now = now ?? Clock.NowSeconds;
        Reset();
    }

    public void Reset()
    {
        _t0 = _now();
        _refFrac = null;
        _lastFrac = 0.0;
        _cumLevels = 0.0;
        _levels = 0;
        _lastGain = 0.0;
        _lastGainT = _t0;
        _hist.Clear();
        _hist.AddLast((_t0, 0.0));
        _recentFrac.Clear();
        _rateEma = null;
    }

    public XpStats Update(double frac)
    {
        var now = _now();
        if (double.IsNaN(frac)) frac = _lastFrac; // bad reading -- hold the last value
        frac = Math.Clamp(frac, 0.0, 1.0);
        var leveled = false;
        var isFirstReading = _refFrac is null;

        double rawDelta;
        if (_refFrac is null)
        {
            rawDelta = 0.0;
            _refFrac = frac;
        }
        else
        {
            var diff = frac - _refFrac.Value;
            if (diff < -_levelUpDrop)
            {
                // Bar wrapped: finished the old level + whatever is on the new one.
                rawDelta = (1.0 - _refFrac.Value) + frac;
                _levels++;
                leveled = true;
                _refFrac = frac;
            }
            else if (diff >= _noiseFloor)
            {
                // A real gain (possibly the sum of several sub-noise-floor
                // readings that accumulated against the fixed reference).
                rawDelta = diff;
                _refFrac = frac;
            }
            else
            {
                // Within the noise band, or a small spurious dip -- don't
                // move the reference, so a slow trickle of XP still lands
                // once it grows past the floor.
                rawDelta = 0.0;
            }
        }

        _cumLevels += rawDelta;
        _lastFrac = frac;

        var fresh = rawDelta > 0.0;
        if (fresh)
        {
            _lastGain = rawDelta;
            _lastGainT = now;
        }

        _hist.AddLast((now, _cumLevels));
        var cutoff = now - _rateWindowSeconds;
        while (_hist.Count > 2 && _hist.First!.Next!.Value.T < cutoff)
            _hist.RemoveFirst();

        // Smoothed fill for display (median of the last few raw reads) --
        // the raw value is used everywhere gains are detected, this only
        // stops the on-screen number twitching on a shimmering bar.
        _recentFrac.Enqueue(frac);
        while (_recentFrac.Count > 5) _recentFrac.Dequeue();
        var displayFrac = Median(_recentFrac);

        var elapsed = now - _t0;
        var (wT0, wC0) = _hist.First!.Value;
        var wSpan = Math.Max(now - wT0, 1e-6);
        var recentRate = (_cumLevels - wC0) / wSpan;     // levels per second
        double smoothRate;
        if (isFirstReading)
        {
            smoothRate = 0.0; // no interval to rate yet
        }
        else
        {
            _rateEma = _rateEma is null
                ? recentRate
                : RateEmaAlpha * recentRate + (1 - RateEmaAlpha) * _rateEma.Value;
            smoothRate = _rateEma.Value;
        }
        var sessionRate = _cumLevels / Math.Max(elapsed, 1e-6);
        var eta = smoothRate > 1e-9 ? (1.0 - displayFrac) / smoothRate : 0.0;

        return new XpStats
        {
            FillPercent = displayFrac * 100.0,
            DeltaPercent = rawDelta * 100.0,
            SessionPercent = _cumLevels * 100.0,
            SessionLevels = _cumLevels,
            RecentRatePercentPerMin = smoothRate * 100.0 * 60.0,
            SessionRatePercentPerMin = sessionRate * 100.0 * 60.0,
            LevelsPerHour = smoothRate * 3600.0,
            EtaNextLevelSeconds = eta,
            ElapsedSeconds = elapsed,
            LevelsGained = _levels,
            LeveledUp = leveled,
            LastGainPercent = _lastGain * 100.0,
            LastGainAgeSeconds = now - _lastGainT,
            FreshGain = fresh,
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        var a = values.ToArray();
        if (a.Length == 0) return 0.0;
        Array.Sort(a);
        var m = a.Length / 2;
        return a.Length % 2 == 1 ? a[m] : (a[m - 1] + a[m]) / 2.0;
    }
}
