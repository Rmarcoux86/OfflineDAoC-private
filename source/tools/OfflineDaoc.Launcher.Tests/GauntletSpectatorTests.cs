using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// Gauntlet spectators in the launcher: the look snapshot built from a character's database rows, which characters the
/// SUBMIT AS SPECTATOR button offers, and the pre-filled issue (its JSON block and its length). No database, no network.
/// </summary>
public sealed class GauntletSpectatorTests
{
    private static readonly Assembly Launcher = Assembly.Load("OfflineDAoC");
    private static readonly Type Spectator = Launcher.GetType("OfflineDaoc.Launcher.GauntletSpectator")!;
    private static readonly Type HallOfFame = Launcher.GetType("OfflineDaoc.Launcher.HallOfFame")!;
    private static readonly Type ItemType = Launcher.GetType("OfflineDaoc.Launcher.GauntletSpectator+Item")!;
    private static readonly BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static object Call(string method, params object[] arguments) =>
        Spectator.GetMethod(method, HiddenStatic)!.Invoke(null, arguments)!;

    private static object Value(object item, string property) => item.GetType().GetProperty(property)!.GetValue(item)!;

    private static List<object> Items(object list) => ((IEnumerable)list).Cast<object>().ToList();

    private static object Item(int slot, int model, int color = 0, int effect = 0, int extension = 0) =>
        Activator.CreateInstance(ItemType, slot, model, color, effect, extension)!;

    private static object Worn(params object[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ItemType))!;

        foreach (object item in items)
        {
            list.Add(item);
        }

