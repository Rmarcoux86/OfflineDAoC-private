using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionGreyLoot
{
    [Test]
    public void GreyCon_FollowsTheGamesConTable()
    {
        Assert.Multiple(() =>
        {
            // At 50 the con table's grey covers levels 0-35; 36 is green.
            Assert.That(ExpeditionLoot.IsGreyCon(50, 35), Is.True);
            Assert.That(ExpeditionLoot.IsGreyCon(50, 36), Is.False);

            // At 10, levels 0-6 are grey.
            Assert.That(ExpeditionLoot.IsGreyCon(10, 6), Is.True);
            Assert.That(ExpeditionLoot.IsGreyCon(10, 7), Is.False);

            // At 1 a level 0 monster is blue (the "annoying lucradan").
            Assert.That(ExpeditionLoot.IsGreyCon(1, 0), Is.False);
            Assert.That(ExpeditionLoot.IsGreyCon(30, 32), Is.False, "higher is never grey");
        });
    }

    [Test]
    public void LootJudge_IsThePlayerOrNobody()
    {
        Assert.That(ExpeditionLoot.LootJudge(null), Is.Null);
    }
}
