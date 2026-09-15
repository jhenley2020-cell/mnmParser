using System.Text.Json;
using System.Text.Json.Serialization;
using MnmDamageParser.Core.Config;

namespace MnmDamageParser.Core.Xp;

/// <summary>A screen-pixel rectangle (virtual-screen coordinates, so
/// <c>Left</c> can be negative on a multi-monitor setup).</summary>
public sealed class ScreenRect
{
    [JsonPropertyName("left")] public int Left { get; set; }
    [JsonPropertyName("top")] public int Top { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

/// <summary>
/// Where the M&amp;M experience bar is on screen and how to read it. Ported
/// from the standalone Python "XP Bar Reader" -- same JSON shape
/// (<c>xpbar_config.json</c>), so an existing calibration drops straight
/// in. The reader has no game-memory or game-file access: it measures how
/// far the coloured fill reaches across <see cref="Region"/>, so "XP" is
/// always "percent of the current level" (100% = one level).
/// </summary>
public sealed class XpBarConfig
{
    /// <summary>Screen rectangle of the bar's FILL area (inside the border).</summary>
    [JsonPropertyName("region")] public ScreenRect Region { get; set; } = new();

    /// <summary>Reference RGB of a filled column (set by calibration).</summary>
    [JsonPropertyName("fill_rgb")] public int[] FillRgb { get; set; } = { 70, 130, 220 };

    /// <summary>Reference RGB of an empty column.</summary>
    [JsonPropertyName("empty_rgb")] public int[] EmptyRgb { get; set; } = { 25, 25, 30 };

    /// <summary>Pixels ignored at each end (border / rounded caps / anti-aliasing).</summary>
    [JsonPropertyName("edge_trim_px")] public int EdgeTrimPx { get; set; } = 2;

    /// <summary>How often to sample the screen.</summary>
    [JsonPropertyName("interval_sec")] public double IntervalSeconds { get; set; } = 0.6;

    /// <summary>Each "sample" is the median of this many quick back-to-back
    /// captures -- kills transient noise (a tooltip, floating combat text or
    /// a particle drawn over the bar for one frame).</summary>
    [JsonPropertyName("samples_per_read")] public int SamplesPerRead { get; set; } = 3;

    /// <summary>Trailing window for the "recent rate" figure.</summary>
    [JsonPropertyName("rate_window_sec")] public double RateWindowSeconds { get; set; } = 120;

    /// <summary>How long the last-gain line stays highlighted before it dims.</summary>
    [JsonPropertyName("delta_hold_sec")] public double DeltaHoldSeconds { get; set; } = 12;

    /// <summary>A fill-fraction drop bigger than this between two readings
    /// is a level-up, not XP loss.</summary>
    [JsonPropertyName("levelup_drop")] public double LevelUpDrop { get; set; } = 0.12;

    /// <summary>Fill-fraction changes smaller than this count as zero --
    /// but they accumulate against a fixed reference, so a slow trickle
    /// still registers once the sum crosses the floor (see XpTracker).
    /// 0.0008 ≈ 0.08% of a level, roughly the sub-pixel read noise.</summary>
    [JsonPropertyName("noise_floor")] public double NoiseFloor { get; set; } = 0.0008;

    [JsonIgnore]
    public bool IsCalibrated => Region is { Width: >= 4, Height: >= 2 };

    public static XpBarConfig Load(string path)
    {
        if (!File.Exists(path)) return new XpBarConfig();
        try
        {
            return JsonSerializer.Deserialize<XpBarConfig>(File.ReadAllText(path), JsonOpts.Default)
                   ?? new XpBarConfig();
        }
        catch
        {
            return new XpBarConfig();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
