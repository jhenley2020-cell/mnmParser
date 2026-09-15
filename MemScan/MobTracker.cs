using System.Text.RegularExpressions;
using MnmDamageParser.Core.Memory;

namespace MnmDamageParser.MemScan;

/// <summary>One live mob record: id, world position, heading, and (when
/// known) species name. See README's "Confirmed against real game data"
/// for how the offsets were found.</summary>
public sealed record MobEntity(int Id, float X, float Y, float Z, float Heading, long Ptr80 = 0, string? Name = null);

/// <summary>
/// Bootstraps into the mob entity-record array and re-walks it live --
/// the logic shared by the console `radar` command and the graphical
/// `map` window, so it isn't duplicated between them.
///
/// Records are packed value-type array elements (no per-element Mono
/// object header -- vtable scanning doesn't apply). Bootstrap: a fresh
/// "npc &lt;name&gt; &lt;id&gt;" debug string (zone-wide, not personal --
/// confirmed needs no local combat) gives a rare id to scan for quickly;
/// each raw hit is then tried as a phase anchor for a bounded local-window
/// check of the confirmed structural signature (1.4 at +0x1C, 180 at +0x20).
///
/// Species names come from the "npc &lt;name&gt; &lt;id&gt;" debug strings,
/// matched to records by EXACT id. Confirmed 2026-09-13: that id IS the
/// same counter as the record's own +0x00 id. (An earlier note in this
/// repo claimed they were different counters; that was wrong. Measured in
/// one session: record ids spanned 121203..122760 and the string ids fell
/// in the same band, matching exactly where both existed.) The sweep runs
/// continuously in the background and harvests EVERY npc string, not just
/// one species, so coverage grows on its own as combat happens anywhere in
/// the zone -- it does not have to be your combat.
///
/// What does NOT work, tested and rejected: inferring a species from any
/// field in the record. `speciesfield` checks every 4- and 8-byte offset
/// against debug-string ground truth (equal within a species, different
/// between species) and nothing qualifies -- +0x80 is constant ACROSS
/// species (a shared manager/zone pointer, not the species key an early
/// small sample suggested), +0x04 is the tick counter, +0x08 a 1/0 flag.
/// Dereferencing +0x80 for name text was also tried two hops deep: hop 2
/// landed on runs of 0xFFFFFFFF, a Dictionary empty-bucket sentinel, not a
/// Mono string. Propagating names by +0x80 labelled a whole zone
/// "Decayed skeleton" -- confidently wrong, which is worse than blank. So
/// a mob with no debug string stays unnamed rather than guessed at.
/// </summary>
public sealed class MobTracker
{
    private const int RecordStride = 0x90;

    /// <summary>Exact id -> name harvested from "npc &lt;name&gt; &lt;id&gt;"
    /// debug strings: authoritative, and the ONLY naming signal that
    /// survived testing. Static, so names harvested during one `radar` run
    /// are still known during a later `map`. Guarded by
    /// <see cref="NamesLock"/> because the sweep that fills it runs on a
    /// background thread.</summary>
    private static readonly Dictionary<int, string> KnownIdNames = new();
    /// <summary>Ids from npc strings WITHOUT a "material N" suffix.
    ///
    /// The distinction matters and cost a real bug. A material line
    /// ("npc a Velgarrith guard 1143 material 2") carries a TEMPLATE id, not a
    /// live instance id -- template ids are small (1143, 1269) and therefore
    /// occur as ordinary integers all over an 11GB process. Parsing them was
    /// right for NAMES (they cover NPCs that never fight) but feeding them to
    /// entity discovery matched junk everywhere: slot count blew up from 4.7k
    /// to 152k and the map filled with phantom entities. Discovery uses only
    /// these instance ids; naming still uses everything.</summary>
    private static readonly HashSet<int> InstanceIds = new();
    /// <summary>Instance ids seen in the MOST RECENT sweep only.
    ///
    /// Discovery must use this, not the cumulative set. Ids accumulate for the
    /// whole session, so after a few minutes the set includes every mob that
    /// has ever spawned and died -- and their stale samples still sit in
    /// memory and still match. That is what made the entity count climb
    /// 57 -> 194 while real mobs numbered ~50, filling the map with phantoms.
    /// A dead mob's debug string eventually disappears, so "seen in the latest
    /// sweep" is a self-cleaning liveness filter.</summary>
    private static HashSet<int> _freshInstanceIds = new();
    /// <summary>Ids whose debug string used the "client " prefix: real player
    /// characters, so the map can draw them differently from mobs.</summary>
    private static readonly HashSet<int> PlayerIds = new();
    private static readonly object NamesLock = new();

    // Exact captured bit patterns, not re-derived from the 1.4f/180f
    // literals (whose compiled bits aren't guaranteed to match what the
    // game actually stored).
    private const int Sig1Value = 1068711936; // f32 1.4  at +0x1C
    private const int Sig2Value = 1127481344; // f32 180  at +0x20
    private static readonly (int Offset, byte[] Expected) Signature1 = (0x1C, BitConverter.GetBytes(Sig1Value));
    private static readonly (int Offset, byte[] Expected) Signature2 = (0x20, BitConverter.GetBytes(Sig2Value));
    private static readonly (int Offset, byte[] Expected)[] Signature = { Signature1, Signature2 };

    // "npc a cave sporeling 122750" -> name + id. Anchored at the end, so
    // the asset variants ("npc a Velgarrith guard 1143 material 2") MUST be
    // excluded before matching -- otherwise they parse as
    // name="a Velgarrith guard 1143 material", id=2, and a bootstrap that
    // then scans for an id of 2 drowns in junk hits and times out. That was
    // a real intermittent-bootstrap-failure bug, found 2026-09-13.
    /// <summary>Matches BOTH entity debug-string forms:
    ///   "npc a cave sporeling 122750"   -- a mob or NPC
    ///   "client Veer 171803"            -- a PLAYER character
    /// Players were missing from the map for a long time purely because only
    /// the "npc " prefix was ever searched for. They are in the same entity
    /// system, with ids in the same range (mobs 169xxx-172xxx, players
    /// 168xxx-172xxx), so once harvested they resolve through exactly the
    /// same position-history machinery.</summary>
    private static readonly Regex NpcNameIdRegex = new(@"^(?<kind>npc|client) (?<name>.+) (?<id>\d+)$", RegexOptions.Compiled);
    private static readonly Regex MaterialSuffixRegex = new(@"\s+material\s+\d+$", RegexOptions.Compiled);

