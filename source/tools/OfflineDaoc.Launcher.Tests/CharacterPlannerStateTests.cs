using System.Reflection;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// The Character Planner's model: spec point budget and costs (autotrained lines start at their free level and only pay
/// above it), the character level cap, realm ability points, maximum levels and prerequisites (a prerequisite cannot be
/// lowered while something needs it), Reset/Clear and the clipboard summary. Expected numbers come from
/// CharacterPlannerRules itself, so the tests hold if the rule bodies are tuned. The launcher's types are internal, so
/// everything goes through reflection; the class, lines and realm abilities are handed to SelectClass directly (an
/// internal interface cannot be implemented from this assembly).
/// </summary>
public sealed class CharacterPlannerStateTests
{
    private static readonly Assembly Launcher = Assembly.Load("OfflineDAoC");
    private static readonly Type StateType = Launcher.GetType("OfflineDaoc.Launcher.CharacterPlannerState")!;
    private static readonly Type RulesType = Launcher.GetType("OfflineDaoc.Launcher.CharacterPlannerRules")!;
    private static readonly Type ClassType = Launcher.GetType("OfflineDaoc.Launcher.PlannerClass")!;
    private static readonly Type SpecType = Launcher.GetType("OfflineDaoc.Launcher.PlannerSpec")!;
    private static readonly Type RealmAbilityType = Launcher.GetType("OfflineDaoc.Launcher.PlannerRealmAbility")!;
    private const string Dash = " — ";

    private static int Rule(string method, params object[] arguments) =>
        (int)RulesType.GetMethods().Single(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length).Invoke(null, arguments)!;

    private static object Armsman(int specPointsMultiplier = 20) =>
        Activator.CreateInstance(ClassType, 2, "Armsman", "Albion", "Fighter", specPointsMultiplier, new[] { "polearm" })!;

    private static object Spec(string key, string name, int levelAcquired = 1, bool trainable = true) =>
        Activator.CreateInstance(SpecType, key, name, levelAcquired, trainable)!;

    private static object Ability(string key, string name, bool passive, int[] costs, params string[] prerequisites) =>
        Activator.CreateInstance(RealmAbilityType, key, name, name + " description", passive, costs, prerequisites)!;

    // A typed array (PlannerSpec[] or PlannerRealmAbility[]), which is the IReadOnlyList SelectClass takes.
    private static Array Typed(Type type, params object[] items)
    {
        Array array = Array.CreateInstance(type, items.Length);

        for (int index = 0; index < items.Length; index++)
        {
            array.SetValue(items[index], index);
        }

        return array;
    }

    private static Planner ArmsmanPlanner(int specPointsMultiplier = 20)
    {
        var planner = new Planner();
        planner.Call("SelectClass", Armsman(specPointsMultiplier),
            Typed(SpecType,
                Spec("slash", "Slash"),
                Spec("polearm", "Polearm"),
                Spec("shield", "Shield"),
                Spec("parry", "Parry"),
                Spec("crossbow", "Crossbow", levelAcquired: 10),
                Spec("armsman_base", "Armsman", trainable: false)),
            Typed(RealmAbilityType,
                Ability("determination", "Determination", true, [1, 1, 2, 2, 3]),
                Ability("purge", "Purge", false, [5]),
                Ability("ignore_pain", "Ignore Pain", false, [8], "determination:2"),
                Ability("unreachable", "Unreachable", true, [1], "not_for_this_class:1")));

        return planner;
    }

    /// <summary>A CharacterPlannerState reached by reflection.</summary>
    private sealed class Planner
    {
        private readonly object _state = Activator.CreateInstance(StateType)!;

        public object Call(string method, params object[] arguments) =>
            StateType.GetMethods().Single(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length).Invoke(_state, arguments);

        public T Get<T>(string property) => (T)StateType.GetProperty(property)!.GetValue(_state)!;

        public int Int(string method, params object[] arguments) => (int)Call(method, arguments)!;

        public bool Bool(string method, params object[] arguments) => (bool)Call(method, arguments)!;

