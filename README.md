# M&M Damage Parser (C# rewrite)

## Handoff status (read this first if you're picking this up fresh, e.g. in Claude Code)

This project has been built and iterated entirely through a chat-based
session with no direct access to Jay's Windows machine or the game --
every fix was diagnosed from pasted terminal output, shipped as files,
then verified by Jay running it and pasting the results back. That
constraint won't apply anymore once this is running locally in Claude
Code (it can read/write/build files directly in this folder), which
should make iteration much faster -- but the discipline that got this
codebase working is worth keeping: **don't guess at a fix without real
evidence** (an error message, a log line, or the actual in-game text),
because this game's exact combat-text wording has repeatedly turned out
to differ from reasonable-looking assumptions.

Confirmed working against Jay's real game (verified via his own pasted
terminal output and/or test cases built from his exact text):
- Melee hits, both yours and an enemy's (verbs like "pierce"/"bites", not
  a hardcoded "hits")
- Enemy misses ("X tries to Y YOU, but misses!")
- Kills ("You have slain X!") -- closes the encounter immediately instead
  of waiting for the idle timeout
- Named-ability/spell hits ("Your Blast of Sleet hits X for N points of
  Cold Damage.")
- Heals ("Your Touch of Nature heals You for N health.")
- The heap-scan performance fix (SIMD `Span.IndexOf` instead of a naive
  byte-search loop) -- full sweeps went from ~75-80s to a projected ~4.5s
  of search time
- Encounter history persisting to `logs/history_<character>.json` across
  app restarts, and the event log split per character
  (`logs/combat_events_<character>.jsonl`). The character is now
  **auto-detected at startup** from the game's on-disk data
  (`CharacterDetector`); `character_name` in `settings.json` is just an
  optional override

### Line-pattern coverage (`config/offsets.json`)

The 19 patterns (confirmed damage/heal/kill/pet/time + 1 guessed resist +
engage/disengage) are verified against ~1,100 real captured lines
(`logs/combat_events*.jsonl`) plus `unmatched` captures, and
`tests/CoreTests/ConfigLoadTests.cs` asserts every shape still parses.
Confirmed working: your named spell/ability hits (with and without a
damage school, ending in `.` **or** `!` -- DoT ticks use `!`); another
player's named spell hits; **any** melee hit
(`<source> <verb> <target> for N points of damage` -- you->mob, mob->you,
other-player->mob, mob->other-player), verb kept as the "ability";
enemy/other special attacks (`X's Strike hits YOU/<name>`, attributed to
`X` not `X's Strike`); the `/time` command's clock + date lines (feed the
status-bar in-world clock); **your pet** (`Your pet Rebel hits <mob>...` ->
source `Rebel`, its own combatant row; `<mob> ... your pet Rebel` ->
incoming, mob stays the source); misses (yours, your pet's, incoming to
you or your pet); kills; and heals from you **or** another player. The local player is normalised to `"You"` whether the game writes
`Your`, `YOU`, or `you`. The `heals ` anchor is narrow on purpose -- a
bare `heal` matched dozens of `*.asset` icon names and `/help` text.

Still guesses / unknown wording -- **`debug_print_unmatched_lines` in
`offsets.json` is left ON; play, then send `logs/unmatched_<char>.log`**
(every anchor-carrying line that matched no pattern is appended there):
- **Critical hits** -- no confirmed wording, and Jay's current character
  is too low to crit. If a crit is the normal hit line with a
  `(Critical!)`-style suffix, the `$`-anchored regexes would reject it and
  the crit's damage would be dropped entirely. Capture one crit line and
  it's a one-pattern fix.
- **Your own misses** (`_unconfirmed_you_miss`) -- guessed as "You try to
  X Y, but miss!"; not seen in any capture (the game may simply not print
  them). Affects your accuracy % and the "announce misses" voice option.
- **Spell resists** (`_unconfirmed_you_resist`, anchor `resists `) --
  guessed as "`<target>` resists your `<spell>`!" by symmetry with the
  game's other third-person lines. Never captured. Needed for the
  "announce resists" voice option and the `Resist` event type (which is
  currently folded into the miss count).
- **Multi-word capitalised NPC names as a melee target** (`Snappy slashes
  Guard Captain Voss for ...`) -- the shaped target group only spans a
  run of capitalised words, so a title + name should work but an
  odd-shaped one might not. None seen yet.
- **DoT ticks / damage shields / dodges / parries** -- none seen; a DoT
  (poison spell) currently shows as a normal instant hit line.