    private readonly StringFinder _finder;
    private readonly MemoryScanner _scanner;
    private readonly IMemoryReader _mh;

    private Task? _sweepTask;
    private DateTime _lastSweepStarted = DateTime.MinValue;

    // Bounds of the address band records were last seen in, so a refresh
    // doesn't have to re-read the entire 16MB region every time.
    private const int ChunkBytes = 1 << 20;   // 1MB per ReadProcessMemory call
    private const long HotMargin = 256 * 1024; // slack so the band can drift
    private const int FullScanEvery = 40;      // ~every 4s at a 10Hz refresh
    private long _hotStart;
    private long _hotEnd;
    private int _walksSinceFullScan;

    public long Anchor { get; private set; }
    public MemoryRegion Region { get; private set; }
    public bool IsBootstrapped { get; private set; }

    /// <summary>How many mob names are currently known from debug strings.</summary>
    public static int KnownNameCount { get { lock (NamesLock) return KnownIdNames.Count; } }

    public MobTracker(StringFinder finder, MemoryScanner scanner, IMemoryReader mh)
    {
        _finder = finder;
        _scanner = scanner;
        _mh = mh;
    }

    /// <summary>One bootstrap attempt. Call again (e.g. on a timer) if it
    /// returns false and you want to keep waiting for the debug string.</summary>
    public bool TryBootstrap(string mobName)
    {
        var id = FindFreshNpcId(mobName);
        if (id is null) return false;

        _scanner.FirstScan(ScanType.I32, ScanValue.Encode(ScanType.I32, id.Value.ToString()));
        foreach (var candidate in _scanner.Candidates.Take(300))
        {
            var hit = TryLocalWindow(candidate);
            if (hit is null) continue;
            var region = FindRegion(hit.Value);
            if (region is null) continue;
            Anchor = hit.Value;
            Region = region.Value;
            IsBootstrapped = true;
            SweepNamesInBackground(force: true);
            return true;
        }
        return false;
    }

    /// <summary>Bootstraps without being told a species name: harvests every
    /// "npc &lt;name&gt; &lt;id&gt;" string and tries each id as an anchor,
    /// newest (highest) first, since live spawn ids run far above the low
    /// template/asset ids. Removes the "you must name a mob that's nearby
    /// AND has recent combat" guessing game.</summary>
    public bool TryBootstrapAuto(int maxIdsToTry = 60)
    {
        SweepNames();
        List<int> ids;
        lock (NamesLock) ids = KnownIdNames.Keys.ToList();

        foreach (var id in ids.OrderByDescending(i => i).Take(maxIdsToTry))
            if (TryAnchorOnId(id))
                return true;
        return false;
    }

    private bool TryAnchorOnId(int id)
    {
        _scanner.FirstScan(ScanType.I32, ScanValue.Encode(ScanType.I32, id.ToString()));
        foreach (var candidate in _scanner.Candidates.Take(300))
        {
            var hit = TryLocalWindow(candidate);
            if (hit is null) continue;
            var region = FindRegion(hit.Value);
            if (region is null) continue;
            Anchor = hit.Value;
            Region = region.Value;
            IsBootstrapped = true;
            return true;
        }
        return false;
    }

    /// <summary>Blocking version of the debug-string sweep, for callers that
    /// need the ids right now rather than eventually.</summary>
    public int SweepNames()
    {
        try
        {
            // Both prefixes: "npc " for mobs/NPCs, "client " for players.
            var found = _finder.Find("npc ");
            found.AddRange(_finder.Find("client "));
            lock (NamesLock)
            {
                // Names accumulate (harmless, and a despawned mob's name is
                // still worth knowing). Instance ids for DISCOVERY are rebuilt
                // from scratch each sweep -- see _freshInstanceIds.
                var fresh = new HashSet<int>();
                foreach (var r in found)
                    if (TryParseNpcString(r.Text, out var id, out var name, out var isInst))
                    {
                        KnownIdNames[id] = name;
                        if (isInst) { InstanceIds.Add(id); fresh.Add(id); }
                    }
                _freshInstanceIds = fresh;
                _lastSweepStarted = DateTime.UtcNow;
                return KnownIdNames.Count;
            }
        }
        catch { return 0; }
    }

    /// <summary>Finds a live "npc &lt;name&gt; &lt;id&gt;" string for this
    /// species and returns its id, skipping the "... material N" asset
    /// entries that would otherwise parse to a garbage id.</summary>
    private int? FindFreshNpcId(string mobName)
    {
        foreach (var r in _finder.Find(mobName))
            if (TryParseNpcString(r.Text, out var id, out _, out _))
                return id;
        return null;
    }

