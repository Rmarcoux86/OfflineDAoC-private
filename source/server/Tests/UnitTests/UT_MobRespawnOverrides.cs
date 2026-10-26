using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_MobRespawnOverrides
{
    [Test]
    public void NaburiteDrinkersAndEssenceShreddersRespawnAfterThreeMinutes()
    {
        Assert.That(MobRespawnOverrides.For("essence shredder", 100), Is.EqualTo(180));
        Assert.That(MobRespawnOverrides.For("naburite drinker", 120), Is.EqualTo(180));
        Assert.That(MobRespawnOverrides.For("Naburite Drinker", 120), Is.EqualTo(180));
    }

    [Test]
    public void OtherMonstersAndUnsetTimersAreUnchanged()
    {
        Assert.That(MobRespawnOverrides.For("molochian tempter", 120), Is.EqualTo(120));
        Assert.That(MobRespawnOverrides.For("naburite drinker", 0), Is.EqualTo(0));
        Assert.That(MobRespawnOverrides.For(null, 300), Is.EqualTo(300));
    }
}