- **Character name auto-detection** -- DONE, via a different route than
  the abandoned one. (The old approach -- heap-scanning for the literal
  name -- never worked: the name isn't in the window title and a full
  sweep for "Radust" found nothing, so that text isn't stored as a
  normal .NET String the way combat text is.) Instead `CharacterDetector`
  (`src/Core/Config/`) reads the game's own on-disk data under
  `%USERPROFILE%\AppData\LocalLow\Niche Worlds Cult\Monsters and Memories\`:
  the last "local player entered world" line in `Player.log`
  (`Migration complete for <Name>`, or a `client <Name> ... received pet
  ids` line with a local-player stack), cross-checked against the
  per-character folders (`<server>\<Name>\`). `Program.cs` calls this at
  startup, so logs/history segment by character automatically with no
  config. `character_name` in `settings.json` is now just an optional
  manual override (blank by default). A background thread
  (`CharacterWatchLoop`) re-checks every `character_recheck_seconds`
  (default 10, 0 disables); on a **mid-session relog** it calls
  `MeterState.SwitchCharacter` -- the in-progress fight is filed under the
  character you're leaving, then the event log + encounter history
  repoint to the new character (its saved history reloads) and the window
  title updates, no restart needed. The watch is skipped when
  `character_name` is a manual override. Verified: correctly returns
  "Radust" on Jay's machine (see `CharacterDetectionTests` / `RelogTests`).
- The `"heal"` anchor is intentionally broad (it also picks up unrelated
  flavor text like seasonal-buff messages mentioning "healing spells") --
  harmless since those don't match any line pattern and get silently
  dropped, but they *will* show up in `logs/unmatched_<char>.log` when
  `debug_print_unmatched_lines` is on, so skip past them.
- No pattern currently sets `EventType.Crit` -- there's no confirmed crit
  wording (see the coverage section above). Crit **counts** therefore
  read 0 until that's captured, and crit **damage** may be missing
  entirely if crit lines carry a trailing suffix.



C# port of the Python DPS/HPS meter, replacing it entirely to get rid of a
class of freeze bugs caused by Python's GIL (a single background thread
doing heavy memory scanning could starve the GUI thread for the length of
a full sweep -- see the "(Not Responding)" freeze from before). C# has
real OS threads and no GIL, so that class of bug can't happen here, and
`MeterState` is now genuinely thread-safe (locked) instead of "usually
fine by luck," which the old Python version was not.

## What's the same as before

- Same heap-scanning approach (`heap_scan` mode): no fixed pointer chain,
  scans memory for anchor substrings every poll, recovers the owning .NET
  String object, dedups by address AND by content (the `<color=...>`
  duplicate-object issue).
- Same tuned config values: 50s encounter idle timeout, 45s full-sweep
  interval, the yield-every-15-regions freeze fix, 3.0s content-dedup
  window -- `config/settings.json` and `config/offsets.json` are ported
  directly from the working Python config, not reset to defaults.
- Still needs to run as **Administrator** (ReadProcessMemory needs
  elevation) -- same as before.

## The window

Two panes, ACT-style:

- **Encounters** (left) -- one row per fight: start time (a `●` marks the
  live one), duration, total damage, and who was in it. Sortable headers.
- **Combatants** (right) -- for the selected encounter: name, total,
  DPS/HPS, share of the pull with an in-cell bar, hits/crits/misses,
  accuracy. Each combatant gets a stable colour tint; you're always blue.
  Sortable headers, and a Damage/Healing + All/Just-you toggle up top.

**Double-click an encounter (or a combatant)** to open its detail window:
a summary strip (duration / total damage / raid DPS / your DPS /
healing), the full combatant table, and the per-ability/spell breakdown
for whichever combatant is selected. It refreshes live while the fight is
going and freezes on the last data once the encounter ages out of
history. Multiple detail windows can be open at once (one per encounter).

### Overall totals (toolbar "Totals")

A running aggregate of **every fight this character has had** -- each
encounter is folded in as it closes, so the totals outlive the
50-encounter history cap *and* app restarts (persisted to
`logs/totals_<char>.json`). The live fight is included on top as it
happens. Header shows: since-when, fights, kills, time in combat, total
damage/healing, and the overall DPS (total over summed combat time).
Same combatant table + per-combatant ability breakdown as the detail
window, with Damage/Healing and a "Just you + pet" toggle. **"Reset
totals"** wipes it for this character and starts the tally from now
(the encounter list / history and personal records are untouched -- and
so is the reverse: "Clear History" doesn't touch the totals).

### DoT / debuff timers (toolbar "Timers")

A small always-on-top-capable window listing the DoTs/debuffs **you**
have running, each with a bar counting down to expiry (green → amber →
red), soonest first. It opens itself the first time a tracked effect is
running (once per session).

The game logs **no cast message and no reliable "wears off" line**, and
there's no spell-data file, so durations are supplied by hand in
`config/spells.json`:

```json
{ "spells": [
  { "name": "Minor Frostbite", "duration_seconds": 24, "tick_seconds": 6, "kind": "dot" }
] }
```

Only entries with `duration_seconds > 0` are tracked. A timer starts (and
the countdown resets to full) when the spell's **damage tick** is seen on
a target -- specifically the first tick, or the first after a gap of more
than ~2 `tick_seconds`, or one after the previous timer already expired
(i.e. a re-cast). Ticks in between don't extend it. A kill clears that
target's timers; an expired one greys out for a few seconds then drops.

**Limitation:** pure debuffs that deal no damage (snares, resist-debuffs,
mez) can't be detected yet -- there's no tick to catch and no cast line.
If you want those, cast one and send `logs/unmatched_<char>.log` so the
"lands on" / "wears off" wording can be added to `offsets.json`.

### In-world clock (bar at the top)

A big bar at the very top of the window shows M&M's time of day
(`☾ 9:11 PM · Autumn` / `☀ 2:10 PM · Summer`), ticking in real time and
blue-tinted at night.

The game only reveals the time via **`/time`** (it prints `Game Time: 3 AM`
plus the date and the real "Earth Time" — all three as one chat block).
**Run `/time` in game and the clock sets itself** — the parser reads that
block out of chat memory, splits it, anchors on the time, and extrapolates
forward at **72 real minutes per in-world day** (M&M's confirmed rate).
Run `/time` again whenever you want it re-synced. Hover the bar for the
full date ("...the 4th of Harvesttide in the year 608... It is Autumn").
**Click the bar** to set the time by hand (before you've run `/time`, or
if the wording ever changes). The anchor is saved to `logs/gametime.json`,
so after a restart the clock is already roughly right.

### Pets (toolbar "Pets")

Set `pet_names` in `config/settings.json` (e.g. `["Rebel"]`) to your
pet's name. The game logs your own pet as **`Your pet Rebel hits …`** /
**`… bites your pet Rebel …`**, and the parser's pet patterns strip the
`Your pet ` wrapper, so the bare name (`Rebel`) is what to put in
`pet_names`. (Other players' pets show as `Owner's pet` and aren't
tracked.) In the main table a pet gets its own row, tinted like you
and tagged `(pet)`, and the **"Just You + pet"** scope shows you *and* the
pet with percentages relative to just the two of you. Personal records
stay yours only.

The **"Pets" button** opens a standalone window (with an "always on top"
option) showing just the pet's damage / DPS / ability breakdown for the
current fight -- keep it in a corner. It also **opens itself** the first
time your pet lands a hit in a fight (once per fight -- close it and it
won't come back until the next encounter; it never steals focus from the
game). Turn that off with `"pet_window_auto_open": false` in
`settings.json`.

### Records (toolbar "Records")

Per-character personal bests -- your **biggest single hit** with each
ability/spell and with each damage type -- persisted to
`logs/records_<character>.json`, reloaded on a relog. Only *your* outgoing
damage counts (crits included; another player's 999 doesn't touch your
records). "Reset records" wipes them for the current character. New
records are also spoken (see Voice).

### Chat (toolbar "Chat")

The game's **non-combat** chat -- everything the meter throws away: OOC /
say / shout / NPC dialogue, the zone-wide death feed, buff/debuff fades,
loot lines, skill-ups, casting messages, mob status flavor
("a cave sporeling is chilled to the bone."), "You have entered ...".
Combat is deliberately excluded -- that's the meter's job.

The **"Chat" button opens one window per category**, tiled down the right
edge of the screen (wrapping into more columns). Categories are defined in
**`config/chat.json`** -- ordered `{name, match:[regex…]}` rules,
first-match-wins, and a final **"Other"** window that catches everything
unmatched (watch it, add rules, relaunch -- no rebuild). A **`deny`** list
in the same file drops lines from every window (seeded with the damageless/
absorbed hits that carry no combat anchor and so leak past the combat
filter). Empty `categories` (or a missing file) → one combined "Chat"
window, the old behaviour. Each window: dark background, per-line in-game
colour (dim ones lifted), `[HH:mm:ss]` prefix, "Follow" (wheel-scroll up
pauses it), per-window "Clear", per-category "Save…".

