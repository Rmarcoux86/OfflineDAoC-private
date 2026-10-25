using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace OfflineDaoc.Launcher;

/// <summary>
/// Finds, downloads and starts this fork's update (the "fork-update-*.zip" of the newest GitHub release).
/// The installed version is runtime\fork-version.txt, written by INSTALL FORK UPDATE.cmd. Everything here fails
/// quietly: no network, no release or a rate limit only means that no update is offered.
/// </summary>
internal static class ForkUpdater
{
    // The GitHub repository whose latest release is the fork update. A runtime\fork-update-source.txt whose first line
    // is "owner/repo" overrides it, so a renamed or moved repository needs no new launcher build.
    internal const string DefaultOwner = "Rmarcoux86";
    internal const string DefaultRepository = "OfflineDAoC-private";

    internal const string SourceOverrideFileName = "fork-update-source.txt";
    internal const string VersionFileName = "fork-version.txt";
    // runtime\fork-version.txt containing just "dev" marks a developer install: no update is ever offered.
    internal const string DeveloperMarker = "dev";
    internal const string InstallerName = "INSTALL FORK UPDATE.cmd";
    internal const string LogFileName = "fork-update.log";
    // The tag whose "Updated to" notice was last shown (or recorded without one on a fresh install), beside fork-version.txt.
    internal const string NoticeFileName = "fork-notes-shown.txt";
    // The installed build history (newest first), shipped in the update zip and copied beside fork-version.txt.
    internal const string HistoryFileName = "build-history.json";
    internal const string NoNotesText = "No notes for this build.";
    // GitHub allows 60 unauthenticated API calls an hour per address; one call every 30 minutes stays far below.
    internal const int CheckIntervalMilliseconds = 30 * 60 * 1000;

    private static readonly HttpClient ApiClient = CreateClient(TimeSpan.FromSeconds(10), "application/vnd.github+json");
    private static readonly HttpClient DownloadClient = CreateClient(TimeSpan.FromMinutes(10), null);

    // Notes: the "What's new" items of the release body (empty when it has no such section).
    internal sealed record ForkRelease(string Tag, string ZipUrl, IReadOnlyList<string> Notes);

    /// <summary>One fork build of runtime\build-history.json: its tag, its day (UTC) and its "What's new" items.</summary>
    internal sealed record ForkBuild(string Tag, DateOnly? Date, IReadOnlyList<string> Notes);

    internal enum UpdatedNotice
    {
        // Nothing to show or record: unknown or developer install, or the notice for this tag was already handled.
        None,
        // A fresh install (no earlier record): remember the tag without showing anything.
        RecordOnly,
        // The installed tag changed since the last notice: show "Updated to" and remember the tag.
        ShowAndRecord,
    }

    internal sealed record ForkUpdateOffer(ForkRelease Release, string? InstalledTag);

    private static HttpClient CreateClient(TimeSpan timeout, string? accept)
    {
        var client = new HttpClient { Timeout = timeout };
        // The GitHub API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OfflineDAoC-Launcher");

        if (accept is not null)
        {
            client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        }

        return client;
    }

    /// <summary>"owner/repo" from runtime\fork-update-source.txt, else the built-in repository.</summary>
    internal static string SourceRepositoryFor(string runtimeFolder)
    {
        string fallback = DefaultOwner + "/" + DefaultRepository;
        string file = Path.Combine(runtimeFolder, SourceOverrideFileName);

        try
        {
            if (!File.Exists(file))
            {
                return fallback;
            }

            return ParseRepository(File.ReadLines(file).FirstOrDefault()) ?? fallback;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    /// <summary>A valid "owner/repo" (letters, digits, '-', '_', '.'), trimmed; otherwise null.</summary>
    internal static string? ParseRepository(string? line)
    {
        string[] parts = (line ?? string.Empty).Trim().Split('/');

        if (parts.Length != 2)
        {
            return null;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || !part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return null;
            }
        }

        return parts[0] + "/" + parts[1];
    }

    internal static string LatestReleaseUrl(string repository) => $"https://api.github.com/repos/{repository}/releases/latest";

    /// <summary>
    /// The items of a release body's "## What's new" section: it runs to the next line starting with "## " (or the end);
    /// an item is a line starting with "- " and a line indented by two spaces continues the previous item. Other lines are
    /// ignored. No section (or no items in it) gives an empty list.
    /// </summary>
    internal static IReadOnlyList<string> ParseWhatsNew(string? body)
    {
        var items = new List<string>();

        if (string.IsNullOrEmpty(body))
        {
            return items;
        }

        bool inSection = false;

        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (!inSection)
            {
                // GitHub's editor may turn the apostrophe into a typographic one.
                inSection = string.Equals(line.Trim().Replace('\u2019', '\''), "## What's new", StringComparison.OrdinalIgnoreCase);

                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                string item = line[2..].Trim();

                if (item.Length > 0)
                {
                    items.Add(item);
                }
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && line.Trim().Length > 0 && items.Count > 0)
            {
                items[items.Count - 1] += " " + line.Trim();
            }
        }

        return items;
    }

