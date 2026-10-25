using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOL.Database;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>The pure rules of <see cref="ExpeditionArrival"/>: no world, database or navigation mesh is needed.</summary>
[TestFixture]
public class UT_ExpeditionArrival
{
    private static string Serialize(ActPlan plan) => JsonSerializer.Serialize(plan, ExpeditionJson.Options);

    private static long DistanceSquared(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;

        return dx * dx + dy * dy;
    }

    private static ActPlan SamplePlan()
    {
        return new ActPlan
        {
            Act = 3,
            ZoneName = "Cursed Tomb",
            ZoneId = 128,
            RegionId = 128,
            CampX = 10000,
            CampY = 10000,
            CampZ = 100,
            CampHeading = 5,
            Objectives = [new ActObjective { Id = "a3-1", Kind = eObjectiveKind.ClearCamp, MobName = "ghoul", Count = 5, RegionId = 128, X = 12000, Y = 10000 }],
            Elites = [new EliteCamp { MobName = "ghoul", Affix = "Vampiric", RegionId = 128, X = 10000, Y = 15000, Description = "old" }],
            Shrines = [new ShrineSpec { Group = "might", RegionId = 128, X = 10000, Y = 15000, Direction = "old" }]
        };
    }

    [Test]
    public void RingCandidates_GoOutwardsWithinTheSearchRadius()
    {
        List<(int X, int Y)> points = ExpeditionArrival.RingCandidates(50000, 60000);
        List<long> distances = points.Select(p => DistanceSquared(p.X, p.Y, 50000, 60000)).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(points.Count, Is.GreaterThanOrEqualTo(8 * 8));
            Assert.That(points.Distinct().Count(), Is.EqualTo(points.Count), "no point twice");
            Assert.That(distances.Min(), Is.GreaterThan(0), "the origin itself is not a ring point");

            // Rounding to whole units may move a point by under a unit; rings are hundreds apart.
            for (int i = 1; i < distances.Count; i++)
            {
                Assert.That(Math.Sqrt(distances[i]), Is.GreaterThanOrEqualTo(Math.Sqrt(distances[i - 1]) - 1), $"point {i} is nearer than point {i - 1}");
            }

            Assert.That(Math.Sqrt(distances.Max()), Is.LessThanOrEqualTo(ExpeditionArrival.MaxSearchRadius + 1));
            Assert.That(ExpeditionArrival.RingCandidates(50000, 60000), Is.EqualTo(points), "deterministic");
        });
    }

    [Test]
    public void IsClear_UsesEachHazardsRadiusIn3D()
    {
        List<ExpeditionArrival.Hazard> hazards = [new ExpeditionArrival.Hazard(1000, 1000, 0, 800)];

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionArrival.IsClear(1000, 1000, 0, hazards), Is.False);
            Assert.That(ExpeditionArrival.IsClear(1800, 1000, 0, hazards), Is.False, "on the edge is not clear");
            Assert.That(ExpeditionArrival.IsClear(1801, 1000, 0, hazards), Is.True);
            Assert.That(ExpeditionArrival.IsClear(1000, 1000, 900, hazards), Is.True, "a floor far above is clear");
            Assert.That(ExpeditionArrival.IsClear(1000, 1000, 0, []), Is.True);
        });
    }

    [Test]
    public void ChooseNearestSafe_TakesTheNearestAcceptedCandidate()
    {
        (int X, int Y, int Z) origin = (0, 0, 0);
        List<(int X, int Y, int Z)> candidates = [(3000, 0, 0), origin, (500, 0, 0), (0, 1200, 0)];
        List<ExpeditionArrival.Hazard> hazards = [new ExpeditionArrival.Hazard(0, 0, 0, 1000)];

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionArrival.ChooseNearestSafe(origin, candidates, (x, y, z) => true), Is.EqualTo(origin), "a safe origin stays");
            Assert.That(ExpeditionArrival.ChooseNearestSafe(origin, candidates, (x, y, z) => ExpeditionArrival.IsClear(x, y, z, hazards)), Is.EqualTo((0, 1200, 0)));
            Assert.That(ExpeditionArrival.ChooseNearestSafe(origin, candidates, (x, y, z) => x > 1000), Is.EqualTo((3000, 0, 0)));
            Assert.That(ExpeditionArrival.ChooseNearestSafe(origin, candidates, (x, y, z) => false), Is.Null);
        });
    }

    [Test]
    public void ChooseEntrance_DarknessFallsUsesEachRealmsHomeEntrance()
    {
        // The installed Darkness Falls entrance rows (UT_AutonomousDarknessFallsPolicy), with landing points.
        List<DbZonePoint> rows =
        [
            new DbZonePoint { Id = 79, SourceRegion = 1, TargetRegion = 249, Realm = 1, TargetX = 100, TargetY = 100 },
            new DbZonePoint { Id = 81, SourceRegion = 1, TargetRegion = 249, Realm = 1, TargetX = 200, TargetY = 200 },
            new DbZonePoint { Id = 82, SourceRegion = 100, TargetRegion = 249, Realm = 2, TargetX = 300, TargetY = 300 },
            new DbZonePoint { Id = 83, SourceRegion = 100, TargetRegion = 249, Realm = 2, TargetX = 400, TargetY = 400 },
            new DbZonePoint { Id = 84, SourceRegion = 100, TargetRegion = 249, Realm = 2, TargetX = 500, TargetY = 500 },
            new DbZonePoint { Id = 86, SourceRegion = 200, TargetRegion = 249, Realm = 3, TargetX = 600, TargetY = 600 },
            new DbZonePoint { Id = 87, SourceRegion = 200, TargetRegion = 249, Realm = 0, TargetX = 700, TargetY = 700 },
            new DbZonePoint { Id = 70, SourceRegion = 249, TargetRegion = 100, Realm = 2, TargetX = 800, TargetY = 800 }
        ];

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionArrival.ChooseEntrance(rows, 249, eRealm.Albion, null)?.Id, Is.EqualTo(81));
            Assert.That(ExpeditionArrival.ChooseEntrance(rows, 249, eRealm.Midgard, null)?.Id, Is.EqualTo(84));
            Assert.That(ExpeditionArrival.ChooseEntrance(rows, 249, eRealm.Hibernia, null)?.Id, Is.EqualTo(87), "Hibernia's home entrance is the neutral row");
        });
    }

    [Test]
    public void ChooseEntrance_PrefersTheRealmsOwnRowInsideTheActZone()
    {
        List<DbZonePoint> rows =
        [
            new DbZonePoint { Id = 5, SourceRegion = 100, TargetRegion = 128, Realm = 0, TargetX = 32000, TargetY = 32000 },
            new DbZonePoint { Id = 9, SourceRegion = 100, TargetRegion = 128, Realm = 2, TargetX = 33000, TargetY = 33000 },
            new DbZonePoint { Id = 2, SourceRegion = 100, TargetRegion = 128, Realm = 2, TargetX = 0, TargetY = 0 },
            new DbZonePoint { Id = 3, SourceRegion = 128, TargetRegion = 128, Realm = 2, TargetX = 34000, TargetY = 34000 },
            new DbZonePoint { Id = 4, SourceRegion = 100, TargetRegion = 128, Realm = 2, TargetX = 900000, TargetY = 900000 },
            new DbZonePoint { Id = 1, SourceRegion = 100, TargetRegion = 127, Realm = 2, TargetX = 32000, TargetY = 32000 }
        ];
        Func<int, int, bool> inZone = (x, y) => x < 65536 && y < 65536;

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionArrival.ChooseEntrance(rows, 128, eRealm.Midgard, inZone)?.Id, Is.EqualTo(9),
                "not the empty landing (2), the one inside the region (3), the one outside the zone (4) or another region's (1)");
            Assert.That(ExpeditionArrival.ChooseEntrance(rows, 128, eRealm.Albion, inZone)?.Id, Is.EqualTo(5), "the neutral row before another realm's");
            Assert.That(ExpeditionArrival.ChooseEntrance(rows, 222, eRealm.Midgard, inZone), Is.Null);
            Assert.That(ExpeditionArrival.ChooseEntrance(null, 128, eRealm.Midgard, inZone), Is.Null);
        });
    }

    [Test]
    public void SpawnAggroRange_FollowsTheServersAggroRules()
    {
        (int, int)[] none = [];

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = 50, AggroRange = 450 }, eRealm.Midgard, none), Is.EqualTo(450));
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = 0, AggroRange = 450 }, eRealm.Midgard, none), Is.EqualTo(0));
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = -1, AggroRange = -1 }, eRealm.Midgard, none), Is.EqualTo(500), "automatic values above level 5");
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 3, AggroLevel = -1, AggroRange = -1 }, eRealm.Midgard, none), Is.EqualTo(0), "automatic values at level 5 or below");
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = 50, AggroRange = 9000 }, eRealm.Midgard, none), Is.EqualTo(DOL.AI.Brain.StandardMobBrain.MAX_AGGRO_DISTANCE));
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = 50, AggroRange = 450, Flags = (uint) GameNPC.eFlags.PEACE }, eRealm.Midgard, none), Is.EqualTo(0));
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = 50, AggroRange = 450, Realm = 2 }, eRealm.Midgard, none), Is.EqualTo(0), "the realm's own guards");
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = 50, AggroRange = 450, Realm = 1 }, eRealm.Midgard, none), Is.EqualTo(450));
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = 50, AggroRange = 450 }, eRealm.Midgard, [(0, 300)]), Is.EqualTo(0), "the template decides");
            Assert.That(ExpeditionArrival.SpawnAggroRange(new DbMob { Level = 20, AggroLevel = 0, AggroRange = 0 }, eRealm.Midgard, [(0, 300), (40, 700)]), Is.EqualTo(700));
            Assert.That(ExpeditionArrival.SpawnAggroRange(null, eRealm.Midgard, none), Is.EqualTo(0));
        });
    }

    [Test]
    public void MoveCamp_UpdatesWhatIsDescribedFromTheCamp()
    {
        ActPlan plan = SamplePlan();
        plan.ExchangerPlaced = true;
        plan.ExchangerInTown = false;

        ExpeditionArrival.MoveCamp(plan, 10000, 20000, 150, 42);

        Assert.Multiple(() =>
        {
            Assert.That((plan.CampX, plan.CampY, plan.CampZ, plan.CampHeading), Is.EqualTo((10000, 20000, 150, (ushort) 42)));
            Assert.That(plan.Shrines[0].Direction, Is.EqualTo(ActGenerator.DescribeFromCamp(10000, 20000, 10000, 15000)));
            Assert.That(plan.Shrines[0].Direction, Is.Not.EqualTo(ActGenerator.DescribeFromCamp(10000, 10000, 10000, 15000)));
            Assert.That(plan.Elites[0].Description, Is.EqualTo($"A Vampiric pack of ghoul lairs {ActGenerator.DescribeFromCamp(10000, 20000, 10000, 15000)}"));
            Assert.That(plan.ExchangerPlaced, Is.False, "a Reforger beside the old camp is placed again");
            Assert.That((plan.Shrines[0].X, plan.Shrines[0].Y, plan.Elites[0].X, plan.Objectives[0].X), Is.EqualTo((10000, 15000, 10000, 12000)), "nothing else moves");
        });

        ActPlan town = SamplePlan();
        town.ExchangerPlaced = true;
        town.ExchangerInTown = true;
        town.ExchangerX = 777;

        ExpeditionArrival.MoveCamp(town, 10000, 20000, 150, 42);

        Assert.That((town.ExchangerPlaced, town.ExchangerX), Is.EqualTo((true, 777)), "a Reforger in town stays");
    }

    [Test]
    public void EnsureSafeCamp_IsDoneOncePerPlan()
    {
        ActPlan checkedPlan = SamplePlan();
        checkedPlan.CampSafetyVersion = ExpeditionArrival.CampSafetyRules;
        string before = Serialize(checkedPlan);

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionArrival.EnsureSafeCamp(checkedPlan, eRealm.Midgard), Is.False);
            Assert.That(Serialize(checkedPlan), Is.EqualTo(before));
            Assert.That(ExpeditionArrival.EnsureSafeCamp(null, eRealm.Midgard), Is.False);
        });
    }

    [Test]
    public void EnsureSafeCamp_WithoutTheWorldLeavesThePlanToBeCheckedLater()
    {
        ActPlan plan = SamplePlan();
        plan.RegionId = 65000;
        string before = Serialize(plan);

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionArrival.EnsureSafeCamp(plan, eRealm.Midgard, out bool moved), Is.False);
            Assert.That(moved, Is.False);
            Assert.That(Serialize(plan), Is.EqualTo(before));
            Assert.That(plan.CampSafetyVersion, Is.EqualTo(0));
        });
    }

    [Test]
    public void CampSafetyVersion_RoundTripsAndOldPlansReadAsUnchecked()
    {
        ActPlan plan = SamplePlan();
        plan.CampSafetyVersion = ExpeditionArrival.CampSafetyRules;
        string json = Serialize(plan);
        ActPlan copy = JsonSerializer.Deserialize<ActPlan>(json, ExpeditionJson.Options);

        JsonObject node = JsonNode.Parse(json).AsObject();
        node.Remove("CampSafetyVersion");
        ActPlan old = JsonSerializer.Deserialize<ActPlan>(node.ToJsonString(), ExpeditionJson.Options);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"CampSafetyVersion\""));
            Assert.That(copy.CampSafetyVersion, Is.EqualTo(ExpeditionArrival.CampSafetyRules));
            Assert.That(Serialize(copy), Is.EqualTo(json));
            Assert.That(old, Is.Not.Null);
            Assert.That(old.CampSafetyVersion, Is.EqualTo(0));
            Assert.That(old.CampX, Is.EqualTo(plan.CampX));
        });
    }
}
