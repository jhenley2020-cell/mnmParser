using System.Drawing;
using System.Drawing.Drawing2D;

namespace MnmDamageParser.MemScan;

/// <summary>
/// Top-down dot map of nearby mobs: zoomable, pannable, repainting at ~20Hz.
///
/// Mob dots are LIVE positions (fixed 2026-09-14). The 0x90 record array
/// this originally read is a frozen snapshot -- its +0x04 tick never
/// advances -- so the map used to draw spawn points that never moved. It now
/// uses MobTracker.DiscoverEntities/ReadEntities instead: entity ids come
/// from the npc debug strings, and each id's newest position-history sample
/// is its current position. Your own position is live too (PlayerFinder).
///
/// CONTROLS: mouse wheel = zoom (anchored at the cursor), left-drag = pan,
/// F = follow/free, 0 = fit all, +/- = zoom, N = toggle name labels.
///
/// "You" starts as the CENTROID of visible mobs (an approximation), then
/// upgrades to your REAL position once PlayerFinder locates it -- which
/// needs you to walk for a few seconds, since a stationary player is
/// indistinguishable from static data. The caption says which is in use.
/// </summary>
internal sealed class MobMapWindow : Form
{
    private readonly MobTracker _tracker;
    private readonly string _bootstrapName;
    private readonly MnmDamageParser.Core.Memory.IMemoryReader _reader;
    private readonly System.Windows.Forms.Timer _timer;

    private List<MobEntity> _mobs = new();
    private DateTime _lastNameSweep = DateTime.MinValue;

    // View state, all in WORLD units except _pixelsPerUnit.
    private float _pixelsPerUnit = 1.2f;
    private float _viewX;
    private float _viewZ;
    private bool _follow = true;
    private bool _showNames = true;
    private bool _hasFitted;

    private bool _dragging;
    private Point _dragFrom;

    private Task? _reanchor;
    private Task? _locate;
    private bool _usingLive;
    private Task? _liveSweep;
    private DateTime _lastLiveSweep = DateTime.MinValue;
    private DateTime? _staleSince;
    private DateTime _lastLocateAttempt = DateTime.MinValue;
    private int _locatedReplicas;
    private (float X, float Y, float Z)? _player;
    private double _lastWalkMs;
    private int _walks;
    private DateTime _rateWindowStart = DateTime.UtcNow;
    private double _hz;

    private const float MinZoom = 0.05f;
    private const float MaxZoom = 40f;
    private const int CaptionHeight = 34;
    private const int HeaderHeight = 26;

    // The plot occupies the band between the header strip and the caption.
    // Every coordinate transform below derives from these, so the two strips
    // can't silently shift the map off-centre.
    private float PlotTop => HeaderHeight;
    private float PlotBottom => Height - CaptionHeight;
    private float PlotCenterY => (PlotTop + PlotBottom) / 2f;

    private static readonly Color Background = Color.FromArgb(0x12, 0x12, 0x16);
    private static readonly Color GridInk = Color.FromArgb(35, 255, 255, 255);
    private static readonly Color GridInkBright = Color.FromArgb(60, 255, 255, 255);
    private static readonly Color YouFill = Color.FromArgb(0x22, 0xC7, 0xFF);
    private static readonly Color MobFill = Color.FromArgb(0xFF, 0x5C, 0x5C);
    private static readonly Color NamedFill = Color.FromArgb(0xFF, 0xB3, 0x4D);
    private static readonly Color PlayerFill = Color.FromArgb(0x6E, 0xE7, 0x7B); // green = another player
    private static readonly Color TextInk = Color.FromArgb(0xC8, 0xC8, 0xC8);
    private static readonly Color DimInk = Color.FromArgb(0x80, 0x80, 0x88);

    public MobMapWindow(MobTracker tracker, string bootstrapName, MnmDamageParser.Core.Memory.IMemoryReader reader)
    {
        _tracker = tracker;
        _bootstrapName = bootstrapName;
        _reader = reader;

        Text = string.IsNullOrWhiteSpace(bootstrapName) ? "Mob Map (live)" : $"Mob Map (live, from \"{bootstrapName}\")";
        Width = 760;
        Height = 800;
        MinimumSize = new Size(360, 380);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        DoubleBuffered = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);

