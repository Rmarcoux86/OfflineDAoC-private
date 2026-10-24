using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionRealm
{
    [Test]
    public void PointsForKill_GreyGivesNothing_EliteGivesFourTimes()
    {
        Assert.That(ExpeditionRealm.PointsForKill(4, 10, false), Is.EqualTo(0), "six levels below the player is grey");
        Assert.That(ExpeditionRealm.PointsForKill(10, 10, false), Is.EqualTo(35));
        Assert.That(ExpeditionRealm.PointsForKill(10, 10, true), Is.EqualTo(35 * ExpeditionRealm.EliteMultiplier));
        Assert.That(ExpeditionRealm.PointsForKill(0, 1, false), Is.EqualTo(5), "a level 0 monster at level 1 still counts");
    }

    [Test]
    public void BackfillEstimate_CountsKillsObjectivesAndBosses()
    {
        // 100 kills at level 10 (mob level 9: 32 each), act 1 won (2 x 250 + 1500), act 2 with one objective done (500).
        Assert.That(ExpeditionRealm.BackfillEstimate(100, 10, 1, 2, 1), Is.EqualTo(3200 + 2000 + 500));
        Assert.That(ExpeditionRealm.BackfillEstimate(0, 1, 0, 1, 0), Is.EqualTo(0));
    }
}
