using System.Collections.Generic;
using System.Linq;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_ExpeditionLegacy
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
    public void RenownFor_SharesRealmPointsAndCountsBosses()
    {
        Assert.That(ExpeditionLegacy.RenownFor(80_000, 5, 50), Is.EqualTo(400 + 5 * ExpeditionLegacy.RenownPerBoss));
        Assert.That(ExpeditionLegacy.RenownFor(8_000, 1, 25), Is.EqualTo(20 + ExpeditionLegacy.RenownPerBoss));
        Assert.That(ExpeditionLegacy.RenownFor(80_000, 5, 0), Is.EqualTo(0), "abandoning banks nothing");
        Assert.That(ExpeditionLegacy.RenownFor(-5, 0, 50), Is.EqualTo(0));
    }

    [Test]
    public void SharePercent_DeathLessThanWin_AbandonNothing()
    {
        int death = ExpeditionLegacy.SharePercent(ExpeditionManager.StatusDead);
        int won = ExpeditionLegacy.SharePercent(ExpeditionManager.StatusWon);

        Assert.That(death, Is.GreaterThan(0));
        Assert.That(won, Is.GreaterThan(death));
        Assert.That(ExpeditionLegacy.SharePercent(ExpeditionManager.StatusAbandoned), Is.EqualTo(0));
    }

    [Test]
    public void Perks_RoundTripAndClampToMaxRank()
    {
        Dictionary<string, int> ranks = ExpeditionLegacy.ParsePerks("seasoned:2, lucky:9, bogus:1, outfitted:x");

        Assert.That(ranks[ExpeditionLegacy.Seasoned], Is.EqualTo(2));
        Assert.That(ranks[ExpeditionLegacy.Lucky], Is.EqualTo(3), "clamped to the perk's ranks");
        Assert.That(ranks.ContainsKey("bogus"), Is.False);
        Assert.That(ranks.ContainsKey(ExpeditionLegacy.Outfitted), Is.False);
        Assert.That(ExpeditionLegacy.ParsePerks(ExpeditionLegacy.FormatPerks(ranks)), Is.EqualTo(ranks));
        Assert.That(ExpeditionLegacy.ParsePerks(null), Is.Empty);
    }

    [Test]
    public void Catalog_HasUniqueIdsAndRisingCosts()
    {
        Assert.That(ExpeditionLegacy.Catalog.Select(perk => perk.Id).Distinct().Count(), Is.EqualTo(ExpeditionLegacy.Catalog.Count));

        foreach (LegacyPerk perk in ExpeditionLegacy.Catalog)
        {
            Assert.That(perk.Costs, Is.Not.Empty, perk.Id);
            Assert.That(perk.Costs, Is.Ordered.Ascending, perk.Id);
            Assert.That(perk.Costs.All(cost => cost > 0), Is.True, perk.Id);
        }
    }

    [Test]
    public void RollOffer_Discerning_OffersFour()
    {
        List<string> offer = ExpeditionBoons.RollOffer(1234, 2, new List<string>(), ExpeditionBoons.OfferSize + 1);

        Assert.That(offer.Count, Is.EqualTo(ExpeditionBoons.OfferSize + 1));
        Assert.That(offer.Distinct().Count(), Is.EqualTo(offer.Count));
    }
}
