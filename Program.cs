using MnmDamageParser.Core;
using MnmDamageParser.Core.CombatLog;
using MnmDamageParser.Core.Config;
using MnmDamageParser.Core.Memory;
using MnmDamageParser.Core.Parser;

namespace MnmDamageParser.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // `MnmDamageParser.exe --ui-preview` opens the window with a few
        // synthetic encounters and no game attach -- for eyeballing layout
        // changes without launching M&M (and without Administrator).
        if (args.Contains("--ui-preview"))
        {
            RunUiPreview(openDetail: args.Contains("--detail"));
            return;
        }

        // `MnmDamageParser.exe --chat-test [seconds]` -- attach, run the
        // watcher headless, and print what the Chat window would show
        // (ChatLine) next to the combat/unmatched split, so the chat_anchors
        // config can be sanity-checked against the live game from a console.
        if (args.Contains("--chat-test"))
        {
            var secs = args.SkipWhile(a => a != "--chat-test").Skip(1).FirstOrDefault();
            RunChatTest(int.TryParse(secs, out var s) ? s : 30);
            return;
        }

        // `MnmDamageParser.exe --voice-test` -- speak one line (blocking)
        // and exit, to confirm Windows TTS works on this machine.
        if (args.Contains("--voice-test"))
        {
            var vs = new MnmDamageParser.Core.Config.VoiceSettings
            {
                AnnounceHits = true, AnnounceMisses = true, AnnounceResists = true,
                AnnounceRecords = true, AnnounceAttackers = true, AnnounceKills = true,
                AnnounceEngage = true, AnnounceDisengage = true,
                VoiceName = "Zira", VoicePitch = "",
            };
            using var v = new VoiceAnnouncer(vs);
            Console.WriteLine("[voice-test] A/B pitch check -- listen for the second line sitting higher...");
            var err = v.SpeakBlocking("This is the plain voice, no pitch shift.");
            Console.WriteLine(err is null ? "[voice-test] plain OK." : "[voice-test] FAILED: " + err);
            vs.VoicePitch = "+6"; v.Apply(vs);
            v.SpeakBlocking("And this is the elf pitch, plus six. A young crypt scarab down.");
            vs.VoicePitch = ""; v.Apply(vs);

            // Run a few fake combat events through the real announcer path.
            Console.WriteLine("[voice-test] simulating combat -> engage, your hit, incoming attacker, miss, record, kill...");
            var ms = new MeterState(idleTimeoutSeconds: 9999);
            ms.EventIngested += v.OnCombatEvent;
            ms.RecordBroken += v.OnRecord;
            ms.EncounterOpened += v.OnEncounterOpened;
            CombatEvent Mine(int amt, EventType t) => new()
            { Timestamp = Clock.NowSeconds(), Source = "You", Target = "a rat", Amount = amt, EventType = t, Ability = "Blast of Sleet", School = "Cold" };
            CombatEvent Incoming(string mob) => new()
            { Timestamp = Clock.NowSeconds(), Source = mob, Target = "You", Amount = 4, EventType = EventType.Hit, Ability = "bites" };
            ms.Ingest(new List<CombatEvent> { new()
            { Timestamp = Clock.NowSeconds(), Source = "You", Target = "a rat", Amount = 0, EventType = EventType.Engage } });
            Thread.Sleep(2500);
            ms.Ingest(new List<CombatEvent> { Mine(20, EventType.Hit) }); // should NOT re-say "engaging" (dedup)
            Thread.Sleep(3000);
            ms.Ingest(new List<CombatEvent> { Incoming("a rotting skeleton") }); // "a rotting skeleton"
            Thread.Sleep(3000);
            ms.Ingest(new List<CombatEvent> { Incoming("a rotting skeleton") }); // silent -- already known
            ms.Ingest(new List<CombatEvent> { Mine(0, EventType.Miss) });
            Thread.Sleep(3000);
            ms.Ingest(new List<CombatEvent> { Mine(99, EventType.Hit) }); // new record
            Thread.Sleep(4000);
            ms.Ingest(new List<CombatEvent> { new()
            { Timestamp = Clock.NowSeconds(), Source = "You", Target = "a rotting skeleton", Amount = 0, EventType = EventType.Kill } });
            Thread.Sleep(1000);
            ms.Ingest(new List<CombatEvent> { new()
            { Timestamp = Clock.NowSeconds(), Source = "You", Target = "a rotting skeleton", Amount = 0, EventType = EventType.Disengage } }); // silent -- kill just fired
            Thread.Sleep(2500);
            // a standalone disengage (fled a fight) -> "combat over"
            var ms2evt = new CombatEvent { Timestamp = Clock.NowSeconds(), Source = "You", Target = "a bat", Amount = 3, EventType = EventType.Hit, Ability = "hits" };
            ms.Ingest(new List<CombatEvent> { ms2evt });
            Thread.Sleep(2500);
            ms.Ingest(new List<CombatEvent> { new()
            { Timestamp = Clock.NowSeconds(), Source = "You", Target = "a bat", Amount = 0, EventType = EventType.Disengage } });
            Thread.Sleep(3000);
            return;
        }

        var root = FindRepoRoot();
        Console.WriteLine($"[main] config + logs directory: {root}");
        var settings = Settings.Load(Path.Combine(root, "config", "settings.json"));
        var offsets = OffsetsConfig.Load(Path.Combine(root, "config", "offsets.json"));
        Console.WriteLine(
            $"[main] offsets.json: {offsets.TextLog.LinePatterns.Count} line pattern(s), " +
            $"debug_print_unmatched_lines={offsets.TextLog.Buffer.DebugPrintUnmatchedLines}");

        var mh = new Win32MemoryReader();
        Console.WriteLine($"[main] waiting for '{settings.ProcessName}'... (launch the game if it isn't running)");
        mh.Attach(settings.ProcessName, wait: true);
        Console.WriteLine($"[main] attached to {settings.ProcessName}");

        // Unconditional self-test (NOT gated by debug_print_scan_stats,
        // since that flag's own effect is one of the things being
        // diagnosed) -- proves whether OpenProcess/VirtualQueryEx/
        // ReadProcessMemory are actually working at all before the poll
        // loop even starts.
        try
        {
            var regions = mh.EnumRegions(writableOnly: true).ToList();
            var readableCount = 0;
            long totalBytes = 0;
            foreach (var r in regions.Take(50))
            {
                var data = mh.ReadBytes(r.Base, (int)Math.Min(r.Size, 4096));
                if (data is { Length: > 0 }) readableCount++;
            }
            foreach (var r in regions) totalBytes += r.Size;
            Console.WriteLine(
                $"[main] self-test: enumerated {regions.Count} writable region(s), " +
                $"{totalBytes / (1024.0 * 1024.0):F1} MB total; " +
                $"{readableCount}/{Math.Min(regions.Count, 50)} of the first 50 regions read successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[main] self-test FAILED: {ex}");
        }

        var watcher = new TextLogWatcher(mh, offsets.TextLog.Buffer, offsets.TextLog.LinePatterns);
        Console.WriteLine(
            $"[main] watcher configured: debug_print_scan_stats={watcher.DebugPrintScanStats}, " +
            $"anchors=[{string.Join(", ", offsets.TextLog.Buffer.Anchors)}], " +
            $"{offsets.TextLog.LinePatterns.Count} line pattern(s) loaded");

        // Non-combat chat feed for the "Chat" windows. Dormant (no extra
        // scanning, no window) when config/offsets.json's buffer.chat_anchors
        // is empty. config/chat.json splits it into per-category windows.
        var chatCategorizer = new ChatCategorizer(ChatConfig.Load(Path.Combine(root, "config", "chat.json")));
        var chatEnabled = offsets.TextLog.Buffer.ChatAnchors.Count > 0;
        var chatLog = chatEnabled ? new ChatLog(chatCategorizer) : null;
        if (chatLog is not null)
        {
            watcher.ChatLine += chatLog.Add;
            Console.WriteLine(
                $"[main] chat window enabled: chat_anchors=[{string.Join(", ", offsets.TextLog.Buffer.ChatAnchors)}], " +
                (chatCategorizer.IsSplit
                    ? $"categories=[{string.Join(", ", chatCategorizer.CategoryNames)}]"
                    : "single combined window (config/chat.json has no categories)"));
        }

        // Which character's logs/history to write. Priority:
        //   1. character_name in settings.json, if set -- an explicit
        //      manual override (also lets you run this on a machine that
        //      isn't the game's).
        //   2. auto-detected from the game's own on-disk data (Player.log
        //      + the per-character folders under LocalLow) -- see
        //      CharacterDetector. This is the normal path: just launch.
        //   3. null -> unsegmented (logs/history.json, event_log_path
        //      as-is), same as before this was automatic.
        var manualCharacter = SanitizeForFileName(settings.CharacterName);
        var characterName = manualCharacter;
        var characterSource = characterName is null ? "" : "settings.json";
        if (characterName is null)
        {
            characterName = SanitizeForFileName(CharacterDetector.Detect());
            if (characterName is not null) characterSource = "auto-detected";
        }

        var settingsPath = Path.Combine(root, "config", "settings.json");
        var spellConfig = MnmDamageParser.Core.Config.SpellConfig.Load(Path.Combine(root, "config", "spells.json"));
        Console.WriteLine($"[main] spells.json: {spellConfig.Tracked.Count} DoT/debuff timer(s) configured" +
            (spellConfig.Tracked.Count > 0 ? " (" + string.Join(", ", spellConfig.Tracked.Keys) + ")" : ""));
        var xpConfigPath = Path.Combine(root, "config", "xpbar.json");
        var xpConfig = MnmDamageParser.Core.Xp.XpBarConfig.Load(xpConfigPath);
        if (xpConfig.IsCalibrated)
        {
            double dr = xpConfig.FillRgb[0] - xpConfig.EmptyRgb[0],
                   dg = xpConfig.FillRgb[1] - xpConfig.EmptyRgb[1],
                   db = xpConfig.FillRgb[2] - xpConfig.EmptyRgb[2];
            var contrast = Math.Sqrt(dr * dr + dg * dg + db * db);
            Console.WriteLine(
                $"[main] XP bar: calibrated ({xpConfig.Region.Width}x{xpConfig.Region.Height} @ " +
                $"{xpConfig.Region.Left},{xpConfig.Region.Top}), colour contrast {contrast:0}" +
                (contrast < 12 ? " -- TOO LOW, recalibrate (cover filled + empty bar)" : "") +
                $", sampling every {xpConfig.IntervalSeconds:0.0}s x{xpConfig.SamplesPerRead}");
        }
        else
        {
            Console.WriteLine("[main] XP bar: not calibrated -- use the XP button's Calibrate");
        }
        var (eventLogPath, historyPath, recordsPath, totalsPath) = CharacterPathsFor(root, settings, characterName);

        // With debug_print_unmatched_lines on, also append every
        // anchor-carrying line that matched NO pattern to
        // logs/unmatched_<char>.log -- run a session, then send that file
        // to see exactly what combat text (crits, DoT ticks, dodges, your
        // own misses, ...) the parser is still blind to.
        if (offsets.TextLog.Buffer.DebugPrintUnmatchedLines)
        {
            var unmatchedPath = Path.Combine(root, "logs",
                characterName is null ? "unmatched.log" : $"unmatched_{characterName}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(unmatchedPath)!);
            var unmatchedLock = new object();
            watcher.UnmatchedLine += line =>
            {
                try
                {
                    lock (unmatchedLock)
                        File.AppendAllText(unmatchedPath, $"{DateTimeOffset.Now:HH:mm:ss}  {line}{Environment.NewLine}");
                }
                catch { /* diagnostics must never crash the meter */ }
            };
            Console.WriteLine($"[main] logging unmatched combat lines to {unmatchedPath}");
        }

        // The in-world clock is server-wide, not per character.
        var gameTimePath = Path.Combine(root, "logs", "gametime.json");

        var meterState = new MeterState(
            idleTimeoutSeconds: settings.EncounterIdleTimeoutSeconds,
            rollingWindowSeconds: settings.RollingWindowSeconds,
            eventLogPath: eventLogPath,
            maxHistory: settings.MaxEncounterHistory,
            historyPath: historyPath,
            characterName: characterName,
            recordsPath: recordsPath,
            totalsPath: totalsPath,
            spells: spellConfig,
            gameTimePath: gameTimePath);
        Console.WriteLine(characterName is null
            ? "[main] character not set and not auto-detected -- history/log are not segmented by character"
            : $"[main] character: {characterName} ({characterSource}) (history: {historyPath})");

        var voice = new VoiceAnnouncer(settings.Voice);
        voice.SetPetNames(settings.PetNames);
        meterState.EventIngested += voice.OnCombatEvent;
        meterState.RecordBroken += voice.OnRecord;
        meterState.EncounterOpened += voice.OnEncounterOpened;
        Console.WriteLine($"[main] voice: name='{settings.Voice.VoiceName}' pitch='{settings.Voice.VoicePitch}' rate={settings.Voice.Rate}");
        Console.WriteLine($"[main] voice callouts: engage={settings.Voice.AnnounceEngage}, " +
            $"disengage={settings.Voice.AnnounceDisengage}, hits={settings.Voice.AnnounceHits}, " +
            $"misses={settings.Voice.AnnounceMisses}, resists={settings.Voice.AnnounceResists}, " +
            $"attackers={settings.Voice.AnnounceAttackers}, kills={settings.Voice.AnnounceKills}, " +
            $"records={settings.Voice.AnnounceRecords}, levelups={settings.Voice.AnnounceLevelUps} " +
            $"-- toggle in the 'Voice' menu");

        var stopFlag = new CancellationTokenSource();
        var pollIntervalMs = (int)(settings.PollIntervalSeconds * 1000);
        var worker = new Thread(() => PollLoop(mh, watcher, meterState, pollIntervalMs, stopFlag.Token))
        {
            IsBackground = true,
            Name = "PollLoop",
        };
        worker.Start();

        // Mid-session relog handling: while character_name is NOT a manual
        // override, keep re-checking the current character and repoint the
        // event log / encounter history when it changes (e.g. you camp and
        // log back in as an alt). Disabled by character_recheck_seconds=0.
        if (manualCharacter is null && settings.CharacterRecheckSeconds > 0)
        {
            var charWatch = new Thread(() => CharacterWatchLoop(
                meterState, characterName, settings.CharacterRecheckSeconds, root, settings, stopFlag.Token))
            {
                IsBackground = true,
                Name = "CharacterWatch",
            };
            charWatch.Start();
        }

        try
        {
            Application.Run(new MainForm(meterState, settings, voice, settingsPath, characterName, xpConfig, xpConfigPath, chatLog, chatCategorizer));
        }
        finally
        {
            stopFlag.Cancel();
            voice.Dispose();
            meterState.Dispose();
            mh.Detach();
        }
    }

    /// <summary>Headless check of the chat feed: attach, poll the real
    /// watcher for <paramref name="seconds"/>, and print every ChatLine
    /// alongside a running combat/unmatched tally. No GUI, no MeterState --
    /// just "does chat_anchors surface the right lines and keep combat
    /// out?". Needs the game running (and, like the meter, elevation to
    /// read its memory).</summary>
    private static void RunChatTest(int seconds)
    {
        var root = FindRepoRoot();
        var offsets = OffsetsConfig.Load(Path.Combine(root, "config", "offsets.json"));
        var categorizer = new ChatCategorizer(ChatConfig.Load(Path.Combine(root, "config", "chat.json")));
        Console.WriteLine($"[chat-test] chat_anchors=[{string.Join(", ", offsets.TextLog.Buffer.ChatAnchors)}], " +
            $"categories=[{string.Join(", ", categorizer.CategoryNames)}], running {seconds}s");

        var mh = new Win32MemoryReader();
        var settings = Settings.Load(Path.Combine(root, "config", "settings.json"));
        Console.WriteLine($"[chat-test] waiting for '{settings.ProcessName}'...");
        mh.Attach(settings.ProcessName, wait: true);
        Console.WriteLine("[chat-test] attached.");

        var watcher = new TextLogWatcher(mh, offsets.TextLog.Buffer, offsets.TextLog.LinePatterns);
        var chatLog = new ChatLog(categorizer);
        var rawCount = 0;
        var unmatchedCount = 0;
        var byCategory = new Dictionary<string, int>();
        watcher.ChatLine += line => { rawCount++; chatLog.Add(line); };
        watcher.UnmatchedLine += _ => unmatchedCount++;

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var combatCount = 0;
        var shown = 0;
        while (DateTime.UtcNow < deadline)
        {
            combatCount += watcher.Poll().Count;
            var snap = chatLog.Snapshot();
            for (; shown < snap.Count; shown++)
            {
                var e = snap[shown];
                byCategory[e.Category] = byCategory.GetValueOrDefault(e.Category) + 1;
                Console.WriteLine($"  [{e.Category,-8}{(e.ColorHex is { } c ? " #" + c : "")}] {e.Text}");
            }
            Thread.Sleep((int)(settings.PollIntervalSeconds * 1000));
        }
        Console.WriteLine(
            $"[chat-test] done: {chatLog.TotalAdded} kept / {rawCount} raw chat line(s), " +
            $"{combatCount} combat event(s), {unmatchedCount} unmatched combat line(s)");
        Console.WriteLine("[chat-test] by category: " +
            string.Join(", ", byCategory.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        mh.Detach();
    }

    private static void RunUiPreview(bool openDetail)
    {
        var previewSpells = new MnmDamageParser.Core.Config.SpellConfig
        {
            Spells =
            {
                new() { Name = "Minor Frostbite", DurationSeconds = 60, TickSeconds = 6, Kind = "dot" },
                new() { Name = "Withering Curse", DurationSeconds = 90, TickSeconds = 6, Kind = "debuff" },
                new() { Name = "Creeping Rot", DurationSeconds = 30, TickSeconds = 6, Kind = "dot" },
            },
        };
        var ms = new MeterState(idleTimeoutSeconds: 9999, characterName: "Radust", spells: previewSpells);
        var now = Clock.NowSeconds();

        CombatEvent E(double t, string src, string tgt, int amt, EventType type, string? ability, string? school = null) => new()
        {
            Timestamp = t, Source = src, Target = tgt, Amount = amt, EventType = type, Ability = ability, School = school,
        };

        // Encounter #1 (oldest): solo rats, closed by a kill.
        var b = now - 900;
        var e1 = new List<CombatEvent>();
        for (var i = 0; i < 18; i++)
        {
            e1.Add(E(b + i * 2, "You", "a giant rat", 70 + i, i % 5 == 0 ? EventType.Crit : EventType.Hit, "Blast of Sleet", "Cold"));
            if (i % 3 == 0) e1.Add(E(b + i * 2 + 0.4, "You", "a giant rat", 0, EventType.Miss, "Blast of Sleet"));
        }
        e1.Add(E(b + 40, "You", "a giant rat", 0, EventType.Kill, null));
        ms.Ingest(e1);

        // Encounter #2 (middle): a quick solo bear, closed.
        b = now - 500;
        var e2 = new List<CombatEvent>();
        for (var i = 0; i < 22; i++)
        {
            e2.Add(E(b + i * 1.6, "You", "a black bear", 95 + i % 30, i % 6 == 0 ? EventType.Crit : EventType.Hit, "Blast of Sleet", "Cold"));
            e2.Add(E(b + i * 1.6 + 0.3, "You", "a black bear", 18 + i % 8, EventType.Hit, "Minor Frostbite", "Cold"));
        }
        e2.Add(E(b + 40, "You", "a black bear", 0, EventType.Kill, null));
        ms.Ingest(e2);

        // Encounter #3 (newest, still LIVE): a group fight -- You, Barrowin,
        // Feyd on a cave bear, with a healer and the bear hitting back.
        b = now - 70;
        var e3 = new List<CombatEvent>();
        for (var i = 0; i < 42; i++)
        {
            e3.Add(E(b + i * 1.5, "You", "a cave bear", 90 + i % 40, i % 6 == 0 ? EventType.Crit : EventType.Hit, "Blast of Sleet", "Cold"));
            e3.Add(E(b + i * 1.5 + 0.3, "You", "a cave bear", 20 + i % 10, EventType.Hit, "Minor Frostbite", "Cold"));
            if (i % 2 == 0) e3.Add(E(b + i * 1.5 + 0.5, "Barrowin", "a cave bear", 150 + i, i % 8 == 0 ? EventType.Crit : EventType.Hit, "Crushing Blow", "Physical"));
            if (i % 3 == 0) e3.Add(E(b + i * 1.5 + 0.6, "Feyd", "a cave bear", 70 + i, EventType.Hit, "Backstab", "Physical"));
            e3.Add(E(b + i * 1.5 + 0.65, "Rebel", "a cave bear", 30 + i % 15, EventType.Hit, "bites"));
            if (i % 4 == 0) e3.Add(E(b + i * 1.5 + 0.7, "a cave bear", "You", 40 + i % 25, EventType.Hit, null));
            if (i % 5 == 0) e3.Add(E(b + i * 1.5 + 0.8, "Eldara", "You", 110 + i, EventType.Heal, "Mending Light"));
        }
        ms.Ingest(e3);

        // A /time reading a couple minutes back, so the status-bar clock shows.
        ms.Ingest(new List<CombatEvent>
        {
            new() { Timestamp = now - 120, Source = "World", Target = "?", Amount = 20 * 60 + 15, EventType = EventType.WorldTime },
            new() { Timestamp = now - 120, Source = "World", Target = "?", Amount = -1, EventType = EventType.WorldTime,
                    Ability = "Tilusten, the 4th of Harvesttide in the year 608 After the Reformation. It is Autumn" },
        });

        // Recent DoT/debuff ticks so the Timers window has something to show.
        var dots = new List<CombatEvent>();
        for (double tt = now - 12; tt <= now; tt += 6)              // Frostbite on the bear: ~48s left (green)
            dots.Add(E(tt, "You", "a cave bear", 2, EventType.Hit, "Minor Frostbite", "Cold"));
        for (double tt = now - 30; tt <= now; tt += 6)              // Curse on the bear: ~60s left (green)
            dots.Add(E(tt, "You", "a cave bear", 5, EventType.Hit, "Withering Curse", null));
        for (double tt = now - 16; tt <= now - 4; tt += 6)          // Rot on the snake: ~10s left (amber/red)
            dots.Add(E(tt, "You", "a rattlesnake", 3, EventType.Hit, "Creeping Rot", null));
        ms.Ingest(dots.OrderBy(x => x.Timestamp).ToList());

        var settings = new Settings
        {
            Window = new WindowSettings { Width = 1040, Height = 640, RefreshMs = 500, PlayerName = "You" },
            PetNames = new List<string> { "Rebel" },
        };
        settings.ShowXpPanel = true;
        var xpCfg = new MnmDamageParser.Core.Xp.XpBarConfig
        {
            Region = new MnmDamageParser.Core.Xp.ScreenRect { Left = 1720, Top = 150, Width = 172, Height = 21 },
            FillRgb = new[] { 163, 135, 42 }, EmptyRgb = new[] { 48, 48, 48 },
        };

        // A few real-shaped chat lines so the "Chat" windows are live in the
        // preview (raw form -- ChatLog.Add strips + filters + categorizes).
        ChatConfig previewChatConfig;
        try { previewChatConfig = ChatConfig.Load(Path.Combine(FindRepoRoot(), "config", "chat.json")); }
        catch { previewChatConfig = new ChatConfig(); }
        var chatCategorizer = new ChatCategorizer(previewChatConfig);
        var chatLog = new ChatLog(chatCategorizer);
        foreach (var raw in new[]
        {
            "<color=#009CFF>Moonspork says out of character, \"Feels like a tutorial zone\"</color>",
            "<color=#009CFF>Clendall says out of character, \"theres no vendor that takes my trash\"</color>",
            "<color=#FF0000>Uus shouts, \"you wouldnt DARE\"</color>",
            "<color=#7F0000>a cave sporeling has been slain by Nyxsus!</color>",
            "<color=#7F0000>Sarit has come back to life.</color>",
            "<color=#5A5AFF>--You loot [Damaged Fungus Cap] from a cave sporeling's corpse.--</color>",
            "<color=#5A5AFF>Your skill in Evocation has increased! (11)</color>",
            "<color=#7900FF>a small beetle is chilled to the bone.</color>",
            "<color=#FFFFFF>Vorh says, \"Hail, an aspirant.\"</color>",
            "<color=#16F396>Mwuahaha is encircled by a magical shield.</color>",
            "<color=#FFFF00>You begin casting Life Drain.</color>",
            "<color=#FFFF00>Your spell fizzles!</color>",
            "<color=#007F00>Innuloki's strength fades.</color>",
            "<color=#FFFF00>You have entered Ail'vorith.</color>",
        }) chatLog.Add(raw);

        var main = new MainForm(ms, settings, voice: null, settingsPath: null, characterName: "Radust",
            xpConfig: xpCfg, xpConfigPath: null, chatLog: chatLog, chatCategorizer: chatCategorizer);
        if (openDetail)
        {
            main.Shown += (_, _) =>
            {
                var detail = new EncounterDetailForm(ms, 3, "Barrowin", 500, "You");
                detail.StartPosition = FormStartPosition.Manual;
                detail.Location = new Point(main.Left + 60, main.Top + 60);
                detail.Show(main);

                var records = new RecordsForm(ms, 500) { StartPosition = FormStartPosition.Manual };
                records.Location = new Point(main.Left + 120, main.Top + 120);
                records.Show(main);

                var pets = new PetWindow(ms, new[] { "Rebel" }, 500) { Location = new Point(main.Right + 8, main.Top) };
                pets.Show(main);

                var totals = new TotalsForm(ms, "You", new[] { "Rebel" }, 500) { StartPosition = FormStartPosition.Manual };
                totals.Location = new Point(main.Left + 180, main.Top + 180);
                totals.Show(main);
            };
        }
        Application.Run(main);
        ms.Dispose();
    }

    /// <summary>The event log, encounter history and personal-records
    /// paths for a given character (null character = unsegmented). Used
    /// both at startup and by the mid-session relog watcher.</summary>
    private static (string? EventLogPath, string HistoryPath, string RecordsPath, string TotalsPath) CharacterPathsFor(
        string root, Settings settings, string? characterName)
    {
        string? eventLogPath = null;
        if (settings.LogEventsToFile)
        {
            eventLogPath = characterName is null
                ? Path.Combine(root, settings.EventLogPath)
                : Path.Combine(root, "logs", $"combat_events_{characterName}.jsonl");
        }
        var historyPath = Path.Combine(root, "logs", characterName is null ? "history.json" : $"history_{characterName}.json");
        var recordsPath = Path.Combine(root, "logs", characterName is null ? "records.json" : $"records_{characterName}.json");
        var totalsPath = Path.Combine(root, "logs", characterName is null ? "totals.json" : $"totals_{characterName}.json");
        return (eventLogPath, historyPath, recordsPath, totalsPath);
    }

    /// <summary>Re-runs CharacterDetector on an interval; on a change,
    /// tells MeterState to switch its log/history files. Only started when
    /// character_name is blank (a manual override is never auto-changed).</summary>
    private static void CharacterWatchLoop(MeterState meterState, string? current, double recheckSeconds,
        string root, Settings settings, CancellationToken token)
    {
        var intervalMs = Math.Max((int)(recheckSeconds * 1000), 1000);
        while (!token.IsCancellationRequested)
        {
            if (token.WaitHandle.WaitOne(intervalMs)) return; // signalled == cancelled

            try
            {
                var detected = SanitizeForFileName(CharacterDetector.Detect());
                if (detected is null || string.Equals(detected, current, StringComparison.OrdinalIgnoreCase))
                    continue;

                var (eventLogPath, historyPath, recordsPath, totalsPath) = CharacterPathsFor(root, settings, detected);
                meterState.SwitchCharacter(detected, eventLogPath, historyPath, recordsPath, totalsPath);
                Console.WriteLine($"[main] character changed: {current ?? "(none)"} -> {detected}; now logging to {historyPath}");
                current = detected;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[main] character watch error: {ex.Message}");
            }
        }
    }

    private static void PollLoop(Win32MemoryReader mh, TextLogWatcher watcher, MeterState meterState, int pollIntervalMs, CancellationToken token)
    {
        var consecutiveErrors = 0;
        var pollCount = 0;
        var totalEvents = 0;
        Console.WriteLine("[main] poll loop starting");
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!mh.IsAlive)
                {
                    Console.Error.WriteLine("[main] game process no longer running, stopping poll loop.");
                    return;
                }
                var events = watcher.Poll();
                if (events.Count > 0)
                {
                    meterState.Ingest(events);
                    totalEvents += events.Count;
                }
                consecutiveErrors = 0;
                pollCount++;
                // Unconditional heartbeat (not gated by debug_print_scan_stats)
                // every 10 polls, so it's obvious the loop is alive and what
                // Poll() has actually returned so far, independent of whether
                // that debug flag is working.
                if (pollCount % 10 == 0)
                    Console.WriteLine($"[main] heartbeat: {pollCount} polls done, {totalEvents} event(s) ingested so far");
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Console.Error.WriteLine($"[main] poll error ({consecutiveErrors}): {ex}");
                if (consecutiveErrors > 50)
                {
                    Console.Error.WriteLine("[main] too many consecutive errors, stopping poll loop.");
                    return;
                }
            }
            Thread.Sleep(pollIntervalMs);
        }
    }

    /// <summary>Trims settings.CharacterName and strips characters that
    /// aren't safe in a Windows file name, returning null for a blank
    /// name so callers can fall back to the unsegmented behavior.</summary>
    private static string? SanitizeForFileName(string? characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(characterName.Trim().Where(c => !invalid.Contains(c)).ToArray());
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Walks up from the executable's directory to find the repo
    /// root (the directory containing "config"), so this runs correctly
    /// whether launched via `dotnet run` (cwd = project dir) or the built
    /// .exe (cwd = bin/Debug/net8.0-windows/).</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "config")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "could not find a 'config' folder anywhere above " + AppContext.BaseDirectory +
            " -- make sure config/settings.json and config/offsets.json exist at the repo root.");
    }
}
