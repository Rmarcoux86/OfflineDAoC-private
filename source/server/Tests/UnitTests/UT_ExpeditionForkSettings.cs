using DOL.GS;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Offline fork settings outside Expedition proper (named UT_Expedition* so the fork's test filter runs them).</summary>
[TestFixture]
public class UT_ExpeditionForkSettings
{
    [Test]
    public void RemoveClassId_TakesTheHereticOutOfTheSetupList()
    {
        Assert.That(ForkClassUnlocks.RemoveClassId("20;33;34;39;58-62", 33), Is.EqualTo("20;34;39;58-62"));
    }

    [Test]
    public void RemoveClassId_SplitsARange()
    {
        Assert.That(ForkClassUnlocks.RemoveClassId("30-35", 33), Is.EqualTo("30-32;34-35"));
        Assert.That(ForkClassUnlocks.RemoveClassId("33-34", 33), Is.EqualTo("34"));
        Assert.That(ForkClassUnlocks.RemoveClassId("32-33", 33), Is.EqualTo("32"));
    }

    [Test]
    public void RemoveClassId_LeavesAListWithoutIt()
    {
        Assert.That(ForkClassUnlocks.RemoveClassId("20;34", 33), Is.EqualTo("20;34"));
        Assert.That(ForkClassUnlocks.RemoveClassId(string.Empty, 33), Is.EqualTo(string.Empty));
        Assert.That(ForkClassUnlocks.RemoveClassId("20,33,34", 33), Is.EqualTo("20;34"));
    }
}
