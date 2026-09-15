using System.Drawing;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>
/// The "open up more information" window: double-click an encounter (or a
/// combatant) in the main window and this shows the whole fight -- a
/// summary strip (duration / total damage / raid DPS / your DPS /
/// healing), the full combatant table, and the per-ability breakdown for
/// whichever combatant is selected. Refreshes live while the encounter is
/// still going; when the encounter ages out of history it freezes on the
/// last data and says so.
/// </summary>
internal sealed class EncounterDetailForm : Form
{
    private readonly MeterState _meterState;
    private readonly int _encounterId;
    private readonly string _playerName;

    private readonly System.Windows.Forms.Timer _timer;
    private SplitContainer _split = null!;
    private readonly Label _summary;
    private readonly RadioButton _damageRadio;
    private readonly RadioButton _healingRadio;
    private readonly Label _abilityHeader;
    private readonly CombatantTable _combatants;
    private readonly AbilityTable _abilities;

    private bool _healing;
    private string? _selectedActor;
    private bool _frozen;

    public EncounterDetailForm(MeterState meterState, int encounterId, string? initialActor,
        int refreshMs, string playerName, IEnumerable<string>? petNames = null)
    {
        _meterState = meterState;
        _encounterId = encounterId;
        _playerName = playerName;
        _selectedActor = initialActor;

        Width = 1020;
        Height = 580;
        MinimumSize = new Size(620, 380);
        StartPosition = FormStartPosition.CenterParent;
        Text = $"Encounter #{encounterId}";

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        _damageRadio = new RadioButton { Text = "Damage", Checked = true, AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
        _healingRadio = new RadioButton { Text = "Healing", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        _damageRadio.CheckedChanged += (_, _) => { if (_damageRadio.Checked) SetMode(false); };
        _healingRadio.CheckedChanged += (_, _) => { if (_healingRadio.Checked) SetMode(true); };
        toolbar.Controls.Add(_damageRadio);
        toolbar.Controls.Add(_healingRadio);

        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Font = new Font(Font, FontStyle.Bold),
        };

        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };

        _combatants = new CombatantTable(playerName, petNames);
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

        Controls.Add(_split);
        Controls.Add(_summary);
        Controls.Add(toolbar);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(refreshMs, 100) };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();

        RefreshNow();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _split.Panel1MinSize = 260;
        _split.Panel2MinSize = 260;
        var max = Math.Max(_split.Width - _split.Panel2MinSize, _split.Panel1MinSize + 1);
        _split.SplitterDistance = Math.Clamp((int)(_split.Width * 0.5), _split.Panel1MinSize, max);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Called when the user double-clicks the same encounter
    /// again (possibly on a specific combatant) while this window is
    /// already open.</summary>
    public void PreselectActor(string? actor)
    {
        if (actor is not null)
        {
            _selectedActor = actor;
            RenderCombatants();
            RenderAbilities();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void SetMode(bool healing)
    {
        _healing = healing;
        _selectedActor = null; // re-default to the leader of the new mode
        RefreshNow();
    }

    private void RefreshNow()
    {
        var enc = _meterState.GetEncounterSummaries().FirstOrDefault(s => s.Id == _encounterId);
        if (enc is null)
        {
            if (!_frozen)
            {
                _frozen = true;
                _timer.Stop();
                Text += "  —  (no longer tracked)";
            }
            return;
        }

        Text = $"Encounter #{enc.Id} — {enc.Who} — {MainForm.FormatDuration(enc.Duration())}{(enc.IsActive ? "  (live)" : "")}";

        var damageRows = _meterState.Snapshot(enc.Id, healing: false, onlySources: null, maxRows: 200) ?? new List<MeterRow>();
        var healRows = _meterState.Snapshot(enc.Id, healing: true, onlySources: null, maxRows: 200) ?? new List<MeterRow>();
        var totalDmg = damageRows.Sum(r => r.TotalDamage);
        var totalHeal = healRows.Sum(r => r.TotalDamage); // healing snapshot carries the heal amount in this field
        var dur = Math.Max(enc.Duration(), 0.001);
        var you = damageRows.FirstOrDefault(r => r.Name == _playerName || r.Name == "You");

        _summary.Text =
            $"Duration {MainForm.FormatDuration(enc.Duration())}    ·    Damage {totalDmg:N0}    ·    " +
            $"Raid DPS {totalDmg / dur:N0}    ·    Your DPS {(you?.Dps ?? 0):N0}    ·    " +
            $"Healing {totalHeal:N0}    ·    {damageRows.Count} combatant(s)";

        RenderCombatants();
        RenderAbilities();
    }

    private void RenderCombatants()
    {
        var rows = _meterState.Snapshot(_encounterId, _healing, onlySources: null, maxRows: 200) ?? new List<MeterRow>();
        _selectedActor ??= rows.Count > 0 ? rows[0].Name : null;
        _combatants.Render(rows, _healing, _selectedActor);
    }

    private void RenderAbilities()
    {
        _abilityHeader.Text = _selectedActor is null ? "Abilities" : $"Abilities — {_selectedActor}";
        var rows = _selectedActor is null
            ? null
            : _meterState.AbilityBreakdown(_encounterId, _selectedActor, _healing);
        _abilities.Render(rows);
    }
}
