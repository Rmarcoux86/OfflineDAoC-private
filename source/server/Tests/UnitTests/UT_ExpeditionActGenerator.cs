using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionActGenerator
{
    private const int ZoneExtent = 65536;

    private static readonly eRealm[] PlayerRealms = [eRealm.Albion, eRealm.Midgard, eRealm.Hibernia];

    // Midgard's open regions (classic 100, Shrouded Isles 151) and its dungeons.
    // 249: Darkness Falls, shared by all realms, is every realm's act 5.
    private static readonly int[] MidgardRegions = [100, 151, 125, 126, 127, 128, 129, 150, 160, 161, 249];

    // The old frontier zones (AutonomousWorldBotController.IsFrontierZone) and the New Frontiers region.
    private static readonly (int Region, int Zone)[] FrontierZones =
    [
        (1, 11), (1, 12), (1, 14), (1, 15),
        (100, 111), (100, 112), (100, 113), (100, 115),
        (200, 210), (200, 211), (200, 212), (200, 214)
    ];

    private static readonly int[] FrontierRegions = [163, 246, 248, 276, 277];

    // A distinct fake offset per zone, so no test needs a loaded world.
    private static (int ox, int oy) FakeOffset(int zoneId) => (100000 + zoneId * 70000, 200000 + zoneId * 70000);

    private static string Serialize(ActPlan plan) => JsonSerializer.Serialize(plan, ExpeditionJson.Options);

    private static ActPlan FromCatalog(int act, eRealm realm, int seed, int previousZoneId)
    {
        return ActGenerator.Generate(act, realm, seed, previousZoneId, FakeOffset, CatalogCampSource.Instance);
    }

    private static bool InsideZone(int zoneId, int x, int y)
    {
        (int ox, int oy) = FakeOffset(zoneId);

        return x >= ox && x < ox + ZoneExtent && y >= oy && y < oy + ZoneExtent;
    }

    // Zones.xml (id -> region, name), found by walking up from the test output to the source tree.
    private static Dictionary<int, (int Region, string Name)> LoadZonesXml()
    {
        string relative = Path.Combine("GameServer", "scripts", "dbupdater", "insert", "Zones.xml");

        foreach (string start in new[] { TestContext.CurrentContext.TestDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo directory = new(start); directory != null; directory = directory.Parent)
            {
                foreach (string candidate in new[] { Path.Combine(directory.FullName, relative), Path.Combine(directory.FullName, "source", "server", relative) })
                {
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    return XDocument.Load(candidate).Root
                        .Elements("Zones")
                        .ToDictionary(z => (int)z.Element("ZoneID"), z => ((int)z.Element("RegionID"), (string)z.Element("Name")));
                }
            }
        }

        Assert.Fail("Zones.xml was not found above " + TestContext.CurrentContext.TestDirectory);

        return null;
    }

    private sealed class FakeCampSource : IExpeditionCampSource
    {
        private readonly Dictionary<(int Region, int Zone), List<AutonomousCapnBryGoalCatalog.Entry>> _camps = new();

        public FakeCampSource Add(int regionId, int zoneId, string name, int level, int localX, int localY, int sightings = 3)
        {
            if (!_camps.TryGetValue((regionId, zoneId), out List<AutonomousCapnBryGoalCatalog.Entry> list))
            {
                list = new List<AutonomousCapnBryGoalCatalog.Entry>();
                _camps[(regionId, zoneId)] = list;
            }

            list.Add(new AutonomousCapnBryGoalCatalog.Entry
            {
                Id = $"fake:{zoneId}:{list.Count}",
                Name = name,
                ZoneId = (ushort)zoneId,
                RegionId = (ushort)regionId,
                LocalX = localX,
                LocalY = localY,
                Z = 100,
                Levels = [level],
                Sightings = sightings
            });

            return this;
        }

        public IReadOnlyList<AutonomousCapnBryGoalCatalog.Entry> GetZoneCamps(int regionId, int zoneId)
        {
            return _camps.TryGetValue((regionId, zoneId), out List<AutonomousCapnBryGoalCatalog.Entry> list) ? list : [];
        }
    }

    [Test]
    public void SameSeed_ProducesIdenticalPlan([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            string first = Serialize(FromCatalog(act, realm, 12345, -1));
            string second = Serialize(FromCatalog(act, realm, 12345, -1));

            Assert.That(second, Is.EqualTo(first), $"act {act}");
        }
    }

    [Test]
    public void DefaultSource_WithoutAWorld_UsesTheCatalog([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            string catalog = Serialize(FromCatalog(act, realm, 777, -1));
            string defaulted = Serialize(ActGenerator.Generate(act, realm, 777, -1, FakeOffset));

            Assert.That(defaulted, Is.EqualTo(catalog), $"act {act}");
        }
    }

    [Test]
    public void DifferentSeeds_ProduceDifferentPlans([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            HashSet<string> distinct = new();

            for (int seed = 0; seed < 20; seed++)
            {
                distinct.Add(Serialize(FromCatalog(act, realm, seed, -1)));
            }

            Assert.That(distinct.Count, Is.GreaterThan(1), $"act {act}");
        }
    }

    [Test]
    public void EveryAct_GeneratesAValidPlan([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            (int min, int max) = ActGenerator.GetLevelBand(act);

            for (int seed = 0; seed < 25; seed++)
            {
                ActPlan plan = null;

                Assert.DoesNotThrow(() => plan = FromCatalog(act, realm, seed, -1), $"{realm} act {act} seed {seed}");
                string context = $"{realm} act {act} seed {seed} zone {plan.ZoneName}";

                Assert.Multiple(() =>
                {
                    Assert.That(plan.Act, Is.EqualTo(act), context);
                    Assert.That((plan.MinLevel, plan.MaxLevel), Is.EqualTo((min, max)), context);
                    Assert.That(plan.ZoneName, Is.Not.Empty, context);
                    Assert.That(InsideZone(plan.ZoneId, plan.CampX, plan.CampY), Is.True, context + " camp");
                    Assert.That(plan.CampHeading, Is.LessThan(4096), context);
                    Assert.That(plan.Objectives.Count, Is.InRange(ExpectedObjectives(act).Min, ExpectedObjectives(act).Max), context);
                    Assert.That(plan.Objectives[0].Kind, Is.EqualTo(eObjectiveKind.ClearCamp), context);
                    Assert.That(plan.Objectives[0].Count, Is.InRange(ActGenerator.ClearCountRange(act).Min, ActGenerator.ClearCountRange(act).Max), context);
                    Assert.That(plan.Objectives[0].Radius, Is.EqualTo(1500), context);
                    Assert.That(plan.BossSpawned || plan.BossKilled, Is.False, context);
                });

                for (int i = 0; i < plan.Objectives.Count; i++)
                {
                    ActObjective objective = plan.Objectives[i];

                    Assert.Multiple(() =>
                    {
                        Assert.That(objective.Id, Is.EqualTo($"a{act}-{i + 1}"), context);
                        Assert.That(objective.Count, Is.GreaterThan(0), context);
                        Assert.That(objective.Progress, Is.Zero, context);
                        Assert.That(objective.Done, Is.False, context);
                        Assert.That(objective.RegionId, Is.EqualTo(plan.RegionId), context);
                        Assert.That(objective.Radius, Is.GreaterThan(0), context);
                        Assert.That(objective.Description, Does.Contain(plan.ZoneName), context);
                        Assert.That(InsideZone(plan.ZoneId, objective.X, objective.Y), Is.True, context + " " + objective.Id);
                    });

                    if (objective.Kind == eObjectiveKind.ReachPoint)
                    {
                        Assert.That(objective.Radius, Is.EqualTo(400), context);
                    }
                    else
                    {
                        Assert.That(objective.MobName, Is.Not.Empty, context);
                    }
                }

                Assert.That(plan.Objectives.Count(o => o.Kind == eObjectiveKind.ClearCamp), Is.GreaterThanOrEqualTo(1), context);
                Assert.Multiple(() =>
                {
                    Assert.That(plan.Boss, Is.Not.Null, context);
                    Assert.That(plan.Boss.Level, Is.EqualTo(max + 2), context);
                    Assert.That(plan.Boss.HealthMultiplier, Is.EqualTo(3 + act), context);
                    Assert.That(plan.Boss.Title, Is.EqualTo(ActGenerator.BossTitle(act, seed, plan.ZoneName)), context);
                    Assert.That(plan.Boss.BaseMobName, Is.Not.Empty, context);
                    Assert.That(plan.Boss.RegionId, Is.EqualTo(plan.RegionId), context);
                    Assert.That(InsideZone(plan.ZoneId, plan.Boss.X, plan.Boss.Y), Is.True, context + " boss");
                });
            }
        }
    }

    [Test]
    public void MidgardPlans_StayInMidgard()
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            for (int seed = 0; seed < 40; seed++)
            {
                ActPlan plan = FromCatalog(act, eRealm.Midgard, seed, -1);

                Assert.That(MidgardRegions, Does.Contain(plan.RegionId), $"act {act} seed {seed} zone {plan.ZoneName}");
            }
        }
    }

    [Test]
    public void PreviousZone_IsExcludedWhenAlternativesExist([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            IReadOnlyList<ActGenerator.ZoneCandidate> candidates = ActGenerator.GetEligibleCandidates(act, realm, CatalogCampSource.Instance);

            if (candidates.Select(c => c.ZoneId).Distinct().Count() < 2)
            {
                continue;
            }

            foreach (ActGenerator.ZoneCandidate previous in candidates)
            {
                for (int seed = 0; seed < 30; seed++)
                {
                    ActPlan plan = FromCatalog(act, realm, seed, previous.ZoneId);

                    Assert.That(plan.ZoneId, Is.Not.EqualTo(previous.ZoneId), $"{realm} act {act} seed {seed}");
                }
            }
        }
    }

    [Test]
    public void ZoneTable_MatchesZonesXml([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        Dictionary<int, (int Region, string Name)> zonesXml = LoadZonesXml();

        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            (int min, int max) = ActGenerator.GetLevelBand(act);

            Assert.That(ActGenerator.HasOwnCandidates(act, realm), Is.True, $"{realm} act {act} has no zones");

            foreach (ActGenerator.ZoneCandidate zone in ActGenerator.GetCandidates(act, realm))
            {
                string context = $"{realm} act {act} zone {zone.ZoneId} {zone.ZoneName}";
                List<AutonomousCapnBryGoalCatalog.Entry> goals = CatalogCampSource.Instance.GetZoneCamps(zone.RegionId, zone.ZoneId).ToList();

                Assert.That(zonesXml.ContainsKey(zone.ZoneId), Is.True, context + " is not in Zones.xml");
                Assert.Multiple(() =>
                {
                    Assert.That(zonesXml[zone.ZoneId].Region, Is.EqualTo(zone.RegionId), context + " region");
                    Assert.That(zonesXml[zone.ZoneId].Name, Is.EqualTo(zone.ZoneName), context + " name");
                    Assert.That(FrontierZones.Contains((zone.RegionId, zone.ZoneId)), Is.False, context + " is a frontier zone");
                    Assert.That(FrontierRegions, Does.Not.Contain(zone.RegionId), context + " is in a frontier region");
                    Assert.That((zone.MinLevel, zone.MaxLevel), Is.EqualTo((min, max)), context);
                    Assert.That(zone.BossCandidates.Where(name => goals.All(e => e.Name != name)), Is.Empty, context + " boss names");
                });
            }
        }
    }

    [Test]
    public void CatalogSource_EveryActHasAListedZoneWithEnoughCamps([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            (int min, int max) = ActGenerator.GetLevelBand(act);
            IReadOnlyList<ActGenerator.ZoneCandidate> listed = ActGenerator.GetCandidates(act, realm);
            IReadOnlyList<ActGenerator.ZoneCandidate> eligible = ActGenerator.GetEligibleCandidates(act, realm, CatalogCampSource.Instance);

            Assert.That(eligible, Is.Not.Empty, $"{realm} act {act}");

            // The CapnBry catalog has no Hibernian mainland camps at levels 32-44 (only Darkness Falls, now act 5 only);
            // the world Mob table the game reads first has them, and the zone table was checked against it.
            if (realm == eRealm.Hibernia && act == 4)
            {
                continue;
            }

            Assert.That(eligible, Is.SubsetOf(listed), $"{realm} act {act} needed the fallback");

            foreach (ActGenerator.ZoneCandidate zone in eligible)
            {
                Assert.That(ActGenerator.CountBandCamps(CatalogCampSource.Instance, zone, min, max), Is.GreaterThanOrEqualTo(ActGenerator.MinimumBandCamps),
                    $"{realm} act {act} {zone.ZoneName}");
            }
        }
    }

    [Test]
    public void ThinListedZones_FallBackToTheRealmZoneWithMostBandCamps()
    {
        // Midgard act 1 lists Vale of Mularn (100); give it only 2 band camps, and give Delling Crater
        // (153, an act-4 zone) 4 level-5 camps and Vanern Swamp (107) 3: Delling Crater wins.
        FakeCampSource source = new FakeCampSource()
            .Add(100, 100, "wolf", 5, 1000, 1000)
            .Add(100, 100, "bear", 6, 2000, 2000)
            .Add(100, 100, "troll", 40, 3000, 3000)
            .Add(151, 153, "imp", 4, 10000, 10000)
            .Add(151, 153, "imp", 5, 20000, 10000)
            .Add(151, 153, "gnat", 6, 10000, 20000)
            .Add(151, 153, "gnat", 7, 20000, 20000)
            .Add(100, 107, "rat", 3, 5000, 5000)
            .Add(100, 107, "rat", 3, 6000, 5000)
            .Add(100, 107, "rat", 3, 7000, 5000);

        IReadOnlyList<ActGenerator.ZoneCandidate> eligible = ActGenerator.GetEligibleCandidates(1, eRealm.Midgard, source);
        ActPlan plan = ActGenerator.Generate(1, eRealm.Midgard, 3, -1, FakeOffset, source);

        Assert.Multiple(() =>
        {
            Assert.That(eligible.Select(z => z.ZoneId), Is.EqualTo(new[] { 153 }));
            Assert.That(plan.ZoneId, Is.EqualTo(153));
            Assert.That(plan.RegionId, Is.EqualTo(151));
            Assert.That(plan.Boss.Level, Is.EqualTo(12));
        });
    }

    [Test]
    public void ComputedBoss_PrefersTheHighestNamedMobOfTheBandOrAbove()
    {
        // Vale of Mularn has no json boss names. "Grak" (1 spawn, level 13) beats "Snorri" (2
        // spawns, level 11); "Old Wyrm" (1 spawn, level 40) is too far above the band.
        FakeCampSource source = new FakeCampSource()
            .Add(100, 100, "wolf", 5, 1000, 1000, 6)
            .Add(100, 100, "wolf", 6, 9000, 1000, 6)
            .Add(100, 100, "boar", 8, 1000, 9000, 5)
            .Add(100, 100, "Snorri", 11, 20000, 20000, 2)
            .Add(100, 100, "Grak", 13, 30000, 30000, 1)
            .Add(100, 100, "Old Wyrm", 40, 40000, 40000, 1);

        for (int seed = 0; seed < 10; seed++)
        {
            ActPlan plan = ActGenerator.Generate(1, eRealm.Midgard, seed, -1, FakeOffset, source);

            Assert.Multiple(() =>
            {
                Assert.That(plan.ZoneId, Is.EqualTo(100), $"seed {seed}");
                Assert.That(plan.Boss.BaseMobName, Is.EqualTo("Grak"), $"seed {seed}");
                Assert.That(plan.Boss.Level, Is.EqualTo(12), $"seed {seed}");
                Assert.That((plan.Boss.X, plan.Boss.Y), Is.EqualTo((FakeOffset(100).ox + 30000, FakeOffset(100).oy + 30000)), $"seed {seed}");
            });
        }
    }

    [Test]
    public void ComputedBoss_WithoutANamedMob_IsTheMostCommonTopMob()
    {
        FakeCampSource source = new FakeCampSource()
            .Add(100, 100, "wolf", 3, 1000, 1000, 6)
            .Add(100, 100, "boar", 9, 9000, 1000, 4)
            .Add(100, 100, "boar", 9, 1000, 9000, 4)
            .Add(100, 100, "bandit", 10, 20000, 20000, 3);

        ActPlan plan = ActGenerator.Generate(1, eRealm.Midgard, 1, -1, FakeOffset, source);

        Assert.That(plan.Boss.BaseMobName, Is.EqualTo("boar"));
    }

    [Test]
    public void RealmWithoutZones_FallsBackInsteadOfThrowing()
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            ActPlan plan = null;

            Assert.That(ActGenerator.HasOwnCandidates(act, eRealm.None), Is.False);
            Assert.DoesNotThrow(() => plan = FromCatalog(act, eRealm.None, 7, -1));
            Assert.That(plan.Objectives, Is.Not.Empty);
            Assert.That(plan.Boss.Level, Is.EqualTo(ActGenerator.GetLevelBand(act).Max + 2));
        }
    }

    [Test]
    public void JsonRoundTrip_PreservesEveryField()
    {
        ActPlan plan = FromCatalog(3, eRealm.Hibernia, 99, -1);
        plan.Objectives[0].Progress = 3;
        plan.Objectives[1].Progress = plan.Objectives[1].Count;
        plan.BossSpawned = true;

        string json = Serialize(plan);
        ActPlan copy = JsonSerializer.Deserialize<ActPlan>(json, ExpeditionJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(Serialize(copy), Is.EqualTo(json));
            Assert.That(json, Does.Not.Contain("\"Done\""));
            Assert.That(copy.ZoneId, Is.EqualTo(plan.ZoneId));
            Assert.That(copy.CampX, Is.EqualTo(plan.CampX));
            Assert.That(copy.CampHeading, Is.EqualTo(plan.CampHeading));
            Assert.That(copy.Objectives.Count, Is.EqualTo(plan.Objectives.Count));
            Assert.That(copy.Objectives[0].Progress, Is.EqualTo(3));
            Assert.That(copy.Objectives[0].Kind, Is.EqualTo(plan.Objectives[0].Kind));
            Assert.That(copy.Objectives[0].X, Is.EqualTo(plan.Objectives[0].X));
            Assert.That(copy.Objectives[1].Done, Is.True);
            Assert.That(copy.Boss.Title, Is.EqualTo(plan.Boss.Title));
            Assert.That(copy.Boss.HealthMultiplier, Is.EqualTo(plan.Boss.HealthMultiplier));
            Assert.That(copy.BossSpawned, Is.True);
            Assert.That(copy.BossKilled, Is.False);
        });
    }

    [Test]
    public void DescribePlan_ListsZoneObjectivesAndBoss()
    {
        ActPlan plan = FromCatalog(1, eRealm.Albion, 5, -1);
        ActObjective clear = plan.Objectives[0];
        clear.Progress = clear.Count;

        IReadOnlyList<string> lines = ActGenerator.DescribePlan(plan);

        Assert.Multiple(() =>
        {
            Assert.That(lines.Count, Is.EqualTo(plan.Objectives.Count + 2));
            Assert.That(lines[0], Does.Contain(plan.ZoneName).And.Contain("1-10"));
            Assert.That(lines[1], Does.StartWith("[done]").And.EndWith($"({clear.Count}/{clear.Count})"));
            Assert.That(lines[2], Does.StartWith("[ ]").And.EndWith("(0/" + plan.Objectives[1].Count + ")"));
            Assert.That(lines[^1], Does.Contain(plan.Boss.Title).And.Contain(plan.Boss.BaseMobName));
        });
    }

    private static (int Min, int Max) ExpectedObjectives(int act)
    {
        return act switch
        {
            1 => (2, 2),
            2 => (3, 3),
            3 => (3, 4),
            4 => (4, 4),
            _ => (4, 5)
        };
    }

    [Test]
    public void ObjectiveCount_GrowsWithTheAct()
    {
        for (int seed = 0; seed < 200; seed++)
        {
            for (int act = 1; act <= ActGenerator.ActCount; act++)
            {
                int count = ActGenerator.ObjectiveCount(act, new System.Random(seed));
                (int min, int max) = ExpectedObjectives(act);

                Assert.That(count, Is.InRange(min, max), $"act {act} seed {seed}");
            }
        }

        Assert.That(ActGenerator.ClearCountRange(1), Is.EqualTo((5, 8)));
        Assert.That(ActGenerator.ClearCountRange(5), Is.EqualTo((9, 12)));
    }

    [Test]
    public void BossTitle_ThreePointOhThenWardenThenNamedBossesWithoutRepeats()
    {
        for (int seed = 0; seed < 500; seed++)
        {
            Assert.That(ActGenerator.BossTitle(1, seed, "Connacht"), Is.EqualTo("Three Point Oh"));
            Assert.That(ActGenerator.BossTitle(2, seed, "Lough Gur"), Is.EqualTo("Warden of Lough Gur"));

            List<string> named = new() { ActGenerator.BossTitle(3, seed, "a"), ActGenerator.BossTitle(4, seed, "b"), ActGenerator.BossTitle(5, seed, "c") };

            Assert.That(named, Is.All.Matches<string>(name => ActGenerator.NamedBosses.Contains(name)), $"seed {seed}");
            Assert.That(named.Distinct().Count(), Is.EqualTo(3), $"seed {seed}: no name twice in a run");
            Assert.That(ActGenerator.BossTitle(4, seed, "x"), Is.EqualTo(named[1]), $"seed {seed}: the same seed gives the same boss");
        }
    }

    [Test]
    public void Shrines_ThreeDifferentGroupsInsideTheZoneAwayFromTheCamp()
    {
        int withShrines = 0;

        foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
        {
            for (int act = 1; act <= ActGenerator.ActCount; act++)
            {
                for (int seed = 0; seed < 15; seed++)
                {
                    ActPlan plan = FromCatalog(act, realm, seed, -1);
                    string context = $"{realm} act {act} seed {seed} zone {plan.ZoneName}";

                    Assert.That(plan.Shrines.Count, Is.LessThanOrEqualTo(ExpeditionShrines.ShrinesPerAct), context);
                    Assert.That(plan.Shrines.Select(s => s.Group).Distinct().Count(), Is.EqualTo(plan.Shrines.Count), context);

                    foreach (ShrineSpec shrine in plan.Shrines)
                    {
                        long dx = shrine.X - plan.CampX;
                        long dy = shrine.Y - plan.CampY;

                        Assert.That(ExpeditionBuffs.Groups, Does.Contain(shrine.Group), context);
                        Assert.That(dx * dx + dy * dy, Is.GreaterThan((long) ExpeditionShrines.MinDistance * ExpeditionShrines.MinDistance), context + " camp distance");
                        Assert.That(shrine.Discovered || shrine.Used, Is.False, context);
                        Assert.That(shrine.Direction, Does.Contain("camp"), context);
                    }

                    if (plan.Shrines.Count == ExpeditionShrines.ShrinesPerAct)
                    {
                        withShrines++;
                    }
                }
            }
        }

        Assert.That(withShrines, Is.GreaterThan(150), "most acts have all three shrines");
    }

    [Test]
    public void LesserBuffs_SixtyPercentOfTheCapAndGrantedByBoonsAndUsedShrines()
    {
        Assert.That(ExpeditionBuffs.StatValue(50), Is.EqualTo(56), "60% of 1.875 x 50");
        Assert.That(ExpeditionBuffs.StatValue(1), Is.EqualTo(1));

        var run = new ExpeditionRun
        {
            CurrentAct = new ActPlan
            {
                Shrines =
                [
                    new ShrineSpec { Group = ExpeditionBuffs.Armor, Used = true },
                    new ShrineSpec { Group = ExpeditionBuffs.Endurance, Used = false }
                ]
            }
        };
        run.Boons.Add(ExpeditionBoons.BlessedArms);

        HashSet<string> wanted = ExpeditionBuffs.Wanted(run);

        Assert.That(wanted, Is.EquivalentTo(new[] { ExpeditionBuffs.StrengthConstitution, ExpeditionBuffs.DexterityQuickness, ExpeditionBuffs.Armor }));
    }

    [Test]
    public void Generate_ForcedZone_ReplansTheSameZone()
    {
        foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
        {
            for (int seed = 0; seed < 20; seed++)
            {
                ActPlan plan = FromCatalog(2, realm, seed, -1);
                ActPlan again = ActGenerator.Generate(2, realm, seed, -1, FakeOffset, CatalogCampSource.Instance, plan.ZoneId);

                Assert.That(again, Is.Not.Null, $"{realm} seed {seed}");
                Assert.That(again.ZoneId, Is.EqualTo(plan.ZoneId), $"{realm} seed {seed}");
                Assert.That(again.Objectives.Select(o => o.Description), Is.EqualTo(plan.Objectives.Select(o => o.Description)), $"{realm} seed {seed}: same zone, same plan");
            }
        }

        Assert.That(ActGenerator.Generate(2, eRealm.Hibernia, 1, -1, FakeOffset, CatalogCampSource.Instance, 99999), Is.Null, "a zone the act cannot use");
    }

    [Test]
    public void BossQuote_OnlyForBossesWithALine()
    {
        Assert.That(ActGenerator.BossQuote("Three Point Oh"), Does.StartWith("You want this ring?"));
        Assert.That(ActGenerator.BossQuote("Warden of Connacht"), Is.Null);
        Assert.That(ActGenerator.BossQuote(null), Is.Null);
    }

    [Test]
    public void Candidates_NoDungeonsBeforeActFive([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        // Owner, 2026-10-25: Stonehenge Barrows, Nisse's Lair, Cursed Tomb, Spindelhalla, Varulvhamn, Muire Tomb, Spraggon Den.
        int[] dungeonRegions = [20, 129, 128, 125, 127, 221, 222];

        for (int act = 1; act < ActGenerator.ActCount; act++)
        {
            foreach (int regionId in ActGenerator.GetCandidates(act, realm).Select(z => z.RegionId))
            {
                Assert.That(dungeonRegions, Does.Not.Contain(regionId), $"{realm} act {act}");
            }
        }
    }

    [Test]
    public void RunZonePlan_NoZoneTwiceAndDarknessFallsLast([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        bool dfListed = true;

        for (int act = 1; act < ActGenerator.ActCount; act++)
        {
            Assert.That(ActGenerator.GetCandidates(act, realm).Select(z => z.ZoneId), Has.No.Member(ActGenerator.DarknessFallsZoneId), $"{realm} act {act}");
        }

        Assert.That(ActGenerator.GetCandidates(ActGenerator.ActCount, realm).Select(z => z.ZoneId), Is.EqualTo(new[] { ActGenerator.DarknessFallsZoneId }), $"{realm} act 5");

        for (int seed = 0; seed < 200; seed++)
        {
            int[] zones = ActGenerator.RunZonePlan(realm, seed, CatalogCampSource.Instance);
            List<int> acts = Enumerable.Range(1, ActGenerator.ActCount).Select(act => zones[act]).ToList();

            Assert.That(acts, Has.None.EqualTo(-1), $"{realm} seed {seed}");
            Assert.That(acts.Distinct().Count(), Is.EqualTo(acts.Count), $"{realm} seed {seed}: a zone repeats");

            if (dfListed)
            {
                Assert.That(acts[^1], Is.EqualTo(ActGenerator.DarknessFallsZoneId), $"{realm} seed {seed}");
            }

            for (int act = 1; act <= ActGenerator.ActCount; act++)
            {
                Assert.That(FromCatalog(act, realm, seed, -1).ZoneId, Is.EqualTo(zones[act]), $"{realm} act {act} seed {seed}: Generate follows the plan");
            }
        }
    }
}