        public int Level => Get<int>("Level");
        public int SpecTotal => Get<int>("SpecPointsTotal");
        public int SpecSpent => Get<int>("SpecPointsSpent");
        public int SpecRemaining => Get<int>("SpecPointsRemaining");
        public int RealmTotal => Get<int>("RealmPointsTotal");
        public int RealmSpent => Get<int>("RealmPointsSpent");
        public int RealmRemaining => Get<int>("RealmPointsRemaining");
        public int SpecLevel(string key) => Int("SpecLevel", key);
        public int RealmAbilityLevel(string key) => Int("RealmAbilityLevel", key);
    }

    [Test]
    public void DefaultsAreLevelFiftyRealmRankOneAndNoClass()
    {
        var planner = new Planner();

        Assert.That(planner.Level, Is.EqualTo(50));
        Assert.That(planner.Get<int>("RealmRank"), Is.EqualTo(1));
        Assert.That(planner.Get<int>("RealmLevel"), Is.EqualTo(0));
        Assert.That(planner.Get<object>("Class"), Is.Null);
        Assert.That(planner.SpecTotal, Is.EqualTo(0));
        Assert.That(planner.Call("Summary"), Is.EqualTo("No class selected"));
    }

    [Test]
    public void SelectingAClassStartsEveryLineAtItsFloor()
    {
        Planner planner = ArmsmanPlanner();
        int free = Rule("AutotrainLevel", 50);

        Assert.That(planner.SpecLevel("slash"), Is.EqualTo(1));
        Assert.That(planner.SpecLevel("polearm"), Is.EqualTo(Math.Max(1, free)), "autotrained lines start at their free level");
        Assert.That(planner.Call("AutotrainFloor", "polearm"), Is.EqualTo(free));
        Assert.That(planner.Call("AutotrainFloor", "slash"), Is.Null);
        Assert.That(planner.Bool("IsAutotrained", "POLEARM"), Is.True, "keys are not case sensitive");
        Assert.That(planner.SpecLevel("crossbow"), Is.EqualTo(1));
        Assert.That(planner.SpecSpent, Is.EqualTo(0), "the floors are free");
        Assert.That(planner.SpecTotal, Is.EqualTo(Rule("SpecPointsAtLevel", 20, 50)));
    }

    [Test]
    public void ALineIsNotTrainableBeforeTheLevelItIsLearnedAt()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetLevel", 5);

        Assert.That(planner.Bool("IsAcquired", "crossbow"), Is.False);
        Assert.That(planner.SpecLevel("crossbow"), Is.EqualTo(0));
        Assert.That(planner.Bool("CanRaiseSpec", "crossbow"), Is.False);
        Assert.That(planner.Bool("RaiseSpec", "crossbow"), Is.False);

        planner.Call("SetLevel", 10);

