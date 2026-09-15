using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using MnmDamageParser.Core;
using MnmDamageParser.Core.Config;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

/// <summary>
/// Spoken combat callouts via Windows TTS. Fed from
/// <c>MeterState.EventIngested</c> / <c>.RecordBroken</c> on the poll
/// thread; every method just drops a string into a small bounded queue and
/// returns. A single dedicated worker thread does the actual speaking, so
/// nothing about parsing or the UI can be stalled by TTS.
///
/// Speech goes through the SAPI <c>SpVoice</c> COM object (late-bound) rather
/// than <c>System.Speech</c>, because the built-in "Desktop" voices (Zira /
/// David) silently ignore SSML <c>&lt;prosody pitch&gt;</c> -- but they DO
/// honour the old SAPI <c>&lt;pitch absmiddle="N"/&gt;</c> XML markup, which
/// is how "Elf pitch" actually raises the voice. <c>System.Speech</c> is kept
/// only to enumerate installed voice names for the menu.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class VoiceAnnouncer : IDisposable
{
    // SAPI SpeechVoiceSpeakFlags
    private const int SvsfAsync = 1;
    private const int SvsfPurgeBeforeSpeak = 2;
    private const int SvsfIsXml = 8;

    private readonly dynamic? _sp;          // SAPI.SpVoice, or null if it wouldn't create
    private readonly SpeechSynthesizer? _fallback;   // used only if _sp is null

    private readonly BlockingCollection<(string Text, long Tick)> _queue = new(boundedCapacity: 3);
    private readonly Thread _worker;

    // An utterance that's waited longer than this is stale -- skip it so a
    // burst (e.g. after a slow full memory sweep) drains instantly instead
    // of the voice narrating 20 seconds behind the fight.
    private const long MaxUtteranceAgeMs = 5000;
    private string _lastEnqueuedText = "";
    private long _lastEnqueuedTick;
    private volatile string _pitchMarkup = "";   // "" or <pitch absmiddle="N"/>
    private readonly object _cooldownGate = new();
    private DateTime _lastThrottledUtc = DateTime.MinValue;
    private volatile string _status = "not initialised";

    public bool AnnounceHits { get; set; }
    public bool AnnounceMisses { get; set; }
    public bool AnnounceResists { get; set; }
    public bool AnnounceRecords { get; set; }
    public bool AnnounceLevelUps { get; set; }
    public bool AnnounceAttackers { get; set; }
    public bool AnnounceKills { get; set; }
    public bool AnnounceEngage { get; set; }
    public bool AnnounceDisengage { get; set; }
    public double HitCooldownSeconds { get; set; } = 2.5;

    // "what is attacking me" -- announce each attacker once per fight. Poll
    // thread only (OnCombatEvent), so no lock needed.
    private readonly HashSet<string> _knownAttackers = new(StringComparer.OrdinalIgnoreCase);
    private long _lastIncomingTick;
    private const long AttackerForgetMs = 20_000;

    // "You" + pet/merc names -- sources that count as friendly.
    private readonly HashSet<string> _friendlies = new(StringComparer.OrdinalIgnoreCase) { "You" };
    private long _lastEngageTick;

    private long _lastKillTick;

    private bool AnyOn => AnnounceHits || AnnounceMisses || AnnounceResists || AnnounceRecords
                          || AnnounceLevelUps || AnnounceAttackers || AnnounceKills
                          || AnnounceEngage || AnnounceDisengage;

    public string Status => _status;

    public VoiceAnnouncer(VoiceSettings s)
    {
        try
        {
            var t = Type.GetTypeFromProgID("SAPI.SpVoice");
            _sp = t is not null ? Activator.CreateInstance(t) : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[voice] SpVoice COM unavailable, falling back to System.Speech: " + ex.Message);
            _sp = null;
        }

        if (_sp is null)
        {
            try { _fallback = new SpeechSynthesizer(); _fallback.SetOutputToDefaultAudioDevice(); }
            catch (Exception ex) { _status = "init failed: " + ex.Message; Console.Error.WriteLine("[voice] " + _status); }
        }

        Console.WriteLine($"[voice] engine: {(_sp is not null ? "SAPI SpVoice (pitch supported)" : "System.Speech (no pitch)")}");
        Console.WriteLine($"[voice] installed voices: {string.Join(", ", InstalledVoiceNames())}");

        Apply(s);
        _status = CurrentVoiceName() is { } vn ? $"ready ({vn})" : "no TTS voices installed";
        Console.WriteLine($"[voice] {_status}");

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Voice" };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        foreach (var (text, tick) in _queue.GetConsumingEnumerable())
        {
            if (Environment.TickCount64 - tick > MaxUtteranceAgeMs)
            {
                Console.WriteLine($"[voice] (dropped stale) {text}");
                continue;
            }
            try { SpeakNow(text); }
            catch (Exception ex) when (!_queue.IsAddingCompleted)
            {
                if (!ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine($"[voice] speak failed: {ex.Message}");
            }
            catch { /* cancelled by Dispose() -- expected */ }
        }
    }

    /// <summary>Push the current settings into the announcer (construction
    /// and whenever the Voice menu changes something).</summary>
    public void Apply(VoiceSettings s)
    {
        AnnounceHits = s.AnnounceHits;
        AnnounceMisses = s.AnnounceMisses;
        AnnounceResists = s.AnnounceResists;
        AnnounceRecords = s.AnnounceRecords;
        AnnounceLevelUps = s.AnnounceLevelUps;
        AnnounceAttackers = s.AnnounceAttackers;
        AnnounceKills = s.AnnounceKills;
        AnnounceEngage = s.AnnounceEngage;
        AnnounceDisengage = s.AnnounceDisengage;
        HitCooldownSeconds = s.HitCooldownSeconds;
        _pitchMarkup = PitchToMarkup(s.VoicePitch);
        try
        {
            var rate = Math.Clamp(s.Rate, -10, 10);
            var vol = Math.Clamp(s.Volume, 0, 100);
            if (_sp is not null) { _sp.Rate = rate; _sp.Volume = vol; }
            else if (_fallback is not null) { _fallback.Rate = rate; _fallback.Volume = vol; }
            SelectVoice(s.VoiceName);
        }
        catch { /* a bad rate/volume/voice shouldn't break combat */ }
    }

    /// <summary>Old SSML-style "+12%" or a plain SAPI level "+6" / "5" / "-3"
    /// -> a <c>&lt;pitch absmiddle="N"/&gt;</c> prefix (N in -10..10). Empty
    /// or a net-zero value -> no markup.</summary>
    internal static string PitchToMarkup(string? cfg)
    {
        cfg = (cfg ?? "").Trim();
        if (cfg.Length == 0) return "";
        int val;
        var m = Regex.Match(cfg, @"^([+-]?\d+)\s*(%?)$");
        if (m.Success)
        {
            val = int.Parse(m.Groups[1].Value);
            if (m.Groups[2].Value == "%") val = (int)Math.Round(val / 4.0); // +12% ~= +3 levels
        }
        else
        {
            val = 6; // an unrecognised keyword (e.g. "elf") -> a clear lift
        }
        val = Math.Clamp(val, -10, 10);
        return val == 0 ? "" : $"<pitch absmiddle=\"{val}\"/>";
    }

    // The classic SAPI registry SpVoice.GetVoices() reads only holds Zira /
    // David. Windows' newer "mobile"/OneCore voices (and anything you add
    // via Settings > Time & language > Speech, e.g. the en-GB "Hazel" voice)
    // live in a separate category -- SpVoice can still *use* those tokens, it
    // just won't list them unless we enumerate that category ourselves.
    private const string OneCoreVoicesReg =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices";

    private static IEnumerable<dynamic> EnumAllVoiceTokens(dynamic sp)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic tok in sp.GetVoices())
            if (seen.Add(SafeDesc(tok))) yield return tok;

        dynamic? cat = null;
        try
        {
            var t = Type.GetTypeFromProgID("SAPI.SpObjectTokenCategory");
            if (t is not null) { cat = Activator.CreateInstance(t); cat!.SetId(OneCoreVoicesReg, false); }
        }
        catch { cat = null; }
        if (cat is null) yield break;

        dynamic toks;
        try { toks = cat.EnumerateTokens(); }
        catch { yield break; }
        foreach (dynamic tok in toks)
            if (seen.Add(SafeDesc(tok))) yield return tok;
    }

    private static string SafeDesc(dynamic tok)
    {
        try { return (string)tok.GetDescription(); } catch { return ""; }
    }

    public static IReadOnlyList<string> InstalledVoiceNames()
    {
        try
        {
            var t = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (t is not null)
            {
                dynamic sp = Activator.CreateInstance(t)!;
                var names = new List<string>();
                foreach (var tok in EnumAllVoiceTokens(sp))
                {
                    var d = SafeDesc(tok);
                    if (d.Length > 0) names.Add(d);
                }
                if (names.Count > 0) return names;
            }
        }
        catch { /* fall through to System.Speech */ }

        try
        {
            using var s = new SpeechSynthesizer();
            return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private string? CurrentVoiceName()
    {
        try
        {
            if (_sp is not null) return (string)_sp.Voice.GetDescription();
            return _fallback?.Voice?.Name;
        }
        catch { return null; }
    }

    private void SelectVoice(string? name)
    {
        if (_sp is not null)
        {
            dynamic? female = null;
            foreach (dynamic tok in EnumAllVoiceTokens(_sp))
            {
                string desc = SafeDesc(tok);
                if (!string.IsNullOrWhiteSpace(name) && desc.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    try { _sp.Voice = tok; return; }
                    catch (Exception ex) { Console.Error.WriteLine($"[voice] can't use '{desc}': {ex.Message}"); }
                }
                if (female is null)
                {
                    try { if ((string)tok.GetAttribute("Gender") == "Female") female = tok; }
                    catch { /* token has no Gender attribute */ }
                }
            }
            if (string.IsNullOrWhiteSpace(name) && female is not null)
            {
                try { _sp.Voice = female; } catch { }
            }
            return;
        }

        if (_fallback is null) return;
        var voices = _fallback.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
        if (voices.Count == 0) return;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = voices.FirstOrDefault(v => v.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) { _fallback.SelectVoice(match.Name); return; }
        }
        var f = voices.FirstOrDefault(v => v.Gender == VoiceGender.Female);
        if (f is not null) _fallback.SelectVoice(f.Name);
    }

    /// <summary>Wire to <c>MeterState.EventIngested</c>. Poll thread.</summary>
    public void OnCombatEvent(CombatEvent e)
    {
        if (e.Source != "You" && e.Target == "You"
            && e.EventType is EventType.Hit or EventType.Crit or EventType.Miss)
        {
            var now = Environment.TickCount64;
            if (now - _lastIncomingTick > AttackerForgetMs) _knownAttackers.Clear();
            _lastIncomingTick = now;
            if (AnnounceAttackers && _knownAttackers.Add(e.Source))
                Enqueue(e.Source, important: false);
        }
        else if (e.EventType == EventType.Kill)
        {
            _knownAttackers.Remove(e.Target);
            _lastKillTick = Environment.TickCount64;
            if (AnnounceKills)
                Enqueue(string.IsNullOrWhiteSpace(e.Target) || e.Target == "?" ? "target down" : $"{e.Target} down",
                    important: false);
        }
        else if (e.EventType == EventType.Disengage)
        {
            _knownAttackers.Clear();
            if (AnnounceDisengage && Environment.TickCount64 - _lastKillTick > 3000)
                Enqueue("combat over", important: false);
            return;
        }
        else if (e.EventType == EventType.Engage && AnnounceEngage && _friendlies.Contains(e.Source))
        {
            _lastEngageTick = Environment.TickCount64;
            var t = e.Target;
            var hasTarget = !string.IsNullOrWhiteSpace(t) && t != "?" && !_friendlies.Contains(t);
            Enqueue(hasTarget ? $"engaging {t}" : "engaging", important: true);
            return;
        }

        if (e.Source != "You") return;
        switch (e.EventType)
        {
            case EventType.Hit or EventType.Crit when AnnounceHits:
                if (PassesCooldown()) Enqueue(e.EventType == EventType.Crit ? "crit" : "hit", important: false);
                break;
            case EventType.Miss when AnnounceMisses:
                if (PassesCooldown()) Enqueue("miss", important: false);
                break;
            case EventType.Resist when AnnounceResists:
                Enqueue("resisted", important: true);
                break;
        }
    }

    private (int Best, long Tick) _lastAbilityRecord;

    /// <summary>Wire to <c>MeterState.RecordBroken</c>.</summary>
    public void OnRecord(RecordBreak r)
    {
        if (!AnnounceRecords) return;
        if (r.Kind == RecordKind.Ability)
        {
            _lastAbilityRecord = (r.NewBest, Environment.TickCount64);
        }
        else if (r.NewBest == _lastAbilityRecord.Best
                 && Environment.TickCount64 - _lastAbilityRecord.Tick < 750)
        {
            return;
        }

        var name = r.Name == PersonalRecords.MeleeAbilityKey ? "melee" : r.Name;
        var what = r.Kind == RecordKind.Ability ? name : $"{name} damage";
        Enqueue($"New record. {what}. {r.NewBest}.", important: true);
    }

    /// <summary>Called by the XP panel when the bar wraps.</summary>
    public void OnLevelUp()
    {
        if (AnnounceLevelUps) Enqueue("Level up!", important: true);
    }

    /// <summary>The pet / merc names that count as "you" for the engage
    /// callout. Program passes settings.PetNames.</summary>
    public void SetPetNames(IEnumerable<string> pets)
    {
        _friendlies.Clear();
        _friendlies.Add("You");
        foreach (var p in pets) if (!string.IsNullOrWhiteSpace(p)) _friendlies.Add(p.Trim());
    }

    /// <summary>Wire to <c>MeterState.EncounterOpened</c> -- fallback "entered
    /// combat" for a fight that opened with a bare hit/miss (mob-initiated,
    /// or engage wording we don't parse).</summary>
    public void OnEncounterOpened(CombatEvent e)
    {
        if (!AnnounceEngage) return;
        if (e.EventType is not (EventType.Hit or EventType.Crit or EventType.Miss)) return;
        if (Environment.TickCount64 - _lastEngageTick < 4000) return;
        if (!_friendlies.Contains(e.Source)) return;
        var target = e.Target;
        if (string.IsNullOrWhiteSpace(target) || target == "?" || _friendlies.Contains(target)) return;
        Enqueue($"engaging {target}", important: true);
    }

    /// <summary>"Test voice" menu item -- always speaks.</summary>
    public string Test()
    {
        Enqueue("Voice announcements are working.", important: true, force: true);
        return _status;
    }

    /// <summary>Speak synchronously on the caller's thread (used by
    /// --voice-test). Returns null on success or the error message.</summary>
    public string? SpeakBlocking(string text)
    {
        try { SpeakNow(text); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>The one place TTS actually happens.</summary>
    private void SpeakNow(string text)
    {
        if (_sp is not null)
        {
            var payload = _pitchMarkup + XmlEscape(text);
            _sp.Speak(payload, SvsfAsync | SvsfIsXml);
            _sp.WaitUntilDone(15000);
            return;
        }
        _fallback?.Speak(text);
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private bool PassesCooldown()
    {
        lock (_cooldownGate)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastThrottledUtc).TotalSeconds < HitCooldownSeconds) return false;
            _lastThrottledUtc = now;
            return true;
        }
    }

    private void Enqueue(string text, bool important, bool force = false)
    {
        if (!AnyOn && !force) return;

        var now = Environment.TickCount64;
        if (text == _lastEnqueuedText && now - _lastEnqueuedTick < 2000) return;

        if (important || force)
        {
            while (_queue.TryTake(out _)) { }
            try
            {
                if (_sp is not null) _sp.Speak(string.Empty, SvsfPurgeBeforeSpeak);
                else _fallback?.SpeakAsyncCancelAll();
            }
            catch { }
        }
        else if (_queue.Count > 0)
        {
            return;
        }

        if (_queue.TryAdd((text, now)))
        {
            _lastEnqueuedText = text;
            _lastEnqueuedTick = now;
            Console.WriteLine($"[voice] {text}");
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try
        {
            if (_sp is not null) _sp.Speak(string.Empty, SvsfPurgeBeforeSpeak);
            else _fallback?.SpeakAsyncCancelAll();
        }
        catch { }
        _worker.Join(TimeSpan.FromMilliseconds(500));
        _fallback?.Dispose();
        _queue.Dispose();
    }
}
