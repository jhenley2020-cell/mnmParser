using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnmDamageParser.Core.Config;

public sealed class WindowSettings
{
    [JsonPropertyName("width")] public int Width { get; set; } = 900;
    [JsonPropertyName("height")] public int Height { get; set; } = 600;
    [JsonPropertyName("refresh_ms")] public int RefreshMs { get; set; } = 500;
    [JsonPropertyName("player_name")] public string PlayerName { get; set; } = "You";
}

/// <summary>Spoken (text-to-speech) announcements. Voice is "on" when at
/// least one of the announce_* flags is set -- there is no separate master
/// switch (an earlier one was a usability trap: people turned on
/// announce_hits and heard nothing). Each flag is a toggle in the "Voice"
/// menu; the whole block is persisted so the choice sticks. Everything is
/// off by default.</summary>
public sealed class VoiceSettings
{
    [JsonPropertyName("announce_hits")] public bool AnnounceHits { get; set; } = false;
    [JsonPropertyName("announce_misses")] public bool AnnounceMisses { get; set; } = false;
    [JsonPropertyName("announce_resists")] public bool AnnounceResists { get; set; } = false;
    [JsonPropertyName("announce_records")] public bool AnnounceRecords { get; set; } = false;
    [JsonPropertyName("announce_levelups")] public bool AnnounceLevelUps { get; set; } = false;

    /// <summary>Speak the name of anything that starts attacking you --
    /// once per attacker per fight, so it flags a new add without
    /// narrating every swing.</summary>
    [JsonPropertyName("announce_attackers")] public bool AnnounceAttackers { get; set; } = false;

    /// <summary>Speak when you slay something ("a rotting skeleton down").</summary>
    [JsonPropertyName("announce_kills")] public bool AnnounceKills { get; set; } = false;

    /// <summary>Speak when you enter combat with a target ("engaging a
    /// rotting skeleton") -- the game's "Starting to attack" line.</summary>
    [JsonPropertyName("announce_engage")] public bool AnnounceEngage { get; set; } = false;

    /// <summary>Speak "combat over" on the game's "Stopped attacking"
    /// line -- suppressed right after a kill (you already heard "X down").</summary>
    [JsonPropertyName("announce_disengage")] public bool AnnounceDisengage { get; set; } = false;

    [JsonIgnore]
    public bool AnyOn => AnnounceHits || AnnounceMisses || AnnounceResists || AnnounceRecords
                         || AnnounceLevelUps || AnnounceAttackers || AnnounceKills
                         || AnnounceEngage || AnnounceDisengage;

    /// <summary>Installed SAPI voice to use -- a substring is enough
    /// ("Zira", "David"). Blank = pick the first female voice, else the
    /// system default. Only Windows' installed voices are available; add
    /// more via Settings &gt; Time &amp; language &gt; Speech.</summary>
    [JsonPropertyName("voice_name")] public string VoiceName { get; set; } = "";

    /// <summary>Pitch shift applied to every line. A SAPI level in -10..10
    /// ("+6" for a lighter / more "elven" timbre, "-4" for lower); a legacy
    /// "+12%" is still accepted (mapped to ~+3). Blank = the voice's natural
    /// pitch. Delivered as the SAPI <c>&lt;pitch absmiddle="N"/&gt;</c> markup
    /// because the built-in Zira/David voices ignore SSML prosody pitch.</summary>
    [JsonPropertyName("voice_pitch")] public string VoicePitch { get; set; } = "";

    /// <summary>SpeechSynthesizer.Rate, -10 (slow) .. 10 (fast).</summary>
    [JsonPropertyName("rate")] public int Rate { get; set; } = 2;
    /// <summary>SpeechSynthesizer.Volume, 0 .. 100.</summary>
    [JsonPropertyName("volume")] public int Volume { get; set; } = 90;
    /// <summary>Minimum gap between spoken "hit"/"miss" callouts so a busy
    /// fight doesn't turn into a stutter. Records and resists ignore this.</summary>
    [JsonPropertyName("hit_cooldown_seconds")] public double HitCooldownSeconds { get; set; } = 2.5;
}

public sealed class Settings
{
    [JsonPropertyName("process_name")] public string ProcessName { get; set; } = "mnm.exe";
    [JsonPropertyName("poll_interval_seconds")] public double PollIntervalSeconds { get; set; } = 0.5;
    [JsonPropertyName("encounter_idle_timeout_seconds")] public double EncounterIdleTimeoutSeconds { get; set; } = 50;
    [JsonPropertyName("rolling_window_seconds")] public double RollingWindowSeconds { get; set; } = 10;
    [JsonPropertyName("max_encounter_history")] public int MaxEncounterHistory { get; set; } = 50;
    [JsonPropertyName("window")] public WindowSettings Window { get; set; } = new();

    /// <summary>Your pet / charm / mercenary names. They still get their
    /// own row in the meter, but the "Just me" scope shows you AND them,
    /// and their rows are tinted like yours and tagged "(pet)". Change
    /// this when you get a new pet.</summary>
    [JsonPropertyName("pet_names")] public List<string> PetNames { get; set; } = new();

    /// <summary>Pop the standalone pet window automatically the first time a
    /// pet lands damage in a fight (once per fight -- closing it doesn't make
    /// it come back until the next encounter). No effect without pet_names.</summary>
    [JsonPropertyName("pet_window_auto_open")] public bool PetWindowAutoOpen { get; set; } = true;

    [JsonPropertyName("log_events_to_file")] public bool LogEventsToFile { get; set; } = true;
    [JsonPropertyName("event_log_path")] public string EventLogPath { get; set; } = "logs/combat_events.jsonl";

