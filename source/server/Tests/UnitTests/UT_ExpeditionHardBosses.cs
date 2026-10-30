using System;
using DOL.GS.Expedition;
using DOL.GS.Expedition.Gauntlet;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>A hard run's act bosses, bounty timer and storm: the pure parts (ExpeditionHardBosses, ExpeditionBounty, ExpeditionWeather).</summary>
[TestFixture]
public class UT_ExpeditionHardBosses
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void ChampionFor_FollowsTheGauntletOrder()
    {
        Assert.That(ExpeditionHardBosses.ChampionFor(1).Name, Is.EqualTo("Gwyraan"));
        Assert.That(ExpeditionHardBosses.HealthMultiplierFor(1), Is.EqualTo(4), "the act boss's own 3 + act");
        Assert.That(ExpeditionHardBosses.HealthMultiplierFor(5), Is.EqualTo(8));
        Assert.That(ExpeditionHardBosses.ChampionFor(2).Name, Is.EqualTo("Toothmouth"));
        Assert.That(ExpeditionHardBosses.ChampionFor(3).Name, Is.EqualTo("Ios"));
        Assert.That(ExpeditionHardBosses.ChampionFor(4).Name, Is.EqualTo("Drunkstains"));
        Assert.That(ExpeditionHardBosses.ChampionFor(5).Name, Is.EqualTo("Preesh"));
        Assert.That(ExpeditionHardBosses.ChampionFor(0).Name, Is.EqualTo("Gwyraan"), "below 1 clamps");
        Assert.That(ExpeditionHardBosses.ChampionFor(9).Name, Is.EqualTo("Preesh"), "past 5 clamps");
    }

    [Test]
    public void LevelFor_IsTheActsTopLevelPlusTwo_CappedAt50()
    {
        Assert.That(ExpeditionHardBosses.LevelFor(new ActPlan { Act = 1, MaxLevel = 10 }), Is.EqualTo(12));
        Assert.That(ExpeditionHardBosses.LevelFor(new ActPlan { Act = 4, MaxLevel = 40 }), Is.EqualTo(42));
        Assert.That(ExpeditionHardBosses.LevelFor(new ActPlan { Act = 5, MaxLevel = 50 }), Is.EqualTo(50));
    }

    [Test]
    public void RealmRanks_AreTheGauntletsForTheAct()
    {
        int[] ranks = new int[5];

        for (int act = 1; act <= 5; act++)
        {
            ranks[act - 1] = GauntletEscalation.For(act, ExpeditionHardBosses.ChampionFor(act).Name).RealmRank;
        }

        Assert.That(ranks, Is.EqualTo(new[] { 2, 4, 6, 8, 10 }));
    }

    [Test]
    public void DurationFor_25_35_45Minutes()
    {
        Assert.That(ExpeditionBounty.DurationFor(1), Is.EqualTo(TimeSpan.FromMinutes(25)));
        Assert.That(ExpeditionBounty.DurationFor(2), Is.EqualTo(TimeSpan.FromMinutes(25)));
        Assert.That(ExpeditionBounty.DurationFor(3), Is.EqualTo(TimeSpan.FromMinutes(35)));
        Assert.That(ExpeditionBounty.DurationFor(4), Is.EqualTo(TimeSpan.FromMinutes(35)));
        Assert.That(ExpeditionBounty.DurationFor(5), Is.EqualTo(TimeSpan.FromMinutes(45)));
    }

    [Test]
    public void Resume_AfterACrash_ResumesFromTheTicksMirror()
    {
        // The deadline was saved running (no clean pause) with the tick's mirror of what was left: the time away does not count.
        ActPlan plan = new() { Act = 3, BountyDeadlineUtc = Now.AddMinutes(35), BountySecondsLeft = 20 * 60 };

        Assert.That(ExpeditionBounty.Resume(plan, Now.AddHours(3)), Is.True);
        Assert.That(plan.BountyDeadlineUtc, Is.EqualTo(Now.AddHours(3).AddMinutes(20)));
        Assert.That(plan.BountySecondsLeft, Is.Null);
    }

    [Test]
    public void Resume_StartsTheActsBounty_ThenPauseAndResumeKeepWhatWasLeft()
    {
        ActPlan plan = new() { Act = 3 };

        Assert.That(ExpeditionBounty.Resume(plan, Now), Is.True);
        Assert.That(plan.BountyDeadlineUtc, Is.EqualTo(Now.AddMinutes(35)));
        Assert.That(ExpeditionBounty.Resume(plan, Now.AddMinutes(1)), Is.False, "a running deadline is kept");

        // Ten minutes played, then away for two hours: 25 minutes are left when the player comes back.
        Assert.That(ExpeditionBounty.Pause(plan, Now.AddMinutes(10)), Is.True);
        Assert.That(plan.BountyDeadlineUtc, Is.Null);
        Assert.That(plan.BountySecondsLeft, Is.EqualTo(25 * 60).Within(1e-9));
        Assert.That(ExpeditionBounty.StatusText(plan, Now.AddHours(1)), Is.EqualTo("25:00 left (paused)"));

        DateTime back = Now.AddHours(2);
        Assert.That(ExpeditionBounty.Resume(plan, back), Is.True);
        Assert.That(plan.BountyDeadlineUtc, Is.EqualTo(back.AddMinutes(25)));
        Assert.That(plan.BountySecondsLeft, Is.Null);
        Assert.That(ExpeditionBounty.SecondsLeft(plan.BountyDeadlineUtc, back.AddSeconds(30)), Is.EqualTo(24 * 60 + 30).Within(1e-9));
        Assert.That(ExpeditionBounty.StatusText(plan, back.AddSeconds(30)), Is.EqualTo("24:30 left"));
    }

    [Test]
    public void Deadline_RunsOutAtItsTime_AndNeverGoesBelowZero()
    {
        ActPlan plan = new() { Act = 1 };
        ExpeditionBounty.Resume(plan, Now);
        DateTime deadline = Now.AddMinutes(25);

        Assert.That(ExpeditionBounty.HasRunOut(plan.BountyDeadlineUtc, deadline.AddSeconds(-1)), Is.False);
        Assert.That(ExpeditionBounty.HasRunOut(plan.BountyDeadlineUtc, deadline), Is.True);
        Assert.That(ExpeditionBounty.SecondsLeft(plan.BountyDeadlineUtc, deadline.AddMinutes(5)), Is.EqualTo(0));
        Assert.That(ExpeditionBounty.HasRunOut(null, Now), Is.False, "no deadline never runs out");
        Assert.That(ExpeditionBounty.StatusText(plan, deadline), Is.EqualTo("run out: the boss will come with a helper pack"));

        // Paused after it ran out: it stays run out.
        ExpeditionBounty.Pause(plan, deadline.AddMinutes(1));
        Assert.That(plan.BountySecondsLeft, Is.EqualTo(0));
        ExpeditionBounty.Resume(plan, deadline.AddHours(1));
        Assert.That(ExpeditionBounty.HasRunOut(plan.BountyDeadlineUtc, deadline.AddHours(1)), Is.True);
    }

    [Test]
    public void Decide_SettlesMetOrMissedOnce()
    {
        ActPlan met = new() { Act = 1 };
        ExpeditionBounty.Resume(met, Now);
        met.BossSpawned = true;
        ExpeditionBounty.Decide(met, Now.AddMinutes(24));

        Assert.That(met.BountyMissed, Is.False);
        Assert.That(met.BountyDeadlineUtc, Is.Null, "a later respawn does not read the old deadline");
        ExpeditionBounty.Decide(met, Now.AddHours(3));
        Assert.That(met.BountyMissed, Is.False);
        Assert.That(ExpeditionBounty.StatusText(met, Now.AddHours(3)), Is.EqualTo("met: the boss came alone"));
        Assert.That(ExpeditionBounty.Resume(met, Now.AddHours(3)), Is.False, "no timer once the boss has come");

        ActPlan missed = new() { Act = 5 };
        ExpeditionBounty.Resume(missed, Now);
        missed.BossSpawned = true;
        ExpeditionBounty.Decide(missed, Now.AddMinutes(45));

        Assert.That(missed.BountyMissed, Is.True);
        Assert.That(ExpeditionBounty.StatusText(missed, Now.AddMinutes(46)), Is.EqualTo("missed: the boss came with its helper pack"));
    }

    [Test]
    public void WarningDue_FiveMinutesThenOneMinute_EachOnce()
    {
        int warned = int.MaxValue;

        Assert.That(ExpeditionBounty.WarningDue(301, warned), Is.EqualTo(0));
        Assert.That(ExpeditionBounty.WarningDue(300, warned), Is.EqualTo(300));
        warned = 300;
        Assert.That(ExpeditionBounty.WarningDue(200, warned), Is.EqualTo(0), "the 5-minute warning once");
        Assert.That(ExpeditionBounty.WarningDue(60, warned), Is.EqualTo(60));
        warned = 60;
        Assert.That(ExpeditionBounty.WarningDue(30, warned), Is.EqualTo(0), "the 1-minute warning once");
        Assert.That(ExpeditionBounty.WarningDue(0, int.MaxValue), Is.EqualTo(0), "nothing once it has run out");
        Assert.That(ExpeditionBounty.WarningDue(45, int.MaxValue), Is.EqualTo(60), "back with under a minute: the last warning only");
    }

    [Test]
    public void FormatLeft_MinutesAndSeconds()
    {
        Assert.That(ExpeditionBounty.FormatLeft(25 * 60), Is.EqualTo("25:00"));
        Assert.That(ExpeditionBounty.FormatLeft(59.2), Is.EqualTo("1:00"), "rounded up");
        Assert.That(ExpeditionBounty.FormatLeft(5), Is.EqualTo("0:05"));
        Assert.That(ExpeditionBounty.FormatLeft(-3), Is.EqualTo("0:00"));
    }

    [Test]
    public void Storm_IsLaidAroundThePlayer_InsideTheServersOwnWeatherRanges_AndAgainNearAnEdge()
    {
        (uint position, uint width) = ExpeditionWeather.FrontFor(300000);

        Assert.That(width, Is.EqualTo(90000u), "the widest random weather (RegionWeather.CreateWeather)");
        Assert.That(position, Is.EqualTo(345000u), "the player in the middle");
        Assert.That(ExpeditionWeather.StormSpeed, Is.InRange(100, 700));
        Assert.That(ExpeditionWeather.StormIntensity, Is.InRange(30, 110));
        Assert.That(ExpeditionWeather.StormFogDiffusion, Is.InRange(16000, 32000));

        Assert.That(ExpeditionWeather.NearAnEdge(position, width, 300000), Is.False, "just laid");
        Assert.That(ExpeditionWeather.NearAnEdge(position + 30000, width, 300000), Is.True, "the back edge drifted near");
        Assert.That(ExpeditionWeather.NearAnEdge(position, width, 330000), Is.True, "walked near the front");
        Assert.That(ExpeditionWeather.NearAnEdge(position, width, 400000), Is.True, "outside it");
        Assert.That(ExpeditionWeather.NightGameTime, Is.EqualTo(21u * 3600 * 1000));
        Assert.That(ExpeditionWeather.IsOurs(null), Is.False);
        Assert.That(ExpeditionWeather.DarkSky(null), Is.False);
    }
}
