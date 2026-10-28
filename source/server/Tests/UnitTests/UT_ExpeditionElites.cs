using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionElites
{
    private const int ZoneExtent = 65536;

    // A distinct fake offset per zone, so no test needs a loaded world (as UT_ExpeditionActGenerator).
    private static (int ox, int oy) FakeOffset(int zoneId) => (100000 + zoneId * 70000, 200000 + zoneId * 70000);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ExpeditionJson.Options);

    private static ActPlan FromCatalog(int act, eRealm realm, int seed)
    {
        return ActGenerator.Generate(act, realm, seed, -1, FakeOffset, CatalogCampSource.Instance);
    }

    private static long DistanceSquared(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;

        return dx * dx + dy * dy;
    }

    private sealed class FakeCampSource : IExpeditionCampSource
    {
        private readonly List<AutonomousCapnBryGoalCatalog.Entry> _camps = new();

        public FakeCampSource Add(string name, int level, int localX, int localY, int sightings)
        {
            _camps.Add(new AutonomousCapnBryGoalCatalog.Entry
            {
                Id = $"fake:100:{_camps.Count}",
                Name = name,
                ZoneId = 100,
                RegionId = 100,
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
            return regionId == 100 && zoneId == 100 ? _camps : [];
        }
    }

    // Vale of Mularn (Midgard act 1, no json camp or boss names): a tight cluster of band camps for the objectives,
    // "Grak" (level 13, one spawn) as the computed boss, and two packs above the band but within its slack:
    // "ghoul" far to the south-east (the farthest camp, so it may be the reach point) and "wight" to the south.
    private static FakeCampSource ValeWithTwoPacks()
    {
        return new FakeCampSource()
            .Add("wolf", 5, 1000, 1000, 3)
            .Add("wolf", 6, 1500, 1000, 3)
            .Add("boar", 7, 1000, 1500, 3)
            .Add("bear", 8, 1500, 1500, 3)
            .Add("Grak", 13, 2000, 2000, 1)
            .Add("ghoul", 12, 40000, 40000, 3)
            .Add("wight", 11, 1000, 40000, 3);
    }

    [Test]
    public void AffixPool_IsTheSixKnownAffixes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionElites.Affixes, Is.EqualTo(new[] { "Swift", "Venomous", "Armoured", "Stalwart", "Frenzied", "Spectral" }));
            Assert.That(ExpeditionElites.Affixes, Is.Unique);
        });
    }

    [Test]
    public void EliteName_PrefixesTheAffix()
    {
        EliteCamp camp = new() { MobName = "goblin scout", Affix = ExpeditionElites.Swift };

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionElites.EliteName(ExpeditionElites.Swift, "goblin scout"), Is.EqualTo("Swift goblin scout"));
            Assert.That(ExpeditionElites.ApproachMessage(camp), Is.EqualTo("You sense a Swift pack of goblin scout nearby."));
        });
    }

    [Test]
    public void DescribeFromCamp_UsesDolHeadings()
    {
        Assert.Multiple(() =>
        {
            // DOL: +Y is south, -Y north, +X east, -X west.
            Assert.That(ActGenerator.DescribeFromCamp(50000, 50000, 50000, 45000), Is.EqualTo("to the north of the camp"));
            Assert.That(ActGenerator.DescribeFromCamp(50000, 50000, 50000, 55000), Is.EqualTo("to the south of the camp"));
            Assert.That(ActGenerator.DescribeFromCamp(50000, 50000, 55000, 50000), Is.EqualTo("to the east of the camp"));
            Assert.That(ActGenerator.DescribeFromCamp(50000, 50000, 45000, 50000), Is.EqualTo("to the west of the camp"));
            Assert.That(ActGenerator.DescribeFromCamp(50000, 50000, 55000, 45000), Is.EqualTo("to the north-east of the camp"));
            Assert.That(ActGenerator.DescribeFromCamp(50000, 50000, 45000, 55000), Is.EqualTo("to the south-west of the camp"));
            Assert.That(ActGenerator.DescribeFromCamp(50000, 50000, 50200, 50100), Is.EqualTo("right by the camp"));
        });
    }

    [Test]
    public void CatalogPlans_ElitesFollowTheSelectionRules([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        int plansWithElites = 0;

        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            (int min, int max) = ActGenerator.GetLevelBand(act);

            for (int seed = 0; seed < 25; seed++)
            {
                ActPlan plan = FromCatalog(act, realm, seed);
                string context = $"{realm} act {act} seed {seed} zone {plan.ZoneName}";
                (int ox, int oy) = FakeOffset(plan.ZoneId);

                Assert.That(plan.Elites, Is.Not.Null, context);
                Assert.That(plan.Elites.Count, Is.InRange(0, 2), context);

                if (plan.Elites.Count > 0)
                {
                    plansWithElites++;
                }

                foreach (EliteCamp elite in plan.Elites)
                {
                    string eliteContext = $"{context} elite {elite.Affix} {elite.MobName}";

                    Assert.Multiple(() =>
                    {
                        Assert.That(ExpeditionElites.Affixes, Does.Contain(elite.Affix), eliteContext);
                        Assert.That(elite.MobName, Is.Not.Empty, eliteContext);
                        Assert.That(elite.Radius, Is.GreaterThan(0), eliteContext);
                        Assert.That(elite.RegionId, Is.EqualTo(plan.RegionId), eliteContext);
                        Assert.That(elite.X >= ox && elite.X < ox + ZoneExtent && elite.Y >= oy && elite.Y < oy + ZoneExtent, Is.True, eliteContext + " inside the zone");
                        Assert.That(elite.Level, Is.GreaterThanOrEqualTo(min + 2), eliteContext);
                        Assert.That(elite.Description, Does.StartWith($"A {elite.Affix} pack of {elite.MobName} lairs "), eliteContext);
                        Assert.That(elite.Description, Does.EndWith("camp"), eliteContext);
                        Assert.That(string.Equals(elite.MobName, plan.Boss.BaseMobName, StringComparison.OrdinalIgnoreCase), Is.False, eliteContext + " is the boss");
                    });

                    foreach (ActObjective objective in plan.Objectives)
                    {
                        Assert.Multiple(() =>
                        {
                            Assert.That(string.Equals(elite.MobName, objective.MobName, StringComparison.OrdinalIgnoreCase), Is.False, $"{eliteContext} shares a name with {objective.Id}");
                            Assert.That(DistanceSquared(elite.X, elite.Y, objective.X, objective.Y), Is.GreaterThan(1500L * 1500L), $"{eliteContext} is within 1500 of {objective.Id}");
                        });
                    }
                }

                if (plan.Elites.Count == 2)
                {
                    EliteCamp a = plan.Elites[0];
                    EliteCamp b = plan.Elites[1];

                    Assert.Multiple(() =>
                    {
                        Assert.That(string.Equals(a.MobName, b.MobName, StringComparison.OrdinalIgnoreCase), Is.False, context + " two packs of one mob");
                        Assert.That(DistanceSquared(a.X, a.Y, b.X, b.Y), Is.GreaterThan(1500L * 1500L), context + " packs too close");
                    });
                }
            }
        }

        Assert.That(plansWithElites, Is.GreaterThan(0), $"{realm}: no catalog plan has an elite pack");
    }

    [Test]
    public void SameSeed_ProducesTheSameElites([Values(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)] eRealm realm)
    {
        for (int act = 1; act <= ActGenerator.ActCount; act++)
        {
            for (int seed = 0; seed < 5; seed++)
            {
                string first = Serialize(FromCatalog(act, realm, seed).Elites);
                string second = Serialize(FromCatalog(act, realm, seed).Elites);

                Assert.That(second, Is.EqualTo(first), $"{realm} act {act} seed {seed}");
            }
        }
    }

    [Test]
    public void FakeZone_PicksOnlyTheFarPacksOutsideTheObjectives()
    {
        FakeCampSource source = ValeWithTwoPacks();
        (int ox, int oy) = FakeOffset(100);
        HashSet<string> affixes = new();
        int twoPacks = 0;

        for (int seed = 0; seed < 100; seed++)
        {
            ActPlan plan = ActGenerator.Generate(1, eRealm.Midgard, seed, -1, FakeOffset, source);
            string context = $"seed {seed}";

            Assert.Multiple(() =>
            {
                Assert.That(plan.ZoneId, Is.EqualTo(100), context);
                Assert.That(plan.Boss.BaseMobName, Is.EqualTo("Grak"), context);
                Assert.That((plan.CampX, plan.CampY), Is.EqualTo((ox + 1000, oy + 1000)), context);
                Assert.That(plan.Elites.Count, Is.InRange(1, 2), context);
            });

            bool ghoulIsTheReachPoint = plan.Objectives.Any(o => o.Kind == eObjectiveKind.ReachPoint && o.X == ox + 40000 && o.Y == oy + 40000);

            foreach (EliteCamp elite in plan.Elites)
            {
                affixes.Add(elite.Affix);

                if (elite.MobName == "wight")
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That((elite.X, elite.Y, elite.Z), Is.EqualTo((ox + 1000, oy + 40000, 100)), context);
                        Assert.That(elite.Level, Is.EqualTo(13), context);
                        Assert.That(elite.Description, Is.EqualTo($"A {elite.Affix} pack of wight lairs to the south of the camp"), context);
                    });
                }
                else
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(elite.MobName, Is.EqualTo("ghoul"), context);
                        Assert.That(ghoulIsTheReachPoint, Is.False, context + " the reach point camp became elite");
                        Assert.That(elite.Level, Is.EqualTo(14), context);
                        Assert.That(elite.Description, Is.EqualTo($"A {elite.Affix} pack of ghoul lairs to the south-east of the camp"), context);
                    });
                }
            }

            if (plan.Elites.Count == 2)
            {
                twoPacks++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(twoPacks, Is.GreaterThan(0), "no seed chose two packs");
            Assert.That(affixes.Count, Is.GreaterThan(1), "every seed chose the same affix");
        });
    }

    [Test]
    public void JsonRoundTrip_KeepsElites()
    {
        ActPlan plan = ActGenerator.Generate(1, eRealm.Midgard, 3, -1, FakeOffset, ValeWithTwoPacks());
        string json = Serialize(plan);
        ActPlan copy = JsonSerializer.Deserialize<ActPlan>(json, ExpeditionJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Elites\""));
            Assert.That(Serialize(copy), Is.EqualTo(json));
            Assert.That(copy.Elites.Count, Is.EqualTo(plan.Elites.Count));
            Assert.That(copy.Elites[0].Affix, Is.EqualTo(plan.Elites[0].Affix));
            Assert.That(copy.Elites[0].Description, Is.EqualTo(plan.Elites[0].Description));
        });
    }

    [Test]
    public void JsonRoundTrip_KeepsSlainElites_AndOldCampsHaveNone()
    {
        ActPlan plan = ActGenerator.Generate(1, eRealm.Midgard, 3, -1, FakeOffset, ValeWithTwoPacks());
        plan.Elites[0].Slain.Add("spawn-1");
        plan.Elites[0].Slain.Add("spawn-2");

        ActPlan copy = JsonSerializer.Deserialize<ActPlan>(Serialize(plan), ExpeditionJson.Options);
        Assert.That(copy.Elites[0].Slain, Is.EqualTo(new[] { "spawn-1", "spawn-2" }));

        // A camp saved before slain elites were kept.
        JsonObject node = JsonNode.Parse(Serialize(plan)).AsObject();
        node["Elites"]![0]!.AsObject().Remove("Slain");
        ActPlan old = JsonSerializer.Deserialize<ActPlan>(node.ToJsonString(), ExpeditionJson.Options);
        Assert.That(old.Elites[0].Slain, Is.Not.Null.And.Empty);
    }

    [Test]
    public void JsonWithoutElites_DeserializesToAnEmptyList()
    {
        // A plan saved before elite packs existed.
        JsonObject node = JsonNode.Parse(Serialize(FromCatalog(2, eRealm.Albion, 11))).AsObject();
        node.Remove("Elites");
        string oldJson = node.ToJsonString();

        ActPlan old = JsonSerializer.Deserialize<ActPlan>(oldJson, ExpeditionJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(oldJson, Does.Not.Contain("Elites"));
            Assert.That(old, Is.Not.Null);
            Assert.That(old.Elites, Is.Not.Null.And.Empty);
            Assert.That(old.Objectives, Is.Not.Empty);
            Assert.That(ExpeditionElites.DescribeElites(old), Is.Empty);
        });
    }

    [Test]
    public void DescribeElites_OneLinePerPack()
    {
        ActPlan plan = new()
        {
            Elites =
            [
                new EliteCamp { MobName = "goblin scout", Affix = "Swift", Description = "A Swift pack of goblin scout lairs to the north-east of the camp" },
                new EliteCamp { MobName = "wight", Affix = "Spectral", Description = "A Spectral pack of wight lairs right by the camp" }
            ]
        };

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionElites.DescribeElites(plan), Is.EqualTo(new[]
            {
                "Elite: A Swift pack of goblin scout lairs to the north-east of the camp",
                "Elite: A Spectral pack of wight lairs right by the camp"
            }));
            Assert.That(ExpeditionElites.DescribeElites(null), Is.Empty);
            Assert.That(ExpeditionElites.DescribeElites(new ActPlan { Elites = null }), Is.Empty);
        });
    }

    [Test]
    public void IsElite_IsFalseForUnknownMonsters()
    {
        Assert.That(ExpeditionElites.IsElite(null), Is.False);
    }

    [Test]
    public void AffixEffect_DescribesEveryAffix()
    {
        foreach (string affix in ExpeditionElites.Affixes)
        {
            Assert.That(ExpeditionElites.AffixEffect(affix), Is.Not.Empty, affix);
        }

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionElites.AffixEffect(ExpeditionElites.Swift), Is.EqualTo("Runs 1.4x as fast, swings 20% faster."));
            Assert.That(ExpeditionElites.AffixEffect(ExpeditionElites.Stalwart), Is.EqualTo("Triple health; mez, stun and snare last 50% as long."));
            Assert.That(ExpeditionElites.AffixEffect("Unknown"), Is.Empty);
        });
    }
}
