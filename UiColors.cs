using System.Drawing;

namespace MnmDamageParser.App;

/// <summary>Stable, faint per-combatant row tints (ACT gives every
/// combatant a colour so you can follow one down the table / across
/// encounters). The local player always gets the same blue.</summary>
internal static class UiColors
{
    private static readonly Color PlayerTint = Color.FromArgb(0xDB, 0xEA, 0xFE);

    private static readonly Color[] Palette =
    {
        Color.FromArgb(0xEF, 0xF6, 0xFF), Color.FromArgb(0xEC, 0xFD, 0xF5),
        Color.FromArgb(0xFF, 0xF7, 0xED), Color.FromArgb(0xFD, 0xF2, 0xF8),
        Color.FromArgb(0xF5, 0xF3, 0xFF), Color.FromArgb(0xF0, 0xFD, 0xFA),
        Color.FromArgb(0xFE, 0xF2, 0xF2), Color.FromArgb(0xF7, 0xFE, 0xE7),
        Color.FromArgb(0xFF, 0xFB, 0xEB), Color.FromArgb(0xF1, 0xF5, 0xF9),
    };

    public static Color ForActor(string name, string playerName)
    {
        if (name == playerName || name.Equals("You", StringComparison.OrdinalIgnoreCase))
            return PlayerTint;

        var h = 17;
        foreach (var ch in name) h = unchecked(h * 31 + ch);
        return Palette[(h & int.MaxValue) % Palette.Length];
    }
}
