using System.Text.Json;
using System.Text.Json.Serialization;
using MnmDamageParser.Core.Config;
using MnmDamageParser.Core.GameTime;

namespace MnmDamageParser.Core.Parser;

/// <summary>
/// Owns a rolling history of Encounter objects plus the one currently live
/// (Current, or null if nothing has happened yet / everything has gone
/// idle).
///
/// Encounter boundaries: a gap of more than idleTimeoutSeconds since the
/// last bit of activity ANYONE in the encounter produced closes it out
/// and files it into history; the next event starts a brand new
/// encounter. This is checked both when new events arrive (Ingest) and
/// independently on the real wall clock (Touch, which the GUI's refresh
/// timer calls every tick), so a fight that goes silent and never
/// produces another event still closes on schedule instead of hanging
/// open forever waiting for a new event that never comes.
///
/// Unlike the original Python version, this class is genuinely
/// thread-safe (a lock guards every read/mutation of Current/history) --
/// C# has real OS threads and no GIL, so a background scanner thread and
/// the WinForms UI thread really can corrupt a Dictionary if they touch
/// it unsynchronized at the same time (in Python this happened to be
/// "mostly safe" only because the GIL serializes individual bytecodes).
/// </summary>
public sealed class MeterState : IDisposable
{
    private readonly object _lock = new();
    private readonly double _idleTimeout;
    private readonly int _maxHistory;
    private readonly LinkedList<Encounter> _history = new();
    private string? _historyPath;
    private string? _eventLogPath;
    private string? _recordsPath;
    private PersonalRecords _records;
    private StreamWriter? _logWriter;

    // "Overall" running totals -- every closed encounter for this character
    // folded into one aggregate that outlives the history cap and restarts.
    private string? _totalsPath;
    private Encounter _lifetime = new(0);
    private double _lifetimeCombatSeconds;
    private int _lifetimeEncounters;
    private int _lifetimeKills;
    private DateTime _lifetimeSince = DateTime.UtcNow;

    // DoT / debuff expiry timers (the "Timers" window).
    private readonly DebuffTracker _debuffs;

    // In-world clock, fed by the game's /time output.
    private readonly GameClock _gameClock = new();
    private string? _gameTimePath;

    /// <summary>Raised once per combat event as it is ingested (on the
    /// poll thread, outside the lock). The voice announcer subscribes to
    /// this. Do not call back into MeterState from the handler.</summary>
    public event Action<CombatEvent>? EventIngested;

    /// <summary>Raised when one of your outgoing hits sets a new personal
    /// best (per ability/spell and/or per damage type). Poll thread,
    /// outside the lock.</summary>
    public event Action<RecordBreak>? RecordBroken;

    /// <summary>Raised with the first event of a brand-new encounter (the
    /// one that transitioned Current from null). Poll thread, outside the
    /// lock. "Entered combat."</summary>
    public event Action<CombatEvent>? EncounterOpened;

    /// <param name="historyPath">If given, closed-encounter history is
    /// loaded from this JSON file on construction (see
    /// EncounterHistoryStore) and re-saved every time a fight closes, so
    /// the encounter list survives closing and reopening the app instead
    /// of resetting to empty each run.</param>
    public MeterState(double idleTimeoutSeconds = 6.0, double rollingWindowSeconds = 10.0,
        string? eventLogPath = null, int maxHistory = 50, string? historyPath = null,
        string? characterName = null, string? recordsPath = null, string? totalsPath = null,
        SpellConfig? spells = null, string? gameTimePath = null)
    {
        _idleTimeout = idleTimeoutSeconds;
        _ = rollingWindowSeconds; // kept for API parity with the old Python signature; ActorStats hardcodes 10s today
        _maxHistory = maxHistory;
        CurrentCharacterName = characterName;
        _recordsPath = recordsPath;
        _records = PersonalRecords.Load(recordsPath);
        _debuffs = new DebuffTracker((spells ?? new SpellConfig()).Tracked);
        _gameTimePath = gameTimePath;
        _gameClock.LoadFrom(gameTimePath);
        OpenLogWriterLocked(eventLogPath);
        LoadHistoryLocked(historyPath);
        LoadLifetimeLocked(totalsPath);
    }

