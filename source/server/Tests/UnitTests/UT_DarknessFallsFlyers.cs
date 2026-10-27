using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_DarknessFallsFlyers
{
    [Test]
    public void AFlyerHighAboveTheFloor_ComesDownNearIt_OthersStay()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DarknessFallsFlyers.LoweredZ(20000, 19000), Is.EqualTo(19150), "1,000 above: lowered");
            Assert.That(DarknessFallsFlyers.LoweredZ(19301, 19000), Is.EqualTo(19150), "just over the limit");
            Assert.That(DarknessFallsFlyers.LoweredZ(19300, 19000), Is.Null, "at the limit: kept");
            Assert.That(DarknessFallsFlyers.LoweredZ(19100, 19000), Is.Null, "near the floor: kept");
        });
    }
}
