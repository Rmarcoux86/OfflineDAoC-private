using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Hard mode's monsters: Dread names, two-affix elite camps, the dormant shrine kill match, and the hunt's schedule.</summary>
[TestFixture]
public class UT_ExpeditionHardMonsters
{
    private const long Minute = 60_000;

    private static AutonomousCapnBryGoalCatalog.Entry Camp(string name, int level, int localX, int localY)
    {
        return new AutonomousCapnBryGoalCatalog.Entry
        {
            Id = $"fake:100:{name}:{localX}",
            Name = name,
            ZoneId = 100,
            RegionId = 100,
            LocalX = localX,
            LocalY = localY,
            Z = 100,
            Levels = [level],
            Sightings = 3
        };
    }

    private static ActPlan Plan()
    {
        return new ActPlan
        {
            Act = 1,
            MinLevel = 5,
            MaxLevel = 10,
            RegionId = 100,
            ZoneId = 100,
            CampX = 1000,
            CampY = 1000,
            Objectives = new List<ActObjective>
            {
                new() { Kind = eObjectiveKind.ClearCamp, MobName = "wolf", RegionId = 100, X = 1000, Y = 1000, Count = 5 }
            },
            Boss = new BossSpec { BaseMobName = "Grak", Level = 12 },
            Elites = new List<EliteCamp>
            {
                new() { MobName = "ghoul", Affix = ExpeditionElites.Swift, RegionId = 100, X = 40000, Y = 40000, Radius = 1200, Level = 14, Slain = new List<string> { "spawn-1" } }
            }
        };
    }

    private static List<AutonomousCapnBryGoalCatalog.Entry> Camps()
    {
        return new List<AutonomousCapnBryGoalCatalog.Entry>
        {
            Camp("wolf", 5, 1000, 1000),
            Camp("boar", 7, 1000, 5000),
            Camp("bear", 8, 5000, 5000),
            Camp("bear", 9, 20000, 5000),
            Camp("Grak", 13, 2000, 2000),
            Camp("ghoul", 12, 40000, 40000),
            Camp("wight", 13, 1000, 40000),
            Camp("dragon", 40, 60000, 60000),
            Camp("rat", 2, 3000, 3000)
        };
    }

