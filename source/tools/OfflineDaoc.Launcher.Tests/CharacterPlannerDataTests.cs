using System.Collections;
using System.Reflection;
using NUnit.Framework;

namespace OfflineDaoc.Launcher.Tests;

/// <summary>
/// The Character Planner's data: the class and realm ability tables lifted from the server scripts, the loader's behaviour
/// without a database, and (when a copy of the game database is found) what it reads from it. The database is the file
/// named by OFFLINEDAOC_DB, else the first data\opendaoc.sqlite3.db (or opendaoc.sqlite3.db) in the test folder or one of
/// its parents; without one those tests are ignored.
/// </summary>
public sealed class CharacterPlannerDataTests
{
    private static readonly Assembly Launcher = Assembly.Load("OfflineDAoC");
    private static readonly Type DataType = Launcher.GetType("OfflineDaoc.Launcher.CharacterPlannerData")!;
    private static readonly Type ClassesType = Launcher.GetType("OfflineDaoc.Launcher.CharacterPlannerClasses")!;
    private static readonly Type RealmAbilitiesType = Launcher.GetType("OfflineDaoc.Launcher.CharacterPlannerRealmAbilities")!;
    private static readonly Lazy<object> Database = new(() => Activator.CreateInstance(DataType, FindDatabase())!);

    private static object Value(object item, string property) => item.GetType().GetProperty(property)!.GetValue(item);

    private static List<object> Items(object list) => ((IEnumerable)list).Cast<object>().ToList();

    private static List<object> Call(object data, string method, params object[] arguments) =>
        Items(DataType.GetMethod(method)!.Invoke(data, arguments)!);

    private static object Named(List<object> items, string property, object value) =>
        items.Single(item => Equals(Value(item, property), value));

    private static List<string> Strings(object list) => Items(list).Cast<string>().ToList();

    private static List<int> Ints(object list) => Items(list).Cast<int>().ToList();

