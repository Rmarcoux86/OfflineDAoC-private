using System.Reflection;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// The fork's self-update: reading GitHub's latest-release answer and its "What's new" notes, deciding whether an
/// update is offered, the installed build history, the one-time "Updated to" notice, the repository override file
/// and the installer's command line. No network is used.
/// </summary>
public sealed class ForkUpdaterTests
{
    private static readonly Assembly Launcher = Assembly.Load("OfflineDAoC");
    private static readonly Type Updater = Launcher.GetType("OfflineDaoc.Launcher.ForkUpdater")!;
    private static readonly BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static object Call(string method, params object[] arguments) =>
        Updater.GetMethod(method, HiddenStatic)!.Invoke(null, arguments);

    private static (string Tag, string ZipUrl)? ParseLatestRelease(string json)
    {
        object release = Call("ParseLatestRelease", json);

        if (release is null)
        {
            return null;
        }

        Type type = release.GetType();

        return ((string)type.GetProperty("Tag")!.GetValue(release)!, (string)type.GetProperty("ZipUrl")!.GetValue(release)!);
    }

    private const string LatestRelease = """
        {
          "url": "https://api.github.com/repos/Rmarcoux86/OfflineDAoC-private/releases/1",
          "tag_name": "fork-v2",
          "name": "fork-v2",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/Rmarcoux86/OfflineDAoC-private/releases/download/fork-v2/notes.txt" },
            { "name": "fork-update-fork-v2.tar.gz", "browser_download_url": "https://github.com/Rmarcoux86/OfflineDAoC-private/releases/download/fork-v2/fork-update-fork-v2.tar.gz" },
            { "name": "fork-update-fork-v2.zip", "size": 2048, "browser_download_url": "https://github.com/Rmarcoux86/OfflineDAoC-private/releases/download/fork-v2/fork-update-fork-v2.zip" },
            { "name": "fork-update-other.zip", "browser_download_url": "https://github.com/Rmarcoux86/OfflineDAoC-private/releases/download/fork-v2/fork-update-other.zip" }
          ]
        }
        """;

    [Test]
    public void LatestReleasePicksTheTagAndTheFirstForkUpdateZip()
    {
        var release = ParseLatestRelease(LatestRelease);

        Assert.That(release, Is.Not.Null);
        Assert.That(release!.Value.Tag, Is.EqualTo("fork-v2"));
        Assert.That(release.Value.ZipUrl, Is.EqualTo("https://github.com/Rmarcoux86/OfflineDAoC-private/releases/download/fork-v2/fork-update-fork-v2.zip"));
    }

    [TestCase("""{ "tag_name": "fork-v2", "assets": [ { "name": "GameServer.dll", "browser_download_url": "https://x/GameServer.dll" } ] }""")]
    [TestCase("""{ "tag_name": "fork-v2", "assets": [] }""")]
    [TestCase("""{ "tag_name": "fork-v2" }""")]
    [TestCase("""{ "assets": [ { "name": "fork-update-fork-v2.zip", "browser_download_url": "https://x/fork-update-fork-v2.zip" } ] }""")]
    [TestCase("""{ "tag_name": "fork-v2", "assets": [ { "name": "fork-update-fork-v2.zip", "browser_download_url": "http://x/fork-update-fork-v2.zip" } ] }""")]
    [TestCase("""{ "message": "Not Found", "documentation_url": "https://docs.github.com/rest" }""")]
    [TestCase("""[]""")]
    [TestCase("""not json""")]
    public void ReleasesWithoutAUsableUpdateZipOfferNothing(string json)
    {
        Assert.That(ParseLatestRelease(json), Is.Null);
    }

    [TestCase(null, "fork-v2", true, TestName = "Unstamped install is offered the release")]
    [TestCase("fork-v1", "fork-v2", true, TestName = "Older stamp is offered the release")]
    [TestCase("fork-v2", "fork-v2", false, TestName = "Same tag is up to date")]
    [TestCase("FORK-V2", "fork-v2", false, TestName = "Tags compare without case")]
    [TestCase("dev", "fork-v2", false, TestName = "Developer install is never offered updates")]
    [TestCase("DEV", "fork-v2", false, TestName = "Developer marker ignores case")]
    public void UpdateIsOfferedWhenTheTagsDiffer(string installed, string latest, bool expected)
    {
        Assert.That(Call("IsUpdateAvailable", installed, latest), Is.EqualTo(expected));
    }