        MouseWheel += OnWheel;
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragging = true; _dragFrom = e.Location; _follow = false; } };
        MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _dragging = false; };
        MouseMove += OnDrag;
        KeyDown += OnKey;

        // 50ms: the walk itself is ~0-7ms, so this is comfortably real-time
        // without pinning a core.
        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void OnWheel(object? sender, MouseEventArgs e)
    {
        // Zoom about the cursor: keep whatever world point is under the
        // pointer pinned there, which is what makes wheel-zoom feel right.
        var (wx, wz) = ScreenToWorld(e.Location);
        var factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
        _pixelsPerUnit = Math.Clamp(_pixelsPerUnit * factor, MinZoom, MaxZoom);
        var (wx2, wz2) = ScreenToWorld(e.Location);
        _viewX += wx - wx2;
        _viewZ += wz - wz2;
        _follow = false;
        Invalidate();
    }

    private void OnDrag(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _viewX -= (e.X - _dragFrom.X) / _pixelsPerUnit;
        _viewZ += (e.Y - _dragFrom.Y) / _pixelsPerUnit; // screen Y is inverted vs world Z
        _dragFrom = e.Location;
        Invalidate();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F: _follow = !_follow; break;
            case Keys.N: _showNames = !_showNames; break;
            case Keys.D0 or Keys.NumPad0: FitAll(); _follow = true; break;
            case Keys.Oemplus or Keys.Add: _pixelsPerUnit = Math.Min(_pixelsPerUnit * 1.25f, MaxZoom); break;
            case Keys.OemMinus or Keys.Subtract: _pixelsPerUnit = Math.Max(_pixelsPerUnit / 1.25f, MinZoom); break;
            default: return;
        }
        Invalidate();
    }

    private void Tick()
    {
        // Re-anchoring does a full heap sweep (~3s). Running that on the UI
        // thread would freeze the window, so it goes to a background task --
        // and we skip walking while it runs, because TryBootstrapAuto writes
        // Anchor/Region that Rewalk reads.
        if (_reanchor is { IsCompleted: false }) { Invalidate(); return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Live source: entity ids from the npc debug strings, resolved to each
        // id's newest position-history sample. The 0x90 array is only a
        // fallback until the first discovery completes -- it is a frozen
        // snapshot (spawn points) and never moves.
        var liveOnes = _tracker.HasEntities ? _tracker.ReadEntities() : new List<MobEntity>();
        _usingLive = liveOnes.Count > 0;
        var mobs = _usingLive ? liveOnes : _tracker.Rewalk();

        // Track how long the live source has been giving us nothing. Silently
        // keeping the last good frame is what made a failure look like "the
        // mobs stopped moving" instead of an error -- so it's surfaced, and a
        // re-discovery is forced rather than waiting for the 20s timer.
        if (_tracker.HasEntities && liveOnes.Count == 0)
        {
            if (_staleSince is null) _staleSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - _staleSince.Value).TotalSeconds > 3) _lastLiveSweep = DateTime.MinValue;
        }
        else _staleSince = null;
        sw.Stop();
        _lastWalkMs = sw.Elapsed.TotalMilliseconds;

        if (!_tracker.IsBootstrapped && !_tracker.HasEntities)
        {
            _reanchor = Task.Run(() => _tracker.TryBootstrapAuto());
            Invalidate();
            return;
        }
        if (mobs.Count > 0) _mobs = mobs;

        // Re-sweep periodically: entities spawn, die and wander in, and the
        // live records are scattered allocations rather than one array, so a
        // stale slot list silently loses mobs.
        if (_liveSweep is null or { IsCompleted: true }
            && (DateTime.UtcNow - _lastLiveSweep).TotalSeconds >= (_tracker.HasEntities ? 20 : 3)
            && _mobs.Count > 0)
        {
            _lastLiveSweep = DateTime.UtcNow;
            // Centre the search on YOU when your position is known, with a
            // fixed radius. Centring on the mob centroid instead makes the
            // search area drift every sweep as mobs wander and as you move, so
            // entities near the edge fall in and out between scans -- which is
            // seen as mobs "appearing and disappearing randomly". Anchored to
            // the player, the area is stable and means a plain "everything
            // within N units of me".
            //
            // The radius still cannot be huge: a wide sphere whose centre sits
            // a few hundred units from the origin swallows all the zero-filled
            // memory and floods discovery with junk.
            PlayerFinder.Zone sweepZone;
            if (_player is { } me)
                sweepZone = new PlayerFinder.Zone(me.X, me.Y, me.Z, 400f, 80f);
            else
            {
                var cx = _mobs.Average(m => m.X); var cz = _mobs.Average(m => m.Z);
                var spread = _mobs.Max(m => MathF.Sqrt((m.X - cx) * (m.X - cx) + (m.Z - cz) * (m.Z - cz)));
                sweepZone = PlayerFinder.Zone.FromMobs(_mobs, radiusXZ: spread + 120f, radiusY: 60f);
            }
            _liveSweep = Task.Run(() => _tracker.DiscoverEverything(sweepZone));
        }

        _walks++;
        var since = (DateTime.UtcNow - _rateWindowStart).TotalSeconds;
        if (since >= 1) { _hz = _walks / since; _walks = 0; _rateWindowStart = DateTime.UtcNow; }

        if ((DateTime.UtcNow - _lastNameSweep).TotalSeconds >= 30)
        {
            _tracker.SweepNamesInBackground();
            _lastNameSweep = DateTime.UtcNow;
        }

        _player = _tracker.ReadPlayer();
        // Keep retrying for as long as we don't have a position. Locating only
        // works while you are WALKING, so a fixed attempt cap (this used to
        // stop after 4) meant that standing still during those few tries left
        // the map on the centroid approximation permanently -- and the same
        // happened after a zone change invalidated the address. Retrying on a
        // throttle costs one background scan every 15s and always recovers.
        if (_player is null && _locate is null or { IsCompleted: true } && _mobs.Count > 0
            && (DateTime.UtcNow - _lastLocateAttempt).TotalSeconds >= 15)
        {
            _lastLocateAttempt = DateTime.UtcNow;
            // Locating needs a heap scan plus a few seconds of watching, so it
            // runs off the UI thread. It only works while you're WALKING --
            // a stationary player is indistinguishable from static data.

            var zone = PlayerFinder.Zone.FromMobs(_mobs);
            _locate = Task.Run(() =>
            {
                // Read the UI thread's latest snapshot rather than calling
                // Rewalk ourselves: two threads walking the tracker at once
                // would race on its anchor/hot-band state. Reference
                // assignment of _mobs is atomic, so this stays current.
                var found = new PlayerFinder(_reader).Locate(zone, () => _mobs);
                if (found is not null) _tracker.PlayerAddress = found.Address;
                _locatedReplicas = found?.Replicas ?? 0;
            });
        }

        if (!_hasFitted && _mobs.Count > 0) { FitAll(); _hasFitted = true; }
        if (_follow && _player is { } pp)
        {
            // Locked onto the real player: centre hard, no easing needed --
            // it's a true position, not a wobbling average.
            _viewX = pp.X;
            _viewZ = pp.Z;
        }
        else if (_follow && _mobs.Count > 0)
        {
            // Ease toward the centroid instead of snapping to it: a single
            // mob spawning or despawning shifts the average, and at 20Hz that
            // would read as the whole map lurching.
            const float ease = 0.15f;
            _viewX += (_mobs.Average(m => m.X) - _viewX) * ease;
            _viewZ += (_mobs.Average(m => m.Z) - _viewZ) * ease;
        }
        Invalidate();
    }

    private void FitAll()
    {
        if (_mobs.Count == 0) return;
        _viewX = _mobs.Average(m => m.X);
        _viewZ = _mobs.Average(m => m.Z);
        var spread = Math.Max(
            _mobs.Max(m => Math.Max(Math.Abs(m.X - _viewX), Math.Abs(m.Z - _viewZ))), 10f);
        var usable = Math.Min(Width, PlotBottom - PlotTop) / 2f - 48f;
        _pixelsPerUnit = Math.Clamp(usable / spread, MinZoom, MaxZoom);
    }

    private PointF WorldToScreen(float wx, float wz) => new(
        Width / 2f + (wx - _viewX) * _pixelsPerUnit,
        PlotCenterY - (wz - _viewZ) * _pixelsPerUnit);

    private (float X, float Z) ScreenToWorld(Point p) => (
        _viewX + (p.X - Width / 2f) / _pixelsPerUnit,
        _viewZ - (p.Y - PlotCenterY) / _pixelsPerUnit);

    /// <summary>Ring spacing that keeps 3-5 rings on screen at any zoom, in
    /// round world units rather than whatever the mob spread happens to be.</summary>
    private float RingStep()
    {
        var targetPixels = 90f;
        var raw = targetPixels / _pixelsPerUnit;
        foreach (var step in new[] { 5f, 10f, 25f, 50f, 100f, 250f, 500f, 1000f, 2500f })
            if (step >= raw) return step;
        return 5000f;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* OnPaint clears the whole surface */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Background);

        using var hintFont = new Font("Segoe UI", 8f);
        using var italicFont = new Font("Segoe UI", 8f, FontStyle.Italic);
        using var labelFont = new Font("Segoe UI", 7.5f);
        using var textBrush = new SolidBrush(TextInk);
        using var dimBrush = new SolidBrush(DimInk);

        DrawHeader(g);

        if (_mobs.Count == 0)
        {
            g.DrawString("waiting for mob data...", italicFont, textBrush, 10, PlotTop + 8);
            DrawCaption(g, hintFont, dimBrush);
            return;
        }

        var plotH = PlotBottom;
        var origin = new PointF(Width / 2f, PlotCenterY);

        // Distance rings centred on the view, labelled in world units so the
        // zoom level is actually readable rather than implied.
        var step = RingStep();
        using (var gridPen = new Pen(GridInk))
        using (var brightPen = new Pen(GridInkBright))
        {
            for (var ring = 1; ring <= 6; ring++)
            {
                var r = step * ring * _pixelsPerUnit;
                if (r > Math.Max(Width, plotH)) break;
                g.DrawEllipse(ring % 2 == 0 ? brightPen : gridPen, origin.X - r, origin.Y - r, r * 2, r * 2);
                g.DrawString($"{step * ring:0}", labelFont, dimBrush, origin.X + 3, origin.Y - r - 2);
            }
        }

        using (var playerBrush = new SolidBrush(PlayerFill))
        using (var playerText = new SolidBrush(PlayerFill))
        using (var playerRing = new Pen(Color.FromArgb(200, 255, 255, 255), 1.2f))
        using (var mobBrush = new SolidBrush(MobFill))
        using (var namedBrush = new SolidBrush(NamedFill))
        using (var headingPen = new Pen(Color.FromArgb(120, 255, 255, 255)))
        {
            foreach (var m in _mobs)
            {
                var p = WorldToScreen(m.X, m.Z);
                if (p.X < -60 || p.X > Width + 60 || p.Y < PlotTop - 40 || p.Y > PlotBottom + 40) continue;

                // Heading tick: makes it obvious at a glance which way things
                // are facing once the map is moving in real time.
                var rad = (float)(m.Heading * Math.PI / 180.0);
                g.DrawLine(headingPen, p.X, p.Y,
                    p.X + (float)Math.Sin(rad) * 11f, p.Y - (float)Math.Cos(rad) * 11f);

                // Players get their own colour and a slightly larger dot --
                // knowing whether that blip is a mob or another character is
                // the single most useful distinction on the map.
                var isPlayer = MobTracker.IsPlayerId(m.Id);
                var named = m.Name is not null;
                var brush = isPlayer ? playerBrush : named ? namedBrush : mobBrush;
                var r = isPlayer ? 5f : 4f;
                g.FillEllipse(brush, p.X - r, p.Y - r, r * 2, r * 2);
                if (isPlayer) g.DrawEllipse(playerRing, p.X - r, p.Y - r, r * 2, r * 2);
                if (_showNames)
                    g.DrawString(m.Name ?? $"id {m.Id}", labelFont,
                        isPlayer ? playerText : named ? textBrush : dimBrush, p.X + 6, p.Y - 6);
            }
        }

        var locked = _player is not null;
        var you = locked
            ? WorldToScreen(_player!.Value.X, _player.Value.Z)
            : WorldToScreen(_mobs.Average(m => m.X), _mobs.Average(m => m.Z));
        using (var youBrush = new SolidBrush(YouFill))
        using (var youPen = new Pen(Color.White, 1.5f))
        {
            g.FillEllipse(youBrush, you.X - 7, you.Y - 7, 14, 14);
            g.DrawEllipse(youPen, you.X - 7, you.Y - 7, 14, 14);
        }
        g.DrawString(locked ? "you" : "you (approx)", labelFont, textBrush, you.X + 9, you.Y - 6);

        DrawCaption(g, hintFont, dimBrush);
    }

    /// <summary>Your live X/Y/Z across the top. Falls back to the mob-centroid
    /// estimate, clearly marked, until PlayerFinder locks your real position --
    /// showing an approximation as if it were a fix would be worse than saying
    /// so.</summary>
    private void DrawHeader(Graphics g)
    {
        using var coordFont = new Font("Consolas", 10.5f, FontStyle.Bold);
        using var noteFont = new Font("Segoe UI", 8f);
        using var back = new SolidBrush(Color.FromArgb(0x1A, 0x1A, 0x20));
        using var rule = new Pen(Color.FromArgb(40, 255, 255, 255));
        g.FillRectangle(back, 0, 0, Width, HeaderHeight);
        g.DrawLine(rule, 0, HeaderHeight, Width, HeaderHeight);

        if (_player is { } p)
        {
            using var live = new SolidBrush(YouFill);
            using var dim = new SolidBrush(DimInk);
            var text = $"X {p.X,8:0.0}    Y {p.Y,7:0.0}    Z {p.Z,8:0.0}";
            g.DrawString(text, coordFont, live, 8, 4);
            var w = g.MeasureString(text, coordFont).Width;
            g.DrawString("your position (live)", noteFont, dim, 16 + w, 7);
        }
        else
        {
            using var warn = new SolidBrush(Color.FromArgb(0xFF, 0xB3, 0x4D));
            using var dim = new SolidBrush(DimInk);
            if (_mobs.Count > 0)
            {
                var cx = _mobs.Average(m => m.X);
                var cy = _mobs.Average(m => m.Y);
                var cz = _mobs.Average(m => m.Z);
                var text = $"X {cx,8:0.0}    Y {cy,7:0.0}    Z {cz,8:0.0}";
                g.DrawString(text, coordFont, warn, 8, 4);
                var w = g.MeasureString(text, coordFont).Width;
                g.DrawString(_locate is { IsCompleted: false }
                        ? "APPROX -- LOCATING NOW, keep walking"
                        : "APPROX (mob centroid) -- walk a few seconds to lock your real position",
                    noteFont, dim, 16 + w, 7);
            }
            else g.DrawString("locating your position -- walk around", noteFont, dim, 8, 7);
        }
    }

    private void DrawCaption(Graphics g, Font font, Brush dim)
    {
        var named = _mobs.Count(m => m.Name is not null);
        var line1 = $"{_mobs.Count} mob(s), {named} named   |   zoom {_pixelsPerUnit:0.00} px/unit   |   " +
                    $"{_hz:0} Hz, read {_lastWalkMs:0.#}ms   |   " +
                    (_staleSince is not null ? $"STALE {(DateTime.UtcNow - _staleSince.Value).TotalSeconds:0}s - re-discovering" : _usingLive ? $"LIVE ({_tracker.EntitySlotCount} samples)" : "snapshot - discovering entities...") +
                    $"   |   {(_follow ? "following" : "free pan")}";
        var line2 = _player is { } p
            ? $"wheel = zoom   drag = pan   F = follow   0 = fit   N = names   |   " +
              $"position LOCKED ({p.X:0}, {p.Z:0}) from {_locatedReplicas} copies"
            : _locate is { IsCompleted: false }
                ? "wheel = zoom   drag = pan   F = follow   0 = fit   N = names   |   " +
                  "locating you -- WALK AROUND for a few seconds"
                : "wheel = zoom   drag = pan   F = follow   0 = fit   N = names   |   " +
                  "\"you\" = mob centroid (approx) -- walk around to lock your real position";
        g.DrawString(line1, font, dim, 8, Height - CaptionHeight + 2);
        g.DrawString(line2, font, dim, 8, Height - CaptionHeight + 16);
    }
}
