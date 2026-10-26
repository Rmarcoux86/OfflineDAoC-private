using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS.Expedition;
using DOL.GS.Expedition.Gauntlet;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>
/// The Gauntlet champions' signatures: the positional geometry (GauntletPositioning, which mirrors Point2D, GameObject
/// and StyleProcessor's positional check), the signature data, and a simulation of a challenger stuck to the champion.
/// No server is needed.
/// </summary>
[TestFixture]
public class UT_ExpeditionGauntletSignatures
{
    private const int TargetX = 40_000;
    private const int TargetY = 40_000;

    private static readonly int[] Distances = [60, 70, 90];

    [Test]
    public void HeadingTo_IsPoint2DGetHeading()
    {
        Point2D target = new(TargetX, TargetY);

        for (int dx = -300; dx <= 300; dx += 7)
        {
            for (int dy = -300; dy <= 300; dy += 11)
            {
                Assert.That(GauntletPositioning.HeadingTo(TargetX, TargetY, TargetX + dx, TargetY + dy),
                    Is.EqualTo(target.GetHeading(TargetX + dx, TargetY + dy)), $"dx {dx}, dy {dy}");
            }
        }
    }

    [Test]
    public void SpotFor_IsPoint2DGetPointFromHeading_WithinRounding()
    {
        Point2D target = new(TargetX, TargetY);

        for (int heading = 0; heading < 4096; heading += 13)
        {
            Point2D expected = target.GetPointFromHeading((ushort) ((heading + 2048) & 0xFFF), 70);
            (int x, int y) = GauntletPositioning.SpotFor(TargetX, TargetY, (ushort) heading, ChampionSpot.Behind, 70);

            Assert.That(Math.Abs(x - expected.X), Is.LessThanOrEqualTo(1), $"heading {heading}");
            Assert.That(Math.Abs(y - expected.Y), Is.LessThanOrEqualTo(1), $"heading {heading}");
        }
    }

