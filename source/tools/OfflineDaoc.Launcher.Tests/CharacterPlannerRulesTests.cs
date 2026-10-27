using System.Reflection;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// The Character Planner's training rules against numbers worked out from the server code: GamePlayer.VerifySpecPoints
/// (spec points start at -1, plus the level for levels 1-5, multiplier * level / 10 after, and multiplier * (level - 1) / 20
/// more above 40), the spec cost (level * (level + 1) - 2) / 2, GamePlayer.GetAutoTrainPoints (level / 4 free levels, at
/// least 1) and AbstractServerRules.GetPlayerRealmPointsTotal (the realm level, at least 1 past character level 19).
/// </summary>
public sealed class CharacterPlannerRulesTests
{
    private static readonly Assembly Launcher = Assembly.Load("OfflineDAoC");
    private static readonly Type Rules = Launcher.GetType("OfflineDaoc.Launcher.CharacterPlannerRules")!;

    private static int Call(string method, params object[] arguments) =>
        (int)Rules.GetMethod(method, arguments.Select(argument => argument.GetType()).ToArray())!.Invoke(null, arguments)!;

    private static int Constant(string name) => (int)Rules.GetField(name)!.GetValue(null)!;

    [TestCase(20, 0, 0)]
    [TestCase(20, 1, 0)]
    [TestCase(20, 2, 2)]
    [TestCase(20, 5, 14)]
    [TestCase(20, 6, 26)]
    [TestCase(20, 10, 94)]
    [TestCase(20, 20, 404)]
    [TestCase(20, 40, 1624)]
    [TestCase(20, 41, 1746)]
    [TestCase(20, 50, 2979)]
    [TestCase(10, 6, 20)]
    [TestCase(10, 50, 1494)]
    [TestCase(15, 50, 2223)]
    [TestCase(22, 50, 3253)]
    [TestCase(25, 50, 3706)]
    public void SpecPointsFollowVerifySpecPoints(int multiplier, int level, int expected)
    {
        Assert.That(Call("SpecPointsAtLevel", multiplier, level), Is.EqualTo(expected));
    }

    [TestCase(0, 0)]
    [TestCase(1, 0)]
    [TestCase(2, 2)]
    [TestCase(3, 5)]
    [TestCase(10, 54)]
    [TestCase(12, 77)]
    [TestCase(50, 1274)]
    public void SpecCostIsTwoPlusThreeUpToTheLevel(int specLevel, int expected)
    {
        Assert.That(Call("SpecCost", specLevel), Is.EqualTo(expected));
    }

    [TestCase(1, 1)]
    [TestCase(3, 1)]
    [TestCase(4, 1)]
    [TestCase(7, 1)]
    [TestCase(8, 2)]
    [TestCase(40, 10)]
    [TestCase(50, 12)]
    public void AutotrainGivesAQuarterOfTheLevelAndAtLeastOne(int level, int expected)
    {
        Assert.That(Call("AutotrainLevel", level), Is.EqualTo(expected));
    }

    [TestCase(50, false, 50, 1274)]
    [TestCase(50, true, 50, 1197)]
    [TestCase(12, true, 50, 0)]
    [TestCase(10, true, 50, 0)]
    [TestCase(13, true, 50, 13)]
    [TestCase(10, false, 50, 54)]
    [TestCase(5, true, 3, 14)]
    public void AutotrainedLinesOnlyPayAboveTheFreeLevels(int specLevel, bool autotrain, int level, int expected)
    {
        Assert.That(Call("SpecCost", specLevel, autotrain, level), Is.EqualTo(expected));
    }

    [TestCase(1, 0, 0)]
    [TestCase(1, 5, 5)]
    [TestCase(2, 0, 10)]
    [TestCase(5, 3, 43)]
    [TestCase(14, 0, 130)]
    public void RealmPointsAreOnePerRealmLevel(int rank, int level, int expected)
    {
        Assert.That(Call("RealmPointsAtRank", rank, level), Is.EqualTo(expected));
    }

    [TestCase(1, 0, 19, 0)]
    [TestCase(1, 0, 20, 1)]
    [TestCase(1, 0, 50, 1)]
    [TestCase(1, 4, 50, 4)]
    [TestCase(3, 2, 50, 22)]
    public void RealmPointsAreAtLeastOnePastLevelNineteen(int rank, int level, int characterLevel, int expected)
    {
        Assert.That(Call("RealmPointsAtRank", rank, level, characterLevel), Is.EqualTo(expected));
    }

    [Test]
    public void LimitsMatchTheServerTables()
    {
        Assert.That(Constant("MaxLevel"), Is.EqualTo(50));
        Assert.That(Constant("MaxRealmRank"), Is.EqualTo(14));
        Assert.That(Constant("MaxRealmLevel"), Is.EqualTo(130));
        Assert.That(Call("RealmPointsAtRank", Constant("MaxRealmRank"), 0), Is.EqualTo(Constant("MaxRealmLevel")));
    }
}
