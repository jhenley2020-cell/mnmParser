using System.Drawing;

namespace MnmDamageParser.App;

/// <summary>
/// The one DataGridView style used everywhere in this app: dense, read-only,
/// full-row select, fill columns. Adds click-to-sort headers (numeric
/// columns start descending, text ascending; clicking the active header
/// flips direction) and a selection-preserving row swap so re-rendering
/// every refresh tick doesn't fight the user's current selection.
///
/// Sorting itself is done by the caller (it knows the row type) -- this
/// only tracks which column/direction and raises <see cref="SortChanged"/>.
/// </summary>
internal class MeterGrid : DataGridView
{
    private readonly HashSet<string> _numericCols;

    public string? SortColumn { get; private set; }
    public bool SortDescending { get; private set; }

    /// <summary>Raised when a header click changes the sort column or
    /// direction -- the owner should re-render.</summary>
    public event Action? SortChanged;

    /// <summary>True while SetRows is swapping rows; SelectionChanged
    /// handlers should bail out when this is set.</summary>
    public bool Repopulating { get; private set; }

    /// <summary>True from a SetRows() call until the user next actually
    /// clicks or key-navigates the grid. Selection changes while this is
    /// set came from re-rendering (every refresh tick) or from WinForms
    /// auto-selecting row 0 after AddRange -- not from the user -- so
    /// SelectionChanged handlers should ignore them.</summary>
    public bool ProgrammaticSelection { get; private set; } = true;

    public MeterGrid(IEnumerable<(string Name, string Header)> columns,
                     IEnumerable<string> numericColumns,
                     string? defaultSortColumn = null,
                     bool defaultSortDescending = true)
    {
        _numericCols = new HashSet<string>(numericColumns);
        SortColumn = defaultSortColumn;
        SortDescending = defaultSortDescending;

        Dock = DockStyle.Fill;
        ReadOnly = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        RowHeadersVisible = false;
        MultiSelect = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        AllowUserToOrderColumns = false;
        BackgroundColor = SystemColors.Window;
        BorderStyle = BorderStyle.None;
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        GridColor = Color.FromArgb(230, 230, 230);
        RowTemplate.Height = 22;
        DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);

        foreach (var (name, header) in columns) Columns.Add(name, header);

        ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex < 0) return;
            var col = Columns[e.ColumnIndex].Name;
            if (SortColumn == col) SortDescending = !SortDescending;
            else { SortColumn = col; SortDescending = _numericCols.Contains(col); }
            SortChanged?.Invoke();
        };
    }

    public DataGridViewRow MakeRow(object?[] cells, object tag)
    {
        var row = new DataGridViewRow();
        row.CreateCells(this, cells);
        row.Tag = tag;
        return row;
    }

    /// <param name="keepSelectedTag">Tag value whose row should stay
    /// selected after the swap, or null for no restore.</param>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        ProgrammaticSelection = false;
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ProgrammaticSelection = false;
        base.OnKeyDown(e);
    }

    public void SetRows(IReadOnlyList<DataGridViewRow> rows, object? keepSelectedTag = null)
    {
        Repopulating = true;
        ProgrammaticSelection = true;
        try
        {
            Rows.Clear();
            if (rows.Count > 0) Rows.AddRange(rows as DataGridViewRow[] ?? rows.ToArray());
            if (keepSelectedTag is not null)
            {
                foreach (DataGridViewRow row in Rows)
                {
                    if (Equals(row.Tag, keepSelectedTag)) { row.Selected = true; break; }
                }
            }
        }
        finally
        {
            Repopulating = false;
        }
    }

    /// <summary>Sort <paramref name="items"/> in place by the active
    /// column, using the caller's per-column comparison. No-op when no
    /// sort column is set.</summary>
    public void ApplySort<T>(List<T> items, Func<string, Comparison<T>> comparisonFor)
    {
        if (SortColumn is null) return;
        items.Sort(comparisonFor(SortColumn));
        if (SortDescending) items.Reverse();
    }

    public object? SelectedTag => SelectedRows.Count > 0 ? SelectedRows[0].Tag : null;
}
