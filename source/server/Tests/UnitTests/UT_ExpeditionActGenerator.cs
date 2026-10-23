using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionActGenerator
{
    private const int ZoneExtent = 65536;

    // A distinct fake offset per zone, so no test needs a loaded world.
    private static (int ox, int oy) FakeOffset(int zoneId) => (100000 + zoneId * 70000, 200000 + zoneId * 70000);

    private static string Serialize(ActPlan plan) => JsonSerializer.Serialize(plan, ExpeditionJson.Options);

    private static bool InsideZone(int zoneId, int x, int y)
    {
        (int ox, int oy) = FakeOffset(zoneId);

        return x >= ox && x < ox + ZoneExtent && y >= oy && y < oy + ZoneExtent;
    }

    [Test]
    public void SameSeed_ProducesIdenticalPlan([Values(eRealm.Albion, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            string first = Serialize(ActGenerator.Generate(act, realm, 12345, -1, FakeOffset));
            string second = Serialize(ActGenerator.Generate(act, realm, 12345, -1, FakeOffset));

            Assert.That(second, Is.EqualTo(first), $"act {act}");
        }
    }

    [Test]
    public void DifferentSeeds_ProduceDifferentPlans([Values(eRealm.Albion, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            HashSet<string> distinct = new();

            for (int seed = 0; seed < 20; seed++)
            {
                distinct.Add(Serialize(ActGenerator.Generate(act, realm, seed, -1, FakeOffset)));
            }

            Assert.That(distinct.Count, Is.GreaterThan(1), $"act {act}");
        }
    }

    [Test]
    public void EveryAct_GeneratesAValidPlan([Values(eRealm.Albion, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            (int min, int max) = ActGenerator.GetLevelBand(act);

            for (int seed = 0; seed < 25; seed++)
            {
                ActPlan plan = ActGenerator.Generate(act, realm, seed, -1, FakeOffset);
                string context = $"{realm} act {act} seed {seed} zone {plan.ZoneName}";

                Assert.Multiple(() =>
                {
                    Assert.That(plan.Act, Is.EqualTo(act), context);
                    Assert.That((plan.MinLevel, plan.MaxLevel), Is.EqualTo((min, max)), context);
                    Assert.That(plan.ZoneName, Is.Not.Empty, context);
                    Assert.That(InsideZone(plan.ZoneId, plan.CampX, plan.CampY), Is.True, context + " camp");
                    Assert.That(plan.CampHeading, Is.LessThan(4096), context);
                    Assert.That(plan.Objectives.Count, Is.InRange(2, 3), context);
                    Assert.That(plan.Objectives[0].Kind, Is.EqualTo(eObjectiveKind.ClearCamp), context);
                    Assert.That(plan.Objectives[0].Count, Is.InRange(5, 8), context);
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

                Assert.That(plan.Objectives.Select(o => o.Kind).Distinct().Count(), Is.EqualTo(plan.Objectives.Count), context);
                Assert.Multiple(() =>
                {
                    Assert.That(plan.Boss, Is.Not.Null, context);
                    Assert.That(plan.Boss.Level, Is.EqualTo(max + 2), context);
                    Assert.That(plan.Boss.HealthMultiplier, Is.EqualTo(3 + act), context);
                    Assert.That(plan.Boss.Title, Is.EqualTo($"Warden of {plan.ZoneName}"), context);
                    Assert.That(plan.Boss.BaseMobName, Is.Not.Empty, context);
                    Assert.That(plan.Boss.RegionId, Is.EqualTo(plan.RegionId), context);
                    Assert.That(InsideZone(plan.ZoneId, plan.Boss.X, plan.Boss.Y), Is.True, context + " boss");
                });
            }
        }
    }

    [Test]
    public void PreviousZone_IsExcludedWhenAlternativesExist([Values(eRealm.Albion, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            IReadOnlyList<ActGenerator.ZoneCandidate> candidates = ActGenerator.GetCandidates(act, realm);

            if (candidates.Select(c => c.ZoneId).Distinct().Count() < 2)
            {
                continue;
            }

            foreach (ActGenerator.ZoneCandidate previous in candidates)
            {
                for (int seed = 0; seed < 30; seed++)
                {
                    ActPlan plan = ActGenerator.Generate(act, realm, seed, previous.ZoneId, FakeOffset);

                    Assert.That(plan.ZoneId, Is.Not.EqualTo(previous.ZoneId), $"{realm} act {act} seed {seed}");
                }
            }
        }
    }

    [Test]
    public void ZoneTable_MatchesTheGoalCatalog([Values(eRealm.Albion, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            (int min, int max) = ActGenerator.GetLevelBand(act);

            Assert.That(ActGenerator.HasOwnCandidates(act, realm), Is.True, $"{realm} act {act} has no zones");

            foreach (ActGenerator.ZoneCandidate zone in ActGenerator.GetCandidates(act, realm))
            {
                List<AutonomousCapnBryGoalCatalog.Entry> goals = AutonomousCapnBryGoalCatalog.Entries
                    .Where(e => e.ZoneId == zone.ZoneId && e.RegionId == zone.RegionId)
                    .ToList();
                string context = $"{realm} act {act} {zone.ZoneName}";

                Assert.Multiple(() =>
                {
                    Assert.That(goals, Is.Not.Empty, context);
                    Assert.That(goals.All(e => e.Zone == zone.ZoneName), Is.True, context + " name");
                    Assert.That((zone.MinLevel, zone.MaxLevel), Is.EqualTo((min, max)), context);
                    Assert.That(zone.BossCandidates.Where(name => goals.All(e => e.Name != name)), Is.Empty, context + " boss names");
                });
            }
        }
    }

    [Test]
    public void RealmWithoutZones_FallsBackInsteadOfThrowing()
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            ActPlan plan = null;

            Assert.DoesNotThrow(() => plan = ActGenerator.Generate(act, eRealm.Midgard, 7, -1, FakeOffset));
            Assert.That(plan.Objectives, Is.Not.Empty);
            Assert.That(plan.Boss.Level, Is.EqualTo(ActGenerator.GetLevelBand(act).Max + 2));
        }
    }

    [Test]
    public void JsonRoundTrip_PreservesEveryField()
    {
        ActPlan plan = ActGenerator.Generate(3, eRealm.Hibernia, 99, -1, FakeOffset);
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
        ActPlan plan = ActGenerator.Generate(1, eRealm.Albion, 5, -1, FakeOffset);
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
}
