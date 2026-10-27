using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ThrownRange
{
    [Test]
    public void ThrownWeapons_IgnoreTheFirstThousandUnitsOfHeight()
    {
        Assert.Multiple(() =>
        {
            // A tempter 800 up on a ledge, 1,000 away along the ground: out of a 1,160 throw before, in reach now.
            Assert.That(RangeAttackComponent.RangeDistanceSquared(1000, 0, 800, false), Is.GreaterThan(1160L * 1160));
            Assert.That(RangeAttackComponent.RangeDistanceSquared(1000, 0, 800, true), Is.EqualTo(1000L * 1000));
            Assert.That(RangeAttackComponent.RangeDistanceSquared(0, 0, -1500, true), Is.EqualTo(500L * 500), "below counts the same");
            Assert.That(RangeAttackComponent.RangeDistanceSquared(300, 400, 0, true), Is.EqualTo(500L * 500), "flat ground unchanged");
        });
    }
}
