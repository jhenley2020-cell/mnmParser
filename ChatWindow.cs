using System.Drawing;
using System.Globalization;
using MnmDamageParser.Core.CombatLog;

namespace MnmDamageParser.App;

/// <summary>
/// A scrolling view of ONE category of the game's non-combat chat (OOC,
/// Say, Deaths, Loot, ...), or all of it when <see cref="_category"/> is
/// null. Fed by the shared <see cref="ChatLog"/> the poll thread appends to
/// from <see cref="TextLogWatcher.ChatLine"/>; this window filters a
/// snapshot on a timer -- the same poll-a-copy pattern as every window here.
///
/// Combat is deliberately excluded (that's the meter's job): a line only
/// reaches <see cref="ChatLog"/> if it carried a <c>chat_anchors</c>
/// substring, no combat anchor, and matched no combat pattern.
/// </summary>
internal sealed class ChatWindow : Form
{
    private readonly ChatLog _chatLog;
    private readonly string? _category;      // null = combined "Chat" window
    private readonly System.Windows.Forms.Timer _timer;
    private readonly RichTextBox _view;
    private readonly CheckBox _follow;

    private static readonly Color Background = Color.FromArgb(0x1E, 0x1E, 0x1E);
    private static readonly Color DefaultInk = Color.FromArgb(0xDC, 0xDC, 0xDC);
    private static readonly Color TimestampInk = Color.FromArgb(0x80, 0x80, 0x80);

    private long _lastSeq = -1;   // highest ChatEntry.Seq this window has processed
    private int _renderedLines;

    public ChatWindow(ChatLog chatLog, int refreshMs, string? category)
    {
        _chatLog = chatLog;
        _category = category;

        Text = category is null ? "Chat" : $"Chat — {category}";
        Width = 420;
        Height = 300;
        MinimumSize = new Size(220, 120);
        StartPosition = FormStartPosition.Manual;

        _follow = new CheckBox { Text = "Follow", AutoSize = true, Checked = true, Margin = new Padding(6, 4, 10, 4) };
        _follow.CheckedChanged += (_, _) => { if (_follow.Checked) ScrollToEnd(); };

        _view = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Background,
            ForeColor = DefaultInk,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f),
            WordWrap = true,
            HideSelection = false,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        // A wheel-scroll up = "I'm reading history" -- stop yanking the view
        // to the bottom until Follow is re-checked.
        _view.MouseWheel += (_, e) => { if (e.Delta > 0) _follow.Checked = false; };

        var onTop = new CheckBox { Text = "On top", AutoSize = true, Margin = new Padding(0, 4, 10, 4) };
        onTop.CheckedChanged += (_, _) => TopMost = onTop.Checked;

        var clear = new Button { Text = "Clear", AutoSize = true, Margin = new Padding(0, 2, 4, 2) };
        clear.Click += (_, _) =>
        {
            // Per-window only: blanks this view and starts fresh from the
            // newest line -- doesn't touch the shared buffer or other windows.
            _view.Clear();
            _renderedLines = 0;
            var snap = _chatLog.Snapshot();
            if (snap.Count > 0) _lastSeq = snap[^1].Seq;
        };

        var save = new Button { Text = "Save…", AutoSize = true, Margin = new Padding(0, 2, 4, 2) };
        save.Click += (_, _) => SaveToFile();

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4, 0, 4, 0),
        };
        bar.Controls.AddRange(new Control[] { _follow, onTop, clear, save });

        Controls.Add(_view);
        Controls.Add(bar);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Clamp(refreshMs, 300, 1000) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    // Don't steal focus from the game when the whole set auto-opens.
    protected override bool ShowWithoutActivation => true;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void Tick()
    {
        var snap = _chatLog.Snapshot();               // oldest -> newest, Seq ascending
        List<ChatEntry>? pending = null;
        var maxSeq = _lastSeq;
        foreach (var e in snap)
        {
            if (e.Seq <= _lastSeq) continue;
            if (e.Seq > maxSeq) maxSeq = e.Seq;
            if (_category is null || e.Category == _category)
                (pending ??= new List<ChatEntry>()).Add(e);
        }
        if (maxSeq == _lastSeq) return;               // nothing new anywhere
        _lastSeq = maxSeq;
        if (pending is null) return;                  // new lines, none in this category

        _view.SuspendLayout();
        foreach (var entry in pending) Append(entry);
        if (_renderedLines > 4000) TrimTop(_renderedLines - 2000);
        _view.ResumeLayout();
        if (_follow.Checked) ScrollToEnd();
    }

    private void Append(ChatEntry entry)
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds((long)(entry.Timestamp * 1000))
            .ToLocalTime().ToString("HH:mm:ss");

        _view.SelectionStart = _view.TextLength;
        _view.SelectionLength = 0;

        _view.SelectionColor = TimestampInk;
        _view.AppendText(ts + "  ");

        _view.SelectionColor = Readable(entry.ColorHex);
        _view.AppendText(entry.Text + "\n");

        _renderedLines++;
    }

    private void TrimTop(int lines)
    {
        if (lines <= 0) return;
        var cut = _view.GetFirstCharIndexFromLine(lines);
        if (cut <= 0) return;
        _view.Select(0, cut);
        _view.SelectedText = "";
        _renderedLines -= lines;
    }

    private void ScrollToEnd()
    {
        _view.SelectionStart = _view.TextLength;
        _view.SelectionLength = 0;
        _view.ScrollToCaret();
    }

    /// <summary>The game's chat colours are picked for a black HUD; on this
    /// window's near-black background the dark ones (e.g. #7F0000 death
    /// notices) would be unreadable, so lift anything too dim.</summary>
    private static Color Readable(string? hex)
    {
        if (hex is null || !TryParseHex(hex, out var c)) return DefaultInk;
        var lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        if (lum >= 70) return c;
        var boost = (float)(70 / Math.Max(lum, 1));
        return Color.FromArgb(
            Math.Min(255, (int)(c.R * boost)),
            Math.Min(255, (int)(c.G * boost)),
            Math.Min(255, (int)(c.B * boost)));
    }

    private static bool TryParseHex(string hex, out Color color)
    {
        color = Color.Empty;
        if (hex.Length is not (6 or 8)) return false;   // #RGB / #RGBA are rare in-game; skip
        if (!int.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !int.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !int.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return false;
        color = Color.FromArgb(r, g, b);
        return true;
    }

    private void SaveToFile()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Save chat log",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"chat_{(_category ?? "all").ToLowerInvariant()}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var lines = _chatLog.Snapshot()
                .Where(e => _category is null || e.Category == _category)
                .Select(e =>
                {
                    var ts = DateTimeOffset.FromUnixTimeMilliseconds((long)(e.Timestamp * 1000))
                        .ToLocalTime().ToString("HH:mm:ss");
                    return $"{ts}\t{e.Text}";
                });
            File.WriteAllLines(dlg.FileName, lines);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't save: " + ex.Message, "Chat log",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