    private static string FindDatabase()
    {
        string fromEnvironment = Environment.GetEnvironmentVariable("OFFLINEDAOC_DB");

        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        for (DirectoryInfo folder = new(TestContext.CurrentContext.TestDirectory); folder != null; folder = folder.Parent)
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(folder.FullName, "data", "opendaoc.sqlite3.db"),
                         Path.Combine(folder.FullName, "opendaoc.sqlite3.db"),
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    // The loader over the real database, or the test is ignored.
    private static object RealData()
    {
        if (FindDatabase().Length == 0)
        {
            Assert.Ignore("No game database: set OFFLINEDAOC_DB or put data\\opendaoc.sqlite3.db above the test folder.");
        }

        object data = Database.Value;
        Assert.That(DataType.GetProperty("LoadError")!.GetValue(data), Is.Null);

        return data;
    }

    #region Static tables (no database)

    [Test]
    public void ClassTableMatchesTheServerScripts()
    {
        List<object> classes = Items(ClassesType.GetField("All")!.GetValue(null)!);
        object armsman = Named(classes, "Name", "Armsman");
        object paladin = Named(classes, "Name", "Paladin");

        Assert.That(classes, Has.Count.EqualTo(40));
        Assert.That(Value(armsman, "Id"), Is.EqualTo(2));
        Assert.That(Value(armsman, "Realm"), Is.EqualTo("Albion"));
        Assert.That(Value(armsman, "BaseClassName"), Is.EqualTo("Fighter"));
        Assert.That(Value(armsman, "SpecPointsMultiplier"), Is.EqualTo(20));
        Assert.That(Strings(Value(armsman, "AutotrainSpecKeys")), Is.EqualTo(new[] { "Slash", "Thrust" }));
        Assert.That(Strings(Value(paladin, "AutotrainSpecKeys")), Is.EqualTo(new[] { "Slash", "Chants" }));
        Assert.That(Value(Named(classes, "Name", "Infiltrator"), "SpecPointsMultiplier"), Is.EqualTo(25));
        Assert.That(Value(Named(classes, "Name", "Nightshade"), "SpecPointsMultiplier"), Is.EqualTo(22));
        Assert.That(Strings(Value(Named(classes, "Name", "Scout"), "AutotrainSpecKeys")), Is.EqualTo(new[] { "Archery", "Longbows" }));
        Assert.That(Strings(Value(Named(classes, "Name", "Hunter"), "AutotrainSpecKeys")), Is.Empty);
        Assert.That(classes.Count(item => Equals(Value(item, "Realm"), "Albion")), Is.EqualTo(14));
        Assert.That(classes.Count(item => Equals(Value(item, "Realm"), "Midgard")), Is.EqualTo(12));
        Assert.That(classes.Count(item => Equals(Value(item, "Realm"), "Hibernia")), Is.EqualTo(13));
        Assert.That(classes.Select(item => (string)Value(item, "Name"))
            .Intersect(new[] { "Mauler", "Heretic", "Valkyrie", "Warlock", "Bainshee", "Vampiir" }), Is.Empty);
        Assert.That(classes.Select(item => (int)Value(item, "Id")), Is.Unique);
    }

    [Test]
    public void RealmAbilityTableHasTheHandlerCosts()
    {
        var table = (IDictionary)RealmAbilitiesType.GetField("ByImplementation")!.GetValue(null)!;

        Assert.That(Ints(Value(table["AtlasOF_RAStrengthEnhancer"]!, "CostPerLevel")), Is.EqualTo(new[] { 1, 3, 6, 10, 14 }));
        Assert.That(Value(table["AtlasOF_RAStrengthEnhancer"]!, "Passive"), Is.True);
        Assert.That(Ints(Value(table["AtlasOF_DeterminationAbility"]!, "CostPerLevel")), Is.EqualTo(new[] { 1, 2, 3, 6, 10 }));
        Assert.That(Ints(Value(table["AtlasOF_DeterminationHybridAbility"]!, "CostPerLevel")), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(Ints(Value(table["AtlasOF_FirstAid"]!, "CostPerLevel")), Is.EqualTo(new[] { 3, 6, 10 }));
        Assert.That(Value(table["AtlasOF_FirstAid"]!, "Passive"), Is.False);
        Assert.That(Ints(Value(table["AtlasOF_PurgeAbilityReduced"]!, "CostPerLevel")), Is.EqualTo(new[] { 4 }));
        Assert.That(Ints(Value(table["MasteryOfStealthAbility"]!, "CostPerLevel")), Is.EqualTo(new[] { 3, 6, 10 }));

        // The server resolves Ability.Implementation ignoring case: the class is AtlasOF_MasteryofConcentration.
        Assert.That(table.Contains("AtlasOF_MasteryofConcentration"), Is.True);
    }

    [Test]
    public void MissingDatabaseLeavesEmptyListsAndAnError()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "no-such-folder", "opendaoc.sqlite3.db");
        object data = Activator.CreateInstance(DataType, path)!;

        Assert.That(DataType.GetProperty("LoadError")!.GetValue(data), Does.Contain("not found"));
        Assert.That(Items(DataType.GetProperty("Classes")!.GetValue(data)!), Has.Count.EqualTo(40));
        Assert.That(Call(data, "SpecsFor", 2), Is.Empty);
        Assert.That(Call(data, "SpellsFor", 1, "Chants"), Is.Empty);
        Assert.That(Call(data, "StylesFor", 2, "Polearm"), Is.Empty);
        Assert.That(Call(data, "AbilitiesFor", 2, "Shields"), Is.Empty);
        Assert.That(Call(data, "RealmAbilitiesFor", 2), Is.Empty);
    }