    [Test]
    public void Classify_UsesStyleProcessorsRanges()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletPositioning.Classify(0f), Is.EqualTo(TargetArc.Front));
            Assert.That(GauntletPositioning.Classify(44.9f), Is.EqualTo(TargetArc.Front));
            Assert.That(GauntletPositioning.Classify(45f), Is.EqualTo(TargetArc.Side));
            Assert.That(GauntletPositioning.Classify(150f), Is.EqualTo(TargetArc.Side));
            Assert.That(GauntletPositioning.Classify(150.1f), Is.EqualTo(TargetArc.Back));
            Assert.That(GauntletPositioning.Classify(180f), Is.EqualTo(TargetArc.Back));
            Assert.That(GauntletPositioning.Classify(209.9f), Is.EqualTo(TargetArc.Back));
            Assert.That(GauntletPositioning.Classify(210f), Is.EqualTo(TargetArc.Side));
            Assert.That(GauntletPositioning.Classify(315f), Is.EqualTo(TargetArc.Side));
            Assert.That(GauntletPositioning.Classify(315.1f), Is.EqualTo(TargetArc.Front));
            Assert.That(GauntletPositioning.Classify(359.9f), Is.EqualTo(TargetArc.Front));
        });
    }

    [Test]
    public void Spots_ClassifyAsTheirArc_AndAreAccepted_ForEveryTargetHeading()
    {
        List<string> failures = [];
        (ChampionSpot Spot, TargetArc Arc)[] cases =
        [
            (ChampionSpot.Behind, TargetArc.Back),
            (ChampionSpot.Left, TargetArc.Side),
            (ChampionSpot.Right, TargetArc.Side),
            (ChampionSpot.Front, TargetArc.Front)
        ];

        for (int heading = 0; heading < 4096; heading++)
        {
            foreach (int distance in Distances)
            {
                foreach ((ChampionSpot spot, TargetArc arc) in cases)
                {
                    (int x, int y) = GauntletPositioning.SpotFor(TargetX, TargetY, (ushort) heading, spot, distance);
                    float angle = GauntletPositioning.AngleFrom(TargetX, TargetY, (ushort) heading, x, y);
                    ushort facing = GauntletPositioning.HeadingTo(x, y, TargetX, TargetY);

                    if (GauntletPositioning.Classify(angle) != arc ||
                        !GauntletPositioning.Accepts(arc, x, y, facing, TargetX, TargetY, (ushort) heading))
                    {
                        failures.Add($"{spot} heading {heading} distance {distance} angle {angle}");
                    }
                }
            }
        }

        Assert.That(failures, Is.Empty);
    }

    [Test]
    public void Accepts_NeedsTheChampionToFaceItsTarget_UnlessWithin32()
    {
        (int x, int y) = GauntletPositioning.SpotFor(TargetX, TargetY, 1000, ChampionSpot.Behind, 70);
        ushort facing = GauntletPositioning.HeadingTo(x, y, TargetX, TargetY);
        ushort away = (ushort) ((facing + 2048) & 0xFFF);
        (int nearX, int nearY) = GauntletPositioning.SpotFor(TargetX, TargetY, 1000, ChampionSpot.Behind, 30);

        Assert.Multiple(() =>
        {
            Assert.That(GauntletPositioning.Accepts(TargetArc.Back, x, y, facing, TargetX, TargetY, 1000), Is.True);
            Assert.That(GauntletPositioning.Accepts(TargetArc.Back, x, y, away, TargetX, TargetY, 1000), Is.False);
            Assert.That(GauntletPositioning.Accepts(TargetArc.Back, nearX, nearY, away, TargetX, TargetY, 1000), Is.True);
            Assert.That(GauntletPositioning.Accepts(TargetArc.Side, x, y, facing, TargetX, TargetY, 1000), Is.False);
        });
    }

    [Test]
    public void NearestSide_IsTheCloserSideSpot()
    {
        for (int heading = 0; heading < 4096; heading += 64)
        {
            (int leftX, int leftY) = GauntletPositioning.SpotFor(TargetX, TargetY, (ushort) heading, ChampionSpot.Left, 150);
            (int rightX, int rightY) = GauntletPositioning.SpotFor(TargetX, TargetY, (ushort) heading, ChampionSpot.Right, 150);

            Assert.That(GauntletPositioning.NearestSide(TargetX, TargetY, (ushort) heading, leftX, leftY, 70), Is.EqualTo(ChampionSpot.Left));
            Assert.That(GauntletPositioning.NearestSide(TargetX, TargetY, (ushort) heading, rightX, rightY, 70), Is.EqualTo(ChampionSpot.Right));
        }
    }

    [Test]
    public void Signatures_AreTheOwnersChains()
    {
        ChampionSignature ios = GauntletChampion.SignatureFor("Ios");
        ChampionSignature preesh = GauntletChampion.SignatureFor("Preesh");
        ChampionSignature drunkstains = GauntletChampion.SignatureFor("Drunkstains");

        Assert.Multiple(() =>
        {
            Assert.That(ios.BehindOpener, Is.EqualTo("Damaging Grasp"));
            Assert.That(ios.SideChain, Is.EqualTo(new[] { "Stunning Blade", "Winter's Scythe", "Blizzard Blade" }));
            Assert.That(preesh.Priority, Is.EqualTo(new[] { "Devastating Blow", "Stunning Blow" }));
            Assert.That(preesh.SideChain, Is.EqualTo(new[] { "Ice Storm", "Tempest", "Supernova" }));
            Assert.That(preesh.BehindOpener, Is.Null);
            Assert.That(drunkstains.PolearmChain, Is.EqualTo(new[] { "Phalanx", "Defender's Revenge" }));
            Assert.That(drunkstains.ShieldStyle, Is.EqualTo("Slam"));
            Assert.That(GauntletChampion.SignatureFor("Gwyraan").RangedDot, Is.True);
            Assert.That(GauntletChampion.SignatureFor("Toothmouth").MezRetreat, Is.True);
            Assert.That(GauntletChampion.SignatureFor("Ios").RangedDot || GauntletChampion.SignatureFor("Ios").MezRetreat, Is.False);
            Assert.That(GauntletChampion.SignatureFor("Scob Mahob"), Is.SameAs(ChampionSignature.None));
            Assert.That(GauntletChampion.SignatureFor(null), Is.SameAs(ChampionSignature.None));
        });
    }

    // The stuck simulation (owner 2026-10-25: "see if you can get it to work in tests with the enemy stuck to you").
    //
    // The champion sprints in a straight line from 300 units in front of the challenger, through them, to the behind
    // spot 70 units past them, then turns to face them (NpcMovementComponent.TurnTo sets the heading at once). Its
    // speed is a sprinting player's: PLAYER_BASE_SPEED (191) * 1.3 (MaxSpeedCalculator's SPRINT) = 248 units/s; a
    // faster champion only shortens the run, not the windows below. The challenger stands and turns: /stick makes their
    // client face the champion, and the server only learns a player's heading from that client's packets
    // (PlayerHeadingUpdateHandler and PlayerPositionUpdateHandler set Heading as received, unthrottled). How often a
    // client sends them is not in the server code, so the update interval is swept: 0 is a perfect stick (the server
    // always has the heading towards the champion), then 100 to 500 ms, at every phase in 10 ms steps. A step counts when
    // StyleProcessor would accept a back positional there (GauntletPositioning.Accepts).
    private const int StepMs = 10;

    private static readonly int[] UpdateIntervals = [100, 200, 250, 300, 500];

    private static readonly int[] Laterals = [0, 20, 40];

    private readonly record struct RunResult(int BackMs, int LongestMs);

    private static RunResult RunThrough(int updateIntervalMs, int phaseMs, int lateral, bool targetStunned)
    {
        double speed = GamePlayer.PLAYER_BASE_SPEED * 1.3;
        (int destX, int destY) = GauntletPositioning.SpotFor(TargetX, TargetY, 0, ChampionSpot.Behind, 70);
        double startX = TargetX + lateral;
        double startY = TargetY + 300;
        double length = Math.Sqrt((destX - startX) * (destX - startX) + (destY - startY) * (destY - startY));
        int endMs = (int) Math.Ceiling(length / speed * 1000) + 1_500;
        ushort targetHeading = GauntletPositioning.HeadingTo(TargetX, TargetY, (int) startX, (int) startY);
        int backMs = 0;
        int runMs = 0;
        int longestMs = 0;

        for (int t = StepMs; t <= endMs; t += StepMs)
        {
            double travelled = Math.Min(length, speed * t / 1000.0);
            int x = (int) Math.Round(startX + (destX - startX) * travelled / length);
            int y = (int) Math.Round(startY + (destY - startY) * travelled / length);
            bool arrived = travelled >= length;
            ushort championHeading = arrived ? GauntletPositioning.HeadingTo(x, y, TargetX, TargetY) : GauntletPositioning.HeadingTo(x, y, destX, destY);
            bool updateDue = updateIntervalMs <= 0 || ((t - phaseMs) % updateIntervalMs + updateIntervalMs) % updateIntervalMs == 0;

            if (!targetStunned && updateDue)
            {
                targetHeading = GauntletPositioning.HeadingTo(TargetX, TargetY, x, y);
            }

            if (GauntletPositioning.Accepts(TargetArc.Back, x, y, championHeading, TargetX, TargetY, targetHeading))
            {
                backMs += StepMs;
                runMs += StepMs;
                longestMs = Math.Max(longestMs, runMs);
            }
            else
            {
                runMs = 0;
            }
        }

        return new RunResult(backMs, longestMs);
    }

    [Test]
    public void StuckChallenger_PerfectStick_IsNeverBehindable()
    {
        foreach (int lateral in Laterals)
        {
            Assert.That(RunThrough(0, 0, lateral, false).BackMs, Is.Zero, $"lateral {lateral}");
        }
    }

    [Test]
    public void StuckChallenger_LaggedStick_IsBehindableForAtMostOneUpdateInterval()
    {
        foreach (int interval in UpdateIntervals)
        {
            foreach (int lateral in Laterals)
            {
                int worst = 0;
                int sum = 0;

                for (int phase = 0; phase < interval; phase += StepMs)
                {
                    RunResult result = RunThrough(interval, phase, lateral, false);
                    Assert.That(result.LongestMs, Is.LessThan(interval), $"interval {interval}, lateral {lateral}, phase {phase}");
                    worst = Math.Max(worst, result.LongestMs);
                    sum += result.BackMs;
                }

                double average = sum / (double) (interval / StepMs);
                TestContext.Out.WriteLine($"update every {interval} ms, passing {lateral} units beside: longest window {worst} ms, average {average:0} ms per run-through");

                if (lateral == 0)
                {
                    // Running straight through leaves a window just after the crossing: the champion is within 32 units
                    // (always "in front" for IsObjectInFront) and behind the stale heading.
                    Assert.That(worst, Is.GreaterThan(0), $"interval {interval}");
                }
            }
        }
    }

    // The timed run through (GauntletChampionBrain.RunThrough, owner 2026-10-25: "make sure in tests you can get that
    // stun off"), modelled on the server's loop. Each loop tick (GAME_LOOP_TICK_RATE default 30, so 33.3 ms) runs, in
    // order (GameLoop.BuildTickSequence): ClientService.BeginTick (player packets, so the challenger's heading),
    // NpcService (the brain), AttackService (the swing, on the first tick within half a tick of its due time,
    // GameServiceUtils.ShouldTick), MovementService (NpcMovementComponent: a walk ordered this tick starts here and
    // moves speed * elapsed per tick). The challenger stands on /stick: each heading their client sends faces the
    // champion where the server last moved her, sent every U ms at a random phase and applied at the next tick, with no
    // network delay (the offline server is local; any delay only makes the heading staler). PlayerMovementMonitor's
    // speed-hack window assumes about 210 ms between a moving client's position packets (ViolationValidityDuration "at
    // least ViolationThreshold * ~210"); how often a standing client sends heading packets is not in the server code,
    // so U is swept from 100 to 500 ms. She starts 60 to 120 units in front of the challenger, who faces her, at any
    // heading; her swing is due 1 to 5 s later; she runs at up to sprint speed (191 * 1.3). A run counts when
    // StyleProcessor's positional check (GauntletPositioning.Accepts) passes for a back style at the swing.
    private static readonly int[] RunThroughIntervals = [100, 150, 200, 210, 250, 300, 500];

    private const double LoopTickMs = 1000.0 / 30;

    private static long LoopTick(int k) => (long) Math.Round(k * LoopTickMs);

    /// <returns>Whether the back positional is accepted at the swing; null when the run never started.</returns>
    private static bool? TimedRunThrough(int updateIntervalMs, int phaseMs, int startDistance, int challengerHeading, long swingDue)
    {
        double topSpeed = Math.Floor(GamePlayer.PLAYER_BASE_SPEED * 1.3);
        double angle = challengerHeading * Point2D.HEADING_TO_RADIAN;
        double x = TargetX - Math.Sin(angle) * startDistance;
        double y = TargetY + Math.Cos(angle) * startDistance;
        Dictionary<int, (int X, int Y)> moved = [];
        bool started = false;
        int startTick = 0;
        double vx = 0;
        double vy = 0;
        int destX = 0;
        int destY = 0;
        ushort walkHeading = 0;
        int fireTick = -1;

        for (int k = 0; k < 1_000 && fireTick < 0; k++)
        {
            long t = LoopTick(k);

            // NpcService: the brain plans on the position MovementService gave her last.
            if (!started)
            {
                int cx = (int) Math.Round(x);
                int cy = (int) Math.Round(y);
                double distance = Math.Sqrt((double) (TargetX - cx) * (TargetX - cx) + (double) (TargetY - cy) * (TargetY - cy));
                double speed = GauntletPositioning.RunThroughSpeed(t, swingDue, LoopTickMs, distance, topSpeed);

                if (speed > 0)
                {
                    started = true;
                    startTick = k;
                    (destX, destY) = GauntletPositioning.PointBeyond(cx, cy, TargetX, TargetY, 20);
                    walkHeading = GauntletPositioning.HeadingTo(cx, cy, destX, destY);
                    double walk = Math.Min(topSpeed, Math.Ceiling(speed));
                    double length = Math.Sqrt((destX - x) * (destX - x) + (destY - y) * (destY - y));
                    vx = (destX - x) / length * walk;
                    vy = (destY - y) / length * walk;
                }
            }

            // AttackService.
            if (swingDue - t - LoopTickMs / 2 <= 0)
            {
                fireTick = k;

                break;
            }

            // MovementService: NpcMovementComponent.UpdatePosition, clamped to the destination per axis.
            if (started && k > startTick)
            {
                double elapsed = (t - LoopTick(k - 1)) / 1000.0;
                x = Math.Abs(destX - x) >= Math.Abs(vx * elapsed) ? x + vx * elapsed : destX;
                y = Math.Abs(destY - y) >= Math.Abs(vy * elapsed) ? y + vy * elapsed : destY;
            }

            moved[k] = ((int) Math.Round(x), (int) Math.Round(y));
        }

        if (!started || fireTick < 1)
        {
            return null;
        }

        // The last heading the challenger's client sent before the swing's tick, facing where she was on the last tick
        // that had finished when it was sent.
        long fireTime = LoopTick(fireTick);
        // None sent yet (sent < 0) leaves the facing she had at the start.
        long sent = phaseMs + (long) Math.Floor((fireTime - phaseMs) / (double) updateIntervalMs) * updateIntervalMs;
        int seenTick = fireTick - 1;

        while (seenTick > 0 && LoopTick(seenTick) >= sent)
        {
            seenTick--;
        }

        (int seenX, int seenY) = moved[seenTick];
        ushort challengerFacing = GauntletPositioning.HeadingTo(TargetX, TargetY, seenX, seenY);
        (int swingX, int swingY) = moved[fireTick - 1];

        return GauntletPositioning.Accepts(TargetArc.Back, swingX, swingY, walkHeading, TargetX, TargetY, challengerFacing);
    }

    private static (int Landed, int Runs, int NotStarted) TimedRunThroughs(int updateIntervalMs, int seed)
    {
        Random random = new(seed);
        int landed = 0;
        int runs = 0;
        int notStarted = 0;

        for (int trial = 0; trial < 2_000; trial++)
        {
            int[] distances = [60, 80, 100, 120];
            bool? result = TimedRunThrough(updateIntervalMs, random.Next(updateIntervalMs), distances[random.Next(distances.Length)],
                random.Next(4096), random.Next(1_000, 5_000));

            if (result == null)
            {
                notStarted++;

                continue;
            }

            runs++;

            if (result.Value)
            {
                landed++;
            }
        }

        return (landed, runs, notStarted);
    }

    [Test]
    public void TimedRunThrough_LandsTheBackStyle_OnAStuckChallenger()
    {
        foreach (int interval in RunThroughIntervals)
        {
            (int landed, int runs, int notStarted) = TimedRunThroughs(interval, interval);
            double rate = landed / (double) runs;
            TestContext.Out.WriteLine($"heading every {interval} ms: back positional accepted at the swing in {rate:P1} of {runs} runs");

            Assert.That(notStarted, Is.Zero, $"interval {interval}");

            // A run fails only when a heading is sent in the last tick before the swing: about 33 ms of every U.
            Assert.That(rate, Is.GreaterThanOrEqualTo(0.6), $"interval {interval}");
            Assert.That(rate, Is.GreaterThanOrEqualTo(1 - 2 * LoopTickMs / interval), $"interval {interval}");
        }
    }

    [Test]
    public void TimedRunThrough_AlwaysLands_WhenNoHeadingArrivesDuringTheRun()
    {
        (int landed, int runs, int notStarted) = TimedRunThroughs(1_000_000, 7);

        Assert.That(notStarted, Is.Zero);
        Assert.That(landed, Is.EqualTo(runs));
    }

    [Test]
    public void RunThroughSpeed_StartsLateAndFast_AndNeverTooFast()
    {
        Assert.Multiple(() =>
        {
            // Too early: a later tick still makes it at 75 % or more of the top speed.
            Assert.That(GauntletPositioning.RunThroughSpeed(0, 3_000, LoopTickMs, 100, 248), Is.Zero);

            // Too late: the swing fires before even the top speed gets her through.
            Assert.That(GauntletPositioning.RunThroughSpeed(0, 200, LoopTickMs, 100, 248), Is.Zero);
        });

        for (long now = 0; now < 2_000; now += 33)
        {
            double speed = GauntletPositioning.RunThroughSpeed(now, 2_000, LoopTickMs, 100, 248);

            Assert.That(speed, Is.EqualTo(0.0).Or.InRange(248 * GauntletPositioning.RunThroughMinSpeedShare, 248.0));
        }
    }

    [Test]
    public void StunnedChallenger_StaysBehindableOnceTheChampionArrives()
    {
        RunResult result = RunThrough(0, 0, 0, true);

        Assert.That(result.LongestMs, Is.GreaterThanOrEqualTo(1_400));
    }

    // Gwyraan and Toothmouth (owner 2026-10-25).
    private static Spell TestSpell(string name, eSpellType type, string target, int level, double castSeconds, double value = 0)
    {
        return new Spell(new DbSpell { Name = name, Type = type.ToString(), Target = target, CastTime = castSeconds, Value = value }, level);
    }

    private static int SpecCost(int level) => level * (level + 1) / 2 - 1;

    [Test]
    public void GwyraanBuild_IsNature50_AndSpendsADruidsPoints()
    {
        GwyraanBotSpec spec = new();
        Dictionary<string, uint> caps = spec.SpecLines.ToDictionary(line => line.Spec, line => line.SpecCap);

        // GameBot.GetSpecPointsForLevel for a level-50 creation with a Druid's multiplier of 10 (ClassDruid).
        int budget = 0;

        for (int level = 2; level <= 50; level++)
        {
            budget += level > 5 ? 10 * level / 10 : level;
            budget += level > 40 ? 10 * (level - 1) / 20 : 0;
        }

        int cost = caps.Values.Sum(cap => SpecCost((int) cap));
        int cheapestNext = caps.Values.Min(cap => (int) cap + 1);

        Assert.Multiple(() =>
        {
            Assert.That(caps[Specs.Nature], Is.EqualTo(50));
            Assert.That(spec.WeaponOneType, Is.EqualTo(eObjectType.Staff));
            Assert.That(spec.Is2H, Is.True);
            Assert.That(budget, Is.EqualTo(1494));
            Assert.That(cost, Is.LessThanOrEqualTo(budget));
            Assert.That(budget - cost, Is.LessThan(cheapestNext), "points left over");
        });
    }

    [Test]
    public void ToothmouthBuild_IsTheHealerBard()
    {
        ToothmouthBotSpec spec = new();
        Dictionary<string, uint> caps = spec.SpecLines.ToDictionary(line => line.Spec, line => line.SpecCap);

        Assert.That(caps, Is.EquivalentTo(new Dictionary<string, uint> { [Specs.Music] = 37, [Specs.Nurture] = 43, [Specs.Regrowth] = 33 }));
        Assert.That(spec.WeaponOneType, Is.EqualTo(eObjectType.Blades));
    }

    [Test]
    public void ChampionSpells_PickTheBestRankForTheLevel()
    {
        Spell[] spells =
        [
            TestSpell("Dilapidation", eSpellType.DamageOverTime, "Enemy", 35, 3),
            TestSpell("Degeneration", eSpellType.DamageOverTime, "Enemy", 41, 3, 73),
            TestSpell("Defedation", eSpellType.DamageOverTime, "Enemy", 48, 3, 87),
            TestSpell("Stingers of the Wild", eSpellType.DamageAdd, "Group", 36, 3),
            TestSpell("Hooks of the Wild", eSpellType.DamageAdd, "Group", 46, 3, 10.5),
            TestSpell("Hypnotic Harmony", eSpellType.Mesmerize, "Enemy", 29, 0),
            TestSpell("Deluding Harmony", eSpellType.Mesmerize, "Enemy", 37, 0),
            TestSpell("Slow Lullaby", eSpellType.Mesmerize, "Enemy", 40, 2.5),
            TestSpell("Renascence", eSpellType.Heal, "Realm", 20, 3, 200),
            TestSpell("Major Renascence", eSpellType.Heal, "Realm", 32, 3.2, 400),
            TestSpell("Wide Renewal", eSpellType.Heal, "Area", 40, 3, 600),
            TestSpell("Nature's Ally", eSpellType.SummonDruidPet, "Self", 30, 3)
        ];

        Assert.Multiple(() =>
        {
            Assert.That(ChampionSpells.Best(spells, eSpellType.DamageOverTime, 50)?.Name, Is.EqualTo("Defedation"));
            Assert.That(ChampionSpells.Best(spells, eSpellType.DamageOverTime, 45)?.Name, Is.EqualTo("Degeneration"));
            Assert.That(ChampionSpells.Best(spells, eSpellType.DamageAdd, 50)?.Name, Is.EqualTo("Hooks of the Wild"));
            Assert.That(ChampionSpells.Best(spells, eSpellType.Mesmerize, 50, true)?.Name, Is.EqualTo("Deluding Harmony"));
            Assert.That(ChampionSpells.Best(spells, eSpellType.Mesmerize, 30, true)?.Name, Is.EqualTo("Hypnotic Harmony"));
            Assert.That(ChampionSpells.Best(spells, eSpellType.Mesmerize, 20, true), Is.Null);
            Assert.That(ChampionSpells.BestSelfHeal(spells, 50)?.Name, Is.EqualTo("Major Renascence"));
            Assert.That(ChampionSpells.WithoutPetsAndAttacks(spells).Select(spell => spell.Name),
                Is.EquivalentTo(new[] { "Stingers of the Wild", "Hooks of the Wild", "Renascence", "Major Renascence" }));
        });
    }

    [Test]
    public void RetreatPoint_IsPastTheSpot_AwayFromTheChallenger()
    {
        for (int heading = 0; heading < 4096; heading += 97)
        {
            (int challengerX, int challengerY) = GauntletPositioning.SpotFor(TargetX, TargetY, (ushort) heading, ChampionSpot.Front, 400);
            (int x, int y) = GauntletPositioning.RetreatPoint(TargetX, TargetY, challengerX, challengerY, 2000);
            double fromSpot = Math.Sqrt((double) (x - TargetX) * (x - TargetX) + (double) (y - TargetY) * (y - TargetY));
            double fromChallenger = Math.Sqrt((double) (x - challengerX) * (x - challengerX) + (double) (y - challengerY) * (y - challengerY));

            Assert.That(fromSpot, Is.EqualTo(2000).Within(2), $"heading {heading}");
            Assert.That(fromChallenger, Is.EqualTo(2400).Within(2), $"heading {heading}");
        }

        Assert.That(GauntletPositioning.RetreatPoint(TargetX, TargetY, TargetX, TargetY, 2000), Is.EqualTo((TargetX + 2000, TargetY)));
    }

    [Test]
    public void GearRarity_ScalesAcrossTheFiveFights()
    {
        Random rng = new(7);

        Assert.That(GauntletChampion.GearRarityFor(1, rng), Is.EqualTo(eLootRarity.Rare));
        Assert.That(GauntletChampion.GearRarityFor(2, rng), Is.EqualTo(eLootRarity.Rare));
        Assert.That(GauntletChampion.GearRarityFor(5, rng), Is.EqualTo(eLootRarity.Epic));

        HashSet<eLootRarity> middle = new();

        for (int i = 0; i < 200; i++)
        {
            middle.Add(GauntletChampion.GearRarityFor(3, rng));
            middle.Add(GauntletChampion.GearRarityFor(4, rng));
        }

        Assert.That(middle, Is.EquivalentTo(new[] { eLootRarity.Rare, eLootRarity.Epic }));
    }
}
