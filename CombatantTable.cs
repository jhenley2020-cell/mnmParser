using System.Drawing;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>
/// The combatant grid (Name / Total / DPS / % / Hits / Crits / Misses /
/// Acc) used in both the main window and the encounter-detail window.
/// Draws an ACT-style bar behind the "%" cell scaled to the top row, and
/// tints each row by combatant so you can track someone across encounters.
/// </summary>
internal sealed class CombatantTable : MeterGrid
{
    private static readonly (string, string)[] Cols =
    {
        ("Name", "Name"), ("Total", "Total"), ("Dps", "DPS/HPS"), ("Pct", "%"),
        ("Hits", "Hits"), ("Crits", "Crits"), ("Misses", "Misses"), ("Acc", "Acc %"),
    };

    private static readonly string[] Numeric = { "Total", "Dps", "Pct", "Hits", "Crits", "Misses", "Acc" };

    private readonly string _playerName;
    private readonly HashSet<string> _petNames;
    private double _maxPct = 1;
    private int _pctColIndex;

    public CombatantTable(string playerName, IEnumerable<string>? petNames = null)
        : base(Cols, Numeric, defaultSortColumn: "Total")
    {
        _playerName = playerName;
        _petNames = new HashSet<string>(petNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _pctColIndex = Columns["Pct"]!.Index;
        Columns["Name"]!.FillWeight = 160;
        Columns["Pct"]!.FillWeight = 90;

        CellPainting += PaintPercentBar;
    }

    /// <summary>Rebuild from a MeterState snapshot. Returns nothing; read
    /// <see cref="MeterGrid.SelectedTag"/> (a combatant name) for the
    /// selection.</summary>
    public void Render(IReadOnlyList<MeterRow> snapshot, bool healing, string? keepSelected)
    {
        var rows = snapshot.ToList();
        ApplySort(rows, SortComparison);

        _maxPct = rows.Count > 0 ? Math.Max(rows.Max(r => r.Percent), 0.0001) : 1;

        var gridRows = new List<DataGridViewRow>(rows.Count);
        foreach (var r in rows)
        {
            var isPet = _petNames.Contains(r.Name);
            var row = MakeRow(new object?[]
            {
                isPet ? r.Name + "  (pet)" : r.Name,
                r.TotalDamage.ToString("N0"),
                r.Dps.ToString("N0") + (healing ? " hps" : " dps"),
                r.Percent.ToString("N1") + "%",
                r.Hits,
                healing ? "" : r.Crits.ToString(),
                healing ? "" : r.Misses.ToString(),
                healing ? "" : r.Accuracy.ToString("N0") + "%",
            }, r.Name);
            // pets are tinted like you, so you + pet read as one block
            row.DefaultCellStyle.BackColor = UiColors.ForActor(isPet ? _playerName : r.Name, _playerName);
            gridRows.Add(row);
        }
        SetRows(gridRows, keepSelected);
    }

    private static Comparison<MeterRow> SortComparison(string col) => col switch
    {
        "Name" => (a, b) => string.CompareOrdinal(a.Name, b.Name),
        "Total" => (a, b) => a.TotalDamage.CompareTo(b.TotalDamage),
        "Dps" => (a, b) => a.Dps.CompareTo(b.Dps),
        "Pct" => (a, b) => a.Percent.CompareTo(b.Percent),
        "Hits" => (a, b) => a.Hits.CompareTo(b.Hits),
        "Crits" => (a, b) => a.Crits.CompareTo(b.Crits),
        "Misses" => (a, b) => a.Misses.CompareTo(b.Misses),
        "Acc" => (a, b) => a.Accuracy.CompareTo(b.Accuracy),
        _ => (_, _) => 0,
    };

    private void PaintPercentBar(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _pctColIndex) return;
        if (e.Value is null || e.Graphics is null) return;

        var pct = ParsePercent(e.Value);
        var frac = Math.Clamp(pct / _maxPct, 0, 1);

        e.PaintBackground(e.CellBounds, e.State.HasFlag(DataGridViewElementStates.Selected));

        var barWidth = (int)((e.CellBounds.Width - 6) * frac);
        if (barWidth > 0)
        {
            var selected = e.State.HasFlag(DataGridViewElementStates.Selected);
            using var brush = new SolidBrush(selected
                ? Color.FromArgb(120, 255, 255, 255)          // light bar on the blue selection row
                : Color.FromArgb(120, 70, 130, 180));         // translucent steel blue otherwise
            e.Graphics.FillRectangle(brush, e.CellBounds.X + 2, e.CellBounds.Y + 3,
                barWidth, e.CellBounds.Height - 6);
        }

        e.Paint(e.CellBounds, DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.Border);
        e.Handled = true;
    }

    private static double ParsePercent(object value)
    {
        var s = value.ToString() ?? "";
        s = s.TrimEnd('%', ' ');
        return double.TryParse(s, out var d) ? d : 0;
    }
}