    private static bool TryParseNpcString(string text, out int id, out string name, out bool isInstance)
    {
        id = 0;
        name = string.Empty;
        isInstance = false;
        // "npc a Velgarrith guard 1143 material 2" carries a perfectly good
        // name+id -- the "material N" is a trailing asset qualifier, not part
        // of the id. Discarding these lines outright (an earlier version did)
        // threw away most of the available names: they cover NPCs that have
        // never been in combat, which is exactly the gap the combat-gated
        // strings leave. Strip the suffix, then parse normally.
        //
        // BUT the id it yields is a TEMPLATE id, not a live instance id. Only
        // suffix-free lines name an actual spawn -- see InstanceIds.
        var trimmed = text.TrimEnd();
        var stripped = MaterialSuffixRegex.Replace(trimmed, string.Empty);
        isInstance = ReferenceEquals(stripped, trimmed) || stripped.Length == trimmed.Length;

        var m = NpcNameIdRegex.Match(stripped);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["id"].Value, out id)) return false;
        var isPlayer = m.Groups["kind"].Value == "client";
        // A player name is a proper noun -- never strip an article from it.
        name = isPlayer ? m.Groups["name"].Value.Trim() : DisplayName(m.Groups["name"].Value);
        if (isPlayer && int.TryParse(m.Groups["id"].Value, out var pid)) lock (NamesLock) PlayerIds.Add(pid);
        return name.Length > 0;
    }

    /// <summary>Re-harvests every "npc &lt;name&gt; &lt;id&gt;" string in the
    /// heap on a background thread (a full sweep takes ~3s, far too slow to
    /// block a 500ms map repaint). Safe to call every tick -- it no-ops
    /// while a sweep is running or if the last one was recent.</summary>
    public void SweepNamesInBackground(bool force = false, int minIntervalSec = 30)
    {
        if (_sweepTask is { IsCompleted: false }) return;
        if (!force && (DateTime.UtcNow - _lastSweepStarted).TotalSeconds < minIntervalSec) return;
        _lastSweepStarted = DateTime.UtcNow;
        _sweepTask = Task.Run(() =>
        {
            try
            {
                // Both prefixes: "npc " for mobs/NPCs, "client " for players.
            var found = _finder.Find("npc ");
            found.AddRange(_finder.Find("client "));
                lock (NamesLock)
                {
                    foreach (var r in found)
                        if (TryParseNpcString(r.Text, out var id, out var name, out var isInst2))
                            { KnownIdNames[id] = name; if (isInst2) InstanceIds.Add(id); }
                }
            }
            catch
            {
                // A sweep racing a zone change can read torn memory; the next
                // sweep picks it up. Never take the map window down for this.
            }
        });
    }

    /// <summary>Re-walks the array at the current anchor/region and returns
    /// every live entity, deduped to the freshest (highest per-tick
    /// sample) per id -- each entity occupies 2-3 consecutive slots as a
    /// short interpolation history, not separate mobs. Sets
    /// <see cref="IsBootstrapped"/> false if the walk comes back empty (the
    /// array moved) -- call <see cref="TryBootstrap"/> again in that case.</summary>
    public List<MobEntity> Rewalk()
    {
        if (!IsBootstrapped) return new List<MobEntity>();

        var freshest = new Dictionary<int, (int Tick, MobEntity Entity)>();
        var phase = (Anchor - Region.Base) % RecordStride;
        if (phase < 0) phase += RecordStride;

        var regionEnd = Region.Base + Region.Size;
        var scanStart = Region.Base + phase;
        var scanEnd = regionEnd;

        // After the first full walk we know the narrow band the records
        // actually occupy, which is a small slice of a 16MB region. Scanning
        // just that band (plus margin) is what makes a 10Hz refresh viable;
        // every FullScanEvery walks we sweep the whole region again so the
        // band can grow when mobs spawn outside it.
        if (_hotStart > 0 && _walksSinceFullScan < FullScanEvery)
        {
            scanStart = Math.Max(scanStart, _hotStart - HotMargin);
            scanEnd = Math.Min(scanEnd, _hotEnd + HotMargin);
            // realign to the record phase
            var delta = (scanStart - Region.Base - phase) % RecordStride;
            if (delta < 0) delta += RecordStride;
            scanStart -= delta;
            _walksSinceFullScan++;
        }
        else _walksSinceFullScan = 0;

        long hitMin = 0, hitMax = 0;
        for (var addr = scanStart; addr + RecordStride <= scanEnd;)
        {
            var recordsLeft = (scanEnd - addr) / RecordStride;
            var recordsThisPass = (int)Math.Min(recordsLeft, ChunkBytes / RecordStride);
            if (recordsThisPass <= 0) break;
            var want = recordsThisPass * RecordStride;

            // One syscall per ~1MB instead of two per 144-byte slot: the old
            // per-slot ReadProcessMemory loop was ~230k syscalls per walk,
            // which is why the map could only refresh every 3 seconds.
            var buf = _mh.ReadBytes(addr, want);
            if (buf is null) { addr += want; continue; } // unreadable page, skip

            for (var i = 0; i + RecordStride <= buf.Length; i += RecordStride)
            {
                if (BitConverter.ToInt32(buf, i + 0x1C) != Sig1Value) continue;
                if (BitConverter.ToInt32(buf, i + 0x20) != Sig2Value) continue;

                var id = BitConverter.ToInt32(buf, i);
                if (id == 0) continue; // empty slot

                var slotAddr = addr + i;
                if (hitMin == 0 || slotAddr < hitMin) hitMin = slotAddr;
                if (slotAddr > hitMax) hitMax = slotAddr;

                var tick = BitConverter.ToInt32(buf, i + 4);
                if (freshest.TryGetValue(id, out var existing) && existing.Tick >= tick) continue;
                freshest[id] = (tick, new MobEntity(id,
                    BitConverter.ToSingle(buf, i + 0x0C),
                    BitConverter.ToSingle(buf, i + 0x10),
                    BitConverter.ToSingle(buf, i + 0x14),
                    BitConverter.ToSingle(buf, i + 0x18),
                    BitConverter.ToInt64(buf, i + 0x80)));
            }
            addr += want;
        }

        if (hitMin > 0) { _hotStart = hitMin; _hotEnd = hitMax; }
        if (freshest.Count == 0)
        {
            // Lost them: drop the hot band so the next attempt scans wide.
            _hotStart = 0;
            IsBootstrapped = false;
        }
        return ResolveNames(freshest.Values.Select(v => v.Entity)).OrderBy(e => e.Id).ToList();
    }

    /// <summary>Address of the player's position field once
    /// <see cref="PlayerFinder"/> has located it, else 0. Found by scanning
    /// for a heavily-replicated coordinate that moves continuously while
    /// sitting nowhere near any mob -- see PlayerFinder.</summary>
    public long PlayerAddress { get; set; }

    /// <summary>The player's live position, or null if not located yet or the
    /// address has gone stale (zoned, or the object moved).</summary>
    public (float X, float Y, float Z)? ReadPlayer()
    {
        if (PlayerAddress == 0) return null;
        var buf = _mh.ReadBytes(PlayerAddress, 12);
        if (buf is null) { PlayerAddress = 0; return null; }
        var x = BitConverter.ToSingle(buf, 0);
        var y = BitConverter.ToSingle(buf, 4);
        var z = BitConverter.ToSingle(buf, 8);
        // Positive test, so NaN/infinity drop out (the bug that wrecked the
        // first two hunts -- see PlayerFinder's remarks).
        if (!(MathF.Abs(x) < 1e6f && MathF.Abs(y) < 1e6f && MathF.Abs(z) < 1e6f)) { PlayerAddress = 0; return null; }
        return (x, y, z);
    }

    private List<long> _liveSlots = new();
    public int LiveSlotCount => _liveSlots.Count;
    public bool HasLiveSlots => _liveSlots.Count > 0;

    /// <summary>Slots and the zone they were validated against, swapped
    /// together as ONE immutable reference.
    ///
    /// They must travel together. An earlier version kept the zone in its own
    /// `PlayerFinder.Zone` field -- a 20-byte struct written by the background
    /// re-discovery thread while the UI thread read it. That write is not
    /// atomic, so the reader could see a half-updated zone, reject every
    /// position as out-of-bounds, and return nothing. The map's
    /// "if (mobs.Count > 0)" guard then kept painting the last good frame
    /// forever: mobs moved for a minute, then froze at the first re-discovery.
    /// A single reference assignment is atomic and cannot tear.</summary>
    private sealed record EntitySet(List<(long Addr, int Id)> Slots, PlayerFinder.Zone Zone);
    private EntitySet? _entities;

    /// <summary>Last good reading per entity, so a one-frame miss does not
    /// blink a dot out. See the hysteresis note in ReadEntities.</summary>
    private readonly Dictionary<int, (MobEntity Entity, DateTime Seen, int Tick)> _lastSeen = new();
    /// <summary>How long an entity may go without its tick advancing before it
    /// is treated as dead. Generous, because an idle NPC may update slowly.</summary>
    /// <summary>How long a missing entity keeps its last position, covering a
    /// read that lands mid-write. Short: real despawns should vanish promptly.</summary>
    private static readonly TimeSpan LingerFor = TimeSpan.FromSeconds(3);

    public int LastRawCount { get; private set; }
    public int LastReadOk { get; private set; }
    public int LastReadFail { get; private set; }
    public int EntitySlotCount => _entities?.Slots.Count ?? 0;
    public bool HasEntities => _entities is { Slots.Count: > 0 };

    /// <summary>
    /// Finds every mob/NPC in the zone by scanning for the EXACT entity ids
    /// harvested from the "npc &lt;name&gt; &lt;id&gt;" debug strings.
    ///
    /// This is what finally worked, after structural searches ("a
    /// record-shaped slot", "three in-range floats", "a quaternion") all
    /// drowned in a 3D game's endless coordinate-shaped bytes -- the
    /// transform shape alone matched ~6500 objects where there were ~9 mobs.
    /// A specific 6-digit id at offset 0, with an in-zone position at +0x0C,
    /// essentially cannot be coincidence, and every hit arrives already
    /// carrying its name.
    ///
    /// The crucial discovery: an id has MANY hits, and they are not stale
    /// duplicates -- sorting one entity's hits by the +0x04 tick traces a
    /// smooth continuous walk (ticks step by 3; one myconid was observed
    /// descending a slope). It is a POSITION HISTORY RING. Each slot is
    /// written once and then frozen, which is precisely why per-slot "did
    /// this value change?" liveness tests kept reporting "static" and sent
    /// the earlier investigation down a blind alley. The entity's current
    /// position is simply its highest-tick sample.
    /// </summary>
    public int DiscoverEntities(PlayerFinder.Zone zone, Action<long, long>? onProgress = null)
    {
        SweepNames();
        HashSet<int> ids;
        lock (NamesLock) ids = new HashSet<int>(_freshInstanceIds); // this sweep only -- see _freshInstanceIds
        if (ids.Count == 0) return 0;

        var found = new List<(long Addr, int Id)>();
        var regions = _mh.EnumRegions(writableOnly: true).ToList();
        var total = regions.Sum(r => r.Size);
        long done = 0;
        const int chunk = 4 << 20;

        foreach (var region in regions)
        {
            for (var addr = region.Base; addr < region.Base + region.Size;)
            {
                var remaining = region.Base + region.Size - addr;
                var want = (int)Math.Min(chunk, remaining);
                var buf = _mh.ReadBytes(addr, want);
                if (buf is null) LastReadFail++;
                if (buf is not null)
                {
                    LastReadOk++;
                    for (var i = 0; i + 0x1C <= buf.Length; i += 4)
                    {
                        var id = BitConverter.ToInt32(buf, i);
                        if (!ids.Contains(id)) continue;
                        if (!zone.Contains(BitConverter.ToSingle(buf, i + 0x0C),
                                           BitConverter.ToSingle(buf, i + 0x10),
                                           BitConverter.ToSingle(buf, i + 0x14))) continue;
                        found.Add((addr + i, id));
                        if (found.Count >= 200_000) break;
                    }
                }
                done += want;
                onProgress?.Invoke(done, total);
                if (remaining <= chunk || found.Count >= 200_000) break;
                addr += chunk - 0x1C;
            }
            if (found.Count >= 200_000) break;
        }

        // Single atomic swap -- see EntitySet's remarks.
        _entities = new EntitySet(found, zone);
        return found.Select(f => f.Id).Distinct().Count();
    }

    /// <summary>
    /// Finds EVERY live entity -- mobs, NPCs and player characters -- without
    /// needing to know who is in the zone first.
    ///
    /// The id-based route this replaces could only find entities that had an
    /// "npc &lt;name&gt; &lt;id&gt;" debug string, so silent NPCs were missing
    /// and player characters could never appear at all (a PC has no npc string
    /// -- verified by searching for the logged-in character's own name).
    ///
    /// Instead, filter on the +0x04 TICK. Every live sample carries the
    /// current tick (observed ~4.19M, stepping by 3 per sample), whereas
    /// coincidental matches carry garbage -- the earlier diagnostic saw junk
    /// ticks in the billions. So: one pass collecting anything with a positive
    /// id and an in-zone position, then keep only records whose tick sits in
    /// the live window. Self-contained: the reference tick is derived from the
    /// data itself (high percentile of what was collected), not supplied.
    /// </summary>
    public int DiscoverAllEntities(PlayerFinder.Zone zone, int referenceTick, Action<long, long>? onProgress = null)
    {
        // The tick window MUST be applied inline. Collecting everything with a
        // positive id and an in-zone position and filtering afterwards hit a
        // 2,000,000-candidate cap after 192 chunks (0.6s), so the scan never
        // saw most of memory and the filter never ran. The reference tick comes
        // from the id-based discovery, which already resolves named mobs.
        const int window = 20_000;      // a few seconds at 3 ticks per sample
        const int MinTrailSamples = 3;   // distinct ticks needed to count as an entity
        const int FreshnessTicks = 3_000;  // ~20s: how stale an entity's newest sample may be
        const int MaxTrackedSlots = 400_000;
        var raw = new List<(long Addr, int Id, int Tick)>();
        LastReadOk = 0; LastReadFail = 0;
        var regions = _mh.EnumRegions(writableOnly: true).ToList();
        var total = regions.Sum(r => r.Size);
        long done = 0;
        const int chunk = 4 << 20;

        foreach (var region in regions)
        {
            for (var addr = region.Base; addr < region.Base + region.Size;)
            {
                var remaining = region.Base + region.Size - addr;
                var want = (int)Math.Min(chunk, remaining);
                var buf = _mh.ReadBytes(addr, want);
                if (buf is null) LastReadFail++;
                if (buf is not null)
                {
                    LastReadOk++;
                    for (var i = 0; i + 0x1C <= buf.Length; i += 4)
                    {
                        var id = BitConverter.ToInt32(buf, i);
                        if (id <= 0 || id > 10_000_000) continue;
                        var tick = BitConverter.ToInt32(buf, i + 4);
                        if (Math.Abs((long)tick - referenceTick) > window) continue;
                        if (!zone.Contains(BitConverter.ToSingle(buf, i + 0x0C),
                                           BitConverter.ToSingle(buf, i + 0x10),
                                           BitConverter.ToSingle(buf, i + 0x14))) continue;
                        raw.Add((addr + i, id, tick));
                        if (raw.Count >= 2_000_000) break;
                    }
                }
                done += want;
                onProgress?.Invoke(done, total);
                if (remaining <= chunk || raw.Count >= 2_000_000) break;
                addr += chunk - 0x1C;
            }
            if (raw.Count >= 2_000_000) break;
        }
        LastRawCount = raw.Count;
        if (raw.Count == 0) return 0;

        // Two filters, both per-ENTITY rather than per-sample:
        //
        // 1. A real entity owns a position-history TRAIL: many samples at
        //    DISTINCT ticks. Junk does not -- without this the scan returned
        //    270 "entities" with sequential ids 920..962 all at exactly
        //    (72.0, 0.0), an ordinary array whose first field is an index.
        //
        // 2. Its NEWEST sample must be recent. The sample-level window spans
        //    ~150s of history, so accepting an entity because any one of its
        //    samples falls inside it keeps mobs alive long after they die --
        //    the count crept to 229 and rising against ~60 real entities.
        //    Judging each entity by its freshest sample drops the dead ones
        //    while keeping idle-but-updating NPCs.

        var live = raw.GroupBy(r => r.Id)
                      .Where(g => g.Select(r => r.Tick).Distinct().Count() >= MinTrailSamples)

                      .SelectMany(g => g.Select(r => (r.Addr, r.Id)))
                      .ToList();
        if (live.Count == 0) return 0;

        // MERGE with what we already have rather than replacing it. Discovery
        // is nondeterministic -- consecutive scans of the same zone returned 60
        // then 224 entities -- so letting each scan define membership makes the
        // population jump at every sweep. Discovery only FINDS addresses;
        // ReadEntities decides what is alive, from the ticks, every frame.
        var merged = new Dictionary<long, int>();
        foreach (var (a, i2) in live) merged[a] = i2;
        var prior = _entities;
        if (prior is not null)
            foreach (var (a, i2) in prior.Slots)
                if (merged.Count < MaxTrackedSlots) merged.TryAdd(a, i2);
        _entities = new EntitySet(merged.Select(kv => (kv.Key, kv.Value)).ToList(), zone);
        return live.Select(l => l.Id).Distinct().Count();
    }

    /// <summary>Two-stage discovery, the one the map uses: the id route
    /// resolves named mobs and supplies a reference tick, then the tick route
    /// finds everything else -- including NPCs that have never fought and so
    /// have no debug string at all.</summary>
    public int DiscoverEverything(PlayerFinder.Zone zone)
    {
        // Take the reference tick from the set we ALREADY have. The obvious
        // version -- run the id-based discovery, then the tick scan -- publishes
        // the id-based result as an intermediate state and then spends ~10s in
        // the tick scan before publishing the real one. Readers see a collapsed
        // world for that entire window: measured as entity count dropping 64 -> 3,
        // holding for 19s, then snapping back to 66 at the next sweep. That was
        // the "mobs appear and disappear randomly" report. Only ever publish the
        // finished set.
        var ticks = EntityTicks();
        if (ticks.Count == 0)
        {
            DiscoverEntities(zone); // cold start only: nothing to take a tick from
            ticks = EntityTicks();
            if (ticks.Count == 0) return 0;
        }
        return DiscoverAllEntities(zone, ticks.Values.Max());
    }

    /// <summary>Newest tick seen per entity id -- diagnostic, for judging how
    /// far behind a stale entity falls.</summary>
    public Dictionary<int, int> EntityTicks()
    {
        var snap = _entities;
        var ticks = new Dictionary<int, int>();
        if (snap is null) return ticks;
        foreach (var (addr, id) in snap.Slots)
        {
            var b = _mh.ReadBytes(addr, 8);
            if (b is null || BitConverter.ToInt32(b, 0) != id) continue;
            var t = BitConverter.ToInt32(b, 4);
            if (!ticks.TryGetValue(id, out var cur) || t > cur) ticks[id] = t;
        }
        return ticks;
    }

    /// <summary>Current position of every discovered entity: re-reads the
    /// history slots and keeps each id's highest-tick sample. Only touches
    /// known addresses, so it is fast enough to drive a live map.</summary>
    public List<MobEntity> ReadEntities()
    {
        // Read the reference once: re-discovery may swap in a new set mid-loop.
        var snap = _entities;
        if (snap is null) return new List<MobEntity>();

        var wide = snap.Zone.Widened(1.5f);
        var best = new Dictionary<int, (int Tick, MobEntity Entity)>();
        var names = AllKnownNames();
        foreach (var (addr, id) in snap.Slots)
        {
            var b = _mh.ReadBytes(addr, 0x1C);
            if (b is null) continue;
            if (BitConverter.ToInt32(b, 0) != id) continue;   // ring slot recycled to another entity
            var tick = BitConverter.ToInt32(b, 4);
            if (best.TryGetValue(id, out var cur) && cur.Tick >= tick) continue;
            var x = BitConverter.ToSingle(b, 0x0C);
            var y = BitConverter.ToSingle(b, 0x10);
            var z = BitConverter.ToSingle(b, 0x14);
            // A ring slot can be caught mid-write or already half-recycled,
            // which showed up as an entity apparently jumping 425 units to
            // z=0. Validating discards those -- but against a WIDENED zone,
            // because the discovery zone is sized to the mobs that existed at
            // scan time, and anything wandering past that edge would blink out
            // and back as it crossed. That edge was a real source of flicker.
            if (!wide.Contains(x, y, z)) continue;
            best[id] = (tick, new MobEntity(id, x, y, z, BitConverter.ToSingle(b, 0x18), 0,
                names.GetValueOrDefault(id)));
        }

        // Liveness is decided HERE, per entity, every frame -- not by whichever
        // discovery scan happened to find what (consecutive scans of the same
        // zone returned 60 then 224, so scan membership is not a stable basis).
        //
        // The test is whether THIS entity's own tick still advances. Comparing
        // ticks BETWEEN entities does not work: measured samples show two live
        // mobs differing by ~2800 ticks at the same instant, so the tick is not
        // a global clock, and a cross-entity freshness cut wiped the map down
        // to 1-3 dots. A dead entity's tick simply stops.
        //
        // This doubles as hysteresis: an entity missing from a single read (slot
        // caught mid-write) keeps its last position until its tick has been
        // stalled for a while, instead of blinking out and back.
        // NOT expired on tick-stall. An idle NPC stops writing samples, so its
        // tick stops -- expiring on that made every idle entity time out
        // together and instantly re-register, a one-frame mass dropout every
        // ~12s (59 -> 9 -> 57). Tick-stall means idle, not dead.
        //
        // The real death signal is already applied above: a despawned entity's
        // slot stops reading back with its own id, so it simply drops out of
        // `best`. The linger below only covers a read landing mid-write.
        var now = DateTime.UtcNow;
        foreach (var (id, v) in best) _lastSeen[id] = (v.Entity, now, v.Tick);
        foreach (var id in _lastSeen.Keys.ToList())
            if ((now - _lastSeen[id].Seen) > LingerFor) _lastSeen.Remove(id);

        return _lastSeen.Values.Select(v => v.Entity)
                        .OrderBy(e => e.Name ?? "").ThenBy(e => e.Id).ToList();
    }

    private List<long> _transformSlots = new();
    public int TransformCount => _transformSlots.Count;
    public bool HasTransforms => _transformSlots.Count > 0;

    /// <summary>
    /// Finds live entity positions via their TRANSFORM shape: a normalised
    /// rotation quaternion immediately followed by the position. Confirmed by
    /// dumping a live entity -- the four floats before its position summed to
    /// exactly 1.0.
    ///
    /// This replaces walking the 0x90 record array, which was measured to be a
    /// frozen snapshot (tick never advances, nothing ever moves). It also
    /// beats the movement-diff approach because it finds entities that are
    /// standing still.
    ///
    /// The raw scan over-matches (animation bones, pooled objects, props), so
    /// candidates are sampled twice and anything that leaves the zone or
    /// teleports is dropped -- pooled objects get recycled to the origin,
    /// which is the dominant false positive.
    /// </summary>
    public int SweepTransforms(PlayerFinder.Zone zone)
    {
        var pf = new PlayerFinder(_mh);
        var raw = pf.ScanTransforms(zone);
        if (raw.Count == 0) { _transformSlots = new List<long>(); return 0; }

        var before = new Dictionary<long, (float X, float Z)>();
        foreach (var t in raw) before[t.Addr] = (t.X, t.Z);
        Thread.Sleep(1200);

        var keep = new List<(long Addr, float X, float Z)>();
        foreach (var (addr, was) in before)
        {
            var b = _mh.ReadBytes(addr, 12);
            if (b is null) continue;
            var x = BitConverter.ToSingle(b, 0);
            var y = BitConverter.ToSingle(b, 4);
            var z = BitConverter.ToSingle(b, 8);
            if (!zone.Contains(x, y, z)) continue;                    // recycled to origin, or gone
            var d = MathF.Sqrt((x - was.X) * (x - was.X) + (z - was.Z) * (z - was.Z));
            if (d > 60f) continue;                                    // teleported: a pooled object, not a mob
            keep.Add((addr, x, z));
        }

        // One address per distinct world position: an entity's transform is
        // mirrored in several places, and duplicate dots help nobody.
        _transformSlots = keep
            .GroupBy(k => ((int)MathF.Round(k.X / 2f), (int)MathF.Round(k.Z / 2f)))
            .Select(g => g.First().Addr)
            .ToList();
        return _transformSlots.Count;
    }

    /// <summary>Reads the transform slots. Cheap enough for a 20Hz map. These
    /// carry no entity id, so ids are synthesised from the slot address --
    /// stable for the session, but names can't be attached to them.</summary>
    public List<MobEntity> ReadTransforms()
    {
        var list = new List<MobEntity>(_transformSlots.Count);
        foreach (var slot in _transformSlots)
        {
            var b = _mh.ReadBytes(slot, 12);
            if (b is null) continue;
            var x = BitConverter.ToSingle(b, 0);
            var y = BitConverter.ToSingle(b, 4);
            var z = BitConverter.ToSingle(b, 8);
            if (!(MathF.Abs(x) < 1e6f && MathF.Abs(y) < 1e6f && MathF.Abs(z) < 1e6f)) continue;
            list.Add(new MobEntity((int)(slot & 0x7FFFFFFF), x, y, z, 0f));
        }
        return list;
    }

    /// <summary>
    /// Finds the LIVE entity records anywhere in the process.
    ///
    /// Measured 2026-09-13: the 0x90 array the bootstrap anchors on is a
    /// frozen SNAPSHOT -- plausible data, but its +0x04 tick never advances
    /// and no position ever changes. The live records use the same 0x90
    /// layout but are scattered across many small regions (3-11 slots each),
    /// so walking a single region can never find them all. That is why the
    /// map showed a subset of mobs, standing still.
    ///
    /// Liveness is decided by the tick at +0x04 ADVANCING by a small positive
    /// step -- which needs nothing to move, so it works in a zone of idle
    /// NPCs. Plain "the value changed" is useless here: it matches every
    /// mutating byte in a 11GB process.
    /// </summary>
    public int SweepLiveSlots(PlayerFinder.Zone zone, Action<long, long>? onProgress = null)
    {
        var candidates = new List<long>();
        var regions = _mh.EnumRegions(writableOnly: true).ToList();
        var total = regions.Sum(r => r.Size);
        long done = 0;

        foreach (var region in regions)
        {
            for (var addr = region.Base; addr < region.Base + region.Size;)
            {
                var remaining = region.Base + region.Size - addr;
                var want = (int)Math.Min(ChunkBytes * 4, remaining);
                var buf = _mh.ReadBytes(addr, want);
                if (buf is null) LastReadFail++;
                if (buf is not null)
                {
                    for (var i = 0; i + 0x18 <= buf.Length; i += 4)
                    {
                        var id = BitConverter.ToInt32(buf, i);
                        if (id <= 0 || id > 1_000_000) continue; // cheapest reject
                        if (!zone.Contains(BitConverter.ToSingle(buf, i + 0x0C),
                                           BitConverter.ToSingle(buf, i + 0x10),
                                           BitConverter.ToSingle(buf, i + 0x14))) continue;
                        candidates.Add(addr + i);
                        if (candidates.Count >= 400_000) break;
                    }
                }
                done += want;
                onProgress?.Invoke(done, total);
                if (remaining <= want || candidates.Count >= 400_000) break;
                addr += want - 0x18;
            }
            if (candidates.Count >= 400_000) break;
        }

        var before = new Dictionary<long, int>(candidates.Count);
        foreach (var a in candidates)
        {
            var b = _mh.ReadBytes(a + 4, 4);
            if (b is not null) before[a] = BitConverter.ToInt32(b);
        }
        Thread.Sleep(1200);

        var live = new List<long>();
        foreach (var (a, was) in before)
        {
            var b = _mh.ReadBytes(a + 4, 4);
            if (b is null) continue;
            var delta = (long)BitConverter.ToInt32(b) - was;
            if (delta <= 0 || delta > 5000) continue;
            if (BitConverter.ToInt32(b) < 10_000) continue;
            live.Add(a);
        }

        _liveSlots = live;
        return live.Count;
    }

    /// <summary>Reads the live slots found by <see cref="SweepLiveSlots"/>.
    /// Cheap -- a few dozen small reads -- so it can run at full refresh rate.
    /// Deduped to the freshest sample per id, since an entity occupies two or
    /// three consecutive slots as interpolation history.</summary>
    public List<MobEntity> ReadLive()
    {
        var freshest = new Dictionary<int, (int Tick, MobEntity Entity)>();
        foreach (var slot in _liveSlots)
        {
            var buf = _mh.ReadBytes(slot, 0x88);
            if (buf is null) continue;
            var id = BitConverter.ToInt32(buf, 0);
            if (id <= 0) continue;
            var tick = BitConverter.ToInt32(buf, 4);
            if (freshest.TryGetValue(id, out var ex) && ex.Tick >= tick) continue;
            freshest[id] = (tick, new MobEntity(id,
                BitConverter.ToSingle(buf, 0x0C), BitConverter.ToSingle(buf, 0x10),
                BitConverter.ToSingle(buf, 0x14), BitConverter.ToSingle(buf, 0x18),
                BitConverter.ToInt64(buf, 0x80)));
        }
        return ResolveNames(freshest.Values.Select(v => v.Entity)).OrderBy(e => e.Id).ToList();
    }

    public sealed record RelaxedRecord(long Slot, int Id, int Tick, float X, float Y, float Z, int Sig1, int Sig2);

    /// <summary>Walks the region looking ONLY for stride-aligned slots whose
    /// position lands inside the zone and whose id is non-zero -- deliberately
    /// ignoring the 1.4/180 constants, so it can reveal whether that signature
    /// is universal or is quietly hiding entities.</summary>
    public List<RelaxedRecord> ScanRelaxed(PlayerFinder.Zone zone)
    {
        var results = new List<RelaxedRecord>();
        if (!IsBootstrapped) return results;

        var phase = (Anchor - Region.Base) % RecordStride;
        if (phase < 0) phase += RecordStride;
        var end = Region.Base + Region.Size;
        var freshest = new Dictionary<int, RelaxedRecord>();

        for (var addr = Region.Base + phase; addr + RecordStride <= end;)
        {
            var recordsLeft = (end - addr) / RecordStride;
            var n = (int)Math.Min(recordsLeft, ChunkBytes / RecordStride);
            if (n <= 0) break;
            var want = n * RecordStride;
            var buf = _mh.ReadBytes(addr, want);
            if (buf is null) { addr += want; continue; }

            for (var i = 0; i + RecordStride <= buf.Length; i += RecordStride)
            {
                var id = BitConverter.ToInt32(buf, i);
                if (id <= 0) continue;
                var x = BitConverter.ToSingle(buf, i + 0x0C);
                var y = BitConverter.ToSingle(buf, i + 0x10);
                var z = BitConverter.ToSingle(buf, i + 0x14);
                if (!zone.Contains(x, y, z)) continue;

                var tick = BitConverter.ToInt32(buf, i + 4);
                if (freshest.TryGetValue(id, out var ex) && ex.Tick >= tick) continue;
                freshest[id] = new RelaxedRecord(addr + i, id, tick, x, y, z,
                    BitConverter.ToInt32(buf, i + 0x1C), BitConverter.ToInt32(buf, i + 0x20));
            }
            addr += want;
        }
        return freshest.Values.OrderBy(r => r.Id).ToList();
    }

    /// <summary>Every live record's full raw bytes (freshest sample per id),
    /// for offline analysis of which offset actually identifies a species.</summary>
    public List<(int Id, byte[] Raw)> RewalkRaw()
    {
        if (!IsBootstrapped) return new List<(int, byte[])>();

        var slots = _scanner.EnumerateBySignature(Anchor, Region, RecordStride, Signature);
        var freshest = new Dictionary<int, (int Tick, byte[] Raw)>();
        foreach (var slot in slots)
        {
            var raw = _mh.ReadBytes(slot, RecordStride);
            if (raw is null || raw.Length < RecordStride) continue;
            var id = BitConverter.ToInt32(raw, 0);
            if (id == 0) continue;
            var tick = BitConverter.ToInt32(raw, 4);
            if (freshest.TryGetValue(id, out var ex) && ex.Tick >= tick) continue;
            freshest[id] = (tick, raw);
        }
        return freshest.Select(kv => (kv.Key, kv.Value.Raw)).ToList();
    }

    /// <summary>Every id -> name pair harvested so far. These ids are ground
    /// truth for "what is actually loaded in this zone", which makes them a
    /// far more selective scan key than any structural guess.</summary>
    public static Dictionary<int, string> AllKnownNames()
    {
        lock (NamesLock) return new Dictionary<int, string>(KnownIdNames);
    }

    /// <summary>The authoritative (debug-string-derived) name for an id, if
    /// one has been harvested. This is ground truth, unlike anything
    /// inferred from record fields.</summary>
    public static bool IsPlayerId(int id) { lock (NamesLock) return PlayerIds.Contains(id); }

    public static string? NameForId(int id)
    {
        lock (NamesLock) return KnownIdNames.TryGetValue(id, out var n) ? n : null;
    }

    /// <summary>Names each record from its EXACT id, and nothing else.
    ///
    /// An earlier version also propagated names to other records sharing the
    /// same +0x80, on the theory that +0x80 was a species key. That is
    /// DISPROVEN -- `speciesfield` tested every 4- and 8-byte field in the
    /// record against debug-string ground truth and no field distinguishes
    /// species: +0x80 is constant across different species (a shared
    /// manager/zone pointer), +0x04 is the tick counter, +0x08 a 1/0 flag.
    /// Propagating by +0x80 produced confidently WRONG labels (an entire
    /// zone rendered as "Decayed skeleton"), which is worse than an honest
    /// blank. Species identity is not stored in this record.</summary>
    private static List<MobEntity> ResolveNames(IEnumerable<MobEntity> entities)
    {
        lock (NamesLock)
        {
            return entities
                .Select(e => KnownIdNames.TryGetValue(e.Id, out var n) ? e with { Name = n } : e)
                .ToList();
        }
    }

    private long? TryLocalWindow(long candidate, int windowSlots = 200)
    {
        for (var k = -windowSlots; k <= windowSlots; k++)
        {
            var slot = candidate + (long)k * RecordStride;
            var matched = true;
            foreach (var (offset, expected) in Signature)
            {
                var cur = _mh.ReadBytes(slot + offset, expected.Length);
                if (cur is null || !cur.AsSpan().SequenceEqual(expected)) { matched = false; break; }
            }
            if (matched) return slot;
        }
        return null;
    }

    /// <summary>Strips a leading article so "a cave sporeling" labels
    /// entities "Cave sporeling" rather than "a cave sporeling". Proper
    /// names ("Maelora") are left alone.</summary>
    private static string DisplayName(string mobName)
    {
        var trimmed = mobName.Trim();
        foreach (var article in new[] { "a ", "an ", "the " })
        {
            if (trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[article.Length..];
                break;
            }
        }
        return trimmed.Length == 0 ? mobName : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private MemoryRegion? FindRegion(long addr) =>
        _mh.EnumRegions(writableOnly: true).Cast<MemoryRegion?>()
            .FirstOrDefault(r => addr >= r!.Value.Base && addr < r.Value.Base + r.Value.Size);
}