**How it's captured.** No chat-log object exists to read -- the same heap
sweep that finds combat text also scans `config/offsets.json`'s
`buffer.chat_anchors` (just `"<color=#"`, the colour-wrapped copy the game
keeps of nearly every chat line). A recovered line reaches the Chat feed
**only if** it carries no combat anchor and matches no combat pattern;
anything combat still flows to the meter / the unmatched-line log exactly
as before. `ChatLog` then drops colour-wrapped non-chat noise: interned tag
fragments, item names (short, no sentence punctuation), the hover tooltip
(its colour tag isn't at char 0). Set `buffer.chat_anchors` to `[]` to
turn the whole feature off (the button greys out, no extra scanning).

Purely plain (never colour-wrapped) chat lines are invisible -- every
channel seen so far *is* colour-wrapped, so this hasn't mattered, but it's
the known gap. **`MnmDamageParser.exe --chat-test [seconds]`** attaches
headless and prints the live feed with each line's category + a
per-category tally + the combat/unmatched split -- run it to tune
`config/chat.json` against whatever the game is doing now.

### XP (toolbar "XP")

Toggles a panel at the bottom of the window that reads the **in-game XP
bar off the screen** (no game memory/files -- the game logs nothing about
XP) and shows fill %, last gain, rate (%/min and levels/hour) and ETA to
the next level. Because it only sees the in-level bar, "XP" is always
*percent of the current level* (100% = one level); a level-up is detected
when the fill suddenly drops.

**Reading it precisely.** Every column of the bar gets a *fractional*
"fill-ness" (its colour projected onto the empty→fill axis), and the mean
of those is the fill level -- so the one part-lit column at the fill edge
counts for its true fraction instead of being rounded to 0 or 1. That's
sub-pixel: ~0.02% of a level per reading instead of ~0.5%. Each reading is
the **median of 3 quick captures** (`samples_per_read`) so a tooltip or
floating combat-text drawn over the bar for one frame can't skew it, and
readings are taken every **0.6s** (`interval_sec`). Gains too small to
clear `noise_floor` (0.08% of a level) aren't discarded -- they accumulate
against a fixed reference and land once they add up, so a slow XP trickle
still registers. The **displayed** fill % is the median of the last 5
reads and the rate is smoothed, so a shimmering/animated bar doesn't make
the panel twitch (the gain maths still runs on the raw reads). The panel
also rides out a few dropped captures without flipping to an error.

**Calibration.** **Calibrate…** in the panel opens a dim full-screen
sheet -- drag a box over the XP bar that covers **both a filled stretch
and an empty stretch**. It works out the fill and empty colours from the
capture itself (2-means clustering), so it doesn't matter what level the
bar is at -- and it shows a **contrast** number: below ~12 the two colours
are too close and it'll say so (a bad calibration reports noise as XP).
`config/xpbar.json` keeps Jay's region (`189x17 @ 1706,164`); recalibrate
after moving the game window / HUD or changing display scaling (100%
here). "Reset session" zeroes the counters.

Wire it into voice with the "level up" toggle in the Voice menu.
`src/Core/Xp/` has the pure logic (`FillMeter.FractionPrecise`,
`BarCalibration`, `XpTracker`) with tests; `src/App/` has the screen
capture (`ScreenXpReader`), the sampler thread, and the calibration overlay.

### Voice (toolbar "🔊 Voice ▾")

Spoken callouts via Windows TTS, on a dedicated worker thread. The queue
is bounded (3) and every utterance is stamped on enqueue: the worker
**drops anything that waited more than 5s** rather than reading a stale
backlog, non-urgent callouts skip themselves if the queue isn't empty,
and an urgent callout (engage) purges whatever is mid-sentence and jumps
the line. This is what fixed the ~20s combat lag -- the old queue never
shed stale items.

Speech goes through the SAPI `SpVoice` COM object, **not** `System.Speech`
-- the built-in Zira/David "Desktop" voices silently ignore SSML
`<prosody pitch>`, so the old elf-pitch code did nothing. They *do* honour
the old `<pitch absmiddle="N"/>` SAPI markup, which is what the pitch
shift now uses.

There is **no master switch** -- voice is on when at least one callout is
ticked. The dropdown toggles, each saved to `config/settings.json`'s
`voice` block:
- **you enter combat** -- "engaging" (the game's line is a bare
  **"Starting to attack."** with no target; if a target ever shows up it's
  spoken too). `EventType.Engage`, pattern `you_engage`. Falls back to the
  first swing you land -- and *that* path does know the target, so you'll
  usually hear "engaging a cave bear" from the fallback. A fight a mob
  opens by jumping you is left to the attacker callout instead.
- **combat ends** -- "combat over" on the game's bare **"Stopped
  attacking."** line (`EventType.Disengage`). Suppressed right after a
  kill (you already heard "X down"). This line also closes the live
  encounter ~6s early instead of waiting out the 50s idle timeout -- a
  quick target switch ("Stopped attacking" then "Starting to attack")
  doesn't split the fight.
- **land a hit** -- says "hit"/"crit" when *you* land one, rate-limited to
  `hit_cooldown_seconds` (default 2.5s) so a fight isn't a stutter
- **miss** -- says "miss" (needs the `_unconfirmed_you_miss` pattern to be
  right -- see coverage notes)
- **are resisted** -- says "resisted" (needs `_unconfirmed_you_resist` --
  **guessed wording**, verify against a real resist line)
- **something new attacks you** -- speaks the attacker's name ("a rotting
  skeleton") the first time it hits/misses you in a fight, then stays
  quiet for its every following swing. The known-attacker set clears
  after 20s with no incoming hits, or when a mob dies -- so a fresh add
  re-announces.
- **you kill something** -- "a rotting skeleton down" on each kill line.
- **set a new record** -- "New record. Blast of Sleet. 47." (a hit that
  beats both its spell record and its damage-type record is announced
  once, not twice)
- **level up** -- "Level up!" (fired by the XP panel's level-up detection)

Below the toggles:
- **Voice** submenu -- one radio item per installed voice. This lists both
  the classic SAPI voices **and** the "mobile"/OneCore ones (SpVoice can
  speak with a OneCore token, it just doesn't list them by default -- we
  enumerate that category too). Picking one sets `voice.voice_name`
  (substring match, so `"Zira"` finds `"Microsoft Zira Desktop"`) and
  speaks a test line.
  **To get a British / non-US voice** (e.g. something in the ballpark of
  Daisy Ridley): *Windows Settings -> Time & language -> Speech -> Manage
  voices -> Add voices -> English (United Kingdom)*. That installs
  **Microsoft Hazel** (en-GB female); it'll show up in this submenu after
  a restart of the meter. Pair it with a small **Elf pitch** nudge for a
  younger timbre. (Windows 11 "Natural" neural voices install the same way
  and usually enumerate too, though not all of them actually speak through
  SAPI.) Out of the box the machine only has US voices (Zira/David/Mark).
- **Elf pitch (lighter)** -- toggles `voice.voice_pitch` between `"+6"`
  and `""`. The value is a SAPI pitch level (`-10`..`+10`); it's delivered
  as `<pitch absmiddle="6"/>` markup, which Zira/David actually respond to
  (a legacy `"+12%"` is still accepted and mapped to ~`+3`).
- **Test voice** -- speaks a line regardless of the toggles

The console prints `[voice] engine: ...` and `[voice] ready (<voice
name>)` at startup and `[voice] <phrase>` each time it speaks.
`MnmDamageParser.exe --voice-test` speaks a plain then a `+6` line so you
can A/B the pitch, then simulates combat callouts, and exits.
`--ui-preview [--detail]` opens the window with synthetic data and no game
attach (the "Chat" button opens the per-category windows with sample lines).
`--chat-test [seconds]` attaches headless and prints the non-combat chat
feed -- see the Chat section above.

## What's different

- Regex patterns in `offsets.json` now use .NET named-group syntax
  `(?<name>...)` instead of Python's `(?P<name>...)`. If you ever add a
  new line pattern by hand, use `(?<name>...)`.
- Only `heap_scan` mode was ported (the only mode this game ever actually
  used -- `dotnet_string_list`/`ring_fixed_stride`/`scrolling_blob`/
  `hp_table` were unused stubs in the Python version and weren't carried
  over).
- `MeterState` is now actually thread-safe (a lock protects every
  read/write), and the GUI never touches a live, mutable game-state object
  -- it only ever gets copied-out snapshots (`GetEncounterSummaries`,
  `Snapshot`, `AbilityBreakdown`). This was a real gap I found and fixed
  during the port: without it, the GUI thread and the background scanner
  thread really could corrupt a Dictionary by touching it at the same
  time, which the old Python code got away with only because the GIL
  serializes individual bytecodes.

## Project layout

```
src/Core/     -- all the logic: memory reading, heap scanning, line
                 parsing, encounter tracking. Plain net8.0, no Windows
                 Desktop dependency, no NuGet packages needed to build.
src/App/      -- the WinForms GUI (net8.0-windows). This is the ONE
                 piece that could not be compiled in the dev sandbox this
                 was built in (no NuGet access there for the Windows
                 Desktop reference pack) -- see "What I could and
                 couldn't verify" below.
src/Sniffer/  -- standalone network sniffer (net8.0 console app), unrelated
                 to the memory-scanning code above -- see "Network sniffer"
                 below.
src/MemScan/  -- interactive live memory scanner (net8.0 console app,
                 references Core for IMemoryReader) -- see "Memory scanner"
                 below.
tests/CoreTests/ -- hand-rolled test suite (no xUnit dependency) covering
                 Core: encounter separation, ability breakdowns, the heap
                 scan's baseline/dedup/content-dedup/yield behavior
                 against FAKE memory, and a real concurrency stress test
                 (background thread + "UI" thread hammering MeterState
                 concurrently for 3 seconds, asserting no exceptions).
                 Run with: cd tests/CoreTests && dotnet run
config/       -- settings.json, offsets.json (ported from the Python
                 version's tuned values), spells.json (DoT timers),
                 xpbar.json (XP-bar region), chat.json (Chat window
                 categories). The ONLY copy -- every launch mode (dist\
                 shortcut, bin\ exe, `dotnet run`) walks up from the exe to
                 find this folder. Edit in place, relaunch.
logs/         -- combat_events + history JSON, and unmatched_<char>.log
                 when the debug flag is on. Also always this one folder.
                 logs/sniffer/ holds the network sniffer's .pcap + hex dumps.
install.ps1   -- one-shot: publishes to dist\ and creates the elevated
                 Desktop shortcut. See "Running it".
dist/         -- publish output (gitignored). Created by install.ps1.
```

## Network sniffer (src/Sniffer)

> **Verdict (2026-09-14): the wire is AES-encrypted and unreadable. This
> route cannot supply radar/position data.** See "Is it encrypted or just
> compressed?" below for the evidence, which is much stronger than the
> hand-wave this repo previously recorded.

A second, independent tool from everything above -- it doesn't touch game
memory at all. Where the damage parser reads combat text the game already
renders to chat, this captures the raw network traffic between the game
client and its server, via [Npcap](https://npcap.com/) (already installed
and running as a service on Jay's machine) through the SharpPcap/PacketDotNet
NuGet packages.

**What it does today (phase 1 -- capture only, no protocol decoding):**
1. Waits for `mnm.exe` to appear (or attach to a specific pid with `--pid`).
2. Auto-detects its server connection(s) by querying the OS's own
   connection table for that process's PID (`GetExtendedTcpTable` /
   `GetExtendedUdpTable` via `iphlpapi.dll` -- the same data `netstat -ano`
   reads, just queried directly instead of shelling out and scraping text).
   TCP connections give an exact remote host:port (Windows tracks the peer).
   UDP is connectionless at the OS level -- Windows only exposes the
   *local* bound port for UDP sockets, not the remote peer -- so UDP
   targets are filtered by local port only, and the remote address falls
   out of whatever traffic actually gets captured on it.
3. Builds a BPF capture filter from whatever it found and opens every
   non-loopback network adapter with SharpPcap/Npcap, promiscuous mode.
4. Every matching packet gets written to **two places**: a Wireshark-
   readable `.pcap` file, and a plain-text hex+ASCII dump with a
   timestamp, direction (`C->S`/`S->C`), protocol, and both endpoints --
   both under `logs/sniffer/`, one pair of files per run.

**It does not decode the game's protocol.** The byte layout is unknown, and
per this project's established rule (see the top of this README --
"don't guess at a fix without real evidence") that has to wait until real
captured bytes are in hand to look at. The hex log *is* that evidence --
play for a bit with something specific in mind (loot a specific item, take
damage, open a vendor window), note the rough time, then go find the
matching bytes in the hex log or pcap. That's the starting point for adding
real opcode parsing, the same way `offsets.json`'s line patterns only ever
got added after a real captured line was in hand.

**Running it** (from an Administrator terminal is safest, though on this
machine Npcap is configured to allow capture without elevation -- if
`device.Open()` fails for you, that's the fix):

```
cd src\Sniffer
dotnet run
```

Useful flags: `--process <exe name>` (default `mnm.exe`), `--pid <n>`
(skip process-name lookup and attach to an exact pid -- handy if multiple
copies could match), `--discover-seconds <n>` (default 30 -- how long to
watch for connections before locking in the capture filter; launch the
sniffer *before or right as* you connect so it sees the handshake),
`--verbose` (print a line per matched packet, not just a 5s summary),
`--filter "<bpf expression>"` (override the auto-built filter entirely),
`--iface <substring>` (only open adapters whose name/description contains
this), `--no-filter` (capture everything on the opened adapter(s), no BPF
filtering at all -- a debugging escape hatch for when auto-detection finds
the wrong thing).

Ctrl+C stops cleanly and flushes both output files.

**Known limitation:** this is a single capture session per run -- if the
game reconnects to a different server mid-session (e.g. a zone that's
actually a different world-server process), packets on the new connection
won't match the filter that was locked in at startup. Restart the sniffer
if that happens. Fixing this properly (re-discovering connections
periodically and refreshing the filter without dropping already-captured
packets) is future work, once there's an actual reason to need it.

### Is it encrypted, or just compressed? (2026-09-14)

Run the analyser over any captured payload log:

```
dotnet run --project src\Sniffer -c Release -- --analyze logs\sniffer\payloads_<stamp>.log --filter UDP:8209
```

**The earlier conclusion in this repo was right by luck, not by argument.**
It claimed "encrypted" from a flat byte histogram -- but *compressed* data
is also flat. A flat histogram proves high entropy and nothing else, and
if the payload were merely compressed we could decompress and read it. So
the question was reopened and tested properly.

**First lesson: split by conversation before analysing anything.** A
capture of "the game's traffic" also contains TLS to auth/patch hosts. The
first clean run reported "no structure" while its most common byte
prefixes were TLS record headers (`16 03 03`) and X.509 certificate OIDs
(`06 03 55 1D 20`) -- it was describing HTTPS, not the game. Only 80 of
13,110 packets were TLS, but they dominated the "repeated prefixes" list
because they were the only repeats. `--filter UDP:8209` isolates the game.

**On the game stream itself, three independent results:**

1. **No plaintext header anywhere.** Per-byte-offset entropy is 8.0 bits
   at *every* offset 0..23, in both directions. Games normally encrypt the
   body but leave a readable length/opcode/sequence header; there isn't
   one. Even the framing is encrypted.
2. **Zero repeats.** Not one exact duplicate payload among 13,030 packets,
   and no 8-byte prefix occurs twice. Deterministic compression of
   repeated messages would collide; a per-packet nonce never does.
3. **Block alignment -- the decisive one.** *100.0%* of payloads, both
   directions, are congruent to **4 mod 16**: lengths run 36, 52, 68, 84,
   100, 116, ... and 1348, 1364, 1380. Every packet is 4 bytes plus a whole
   number of 16-byte blocks. That is a **128-bit block cipher (AES) with a
   4-byte header/nonce**. Compression produces no such quantisation.

**Why this closes the route, not just blocks it.** Reading these packets
would require the session keys, which live in the client's memory. But if
we can pull keys out of memory, we can equally read *positions* out of
memory -- and the client has to decrypt into memory regardless. Sniffing
is therefore strictly harder than the memory route for the same goal, with
the extra step of reimplementing the protocol on top. **Use MemScan.**

## Memory scanner (src/MemScan)

A live, interactive Cheat-Engine-style REPL for exploring this game's own
process memory -- built to find *other* game state than combat text, e.g.
a mob's position and HP, which never gets printed to chat and so can't be
found by the text-anchor heap scan the damage parser uses.

**This is a materially different kind of tool than the damage parser or the
sniffer, and worth being clear-eyed about:** a combat log reader shows you
text the game already puts on your screen. A tool that surfaces every
mob's location and health in the zone -- including ones you can't see or
haven't engaged -- is a radar/ESP-style tool, which is against the ToS of
essentially every MMO regardless of implementation technique, and the kind
of thing anti-cheat is specifically built to catch. Built at Jay's explicit
request after that trade-off was discussed -- if you're picking this repo
back up later and weren't part of that conversation, that risk still
applies to using it.

**Why this game's memory is approachable at all:** the damage parser's own
`config/offsets.json` already reveals the game runs on **Mono** (its
`.NET string` header offsets -- length at `0x10`, chars at `0x14` -- are
exactly Mono's 64-bit `MonoObject` layout: an 8-byte vtable pointer + 8-byte
sync block, then the fields). That means mobs are almost certainly plain
Mono-managed objects too, which makes them findable with the same
techniques used to build most "external" tools for Mono/Unity games:

1. **`find "<exact mob name>"`** -- searches live memory for that string,
   the same way the damage parser finds chat text, and returns the live
   Mono string object's address. In practice, the most useful hits are a
   distinct debug format the game emits per-instance: **`"npc <name>
   <id>"`** (e.g. `"npc a fire beetle 89580"`). Confirmed: this string only
   appears **after some combat event has fired for that instance** (being
   attacked, attacking, etc.) -- searching for a mob you haven't engaged
   yet will come back empty even though it's alive and visible.
2. **`findid <name>`** -- atomic version of the above: finds the current
   `"npc <name> <id>"` string, extracts the id, and immediately does a
   `scan i32 <id>` for it, all in one step. Splitting "find the id" and
   "scan for it" into two separate commands was tried first and is a real
   trap: the game's heap layout shifts (Mono GC reallocates generations)
   within seconds to low minutes, so an id found one moment can point at
   completely unrelated data by the time a second, separate `dotnet run`
   invocation scans for it. Keeping it atomic avoids that gap entirely.
3. **`dump 0x<address>`** -- typed hex dump around a candidate address:
   every 4-byte-aligned offset gets an int32/float reading, and every
   8-byte-aligned offset additionally gets int64/double + a "looks like a
   pointer" flag. (Earlier versions of this tool only showed the *lower*
   4 bytes of each 8-byte word as int32/float, silently hiding whatever
   sat in the upper half -- fixed; if re-deriving anything from an old
   session's raw hex dumps, decode the upper halves by hand or re-dump.)
4. **`watch 0x<address> i32`** (or `f32`, etc.) -- polls one address and
   prints it every time it changes, to confirm a guess against something
   happening live in-game (the mob taking damage, moving).
5. **`vtable 0x<confirmed object base>`** -- for *individually-boxed* Mono
   objects (every Mono object's first 8 bytes are its vtable pointer,
   identical across instances of the same class, so this scans for that
   exact value to find every other live instance). **Confirmed NOT
   applicable to the mob records below** -- they're packed value-type
   array elements with no per-element header/vtable, so this would just
   match unrelated coincidental 8-byte patterns. Still useful for anything
   that genuinely is a boxed reference type.
6. **`radar <any mob name that has a fresh debug string> [durationSec=60]`**
   -- the actual "enumerate every mob live" answer for records like these
   (see "Confirmed against real game data" below for the full mechanism):
   finds one confirmed record via a bounded local-window search around a
   fresh debug-string id, then walks its whole containing array by fixed
   stride, refreshing every ~5s. Retries its own bootstrap for up to 75s.
   **Confirmed 2026-09-10: you don't have to fight anything yourself** --
   the `"npc <name> <id>"` string is zone-wide, so in a populated area
   someone else's recent fight against that species is enough. Only fall
   back to attacking it yourself if the bootstrap window times out. Each
   row is labeled with the mob's **species name** when known (see "Species
   names" below) instead of just a bare id.
7. **`map <name>`** -- graphical top-down dot map (`src/MemScan/MobMapWindow.cs`,
   `net8.0-windows` + WinForms; everything else in MemScan is plain
   console). Same bootstrap as `radar`, reusing the shared
   `src/MemScan/MobTracker.cs` (both commands were originally separate
   copies of the same bootstrap/re-walk logic; extracted into one class so
   they can't drift). Blocks the prompt (pumps the window's own message
   loop) until you close it. Zoomable and pannable: mouse wheel zooms about
   the cursor, left-drag pans, `F` toggles follow/free, `0` fits
   everything, `+`/`-` zoom, `N` toggles name labels. Distance rings are
   labelled in world units so the zoom level is readable, and each dot
   carries a heading tick. Dots are labeled by name when known (orange) and
   `id <n>` when not (red).

   Mob dots are **live positions** and your own position is live too. (Both
   were broken until 2026-09-14 -- the map drew spawn points that never
   moved; see "SOLVED: live positions" below for what the bug was.)

   The window repaints at 20Hz because `Rewalk` was rewritten to read the
   record array in **1MB blocks** rather than issuing two
   `ReadProcessMemory` calls per 144-byte slot (~230k syscalls per walk,
   ~3 seconds, which is why the old map repainted every 3s). It also
   remembers the narrow address band records actually occupy and rescans
   only that, doing a full-region sweep every ~4s in case the band grows.
   **Measured: 7ms for the first full walk, then 0ms.** That work stands --
   it just turned out to be reading stale data quickly.

   **"You" starts as the mob centroid (an approximation) and upgrades to
   your REAL position** once the map locates it in the background -- which
   takes a few seconds of you walking. The caption says which is in use.
   See "Finding the player" below.

There's also a classic Cheat Engine value scanner as a fallback/
alternative path: **`scan <i16|i32|i64|f32|f64|byte> <value>`** does a
first scan of all writable memory for that exact value; **`next
changed`** / **`unchanged`** / **`increased`** / **`decreased`** / `next
<newValue>` narrows the surviving candidates; **`list`** shows current
candidates with live values; **`nextfield <offset> <type> <value>`**
narrows by a *second* field at a fixed relative offset from each
candidate (e.g. verify a known shared constant to rule out coincidental
matches). For fields with **no on-screen number at all** (this game shows
HP as a plain shrinking bar, no digits) there's **`baseline <type>
<hexAddr>`**: snapshots *every* aligned value in the writable region
containing that address (refuses anything over ~200MB -- full-memory
"unknown initial value" scans aren't tractable with plain in-memory
collections), so a later `next decreased`/`next changed` can find a field
that changed without ever having known its starting value. `regionof
<hexAddr>` reports a region's bounds/size before committing to a baseline.
**`findhp <name> [watchSeconds]`** chains find-id -> scan -> the specific
structural filter confirmed below -> watch, atomically, for exactly this
kind of race-against-a-dying-target hunt.

### Confirmed against real game data (2026-09-04, updated 2026-09-10)

**Position + heading -- confirmed, reproducible, and live-enumerable via
`radar <any mob name> [durationSec=60]`, with no combat required from
you personally** (2026-09-10: bootstrapped and tracked 7 live mobs off a
zone-wide debug string, zero local action). Mobs live in a table of
fixed-stride (**0x90 / 144-byte**) records. Offsets, relative to a
record's base:

```
+0x00  i32   entity/spawn id (NOT the same counter as the "npc <name> <id>"
             debug string's id -- that theory was tried and disconfirmed;
             an id-based scan lands you in the right heap *neighborhood*
             via allocation-time locality, not on the exact record)
+0x04  i32   a per-tick/sequence counter, NOT a species id (see below)
+0x08  i32   shared flag (seen as 1)
+0x0C  f32   position.X
+0x10  f32   position.Y (height)
+0x14  f32   position.Z
+0x18  f32   heading/rotation
+0x1C  f32   shared constant across ALL records seen so far, any species
             (exact bits 0x3FB33333, prints as 1.4 -- scale/radius guess)
+0x20  f32   shared constant across ALL records seen so far, any species
             (exact bits 0x43340000, prints as 180)
+0x24  f32   position.X, secondary sample (tiny delta from +0x0C --
             interpolation/smoothing buffer, not a separate field)
+0x28  f32   position.Y, secondary sample
+0x2C  f32   position.Z, secondary sample
+0x30  f32   heading, secondary sample (exact duplicate of +0x18)
+0x34..0x6F  zero in every instance seen so far
+0x70        a UTF-16 string-fragment tail, identical across instances
             (likely stale/shared, not per-instance text)
+0x80  i64   pointer-shaped value, shared across DIFFERENT species (a
             manager/zone reference, NOT a species key -- see below)
```

No HP field anywhere in this record -- everything not listed above was
zero or shared.

**Mob names (2026-09-10, revised 2026-09-13 after the first approach was
disproven).** Names come from the `"npc <name> <id>"` debug strings,
matched to records by **exact id** -- nothing is inferred from the record
itself.

*Correction 1 (2026-09-13): the debug-string id IS the record's own
`+0x00` id.* An earlier note here claimed they were different counters.
That was wrong, and it was load-bearing -- it's why the first naming
attempt went looking for a species key instead of just matching ids.
Measured in one session: live record ids spanned 121203..122760 and the
string ids fell in the same band, matching exactly wherever both existed
(e.g. record `122750` <-> `"npc a cave sporeling 122750"`).

*Correction 2 (2026-09-13): `+0x80` is NOT a species key.* The earlier
claim rested on one sample where 90 of 103 entities shared a `+0x80`
value -- but that zone was ~90% one species, so the "clustering" was
mostly just the population. The `speciesfield` command now tests **every**
4- and 8-byte offset in the record against debug-string ground truth (a
real species key must be equal within a species and different between
species) and **nothing qualifies**: `+0x80` is *constant across different
species* (a shared manager/zone pointer), `+0x04` is the tick counter, and
`+0x08` is a 1/0 flag. Dereferencing `+0x80` for name text had already
failed two hops deep (hop 2 landed on runs of `0xFFFFFFFF`, a Dictionary
empty-bucket sentinel, not a Mono string). Propagating names by `+0x80`
labelled an entire zone "Decayed skeleton" -- confidently wrong, which is
worse than blank, so it was removed. **Species identity is not stored in
this record.**

*What ships*: a background sweep (every ~30s, off the UI thread, ~3s per
pass) harvests **every** `"npc <name> <id>"` string in the heap, not just
one species, into a process-lifetime id -> name map. Coverage in a real
zone went from ~13% (one typed species) to ~33%+ and keeps climbing as
strings appear, naming several distinct species at once. Two things
mattered for coverage:
- The `"... material N"` variants (`"npc a Velgarrith guard 1143 material 2"`)
  carry a perfectly good name and id -- the suffix is an asset qualifier.
  An earlier version discarded those lines outright, throwing away most of
  the available names, since they cover NPCs that have never been in
  combat (exactly the gap the combat-gated strings leave). They're now
  parsed after stripping the suffix.
- Those same lines must still never be parsed *naively*: anchored at the
  end, `"...guard 1143 material 2"` yields id=2, and a bootstrap scanning
  for an id of 2 drowns in junk hits and times out. That was a real
  intermittent-bootstrap-failure bug.

A mob with no debug string shows `(unknown species)` in `radar` / `id <n>`
on the `map` rather than a guess. Closing that last gap needs finding the
entity object itself (id -> object -> name), which is not done.

**`radar` and `map` no longer need a mob name.** With no argument they
auto-bootstrap: harvest every npc string, then try each id as an anchor,
highest first (live spawn ids run far above the low template ids). Naming
a species still works and just picks the anchor.

**Important structural correction from the first pass at this:** these
records are **packed value-type array elements, not individually-boxed
Mono objects** -- confirmed by the fact that a record's own preceding
bytes are just the tail of the *previous* record, not a 16-byte MonoObject
header (vtable pointer + sync block). That means **`vtable` does not apply
to them** -- there's no per-element vtable pointer to scan for, only the
array's own single header (if any) at its very start. `radar` instead
walks the array directly by fixed stride, using the two shared constants
above as a "this slot is a real record" signature.

**+0x04 is a tick/sequence counter, not a species id -- a real correction
made mid-session.** The initial theory (comparing two *different* fire
beetles at one moment and finding this field identical) was wrong. Once
`radar` could show multiple live records at once, it became clear: **each
live entity occupies 2-3 consecutive array slots** with the same id and
nearly-identical (very slightly interpolating) position, differing only in
this field, which decreases by exactly 2 between consecutive slots for the
same entity. That's a short history/interpolation ring buffer per entity,
not one slot per entity -- two *different* entities sampled at the same
moment naturally show the same current tick, which is what looked like a
"shared species id" from only two data points. `radar`'s live view already
dedupes by id, keeping only the highest-tick (freshest) sample per entity.

**Bootstrap is a two-stage process, and it's the fiddly part.** The
`"npc <name> <id>"` debug string only appears once *some* combat event has
fired for that specific instance (engaging, attacking, taking damage) --
searching for a mob you haven't touched yet, or one that died minutes ago
and had its string garbage-collected, comes back empty. Given a fresh id,
naively scanning for it as an exact i32 and hoping the hit *is* the record
does not work reliably (that id is a different counter from the record's
own +0x00 field). What does work: scan for the id anyway (it's rare enough
that this full-memory scan is fast -- seconds, not minutes), then treat
each raw hit as a **phase anchor** and check a small window of stride-
aligned slots around it (±200 slots, not the whole containing region) for
the two-constant signature; stop at the first one that matches. This
combination -- rare id for a fast global scan, bounded local window for
structural confirmation -- avoids two failure modes hit along the way:
a full-region walk per candidate (minutes, when tried across ~60
candidates on a several-hundred-MB region) and a full-memory scan for the
shared constants directly (also minutes -- `1.4f`'s exact bit pattern
turned out to be extremely common across a busy heap, unlike a specific
5-6 digit id, so enumerating *every* occurrence before filtering was the
expensive part). `radar` retries this whole bootstrap for up to 75
seconds. **The combat event doesn't have to be yours** -- the debug
string is zone-wide, so in a populated area someone else has usually
already fought that species recently and no action is needed at all
(confirmed 2026-09-10: bootstrapped and tracked live with zero local
combat). The 75s retry window exists for the case where it isn't there
yet and you (or the round-trip of telling you to attack something) need
the time.

**HP -- not found; one specific lead disconfirmed.** A *separate* record
(different region, different shape, also anchored by an exact id match)
was found with the shape `[id, 0, X, X, Y]` at the analogous +0x00/+0x04/
+0x08/+0x0C/+0x10 offsets -- e.g. `[90435, 0, 8.0, 8.0, 24.0]`, which reads
very naturally as `[id, ?, hp, hp-mirrored, maxHp]`. **This is now
disconfirmed**: the exact same `8.0 / 8.0 / 24.0` triple showed up for
three different species (a fire beetle, a spiderling, and a dunes scarab),
which real per-instance HP/maxHP would not do. It's some other shared
per-archetype constant, not health. `findhp` still encodes this shape as
its filter (it's a reasonable structural fingerprint to search *by*, even
though these particular offsets turned out to hold the wrong thing) --
whoever picks this up next should either find where the *actual* varying
number lives near this same record, or abandon this specific record
entirely and look elsewhere.

Two other confounds worth knowing before spending more time on this:
- **Corpse persistence**: watching this field across a kill (rather than
  mid-fight) is ambiguous -- a dead target's record may persist unchanged
  for some time (for looting?) rather than zeroing out immediately, so
  "value didn't change after death" doesn't cleanly rule a field out.
- **Fast/group kills race the discovery chain**: even the atomic `findhp`
  command takes a couple of seconds (string search + scan + filter) before
  the `watch` actually starts. A weak mob being hit by multiple
  players/pets at once can die within that window, before any hit lands
  while actually being observed. A tougher mob fought solo gives much more
  margin -- worth trying first next time.

### SOLVED 2026-09-14: live positions for every mob, NPC and player (`zonescan`)

**The fix was changing the search key, not the search.** Every structural
attempt -- "a record-shaped slot", "three in-range floats", "a rotation
quaternion" -- drowned, because a 3D game is wall-to-wall coordinate-shaped
bytes (the transform shape alone matched ~6500 objects where there were ~9
mobs). But the `"npc <name> <id>"` debug strings hand over the **exact ids
currently loaded**. A specific 6-digit id at offset 0, with an in-zone
position at `+0x0C`, is essentially never a coincidence -- and every hit
arrives with its name already attached.

**The discovery that unlocked it:** each id has *many* hits, and they are
not stale duplicates. Ordered by the `+0x04` tick they trace a smooth
continuous walk -- ticks step by 3, and one cave myconid was caught
descending a slope, Y falling 3.1 -> 1.9 over the trail. It is a **position
history ring**. Each slot is written once and then frozen, which is exactly
why every per-slot "did this value change?" liveness test kept answering
"static" and sent the previous investigation down a blind alley. **An
entity's current position is simply its highest-tick sample.**

Measured live: 45 named entities, 11 of them moving over 10s at sensible
distances (0.98u, 3.79u, 5.22u, 9.23u); the rest were idle NPCs, correctly
stationary. Player characters appear too, since they also get debug strings.

**Three bugs surfaced once it was running live** ("the mobs moved for a
minute then stopped"), all worth knowing:

1. **A torn struct across threads.** The zone was a 20-byte
   `PlayerFinder.Zone` field, written by the background re-discovery thread
   while the UI thread read it. That write is not atomic, so the reader
   could see a half-updated zone, reject every position as out of bounds,
   and return nothing -- whereupon the map's `if (mobs.Count > 0)` guard
   repainted the last good frame *forever*. Mobs moved for a minute, then
   froze at the first 20s re-discovery. Slots and zone are now one
   immutable record swapped by reference, which cannot tear.
2. **`material` lines carry TEMPLATE ids, not instance ids.** Parsing them
   is right for *names* (they cover NPCs that never fight) but wrong for
   discovery: template ids are small (1143, 1269) and occur as ordinary
   integers everywhere, blowing slot counts from 4.7k to 152k.
3. **The id set accumulated forever.** After a few minutes it held every
   mob that had ever spawned and died this session, and their stale samples
   still matched -- entity count climbed 57 -> 194 against ~50 real mobs.
   Discovery now uses only ids seen in the *latest* sweep, which
   self-cleans as dead mobs' strings disappear.

**A testing lesson from (1):** the first soak test ran without concurrent
re-discovery and reported a perfectly healthy tracker. The bug only exists
when a background sweep swaps state under the reader. Reproduce the real
concurrency, or the test proves nothing.

`MobTracker.DiscoverEntities` does the (slow, ~10s) full scan for known ids
and caches the slot addresses; `ReadEntities` then re-reads only those and
keeps each id's highest tick, which is fast enough to drive the 20Hz map.
The map re-discovers every 20s so spawns and despawns are picked up. Reads
are validated against the zone: a ring slot can be caught mid-write, which
showed up once as an entity apparently teleporting 425 units to z=0.

### How that was missed for so long: the array is a snapshot (2026-09-13)

**This invalidates the premise of the mob map.** Jay reported "not showing
all mobs, and the mobs are not moving". Both are real, and the cause is
that the 0x90 record array everything is built on is a **frozen
snapshot**:

- the `+0x04` tick is stuck (2833480 across repeated reads, never advances)
- 0 of 31 records changed position over 1.5s (max delta `0.00`)

So `map` and `radar` show **spawn points**. Separately, the `1.4`/`180`
signature is **not universal** -- real `+0x1C` values include `1.4`, `0`,
`5.051`, `4.992`, `3.051`, `1.26`, `1.54` -- so it was independently
hiding about half the records. `arraydiag` re-checks both claims.

**The liveness test worth remembering: ask whether `+0x04` ADVANCES**, not
whether it changed. "Changed" matches every mutating byte in an 11GB
process -- the first attempt returned garbage like `-2147483648 -> 0`.
Require a small *positive* delta on a large-magnitude counter. This needs
nothing to move, so it works in a zone full of idle NPCs.

**Live data exists and is trackable, but isn't cleanly separable from
scenery yet:**
- `mobhunt` (you stand still; diff two full snapshots of every in-zone
  float triple by address) found 15,826 moving addresses forming 1279
  distinct moving positions, and one address tracked a mob smoothly for
  49 units across 92 samples.
- Dumping one revealed the layout: a **Unity-style Transform** -- a
  normalised quaternion (four floats whose squares sum to 1.000)
  immediately followed by the position triple. `PlayerFinder.ScanTransforms`
  matches that shape, which needs no movement and so finds idle entities.
- **But it over-matches**: ~6500 "entities" where there are ~9 mobs, since
  scenery, props and animation bones all have transforms.
  `MobTracker.SweepTransforms` drops recycled-to-origin and teleporting
  candidates but cannot tell a creature from a rock.

**Current behaviour:** the map keeps using the snapshot (stale, but a
clean list with real ids and names) and only switches to the transform
source when it returns a plausible count. A stale-but-correct map beats a
live cloud of noise.

**Next step** is finding the real entity list by pointer-chasing from a
game manager object -- a bigger job than heuristics on raw memory.

### Players on the map -- SOLVED 2026-09-14 (`client <name> <id>`)

Player characters were absent from the map for a long time, and the working
theory was that they lived in some separate structure -- `scanall` kept
returning **0 unnamed** entities, which seemed to prove PCs weren't in the
mob record array (if they were, they'd show up unnamed).

**The theory was wrong, and the real cause was embarrassingly narrow: only
the `"npc "` prefix was ever searched for.** Players get their own debug
string with the same shape but a different verb:

```
npc a cave sporeling 122750     <- a mob or NPC
client Veer 171803              <- a PLAYER
```

Same format, same id range (mobs 169xxx-172xxx, players 168xxx-172xxx),
same position-history machinery. Harvesting both prefixes made players
appear immediately, with names and live coordinates, alongside the mobs.

**How it was found:** Jay mentioned `/who` lists everyone in the zone. That
turned out to be a red herring for positions -- the `/who` roster is just an
array of name strings with no spatial data -- but searching for a *nearby*
player's name surfaced `"client Veer 171803 material 0"` in the results,
which was the actual answer. Testing a distant player first would have
proved nothing: `/who` covers the whole zone (78 players), almost all of
them unloaded.

**The lesson worth keeping:** "0 unnamed entities" was read as evidence
about where players are stored, when it was really evidence about what the
harvester was looking for. A filter's blind spot looks exactly like an
absence in the data.

Players are drawn green with a white ring and their real name;
`MobTracker.IsPlayerId` distinguishes them.

### Finding the player -- SOLVED 2026-09-13 (`findme`)

**Found on the fourth approach.** The `findme` command locates the
player's live position field, and `map` now runs the same search in the
background to upgrade its "you" dot from the centroid approximation to
your real position.

**Why this one worked when three didn't:** it had ground truth. Every
mob's world position is already known exactly, which pins down where the
player must be standing, so the search knows what a correct answer looks
like. The earlier attempts were all searching blind.

The method, in three ideas:
1. **A position is three consecutive in-range floats.** Filter all of
   writable memory for triples that form a plausible coordinate near the
   mobs.
2. **Your position is replicated, mesh data is not.** Raw matches are
   useless -- a 3D game is full of coordinate-shaped floats (the first run
   returned **37.5 million**). But the player's position is copied across
   many subsystems (render transform, physics, camera, network
   interpolation), so bucketing matches by rounded world position makes it
   pile up at one point. Counting per bucket, rather than keeping matches,
   also bounds memory by cell count instead of match count.
3. **The player moves while being nowhere near any mob.** Mob render and
   physics copies move too, so "it moves" is not enough. But every mob's
   exact position is known, so they can be subtracted out. What remains --
   moving continuously, no teleports, and not sitting on any tracked mob --
   is you.

Confirmed twice on live data: **849 and 618 addresses** independently
agreeing on one moving position, with steps of ~1 unit per 100ms (walking
pace) and no jumps, from the same memory neighbourhoods both times. The
reported coordinate differed between runs because the player had moved.

**Two float bugs nearly sank this, both worth remembering:**
- **NaN passes a naive range check.** Written as
  `if (x < min || x > max) continue;`, a NaN takes *neither* branch --
  every comparison with NaN is false -- so it sails through. 18.7 million
  NaN "positions" ranked top, then poisoned the next stage into a box of
  `NaN..NaN`, which *everything* passes, and the scan collapsed to 1MB/s.
  Every test is now a POSITIVE "must be within" predicate, which rejects
  NaN and infinity for free.
- **A min/max box built from mob positions spans the origin**, and
  zero-filled memory is everywhere, so `(0,0,0)` matched endlessly. A
  sphere around the mob centroid excludes it.

Fixing both cut stage 1 from 59s to **6s** and from 37.5M hits to 1.87M.

**Caveat: it requires you to be walking.** A stationary player is
indistinguishable from static data, which is the one thing this method
fundamentally cannot work around.

**For the record, the three approaches that failed (2026-09-10)**, all
before the mob array could supply ground truth:
1. Reverse-pointer-scan from a mob's bare (non-debug-string) name --
   confirmed such names DO get referenced (real pointers found), but
   dumping around the pointer site showed an unrelated structure, not the
   known position-record shape.
2. Same technique against the player's OWN name (`Suntarine`) -- found via
   `CharacterDetector.Detect()`, which MemScan can call directly (no need
   to ask the player). ~1900 occurrences total (name gets cached in every
   UI element); the one isolated (non-clustered) hit had 7 pointers to it,
   none of which led to the known record shape either.
3. `baselinenear` (new command, added this session for this search --
   `baseline` scoped to a byte window around an address instead of the
   whole containing region, since the entity array's region had grown to
   16MB, too big to baseline whole) + `nextrange` (new command: filter
   candidates to a plausible value range) + watching the player walk, in
   increasingly careful variants (single "changed" pass; range-filtered;
   a two-phase walk-forward-then-reverse to demand TWO independent
   movement-correlated changes). All three variants converged on the exact
   same ~12-900 candidates in a regular 32-128-byte-stride pattern that
   collapses to `0` -- some other fast-mutating subsystem (not
   player-related) dominates this specific 16MB region and swamps any real
   signal. This rules out that region for this technique; a different
   anchor entirely (not derived from the mob array) is needed next.

Also ruled out: the low, stable entity ids in the mob array (284, 1033,
1034, 1087, 1100 -- present in every `radar`/`map` session this whole
time) are NOT the player -- their positions never changed even while the
player was actively walking during the tests above. Probably static
world fixtures, not characters.

**Running it** (same elevation note as the sniffer -- try unelevated
first):

```
cd src\MemScan
dotnet run
```

It waits for `mnm.exe` (or attach to an exact `--pid`), then drops into a
prompt -- type `help` for the full command list. This has to be driven live
while playing (get near a specific mob, note its exact name, run `find`,
then go take an action in-game and come back to `watch`/`next`) -- there's
no way to do this offline the way the sniffer's captured pcap can be
inspected later, since the memory addresses you're hunting only mean
anything while the process is actually running.

## Running it

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or
newer -- the .NET 10 SDK is fine, the app just needs the .NET 8 Windows
Desktop runtime present, which the SDK installs). The first build needs
internet access once for NuGet; after that it's cached.

### Desktop icon (the normal way)

Run this once from PowerShell in the repo root:

```
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It builds the app into `dist\` and puts a **"M&M Damage Parser"** shortcut
on your Desktop that launches `dist\MnmDamageParser.exe` **as
Administrator** (the parser needs elevation to read the game's memory).
Double-click it, approve the UAC prompt, and the meter opens -- it waits
for `mnm.exe` if the game isn't up yet.

Re-run `install.ps1` after a **code** change to refresh `dist\`. A
**config** change (`config\settings.json` / `config\offsets.json`) needs
no rebuild -- the shortcut-launched exe reads the repo's `config\` folder
directly; just edit and relaunch. The startup console prints
`[main] config + logs directory: ...` so you can always see which folder
is live.

(If Windows ever forgets the elevation: right-click the shortcut ->
Properties -> Shortcut -> Advanced -> "Run as administrator".)

### From a terminal (dev)

From an **Administrator** terminal:

```
cd src\App
dotnet run
```

Or open `MnmDamageParser.sln` in Visual Studio and run the `App` project.

## What's been verified where

Everything in `src/Core` and `tests/CoreTests` is built and run on every
change in the dev sandbox this was authored in (Linux, with a
locally-installed .NET 8 SDK, no NuGet access) -- run `cd tests/CoreTests
&& dotnet run -c Release` any time to re-check after an edit; it should
say `ALL TESTS PASSED` with 0 failures. That sandbox has no Windows
process to attach to and can't build `src/App` at all (`net8.0-windows`/
`UseWindowsForms` needs the `Microsoft.WindowsDesktop.App.Ref` NuGet
package, which its network access doesn't reach), so `src/App` has only
ever been built and run on Jay's real machine -- confirmed working there
(compiles clean, attaches to `mnm.exe`, populates the GUI with real
combat data).
