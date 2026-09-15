using System.Text;
using System.Text.RegularExpressions;
using MnmDamageParser.Core.Config;
using MnmDamageParser.Core.Memory;

namespace MnmDamageParser.Core.CombatLog;

/// <summary>
/// Direct port of combat_log.py's TextLogWatcher, heap_scan mode only
/// (the only mode this game actually needs -- see the Python version's
/// module docstring for why the other buffer modes exist/aren't used).
///
/// This game doesn't keep combat-log lines in one findable container --
/// individual message objects are referenced from scattered, shifting
/// locations. So instead of resolving a pointer chain once, this scans
/// memory for known combat-log keyword substrings (Anchors) every poll,
/// decodes whatever .NET string object each hit belongs to, and returns
/// text we haven't already surfaced (tracked by the string object's own
/// address, which stays valid for the life of the object since this
/// game's GC does not appear to relocate live objects).
///
/// A full sweep over this game's ~10+ GB of writable memory measured
/// around 30+ seconds -- far too slow to do every poll. So most polls are
/// a FAST sweep: only regions that have produced a hit before (the
/// "productive" set) plus any regions that are brand new since the last
/// poll. A FULL sweep still happens every FullSweepIntervalSeconds to
/// catch hits landing in a region that's never been productive.
///
/// Each FAST sweep also spends a small extra byte budget
/// (ExploreBytesPerPoll) round-robining through never-productive regions,
/// so a brand new actor's first hit doesn't have to wait for the next
/// full sweep.
///
/// The very first call is a BASELINE pass: this game keeps chat/combat
/// text sitting in memory from before we attached, so the first full
/// sweep finds every line still resident, all at once. Those get marked
/// seen but are NOT returned as events -- otherwise they'd all get
/// timestamped "now" and dumped into the meter as a nonsense instant-DPS
/// spike the moment the app opens.
///
/// One more layer on top of address-based dedup: this game stores some
/// messages as MORE THAN ONE String object (confirmed: the same line
/// shows up as two byte-identical objects at different addresses, one
/// wrapped in a &lt;color=...&gt; tag and one plain). _recentTextSeen
/// catches this by comparing color-tag-stripped text of new hits against
/// anything emitted within ContentDedupWindowSeconds.
/// </summary>
public sealed class TextLogWatcher
{
    private static readonly Regex ColorTagRe = new(@"</?color(?:=[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record CompiledPattern(string Name, EventType EventType, Regex Regex, string? DefaultSource, string? DefaultTarget);

    private readonly IMemoryReader _mh;
    private readonly HeapScanBufferConfig _cfg;
    private readonly List<CompiledPattern> _patterns;

    // The resolved combat anchor list (mirrors PollHeapScan's fallback).
    // Used both for scanning AND to decide, from a recovered line's text
    // alone, whether it's a combat line -- so routing doesn't depend on
    // which anchor's search happened to hit it first before dedup.
    private readonly string[] _combatAnchorTexts;

    // Chat anchors (config buffer.chat_anchors). A line found only via one
    // of these -- carrying no combat anchor and matching no combat pattern
    // -- is a non-combat chat line: it goes to ChatLine, never to the
    // combat parser or the unmatched-line diagnostic.
    private readonly string[] _chatAnchorTexts;

    // -- heap_scan mode state --
    private HashSet<long> _knownRegionBases = new();
    private readonly HashSet<long> _productiveRegionBases = new();
    private readonly HashSet<long> _seenObjectAddrs = new();
    private double? _lastFullSweepTime;
    private bool _baselineDone;
    private int _exploreCursor;
    private readonly Dictionary<string, double> _recentTextSeen = new();

    public bool DebugPrintUnmatchedLines { get; set; }
    public bool DebugPrintScanStats { get; set; }

    /// <summary>Test seam: swap this out to observe/skip the real sleep
    /// during unit tests instead of actually pausing the test run.</summary>
    public static Action<double> SleepHook { get; set; } = seconds => Thread.Sleep(TimeSpan.FromSeconds(seconds));

    public TextLogWatcher(IMemoryReader mh, HeapScanBufferConfig bufferConfig, List<LinePatternConfig> linePatterns)
    {
        _mh = mh;
        _cfg = bufferConfig;
        DebugPrintUnmatchedLines = bufferConfig.DebugPrintUnmatchedLines;
        DebugPrintScanStats = bufferConfig.DebugPrintScanStats;

        _combatAnchorTexts = (bufferConfig.Anchors.Count > 0
            ? bufferConfig.Anchors
            : new List<string> { "points of", "misses!" }).ToArray();
        _chatAnchorTexts = bufferConfig.ChatAnchors.ToArray();

        _patterns = linePatterns.Select(p => new CompiledPattern(
            p.Name,
            p.EventType switch
            {
                "hit" => EventType.Hit,
                "crit" => EventType.Crit,
                "miss" => EventType.Miss,
                "heal" => EventType.Heal,
                "kill" => EventType.Kill,
                "resist" => EventType.Resist,
                "engage" => EventType.Engage,
                "disengage" => EventType.Disengage,
                "world_time" => EventType.WorldTime,
                _ => throw new InvalidDataException($"unknown event_type in line_patterns: '{p.EventType}'"),
            },
            new Regex(p.Regex, RegexOptions.Compiled),
            p.DefaultSource,
            p.DefaultTarget
        )).ToList();
    }

    private static readonly char[] LineBreaks = { '\n', '\r' };

    public List<CombatEvent> Poll()
    {
        var newLines = PollHeapScan();
        var now = Clock.NowSeconds();
        var events = new List<CombatEvent>();
        foreach (var rec in newLines)
        {
            // A recovered string can hold more than one chat line (the
            // /time command prints three at once). Match each line on its
            // own so an $-anchored pattern isn't defeated by the rest.
            foreach (var line in rec.Text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var evt = MatchLine(line, now);
                if (evt is not null) { events.Add(evt); continue; }
                if (line.Length == 0) continue;

                if (rec.HasCombatAnchor)
                {
                    // An anchor-carrying combat line the parser can't shape
                    // yet (crit wording, DoT ticks, dodges/parries, ...).
                    // Same diagnostic as before -- just lifted out of
                    // MatchLine so pure chat lines never touch it.
                    if (DebugPrintUnmatchedLines)
                        Console.Error.WriteLine($"[combat_log] unmatched line: {line}");
                    UnmatchedLine?.Invoke(line);
                }
                else
                {
                    // No combat anchor, matched no combat pattern -> a
                    // non-combat chat line (social channel, NPC dialogue,
                    // death notice, buff fade, system notice, ...).
                    ChatLine?.Invoke(line);
                }
            }
        }
        return events;
    }

    private CombatEvent? MatchLine(string rawLine, double now)
    {
        var line = rawLine.Trim();
        foreach (var spec in _patterns)
        {
            var m = spec.Regex.Match(line);
            if (!m.Success) continue;

            string? Group(string name) => m.Groups[name].Success ? m.Groups[name].Value : null;

            var amountStr = Group("amount");
            var amount = amountStr is not null ? int.Parse(amountStr) : 0;

            // The game's /time output, in two lines:
            //   "Game Time: 3 AM"  (minutes optional)  -> the clock
            //   "The date is <...>. It is <Season>"    -> the calendar
            // A WorldTime event carries the game minute-of-day in Amount, or
            // Amount = -1 with the date text in Ability for the date line. A
            // time that doesn't parse falls through so the unmatched log
            // still sees it.
            if (spec.EventType == EventType.WorldTime)
            {
                if (Group("hour") is { } hs && Group("ampm") is { } ap
                    && int.TryParse(hs, out var h))
                {
                    var mm = int.TryParse(Group("minute"), out var mv) ? mv : 0;
                    if (GameTime.GameClock.ParseClock12(h, mm, ap) is not { } minuteOfDay) continue;
                    return new CombatEvent
                    {
                        Timestamp = now, Source = "World", Target = "?", Amount = minuteOfDay,
                        EventType = EventType.WorldTime, RawLine = line,
                    };
                }
                if (Group("date") is { } dateText && dateText.Trim().Length > 0)
                {
                    return new CombatEvent
                    {
                        Timestamp = now, Source = "World", Target = "?", Amount = -1,
                        EventType = EventType.WorldTime, RawLine = line, Ability = dateText.Trim(),
                    };
                }
                continue;
            }

            var source = NormalizeName((Group("source") ?? spec.DefaultSource ?? "?").Trim());
            var target = NormalizeName((Group("target") ?? spec.DefaultTarget ?? "?").Trim());
            // ability2 exists only on the heal-guess pattern's second branch
            // ("X's <ability2> heals Y"); ability/ability2 are mutually
            // exclusive per match, so this never overwrites real data.
            var ability = Group("ability") ?? Group("ability2");

            return new CombatEvent
            {
                Timestamp = now,
                Source = source,
                Target = target,
                Amount = amount,
                EventType = spec.EventType,
                School = Group("school"),
                RawLine = line,
                Ability = ability?.Trim(),
            };
        }
        // No pattern matched. Poll() decides what to do with it (unmatched
        // combat diagnostic vs. the Chat feed), since only Poll() knows
        // whether the line carried a combat anchor.
        return null;
    }

    /// <summary>The game writes the local player as "YOU" in some lines
    /// ("a rat bites YOU"), "you" in others ("heals you for N"), and "Your"
    /// as a possessive -- normalise all of those to a single "You" so the
    /// player is one combatant, not three.</summary>
    private static string NormalizeName(string name) =>
        name is "YOU" or "you" ? "You" : name;

    /// <summary>Raised for every non-empty line that carried a scan anchor
    /// but matched no line pattern -- i.e. combat text the parser is
    /// currently blind to (crit wording, DoT ticks, dodges/parries, your
    /// own misses, ...). Program wires this to logs/unmatched_&lt;char&gt;.log
    /// when debug_print_unmatched_lines is set, so real gaps can be
    /// captured from a live session instead of guessed at.</summary>
    public event Action<string>? UnmatchedLine;

    /// <summary>Raised for every recovered line that is NOT combat -- it
    /// carried a <c>chat_anchors</c> substring, no combat anchor, and
    /// matched no combat pattern. This is the raw feed for the Chat window
    /// (social channels, NPC dialogue, death notices, buff/debuff fades,
    /// zone/craft messages, "You are hungry..." and the like). Empty
    /// <c>chat_anchors</c> means this never fires.</summary>
    public event Action<string>? ChatLine;

    /// <summary>One recovered Mono string plus whether its text carries a
    /// combat anchor -- computed from the text itself, not from which
    /// anchor's byte-search hit it, so it's stable regardless of scan order
    /// or the address/content dedup dropping a later duplicate hit.</summary>
    private readonly record struct Recovered(string Text, bool HasCombatAnchor);

    private List<Recovered> PollHeapScan()
    {
        var encoding = _cfg.Encoding; // "utf-16-le" -- the only encoding this game has ever needed
        var anchorStrs = _cfg.Anchors.Count > 0 ? _cfg.Anchors : new List<string> { "points of", "misses!" };
        // Combat anchors first, then chat anchors -- one sweep covers both.
        var anchors = anchorStrs.Concat(_chatAnchorTexts)
            .Select(a => (Text: a, Bytes: EncodeAnchor(a, encoding))).ToList();
        var lengthOffset = ParseHex(_cfg.StringLengthOffset);
        var charsOffset = ParseHex(_cfg.StringCharsOffset);
        var maxBack = _cfg.MaxBackWalkBytes;
        var fullSweepInterval = _cfg.FullSweepIntervalSeconds;
        var exploreBudget = _cfg.ExploreBytesPerPoll;
        var contentDedupWindow = _cfg.ContentDedupWindowSeconds;
        var yieldEveryRegions = _cfg.YieldEveryRegions;
        var yieldSeconds = _cfg.YieldSeconds;
        var isBaselinePass = !_baselineDone;
        var maxNewPerPoll = isBaselinePass ? int.MaxValue : _cfg.MaxEntriesPerPoll;

        var t0 = Clock.NowSeconds();
        var doFullSweep = _lastFullSweepTime is null || (t0 - _lastFullSweepTime.Value) >= fullSweepInterval;

        var allRegions = _mh.EnumRegions(writableOnly: true).ToList();
        var currentBases = allRegions.Select(r => r.Base).ToHashSet();

        var regionsToScan = new List<(long Base, long Size)>();
        var exploredCount = 0;

        if (doFullSweep)
        {
            regionsToScan.AddRange(allRegions.Select(r => (r.Base, r.Size)));
            _lastFullSweepTime = t0;
        }
        else
        {
            var newBases = currentBases.Except(_knownRegionBases).ToHashSet();
            var coreBases = new HashSet<long>(_productiveRegionBases);
            coreBases.UnionWith(newBases);
            regionsToScan.AddRange(allRegions.Where(r => coreBases.Contains(r.Base)).Select(r => (r.Base, r.Size)));

            var explorable = allRegions.Where(r => !coreBases.Contains(r.Base)).OrderBy(r => r.Base).ToList();
            if (explorable.Count > 0 && exploreBudget > 0)
            {
                var n = explorable.Count;
                var cursor = ((_exploreCursor % n) + n) % n;
                long budgetLeft = exploreBudget;
                var taken = 0;
                while (taken < n && budgetLeft > 0)
                {
                    var r = explorable[(cursor + taken) % n];
                    regionsToScan.Add((r.Base, r.Size));
                    budgetLeft -= r.Size;
                    taken++;
                }
                exploredCount = taken;
                _exploreCursor = (cursor + taken) % n;
            }
        }
        _knownRegionBases = currentBases;

        var newLines = new List<Recovered>();
        long scannedBytes = 0;

        // Yield the OS scheduler every yieldEveryRegions regions -- see
        // Win32MemoryReader.EnumRegions's matching comment. Without this,
        // a background thread doing nothing but tight ReadProcessMemory +
        // byte-search calls can still starve the WinForms UI thread of
        // scheduling time for the whole multi-second sweep.
        for (var scanIdx = 0; scanIdx < regionsToScan.Count; scanIdx++)
        {
            if (yieldEveryRegions > 0 && scanIdx != 0 && scanIdx % yieldEveryRegions == 0)
                SleepHook(yieldSeconds);

            var (baseAddr, size) = regionsToScan[scanIdx];
            var data = _mh.ReadBytes(baseAddr, (int)size);
            if (data is null || data.Length == 0) continue;
            scannedBytes += data.Length;

            var regionHit = false;
            foreach (var (mustContain, pattern) in anchors)
            {
                var start = 0;
                while (true)
                {
                    var idx = IndexOf(data, pattern, start);
                    if (idx == -1) break;
                    regionHit = true;

                    var (objAddr, text) = RecoverStringObject(baseAddr + idx, mustContain, maxBack, lengthOffset, charsOffset);
                    if (objAddr is not null && !_seenObjectAddrs.Contains(objAddr.Value))
                    {
                        _seenObjectAddrs.Add(objAddr.Value);
                        var normalized = StripColorTags(text!);
                        var isDuplicateCopy = _recentTextSeen.TryGetValue(normalized, out var lastEmit)
                                               && (t0 - lastEmit) < contentDedupWindow;
                        if (!isDuplicateCopy)
                        {
                            _recentTextSeen[normalized] = t0;
                            newLines.Add(new Recovered(text!, HasCombatAnchor(text!)));
                            if (newLines.Count >= maxNewPerPoll) { regionHit = true; break; }
                        }
                    }
                    start = idx + 1;
                }
                if (newLines.Count >= maxNewPerPoll) break;
            }
            if (regionHit) _productiveRegionBases.Add(baseAddr);
            if (newLines.Count >= maxNewPerPoll) break;
        }

        // Bound _recentTextSeen's growth over a long session.
        var staleBefore = t0 - Math.Max(contentDedupWindow * 5, 30.0);
        foreach (var key in _recentTextSeen.Where(kv => kv.Value < staleBefore).Select(kv => kv.Key).ToList())
            _recentTextSeen.Remove(key);

        var emittedLines = newLines;
        if (isBaselinePass)
        {
            _baselineDone = true;
            emittedLines = new List<Recovered>();
        }

        if (DebugPrintScanStats)
        {
            var kind = isBaselinePass ? "BASELINE (full, not emitted) " : (doFullSweep ? "FULL " : "fast ");
            var elapsed = Clock.NowSeconds() - t0;
            var exploreNote = !doFullSweep ? $", {exploredCount} exploratory" : "";
            Console.Error.WriteLine(
                $"[combat_log] {kind}sweep: {regionsToScan.Count} region(s){exploreNote}, " +
                $"{scannedBytes / (1024.0 * 1024.0):F1} MB, {newLines.Count} line(s) found " +
                $"({emittedLines.Count} emitted), {elapsed:F2}s " +
                $"(productive regions tracked: {_productiveRegionBases.Count})");
        }

        return emittedLines;
    }

    /// <summary>hitAddr is somewhere INSIDE a string's char data (a raw
    /// keyword match). Walk backward looking for the real object base:
    /// the address where reading a .NET string produces clean text
    /// containing mustContain. Char-aligned steps (2 bytes) since UTF-16
    /// chars are 2 bytes wide.</summary>
    private (long? ObjAddr, string? Text) RecoverStringObject(long hitAddr, string mustContain, int maxBack, int lengthOffset, int charsOffset)
    {
        for (var k = 0; k < maxBack; k += 2)
        {
            var objAddr = hitAddr - k;
            var lengthBytes = _mh.ReadBytes(objAddr + lengthOffset, 4);
            if (lengthBytes is null || lengthBytes.Length < 4) continue;
            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > 1000) continue;

            var text = ReadDotNetString(objAddr, lengthOffset, charsOffset, 1000);
            if (text is not null && text.Contains(mustContain))
            {
                if (text.All(ch => ch is '\n' or '\r' or '\t' || !char.IsControl(ch)))
                    return (objAddr, text);
            }
        }
        return (null, null);
    }