    [Test]
    public void UnreadableDatabaseIsAnErrorNotAnException()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, $"planner-not-a-database-{Guid.NewGuid():N}.db");
        File.WriteAllText(path, "this is not an SQLite database");

        try
        {
            object data = Activator.CreateInstance(DataType, path)!;

            Assert.That(DataType.GetProperty("LoadError")!.GetValue(data), Is.Not.Null);
            Assert.That(Call(data, "SpecsFor", 2), Is.Empty);
        }
        finally
        {
            File.Delete(path);
        }
    }

    #endregion

    #region The game database

    [Test]
    public void ArmsmanTrainsItsWeaponLines()
    {
        List<object> specs = Call(RealData(), "SpecsFor", 2);
        List<string> trainable = specs.Where(spec => (bool)Value(spec, "Trainable")).Select(spec => (string)Value(spec, "KeyName")).ToList();

        Assert.That(trainable, Is.EquivalentTo(new[] { "Crush", "Slash", "Thrust", "Polearm", "Shields", "Two Handed", "Parry", "Crossbows" }));
        Assert.That(Value(Named(specs, "KeyName", "Shields"), "Name"), Is.EqualTo("Shield"));
        Assert.That(Value(Named(specs, "KeyName", "Polearm"), "LevelAcquired"), Is.EqualTo(1));
        Assert.That(Value(Named(specs, "KeyName", "Two Handed"), "LevelAcquired"), Is.EqualTo(10));
        Assert.That(Value(Named(specs, "KeyName", "Crossbows"), "LevelAcquired"), Is.EqualTo(15));
        Assert.That(Value(Named(specs, "KeyName", "ArmsmanCareer"), "Trainable"), Is.False);
        Assert.That(specs.Select(spec => (string)Value(spec, "KeyName")).Intersect(new[] { "Realm Abilities", "CharacterCareer" }), Is.Empty);
        Assert.That(specs.TakeWhile(spec => (bool)Value(spec, "Trainable")).Count(), Is.EqualTo(trainable.Count), "trainable lines come first");
    }

    [Test]
    public void PolearmStylesHaveLevelsAndOpenings()
    {
        List<object> styles = Call(RealData(), "StylesFor", 2, "Polearm");
        List<int> levels = styles.Select(style => (int)Value(style, "Level")).ToList();

        Assert.That(styles, Has.Count.EqualTo(15));
        Assert.That(levels, Is.Ordered);
        Assert.That(Value(styles[0], "Name"), Is.EqualTo("Impale"));
        Assert.That(levels[0], Is.EqualTo(2));
        Assert.That(Value(styles[0], "Opening"), Is.EqualTo("Any"));
        Assert.That(Value(Named(styles, "Name", "Defender's Cross"), "Opening"), Is.EqualTo("Side"));
        Assert.That(Value(Named(styles, "Name", "Executioner"), "Opening"), Is.EqualTo("After parry"));
        Assert.That(Value(Named(styles, "Name", "Defender's Courage"), "Opening"), Is.EqualTo("Follow-up of Defender's Faith"));
        Assert.That(Value(Named(styles, "Name", "Defender's Aegis"), "Level"), Is.EqualTo(50));
        Assert.That(Value(styles[0], "Description"), Does.Contain("Growth rate"));
    }

    [Test]
    public void PaladinChantsAreListedByLevel()
    {
        List<object> spells = Call(RealData(), "SpellsFor", 1, "Chants");
        List<int> levels = spells.Select(spell => (int)Value(spell, "Level")).ToList();

        Assert.That(spells, Is.Not.Empty);
        Assert.That(levels, Is.Ordered);
        Assert.That(spells.Select(spell => Value(spell, "LineName")).Distinct(), Is.EqualTo(new[] { "Chants" }));
        Assert.That(Value(spells[0], "Name"), Is.EqualTo("Aura of Protection"));
        Assert.That(levels[0], Is.EqualTo(1));
        Assert.That(Value(spells[0], "Kind"), Is.EqualTo("Buff"));
    }

    [Test]
    public void ClericRejuvenationHasTheBaselineAndTheClericSpecLine()
    {
        List<object> spells = Call(RealData(), "SpellsFor", 6, "Rejuvenation");
        object minor = Named(spells, "Name", "Minor Rejuvenation");
        object major = Named(spells, "Name", "Major Restoration");

        Assert.That(Value(minor, "LineName"), Is.EqualTo("Rejuvenation (baseline)"));
        Assert.That(Value(minor, "Level"), Is.EqualTo(1));
        Assert.That(Value(minor, "Kind"), Is.EqualTo("Heal"));
        Assert.That(Value(major, "LineName"), Is.EqualTo("Rejuvenation"));
        Assert.That(Value(major, "Level"), Is.EqualTo(5));
    }

    [Test]
    public void NightshadeMagicIsAnUntrainableBaselineAndEnvenomHasNoSpells()
    {
        object data = RealData();
        List<object> specs = Call(data, "SpecsFor", 49);
        List<object> magic = Call(data, "SpellsFor", 49, "Nightshade Magic");

        Assert.That(Value(Named(specs, "KeyName", "Nightshade Magic"), "Trainable"), Is.False);
        Assert.That(Value(Named(specs, "KeyName", "Envenom"), "Trainable"), Is.True);
        Assert.That(magic, Is.Not.Empty);
        Assert.That(magic.Select(spell => Value(spell, "LineName")).Distinct(), Is.EqualTo(new[] { "Nightshade Magic (baseline)" }));
        Assert.That(Call(data, "SpellsFor", 49, "Envenom"), Is.Empty);
    }

    [Test]
    public void SpecAbilitiesCarryTheirSpecLevelAndRomanLevel()
    {
        object data = RealData();
        List<(string, int)> shields = Call(data, "AbilitiesFor", 2, "Shields")
            .Select(ability => ((string)Value(ability, "Name"), (int)Value(ability, "Level"))).ToList();
        List<(string, int)> career = Call(data, "AbilitiesFor", 2, "ArmsmanCareer")
            .Select(ability => ((string)Value(ability, "Name"), (int)Value(ability, "Level"))).ToList();

        Assert.That(shields, Is.EqualTo(new[] { ("Guard I", 5), ("Engage Ability", 7), ("Guard II", 10), ("Guard III", 15) }));
        Assert.That(career, Does.Contain(("Protect", 11)));
        Assert.That(career, Does.Contain(("Intercept", 12)));
        Assert.That(career, Does.Contain(("Armor Ability: Plate", 15)));
    }

    [Test]
    public void ArmsmanRealmAbilitiesAreAllPriced()
    {
        List<object> abilities = Call(RealData(), "RealmAbilitiesFor", 2);
        object determination = Named(abilities, "KeyName", "AtlasOF_Determination");

        Assert.That(abilities, Has.Count.EqualTo(32));
        Assert.That(abilities.Where(ability => Items(Value(ability, "CostPerLevel")).Count == 0), Is.Empty);
        Assert.That(Value(determination, "Name"), Is.EqualTo("Determination"));
        Assert.That(Value(determination, "Passive"), Is.True);
        Assert.That(Ints(Value(determination, "CostPerLevel")), Is.EqualTo(new[] { 1, 2, 3, 6, 10 }));
        Assert.That(Value(determination, "MaxLevel"), Is.EqualTo(5));
        Assert.That(Ints(Value(Named(abilities, "KeyName", "AtlasOF_AugStr"), "CostPerLevel")), Is.EqualTo(new[] { 1, 3, 6, 10, 14 }));
        Assert.That(Ints(Value(Named(abilities, "KeyName", "AtlasOF_PurgeReduced"), "CostPerLevel")), Is.EqualTo(new[] { 4 }));
        Assert.That(Strings(Value(Named(abilities, "KeyName", "AtlasOF_MasteryOfArms"), "Prerequisites")), Is.EqualTo(new[] { "AtlasOF_AugStr:3" }));
        Assert.That(Strings(Value(Named(abilities, "KeyName", "AtlasOF_IgnorePainTank"), "Prerequisites")), Is.EqualTo(new[] { "AtlasOF_FirstAid:2" }));
        Assert.That(Strings(Value(Named(abilities, "KeyName", "AtlasOF_SecondWind"), "Prerequisites")), Is.EqualTo(new[] { "AtlasOF_AugCon:3" }));
        Assert.That(Value(Named(abilities, "KeyName", "AtlasOF_SecondWind"), "Passive"), Is.False);
    }

    [Test]
    public void FriarMasteryOfArmsNeedsDexterity()
    {
        List<object> abilities = Call(RealData(), "RealmAbilitiesFor", 10);

        Assert.That(Strings(Value(Named(abilities, "KeyName", "AtlasOF_MasteryOfArms"), "Prerequisites")), Is.EqualTo(new[] { "AtlasOF_AugDex:3" }));
    }

    [Test]
    public void EveryRealmAbilityIsPricedExceptTheKnownGap()
    {
        // AtlasOF_SeveringTheTether (Mentalist) has no handler class in the server, so SkillBase cannot instantiate it and
        // the trainer never offers it; the planner lists it without levels and warns.
        object data = RealData();
        var unpriced = new HashSet<string>();

        foreach (object playerClass in Items(DataType.GetProperty("Classes")!.GetValue(data)!))
        {
            foreach (object ability in Call(data, "RealmAbilitiesFor", (int)Value(playerClass, "Id")))
            {
                if (Items(Value(ability, "CostPerLevel")).Count == 0)
                {
                    unpriced.Add((string)Value(ability, "KeyName"));
                }
            }
        }

        Assert.That(unpriced, Is.SubsetOf(new[] { "AtlasOF_SeveringTheTether" }));
        Assert.That(Strings(DataType.GetProperty("Warnings")!.GetValue(data)!).Count(warning => warning.Contains("no cost table")),
            Is.EqualTo(unpriced.Count));
    }

    [Test]
    public void ClassesWithoutCareerRowsAreLeftOut()
    {
        object data = RealData();
        List<object> classes = Items(DataType.GetProperty("Classes")!.GetValue(data)!);

        Assert.That(classes, Has.Count.GreaterThanOrEqualTo(38));
        Assert.That(classes.Where(playerClass => Call(data, "SpecsFor", (int)Value(playerClass, "Id")).Count == 0), Is.Empty);
    }

    #endregion
}
