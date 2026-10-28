using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// The Hall of Fame's pure logic: reading the shared hall-of-fame.json, merging it with this PC's wins (shared wins on
/// the same id), newest-first order, played time, the pre-filled issue URL and the "already shared" filter. No network.
/// </summary>
public sealed class HallOfFameTests
{
    private static readonly Assembly Launcher = Assembly.Load("OfflineDAoC");
    private static readonly Type HallOfFame = Launcher.GetType("OfflineDaoc.Launcher.HallOfFame")!;
    private static readonly BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static object Call(string method, params object[] arguments) =>
        HallOfFame.GetMethod(method, HiddenStatic)!.Invoke(null, arguments);

    private static object Value(object item, string property) => item.GetType().GetProperty(property)!.GetValue(item);

    // Parsed entries (a List of the internal Entry record), or null.
    private static object Parse(string json) => Call("Parse", json);

    private static object Entries(params string[] entries) => Parse("{ \"entries\": [" + string.Join(",", entries) + "] }")!;

    private static string Win(string id, string character, string endedUtc, string extra = "") =>
        $$"""{ "id": "{{id}}", "character": "{{character}}", "className": "Hero", "realm": "Hibernia", "level": 50, "endedUtc": "{{endedUtc}}"{{extra}} }""";

    private static List<object> Items(object list) => ((IEnumerable)list).Cast<object>().ToList();

    [Test]
    public void SharedFileIsReadWithEveryField()
    {
        object entries = Parse("""
            { "entries": [
              { "id": "abc:2026-10-25T18:00:00.0000000Z", "character": "Aldric", "className": "Armsman", "race": "Briton",
                "realm": "Albion", "level": 50, "seed": 1234, "playedSeconds": 45296, "endedUtc": "2026-10-25T18:00:00.0000000Z",
                "kills": 812, "bossesKilled": 9, "uniques": 3, "realmPoints": 15000, "gauntletCleared": true,
                "player": "someone", "submittedUtc": "2026-10-26T09:00:00Z", "futureField": [1, 2] }
            ] }
            """);
        List<object> items = Items(entries);

        Assert.That(items, Has.Count.EqualTo(1));
        object entry = items[0];
        Assert.That(Value(entry, "Id"), Is.EqualTo("abc:2026-10-25T18:00:00.0000000Z"));
        Assert.That(Value(entry, "Character"), Is.EqualTo("Aldric"));
        Assert.That(Value(entry, "ClassName"), Is.EqualTo("Armsman"));
        Assert.That(Value(entry, "Race"), Is.EqualTo("Briton"));
        Assert.That(Value(entry, "Realm"), Is.EqualTo("Albion"));
        Assert.That(Value(entry, "Level"), Is.EqualTo(50));
        Assert.That(Value(entry, "Seed"), Is.EqualTo(1234));
        Assert.That(Value(entry, "PlayedSeconds"), Is.EqualTo(45296L));
        Assert.That(Value(entry, "Kills"), Is.EqualTo(812));
        Assert.That(Value(entry, "BossesKilled"), Is.EqualTo(9));
        Assert.That(Value(entry, "Uniques"), Is.EqualTo(3));
        Assert.That(Value(entry, "RealmPoints"), Is.EqualTo(15000L));
        Assert.That(Value(entry, "GauntletCleared"), Is.True);
        Assert.That(Value(entry, "Player"), Is.EqualTo("someone"));
    }

    [Test]
    public void EntriesWithoutIdOrCharacterAndRepeatedIdsAreSkipped()
    {
        object entries = Entries(
            Win("a:1", "First", "2026-10-01T00:00:00Z"),
            Win("a:1", "Copy", "2026-10-01T00:00:00Z"),
            """{ "id": "b:1", "endedUtc": "2026-10-01T00:00:00Z" }""",
            """{ "character": "NoId" }""",
            "42");

        Assert.That(Items(entries).Select(entry => Value(entry, "Character")), Is.EqualTo(new[] { "First" }));
    }

    [TestCase("""[]""")]
    [TestCase("""{ "entries": {} }""")]
    [TestCase("""{ }""")]
    [TestCase("""not json""")]
    public void TextThatIsNotAHallOfFameFileGivesNull(string json)
    {
        Assert.That(Parse(json), Is.Null);
    }