    /// <summary>Optional manual override for the character the event log
    /// and encounter history are filed under (e.g.
    /// "logs/combat_events_Radust.jsonl", "logs/history_Radust.json").
    ///
    /// Normally you leave this blank: on startup the app auto-detects the
    /// current character from the game's own on-disk data (see
    /// CharacterDetector) and segments logs/history automatically. Set it
    /// only to force a specific name -- e.g. when running this on a
    /// machine that isn't the one the game is on, or to override a
    /// misdetection. A blank value with nothing detectable falls back to
    /// unsegmented files (event_log_path as-is, "logs/history.json").
    ///
    /// When character_name is blank, the app also keeps watching the
    /// game's data while it runs and switches logs/history automatically
    /// on a mid-session relog (see character_recheck_seconds).</summary>
    [JsonPropertyName("character_name")] public string CharacterName { get; set; } = "";

    /// <summary>How often (seconds) to re-check the current character
    /// while the app is running, so a mid-session relog switches the
    /// event log and encounter history without a restart. 0 disables the
    /// re-check (startup detection only). Ignored entirely when
    /// character_name is set -- a manual override is never auto-changed.</summary>
    [JsonPropertyName("character_recheck_seconds")] public double CharacterRecheckSeconds { get; set; } = 10;

    [JsonPropertyName("voice")] public VoiceSettings Voice { get; set; } = new();

    /// <summary>Whether the XP section is shown at the bottom of the
    /// window (toolbar "XP" button toggles it).</summary>
    [JsonPropertyName("show_xp_panel")] public bool ShowXpPanel { get; set; } = false;

    public static Settings Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Settings>(json, JsonOpts.Default)
               ?? throw new InvalidDataException($"could not parse {path}");
    }

    private static readonly JsonSerializerOptions SaveOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Rewrites settings.json (used when the Voice menu toggles
    /// something). Every property is explicitly mapped, so this round-trips
    /// the whole file without losing anything -- but it does drop any hand
    /// comments, of which settings.json currently has none.</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SaveOpts));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class LinePatternConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "?";
    [JsonPropertyName("event_type")] public string EventType { get; set; } = "hit";
    [JsonPropertyName("regex")] public string Regex { get; set; } = "";
    [JsonPropertyName("default_source")] public string? DefaultSource { get; set; }
    [JsonPropertyName("default_target")] public string? DefaultTarget { get; set; }
}

public sealed class HeapScanBufferConfig
{
    [JsonPropertyName("encoding")] public string Encoding { get; set; } = "utf-16-le";
    [JsonPropertyName("anchors")] public List<string> Anchors { get; set; } = new();

    /// <summary>Substrings that mark a NON-combat chat line -- social
    /// channels, NPC dialogue, death notices, buff/debuff fades, zone/craft
    /// messages, system notices ("You are hungry..."). Scanned in the same
    /// sweep as <see cref="Anchors"/>, but a line found ONLY via one of
    /// these (no combat anchor, no combat-pattern match) is routed to
    /// <c>TextLogWatcher.ChatLine</c> for the Chat window instead of through
    /// the combat parser / the unmatched-line diagnostic.
    ///
    /// "<c>&lt;color=#</c>" catches the colour-wrapped copy the game keeps
    /// of essentially every chat line. Empty list = the Chat feature is off
    /// (no extra scanning, no ChatLine events).</summary>
    [JsonPropertyName("chat_anchors")] public List<string> ChatAnchors { get; set; } = new();
    [JsonPropertyName("string_length_offset")] public string StringLengthOffset { get; set; } = "0x10";
    [JsonPropertyName("string_chars_offset")] public string StringCharsOffset { get; set; } = "0x14";
    [JsonPropertyName("max_back_walk_bytes")] public int MaxBackWalkBytes { get; set; } = 600;
    [JsonPropertyName("full_sweep_interval_seconds")] public double FullSweepIntervalSeconds { get; set; } = 45;
    [JsonPropertyName("explore_bytes_per_poll")] public long ExploreBytesPerPoll { get; set; } = 256 * 1024 * 1024;
    [JsonPropertyName("yield_every_regions")] public int YieldEveryRegions { get; set; } = 15;
    [JsonPropertyName("yield_seconds")] public double YieldSeconds { get; set; } = 0.001;
    [JsonPropertyName("content_dedup_window_seconds")] public double ContentDedupWindowSeconds { get; set; } = 3.0;
    [JsonPropertyName("max_entries_per_poll")] public int MaxEntriesPerPoll { get; set; } = 200;
    [JsonPropertyName("debug_print_unmatched_lines")] public bool DebugPrintUnmatchedLines { get; set; } = false;
    [JsonPropertyName("debug_print_scan_stats")] public bool DebugPrintScanStats { get; set; } = false;
}

public sealed class TextLogConfig
{
    [JsonPropertyName("buffer")] public HeapScanBufferConfig Buffer { get; set; } = new();
    [JsonPropertyName("line_patterns")] public List<LinePatternConfig> LinePatterns { get; set; } = new();
}

public sealed class OffsetsConfig
{
    [JsonPropertyName("module")] public string Module { get; set; } = "";
    [JsonPropertyName("text_log")] public TextLogConfig TextLog { get; set; } = new();

    public static OffsetsConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<OffsetsConfig>(json, JsonOpts.Default)
               ?? throw new InvalidDataException($"could not parse {path}");
    }
}

internal static class JsonOpts
{
    // PropertyNameCaseInsensitive isn't needed since every property is
    // explicitly mapped via [JsonPropertyName]; extra JSON keys (the
    // offsets.json "_readme"/legacy-fields documentation, which is meant
    // for a human editing the file, not for the app) are ignored by
    // default rather than erroring.
    public static readonly JsonSerializerOptions Default = new();
}
