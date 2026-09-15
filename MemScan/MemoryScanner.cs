using MnmDamageParser.Core.Memory;

namespace MnmDamageParser.MemScan;

public enum NextScanMode { Changed, Unchanged, Increased, Decreased }

/// <summary>
/// The classic Cheat Engine "first scan / next scan" workflow, built on top
/// of this repo's existing IMemoryReader (ReadProcessMemory + VirtualQueryEx
/// enumeration -- the same plumbing src/Core/CombatLog/TextLogWatcher.cs
/// uses for the heap-scan text parser). A first scan reads every writable
/// region and records every aligned offset whose bytes match the given
/// value; a next scan re-reads ONLY the surviving candidate addresses
/// (fast -- no full sweep) and narrows by an exact value or by how the
/// value changed since the last scan.
///
/// Also exposes a generic exact-byte-pattern search (FindExactBytePattern)
/// used both for reverse pointer scans (find what points at address X) and
/// vtable scans (find every other live instance of the same class as a
/// known object, by matching its vtable pointer) -- see Program.cs's "ptr"
/// and "vtable" commands.
/// </summary>
public sealed class MemoryScanner
{
    private readonly IMemoryReader _mh;
    public MemoryScanner(IMemoryReader mh) => _mh = mh;

    public IReadOnlyList<long> Candidates => _candidates;
    public ScanType? CurrentType { get; private set; }

    private List<long> _candidates = new();
    private readonly Dictionary<long, byte[]> _lastBytes = new();

    public int FirstScan(ScanType type, byte[] pattern)
    {
        CurrentType = type;
        _candidates = FindExactBytePattern(pattern, ScanValue.Size(type));
        _lastBytes.Clear();
        foreach (var addr in _candidates)
            _lastBytes[addr] = pattern;
        return _candidates.Count;
    }

    public int NextScanValue(byte[] pattern)
    {
        if (CurrentType is null) throw new InvalidOperationException("run 'scan' first");
        var size = pattern.Length;
        var survivors = new List<long>();
        foreach (var addr in _candidates)
        {
            var cur = _mh.ReadBytes(addr, size);
            if (cur is null || !cur.AsSpan().SequenceEqual(pattern)) continue;
            survivors.Add(addr);
            _lastBytes[addr] = cur;
        }
        _candidates = survivors;
        return _candidates.Count;
    }

