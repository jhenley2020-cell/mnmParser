using System.Drawing;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>
/// The "Overall" window (toolbar "Totals"): every fight this character has
/// had, folded into one running aggregate that outlives the 50-encounter
/// history cap and app restarts (persisted to
/// <c>logs/totals_&lt;char&gt;.json</c>). The live fight is included on top.
/// "Reset totals" wipes it and starts the tally from now. Damage/Healing
/// toggle and per-combatant ability breakdown, same as the encounter detail
/// window.
/// </summary>
internal sealed class TotalsForm : Form
{
    private readonly MeterState _meterState;
    private readonly string _playerName;
    private readonly string[] _selfScope;

    private readonly System.Windows.Forms.Timer _timer;
    private SplitContainer _split = null!;
    private readonly Label _header;
    private readonly RadioButton _damageRadio;
    private readonly RadioButton _healingRadio;
    private readonly CheckBox _justYou;
    private readonly Label _abilityHeader;
    private readonly CombatantTable _combatants;
    private readonly AbilityTable _abilities;

    private bool _healing;
    private string? _selectedActor;

    public TotalsForm(MeterState meterState, string playerName, IEnumerable<string> petNames, int refreshMs)
    {
        _meterState = meterState;
        _playerName = playerName;
        var pets = petNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        _selfScope = new[] { _playerName }.Concat(pets).ToArray();

        Text = "Overall totals";
        Width = 1000;
        Height = 560;
        MinimumSize = new Size(620, 380);
        StartPosition = FormStartPosition.CenterParent;

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        _damageRadio = new RadioButton { Text = "Damage", Checked = true, AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
        _healingRadio = new RadioButton { Text = "Healing", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
        _damageRadio.CheckedChanged += (_, _) => { if (_damageRadio.Checked) SetMode(false); };
        _healingRadio.CheckedChanged += (_, _) => { if (_healingRadio.Checked) SetMode(true); };
        _justYou = new CheckBox
        {
            Text = pets.Length > 0 ? $"Just {_playerName} + pet" : $"Just {_playerName}",
            AutoSize = true,
            Margin = new Padding(3, 7, 3, 3),
        };
        _justYou.CheckedChanged += (_, _) => RefreshNow();
        toolbar.Controls.Add(_damageRadio);
        toolbar.Controls.Add(_healingRadio);
        toolbar.Controls.Add(_justYou);

        _header = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Font = new Font(Font, FontStyle.Bold),
        };

        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };

        _combatants = new CombatantTable(playerName, pets);
        _combatants.SortChanged += RenderCombatants;
        _combatants.SelectionChanged += (_, _) =>
        {
            if (_combatants.Repopulating || _combatants.ProgrammaticSelection) return;
            _selectedActor = _combatants.SelectedTag as string;
            RenderAbilities();
        };
        var leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_combatants);
        leftPanel.Controls.Add(new Label { Text = "Combatants", Dock = DockStyle.Top, Height = 20 });
        _split.Panel1.Controls.Add(leftPanel);

        _abilities = new AbilityTable();
        _abilities.SortChanged += RenderAbilities;
        _abilityHeader = new Label { Text = "Abilities", Dock = DockStyle.Top, Height = 20 };
        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_abilities);
        rightPanel.Controls.Add(_abilityHeader);
        _split.Panel2.Controls.Add(rightPanel);

        var reset = new Button { Text = "Reset totals", AutoSize = true, Margin = new Padding(6) };
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Wipe the running overall totals for this character and start the tally from now?",
                    "Reset totals", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _meterState.ResetLifetime();
                _selectedActor = null;
                RefreshNow();
            }
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        bar.Controls.Add(reset);

        Controls.Add(_split);
        Controls.Add(bar);
        Controls.Add(_header);
        Controls.Add(toolbar);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(refreshMs, 250) };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();
        RefreshNow();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _split.Panel1MinSize = 240;
        _split.Panel2MinSize = 240;
        var max = Math.Max(_split.Width - _split.Panel2MinSize, _split.Panel1MinSize + 1);
        _split.SplitterDistance = Math.Clamp((int)(_split.Width * 0.52), _split.Panel1MinSize, max);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void SetMode(bool healing)
    {
        _healing = healing;
        _selectedActor = null;
        RefreshNow();
    }

    private IReadOnlyCollection<string>? Scope => _justYou.Checked ? _selfScope : null;

    private void RefreshNow()
    {
        var info = _meterState.GetLifetimeInfo();
        var shown = _healing ? info.TotalHealing : info.TotalDamage;
        var dps = info.CombatSeconds > 0 ? shown / info.CombatSeconds : 0;
        _header.Text =
            $"Since {info.Since.ToLocalTime():yyyy-MM-dd HH:mm}   ·   {info.Encounters:N0} fights   ·   " +
            $"{info.Kills:N0} kills   ·   {FormatSpan(info.CombatSeconds)} in combat   ·   " +
            $"{shown:N0} {(_healing ? "healing" : "damage")}   ·   {dps:N0} {(_healing ? "hps" : "dps")} overall";

        RenderCombatants();
        RenderAbilities();
    }

    private void RenderCombatants()
    {
        var rows = _meterState.LifetimeSnapshot(_healing, Scope, maxRows: 200);
        _selectedActor ??= rows.Count > 0 ? rows[0].Name : null;
        _combatants.Render(rows, _healing, _selectedActor);
    }

    private void RenderAbilities()
    {
        _abilityHeader.Text = _selectedActor is null ? "Abilities" : $"Abilities — {_selectedActor}";
        var rows = _selectedActor is null
            ? null
            : _meterState.LifetimeAbilityBreakdown(_selectedActor, _healing);
        _abilities.Render(rows);
    }

    private static string FormatSpan(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(seconds, 0));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes}m"
            : $"{t.Minutes}m {t.Seconds}s";
    }
}