    [Test]
    public void DreadName_PrefixesOnce()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionHardMonsters.DreadName("goblin"), Is.EqualTo("Dread goblin"));
            Assert.That(ExpeditionHardMonsters.DreadName("Dread goblin"), Is.EqualTo("Dread goblin"));
            Assert.That(ExpeditionHardMonsters.DreadName("dread lich"), Is.EqualTo("dread lich"), "a world monster already called dread");
            Assert.That(ExpeditionElites.EliteName(new[] { ExpeditionElites.Swift, ExpeditionElites.Venomous }, ExpeditionHardMonsters.DreadName("goblin")),
                Is.EqualTo("Swift Venomous Dread goblin"));
        });
    }

    [Test]
    public void BaseName_StripsAffixesAndDread()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionHardMonsters.BaseName("goblin"), Is.EqualTo("goblin"));
            Assert.That(ExpeditionHardMonsters.BaseName("Dread goblin"), Is.EqualTo("goblin"));
            Assert.That(ExpeditionHardMonsters.BaseName("Swift goblin"), Is.EqualTo("goblin"));
            Assert.That(ExpeditionHardMonsters.BaseName("Swift Venomous Dread goblin"), Is.EqualTo("goblin"));
            Assert.That(ExpeditionHardMonsters.BaseName("Venomous Swift Dread goblin scout"), Is.EqualTo("goblin scout"));
            Assert.That(ExpeditionHardMonsters.BaseName("Dread"), Is.EqualTo("Dread"), "nothing after the prefix");
        });
    }

    [Test]
    public void DormantShrineKill_CountsDreadAndTwoAffixNames()
    {
        ShrineSpec shrine = new()
        {
            RegionId = 249,
            CampMob = "essence shredder",
            CampX = 30000,
            CampY = 30000,
            KillsNeeded = 5,
            Placed = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Dread essence shredder", 249, 30500, 30500), Is.True, "Dread");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Swift Dread essence shredder", 249, 30500, 30500), Is.True, "Dread elite");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Swift Venomous Dread essence shredder", 249, 30500, 30500), Is.True, "hard elite");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Dread naburite drinker", 249, 30500, 30500), Is.False, "another monster");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Dread essence shredder", 249, 33000, 30000), Is.False, "too far from the camp");
        });
    }

    [Test]
    public void CampAffixes_AddTheHardExtras()
    {
        EliteCamp normal = new() { MobName = "goblin", Affix = ExpeditionElites.Swift };
        EliteCamp hard = new() { MobName = "goblin", Affix = ExpeditionElites.Swift, ExtraAffixes = new List<string> { ExpeditionElites.Venomous } };

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionElites.CampAffixes(normal), Is.EqualTo(new[] { "Swift" }));
            Assert.That(ExpeditionElites.ApproachMessage(normal), Is.EqualTo("You sense a Swift pack of goblin nearby."));
            Assert.That(ExpeditionElites.CampAffixes(hard), Is.EqualTo(new[] { "Swift", "Venomous" }));
            Assert.That(ExpeditionElites.ApproachMessage(hard), Is.EqualTo("You sense a Swift Venomous pack of goblin nearby."));
        });
    }

    [Test]
    public void HardenElites_MakesEveryBandCampAnEliteWithTwoAffixes()
    {
        ActPlan plan = Plan();
        int added = ExpeditionHardMonsters.HardenElites(plan, Camps(), 0, 0, new Random(7));
        List<string> names = plan.Elites.Select(e => e.MobName).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(plan.HardElites, Is.True);
            Assert.That(added, Is.EqualTo(4));
            Assert.That(names, Is.EquivalentTo(new[] { "ghoul", "boar", "bear", "bear", "wight" }));
            Assert.That(names, Does.Not.Contain("wolf"), "an objective's monster");
            Assert.That(names, Does.Not.Contain("Grak"), "the boss's monster");
            Assert.That(names, Does.Not.Contain("dragon"), "above the band");
            Assert.That(names, Does.Not.Contain("rat"), "below the band");

            EliteCamp ghoul = plan.Elites[0];
            Assert.That(ghoul.Affix, Is.EqualTo(ExpeditionElites.Swift), "the plan's own pack keeps its affix");
            Assert.That(ghoul.Slain, Is.EqualTo(new[] { "spawn-1" }));
            Assert.That(ghoul.Level, Is.EqualTo(15));

            foreach (EliteCamp camp in plan.Elites)
            {
                List<string> affixes = ExpeditionElites.CampAffixes(camp);
                Assert.That(affixes, Has.Count.EqualTo(2), camp.MobName);
                Assert.That(affixes, Is.Unique, camp.MobName);
                Assert.That(affixes, Is.SubsetOf(ExpeditionElites.Affixes), camp.MobName);
                Assert.That(camp.Description, Does.StartWith($"A {affixes[0]} {affixes[1]} pack of {camp.MobName} lairs "), camp.MobName);
                Assert.That(camp.Radius, Is.EqualTo(ActGenerator.EliteCampRadius), camp.MobName);
            }

            Assert.That(plan.Elites.Single(e => e.MobName == "boar").Level, Is.EqualTo(7 + ExpeditionElites.LevelBonus + ExpeditionHardMonsters.DreadLevelBonus));
            Assert.That(ExpeditionHardMonsters.HardenElites(plan, Camps(), 0, 0, new Random(7)), Is.EqualTo(0), "once per act");
        });
    }

    [Test]
    public void HardPlan_RoundTripsAndOldPlansReadAsNormal()
    {
        ActPlan plan = Plan();
        ExpeditionHardMonsters.HardenElites(plan, Camps(), 0, 0, new Random(3));
        ActPlan copy = JsonSerializer.Deserialize<ActPlan>(JsonSerializer.Serialize(plan, ExpeditionJson.Options), ExpeditionJson.Options);
        ActPlan old = JsonSerializer.Deserialize<ActPlan>("{\"Act\":1,\"Elites\":[{\"MobName\":\"ghoul\",\"Affix\":\"Swift\"}]}", ExpeditionJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(copy.HardElites, Is.True);
            Assert.That(copy.Elites.Select(e => ExpeditionElites.CampAffixText(e)), Is.EqualTo(plan.Elites.Select(e => ExpeditionElites.CampAffixText(e))));
            Assert.That(old.HardElites, Is.False);
            Assert.That(old.Elites[0].ExtraAffixes, Is.Empty);
            Assert.That(ExpeditionElites.CampAffixes(old.Elites[0]), Is.EqualTo(new[] { "Swift" }));
        });
    }

    [Test]
    public void Hunt_NextSpawnIsTheNextTwentyMinuteMark()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionHunt.NextSpawnAt(0), Is.EqualTo(20 * Minute));
            Assert.That(ExpeditionHunt.NextSpawnAt(20 * Minute - 1), Is.EqualTo(20 * Minute));
            Assert.That(ExpeditionHunt.NextSpawnAt(20 * Minute), Is.EqualTo(40 * Minute));
            Assert.That(ExpeditionHunt.NextSpawnAt(59 * Minute), Is.EqualTo(60 * Minute));
            Assert.That(ExpeditionHunt.NextSpawnAt(-5), Is.EqualTo(20 * Minute));
        });
    }

    [Test]
    public void Hunt_WarnsAMinuteBeforeEachSpawnOnTwoSecondTicks()
    {
        List<long> warnings = new();
        List<long> spawns = new();

        for (long time = 0; time < 65 * Minute; time += 2000)
        {
            if (ExpeditionHunt.WarningDue(time, time + 2000))
            {
                warnings.Add(time + 2000);
            }

            if (ExpeditionHunt.SpawnDue(time, time + 2000))
            {
                spawns.Add(time + 2000);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(warnings, Is.EqualTo(new[] { 19 * Minute, 39 * Minute, 59 * Minute }));
            Assert.That(spawns, Is.EqualTo(new[] { 20 * Minute, 40 * Minute, 60 * Minute }));
            Assert.That(ExpeditionHunt.SpawnDue(20 * Minute, 20 * Minute + 2000), Is.False, "a mark is crossed once");
            Assert.That(ExpeditionHunt.WarningDue(19 * Minute - 7000, 19 * Minute + 3000), Is.True, "a long tick still crosses it");
        });
    }

    [Test]
    public void Hunt_ReportsRoughDistanceAndDirection()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionHunt.RoughDistance(100), Is.EqualTo(500));
            Assert.That(ExpeditionHunt.RoughDistance(2140), Is.EqualTo(2000));
            Assert.That(ExpeditionHunt.RoughDistance(4800), Is.EqualTo(5000));
            Assert.That(ExpeditionHunt.StalkerName("Lyonesse"), Is.EqualTo("Stalker of Lyonesse"));
            Assert.That(ExpeditionHunt.ReportText("Stalker of Lyonesse", "north-east", 2140),
                Is.EqualTo("The Stalker of Lyonesse closes in from the north-east, about 2,000 units."));
        });
    }
}
