using System.Drawing;
using System.Runtime.Versioning;
using MnmDamageParser.Core.Xp;

namespace MnmDamageParser.App;

/// <summary>
/// Full-(virtual-)screen dim sheet: drag a box around the XP bar's
/// coloured fill, then Enter to save. Port of the Python reader's
/// <c>calibrate.py</c>. Writes <see cref="XpBarConfig.Region"/>,
/// <c>FillRgb</c> and <c>EmptyRgb</c> into the passed config and returns
/// <see cref="DialogResult.OK"/>; the caller persists it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class XpCalibrationForm : Form
{
    private readonly XpBarConfig _cfg;
    private readonly Rectangle _virtual = SystemInformation.VirtualScreen;

    private Point? _dragStart;
    private Rectangle _sel;                 // in this form's client coords
    private readonly Label _info;
    private readonly Label _readout;
    private ScreenRect? _pendingRegion;
    private int[]? _pendingFill, _pendingEmpty;

    public XpCalibrationForm(XpBarConfig cfg)
    {
        _cfg = cfg;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _virtual;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.30;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;

        _info = new Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 13),
            Text = "Drag a box over the XP bar — include BOTH a filled part and an empty part   —   Enter = save   ·   Esc = cancel",
            Location = new Point(30, 24),
        };
        _readout = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(0x8f, 0xd0, 0xff),
            BackColor = Color.Transparent,
            Font = new Font("Consolas", 11),
            Location = new Point(30, 54),
        };
        Controls.Add(_info);
        Controls.Add(_readout);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _dragStart = e.Location;
        _sel = new Rectangle(e.Location, Size.Empty);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } s) return;
        _sel = Rectangle.FromLTRB(
            Math.Min(s.X, e.X), Math.Min(s.Y, e.Y), Math.Max(s.X, e.X), Math.Max(s.Y, e.Y));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragStart = null;
        if (_sel.Width < 5 || _sel.Height < 2)
        {
            _readout.Text = "Box too small — try again";
            _pendingRegion = null;
            return;
        }
        Preview();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_sel.Width > 0 && _sel.Height > 0)
            using (var pen = new Pen(Color.FromArgb(0xff, 0x3b, 0x3b), 2))
                e.Graphics.DrawRectangle(pen, _sel);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        else if (e.KeyCode == Keys.Enter) Save();
    }

    private void Preview()
    {
        var region = new ScreenRect
        {
            Left = _virtual.Left + _sel.Left,
            Top = _virtual.Top + _sel.Top,
            Width = _sel.Width,
            Height = _sel.Height,
        };

        // Hide the sheet so we capture the game, not our own dimming.
        Opacity = 0;
        Update();
        Thread.Sleep(70);
        var cols = ScreenXpReader.Capture(region);
        Opacity = 0.30;

        if (cols is null || cols.Length < 8)
        {
            _readout.Text = "Capture failed there — try a different box";
            _pendingRegion = null;
            return;
        }

        var trim = Math.Max(_cfg.EdgeTrimPx, 1);

        // Derive the fill / empty colours from the capture itself (2-means),
        // so it works whatever level the bar is at right now -- as long as
        // BOTH a filled and an empty stretch are inside the box.
        var cal = BarCalibration.Detect(cols, trim);
        if (cal is null)
        {
            _readout.Text = "Couldn't read two colours there — make the box a bit wider";
            _pendingRegion = null;
            return;
        }

        _pendingFill = cal.FillRgb;
        _pendingEmpty = cal.EmptyRgb;
        _pendingRegion = region;

        var contrast = Math.Sqrt(cal.ContrastSq);
        var frac = double.IsNaN(cal.Fraction) ? 0 : cal.Fraction * 100.0;
        var verdict = !cal.Usable
            ? "  ⚠ TOO LOW — include some FILLED and some EMPTY bar in the box"
            : contrast < 40 ? "  (contrast ok)" : "  (contrast good)";
        _readout.Text =
            $"region {region.Width}x{region.Height} @ ({region.Left},{region.Top})   " +
            $"fill≈{frac:0.0}%   fill=[{string.Join(",", _pendingFill)}] empty=[{string.Join(",", _pendingEmpty)}]   " +
            $"contrast {contrast:0}{verdict}";
    }

    private void Save()
    {
        if (_pendingRegion is null || _pendingFill is null || _pendingEmpty is null)
        {
            _readout.Text = "Nothing selected yet — drag a box first";
            return;
        }
        _cfg.Region = _pendingRegion;
        _cfg.FillRgb = _pendingFill;
        _cfg.EmptyRgb = _pendingEmpty;
        DialogResult = DialogResult.OK;
        Close();
    }
}