    /// <summary>
    /// The builds of a build-history.json: an array of { "tag", "date" (yyyy-MM-dd), "items": [ ... ] }, kept in the
    /// file's order (newest first). Entries without a tag are skipped; an unreadable date is left out; unknown fields
    /// are ignored. Null when the text is not such an array.
    /// </summary>
    internal static IReadOnlyList<ForkBuild>? ParseBuildHistory(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builds = new List<ForkBuild>();

            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                string? tag = entry.ValueKind == JsonValueKind.Object ? StringProperty(entry, "tag")?.Trim() : null;

                if (string.IsNullOrEmpty(tag))
                {
                    continue;
                }

                DateOnly? date = DateOnly.TryParseExact(StringProperty(entry, "date")?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day)
                    ? day
                    : null;
                var items = new List<string>();

                if (entry.TryGetProperty("items", out JsonElement itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in itemsElement.EnumerateArray())
                    {
                        string? text = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;

                        if (!string.IsNullOrEmpty(text))
                        {
                            items.Add(text);
                        }
                    }
                }

                builds.Add(new ForkBuild(tag, date, items));
            }

            return builds;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The builds of runtime\build-history.json, or null when the file is missing, unreadable or not valid.</summary>
    internal static IReadOnlyList<ForkBuild>? ReadBuildHistory(string runtimeFolder)
    {
        string file = Path.Combine(runtimeFolder, HistoryFileName);

        try
        {
            return File.Exists(file) ? ParseBuildHistory(File.ReadAllText(file)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The tag and the fork-update zip of a GitHub "latest release" response, or null when either is missing.</summary>
    internal static ForkRelease? ParseLatestRelease(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out JsonElement tagElement) || tagElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string tag = tagElement.GetString()!.Trim();

            if (tag.Length == 0 || !root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || !asset.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String
                    || !asset.TryGetProperty("browser_download_url", out JsonElement urlElement) || urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string name = nameElement.GetString()!;
                string url = urlElement.GetString()!;

                if (name.StartsWith("fork-update-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return new ForkRelease(tag, url, ParseWhatsNew(StringProperty(root, "body")));
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first non-empty line of runtime\fork-version.txt, or null when there is none (stock or unstamped).</summary>
    internal static string? ReadInstalledVersion(string runtimeFolder) => ReadFirstLine(Path.Combine(runtimeFolder, VersionFileName));

    /// <summary>The tag recorded in runtime\fork-notes-shown.txt, or null when there is none yet.</summary>
    internal static string? ReadNoticeTag(string runtimeFolder) => ReadFirstLine(Path.Combine(runtimeFolder, NoticeFileName));

    /// <summary>Records the tag in runtime\fork-notes-shown.txt; false (logged) when the file can't be written.</summary>
    internal static bool WriteNoticeTag(string runtimeFolder, string tag, string logsDirectory)
    {
        try
        {
            File.WriteAllText(Path.Combine(runtimeFolder, NoticeFileName), tag + Environment.NewLine);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log(logsDirectory, $"Could not record the shown update notice: {exception.Message}");

            return false;
        }
    }

    /// <summary>
    /// Whether the "Updated to" notice is due: only for a real fork tag (not "dev", not unknown) that differs from the
    /// last recorded one. With no record at all (a fresh install) the tag is only recorded.
    /// </summary>
    internal static UpdatedNotice DecideUpdatedNotice(string? installedTag, string? noticeTag)
    {
        if (string.IsNullOrWhiteSpace(installedTag) || IsDeveloperInstall(installedTag))
        {
            return UpdatedNotice.None;
        }

        if (string.IsNullOrWhiteSpace(noticeTag))
        {
            return UpdatedNotice.RecordOnly;
        }

        return string.Equals(installedTag, noticeTag, StringComparison.OrdinalIgnoreCase) ? UpdatedNotice.None : UpdatedNotice.ShowAndRecord;
    }

    private static string? ReadFirstLine(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            return File.ReadLines(file).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool IsDeveloperInstall(string? installedTag) =>
        string.Equals(installedTag, DeveloperMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An update is offered when the latest tag differs from the installed one. No stamp (stock or a first fork
    /// install) counts as out of date; a developer install ("dev") never is.
    /// </summary>
    internal static bool IsUpdateAvailable(string? installedTag, string latestTag)
    {
        if (IsDeveloperInstall(installedTag))
        {
            return false;
        }

        if (installedTag is null)
        {
            return true;
        }

        return !string.Equals(installedTag, latestTag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Asks GitHub for the latest release. Null means no update (or no answer); failures are logged, never thrown.</summary>
    internal static async Task<ForkUpdateOffer?> CheckAsync(string runtimeFolder, string logsDirectory)
    {
        string? installed = ReadInstalledVersion(runtimeFolder);

        if (IsDeveloperInstall(installed))
        {
            return null;
        }

        string url = LatestReleaseUrl(SourceRepositoryFor(runtimeFolder));

        try
        {
            using HttpResponseMessage response = await ApiClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log(logsDirectory, $"Update check: {url} answered HTTP {(int)response.StatusCode}.");

                return null;
            }

            ForkRelease? release = ParseLatestRelease(await response.Content.ReadAsStringAsync());

            if (release is null)
            {
                Log(logsDirectory, $"Update check: the latest release at {url} has no fork-update zip.");

                return null;
            }

            return IsUpdateAvailable(installed, release.Tag) ? new ForkUpdateOffer(release, installed) : null;
        }
        catch (Exception exception)
        {
            Log(logsDirectory, $"Update check failed: {exception.GetType().Name}: {exception.Message}");

            return null;
        }
    }

    /// <summary>
    /// Downloads the release zip to %TEMP%\OfflineDAoC-fork-update\&lt;tag&gt;.zip, extracts it into a clean folder beside it and
    /// returns that folder. Progress (0-100) is reported on the caller's thread.
    /// </summary>
    internal static async Task<string> DownloadAsync(ForkRelease release, Action<int>? progress)
    {
        string safeTag = string.Concat(release.Tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        // "." or ".." would make the folder below %TEMP% itself (deleted further down); git tags can't start with '.'.
        if (safeTag.Length == 0 || safeTag.StartsWith('.'))
        {
            throw new InvalidDataException($"The release tag \"{release.Tag}\" can't be used as a folder name.");
        }

        string work = Path.Combine(Path.GetTempPath(), "OfflineDAoC-fork-update");
        string zip = Path.Combine(work, safeTag + ".zip");
        string folder = Path.Combine(work, safeTag);
        Directory.CreateDirectory(work);

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }

        using (HttpResponseMessage response = await DownloadClient.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using Stream source = await response.Content.ReadAsStreamAsync();
            await using FileStream target = File.Create(zip);
            byte[] buffer = new byte[81920];
            long received = 0;
            int lastPercent = -1;
            int read;

            while ((read = await source.ReadAsync(buffer.AsMemory())) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                int percent = total is > 0 ? (int)Math.Min(100, received * 100 / total.Value) : -1;

                if (percent != lastPercent && percent >= 0)
                {
                    lastPercent = percent;
                    progress?.Invoke(percent);
                }
            }
        }

        await Task.Run(() => ZipFile.ExtractToDirectory(zip, folder, overwriteFiles: true));

        foreach (string required in new[] { InstallerName, "GameServer.dll" })
        {
            if (!File.Exists(Path.Combine(folder, required)))
            {
                throw new InvalidDataException($"The downloaded update has no {required}.");
            }
        }

        return folder;
    }

    /// <summary>
    /// cmd.exe arguments that run the installer unattended. With /s, cmd removes exactly the first and the last quote of
    /// the command and keeps the rest, so the quoted script and game paths (both may contain spaces) survive intact.
    /// </summary>
    internal static string InstallerArguments(string script, string gameFolder, bool newClient)
    {
        string command = $"\"{script}\" \"{Path.TrimEndingDirectorySeparator(gameFolder)}\" /auto";

        if (newClient)
        {
            command += " /newclient";
        }

        return $"/d /s /c \"{command}\"";
    }

    /// <summary>Starts the extracted INSTALL FORK UPDATE.cmd in its own console window. The caller then exits.</summary>
    internal static void StartInstaller(string extractedFolder, string gameFolder, bool newClient)
    {
        string script = Path.Combine(extractedFolder, InstallerName);
        string? comSpec = Environment.GetEnvironmentVariable("ComSpec");
        string shell = string.IsNullOrWhiteSpace(comSpec) ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : comSpec;
        var startInfo = new ProcessStartInfo(shell, InstallerArguments(script, gameFolder, newClient))
        {
            // Shell execution gives the installer its own console (its messages stay readable after the launcher
            // exits) and passes the argument string to cmd unchanged.
            UseShellExecute = true,
            WorkingDirectory = extractedFolder,
        };
        Process.Start(startInfo)?.Dispose();
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
