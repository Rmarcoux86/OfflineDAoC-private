using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OfflineDaoc.Launcher;

/// <summary>
/// The Hall of Fame (owner 2026-10-26): every player's expedition wins, shared through the fork's GitHub repository.
/// A player shares a win by opening a pre-filled "[Hall of Fame]" issue; the hall-of-fame workflow checks it and adds
/// it to hall-of-fame/hall-of-fame.json on main, which every launcher reads. Wins on this PC that are not listed yet
/// are shown beside the shared ones. This file is the pure logic (parse, merge, sort, text, URLs) plus the download.
/// </summary>
internal static class HallOfFame
{
    internal const string RepositoryPath = "hall-of-fame/hall-of-fame.json";
    // The last good shared list, beside fork-version.txt, so the panel has something to show offline.
    internal const string CacheFileName = "hall-of-fame.json";
    // The ids of wins this PC already sent as issues, one per line, so SHARE MY WIN doesn't offer them again.
    internal const string SharedIdsFileName = "hall-of-fame-shared.txt";
    internal const string LogFileName = "hall-of-fame.log";
    internal const string IssueTitlePrefix = "[Hall of Fame] ";
    internal const string EmptyText = "No champions yet. Win an expedition to be the first.";
    // GauntletRules.Rounds on the server: a saved Depth of this many rounds means the Gauntlet was cleared.
    internal const int GauntletRounds = 5;

    private static readonly HttpClient Client = CreateClient();

    /// <summary>One expedition win. Player and SubmittedUtc are only set on shared entries (the workflow adds them).</summary>
    internal sealed record Entry(
        string Id,
        string Character,
        string ClassName,
        string? Race,
        string Realm,
        int Level,
        int Seed,
        long PlayedSeconds,
        string EndedUtc,
        int Kills,
        int BossesKilled,
        int Uniques,
        long RealmPoints,
        bool GauntletCleared,
        string? Player,
        string? SubmittedUtc);

    internal enum RowState
    {
        // In the shared list.
        Shared,
        // Only on this PC and not sent yet.
        Local,
        // Only on this PC; its issue was opened but the shared list doesn't have it yet.
        Sent,
    }

    internal sealed record Row(Entry Entry, RowState State);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OfflineDAoC-Launcher");

