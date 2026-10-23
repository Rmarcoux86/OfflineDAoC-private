using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DOL.GS.Expedition;
using NUnit.Framework;
using SpawnRecord = DOL.GS.Expedition.WorldCampSource.SpawnRecord;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionWorldCampSource
{
    private const uint Peace = (uint)GameNPC.eFlags.PEACE;
    private const uint CantTarget = (uint)GameNPC.eFlags.CANTTARGET;

    private static SpawnRecord Spawn(string name, int localX, int localY, int level = 20, int zoneId = 101, byte realm = 0,
        uint flags = 0, string classType = "DOL.GS.GameNPC", string guild = "", int z = 500)
    {
        return new SpawnRecord(name, 100, zoneId, "East Svealand", localX, localY, z, level, realm, flags, classType, guild);
    }

    private sealed class ListCampSource : IExpeditionCampSource
    {
        private readonly List<AutonomousCapnBryGoalCatalog.Entry> _camps;

        public ListCampSource(List<AutonomousCapnBryGoalCatalog.Entry> camps)
        {
            _camps = camps;
        }

        public IReadOnlyList<AutonomousCapnBryGoalCatalog.Entry> GetZoneCamps(int regionId, int zoneId)
        {
            return _camps.Where(c => c.RegionId == regionId && c.ZoneId == zoneId).ToList();
        }
    }

    [Test]
    public void Filters_KeepOnlyHostileMonsters()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("wolf", 0, 0)), Is.True, "plain mob");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("wolf", 0, 0, classType: "")), Is.True, "empty class type");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("wolf", 0, 0, classType: null, guild: null)), Is.True, "null class type");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("wolf", 0, 0, flags: 0x01)), Is.True, "ghost flag only");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("guard", 0, 0, realm: 2)), Is.False, "realm NPC");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("deer", 0, 0, flags: Peace)), Is.False, "peaceful");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("marker", 0, 0, flags: CantTarget)), Is.False, "untargetable");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("rat", 0, 0, level: 0)), Is.False, "level 0");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn(" ", 0, 0)), Is.False, "no name");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("Hrolf", 0, 0, classType: "DOL.GS.GameMerchant")), Is.False, "merchant");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("Ulf", 0, 0, classType: "DOL.GS.Trainer.WarriorTrainer")), Is.False, "trainer");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("Sven", 0, 0, classType: "DOL.GS.GameStableMaster")), Is.False, "stable master");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("Ola", 0, 0, classType: "DOL.GS.Scripts.SomeTeleporter")), Is.False, "teleporter");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("Kari", 0, 0, guild: "Healer")), Is.False, "service guild");
            Assert.That(WorldCampSource.IsHostileMonster(Spawn("Earl Mermer", 0, 0, level: 71, classType: "DOL.GS.GameEpicNPC")), Is.False, "epic encounter");
        });
    }

    [Test]
    public void BuildCamps_GroupsByNameAndCell()
    {
        List<SpawnRecord> spawns =
        [
            Spawn("wolf", 100, 100, 10),
            Spawn("wolf", 300, 500, 12),
            Spawn("wolf", 4300, 200, 11),
            Spawn("wolf", 4500, 200, 13),
            Spawn("bear", 200, 200, 14),
            Spawn("wolf", 150, 150, 10, zoneId: 102),
            Spawn("deer", 120, 120, 3, flags: Peace)
        ];

        List<AutonomousCapnBryGoalCatalog.Entry> camps = WorldCampSource.BuildCamps(spawns);

        Assert.That(camps.Select(c => (c.ZoneId, c.Name, c.Sightings)), Is.EqualTo(new (ushort, string, int)[]
        {
            (101, "bear", 1),
            (101, "wolf", 2),
            (101, "wolf", 2),
            (102, "wolf", 1)
        }));

        AutonomousCapnBryGoalCatalog.Entry firstWolf = camps[1];

        Assert.Multiple(() =>
        {
            // Cell (0,0): spawns at x 100 and 300; the median of two is the upper one.
            Assert.That((firstWolf.LocalX, firstWolf.LocalY), Is.EqualTo((300, 500)));
            Assert.That(firstWolf.Levels, Is.EqualTo(new[] { 10, 12 }));
            Assert.That(firstWolf.RegionId, Is.EqualTo((ushort)100));
            Assert.That(firstWolf.Zone, Is.EqualTo("East Svealand"));
            Assert.That(camps[2].Levels, Is.EqualTo(new[] { 11, 13 }));
            Assert.That(camps[2].LocalX, Is.EqualTo(4500));
            Assert.That(camps.Select(c => c.Id).Distinct().Count(), Is.EqualTo(camps.Count));
        });
    }

    [Test]
    public void BuildCamps_UsesTheSpawnNearestTheMedianAndDistinctLevels()
    {
        List<SpawnRecord> spawns =
        [
            Spawn("orc", 1000, 3000, 20, z: 10),
            Spawn("orc", 2000, 1000, 22, z: 30),
            Spawn("orc", 3000, 2000, 20, z: 20)
        ];

        AutonomousCapnBryGoalCatalog.Entry camp = WorldCampSource.BuildCamps(spawns).Single();

        Assert.Multiple(() =>
        {
            // Median point (2000, 2000); the two spawns 1000 away tie and the lower X wins.
            Assert.That((camp.LocalX, camp.LocalY, camp.Z), Is.EqualTo((2000, 1000, 30)));
            Assert.That(camp.Levels, Is.EqualTo(new[] { 20, 22 }));
            Assert.That(camp.Sightings, Is.EqualTo(3));
        });
    }

    [Test]
    public void BuildCamps_IgnoresInputOrder()
    {
        List<SpawnRecord> spawns = [];
        Random random = new(42);

        for (int i = 0; i < 300; i++)
        {
            spawns.Add(Spawn(i % 3 == 0 ? "wolf" : i % 3 == 1 ? "bear" : "Grak", random.Next(0, 60000), random.Next(0, 60000), random.Next(1, 50)));
        }

        string ordered = JsonSerializer.Serialize(WorldCampSource.BuildCamps(spawns));
        string shuffled = JsonSerializer.Serialize(WorldCampSource.BuildCamps(spawns.OrderBy(_ => random.Next()).ToList()));
        string reversed = JsonSerializer.Serialize(WorldCampSource.BuildCamps(Enumerable.Reverse(spawns).ToList()));

        Assert.Multiple(() =>
        {
            Assert.That(shuffled, Is.EqualTo(ordered));
            Assert.That(reversed, Is.EqualTo(ordered));
        });
    }

    [Test]
    public void WorldLikeSource_GivesTheSamePlanForTheSameSeedWhateverTheRowOrder()
    {
        // A Midgard act-2 zone (West Svealand, 102) filled with world-style spawns.
        List<SpawnRecord> spawns = [];
        Random random = new(7);
        string[] names = ["svartalf", "wolfhound", "bear cub", "huldu", "Thrym"];

        for (int i = 0; i < 200; i++)
        {
            string name = names[i % names.Length];
            int level = name == "Thrym" ? 21 : random.Next(10, 21);
            spawns.Add(new SpawnRecord(name, 100, 102, "West Svealand", random.Next(0, 65000), random.Next(0, 65000), 800, level, 0, 0, "", ""));
        }

        ListCampSource first = new(WorldCampSource.BuildCamps(spawns));
        ListCampSource second = new(WorldCampSource.BuildCamps(spawns.OrderBy(_ => random.Next()).ToList()));

        for (int seed = 0; seed < 10; seed++)
        {
            ActPlan a = ActGenerator.Generate(2, eRealm.Midgard, seed, -1, zone => (zone * 70000, zone * 70000), first);
            ActPlan b = ActGenerator.Generate(2, eRealm.Midgard, seed, -1, zone => (zone * 70000, zone * 70000), second);

            Assert.Multiple(() =>
            {
                Assert.That(a.ZoneId, Is.EqualTo(102), $"seed {seed}");
                Assert.That(JsonSerializer.Serialize(b, ExpeditionJson.Options), Is.EqualTo(JsonSerializer.Serialize(a, ExpeditionJson.Options)), $"seed {seed}");
            });
        }
    }

    [Test]
    public void SourceWithoutAServer_ReturnsNothing()
    {
        Assert.That(WorldCampSource.Instance.GetZoneCamps(100, 100), Is.Empty);
    }
}