    [Test]
    public void MergeKeepsTheSharedEntryOnTheSameIdAndMarksLocalAndSentWins()
    {
        object shared = Entries(Win("c1:2026-10-20T10:00:00Z", "Shared", "2026-10-20T10:00:00Z", ", \"player\": \"friend\""));
        object local = Entries(
            Win("c1:2026-10-20T10:00:00Z", "LocalCopy", "2026-10-20T10:00:00Z"),
            Win("c2:2026-10-22T10:00:00Z", "Newest", "2026-10-22T10:00:00Z"),
            Win("c3:2026-10-18T10:00:00Z", "Sent", "2026-10-18T10:00:00Z"));
        var sent = new HashSet<string> { "c3:2026-10-18T10:00:00Z" };

        List<object> rows = Items(Call("Merge", shared, local, sent));

        Assert.That(rows.Select(row => Value(Value(row, "Entry"), "Character")), Is.EqualTo(new[] { "Newest", "Shared", "Sent" }));
        Assert.That(rows.Select(row => Value(row, "State").ToString()), Is.EqualTo(new[] { "Local", "Shared", "Sent" }));
        Assert.That(Value(Value(rows[1], "Entry"), "Player"), Is.EqualTo("friend"));
    }

    [Test]
    public void RowsAreNewestFirstAndAnUnreadableTimeSortsLast()
    {
        object shared = Entries(
            Win("a:x", "Broken", "not a time"),
            Win("b:1", "Older", "2026-09-01T08:00:00.0000000Z"),
            Win("c:1", "Newer", "2026-10-01T08:00:00.0000000Z"),
            Win("d:1", "SameDayLater", "2026-10-01T09:30:00+00:00"));

        List<object> rows = Items(Call("Merge", shared, Entries(), new HashSet<string>()));

        Assert.That(rows.Select(row => Value(Value(row, "Entry"), "Character")), Is.EqualTo(new[] { "SameDayLater", "Newer", "Older", "Broken" }));
    }

    [Test]
    public void ShareOffersOnlyLocalWinsThatAreNeitherListedNorSent()
    {
        object shared = Entries(Win("listed:1", "Listed", "2026-10-20T10:00:00Z"));
        object local = Entries(
            Win("listed:1", "Listed", "2026-10-20T10:00:00Z"),
            Win("sent:1", "Sent", "2026-10-23T10:00:00Z"),
            Win("old:1", "Old", "2026-10-10T10:00:00Z"),
            Win("new:1", "New", "2026-10-21T10:00:00Z"));
        var sent = new HashSet<string>((IEnumerable<string>)Call("ParseSentIds", "sent:1\r\n\r\n  \n"));

        List<object> unshared = Items(Call("Unshared", local, shared, sent));

        Assert.That(unshared.Select(entry => Value(entry, "Character")), Is.EqualTo(new[] { "New", "Old" }));
    }

    [TestCase(0L, "0:00")]
    [TestCase(59L, "0:00")]
    [TestCase(60L, "0:01")]
    [TestCase(3725L, "1:02")]
    [TestCase(45296L, "12:34")]
    [TestCase(90061L, "25:01")]
    [TestCase(-5L, "0:00")]
    public void PlayedTimeIsHoursAndMinutes(long seconds, string expected)
    {
        Assert.That(Call("FormatPlayedTime", seconds), Is.EqualTo(expected));
    }

