using System.Drawing;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>
/// A standalone window that shows just your pet(s)' damage for the current
/// (or most recent) encounter -- their combatant row(s) up top, and the
/// per-ability breakdown for the selected pet below. Live-refreshing;
/// there's an "always on top" toggle so you can keep it in a corner.
/// </summary>
internal sealed class PetWindow : Form
{
    private readonly MeterState _meterState;
    private readonly string[] _petNames;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly Label _header;
    private readonly CombatantTable _pets;
    private readonly AbilityTable _abilities;
    private readonly Label _abilityHeader;
    private string? _selectedPet;

    public PetWindow(MeterState meterState, IEnumerable<string> petNames, int refreshMs)
    {
        _meterState = meterState;
        _petNames = petNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();

        Text = _petNames.Length == 1 ? $"Pet damage — {_petNames[0]}" : "Pet damage";
        Width = 560;
        Height = 420;
        MinimumSize = new Size(360, 260);
        StartPosition = FormStartPosition.Manual;

        _header = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Consolas", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

        _pets = new CombatantTable(playerName: "", petNames: _petNames); // pet rows only
        _pets.SortChanged += RefreshNow;
        _pets.SelectionChanged += (_, _) =>
        {
            if (_pets.Repopulating || _pets.ProgrammaticSelection) return;
            _selectedPet = _pets.SelectedTag as string;
            RenderAbilities();
        };
        var top = new Panel { Dock = DockStyle.Fill };
        top.Controls.Add(_pets);
        split.Panel1.Controls.Add(top);

        _abilityHeader = new Label { Dock = DockStyle.Top, Height = 20, Text = "Abilities" };
        _abilities = new AbilityTable();
        _abilities.SortChanged += RenderAbilities;
        var bottom = new Panel { Dock = DockStyle.Fill };
        bottom.Controls.Add(_abilities);
        bottom.Controls.Add(_abilityHeader);
        split.Panel2.Controls.Add(bottom);

        var onTop = new CheckBox { Text = "Always on top", AutoSize = true, Margin = new Padding(6) };
        onTop.CheckedChanged += (_, _) => TopMost = onTop.Checked;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        bar.Controls.Add(onTop);

        Controls.Add(split);
        Controls.Add(bar);
        Controls.Add(_header);

        _split = split;
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(refreshMs, 250) };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();
        RefreshNow();
    }

    private readonly SplitContainer _split;

    // Don't yank foreground off the game when we pop up mid-fight.
    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _split.SplitterDistance = Math.Clamp(_split.Height / 2, 60, Math.Max(61, _split.Height - 60));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void RefreshNow()
    {
        var enc = _meterState.GetEncounterSummaries().FirstOrDefault();
        if (enc is null)
        {
            _header.Text = "No fights yet";
            _pets.Render(Array.Empty<MeterRow>(), healing: false, null);
            _abilities.Render(null);
            return;
        }

        var rows = _meterState.Snapshot(enc.Id, healing: false, onlySources: _petNames, maxRows: 20)
                   ?? new List<MeterRow>();
        var total = rows.Sum(r => r.TotalDamage);
        var dps = rows.Sum(r => r.Dps);
        var state = enc.IsActive ? "live" : "ended";
        _header.Text = _petNames.Length == 0
            ? "Set pet_names in settings.json"
            : $"Encounter #{enc.Id} ({state})   ·   pet damage {total:N0}   ·   {dps:N0} dps";

        _selectedPet ??= rows.Count > 0 ? rows[0].Name : null;
        _pets.Render(rows, healing: false, _selectedPet);
        RenderAbilities();
    }

    private void RenderAbilities()
    {
        var enc = _meterState.GetEncounterSummaries().FirstOrDefault();
        _abilityHeader.Text = _selectedPet is null ? "Abilities" : $"Abilities — {_selectedPet}";
        var rows = enc is not null && _selectedPet is not null
            ? _meterState.AbilityBreakdown(enc.Id, _selectedPet, healing: false)
            : null;
        _abilities.Render(rows);
    }
}
