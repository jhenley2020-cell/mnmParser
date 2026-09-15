using System.Runtime.Versioning;
using MnmDamageParser.Core.Xp;

namespace MnmDamageParser.App;

/// <summary>
/// Background thread: every <c>interval_sec</c> it grabs the XP bar,
/// feeds the fraction to an <see cref="XpTracker"/>, and publishes the
/// latest <see cref="XpStats"/>. The UI reads <see cref="Latest"/> on its
/// own refresh tick. Recalibration is picked up automatically because the
/// same <see cref="XpBarConfig"/> instance is shared and just mutated.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class XpSampler : IDisposable
{
    private readonly XpBarConfig _cfg;
    private readonly XpTracker _tracker;
    private readonly Thread _thread;
    private volatile bool _stop;
    private int _resetRequested;
    private volatile XpStats _latest = XpStats.Empty;
    private volatile string? _error;
    private int _consecutiveFails;
    private const int FailsBeforeError = 3;   // ride out a transient bad capture without flashing an error

    public XpStats Latest => _latest;
    public string? Error => _error;

    public XpSampler(XpBarConfig cfg)
    {
        _cfg = cfg;
        _tracker = new XpTracker(cfg.RateWindowSeconds, cfg.LevelUpDrop, cfg.NoiseFloor);
        _thread = new Thread(Loop) { IsBackground = true, Name = "XpSampler" };
        _thread.Start();
    }

    public void ResetSession() => Interlocked.Exchange(ref _resetRequested, 1);

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                if (Interlocked.Exchange(ref _resetRequested, 0) == 1) _tracker.Reset();

                if (_cfg.IsCalibrated)
                {
                    var frac = ScreenXpReader.ReadFraction(_cfg);
                    if (double.IsNaN(frac ?? 0.0))
                    {
                        // A calibration problem is persistent -- report it straight away.
                        _error = "XP bar calibration has almost no colour contrast -- recalibrate";
                    }
                    else if (frac is null)
                    {
                        // A missed capture is usually a one-off (GDI busy, a
                        // fullscreen toggle) -- keep showing the last good
                        // reading and only surface an error if it persists.
                        if (++_consecutiveFails >= FailsBeforeError)
                            _error = "couldn't read the screen there";
                    }
                    else
                    {
                        _consecutiveFails = 0;
                        _error = null;
                        _latest = _tracker.Update(frac.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }

            var intervalMs = Math.Max((int)(_cfg.IntervalSeconds * 1000), 250);
            Thread.Sleep(intervalMs);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromMilliseconds(600));
    }
}