    [Test]
    public void InstalledVersionIsTheFirstNonEmptyLineOrNull()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "odc-forkupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);

        try
        {
            Assert.That(Call("ReadInstalledVersion", runtime), Is.Null, "no fork-version.txt");

            File.WriteAllText(Path.Combine(runtime, "fork-version.txt"), "\r\n  fork-v3  \r\n");
            Assert.That(Call("ReadInstalledVersion", runtime), Is.EqualTo("fork-v3"));

            File.WriteAllText(Path.Combine(runtime, "fork-version.txt"), "dev\r\n");
            Assert.That(Call("ReadInstalledVersion", runtime), Is.EqualTo("dev"));
            Assert.That(Call("IsUpdateAvailable", "dev", "fork-v9"), Is.False);
        }
        finally
        {
            Directory.Delete(runtime, true);
        }
    }

    [TestCase("someone/OfflineDAoC", "someone/OfflineDAoC")]
    [TestCase("  some-one/Offline_DAoC.fork  ", "some-one/Offline_DAoC.fork")]
    [TestCase("someone", null)]
    [TestCase("someone/repo/extra", null)]
    [TestCase("/repo", null)]
    [TestCase("someone/..", null)]
    [TestCase("some one/repo", null)]
    [TestCase("", null)]
    public void RepositoryOverrideMustBeOwnerSlashRepo(string line, string expected)
    {
        Assert.That(Call("ParseRepository", line), Is.EqualTo(expected));
    }

    [Test]
    public void OverrideFileReplacesTheBuiltInRepository()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "odc-forkupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);

        try
        {
            Assert.That(Call("SourceRepositoryFor", runtime), Is.EqualTo("Rmarcoux86/OfflineDAoC-private"), "built-in default");

            File.WriteAllText(Path.Combine(runtime, "fork-update-source.txt"), "Rmarcoux86/OfflineDAoC\r\nignored second line\r\n");
            Assert.That(Call("SourceRepositoryFor", runtime), Is.EqualTo("Rmarcoux86/OfflineDAoC"));
            Assert.That(Call("LatestReleaseUrl", "Rmarcoux86/OfflineDAoC"),
                Is.EqualTo("https://api.github.com/repos/Rmarcoux86/OfflineDAoC/releases/latest"));

            File.WriteAllText(Path.Combine(runtime, "fork-update-source.txt"), "not a repository\r\n");
            Assert.That(Call("SourceRepositoryFor", runtime), Is.EqualTo("Rmarcoux86/OfflineDAoC-private"), "an unreadable override falls back");
        }
        finally
        {
            Directory.Delete(runtime, true);
        }
    }

    private static string[] WhatsNew(string body) => ((IEnumerable<string>)Call("ParseWhatsNew", new object[] { body })).ToArray();

    // (Tag, Date as yyyy-MM-dd or null, Notes) of each build, in the parsed order.
    private static List<(string Tag, string Date, string[] Notes)> BuildHistory(string json)
    {
        object builds = Call("ParseBuildHistory", json);

        if (builds is null)
        {
            return null;
        }

        var result = new List<(string, string, string[])>();

        foreach (object build in (System.Collections.IEnumerable)builds)
        {
            Type type = build.GetType();
            var date = (DateOnly?)type.GetProperty("Date")!.GetValue(build);
            result.Add(((string)type.GetProperty("Tag")!.GetValue(build)!,
                date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ((IEnumerable<string>)type.GetProperty("Notes")!.GetValue(build)!).ToArray()));
        }

        return result;
    }

    [Test]
    public void WhatsNewTakesTheItemsOfItsSectionOnly()
    {
        const string body = "Intro line.\n\n## What's new\n- First change.\n- Second change.\n\n## How to install\n- Not a change.\n";

        Assert.That(WhatsNew(body), Is.EqualTo(new[] { "First change.", "Second change." }));
    }

    [Test]
    public void WhatsNewRunsToTheEndWithoutAFollowingHeading()
    {
        Assert.That(WhatsNew("## What's new\n- Only change."), Is.EqualTo(new[] { "Only change." }));
    }

    [Test]
    public void WhatsNewJoinsContinuationLinesAndIgnoresOtherText()
    {
        const string body = "## What's new\nSome prose.\n- A long change that\n  goes on here\n  and here.\n-   Padded item.  \n- \n### Subheading\n- Last.\n";

        Assert.That(WhatsNew(body), Is.EqualTo(new[] { "A long change that goes on here and here.", "Padded item.", "Last." }));
    }

    [Test]
    public void WhatsNewReadsWindowsLineEndingsAndATypographicApostrophe()
    {
        Assert.That(WhatsNew("## What's new\r\n- First.\r\n  More.\r\n- Second.\r\n\r\n## How to install\r\nSteps.\r\n"),
            Is.EqualTo(new[] { "First. More.", "Second." }));
        Assert.That(WhatsNew("## What\u2019s New  \n- Curly.\n"), Is.EqualTo(new[] { "Curly." }));
    }

    [TestCase("")]
    [TestCase("Fork update for Offline DAoC 0.37.\n\n1. Install the stock game.\n- A list outside any section.\n")]
    [TestCase("## How to install\n- Step.\n")]
    [TestCase("## What's new\n\n## How to install\n- Step.\n")]
    [TestCase("### What's new\n- Wrong heading level.\n")]
    public void BodiesWithoutWhatsNewItemsGiveNoNotes(string body)
    {
        Assert.That(WhatsNew(body), Is.Empty);
    }

    [Test]
    public void MissingBodyGivesNoNotes()
    {
        Assert.That(Call("ParseWhatsNew", new object[] { null }), Is.Empty);
    }

    [Test]
    public void LatestReleaseKeepsTheWhatsNewItemsOfItsBody()
    {
        const string json = """
            { "tag_name": "fork-v3", "body": "## What's new\r\n- Faster fishing.\r\n\r\n## How to install\r\n1. Run it.",
              "assets": [ { "name": "fork-update-fork-v3.zip", "browser_download_url": "https://x/fork-update-fork-v3.zip" } ] }
            """;
        object release = Call("ParseLatestRelease", json);
        object noBody = Call("ParseLatestRelease", LatestRelease);

        Assert.That((IEnumerable<string>)release.GetType().GetProperty("Notes")!.GetValue(release)!, Is.EqualTo(new[] { "Faster fishing." }));
        Assert.That((IEnumerable<string>)noBody.GetType().GetProperty("Notes")!.GetValue(noBody)!, Is.Empty);
    }

    [Test]
    public void BuildHistoryKeepsTheFileOrderAndIgnoresUnknownFields()
    {
        const string json = """
            [
              { "tag": "fork-v53", "date": "2026-10-25", "items": ["First change.", "  Second change.  "], "extra": { "x": 1 } },
              { "tag": "fork-v52", "date": "2026-10-24", "items": [] },
              { "tag": "fork-v51" }
            ]
            """;

        var builds = BuildHistory(json);

        Assert.That(builds, Has.Count.EqualTo(3));
        Assert.That(builds![0].Tag, Is.EqualTo("fork-v53"));
        Assert.That(builds[0].Date, Is.EqualTo("2026-10-25"));
        Assert.That(builds[0].Notes, Is.EqualTo(new[] { "First change.", "Second change." }));
        Assert.That(builds[1].Tag, Is.EqualTo("fork-v52"));
        Assert.That(builds[1].Notes, Is.Empty);
        Assert.That(builds[2].Date, Is.Null);
        Assert.That(builds[2].Notes, Is.Empty);
    }

    [Test]
    public void BuildHistorySkipsBadEntriesAndBadValues()
    {
        const string json = """
            [
              "not an object",
              { "date": "2026-10-25", "items": ["No tag."] },
              { "tag": "  ", "items": ["Blank tag."] },
              { "tag": 52, "items": ["Number tag."] },
              { "tag": "fork-v50", "date": "25/10/2026", "items": ["Kept.", 7, null, "", { "a": 1 }] },
              { "tag": "fork-v49", "date": 20261025, "items": "not a list" }
            ]
            """;

        var builds = BuildHistory(json);

        Assert.That(builds!.Select(build => build.Tag), Is.EqualTo(new[] { "fork-v50", "fork-v49" }));
        Assert.That(builds[0].Date, Is.Null, "a date that isn't yyyy-MM-dd is left out");
        Assert.That(builds[0].Notes, Is.EqualTo(new[] { "Kept." }));
        Assert.That(builds[1].Date, Is.Null);
        Assert.That(builds[1].Notes, Is.Empty);
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("""{ "tag": "fork-v53", "items": [] }""")]
    [TestCase("""[ { "tag": "fork-v53" """)]
    public void UnreadableBuildHistoryIsNull(string json)
    {
        Assert.That(BuildHistory(json), Is.Null);
    }

    [Test]
    public void BuildHistoryFileIsReadFromTheRuntimeFolder()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "odc-forkupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);

        try
        {
            Assert.That(Call("ReadBuildHistory", runtime), Is.Null, "no build-history.json");

            // Written with a UTF-8 byte order mark, as Windows tools often do.
            File.WriteAllText(Path.Combine(runtime, "build-history.json"), "[ { \"tag\": \"fork-v53\", \"date\": \"2026-10-25\", \"items\": [\"Caf\u00e9 change.\"] } ]\r\n",
                new System.Text.UTF8Encoding(true));
            var builds = (System.Collections.ICollection)Call("ReadBuildHistory", runtime);
            Assert.That(builds, Has.Count.EqualTo(1));

            File.WriteAllText(Path.Combine(runtime, "build-history.json"), "{ broken");
            Assert.That(Call("ReadBuildHistory", runtime), Is.Null);
        }
        finally
        {
            Directory.Delete(runtime, true);
        }
    }

    [TestCase("fork-v53", "fork-v52", "ShowAndRecord", TestName = "A new tag shows the notice")]
    [TestCase("fork-v53", "fork-v53", "None", TestName = "The same tag shows nothing again")]
    [TestCase("FORK-V53", "fork-v53", "None", TestName = "Notice tags compare without case")]
    [TestCase("fork-v53", null, "RecordOnly", TestName = "A fresh install only records the tag")]
    [TestCase("fork-v53", "", "RecordOnly", TestName = "An empty record counts as none")]
    [TestCase(null, "fork-v52", "None", TestName = "An unknown version shows nothing")]
    [TestCase(null, null, "None", TestName = "An unknown version records nothing")]
    [TestCase("dev", "fork-v52", "None", TestName = "A developer install shows nothing")]
    [TestCase("DEV", null, "None", TestName = "A developer install records nothing")]
    public void UpdatedNoticeIsShownOncePerNewTag(string installed, string recorded, string expected)
    {
        Assert.That(Call("DecideUpdatedNotice", installed, recorded).ToString(), Is.EqualTo(expected));
    }

    [Test]
    public void NoticeTagIsWrittenAndReadBesideTheVersionFile()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "odc-forkupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);

        try
        {
            Assert.That(Call("ReadNoticeTag", runtime), Is.Null);
            Assert.That(Call("WriteNoticeTag", runtime, "fork-v53", Path.Combine(runtime, "logs")), Is.True);
            Assert.That(File.Exists(Path.Combine(runtime, "fork-notes-shown.txt")), Is.True);
            Assert.That(Call("ReadNoticeTag", runtime), Is.EqualTo("fork-v53"));
        }
        finally
        {
            Directory.Delete(runtime, true);
        }
    }

    [Test]
    public void UpdateTipListsTheFirstNotesAndCountsTheRest()
    {
        MethodInfo tip = Launcher.GetType("OfflineDaoc.Launcher.MainForm")!.GetMethod("UpdateTip", HiddenStatic)!;
        string[] notes = { "One.", "Two.", "Three.", "Four.", "Five.", "Six.", "Seven." };

        var plain = (string)tip.Invoke(null, new object[] { "fork-v53", null, Array.Empty<string>() })!;
        var listed = (string)tip.Invoke(null, new object[] { "fork-v53", "fork-v52", notes })!;

        Assert.That(plain, Does.StartWith("fork-v53 available (you have an unversioned server)."));
        Assert.That(plain, Does.Not.Contain("What's new"));
        Assert.That(listed, Does.Contain("you have fork-v52"));
        Assert.That(listed, Does.Contain("What's new:"));
        Assert.That(listed, Does.Contain("Five."));
        Assert.That(listed, Does.Not.Contain("Six."));
        Assert.That(listed, Does.Contain("and 2 more"));
    }

    [Test]
    public void InstallerCommandLineQuotesPathsWithSpacesForCmd()
    {
        string script = @"C:\Users\Some One\AppData\Local\Temp\OfflineDAoC-fork-update\fork-v2\INSTALL FORK UPDATE.cmd";

        Assert.That(Call("InstallerArguments", script, @"D:\My Games\playable-v0.37\", false),
            Is.EqualTo("/d /s /c \"\"" + script + "\" \"D:\\My Games\\playable-v0.37\" /auto\""));
        Assert.That(Call("InstallerArguments", script, @"D:\My Games\playable-v0.37", true),
            Is.EqualTo("/d /s /c \"\"" + script + "\" \"D:\\My Games\\playable-v0.37\" /auto /newclient\""));
    }
}