        return list;
    }

    private static object Build(object worn, int currentModel = 4130, int creationModel = 4130, bool hoodUp = false, bool helmHidden = false,
        bool cloakHidden = false, int activeWeaponSlot = 0, string id = "char-1", string name = "Aldric") =>
        Call("Build", id, name, 2, 1, 0, 1, currentModel, creationModel, hoodUp, helmHidden, cloakHidden, activeWeaponSlot, worn);

    private static object Entries(params string[] entries) =>
        HallOfFame.GetMethod("Parse", HiddenStatic)!.Invoke(null, ["{ \"entries\": [" + string.Join(",", entries) + "] }"])!;

    private static string Win(string characterId, string character, string endedUtc, bool gauntletCleared) =>
        $$"""{ "id": "{{characterId}}:{{endedUtc}}", "character": "{{character}}", "className": "Hero", "realm": "Hibernia", "level": 50, "endedUtc": "{{endedUtc}}", "gauntletCleared": {{(gauntletCleared ? "true" : "false")}} }""";

    [Test]
    public void SnapshotKeepsOneVisibleItemPerSlotWithAModelInSlotOrder()
    {
        object snapshot = Build(Worn(
            Item(25, 1003, 43, 0, 4),
            Item(10, 903, 0, 50),
            Item(10, 999),
            Item(24, 101),
            Item(36, 102),
            Item(22, 0),
            Item(26, 559, 12, -3, 300),
            Item(12, 932, 7)));
        List<object> items = Items(Value(snapshot, "Items"));

        Assert.That(items.Select(item => Value(item, "Slot")), Is.EqualTo(new[] { 10, 12, 25, 26 }), "jewelry, rings and model 0 are left out");
        Assert.That(Value(items[0], "Model"), Is.EqualTo(903), "the first item of a slot is kept");
        Assert.That(Value(items[0], "Effect"), Is.EqualTo(50));
        Assert.That(Value(items[2], "Color"), Is.EqualTo(43));
        Assert.That(Value(items[2], "Extension"), Is.EqualTo(4));
        Assert.That(Value(items[3], "Effect"), Is.EqualTo(0), "a negative glow becomes none");
        Assert.That(Value(items[3], "Extension"), Is.EqualTo(255), "an extension is one byte");
    }

    [Test]
    public void HiddenHelmAndCloakAreLeftOutAndTheHoodIsKept()
    {
        object worn = Worn(Item(21, 438), Item(26, 559), Item(25, 1003));

        Assert.That(Items(Value(Build(worn, helmHidden: true), "Items")).Select(item => Value(item, "Slot")), Is.EqualTo(new[] { 25, 26 }));
        Assert.That(Items(Value(Build(worn, cloakHidden: true), "Items")).Select(item => Value(item, "Slot")), Is.EqualTo(new[] { 21, 25 }));
        Assert.That(Value(Build(worn, hoodUp: true), "HoodUp"), Is.True);
        Assert.That(Value(Build(worn), "HoodUp"), Is.False);
    }

    [Test]
    public void ModelFallsBackToTheCreationModelAndTheQuiverBitsAreDropped()
    {
        Assert.That(Value(Build(Worn(), currentModel: 0, creationModel: 6454), "Model"), Is.EqualTo(6454));
        Assert.That(Value(Build(Worn(), currentModel: 4398, creationModel: 6454), "Model"), Is.EqualTo(4398));
        Assert.That(Value(Build(Worn(), activeWeaponSlot: 0x21), "ActiveWeapon"), Is.EqualTo(1));
        Assert.That(Value(Build(Worn(), activeWeaponSlot: 0x12), "ActiveWeapon"), Is.EqualTo(2));
        Assert.That(Value(Build(Worn(), activeWeaponSlot: 0x0F), "ActiveWeapon"), Is.EqualTo(0), "an unknown slot is one-hand");
    }

    [Test]
    public void OnlyGauntletClearedCharactersNotSubmittedYetAreOfferedOncePerCharacterNewestFirst()
    {
        object local = Entries(
            Win("a", "Older", "2026-10-01T10:00:00Z", true),
            Win("b", "NoGauntlet", "2026-10-24T10:00:00Z", false),
            Win("c", "Submitted", "2026-10-23T10:00:00Z", true),
            Win("d", "Newest", "2026-10-25T10:00:00Z", true),
            Win("a", "OlderAgain", "2026-10-20T10:00:00Z", true));
        var submitted = new HashSet<string> { "c" };

        List<object> candidates = Items(Call("Candidates", local, submitted));

        Assert.That(candidates.Select(entry => Value(entry, "Character")), Is.EqualTo(new[] { "Newest", "OlderAgain" }));
        Assert.That(Call("CharacterIdOf", candidates[1]), Is.EqualTo("a"));
    }

    [Test]
    public void IssueUrlCarriesTheSnapshotAsAJsonBlockTheWorkflowAccepts()
    {
        object snapshot = Build(Worn(Item(10, 903, 0, 50), Item(26, 559, 12)), hoodUp: true, activeWeaponSlot: 1);

        string url = (string)Call("IssueUrl", "Owner/Repo", snapshot);

        Assert.That(url, Does.StartWith("https://github.com/Owner/Repo/issues/new?title=%5BGauntlet%20Champion%5D%20Aldric&body="));
        Assert.That(url.Split('&'), Has.Length.EqualTo(2), "only the one separator between title and body");

        string body = Uri.UnescapeDataString(url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Match block = Regex.Match(body, "```json\\n(.*?)\\n```", RegexOptions.Singleline);
        Assert.That(block.Success, Is.True, body);

        using JsonDocument document = JsonDocument.Parse(block.Groups[1].Value);
        JsonElement root = document.RootElement;
        Assert.That(root.GetProperty("id").GetString(), Is.EqualTo("char-1"));
        Assert.That(root.GetProperty("name").GetString(), Is.EqualTo("Aldric"));
        Assert.That(root.GetProperty("classId").GetInt32(), Is.EqualTo(2));
        Assert.That(root.GetProperty("race").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("gender").GetInt32(), Is.EqualTo(0));
        Assert.That(root.GetProperty("realm").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("model").GetInt32(), Is.EqualTo(4130));
        Assert.That(root.GetProperty("hoodUp").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("activeWeapon").GetInt32(), Is.EqualTo(1));

        JsonElement[] items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.That(items, Has.Length.EqualTo(2));
        Assert.That(items[0].GetProperty("slot").GetInt32(), Is.EqualTo(10));
        Assert.That(items[0].GetProperty("effect").GetInt32(), Is.EqualTo(50));
        Assert.That(items[1].GetProperty("model").GetInt32(), Is.EqualTo(559));
        Assert.That(items[1].GetProperty("color").GetInt32(), Is.EqualTo(12));
        Assert.That(items[1].GetProperty("extension").GetInt32(), Is.EqualTo(0));
    }

    [Test]
    public void AFullyDressedSnapshotStaysWellUnderTheIssueUrlLimit()
    {
        int[] slots = (int[])Spectator.GetField("VisibleSlots", HiddenStatic)!.GetValue(null)!;
        object worn = Worn(slots.Select(slot => Item(slot, int.MaxValue, int.MaxValue, int.MaxValue, 255)).ToArray());
        object snapshot = Build(worn, currentModel: int.MaxValue, hoodUp: true, activeWeaponSlot: 2, id: new string('x', 128), name: new string('W', 32));

        string url = (string)Call("IssueUrl", "Rmarcoux86/OfflineDAoC-private", snapshot);
        int limit = (int)Spectator.GetField("MaxIssueUrlLength", HiddenStatic)!.GetValue(null)!;

        Assert.That(Items(Value(snapshot, "Items")), Has.Count.EqualTo(slots.Length));
        Assert.That(url.Length, Is.LessThan(limit / 2), $"the worst-case link is {url.Length} characters");
    }
}
