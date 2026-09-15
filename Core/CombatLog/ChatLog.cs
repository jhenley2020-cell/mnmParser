using System.Text.RegularExpressions;

namespace MnmDamageParser.Core.CombatLog;

/// <summary>One non-combat chat line, ready for display: rich-text tags
/// stripped, the line's leading colour kept separately.</summary>
/// <param name="Timestamp">Unix seconds when the scan first surfaced it
/// (not when the game printed it -- the heap scan has no timestamp).</param>
/// <param name="Text">Display text, all <c>&lt;...&gt;</c> tags removed.</param>
/// <param name="ColorHex">The <c>#RRGGBB</c> from a leading
/// <c>&lt;color=#...&gt;</c>, without the <c>#</c>; null if the line wasn't
/// colour-wrapped.</param>
/// <param name="Category">Which Chat window this line belongs in
/// (<c>config/chat.json</c>); <c>"Other"</c> when it matched no category or
/// no categories are configured.</param>
/// <param name="Seq">Monotonic insert index (== <c>ChatLog.TotalAdded</c>
/// at insert). Lets a filtered window append only what's new to it without
/// tie-breaking on identical timestamps.</param>
public sealed record ChatEntry(double Timestamp, string Text, string? ColorHex, string Category, long Seq);

/// <summary>
/// Thread-safe, bounded ring buffer of the non-combat chat lines
/// <see cref="TextLogWatcher.ChatLine"/> emits. The poll thread calls
/// <see cref="Add"/>; the UI thread reads <see cref="Snapshot"/> on a timer
/// -- same write-on-worker / poll-a-copy-on-UI pattern as MeterState.
/// </summary>
public sealed class ChatLog
{
    // The game runs on Unity/TextMeshPro, so lines can carry <color>, <b>,
    // <i>, <size=...>, <sprite=...> etc. A blanket "<...>" strip is the
    // pragmatic call for display -- a literal '<' in typed chat only gets
    // clipped if a matching '>' follows it on the same line, which is rare.
    private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex LeadingColor = new(@"^<color=#([0-9A-Fa-f]{3,8})>", RegexOptions.Compiled);

    // Every real chat/status line carries at least one of these -- channel
    // lines have ',' and '"', flavor/status lines end in '.'. Colour-wrapped
    // item names ("Copper Bar", "Fragile Sharpening Stone (2)") have none.
    private static readonly char[] SentenceMarks = { '.', '!', '?', '"', ',', ':' };

    private readonly object _lock = new();
    private readonly LinkedList<ChatEntry> _entries = new();
    private readonly int _capacity;
    private readonly ChatCategorizer? _categorizer;

    /// <summary>Total lines ever added (including ones since evicted), so a
    /// consumer can tell "N new since I last looked" apart from "the buffer
    /// wrapped" without diffing contents.</summary>
    public long TotalAdded { get; private set; }

    public ChatLog(ChatCategorizer? categorizer = null, int capacity = 2000)
    {
        _categorizer = categorizer;
        _capacity = Math.Max(capacity, 16);
    }

    public void Add(string rawLine)
    {
        if (string.IsNullOrEmpty(rawLine)) return;

        // Real chat is colour-wrapped from the very start. A colour tag
        // mid-string means UI chrome that merely embeds one (the hover
        // tooltip, "Left-click to target. Press ...").
        if (!rawLine.StartsWith("<color=#", StringComparison.Ordinal)) return;

        var colorHex = LeadingColor.Match(rawLine) is { Success: true } m ? m.Groups[1].Value : null;
        var text = AnyTag.Replace(rawLine, "").Trim();

        // Drop the interned tag fragments the heap holds ("<color=#",
        // "<b><color=#F2D675>", ...). Complete tags are already gone; an
        // INCOMPLETE one leaves a stray '<', and a formatting-only fragment
        // leaves nothing readable.
        if (text.Length < 2 || text.Contains('<') || !text.Any(char.IsLetterOrDigit)) return;

        // Drop colour-wrapped inventory/loot item names -- bare noun
        // phrases: short, and with none of the punctuation every real chat
        // or status line carries.
        if (text.Length <= 44 && text.IndexOfAny(SentenceMarks) < 0) return;

        // config/chat.json 'deny' -- damageless combat that leaked past the
        // combat filter, etc.
        if (_categorizer?.IsDenied(text) == true) return;

        var category = _categorizer?.Classify(text) ?? ChatCategorizer.Other;
        var ts = Clock.NowSeconds();
        lock (_lock)
        {
            _entries.AddLast(new ChatEntry(ts, text, colorHex, category, TotalAdded));
            TotalAdded++;
            while (_entries.Count > _capacity) _entries.RemoveFirst();
        }
    }

    public IReadOnlyList<ChatEntry> Snapshot()
    {
        lock (_lock) return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