    /// <summary>A copy of the current personal bests, for the UI.</summary>
    public RecordsSnapshot GetRecords()
    {
        lock (_lock) return _records.Snapshot();
    }

    /// <summary>Wipe the current character's personal bests.</summary>
    public void ResetRecords()
    {
        lock (_lock) _records.Clear();
    }

    /// <summary>Running DoT / debuff expiry timers for things you've hit,
    /// soonest-to-expire first. Empty unless <c>config/spells.json</c> lists
    /// durations. Safe to call from the UI thread.</summary>
    public IReadOnlyList<DebuffTimer> GetDebuffTimers()
    {
        lock (_lock) return _debuffs.Snapshot(Clock.NowSeconds());
    }

    /// <summary>The extrapolated in-world time now (and the last calendar
    /// line), or Now == null until the first <c>/time</c> reading or a manual
    /// set. UI-thread safe.</summary>
    public (GameTimeSnapshot? Now, string? DateText) GetGameTime()
    {
        lock (_lock) return (_gameClock.Now(Clock.NowSeconds()), _gameClock.DateText);
    }

    /// <summary>Manually anchor the in-world clock (the "set game time"
    /// dialog) -- <paramref name="minuteOfDay"/> is 0..1439.</summary>
    public void SetGameTime(int minuteOfDay)
    {
        lock (_lock)
        {
            _gameClock.Observe(minuteOfDay, Clock.NowSeconds());
            _gameClock.SaveTo(_gameTimePath);
        }
    }

    /// <summary>The character whose files are currently active, for the
    /// GUI's window title. Null = unsegmented. Updated by
    /// <see cref="SwitchCharacter"/> on a mid-session relog.</summary>
    public string? CurrentCharacterName { get; private set; }

    /// <summary>Repoint the event log and encounter history at a different
    /// character's files -- called when a mid-session relog is detected
    /// (see CharacterDetector). The in-progress fight (if any) is closed
    /// out and saved under the OUTGOING character first, then the log
    /// writer is swapped and the INCOMING character's history is loaded
    /// from disk. A no-op if both paths already match. Safe to call from
    /// any thread.</summary>
    public void SwitchCharacter(string? characterName, string? eventLogPath, string? historyPath,
        string? recordsPath = null, string? totalsPath = null)
    {
        lock (_lock)
        {
            var samePaths =
                string.Equals(_historyPath, historyPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_eventLogPath, eventLogPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_recordsPath, recordsPath, StringComparison.OrdinalIgnoreCase);
            if (samePaths) { CurrentCharacterName = characterName; return; }
            CurrentCharacterName = characterName;

            // A relog means whatever was being tracked is over -- file it
            // under the character we're leaving (still _historyPath /
            // _totalsPath here, so it folds into the OUTGOING totals).
            if (Current is not null)
                CloseCurrentEncounterLocked(Current.LastActivity());

            _logWriter?.Flush();
            _logWriter?.Dispose();
            OpenLogWriterLocked(eventLogPath);

            LoadHistoryLocked(historyPath);
            _recordsPath = recordsPath;
            _records = PersonalRecords.Load(recordsPath);
            LoadLifetimeLocked(totalsPath);
            _debuffs.Clear();
            Current = null;
        }
    }

