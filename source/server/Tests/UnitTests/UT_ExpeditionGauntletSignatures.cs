using System;
using System.Collections.Generic;
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
            Assert.That(GauntletChampion.SignatureFor("Toothmouth"), Is.SameAs(ChampionSignature.None));
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

    [Test]
    public void StunnedChallenger_StaysBehindableOnceTheChampionArrives()
    {
        RunResult result = RunThrough(0, 0, 0, true);

        Assert.That(result.LongestMs, Is.GreaterThanOrEqualTo(1_400));
    }
}