    private string? ReadDotNetString(long objectAddress, int lengthOffset, int charsOffset, int maxChars)
    {
        if (objectAddress == 0) return null;
        var lengthBytes = _mh.ReadBytes(objectAddress + lengthOffset, 4);
        if (lengthBytes is null || lengthBytes.Length < 4) return null;
        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length < 0 || length > maxChars) return null;
        if (length == 0) return "";
        var raw = _mh.ReadBytes(objectAddress + charsOffset, length * 2);
        if (raw is null) return null;
        try { return Encoding.Unicode.GetString(raw); }
        catch { return null; }
    }

    private static string StripColorTags(string text) => ColorTagRe.Replace(text, "").Trim();

    /// <summary>True if the recovered line's text contains any combat
    /// anchor -- decides whether an unmatched line is a combat line the
    /// parser is blind to (-&gt; UnmatchedLine diagnostic) or a non-combat
    /// chat line (-&gt; ChatLine feed).</summary>
    private bool HasCombatAnchor(string text)
    {
        foreach (var anchor in _combatAnchorTexts)
            if (text.Contains(anchor, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static byte[] EncodeAnchor(string anchor, string encoding) => encoding.ToLowerInvariant() switch
    {
        "utf-16-le" or "utf-16le" or "utf16-le" => Encoding.Unicode.GetBytes(anchor),
        "utf-8" or "utf8" => Encoding.UTF8.GetBytes(anchor),
        _ => Encoding.Unicode.GetBytes(anchor),
    };

    private static int ParseHex(string s) =>
        Convert.ToInt32(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, 16);

    /// <summary>Byte-array substring search (equivalent of Python's
    /// bytes.find(pattern, start)), starting at a given offset.
    ///
    /// This used to be a hand-rolled nested loop (byte-by-byte comparison)
    /// -- functionally correct, but it turned out to be the dominant cost
    /// of a sweep: measured at ~75-80s per full sweep on Jay's machine vs.
    /// ~26-28s for the equivalent Python version using CPython's
    /// hand-optimized bytes.find. ReadOnlySpan&lt;byte&gt;.IndexOf is a
    /// real BCL method with a SIMD-vectorized implementation (Vector128/
    /// Vector256 where the CPU supports it) -- switching to it is a
    /// one-line fix for what was actually a hand-rolled-search-algorithm
    /// problem, not a fundamental C#-vs-Python speed problem.</summary>
    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        if (start < 0) start = 0;
        if (start > haystack.Length) return -1;
        if (needle.Length == 0) return start;
        var idx = haystack.AsSpan(start).IndexOf(needle);
        return idx < 0 ? -1 : idx + start;
    }
}
