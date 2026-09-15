using System.Drawing;
using MnmDamageParser.Core.Xp;

namespace MnmDamageParser.App;

/// <summary>The XP section that docks at the bottom of the main window:
/// current bar fill, last gain, rate, ETA to next level. Reads the
/// screen-scraped <see cref="XpStats"/> the sampler publishes.</summary>
internal sealed class XpPanel : Panel
{
    private static readonly Color Ink = Color.FromArgb(0x20, 0x28, 0x38);
    private static readonly Color Flash = Color.FromArgb(0xE6, 0xF9, 0xE9);

    /// <summary>A Label that paints in one buffered pass with no separate
    /// background-erase -- a plain Label flashes its background on every
    /// Text change, which is the XP panel "flicker".</summary>
    private sealed class FlatLabel : Label
    {
        public FlatLabel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
        }
    }

    private readonly Label _line1;
    private readonly Label _line2;
    private readonly Button _calibrate;
    private readonly Button _reset;
    private long _flashUntilTick;

    public event Action? CalibrateClicked;
    public event Action? ResetClicked;

    public XpPanel()
    {
        Dock = DockStyle.Bottom;
        Height = 52;
        BorderStyle = BorderStyle.FixedSingle;
        BackColor = SystemColors.Control;
        DoubleBuffered = true;

        _calibrate = new Button { Text = "Calibrate…", AutoSize = true, Margin = new Padding(3) };
        _reset = new Button { Text = "Reset session", AutoSize = true, Margin = new Padding(3) };
        _calibrate.Click += (_, _) => CalibrateClicked?.Invoke();
        _reset.Click += (_, _) => ResetClicked?.Invoke();
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(4, 8, 4, 4),
        };
        buttons.Controls.Add(_calibrate);
        buttons.Controls.Add(_reset);

        _line1 = new FlatLabel
        {
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Consolas", 10.5f, FontStyle.Bold),
            ForeColor = Ink,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };
        _line2 = new FlatLabel
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };

        Controls.Add(_line2);
        Controls.Add(_line1);
        Controls.Add(buttons);
    }

    /// <param name="calibrated">config.IsCalibrated</param>
    /// <param name="error">sampler error, or null</param>
    public void Show(XpStats s, bool calibrated, string? error)
    {
        if (!calibrated)
        {
            SetText(_line1, "XP bar not calibrated");
            SetText(_line2, "Click “Calibrate…”, then drag a box over the in-game XP bar — cover a filled stretch AND an empty stretch.");
            SetFore(_line2, SystemColors.GrayText);
            _reset.Enabled = false;
            SetBack(SystemColors.Control);
            return;
        }
        _reset.Enabled = true;

        SetText(_line1,
            $"XP {s.FillPercent,6:0.00}%     session {Signed(s.SessionPercent)}%  ({s.SessionLevels:0.000} lvl)" +
            $"     {s.RecentRatePercentPerMin:0.0} %/min  ~{s.LevelsPerHour:0.00} lvl/h");

        if (error is not null)
        {
            SetText(_line2, error);
            SetFore(_line2, Color.Firebrick);
        }
        else
        {
            var last = s.LastGainPercent > 0
                ? $"last +{s.LastGainPercent:0.00}% ({FmtDur(s.LastGainAgeSeconds)} ago)     "
                : "";
            var eta = s.EtaNextLevelSeconds > 0 ? FmtEta(s.EtaNextLevelSeconds) : "—";
            SetText(_line2,
                $"{last}next level ~{eta}     played {FmtDur(s.ElapsedSeconds)}" +
                (s.LevelsGained > 0 ? $"     {s.LevelsGained} level(s) this session" : ""));
            SetFore(_line2, SystemColors.GrayText);
        }

        if (s.LeveledUp) _flashUntilTick = Environment.TickCount64 + 2500;
        SetBack(Environment.TickCount64 < _flashUntilTick ? Flash : SystemColors.Control);
    }

    // Only touch a control when the value actually changed -- setting
    // Label.Text (even to the same string, in some paths) and BackColor
    // repaints, and a repaint every refresh tick is the flicker.
    private static void SetText(Label l, string t) { if (l.Text != t) l.Text = t; }
    private static void SetFore(Label l, Color c) { if (l.ForeColor != c) l.ForeColor = c; }
    private void SetBack(Color c) { if (BackColor != c) BackColor = c; }

    private static string Signed(double v) => (v >= 0 ? "+" : "") + v.ToString("0.00");

    /// <summary>Coarse ETA so the line doesn't tick every second.</summary>
    private static string FmtEta(double seconds)
    {
        if (seconds >= 3600) return $"{seconds / 3600.0:0.0}h";
        if (seconds >= 600) return $"{Math.Round(seconds / 60.0 / 5.0) * 5:0}m";
        if (seconds >= 60) return $"{Math.Round(seconds / 60.0):0}m";
        return "<1m";
    }

    internal static string FmtDur(double seconds)
    {
        var s = (int)Math.Max(seconds, 0);
        var h = s / 3600;
        var m = s % 3600 / 60;
        var sec = s % 60;
        if (h > 0) return $"{h}h{m:00}m";
        if (m > 0) return $"{m}m{sec:00}s";
        return $"{sec}s";
    }
}