        return client;
    }

    internal static string RawUrl(string repository) => $"https://raw.githubusercontent.com/{repository}/main/{RepositoryPath}";

    /// <summary>"characterId:endedUtc": one id per win (a character can win again after a new expedition).</summary>
    internal static string EntryId(string characterId, string endedUtc) => characterId + ":" + endedUtc;

    /// <summary>
    /// The entries of a hall-of-fame.json ({ "entries": [ ... ] }). Entries without an id or a character are skipped, a
    /// repeated id keeps its first entry, missing fields get defaults and unknown fields are ignored. Null when the text
    /// is not such an object.
    /// </summary>
    internal static IReadOnlyList<Entry>? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<Entry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (JsonElement element in entries.EnumerateArray())
            {
                Entry? entry = element.ValueKind == JsonValueKind.Object ? ReadEntry(element) : null;

                if (entry is not null && ids.Add(entry.Id))
                {
                    result.Add(entry);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Entry? ReadEntry(JsonElement element)
    {
        string? id = Text(element, "id");
        string? character = Text(element, "character");

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(character))
        {
            return null;
        }

        return new Entry(
            id.Trim(),
            character.Trim(),
            Text(element, "className")?.Trim() ?? "Unknown",
            NullIfEmpty(Text(element, "race")),
            Text(element, "realm")?.Trim() ?? "Unknown",
            (int)Number(element, "level"),
            (int)Number(element, "seed"),
            Number(element, "playedSeconds"),
            Text(element, "endedUtc")?.Trim() ?? string.Empty,
            (int)Number(element, "kills"),
            (int)Number(element, "bossesKilled"),
            (int)Number(element, "uniques"),
            Number(element, "realmPoints"),
            element.TryGetProperty("gauntletCleared", out JsonElement cleared) && cleared.ValueKind == JsonValueKind.True,
            NullIfEmpty(Text(element, "player")),
            NullIfEmpty(Text(element, "submittedUtc")));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Whole numbers only (an int32-sized seed or level, a long for seconds and realm points); anything else is 0.
    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : 0;

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>True when an OfflineExpeditionRecord.GauntletJson (GauntletSave: Round, Depth) has all rounds beaten.</summary>
    internal static bool GauntletCleared(string? gauntletJson)
    {
        if (string.IsNullOrWhiteSpace(gauntletJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(gauntletJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // The server writes "Depth" (no naming policy); any case is accepted.
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "Depth", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Number)
                {
                    return property.Value.TryGetInt32(out int depth) && depth >= GauntletRounds;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The race's name for DOLCharacters.Race (DOL.GS.eRace), or null when it is not known.</summary>
    internal static string? RaceName(int race) => race switch
    {
        1 => "Briton", 2 => "Avalonian", 3 => "Highlander", 4 => "Saracen", 5 => "Norseman", 6 => "Troll", 7 => "Dwarf",
        8 => "Kobold", 9 => "Celt", 10 => "Firbolg", 11 => "Elf", 12 => "Lurikeen", 13 => "Inconnu", 14 => "Valkyn",
        15 => "Sylvan", 16 => "Half Ogre", 17 => "Frostalf", 18 => "Shar", 19 or 20 or 21 => "Minotaur",
        _ => null,
    };

    /// <summary>
    /// Shared entries and the local wins the shared list doesn't have (shared wins on the same id), newest first.
    /// A local win whose id is in <paramref name="sentIds"/> is marked as sent.
    /// </summary>
    internal static IReadOnlyList<Row> Merge(IReadOnlyList<Entry> shared, IReadOnlyList<Entry> local, ISet<string> sentIds)
    {
        var sharedIds = new HashSet<string>(shared.Select(entry => entry.Id), StringComparer.Ordinal);
        var rows = shared.Select(entry => new Row(entry, RowState.Shared)).ToList();

        foreach (Entry entry in local)
        {
            if (sharedIds.Add(entry.Id))
            {
                rows.Add(new Row(entry, sentIds.Contains(entry.Id) ? RowState.Sent : RowState.Local));
            }
        }

        return rows
            .OrderByDescending(row => EndedAt(row.Entry.EndedUtc))
            .ThenBy(row => row.Entry.Character, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Entry.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The local wins that are neither in the shared list nor already sent from this PC, newest first.</summary>
    internal static IReadOnlyList<Entry> Unshared(IReadOnlyList<Entry> local, IReadOnlyList<Entry> shared, ISet<string> sentIds)
    {
        var sharedIds = new HashSet<string>(shared.Select(entry => entry.Id), StringComparer.Ordinal);

        return local
            .Where(entry => !sharedIds.Contains(entry.Id) && !sentIds.Contains(entry.Id))
            .OrderByDescending(entry => EndedAt(entry.EndedUtc))
            .ToList();
    }

    // An unreadable time sorts last.
    internal static DateTimeOffset EndedAt(string endedUtc) =>
        DateTimeOffset.TryParse(endedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset ended)
            ? ended
            : DateTimeOffset.MinValue;

    /// <summary>Played time as hours and minutes ("h:mm"; hours go past 24).</summary>
    internal static string FormatPlayedTime(long seconds)
    {
        long minutes = Math.Max(0, seconds) / 60;

        return $"{minutes / 60}:{minutes % 60:00}";
    }

    /// <summary>"level 50 Celt Hero, Hibernia" (the race is left out when unknown).</summary>
    internal static string Summary(Entry entry)
    {
        string race = entry.Race is null ? string.Empty : entry.Race + " ";

        return $"level {entry.Level} {race}{entry.ClassName}, {entry.Realm}";
    }

    /// <summary>"by player · 12:05 · seed 1234 · date" (played time left out when unknown, i.e. 0 on older saves).</summary>
    internal static string Details(Row row)
    {
        Entry entry = row.Entry;
        var parts = new List<string>(4)
        {
            row.State switch
            {
                RowState.Local => "(this PC, not shared yet)",
                RowState.Sent => "(this PC, sent, not listed yet)",
                _ => "by " + (entry.Player ?? "unknown"),
            },
        };

        if (entry.PlayedSeconds > 0)
        {
            parts.Add(FormatPlayedTime(entry.PlayedSeconds));
        }

        parts.Add("seed " + entry.Seed.ToString(CultureInfo.InvariantCulture));
        DateTimeOffset ended = EndedAt(entry.EndedUtc);

        if (ended != DateTimeOffset.MinValue)
        {
            parts.Add(ended.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The JSON object the workflow reads from the issue (the fields a launcher knows; no player or submit time).</summary>
    internal static string SubmissionJson(Entry entry)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", entry.Id);
            writer.WriteString("character", entry.Character);
            writer.WriteString("className", entry.ClassName);

            if (entry.Race is not null)
            {
                writer.WriteString("race", entry.Race);
            }

            writer.WriteString("realm", entry.Realm);
            writer.WriteNumber("level", entry.Level);
            writer.WriteNumber("seed", entry.Seed);
            writer.WriteNumber("playedSeconds", entry.PlayedSeconds);
            writer.WriteString("endedUtc", entry.EndedUtc);
            writer.WriteNumber("kills", entry.Kills);
            writer.WriteNumber("bossesKilled", entry.BossesKilled);
            writer.WriteNumber("uniques", entry.Uniques);
            writer.WriteNumber("realmPoints", entry.RealmPoints);
            writer.WriteBoolean("gauntletCleared", entry.GauntletCleared);
            writer.WriteEndObject();
        }

        // The writer's line breaks follow the platform; the issue body uses plain "\n".
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// The win with the character's spectator look in a "spectator" field (owner 2026-10-27: sharing a win also makes the
    /// character a Gauntlet spectator), compact so the pre-filled link stays short.
    /// </summary>
    internal static string SubmissionJsonWithSpectator(Entry entry, string spectatorJson)
    {
        JsonObject win = JsonNode.Parse(SubmissionJson(entry))!.AsObject();
        win["spectator"] = JsonNode.Parse(spectatorJson);

        return win.ToJsonString();
    }

    /// <summary>The issue body for a win, with the spectator look when there is one.</summary>
    internal static string IssueBodyWithSpectator(Entry entry, string? spectatorJson) =>
        "My DAoC Expedition win for the Hall of Fame (with my character's look, to stand as a spectator at the "
        + "Gauntlet arena). Submit this issue as it is: a bot checks the block below, adds it to the shared lists and "
        + "closes the issue.\n\n```json\n" + (spectatorJson is null ? SubmissionJson(entry) : SubmissionJsonWithSpectator(entry, spectatorJson)) + "\n```\n";

    /// <summary>The "new issue" page, pre-filled with the win and the spectator look when there is one.</summary>
    internal static string IssueUrlWithSpectator(string repository, Entry entry, string? spectatorJson) =>
        $"https://github.com/{repository}/issues/new?title={Uri.EscapeDataString(IssueTitle(entry))}&body={Uri.EscapeDataString(IssueBodyWithSpectator(entry, spectatorJson))}";

    /// <summary>
    /// The wins the share button offers: those not shared yet, and the newest win of each character not yet sent as a
    /// spectator from this PC (a win shared before sharing included the look), newest first.
    /// </summary>
    internal static IReadOnlyList<Entry> ToShare(IReadOnlyList<Entry> local, IReadOnlyList<Entry> shared, ISet<string> sentIds, ISet<string> spectatorIds)
    {
        var offered = new List<Entry>(Unshared(local, shared, sentIds));
        var characters = new HashSet<string>(offered.Select(GauntletSpectator.CharacterIdOf), StringComparer.Ordinal);

        foreach (Entry entry in local.OrderByDescending(entry => EndedAt(entry.EndedUtc)))
        {
            string character = GauntletSpectator.CharacterIdOf(entry);

            if (!spectatorIds.Contains(character) && characters.Add(character))
            {
                offered.Add(entry);
            }
        }

        return offered.OrderByDescending(entry => EndedAt(entry.EndedUtc)).ToList();
    }

    internal static string IssueTitle(Entry entry) => IssueTitlePrefix + entry.Character;

    internal static string IssueBody(Entry entry) =>
        "My DAoC Expedition win for the Hall of Fame. Submit this issue as it is: a bot checks the block below, "
        + "adds the win to the shared list and closes the issue.\n\n```json\n" + SubmissionJson(entry) + "\n```\n";

    /// <summary>The "new issue" page of the repository, pre-filled with the win.</summary>
    internal static string IssueUrl(string repository, Entry entry) =>
        $"https://github.com/{repository}/issues/new?title={Uri.EscapeDataString(IssueTitle(entry))}&body={Uri.EscapeDataString(IssueBody(entry))}";

    /// <summary>The ids in runtime\hall-of-fame-shared.txt (empty when the file is missing or unreadable).</summary>
    internal static HashSet<string> ReadSentIds(string runtimeFolder)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string file = Path.Combine(runtimeFolder, SharedIdsFileName);

        try
        {
            if (File.Exists(file))
            {
                ids.UnionWith(ParseSentIds(File.ReadAllText(file)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing remembered: the button may offer a win again, which is harmless (the workflow dedupes).
        }

        return ids;
    }

    internal static IEnumerable<string> ParseSentIds(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);

    /// <summary>Adds the id to runtime\hall-of-fame-shared.txt; failures are logged.</summary>
    internal static void RememberSent(string runtimeFolder, string id, string logsDirectory)
    {
        try
        {
            File.AppendAllText(Path.Combine(runtimeFolder, SharedIdsFileName), id + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log(logsDirectory, $"Could not remember the shared win {id}: {exception.Message}");
        }
    }

    /// <summary>The cached shared list (runtime\hall-of-fame.json), or null when there is none or it can't be read.</summary>
    internal static IReadOnlyList<Entry>? ReadCache(string runtimeFolder)
    {
        string file = Path.Combine(runtimeFolder, CacheFileName);

        try
        {
            return File.Exists(file) ? Parse(File.ReadAllText(file)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the shared list from the repository's main branch and caches it. Null when it can't be had (no network,
    /// HTTP error or invalid file); failures are logged, never thrown.
    /// </summary>
    internal static async Task<IReadOnlyList<Entry>?> FetchAsync(string repository, string runtimeFolder, string logsDirectory)
    {
        string url = RawUrl(repository);

        try
        {
            using HttpResponseMessage response = await Client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log(logsDirectory, $"Hall of Fame: {url} answered HTTP {(int)response.StatusCode}.");

                return null;
            }

            string json = await response.Content.ReadAsStringAsync();
            IReadOnlyList<Entry>? entries = Parse(json);

            if (entries is null)
            {
                Log(logsDirectory, $"Hall of Fame: {url} is not a valid hall-of-fame.json.");

                return null;
            }

            try
            {
                File.WriteAllText(Path.Combine(runtimeFolder, CacheFileName), json);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log(logsDirectory, $"Hall of Fame: could not cache the list: {exception.Message}");
            }

            return entries;
        }
        catch (Exception exception)
        {
            Log(logsDirectory, $"Hall of Fame download failed: {exception.GetType().Name}: {exception.Message}");

            return null;
        }
    }

    internal static void Log(string logsDirectory, string message)
    {
        try
        {
            Directory.CreateDirectory(logsDirectory);
            string path = Path.Combine(logsDirectory, LogFileName);

            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never break the launcher.
        }
    }
}
