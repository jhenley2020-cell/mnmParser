using System.Drawing;
using MnmDamageParser.Core.Config;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>
/// Advanced-Combat-Tracker-style main window: two panes only -- the
/// encounter list on the left, the combatant table on the right (colour
/// per combatant, an in-cell bar behind the "%" column). Double-clicking
/// an encounter OR a combatant opens an <see cref="EncounterDetailForm"/>
/// with the deeper stats and the per-ability breakdown.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly MeterState _meterState;
    private readonly Settings _settings;
    private readonly VoiceAnnouncer? _voice;
    private readonly string? _settingsPath;
    private readonly string _playerName;
    private readonly IReadOnlyList<string> _petNames;
    private readonly string[] _selfScope;   // player + pets, for the "Just me" filter

    private readonly Core.Xp.XpBarConfig? _xpConfig;
    private readonly string? _xpConfigPath;
    private readonly Core.CombatLog.ChatLog? _chatLog;
    private readonly Core.CombatLog.ChatCategorizer? _chatCategorizer;
    private XpSampler? _xpSampler;
    private XpPanel? _xpPanel;
    private Button _xpButton = null!;
    private int _lastXpLevels;

    private bool _healing;
    private bool _scopeAll = true;
    private int? _selectedEncounterId;

    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<int, EncounterDetailForm> _details = new();

    private SplitContainer _split = null!;
    private RadioButton _damageRadio = null!;
    private RadioButton _healingRadio = null!;
    private RadioButton _allRadio = null!;
    private RadioButton _selfRadio = null!;
    private Label _statusLabel = null!;
    private Label _clockLabel = null!;
    private Panel _clockStrip = null!;
    private Label _combatantLabel = null!;
    private MeterGrid _encounterGrid = null!;
    private CombatantTable _combatantGrid = null!;

    private string? _shownCharacter;

    private sealed record EncounterListRow(int Id, string Started, double DurationSeconds, int Total, string Who);

    public MainForm(MeterState meterState, Settings settings, VoiceAnnouncer? voice, string? settingsPath,
        string? characterName = null, Core.Xp.XpBarConfig? xpConfig = null, string? xpConfigPath = null,
        Core.CombatLog.ChatLog? chatLog = null, Core.CombatLog.ChatCategorizer? chatCategorizer = null)
    {
        _meterState = meterState;
        _settings = settings;
        _voice = voice;
        _settingsPath = settingsPath;
        _xpConfig = xpConfig;
        _xpConfigPath = xpConfigPath;
        _chatLog = chatLog;
        _chatCategorizer = chatCategorizer;
        _playerName = settings.Window.PlayerName;
        _petNames = settings.PetNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        _selfScope = new[] { _playerName }.Concat(_petNames).ToArray();
        _shownCharacter = characterName;

        Text = TitleFor(characterName);
        Width = Math.Max(settings.Window.Width, 720);
        Height = Math.Max(settings.Window.Height, 420);
        MinimumSize = new Size(680, 380);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();

        if (_xpConfig is not null)
            _xpSampler = new XpSampler(_xpConfig);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(settings.Window.RefreshMs, 50) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // -- layout ----------------------------------------------------------

    private void BuildUi()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6),
        };

        _damageRadio = new RadioButton { Text = "Damage", Checked = true, AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
        _healingRadio = new RadioButton { Text = "Healing", AutoSize = true, Margin = new Padding(3, 6, 20, 3) };
        _damageRadio.CheckedChanged += (_, _) => { if (_damageRadio.Checked) { _healing = false; RefreshNow(); } };
        _healingRadio.CheckedChanged += (_, _) => { if (_healingRadio.Checked) { _healing = true; RefreshNow(); } };

        _allRadio = new RadioButton { Text = "All combatants", Checked = true, AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
        _selfRadio = new RadioButton
        {
            Text = _petNames.Count > 0 ? $"Just {_playerName} + pet" : $"Just {_playerName}",
            AutoSize = true, Margin = new Padding(3, 6, 20, 3),
        };
        _allRadio.CheckedChanged += (_, _) => { if (_allRadio.Checked) { _scopeAll = true; RefreshNow(); } };
        _selfRadio.CheckedChanged += (_, _) => { if (_selfRadio.Checked) { _scopeAll = false; RefreshNow(); } };

        var clearButton = new Button { Text = "Clear History", AutoSize = true, Margin = new Padding(3) };
        clearButton.Click += (_, _) => { _meterState.Reset(); _selectedEncounterId = null; RefreshNow(); };

        var recordsButton = new Button { Text = "Records", AutoSize = true, Margin = new Padding(3) };
        recordsButton.Click += (_, _) => ShowRecords();

        var totalsButton = new Button { Text = "Totals", AutoSize = true, Margin = new Padding(3) };
        totalsButton.Click += (_, _) => ShowTotals();

        var timersButton = new Button { Text = "Timers", AutoSize = true, Margin = new Padding(3) };
        timersButton.Click += (_, _) => ShowDebuffs();

        var chatButton = new Button { Text = "Chat", AutoSize = true, Margin = new Padding(3), Enabled = _chatLog is not null };
        chatButton.Click += (_, _) => ShowChatWindows();

        var petButton = new Button { Text = "Pets", AutoSize = true, Margin = new Padding(3), Enabled = _petNames.Count > 0 };
        petButton.Click += (_, _) => ShowPets();

        _xpButton = new Button { Text = "XP", AutoSize = true, Margin = new Padding(3), Enabled = _xpConfig is not null };
        _xpButton.Click += (_, _) => ToggleXpPanel();

        var voiceButton = BuildVoiceButton();

        var hint = new Label
        {
            Text = "double-click an encounter or combatant for detail",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(10, 9, 3, 3),
        };

        toolbar.Controls.AddRange(new Control[]
            { _damageRadio, _healingRadio, _allRadio, _selfRadio, clearButton, recordsButton, totalsButton, timersButton, chatButton, petButton, _xpButton, voiceButton, hint });

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            Text = "Waiting for combat...",
        };

        // In-world clock -- its own bar at the very top, big and obvious.
        _clockLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x28, 0x34, 0x46),
            Cursor = Cursors.Hand,
            Text = "◷  set in-world time  —  run  /time  in game, or click here",
        };
        _clockLabel.Click += (_, _) => SetGameTimeDialog();
        _clockStrip = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = Color.FromArgb(0xEC, 0xEF, 0xF4),
            Padding = new Padding(0, 0, 0, 1),
        };
        _clockStrip.Controls.Add(_clockLabel);

        if (_xpConfig is not null)
        {
            _xpPanel = new XpPanel { Visible = _settings.ShowXpPanel };
            _xpPanel.CalibrateClicked += CalibrateXp;
            _xpPanel.ResetClicked += () => { _xpSampler?.ResetSession(); _lastXpLevels = 0; };
        }

        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };

        _encounterGrid = new MeterGrid(
            new[] { ("Started", "Started"), ("Duration", "Dur"), ("Total", "Total"), ("Who", "Combatants") },
            new[] { "Duration", "Total" },
            defaultSortColumn: null);
        _encounterGrid.Columns["Started"]!.FillWeight = 95;
        _encounterGrid.Columns["Duration"]!.FillWeight = 42;
        _encounterGrid.Columns["Total"]!.FillWeight = 60;
        _encounterGrid.Columns["Who"]!.FillWeight = 120;
        _encounterGrid.SortChanged += RefreshNow;
        _encounterGrid.SelectionChanged += (_, _) =>
        {
            if (_encounterGrid.Repopulating || _encounterGrid.ProgrammaticSelection) return;
            if (_encounterGrid.SelectedTag is int id) { _selectedEncounterId = id; RefreshNow(); }
        };
        _encounterGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _encounterGrid.Rows[e.RowIndex].Tag is int id) OpenDetail(id, null);
        };
        var leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_encounterGrid);
        leftPanel.Controls.Add(new Label { Text = "Encounters", Dock = DockStyle.Top, Height = 20 });
        _split.Panel1.Controls.Add(leftPanel);

        _combatantGrid = new CombatantTable(_playerName, _petNames);
        _combatantGrid.SortChanged += RefreshNow;
        _combatantGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _selectedEncounterId is int id && _combatantGrid.Rows[e.RowIndex].Tag is string actor)
                OpenDetail(id, actor);
        };
        _combatantLabel = new Label { Text = "Combatants", Dock = DockStyle.Top, Height = 20 };
        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_combatantGrid);
        rightPanel.Controls.Add(_combatantLabel);
        _split.Panel2.Controls.Add(rightPanel);

        Controls.Add(_split);
        if (_xpPanel is not null) Controls.Add(_xpPanel);  // docks above the status strip
        Controls.Add(_statusLabel);
        Controls.Add(toolbar);
        Controls.Add(_clockStrip);   // added last -> sits at the very top
    }

    // -- XP section ----------------------------------------------------------

    private void ToggleXpPanel()
    {
        if (_xpPanel is null) return;
        _xpPanel.Visible = !_xpPanel.Visible;
        _settings.ShowXpPanel = _xpPanel.Visible;
        SaveSettings();
        if (_xpPanel.Visible) RefreshXp();
    }

    private void CalibrateXp()
    {
        if (_xpConfig is null) return;
        using var f = new XpCalibrationForm(_xpConfig);
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            try { if (_xpConfigPath is not null) _xpConfig.Save(_xpConfigPath); }
            catch (Exception ex) { _statusLabel.Text = "Couldn't save XP calibration: " + ex.Message; }
            _xpSampler?.ResetSession();
            _lastXpLevels = 0;
        }
    }

    private void RefreshXp()
    {
        if (_xpPanel is null || !_xpPanel.Visible || _xpConfig is null) return;
        var s = _xpSampler?.Latest ?? Core.Xp.XpStats.Empty;
        _xpPanel.Show(s, _xpConfig.IsCalibrated, _xpSampler?.Error);
        if (s.LevelsGained > _lastXpLevels)
        {
            _lastXpLevels = s.LevelsGained;
            _voice?.OnLevelUp();
        }
    }

    // -- voice + records -------------------------------------------------

    private Button _voiceButton = null!;

    private Button BuildVoiceButton()
    {
        var v = _settings.Voice;
        var menu = new ContextMenuStrip();

        ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set)
        {
            var mi = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = get() };
            mi.CheckedChanged += (_, _) => { set(mi.Checked); OnVoiceChanged(); };
            return mi;
        }

        var header = new ToolStripMenuItem("Speak when…") { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(Toggle("  you enter combat", () => v.AnnounceEngage, x => v.AnnounceEngage = x));
        menu.Items.Add(Toggle("  you land a hit", () => v.AnnounceHits, x => v.AnnounceHits = x));
        menu.Items.Add(Toggle("  you miss", () => v.AnnounceMisses, x => v.AnnounceMisses = x));
        menu.Items.Add(Toggle("  you're resisted", () => v.AnnounceResists, x => v.AnnounceResists = x));
        menu.Items.Add(Toggle("  something new attacks you", () => v.AnnounceAttackers, x => v.AnnounceAttackers = x));
        menu.Items.Add(Toggle("  you kill something", () => v.AnnounceKills, x => v.AnnounceKills = x));
        menu.Items.Add(Toggle("  combat ends", () => v.AnnounceDisengage, x => v.AnnounceDisengage = x));
        menu.Items.Add(Toggle("  you set a new record", () => v.AnnounceRecords, x => v.AnnounceRecords = x));
        menu.Items.Add(Toggle("  you level up", () => v.AnnounceLevelUps, x => v.AnnounceLevelUps = x));
        menu.Items.Add(new ToolStripSeparator());

        // Voice picker
        var voiceSub = new ToolStripMenuItem("Voice");
        var installed = VoiceAnnouncer.InstalledVoiceNames();
        foreach (var name in installed)
        {
            var mi = new ToolStripMenuItem(name)
            {
                CheckOnClick = true,
                Checked = !string.IsNullOrWhiteSpace(v.VoiceName) && name.Contains(v.VoiceName, StringComparison.OrdinalIgnoreCase),
            };
            mi.Click += (_, _) =>
            {
                foreach (ToolStripMenuItem other in voiceSub.DropDownItems.OfType<ToolStripMenuItem>())
                    other.Checked = ReferenceEquals(other, mi);
                v.VoiceName = name;
                OnVoiceChanged();
                _voice?.Test();
            };
            voiceSub.DropDownItems.Add(mi);
        }
        if (installed.Count == 0)
            voiceSub.DropDownItems.Add(new ToolStripMenuItem("(no voices installed)") { Enabled = false });
        menu.Items.Add(voiceSub);

        menu.Items.Add(Toggle("Elf pitch (lighter)",
            () => !string.IsNullOrWhiteSpace(v.VoicePitch),
            x => v.VoicePitch = x ? "+6" : ""));

        menu.Items.Add(new ToolStripSeparator());
        var test = new ToolStripMenuItem("Test voice");
        test.Click += (_, _) => _statusLabel.Text = "Voice: " + (_voice?.Test() ?? "not available");
        menu.Items.Add(test);

        _voiceButton = new Button { AutoSize = true, Margin = new Padding(12, 3, 3, 3), Enabled = _voice is not null };
        _voiceButton.Click += (_, _) => menu.Show(_voiceButton, new Point(0, _voiceButton.Height));
        UpdateVoiceButton();
        return _voiceButton;
    }

    private void UpdateVoiceButton() =>
        _voiceButton.Text = _settings.Voice.AnyOn ? "🔊 Voice ▾" : "🔈 Voice ▾";

    private void OnVoiceChanged()
    {
        _voice?.Apply(_settings.Voice);
        UpdateVoiceButton();
        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_settingsPath is null) return;
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) { _statusLabel.Text = "Couldn't save settings: " + ex.Message; }
    }

    private RecordsForm? _recordsForm;

    private void ShowRecords()
    {
        if (_recordsForm is { IsDisposed: false }) { _recordsForm.Activate(); return; }
        _recordsForm = new RecordsForm(_meterState, Math.Max(_settings.Window.RefreshMs, 250));
        _recordsForm.FormClosed += (_, _) => _recordsForm = null;
        _recordsForm.Show(this);
    }

    private TotalsForm? _totalsForm;

    private void ShowTotals()
    {
        if (_totalsForm is { IsDisposed: false }) { _totalsForm.Activate(); return; }
        _totalsForm = new TotalsForm(_meterState, _playerName, _petNames, Math.Max(_settings.Window.RefreshMs, 250));
        _totalsForm.FormClosed += (_, _) => _totalsForm = null;
        _totalsForm.Show(this);
    }

    private DebuffWindow? _debuffWindow;
    private bool _debuffAutoOpened;

    private void ShowDebuffs(bool activate = true)
    {
        if (_debuffWindow is { IsDisposed: false }) { if (activate) _debuffWindow.Activate(); return; }
        _debuffWindow = new DebuffWindow(_meterState, _settings.Window.RefreshMs)
        {
            Location = new Point(Right + 8, Top + 260),
        };
        _debuffWindow.FormClosed += (_, _) => _debuffWindow = null;
        _debuffWindow.Show(this);
    }

    /// <summary>Open the timers window the first time a DoT/debuff is
    /// actually running (only once per session -- close it and it stays
    /// closed). No-op if nothing is configured in spells.json.</summary>
    private void MaybeAutoOpenDebuffs()
    {
        if (_debuffAutoOpened || _debuffWindow is { IsDisposed: false }) return;
        if (_meterState.GetDebuffTimers().Count == 0) return;
        _debuffAutoOpened = true;
        ShowDebuffs(activate: false);
    }

    private readonly List<ChatWindow> _chatWindows = new();

    /// <summary>Opens one Chat window per <c>config/chat.json</c> category
    /// (plus "Other"), tiled down the right edge of the screen and wrapping
    /// into more columns as needed. No categories configured -> a single
    /// combined "Chat" window. Re-clicking with windows already open just
    /// brings them all forward; if they've all been closed it re-tiles.</summary>
    private void ShowChatWindows()
    {
        if (_chatLog is null) return;

        _chatWindows.RemoveAll(w => w.IsDisposed);
        if (_chatWindows.Count > 0)
        {
            foreach (var w in _chatWindows) { w.WindowState = FormWindowState.Normal; w.BringToFront(); }
            return;
        }

        var categories = (_chatCategorizer?.IsSplit ?? false)
            ? _chatCategorizer!.CategoryNames.Select(n => (string?)n).ToList()
            : new List<string?> { null };   // one combined window

        var area = Screen.FromControl(this).WorkingArea;
        const int winW = 340, winH = 200, gap = 4;
        var perColumn = Math.Max(1, (area.Height - 8) / (winH + gap));
        var refresh = Math.Max(_settings.Window.RefreshMs, 300);

        for (var i = 0; i < categories.Count; i++)
        {
            var col = i / perColumn;
            var row = i % perColumn;
            var win = new ChatWindow(_chatLog, refresh, categories[i])
            {
                Size = new Size(winW, winH),
                Location = new Point(area.Right - (col + 1) * (winW + gap), area.Top + row * (winH + gap)),
            };
            _chatWindows.Add(win);
            win.FormClosed += (_, _) => _chatWindows.Remove(win);
            win.Show(this);
        }
    }

    // -- in-world clock ------------------------------------------------------

    private string _lastClockText = "";
    private Color _lastClockColor;
    private string? _lastClockTip;
    private ToolTip? _clockTip;

    private static readonly Color ClockDay = Color.FromArgb(0x28, 0x34, 0x46);
    private static readonly Color ClockNight = Color.FromArgb(0x3b, 0x4a, 0x86);
    private static readonly Color ClockUnset = SystemColors.GrayText;

    private void RefreshClock()
    {
        var (now, dateText) = _meterState.GetGameTime();

        string text;
        Color color;
        if (now is null)
        {
            text = "◷   run  /time  in game to set the clock   (or click here)";
            color = ClockUnset;
        }
        else
        {
            var season = ExtractSeason(dateText);
            text = $"{(now.IsNight ? "☾" : "☀")}   {now.Label}" + (season is null ? "" : $"     ·     {season}");
            color = now.IsNight ? ClockNight : ClockDay;
        }
        if (text != _lastClockText) { _clockLabel.Text = text; _lastClockText = text; }
        if (color != _lastClockColor) { _clockLabel.ForeColor = color; _lastClockColor = color; }

        var tip = dateText is null
            ? "In-world time. Run /time in game and it sets automatically; or click to set it by hand."
            : $"{dateText}\n(from /time — 72 real minutes per in-world day. Click to set by hand.)";
        if (tip != _lastClockTip)
        {
            (_clockTip ??= new ToolTip()).SetToolTip(_clockLabel, tip);
            _lastClockTip = tip;
        }
    }

    private static string? ExtractSeason(string? dateText)
    {
        if (string.IsNullOrEmpty(dateText)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(dateText, @"It is (\w+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private void SetGameTimeDialog()
    {
        using var dlg = new Form
        {
            Text = "Set in-world time",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(300, 116),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var hour = new NumericUpDown { Minimum = 1, Maximum = 12, Value = 12, Left = 14, Top = 16, Width = 48 };
        var colon = new Label { Text = ":", Left = 64, Top = 20, AutoSize = true };
        var min = new NumericUpDown { Minimum = 0, Maximum = 59, Value = 0, Left = 76, Top = 16, Width = 48 };
        var ap = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 132, Top = 16, Width = 58 };
        ap.Items.AddRange(new object[] { "AM", "PM" });
        ap.SelectedIndex = 0;

        var (cur, _) = _meterState.GetGameTime();
        if (cur is not null)
        {
            hour.Value = cur.Hour12;
            min.Value = cur.Minute;
            ap.SelectedItem = cur.AmPm;
        }

        var hint = new Label
        {
            Text = "Type what the game's /time shows (e.g. 3 AM → 3 : 0 AM).",
            Left = 14, Top = 48, AutoSize = true, ForeColor = SystemColors.GrayText,
        };
        var ok = new Button { Text = "Set", DialogResult = DialogResult.OK, Left = 132, Top = 76, Width = 70 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 210, Top = 76, Width = 76 };
        dlg.Controls.AddRange(new Control[] { hour, colon, min, ap, hint, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) == DialogResult.OK
            && Core.GameTime.GameClock.ParseClock12((int)hour.Value, (int)min.Value, (string)ap.SelectedItem!) is { } minuteOfDay)
        {
            _meterState.SetGameTime(minuteOfDay);
            RefreshClock();
        }
    }

    private PetWindow? _petWindow;
    private int _petPopEncounterId = -1;

    private void ShowPets(bool activate = true)
    {
        if (_petNames.Count == 0) return;
        if (_petWindow is { IsDisposed: false }) { if (activate) _petWindow.Activate(); return; }
        _petWindow = new PetWindow(_meterState, _petNames, Math.Max(_settings.Window.RefreshMs, 250))
        {
            Location = new Point(Right + 8, Top),
        };
        _petWindow.FormClosed += (_, _) => _petWindow = null;
        _petWindow.Show(this);
    }

    /// <summary>Auto-open the pet window the first time a pet deals damage in
    /// the newest (live) fight. Once per encounter -- if the user closes it,
    /// it stays closed until the next fight.</summary>
    private void MaybeAutoPopPetWindow(List<EncounterSummary> encounters)
    {
        if (!_settings.PetWindowAutoOpen || _petNames.Count == 0) return;
        if (encounters.Count == 0) return;
        var newest = encounters[0];
        if (!newest.IsActive || newest.Id == _petPopEncounterId) return;
        if (_petWindow is { IsDisposed: false }) { _petPopEncounterId = newest.Id; return; }

        var petRows = _meterState.Snapshot(newest.Id, healing: false, onlySources: _petNames, 5);
        if (petRows is null || petRows.Sum(r => r.TotalDamage) <= 0) return;

        _petPopEncounterId = newest.Id;
        ShowPets(activate: false);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Panel min sizes + splitter position must be applied once the
        // container has its real docked width -- setting them in the
        // initializer trips SplitterDistance validation against the
        // default 150px width.
        _split.Panel1MinSize = 160;
        _split.Panel2MinSize = 320;
        var max = Math.Max(_split.Width - _split.Panel2MinSize, _split.Panel1MinSize + 1);
        _split.SplitterDistance = Math.Clamp(280, _split.Panel1MinSize, max);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _xpSampler?.Dispose();
        base.OnFormClosed(e);
    }

    // -- detail windows -------------------------------------------------

    private void OpenDetail(int encounterId, string? actor)
    {
        if (_details.TryGetValue(encounterId, out var existing))
        {
            existing.PreselectActor(actor);
            return;
        }
        var form = new EncounterDetailForm(_meterState, encounterId, actor, _timer.Interval, _playerName, _petNames);
        form.FormClosed += (_, _) => _details.Remove(encounterId);
        _details[encounterId] = form;
        form.Show(this);
    }

    // -- refresh loop --------------------------------------------------

    private static string TitleFor(string? characterName) =>
        characterName is null ? "M&M Damage Parser" : $"M&M Damage Parser — {characterName}";

    private void Tick()
    {
        _meterState.Touch();

        var current = _meterState.CurrentCharacterName;
        if (!string.Equals(current, _shownCharacter, StringComparison.Ordinal))
        {
            _shownCharacter = current;
            Text = TitleFor(current);
        }

        RefreshNow();
        RefreshXp();
    }

    private void RefreshNow()
    {
        // All data comes from MeterState's thread-safe DTO API -- this UI
        // thread never touches a live Encounter/ActorStats the poll thread
        // could be mutating.
        var encounters = _meterState.GetEncounterSummaries();
        var ids = encounters.Select(e => e.Id).ToList();
        if (_selectedEncounterId is null || !ids.Contains(_selectedEncounterId.Value))
            _selectedEncounterId = ids.Count > 0 ? ids[0] : null;

        RenderEncounterList(encounters);
        MaybeAutoPopPetWindow(encounters);
        MaybeAutoOpenDebuffs();
        RefreshClock();

        var enc = encounters.FirstOrDefault(e => e.Id == _selectedEncounterId);
        if (enc is null)
        {
            _combatantGrid.Render(Array.Empty<MeterRow>(), _healing, null);
            _combatantLabel.Text = "Combatants";
            _statusLabel.Text = "Waiting for combat...";
            return;
        }

        var snapshot = _meterState.Snapshot(enc.Id, _healing, _scopeAll ? null : _selfScope, 50)
                       ?? new List<MeterRow>();
        _combatantGrid.Render(snapshot, _healing, _combatantGrid.SelectedTag as string);
        _combatantLabel.Text = $"Combatants — {enc.Who}";

        var state = enc.IsActive ? "live" : "ended";
        _statusLabel.Text =
            $"Encounter #{enc.Id} ({state}) — {FormatDuration(enc.Duration())} — " +
            $"{enc.TotalDamage:N0} dmg — started {FormatTime(enc.StartTime)}";
    }

    private void RenderEncounterList(List<EncounterSummary> encounters)
    {
        var rows = encounters.Select(e => new EncounterListRow(
            e.Id,
            (e.IsActive ? "● " : "") + FormatTime(e.StartTime),
            e.Duration(),
            e.TotalDamage,
            e.Who)).ToList();

        _encounterGrid.ApplySort(rows, col => col switch
        {
            "Started" => (a, b) => string.CompareOrdinal(a.Started, b.Started),
            "Duration" => (a, b) => a.DurationSeconds.CompareTo(b.DurationSeconds),
            "Total" => (a, b) => a.Total.CompareTo(b.Total),
            "Who" => (a, b) => string.CompareOrdinal(a.Who, b.Who),
            _ => (_, _) => 0,
        });

        var gridRows = rows
            .Select(r => _encounterGrid.MakeRow(
                new object?[] { r.Started, FormatDuration(r.DurationSeconds), r.Total.ToString("N0"), r.Who },
                r.Id))
            .ToList();
        _encounterGrid.SetRows(gridRows, _selectedEncounterId);
    }

    private static string FormatTime(double unixSeconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)).ToLocalTime().ToString("HH:mm:ss");

    internal static string FormatDuration(double seconds)
    {
        seconds = Math.Max(seconds, 0);
        return $"{(int)seconds / 60}:{(int)seconds % 60:D2}";
    }
}