    [Test]
    public void IssueUrlIsEncodedAndCarriesTheWinAsAJsonBlock()
    {
        object entry = Items(Entries(Win("id-1:2026-10-25T18:00:00.0000000Z", "Aldric", "2026-10-25T18:00:00.0000000Z",
            ", \"race\": \"Celt\", \"seed\": 77, \"playedSeconds\": 3600, \"gauntletCleared\": true")))[0];

        string url = (string)Call("IssueUrl", "Owner/Repo", entry);

        Assert.That(url, Does.StartWith("https://github.com/Owner/Repo/issues/new?title=%5BHall%20of%20Fame%5D%20Aldric&body="));
        Assert.That(url, Does.Not.Contain(" ").And.Not.Contain("\n").And.Not.Contain("#").And.Not.Contain("\""));
        Assert.That(url.Split('&'), Has.Length.EqualTo(2), "only the one separator between title and body");

        string body = Uri.UnescapeDataString(url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Match block = Regex.Match(body, "```json\\n(.*?)\\n```", RegexOptions.Singleline);
        Assert.That(block.Success, Is.True, body);

        object parsed = Items(Parse("{ \"entries\": [" + block.Groups[1].Value + "] }"))[0];
        Assert.That(Value(parsed, "Id"), Is.EqualTo("id-1:2026-10-25T18:00:00.0000000Z"));
        Assert.That(Value(parsed, "Character"), Is.EqualTo("Aldric"));
        Assert.That(Value(parsed, "Race"), Is.EqualTo("Celt"));
        Assert.That(Value(parsed, "Seed"), Is.EqualTo(77));
        Assert.That(Value(parsed, "PlayedSeconds"), Is.EqualTo(3600L));
        Assert.That(Value(parsed, "GauntletCleared"), Is.True);
        Assert.That(block.Groups[1].Value, Does.Not.Contain("player").And.Not.Contain("\r"));
    }

    [Test]
    public void BoonsAreSharedAndShown()
    {
        object entry = Items(Entries(Win("id-2:2026-10-27T18:00:00.0000000Z", "Brenna", "2026-10-27T18:00:00.0000000Z",
            ", \"boons\": [\"Marked Prey\", \"Hunter's Instinct\"]")))[0];

        Assert.That(Value(entry, "Boons"), Is.EqualTo(new[] { "Marked Prey", "Hunter's Instinct" }));
        Assert.That(Call("BoonsLine", entry), Is.EqualTo("Boons: Marked Prey, Hunter's Instinct"));

        string json = (string)Call("SubmissionJson", entry);
        object parsed = Items(Parse("{ \"entries\": [" + json + "] }"))[0];
        Assert.That(Value(parsed, "Boons"), Is.EqualTo(new[] { "Marked Prey", "Hunter's Instinct" }));

        object none = Items(Entries(Win("id-3:2026-10-27T18:00:00.0000000Z", "Corin", "2026-10-27T18:00:00.0000000Z")))[0];
        Assert.That(Call("BoonsLine", none), Is.Null);
        Assert.That((string)Call("SubmissionJson", none), Does.Not.Contain("boons"));
    }

    [TestCase("""["marked_prey","phoenix","second_skin","some_new_one"]""", "Marked Prey|Phoenix Feather|Second Skin|Some New One")]
    [TestCase("", "")]
    [TestCase("not json", "")]
    [TestCase("""{"a":1}""", "")]
    public void BoonIdsBecomeNames(string boonsJson, string expected)
    {
        var names = (IEnumerable<string>)Call("BoonNamesFromJson", boonsJson);

        Assert.That(string.Join("|", names), Is.EqualTo(expected));
    }

    [TestCase("""{"Round":5,"Depth":5}""", true)]
    [TestCase("""{"round":5,"depth":7}""", true)]
    [TestCase("""{"Round":5,"Depth":4}""", false)]
    [TestCase("""{"Round":1}""", false)]
    [TestCase("", false)]
    [TestCase("garbage", false)]
    public void GauntletCountsAsClearedAtFullDepth(string json, bool expected)
    {
        Assert.That(Call("GauntletCleared", json), Is.EqualTo(expected));
    }

    [Test]
    public void SummaryAndDetailsReadLikeTheList()
    {
        object entry = Items(Entries(Win("a:1", "Aldric", "2026-10-25T18:00:00Z", ", \"race\": \"Celt\", \"seed\": 9, \"playedSeconds\": 7260, \"player\": \"friend\"")))[0];
        List<object> rows = Items(Call("Merge", Entries(Win("a:1", "Aldric", "2026-10-25T18:00:00Z",
            ", \"race\": \"Celt\", \"seed\": 9, \"playedSeconds\": 7260, \"player\": \"friend\"")), Entries(), new HashSet<string>()));

        Assert.That(Call("Summary", entry), Is.EqualTo("level 50 Celt Hero, Hibernia"));
        Assert.That((string)Call("Details", rows[0]), Does.StartWith("by friend · 2:01 · seed 9 · "));
    }

    [Test]
    public void ShareOffersUnsharedWinsAndWinsWhoseCharacterIsNotASpectatorYet()
    {
        object shared = Entries(Win("a:1", "Alpha", "2026-10-20T10:00:00Z"));
        object local = Entries(
            Win("a:1", "Alpha", "2026-10-20T10:00:00Z"),
            Win("b:1", "Beta", "2026-10-21T10:00:00Z"));

        List<object> spectatorMissing = Items(Call("ToShare", local, shared, new HashSet<string> { "b:1" }, new HashSet<string>()));
        List<object> allDone = Items(Call("ToShare", local, shared, new HashSet<string> { "b:1" }, new HashSet<string> { "a", "b" }));

        Assert.That(spectatorMissing.Select(entry => Value(entry, "Character")), Is.EqualTo(new[] { "Beta", "Alpha" }));
        Assert.That(allDone, Is.Empty);
    }

    [Test]
    public void TheWinCarriesTheSpectatorLook()
    {
        object win = Items(Entries(Win("a:2026-10-20T10:00:00Z", "Alpha", "2026-10-20T10:00:00Z")))[0];
        string body = (string)Call("IssueBodyWithSpectator", win, "{\"id\":\"a\",\"name\":\"Alpha\",\"classId\":1,\"items\":[]}");
        string alone = (string)Call("IssueBodyWithSpectator", win, null!);

        Assert.That(body, Does.Contain("\"spectator\":{\"id\":\"a\",\"name\":\"Alpha\""));
        Assert.That(alone, Does.Not.Contain("spectator\""));
    }
}
