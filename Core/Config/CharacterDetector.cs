using System.Text.RegularExpressions;

namespace MnmDamageParser.Core.Config;

/// <summary>
/// Works out which M&amp;M character is currently being played, so the event
/// log and encounter history can be filed per-character automatically
/// instead of via the manual <c>character_name</c> setting.
///
/// M&amp;M (a Unity game by Niche Worlds Cult) puts no character name in its
/// window title, and an earlier heap-scan for the name came up empty --
/// but the game leaves two usable traces on disk under
/// <c>%USERPROFILE%\AppData\LocalLow\Niche Worlds Cult\Monsters and Memories\</c>:
///
/// <list type="bullet">
/// <item><b>Player.log</b> (the Unity player log). Each time the local
/// player enters the world the game logs, from inside
/// <c>Client.Client.SetMine()</c>:
/// <code>[LEDGER MIGRATION] Migration complete for &lt;Name&gt;</code>
/// and, repeatedly,
/// <code>client &lt;Name&gt; &lt;id&gt; received pet ids ...</code>
/// whose surrounding stack frames are <c>DoWhenMyClientExists</c> /
/// <c>HandleUpdatePets</c> -- the LOCAL client. (Other players' identical
/// "received pet ids" text sits under <c>SpawnEntitySystem</c> instead,
/// so the stack is what tells them apart.)</item>
///
/// <item><b>&lt;server&gt;\&lt;Name&gt;\</b> -- one folder per character you have
/// played, each holding <c>Ledger\</c> and/or <c>journal\</c> subfolders,
/// written while that character is active.</item>
/// </list>
///
/// Strategy: build the set of names that actually have a character folder
/// on disk, then return the most recent name mentioned as the local
/// player in Player.log (then Player-prev.log) that is in that set. If the
/// logs yield nothing, fall back to the most-recently-modified character
/// folder. Every candidate is checked against the on-disk folder set, so
/// a change in the game's log format can never make this invent a name --
/// worst case it returns null and the app runs unsegmented, exactly as it
/// does today with a blank <c>character_name</c>.
/// </summary>
public static class CharacterDetector
{
    private static readonly Regex MigrationLine = new(
        @"Migration complete for (?<name>[\p{L}][\p{L}\p{N}'_-]{1,31})\b",
        RegexOptions.Compiled);

    private static readonly Regex ClientPetLine = new(
        @"^client (?<name>[\p{L}][\p{L}\p{N}'_-]{1,31}) \d+ received pet ids\b",
        RegexOptions.Compiled);

    // If one of these shows up within a few lines below a "client X
    // received pet ids" line, that line was about the LOCAL player.
    private static readonly string[] LocalPlayerStackHints =
        { "DoWhenMyClientExists", "HandleUpdatePets", "SetMine" };

    private const string PublisherDir = "Niche Worlds Cult";
    private const string ProductDir = "Monsters and Memories";

    /// <summary>The game's per-user data directory
    /// (<c>%USERPROFILE%\AppData\LocalLow\Niche Worlds Cult\Monsters and Memories</c>).</summary>
    public static string DefaultDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", PublisherDir, ProductDir);

    /// <param name="dataDir">Override the game data directory (tests point
    /// this at a fixture; production leaves it null for
    /// <see cref="DefaultDataDir"/>).</param>
    /// <returns>The detected character name, or null if nothing could be
    /// determined -- the caller then runs unsegmented, same as a blank
    /// <c>character_name</c>.</returns>
    public static string? Detect(string? dataDir = null)
    {
        dataDir ??= DefaultDataDir;
        if (!Directory.Exists(dataDir)) return null;

        var known = KnownCharacterFolders(dataDir);
        if (known.Count == 0) return null;

        foreach (var logName in new[] { "Player.log", "Player-prev.log" })
        {
            var logPath = Path.Combine(dataDir, logName);
            if (!File.Exists(logPath)) continue;
            var fromLog = ScanLog(logPath, known.Keys);
            if (fromLog is not null) return fromLog;
        }

        // No usable log line -- fall back to whichever character folder was
        // touched most recently.
        return known.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>Character name -> most recent write time under that
    /// character's folder, keyed case-insensitively but preserving the
    /// on-disk casing as the key.</summary>
    private static Dictionary<string, DateTime> KnownCharacterFolders(string dataDir)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var serverDir in SafeDirs(dataDir))
        {
            var serverName = Path.GetFileName(serverDir);
            // "Exported" is the game's data-export dump, not a server.
            if (serverName.Equals("Exported", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var charDir in SafeDirs(serverDir))
            {
                var isCharacter =
                    Directory.Exists(Path.Combine(charDir, "Ledger")) ||
                    Directory.Exists(Path.Combine(charDir, "journal"));
                if (!isCharacter) continue;

                var name = Path.GetFileName(charDir);
                var touched = MostRecentWrite(charDir);
                if (!result.TryGetValue(name, out var existing) || touched > existing)
                    result[name] = touched;
            }
        }
        return result;
    }

    private static string? ScanLog(string logPath, IEnumerable<string> knownNames)
    {
        var known = new HashSet<string>(knownNames, StringComparer.OrdinalIgnoreCase);

        List<string> lines;
        try
        {
            // The game keeps Player.log open for writing -- must share it.
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null) lines.Add(line);
        }
        catch
        {
            return null;
        }

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var mig = MigrationLine.Match(lines[i]);
            if (mig.Success && known.TryGetValue(mig.Groups["name"].Value, out var migName))
                return migName;

            var pet = ClientPetLine.Match(lines[i]);
            if (pet.Success && known.Contains(pet.Groups["name"].Value))
            {
                var limit = Math.Min(lines.Count, i + 8);
                for (var j = i + 1; j < limit; j++)
                {
                    if (LocalPlayerStackHints.Any(h => lines[j].Contains(h)))
                    {
                        known.TryGetValue(pet.Groups["name"].Value, out var petName);
                        return petName;
                    }
                }
            }
        }
        return null;
    }

    private static DateTime MostRecentWrite(string dir)
    {
        // Files only -- directory mtimes are noisy (they move when a child
        // is added but not when one is edited).
        var newest = Directory.GetLastWriteTimeUtc(dir);
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(file);
                if (t > newest) newest = t;
            }
        }
        catch
        {
            // permission / race -- the top-level dir time is a fine fallback
        }
        return newest;
    }

    private static IEnumerable<string> SafeDirs(string parent)
    {
        try { return Directory.EnumerateDirectories(parent); }
        catch { return Array.Empty<string>(); }
    }
}
