using System.Reflection;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// The fork's self-update: reading GitHub's latest-release answer, deciding whether an update is offered, the
/// repository override file and the installer's command line. No network is used.
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
