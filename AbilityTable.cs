using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>Per-ability / per-spell breakdown for one combatant, shown in
/// the encounter-detail window.</summary>
internal sealed class AbilityTable : MeterGrid
{
    private static readonly (string, string)[] Cols =
    {
        ("Name", "Ability"), ("School", "Type"), ("Count", "Hits"), ("Crits", "Crits"),
        ("Total", "Total"), ("Avg", "Avg"), ("Min", "Min"), ("Max", "Max"), ("Pct", "%"),
    };

    private static readonly string[] Numeric =
        { "Count", "Crits", "Total", "Avg", "Min", "Max", "Pct" };

    public AbilityTable() : base(Cols, Numeric, defaultSortColumn: "Total")
    {
        Columns["Name"]!.FillWeight = 200;
        Columns["School"]!.FillWeight = 95;
    }

    public void Render(IReadOnlyList<AbilityRow>? breakdown)
    {
        if (breakdown is null)
        {
            SetRows(Array.Empty<DataGridViewRow>());
            return;
        }

        var rows = breakdown.ToList();
        ApplySort(rows, SortComparison);

        var gridRows = rows.Select(r => MakeRow(new object?[]
        {
            r.Name, r.School ?? "", r.Count, r.Crits,
            r.Total.ToString("N0"), r.Avg.ToString("N1"), r.MinHit, r.MaxHit,
            r.Percent.ToString("N1") + "%",
        }, r.Name)).ToList();

        SetRows(gridRows);
    }

    private static Comparison<AbilityRow> SortComparison(string col) => col switch
    {
        "Name" => (a, b) => string.CompareOrdinal(a.Name, b.Name),
        "School" => (a, b) => string.CompareOrdinal(a.School ?? "", b.School ?? ""),
        "Count" => (a, b) => a.Count.CompareTo(b.Count),
        "Crits" => (a, b) => a.Crits.CompareTo(b.Crits),
        "Total" => (a, b) => a.Total.CompareTo(b.Total),
        "Avg" => (a, b) => a.Avg.CompareTo(b.Avg),
        "Min" => (a, b) => a.MinHit.CompareTo(b.MinHit),
        "Max" => (a, b) => a.MaxHit.CompareTo(b.MaxHit),
        "Pct" => (a, b) => a.Percent.CompareTo(b.Percent),
        _ => (_, _) => 0,
    };
}