        Assert.That(planner.SpecLevel("crossbow"), Is.EqualTo(1));
    }

    [Test]
    public void RaisingAndLoweringSpendsAndRefundsTheRulesCost()
    {
        Planner planner = ArmsmanPlanner();
        int raiseCost = planner.Int("SpecRaiseCost", "slash");

        Assert.That(planner.Bool("RaiseSpec", "slash"), Is.True);
        Assert.That(planner.SpecLevel("slash"), Is.EqualTo(2));
        Assert.That(planner.SpecSpent, Is.EqualTo(Rule("SpecCost", 2)));
        Assert.That(raiseCost, Is.EqualTo(Rule("SpecCost", 2) - Rule("SpecCost", 1)));
        Assert.That(planner.SpecRemaining, Is.EqualTo(planner.SpecTotal - planner.SpecSpent));

        Assert.That(planner.Bool("LowerSpec", "slash"), Is.True);
        Assert.That(planner.SpecLevel("slash"), Is.EqualTo(1));
        Assert.That(planner.SpecSpent, Is.EqualTo(0));
    }

    [Test]
    public void LinesCannotGoBelowTheirFloor()
    {
        Planner planner = ArmsmanPlanner();

        Assert.That(planner.Bool("CanLowerSpec", "slash"), Is.False);
        Assert.That(planner.Bool("LowerSpec", "slash"), Is.False);
        Assert.That(planner.Bool("LowerSpec", "polearm"), Is.False, "the autotrained free level is the floor");
        Assert.That(planner.Int("SetSpec", "polearm", 0), Is.EqualTo(Math.Max(1, Rule("AutotrainLevel", 50))));
        Assert.That(planner.Int("SetSpec", "slash", -5), Is.EqualTo(1));
    }

    [Test]
    public void AnAutotrainedLinePaysOnlyAboveItsFreeLevel()
    {
        Planner planner = ArmsmanPlanner();

        Assert.That(planner.Int("SetSpec", "polearm", 30), Is.EqualTo(30));
        Assert.That(planner.SpecSpent, Is.EqualTo(Rule("SpecCost", 30, true, 50)));
        Assert.That(planner.Int("SpecPointsIn", "polearm"), Is.EqualTo(Rule("SpecCost", 30, true, 50)));
        Assert.That(planner.SpecSpent, Is.LessThan(Rule("SpecCost", 30)));
    }

    [Test]
    public void ALineCannotPassTheCharacterLevel()
    {
        Planner planner = ArmsmanPlanner(specPointsMultiplier: 200);
        planner.Call("SetLevel", 20);

        Assert.That(planner.Int("SetSpec", "slash", 50), Is.EqualTo(20));
        Assert.That(planner.Bool("CanRaiseSpec", "slash"), Is.False);
        Assert.That(planner.Bool("RaiseSpec", "slash"), Is.False);
        Assert.That(planner.Int("SpecRaiseCost", "slash"), Is.EqualTo(0));
    }

    [Test]
    public void TrainingStopsWhenThePointsRunOut()
    {
        Planner planner = ArmsmanPlanner(specPointsMultiplier: 10);

        foreach (string key in new[] { "slash", "polearm", "shield", "parry" })
        {
            planner.Call("SetSpec", key, 50);
        }

        Assert.That(planner.SpecSpent, Is.LessThanOrEqualTo(planner.SpecTotal));
        Assert.That(planner.SpecRemaining, Is.GreaterThanOrEqualTo(0));

        foreach (string key in new[] { "slash", "polearm", "shield", "parry" })
        {
            if (planner.SpecLevel(key) < 50)
            {
                Assert.That(planner.Bool("CanRaiseSpec", key), Is.False, key);
                Assert.That(planner.Int("SpecRaiseCost", key), Is.GreaterThan(planner.SpecRemaining), key);
            }
        }

        while (planner.Bool("RaiseSpec", "crossbow"))
        {
        }

        Assert.That(planner.SpecSpent, Is.LessThanOrEqualTo(planner.SpecTotal));
    }

    [Test]
    public void ChangingTheLevelClampsLinesAndMovesTheAutotrainFloor()
    {
        Planner planner = ArmsmanPlanner(specPointsMultiplier: 200);
        planner.Call("SetSpec", "slash", 40);
        planner.Call("SetLevel", 30);

        Assert.That(planner.SpecLevel("slash"), Is.EqualTo(30));
        Assert.That(planner.SpecLevel("polearm"), Is.EqualTo(Math.Max(1, Rule("AutotrainLevel", 30))));

        planner.Call("SetLevel", 50);

        Assert.That(planner.SpecLevel("slash"), Is.EqualTo(30), "raising the level keeps what was trained");
        Assert.That(planner.SpecLevel("polearm"), Is.EqualTo(Math.Max(1, Rule("AutotrainLevel", 50))), "the free level rises with the character");

        planner.Call("SetLevel", 99);
        Assert.That(planner.Level, Is.EqualTo(50));
        planner.Call("SetLevel", 0);
        Assert.That(planner.Level, Is.EqualTo(1));
    }

    [Test]
    public void ALineThatIsNotTrainableFollowsTheLevelAndCostsNothing()
    {
        Planner planner = ArmsmanPlanner();

        Assert.That(planner.SpecLevel("armsman_base"), Is.EqualTo(50));
        Assert.That(planner.Bool("CanRaiseSpec", "armsman_base"), Is.False);
        Assert.That(planner.Bool("CanLowerSpec", "armsman_base"), Is.False);
        Assert.That(planner.Int("SetSpec", "armsman_base", 10), Is.EqualTo(50));
        Assert.That(planner.SpecSpent, Is.EqualTo(0));

        planner.Call("SetLevel", 33);

        Assert.That(planner.SpecLevel("armsman_base"), Is.EqualTo(33));
    }

    [Test]
    public void UnknownLinesAreIgnored()
    {
        Planner planner = ArmsmanPlanner();

        Assert.That(planner.Bool("RaiseSpec", "nope"), Is.False);
        Assert.That(planner.Int("SetSpec", "nope", 20), Is.EqualTo(0));
        Assert.That(planner.SpecLevel("nope"), Is.EqualTo(0));
    }

    [Test]
    public void RealmPointsFollowTheRankAndAreClamped()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetRealmRank", 5, 3);

        Assert.That(planner.RealmTotal, Is.EqualTo(Rule("RealmPointsAtRank", 5, 3, 50)));

        planner.Call("SetRealmRank", 20, 15);

        // The server's realm levels end at RR14 L0.
        Assert.That(planner.Get<int>("RealmRank"), Is.EqualTo(14));
        Assert.That(planner.Get<int>("RealmLevel"), Is.EqualTo(0));

        planner.Call("SetRealmRank", 13, 15);

        Assert.That(planner.Get<int>("RealmLevel"), Is.EqualTo(9));

        planner.Call("SetRealmRank", 0, -1);

        Assert.That(planner.Get<int>("RealmRank"), Is.EqualTo(1));
        Assert.That(planner.Get<int>("RealmLevel"), Is.EqualTo(0));
    }

    [Test]
    public void RealmAbilitiesStopAtTheirMaximumLevel()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetRealmRank", 14, 9);

        for (int level = 1; level <= 5; level++)
        {
            Assert.That(planner.Bool("RaiseRealmAbility", "determination"), Is.True, $"level {level}");
        }

        Assert.That(planner.RealmAbilityLevel("determination"), Is.EqualTo(5));
        Assert.That(planner.Bool("CanRaiseRealmAbility", "determination"), Is.False);
        Assert.That(planner.Bool("RaiseRealmAbility", "determination"), Is.False);
        Assert.That(planner.Int("RealmAbilityRaiseCost", "determination"), Is.EqualTo(0));
        Assert.That(planner.RealmSpent, Is.EqualTo(1 + 1 + 2 + 2 + 3));
        Assert.That(planner.RealmRemaining, Is.EqualTo(planner.RealmTotal - 9));
    }

    [Test]
    public void RealmAbilitiesNeedThePoints()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetRealmRank", 1, 0);

        while (planner.Bool("RaiseRealmAbility", "determination"))
        {
        }

        Assert.That(planner.RealmSpent, Is.LessThanOrEqualTo(planner.RealmTotal));

        if (planner.RealmAbilityLevel("determination") < 5)
        {
            Assert.That(planner.Int("RealmAbilityRaiseCost", "determination"), Is.GreaterThan(planner.RealmRemaining));
        }
    }

    [Test]
    public void PrerequisitesMustBeTrainedFirstAndCannotBeLoweredWhileNeeded()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetRealmRank", 14, 9);

        Assert.That(planner.Bool("PrerequisitesMet", "ignore_pain"), Is.False);
        Assert.That(planner.Bool("CanRaiseRealmAbility", "ignore_pain"), Is.False);

        planner.Call("RaiseRealmAbility", "determination");

        Assert.That(planner.Bool("RaiseRealmAbility", "ignore_pain"), Is.False, "Determination 1 is not enough");

        planner.Call("RaiseRealmAbility", "determination");

        Assert.That(planner.Bool("RaiseRealmAbility", "ignore_pain"), Is.True);
        Assert.That(planner.Bool("CanLowerRealmAbility", "determination"), Is.False, "Ignore Pain needs Determination 2");
        Assert.That(planner.Bool("LowerRealmAbility", "determination"), Is.False);
        Assert.That(planner.RealmAbilityLevel("determination"), Is.EqualTo(2));

        var needing = (System.Collections.IEnumerable)planner.Call("RealmAbilitiesNeeding", "determination", 1)!;
        Assert.That(needing.Cast<object>().Select(ability => ability.GetType().GetProperty("Name")!.GetValue(ability)), Is.EqualTo(new[] { "Ignore Pain" }));

        planner.Call("RaiseRealmAbility", "determination");

        Assert.That(planner.Bool("LowerRealmAbility", "determination"), Is.True, "3 to 2 still meets the prerequisite");
        Assert.That(planner.Bool("LowerRealmAbility", "determination"), Is.False);
        Assert.That(planner.Bool("LowerRealmAbility", "ignore_pain"), Is.True);
        Assert.That(planner.Bool("LowerRealmAbility", "determination"), Is.True, "nothing needs it any more");
        Assert.That(planner.RealmAbilityLevel("determination"), Is.EqualTo(1));
        Assert.That(planner.Bool("LowerRealmAbility", "determination"), Is.True);
        Assert.That(planner.Bool("LowerRealmAbility", "determination"), Is.False, "already untrained");
        Assert.That(planner.RealmSpent, Is.EqualTo(0));
    }

    [Test]
    public void APrerequisiteTheClassCannotBuyIsNeverMet()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetRealmRank", 14, 9);

        Assert.That(planner.Bool("PrerequisitesMet", "unreachable"), Is.False);
        Assert.That(planner.Bool("RaiseRealmAbility", "unreachable"), Is.False);
        Assert.That(planner.Bool("RaiseRealmAbility", "nope"), Is.False);
    }

    [Test]
    public void ResetUntrainsButKeepsTheClassLevelAndRank()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetLevel", 40);
        planner.Call("SetRealmRank", 6, 2);
        planner.Call("SetSpec", "slash", 30);
        planner.Call("RaiseRealmAbility", "purge");
        planner.Call("Reset");

        Assert.That(planner.Get<object>("Class"), Is.Not.Null);
        Assert.That(planner.Level, Is.EqualTo(40));
        Assert.That(planner.Get<int>("RealmRank"), Is.EqualTo(6));
        Assert.That(planner.Get<int>("RealmLevel"), Is.EqualTo(2));
        Assert.That(planner.SpecLevel("slash"), Is.EqualTo(1));
        Assert.That(planner.SpecLevel("polearm"), Is.EqualTo(Math.Max(1, Rule("AutotrainLevel", 40))));
        Assert.That(planner.RealmAbilityLevel("purge"), Is.EqualTo(0));
        Assert.That(planner.SpecSpent, Is.EqualTo(0));
        Assert.That(planner.RealmSpent, Is.EqualTo(0));
    }

    [Test]
    public void ClearForgetsTheClassAndReturnsToTheDefaults()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetLevel", 40);
        planner.Call("SetRealmRank", 6, 2);
        planner.Call("SetSpec", "slash", 30);
        planner.Call("Clear");

        Assert.That(planner.Get<object>("Class"), Is.Null);
        Assert.That(planner.Level, Is.EqualTo(50));
        Assert.That(planner.Get<int>("RealmRank"), Is.EqualTo(1));
        Assert.That(planner.Get<int>("RealmLevel"), Is.EqualTo(0));
        Assert.That(((System.Collections.IEnumerable)planner.Get<object>("Specs")).Cast<object>(), Is.Empty);
        Assert.That(planner.SpecLevel("slash"), Is.EqualTo(0));
        Assert.That(planner.SpecTotal, Is.EqualTo(0));
    }

    [Test]
    public void SummaryListsTheBuildHighestFirst()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetRealmRank", 10, 0);
        planner.Call("SetSpec", "slash", 20);
        planner.Call("SetSpec", "polearm", 30);
        planner.Call("SetSpec", "parry", 20);
        planner.Call("RaiseRealmAbility", "purge");

        for (int level = 0; level < 3; level++)
        {
            planner.Call("RaiseRealmAbility", "determination");
        }

        Assert.That(planner.Call("Summary"),
            Is.EqualTo("Armsman L50 RR10L0" + Dash + "Polearm 30, Parry 20, Slash 20" + Dash + "Determination 3, Purge 1"));
    }

    [Test]
    public void SummaryOfAnUntrainedCharacterSaysSo()
    {
        Planner planner = ArmsmanPlanner();
        planner.Call("SetLevel", 3);

        Assert.That(planner.Call("Summary"), Is.EqualTo("Armsman L3 RR1L0" + Dash + "no specs trained" + Dash + "no realm abilities"));
    }

    [TestCase("determination:2", "determination", 2)]
    [TestCase(" purge : 3 ", "purge", 3)]
    [TestCase("purge", "purge", 1)]
    [TestCase("purge:x", "purge", 1)]
    [TestCase("purge:0", "purge", 1)]
    public void PrerequisitesAreReadAsKeyAndLevel(string prerequisite, string key, int level)
    {
        var parsed = ((string Key, int Level))StateType.GetMethod("ParsePrerequisite")!.Invoke(null, [prerequisite])!;

        Assert.That(parsed.Key, Is.EqualTo(key));
        Assert.That(parsed.Level, Is.EqualTo(level));
    }
}
