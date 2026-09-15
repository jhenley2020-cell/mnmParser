using System.Drawing;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>
/// The personal-bests window (toolbar "Records" button): your largest
/// single hit with each ability/spell and with each damage type, for the
/// current character. Live-refreshes; "Reset" wipes them for this
/// character. New records are also announced by voice as they happen.
/// </summary>
internal sealed class RecordsForm : Form
{
    private readonly MeterState _meterState;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly MeterGrid _abilityGrid;
    private readonly MeterGrid _typeGrid;

    public RecordsForm(MeterState meterState, int refreshMs)
    {
        _meterState = meterState;

        Text = "Personal bests — biggest single hit";
        Width = 640;
        Height = 460;
        MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterParent;

        _abilityGrid = new MeterGrid(
            new[] { ("Name", "Ability / spell"), ("Best", "Biggest hit") },
            new[] { "Best" }, defaultSortColumn: "Best");
        _typeGrid = new MeterGrid(
            new[] { ("Name", "Damage type"), ("Best", "Biggest hit") },
            new[] { "Best" }, defaultSortColumn: "Best");
        _abilityGrid.SortChanged += RefreshNow;
        _typeGrid.SortChanged += RefreshNow;

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_abilityGrid);
        left.Controls.Add(new Label { Text = "By ability / spell", Dock = DockStyle.Top, Height = 20 });
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_typeGrid);
        right.Controls.Add(new Label { Text = "By damage type", Dock = DockStyle.Top, Height = 20 });
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        var reset = new Button { Text = "Reset records", AutoSize = true, Margin = new Padding(6) };
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Wipe all personal bests for this character?", "Reset records",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _meterState.ResetRecords();
                RefreshNow();
            }
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        bar.Controls.Add(reset);

        Controls.Add(split);
        Controls.Add(bar);

        _split = split;
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(refreshMs, 250) };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();
        RefreshNow();
    }

    private readonly SplitContainer _split;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _split.SplitterDistance = _split.Width / 2;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private void RefreshNow()
    {
        var snap = _meterState.GetRecords();
        Fill(_abilityGrid, snap.Abilities);
        Fill(_typeGrid, snap.DamageTypes);
    }

    private static void Fill(MeterGrid grid, IReadOnlyList<(string Name, int Best)> rows)
    {
        var list = rows.ToList();
        grid.ApplySort(list, col => col == "Best"
            ? (a, b) => a.Best.CompareTo(b.Best)
            : (a, b) => string.CompareOrdinal(a.Name, b.Name));
        var gridRows = list
            .Select(r => grid.MakeRow(new object?[] { r.Name, r.Best.ToString("N0") }, r.Name))
            .ToList();
        grid.SetRows(gridRows);
    }
}