    /// <summary>Caller must hold _lock (or be the constructor).</summary>
    private void OpenLogWriterLocked(string? eventLogPath)
    {
        _eventLogPath = eventLogPath;
        _logWriter = null;
        if (string.IsNullOrEmpty(eventLogPath)) return;
        var full = Path.GetFullPath(eventLogPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _logWriter = new StreamWriter(full, append: true) { AutoFlush = false };
    }

    /// <summary>Caller must hold _lock (or be the constructor). Replaces
    /// _history with what's on disk at historyPath (newest-first, capped
    /// at _maxHistory).</summary>
    private void LoadHistoryLocked(string? historyPath)
    {
        _historyPath = string.IsNullOrEmpty(historyPath) ? null : historyPath;
        _history.Clear();
        if (_historyPath is null) return;
        foreach (var e in EncounterHistoryStore.Load(_historyPath))
        {
            if (_history.Count >= _maxHistory) break;
            _history.AddLast(e); // Load() already returns newest-first
        }
    }

    /// <summary>Caller must hold _lock (or be the constructor). Replaces the
    /// lifetime aggregate with what's on disk at totalsPath.</summary>
    private void LoadLifetimeLocked(string? totalsPath)
    {
        _totalsPath = string.IsNullOrEmpty(totalsPath) ? null : totalsPath;
        _lifetime = new Encounter(0);
        _lifetimeCombatSeconds = 0;
        _lifetimeEncounters = 0;
        _lifetimeKills = 0;
        _lifetimeSince = DateTime.UtcNow;
        if (_totalsPath is null) return;

        var saved = LifetimeTotalsStore.Load(_totalsPath);
        if (saved is null) return;
        _lifetime = saved.Aggregate;
        _lifetimeCombatSeconds = saved.CombatSeconds;
        _lifetimeEncounters = saved.Encounters;
        _lifetimeKills = saved.Kills;
        _lifetimeSince = saved.Since;
    }

    private void SaveLifetimeLocked()
    {
        if (_totalsPath is null) return;
        try
        {
            LifetimeTotalsStore.Save(_totalsPath, new LifetimeTotals(
                _lifetimeSince, _lifetimeEncounters, _lifetimeKills, _lifetimeCombatSeconds, _lifetime));
        }
        catch { /* never let a totals-save failure take down the live meter */ }
    }

    /// <summary>Fold a just-closed encounter into the lifetime aggregate.
    /// Caller holds _lock; the encounter must have combatants (the caller
    /// already dropped empty ones).</summary>
    private void FoldLifetimeLocked(Encounter closed)
    {
        _lifetimeEncounters++;
        _lifetimeCombatSeconds += Math.Max(closed.Duration(closed.EndTime), 0.0);

        foreach (var a in closed.Actors.Values) LifetimeActor(_lifetime.Actors, a.Name).AddFrom(a);
        foreach (var h in closed.Healers.Values) LifetimeActor(_lifetime.Healers, h.Name).AddFrom(h);

        // Pin every folded actor's window to [0, total combat time] so the
        // overall view's DPS is TotalDamage / summed-combat-time.
        foreach (var a in _lifetime.Actors.Values) a.SetWindow(0, _lifetimeCombatSeconds);
        foreach (var a in _lifetime.Healers.Values) a.SetWindow(0, _lifetimeCombatSeconds);
        _lifetime.EndTime = _lifetimeCombatSeconds;

        SaveLifetimeLocked();
    }

    private static ActorStats LifetimeActor(Dictionary<string, ActorStats> bucket, string name)
    {
        if (!bucket.TryGetValue(name, out var a)) { a = new ActorStats(name); bucket[name] = a; }
        return a;
    }

    public Encounter? Current { get; private set; }

    public void Dispose()
    {
        lock (_lock)
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }

    public void Ingest(IReadOnlyList<CombatEvent> events)
    {
        if (events.Count == 0) return;

        List<RecordBreak>? recordBreaks = null;
        CombatEvent? opener = null;
        lock (_lock)
        {
            var batchNow = events.Max(e => e.Timestamp);
            TouchLocked(batchNow);
            foreach (var evt in events)
            {
                if (evt.EventType == EventType.Kill)
                {
                    // A kill/death message ends the fight right now,
                    // instead of leaving it open for up to idleTimeout
                    // seconds of silence. If there's nothing currently
                    // being tracked (e.g. a stray kill line with no
                    // preceding damage), there's nothing to close.
                    _lifetimeKills++;
                    _debuffs.OnTargetDown(evt.Target);
                    if (Current is not null)
                        CloseCurrentEncounterLocked(evt.Timestamp); // folds + saves lifetime (kill count included)
                    else
                        SaveLifetimeLocked();
                    LogLocked(evt);
                    continue;
                }
                if (evt.EventType == EventType.Disengage)
                {
                    // "Stopped attacking X" -- arm a short-fuse close (see
                    // TouchLocked). Real activity after this disarms it.
                    if (Current is not null) Current.DisengagedAt = evt.Timestamp;
                    LogLocked(evt);
                    continue;
                }
                if (evt.EventType == EventType.WorldTime)
                {
                    // A /time reading -- update the in-world clock / calendar,
                    // don't touch encounters. Amount >= 0 is a clock reading
                    // (game minute-of-day); Amount == -1 is the date line.
                    if (evt.Amount >= 0)
                        _gameClock.Observe(evt.Amount, evt.Timestamp);
                    else
                        _gameClock.SetDate(evt.Ability);
                    _gameClock.SaveTo(_gameTimePath);
                    LogLocked(evt);
                    continue;
                }
                if (Current is null)
                {
                    Current = new Encounter(evt.Timestamp);
                    opener ??= evt;
                }
                Current.DisengagedAt = null; // any real event means the fight is still on
                Current.Apply(evt);
                LogLocked(evt);

                if (evt.Source == "You" && evt.Amount > 0
                    && evt.EventType is EventType.Hit or EventType.Crit)
                {
                    var broke = _records.Register(evt.Ability, evt.School, evt.Amount);
                    if (broke.Count > 0) (recordBreaks ??= new()).AddRange(broke);
                    _debuffs.OnYourDamage(evt.Ability, evt.Target, evt.Timestamp);
                }
            }
            _logWriter?.Flush();
        }

        // Fire subscriber callbacks OUTSIDE the lock -- the voice announcer
        // enqueues an utterance and returns, but keep it off the lock so a
        // slow handler can't stall the poll loop's next Ingest.
        if (opener is not null) EncounterOpened?.Invoke(opener);

        var ingested = EventIngested;
        if (ingested is not null)
            foreach (var evt in events) ingested(evt);

        if (recordBreaks is not null)
        {
            var onRecord = RecordBroken;
            if (onRecord is not null)
                foreach (var b in recordBreaks) onRecord(b);
        }
    }

    /// <summary>Close out the live encounter if it's gone idle. Call this
    /// on every real wall-clock tick (the GUI's refresh timer does), not
    /// just when new events arrive -- a fight that goes silent and never
    /// produces another event still needs to close on schedule.</summary>
    public void Touch(double? now = null)
    {
        lock (_lock)
        {
            TouchLocked(now ?? Clock.NowSeconds());
        }
    }

    /// <summary>After a "Stopped attacking" line, wait only this long for
    /// the fight to resume before closing the encounter (vs. the full
    /// idle timeout). Enough that a quick target switch -- "Stopped
    /// attacking A" then "Starting to attack B" -- doesn't split one
    /// fight in two.</summary>
    private const double DisengageGraceSeconds = 6.0;

    private void TouchLocked(double now)
    {
        if (Current is null) return;
        var lastActivity = Current.LastActivity();

        if (Current.DisengagedAt is { } d && now - d > DisengageGraceSeconds)
        {
            CloseCurrentEncounterLocked(Math.Max(d, lastActivity));
            return;
        }
        if (now - lastActivity > _idleTimeout)
            CloseCurrentEncounterLocked(lastActivity);
    }

    /// <summary>Files Current into history and clears it. Caller must hold
    /// _lock and have already checked Current is not null.</summary>
    private void CloseCurrentEncounterLocked(double endTime)
    {
        // A lone "starting to attack" that never turned into a hit (mob
        // fled, you zoned) leaves an encounter with no combatants -- drop
        // it rather than clutter the list/history.
        if (Current!.Actors.Count == 0 && Current.Healers.Count == 0)
        {
            Current = null;
            return;
        }

        Current.EndTime = Math.Max(endTime, Current.LastActivity());
        FoldLifetimeLocked(Current);
        _history.AddFirst(Current);
        while (_history.Count > _maxHistory) _history.RemoveLast();
        Current = null;
        SaveHistoryLocked();
    }

    /// <summary>Persists the current closed-encounter history to disk (if
    /// a historyPath was configured). Called every time a fight closes,
    /// so a fight that just ended is never lost even if the app closes
    /// abnormally afterward -- only a fight still IN PROGRESS when the
    /// app exits is not yet saved.</summary>
    private void SaveHistoryLocked()
    {
        if (_historyPath is null) return;
        try
        {
            EncounterHistoryStore.Save(_historyPath, _history);
        }
        catch
        {
            // never let a history-save failure take down the live meter
        }
    }

    /// <summary>Manual full clear (the "Clear History" button) -- drops
    /// the live encounter (without filing it to history) and wipes
    /// history.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            Current = null;
            _history.Clear();
            _debuffs.Clear();
            SaveHistoryLocked(); // so a cleared history stays cleared after a restart, not just in-memory
        }
    }

    /// <summary>Current (if any) first, then history, newest first -- the
    /// order an encounter-list UI wants. Returns a snapshot list safe to
    /// enumerate outside the lock.
    ///
    /// WARNING: the Encounter objects themselves are still the live,
    /// mutable ones -- Current in particular can be actively mutated by
    /// the background poll thread's Ingest() at any moment after this
    /// method returns. Fine for single-threaded test code that fully
    /// controls Ingest()/Touch() timing itself, but NOT safe to call from
    /// a GUI thread while a poll thread is running concurrently: use the
    /// DTO-returning methods below (GetEncounterSummaries/Snapshot/
    /// AbilityBreakdown) instead, which copy everything out while still
    /// holding the lock.</summary>
    public List<Encounter> AllEncounters()
    {
        lock (_lock)
        {
            var result = new List<Encounter>();
            if (Current is not null) result.Add(Current);
            result.AddRange(_history);
            return result;
        }
    }

    // -- thread-safe read API for the GUI ---------------------------------
    //
    // Everything below copies data out of the Encounter/ActorStats objects
    // while still holding _lock, so the caller never touches a mutable
    // object the poll thread could be writing to concurrently -- unlike
    // AllEncounters() above, these are safe to call from the UI thread at
    // any time.

    public List<EncounterSummary> GetEncounterSummaries()
    {
        lock (_lock)
        {
            var result = new List<EncounterSummary>();
            if (Current is not null) result.Add(Summarize(Current));
            foreach (var e in _history) result.Add(Summarize(e));
            return result;
        }
    }

    private static EncounterSummary Summarize(Encounter e) => new(
        e.Id, e.StartTime, e.EndTime, e.IsActive, e.Label(),
        e.Actors.Values.Sum(a => a.TotalDamage));

    /// <summary>Combatant rows for one encounter (by id), or null if that
    /// encounter no longer exists (e.g. aged out of history).</summary>
    public List<MeterRow>? Snapshot(int encounterId, bool healing, IReadOnlyCollection<string>? onlySources, int maxRows = 50)
    {
        lock (_lock)
        {
            var enc = FindLocked(encounterId);
            if (enc is null) return null;
            double? now = enc.IsActive ? null : enc.EndTime;
            return enc.Snapshot(healing, now, onlySources, maxRows);
        }
    }

    /// <summary>One combatant's ability/spell breakdown within one
    /// encounter, or null if the encounter or that combatant isn't
    /// found.</summary>
    public List<AbilityRow>? AbilityBreakdown(int encounterId, string actorName, bool healing)
    {
        lock (_lock)
        {
            var enc = FindLocked(encounterId);
            if (enc is null) return null;
            var bucket = healing ? enc.Healers : enc.Actors;
            return bucket.TryGetValue(actorName, out var actor) ? actor.AbilityRows(healing) : null;
        }
    }

    private Encounter? FindLocked(int id)
    {
        if (Current?.Id == id) return Current;
        foreach (var h in _history) if (h.Id == id) return h;
        return null;
    }

    // -- "Overall" running totals (the Totals window) --------------------

    /// <summary>Header figures for the Totals window.</summary>
    public LifetimeInfo GetLifetimeInfo()
    {
        lock (_lock)
        {
            var seconds = _lifetimeCombatSeconds + (Current is not null ? Math.Max(Current.Duration(), 0.0) : 0.0);
            var total = _lifetime.Actors.Values.Sum(a => (long)a.TotalDamage)
                        + (Current?.Actors.Values.Sum(a => (long)a.TotalDamage) ?? 0);
            var heal = _lifetime.Healers.Values.Sum(a => (long)a.TotalHealing)
                       + (Current?.Healers.Values.Sum(a => (long)a.TotalHealing) ?? 0);
            return new LifetimeInfo(_lifetimeSince, _lifetimeEncounters, _lifetimeKills, seconds, total, heal);
        }
    }

    /// <summary>Combatant rows for the lifetime aggregate (the live fight, if
    /// any, is included on top). Same shape as <see cref="Snapshot"/>.</summary>
    public List<MeterRow> LifetimeSnapshot(bool healing, IReadOnlyCollection<string>? onlySources, int maxRows = 100)
    {
        lock (_lock)
        {
            var view = BuildLifetimeViewLocked(out var seconds);
            return view.Snapshot(healing, seconds, onlySources, maxRows);
        }
    }

    /// <summary>One combatant's ability breakdown across the lifetime
    /// aggregate (+ the live fight), or null if that combatant has no rows.</summary>
    public List<AbilityRow>? LifetimeAbilityBreakdown(string actorName, bool healing)
    {
        lock (_lock)
        {
            var view = BuildLifetimeViewLocked(out _);
            var bucket = healing ? view.Healers : view.Actors;
            if (!bucket.TryGetValue(actorName, out var actor)) return null;
            var rows = actor.AbilityRows(healing);
            return rows.Count == 0 ? null : rows;
        }
    }

    /// <summary>Wipe the running totals for this character and start over
    /// from now (the "Reset totals" button).</summary>
    public void ResetLifetime()
    {
        lock (_lock)
        {
            _lifetime = new Encounter(0);
            _lifetimeCombatSeconds = 0;
            _lifetimeEncounters = 0;
            _lifetimeKills = 0;
            _lifetimeSince = DateTime.UtcNow;
            SaveLifetimeLocked();
        }
    }

    /// <summary>Fresh Encounter holding the persisted aggregate + the live
    /// fight, windows pinned so DPS = total / combat-seconds. Caller holds _lock.</summary>
    private Encounter BuildLifetimeViewLocked(out double seconds)
    {
        var view = new Encounter(0);
        foreach (var a in _lifetime.Actors.Values) LifetimeActor(view.Actors, a.Name).AddFrom(a);
        foreach (var a in _lifetime.Healers.Values) LifetimeActor(view.Healers, a.Name).AddFrom(a);

        seconds = _lifetimeCombatSeconds;
        if (Current is not null)
        {
            foreach (var a in Current.Actors.Values) LifetimeActor(view.Actors, a.Name).AddFrom(a);
            foreach (var a in Current.Healers.Values) LifetimeActor(view.Healers, a.Name).AddFrom(a);
            seconds += Math.Max(Current.Duration(), 0.0);
        }

        foreach (var a in view.Actors.Values) a.SetWindow(0, seconds);
        foreach (var a in view.Healers.Values) a.SetWindow(0, seconds);
        view.EndTime = seconds;
        return view;
    }

    private static readonly JsonSerializerOptions LogJsonOpts = new() { WriteIndented = false };

    private void LogLocked(CombatEvent evt)
    {
        if (_logWriter is null) return;
        try
        {
            var dto = new LoggedEvent(
                evt.Timestamp, evt.Source, evt.Target, evt.Amount,
                evt.EventType switch
                {
                    EventType.Hit => "hit",
                    EventType.Crit => "crit",
                    EventType.Miss => "miss",
                    EventType.Heal => "heal",
                    EventType.Kill => "kill",
                    EventType.Resist => "resist",
                    EventType.Engage => "engage",
                    EventType.Disengage => "disengage",
                    EventType.WorldTime => "world_time",
                    _ => "?",
                },
                evt.School, evt.RawLine, evt.Ability);
            _logWriter.WriteLine(JsonSerializer.Serialize(dto, LogJsonOpts));
        }
        catch
        {
            // never let logging failures take down the live meter
        }
    }

    // Mirrors the Python version's evt.__dict__ JSON shape (same field
    // names, event_type as the lowercase string) so logs/combat_events.jsonl
    // stays in the same format across the rewrite.
    private sealed record LoggedEvent(
        [property: JsonPropertyName("timestamp")] double Timestamp,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("amount")] int Amount,
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("school")] string? School,
        [property: JsonPropertyName("raw_line")] string? RawLine,
        [property: JsonPropertyName("ability")] string? Ability);
}
