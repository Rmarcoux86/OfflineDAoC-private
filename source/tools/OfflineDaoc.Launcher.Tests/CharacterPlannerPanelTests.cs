using System.Reflection;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// The Character Planner panel's texts and colours (CharacterPlannerFormat), without a window: the points labels (red
/// when over budget), the autotrain floor beside a line's name, the cost column, realm ability levels and costs,
/// prerequisite text and the unlock highlighting (bought rows in the text colour, the rest muted).
/// </summary>
public sealed class CharacterPlannerPanelTests
{
    private static readonly Assembly Launcher = Assembly.Load("OfflineDAoC");
    private static readonly Type Format = Launcher.GetType("OfflineDaoc.Launcher.CharacterPlannerFormat")!;
    private static readonly Type Theme = Launcher.GetType("OfflineDaoc.Launcher.DaocTheme")!;

    private static object Call(string method, params object[] arguments) =>
        Format.GetMethod(method, BindingFlags.Static | BindingFlags.Public)!.Invoke(null, arguments);

    private static Color ThemeColor(string name) =>
        (Color)Theme.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;

    [Test]
    public void PointsLabelsShowUsedOverTotalAndTurnRedWhenOverBudget()
    {
        Assert.That(Call("PointsText", "Spec points", 120, 154), Is.EqualTo("Spec points: 120/154"));
        Assert.That(Call("PointsText", "RA points", 0, 43), Is.EqualTo("RA points: 0/43"));
        Assert.That(Call("PointsColor", 154, 154), Is.EqualTo(ThemeColor("GoldLight")));
        Assert.That(Call("PointsColor", 155, 154), Is.EqualTo(ThemeColor("Danger")));
    }

    [Test]
    public void AutotrainedLinesShowTheirFreeLevel()
    {
        Assert.That(Call("SpecNameText", "Polearm", 12), Is.EqualTo("Polearm (auto 12)"));
        Assert.That(Call("SpecNameText", "Slash", null), Is.EqualTo("Slash"));
    }

    [TestCase(true, true, 1, false, 43, "43 pts")]
    [TestCase(true, true, 1, true, 0, "max")]
    [TestCase(false, true, 1, false, 0, "fixed")]
    [TestCase(true, false, 20, false, 0, "L20")]
    public void SpecCostColumn(bool trainable, bool acquired, int levelAcquired, bool atMaximum, int nextCost, string expected)
    {
        Assert.That(Call("SpecCostText", trainable, acquired, levelAcquired, atMaximum, nextCost), Is.EqualTo(expected));
    }

    [Test]
    public void RealmAbilityLevelAndCost()
    {
        Assert.That(Call("RealmAbilityLevelText", 2, 5), Is.EqualTo("2/5"));
        Assert.That(Call("RealmAbilityCostText", 2, 5, 6), Is.EqualTo("6 pts"));
        Assert.That(Call("RealmAbilityCostText", 5, 5, 0), Is.EqualTo("max"));
        Assert.That(Call("RealmAbilityCostText", 0, 0, 0), Is.EqualTo("n/a"), "no cost table: it cannot be bought");
    }

    [Test]
    public void BaselineSpellsCompareWithTheCharacterLevel()
    {
        var suffix = (string)Launcher.GetType("OfflineDaoc.Launcher.CharacterPlannerData")!.GetField("BaselineSuffix")!.GetValue(null)!;

        Assert.That(Call("UnlocksAtCharacterLevel", "Polearm Buffs" + suffix), Is.True);
        Assert.That(Call("UnlocksAtCharacterLevel", "Polearm Buffs"), Is.False);
    }

    [TestCase(1, 0, false)]
    [TestCase(20, 19, false)]
    [TestCase(20, 20, true)]
    [TestCase(4, 50, true)]
    public void RowsAtOrBelowTheTrainedLevelAreHighlighted(int unlockLevel, int trainedLevel, bool unlocked)
    {
        Assert.That(Call("IsUnlocked", unlockLevel, trainedLevel), Is.EqualTo(unlocked));
        Assert.That(Call("UnlockColor", unlockLevel, trainedLevel), Is.EqualTo(ThemeColor(unlocked ? "Text" : "Muted")));
    }

    [Test]
    public void PrerequisitesAreNamed()
    {
        Func<string, string> nameOf = key => key == "determination" ? "Determination" : key;

        Assert.That(Call("PrerequisiteText", new[] { "determination:2", "purge" }, nameOf), Is.EqualTo("Requires Determination 2, purge 1"));
        Assert.That(Call("PrerequisiteText", Array.Empty<string>(), nameOf), Is.EqualTo(string.Empty));
    }

    [Test]
    public void TheEmptyAndLoadingMessages()
    {
        Assert.That(Format.GetField("EmptyText")!.GetValue(null), Is.EqualTo("Character data not found — is the game installed?"));
        Assert.That(Format.GetField("LoadingText")!.GetValue(null), Does.StartWith("Loading"));
    }
}
