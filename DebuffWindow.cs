using System.Drawing;
using System.Drawing.Drawing2D;
using MnmDamageParser.Core;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>
/// A small always-on-top-capable window that lists the DoTs / debuffs YOU
/// currently have running, each with a bar counting down to expiry. Timers
/// come from <see cref="MeterState.GetDebuffTimers"/>, which needs
/// <c>config/spells.json</c> filled in with durations. Repaints ~5x/sec so
/// the countdown is smooth.
/// </summary>
internal sealed class DebuffWindow : Form
{
    private readonly MeterState _meterState;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ListPanel _list;
    private readonly Label _hint;

    public DebuffWindow(MeterState meterState, int refreshMs)
    {
        _meterState = meterState;

        Text = "DoT / debuff timers";
        Width = 340;
        Height = 300;
        MinimumSize = new Size(240, 140);
        StartPosition = FormStartPosition.Manual;

        _list = new ListPanel { Dock = DockStyle.Fill };
        _hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
            Text = "Nothing running. Cast a DoT/debuff that's listed in config/spells.json.",
        };

        var onTop = new CheckBox { Text = "Always on top", AutoSize = true, Margin = new Padding(6) };
        onTop.CheckedChanged += (_, _) => TopMost = onTop.Checked;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        bar.Controls.Add(onTop);

        Controls.Add(_list);
        Controls.Add(_hint);
        Controls.Add(bar);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(refreshMs, 150, 400) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    // Don't yank focus off the game when it auto-opens.
    protected override bool ShowWithoutActivation => true;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void Tick()
    {
        var timers = _meterState.GetDebuffTimers();
        _hint.Visible = timers.Count == 0;
        _list.Timers = timers;
        _list.Invalidate();
    }

    /// <summary>Owner-drawn timer rows -- a plain list of bars is much
    /// smoother for a 5Hz countdown than a DataGridView.</summary>
    private sealed class ListPanel : Panel
    {
        private const int RowH = 32;

        public IReadOnlyList<DebuffTimer> Timers { get; set; } = Array.Empty<DebuffTimer>();

        public ListPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = SystemColors.Window;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var now = Clock.NowSeconds();
            using var nameFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            using var subFont = new Font("Segoe UI", 8f);
            using var numFont = new Font("Consolas", 9.5f, FontStyle.Bold);

            int y = 4;
            foreach (var t in Timers)
            {
                if (y > Height) break;
                var rowRect = new Rectangle(6, y, Width - 12, RowH - 4);
                var remaining = t.RemainingSeconds(now);
                var frac = t.Fraction(now);
                var expired = remaining <= 0;

                // track + fill bar
                var track = new Rectangle(rowRect.X, rowRect.Bottom - 8, rowRect.Width, 6);
                using (var tb = new SolidBrush(Color.FromArgb(230, 230, 232)))
                    g.FillRectangle(tb, track);
                if (!expired)
                {
                    var fillW = (int)Math.Round(track.Width * frac);
                    using var fb = new SolidBrush(BarColor(frac));
                    g.FillRectangle(fb, new Rectangle(track.X, track.Y, fillW, track.Height));
                }

                // spell -> target
                using (var ink = new SolidBrush(expired ? SystemColors.GrayText : Color.FromArgb(0x20, 0x28, 0x38)))
                {
                    g.DrawString(t.Spell, nameFont, ink, rowRect.X, rowRect.Y);
                    var sw = g.MeasureString(t.Spell, nameFont).Width;
                    g.DrawString($"→ {t.Target}", subFont, SystemBrushes.GrayText, rowRect.X + sw + 4, rowRect.Y + 2);
                }

                // remaining seconds, right-aligned
                var label = expired ? "expired" : remaining >= 10 ? $"{remaining:0}s" : $"{remaining:0.0}s";
                using (var nb = new SolidBrush(expired ? SystemColors.GrayText : BarColor(frac)))
                {
                    var lw = g.MeasureString(label, numFont).Width;
                    g.DrawString(label, numFont, nb, rowRect.Right - lw, rowRect.Y);
                }

                y += RowH;
            }
        }

        private static Color BarColor(double frac) =>
            frac > 0.5 ? Color.FromArgb(0x2e, 0xa0, 0x43)      // green
            : frac > 0.2 ? Color.FromArgb(0xd8, 0x8a, 0x00)     // amber
            : Color.FromArgb(0xd1, 0x34, 0x38);                 // red
    }
}
