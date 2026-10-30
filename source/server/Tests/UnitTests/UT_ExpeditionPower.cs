using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionPower
{
    [Test]
    public void Meter_CountsCappedGaps_AndKeepsTheLastFiveMinutes()
    {
        (double damage, double seconds) = (0, 0);

        // 30 hits of 100, 3 s apart, after one 60 s pause: the pause counts as 3 s.
        (damage, seconds) = ExpeditionPower.Add(damage, seconds, 100, 60);

        for (int i = 0; i < 29; i++)
        {
            (damage, seconds) = ExpeditionPower.Add(damage, seconds, 100, 3);
        }

        Assert.That(seconds, Is.EqualTo(90).Within(1e-9));
        Assert.That(ExpeditionPower.Dps(damage, seconds), Is.EqualTo(3000 / 90.0).Within(1e-9));
        Assert.That(ExpeditionPower.Dps(100, 10), Is.EqualTo(0), "too little fighting to trust");

        // A long fight is kept to the window, at its rate.
        for (int i = 0; i < 400; i++)
        {
            (damage, seconds) = ExpeditionPower.Add(damage, seconds, 300, 3);
        }

        Assert.That(seconds, Is.EqualTo(ExpeditionPower.WindowSeconds).Within(1e-9));
        Assert.That(ExpeditionPower.Dps(damage, seconds), Is.EqualTo(100).Within(1));
    }

    [Test]
    public void Stalker_IsNinetyPercentOfAnEvenMatch()
    {
        // 100 damage a second: an even match would take 30 s, so 2,700 health.
        Assert.That(ExpeditionPower.StalkerHealth(100), Is.EqualTo(2700));
        Assert.That(ExpeditionPower.StalkerHealth(0), Is.EqualTo(0), "level-based until measured");

        // 2,500 health: an even match kills in 25 s (100 a second); at 90%, 90 a second, so 270 for a 3 s swing.
        Assert.That(ExpeditionPower.StalkerHitDamage(2500, 3), Is.EqualTo(270));
        Assert.That(ExpeditionPower.StalkerHitDamage(2500, 30), Is.EqualTo(450), "a long gap (a stun) counts as 5 s at most");
        Assert.That(ExpeditionPower.StalkerHitDamage(2500, 0.2), Is.EqualTo(135), "1.5 s at least");
    }
}