    public int NextScanCompare(NextScanMode mode)
    {
        if (CurrentType is null) throw new InvalidOperationException("run 'scan' first");
        var type = CurrentType.Value;
        var size = ScanValue.Size(type);
        var survivors = new List<long>();
        foreach (var addr in _candidates)
        {
            var cur = _mh.ReadBytes(addr, size);
            if (cur is null || cur.Length < size) continue;
            var prevBytes = _lastBytes.TryGetValue(addr, out var pb) ? pb : null;
            if (prevBytes is null) continue;

            var same = cur.AsSpan().SequenceEqual(prevBytes);
            var keep = mode switch
            {
                NextScanMode.Unchanged => same,
                NextScanMode.Changed => !same,
                NextScanMode.Increased => ScanValue.Decode(type, cur) > ScanValue.Decode(type, prevBytes),
                NextScanMode.Decreased => ScanValue.Decode(type, cur) < ScanValue.Decode(type, prevBytes),
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            if (!keep) continue;
            survivors.Add(addr);
            _lastBytes[addr] = cur;
        }
        _candidates = survivors;
        return _candidates.Count;
    }

    public byte[]? CurrentBytesAt(long addr) => CurrentType is null ? null : _mh.ReadBytes(addr, ScanValue.Size(CurrentType.Value));

    /// <summary>Filters the current candidate list to those where a SECOND
    /// field, at a fixed byte offset from the candidate address, currently
    /// holds a given value -- e.g. "keep only candidates whose +0x04 field
    /// equals the known species-id constant". Doesn't touch CurrentType/
    /// _lastBytes bookkeeping for the primary scan; this is a one-off
    /// structural filter, not a step in the changed/unchanged workflow.</summary>
    public int FilterByField(int relativeOffset, ScanType fieldType, byte[] expectedBytes)
    {
        var survivors = new List<long>();
        foreach (var addr in _candidates)
        {
            var cur = _mh.ReadBytes(addr + relativeOffset, expectedBytes.Length);
            if (cur is not null && cur.AsSpan().SequenceEqual(expectedBytes))
                survivors.Add(addr);
        }
        _candidates = survivors;
        return _candidates.Count;
    }

    /// <summary>Keep only candidates where the value at (addr+min) and
    /// (addr+max) fall within [min, max] -- e.g. "plausibly HP-sized".</summary>
    public int FilterWhereInRange(int relativeOffset, ScanType fieldType, double min, double max)
    {
        var size = ScanValue.Size(fieldType);
        var survivors = new List<long>();
        foreach (var addr in _candidates)
        {
            var cur = _mh.ReadBytes(addr + relativeOffset, size);
            if (cur is null) continue;
            var v = ScanValue.Decode(fieldType, cur);
            if (v >= min && v <= max) survivors.Add(addr);
        }
        _candidates = survivors;
        return _candidates.Count;
    }

    /// <summary>Keep only candidates where the values at two relative
    /// offsets are exactly equal -- e.g. the "HP mirrored at two offsets"
    /// pattern confirmed by hand for this game's stats record.</summary>
    public int FilterWhereFieldsEqual(int offsetA, int offsetB, ScanType fieldType)
    {
        var size = ScanValue.Size(fieldType);
        var survivors = new List<long>();
        foreach (var addr in _candidates)
        {
            var a = _mh.ReadBytes(addr + offsetA, size);
            var b = _mh.ReadBytes(addr + offsetB, size);
            if (a is not null && b is not null && a.AsSpan().SequenceEqual(b))
                survivors.Add(addr);
        }
        _candidates = survivors;
        return _candidates.Count;
    }

    /// <summary>"Unknown initial value" scan (Cheat Engine's term): instead
    /// of matching a specific number, record EVERY aligned slot in one
    /// region as a candidate with its current value, so a later 'next
    /// decreased'/'next changed' can find whatever changed without ever
    /// having known the starting value -- the only option when the game
    /// doesn't display an exact number (e.g. a health bar with no HP text).
    /// Deliberately scoped to a single region (not all of writable memory)
    /// since recording every 4-byte slot across gigabytes would be millions
    /// of entries -- impractical for a plain List/Dictionary. Program.cs
    /// refuses to call this on anything bigger than ~200MB.</summary>
    public int BaselineScanRegion(MemoryRegion region, ScanType type)
    {
        CurrentType = type;
        var size = ScanValue.Size(type);
        var data = _mh.ReadBytes(region.Base, (int)region.Size);
        _candidates = new List<long>();
        _lastBytes.Clear();
        if (data is null) return 0;

        for (var i = 0; i + size <= data.Length; i += size)
        {
            var addr = region.Base + i;
            _candidates.Add(addr);
            _lastBytes[addr] = data.AsSpan(i, size).ToArray();
        }
        return _candidates.Count;
    }

    /// <summary>Enumerates every fixed-stride slot within one region that
    /// matches a signature of (relative offset -> expected bytes) checks --
    /// e.g. this game's NPC records are packed value-type array elements
    /// (no individual Mono object header/vtable per element, unlike a
    /// boxed reference type -- confirmed by the fact that a record's own
    /// preceding bytes are just the tail of the PREVIOUS record, not a
    /// 16-byte MonoObject header), so a vtable scan doesn't apply here.
    /// Instead this walks the array directly, phase-aligned to a known-good
    /// anchor address so slot boundaries line up even if the array doesn't
    /// start exactly at the region's base.</summary>
    public List<long> EnumerateBySignature(long anchor, MemoryRegion region, int stride, IEnumerable<(int Offset, byte[] Expected)> signature)
    {
        var sig = signature.ToList();
        var phase = (anchor - region.Base) % stride;
        if (phase < 0) phase += stride;

        var hits = new List<long>();
        for (var slotBase = region.Base + phase; slotBase + stride <= region.Base + region.Size; slotBase += stride)
        {
            var ok = true;
            foreach (var (offset, expected) in sig)
            {
                var cur = _mh.ReadBytes(slotBase + offset, expected.Length);
                if (cur is null || !cur.AsSpan().SequenceEqual(expected)) { ok = false; break; }
            }
            if (ok) hits.Add(slotBase);
        }
        return hits;
    }

    /// <summary>Full sweep of every writable region for every occurrence of
    /// `pattern`, filtered to addresses aligned to `alignment` bytes.
    /// Uses the same trick as TextLogWatcher's anchor search: repeated
    /// vectorized Span.IndexOf calls to enumerate all raw occurrences (fast
    /// -- BCL SIMD implementation), then a cheap post-filter for alignment
    /// instead of manually stepping every aligned offset by hand.</summary>
    public List<long> FindExactBytePattern(byte[] pattern, int alignment)
    {
        var hits = new List<long>();
        var regions = _mh.EnumRegions(writableOnly: true).ToList();
        var stepCount = 0;
        foreach (var region in regions)
        {
            stepCount++;
            if (stepCount % 50 == 0) Thread.Sleep(1); // same UI-starvation insurance as EnumRegions

            if (region.Size <= 0 || region.Size > int.MaxValue) continue;
            var data = _mh.ReadBytes(region.Base, (int)region.Size);
            if (data is null || data.Length < pattern.Length) continue;

            var start = 0;
            while (true)
            {
                var idx = data.AsSpan(start).IndexOf(pattern);
                if (idx < 0) break;
                var absolute = region.Base + start + idx;
                if (absolute % alignment == 0) hits.Add(absolute);
                start += idx + 1;
                if (start >= data.Length) break;
            }
        }
        return hits;
    }
}
