using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Expedition boons' pure logic: the catalog, seeded offers, loot math and journal lines. No server is needed.</summary>
[TestFixture]
public class UT_ExpeditionBoons
{
    private static readonly string[] LootIds = ExpeditionBoons.Catalog.Where(boon => boon.IsLoot).Select(boon => boon.Id).ToArray();
    private static readonly string[] OtherIds = ExpeditionBoons.Catalog.Where(boon => !boon.IsLoot).Select(boon => boon.Id).ToArray();

    [Test]
    public void Catalog_HasUniqueIds_AndAtLeastSixBoons()
    {
        List<string> ids = ExpeditionBoons.Catalog.Select(boon => boon.Id).ToList();

        Assert.That(ids.Count, Is.GreaterThanOrEqualTo(6));
        Assert.That(ids, Is.Unique);
        Assert.That(ids.All(id => !string.IsNullOrWhiteSpace(id)), Is.True);
        Assert.That(ExpeditionBoons.Catalog.All(boon => !string.IsNullOrWhiteSpace(boon.Name) && !string.IsNullOrWhiteSpace(boon.Effect)), Is.True);
        Assert.That(LootIds.Length, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Offer_IsDeterministicPerSeedAndAct()
    {
        bool actChangesOffer = false;

        for (int seed = 1; seed <= 200; seed++)
        {
            List<string> first = ExpeditionBoons.RollOffer(seed, 1, new List<string>());
            List<string> again = ExpeditionBoons.RollOffer(seed, 1, new List<string>());
            List<string> nextAct = ExpeditionBoons.RollOffer(seed, 2, new List<string>());

            Assert.That(again, Is.EqualTo(first), $"seed {seed}");

            if (!first.SequenceEqual(nextAct))
            {
                actChangesOffer = true;
            }
        }

        Assert.That(actChangesOffer, Is.True, "the act should take part in the draw");
    }

    [Test]
    public void Offer_HasThreeDistinctCatalogBoons()
    {
        for (int seed = -50; seed <= 50; seed++)
        {
            List<string> offer = ExpeditionBoons.RollOffer(seed, 3, new List<string>());

            Assert.That(offer.Count, Is.EqualTo(ExpeditionBoons.OfferSize), $"seed {seed}");
            Assert.That(offer, Is.Unique, $"seed {seed}");
            Assert.That(offer.All(id => ExpeditionBoons.Find(id) != null), Is.True, $"seed {seed}");
        }
    }

    [Test]
    public void Offer_NeverIncludesATakenBoon()
    {
        var rng = new Random(4242);

        for (int seed = 1; seed <= 500; seed++)
        {
            List<string> taken = ExpeditionBoons.Catalog.Select(boon => boon.Id).Where(_ => rng.Next(3) == 0).ToList();
            List<string> offer = ExpeditionBoons.RollOffer(seed, 1 + seed % 4, taken);

            Assert.That(offer.Intersect(taken), Is.Empty, $"seed {seed}");
        }
    }

    [Test]
    public void Offer_GivesEveryBoonTheSameChance()
    {
        var seen = ExpeditionBoons.Catalog.ToDictionary(boon => boon.Id, _ => 0);
        const int Offers = 20000;

        for (int seed = 1; seed <= Offers / 4; seed++)
        {
            for (int act = 1; act <= 4; act++)
            {
                foreach (string id in ExpeditionBoons.RollOffer(seed, act, Array.Empty<string>(), hasPet: true))
                {
                    seen[id]++;
                }
            }
        }

        double expected = Offers * (double) ExpeditionBoons.OfferSize / ExpeditionBoons.Catalog.Count;

        foreach ((string id, int count) in seen)
        {
            Assert.That(count, Is.InRange(expected * 0.85, expected * 1.15), $"{id}: {count} offers, expected about {expected:0}");
        }

        Assert.That(LootIds.Length + OtherIds.Length, Is.EqualTo(ExpeditionBoons.Catalog.Count));
    }

    [Test]
    public void Offer_ShrinksWhenFewBoonsAreLeft_AndIsEmptyWhenAllAreTaken()
    {
        List<string> all = ExpeditionBoons.Catalog.Select(boon => boon.Id).ToList();
        List<string> allButOne = all.Skip(1).ToList();

        Assert.That(ExpeditionBoons.RollOffer(9, 2, all), Is.Empty);
        Assert.That(ExpeditionBoons.RollOffer(9, 2, allButOne), Is.EqualTo(new[] { all[0] }));
    }

    [Test]
    public void OfferAtActBreak_StoresTheSeededOffer_AndReportsAReplacedOne()
    {
        ExpeditionRun run = new() { Seed = 1234, Act = 2 };
        run.Boons.Add(ExpeditionBoons.Bloodlust);

        bool offered = ExpeditionBoons.OfferAtActBreak(run, 2, out bool replaced);

        Assert.That(offered, Is.True);
        Assert.That(replaced, Is.False);
        Assert.That(run.PendingBoonOffer, Is.EqualTo(ExpeditionBoons.RollOffer(1234, 2, new[] { ExpeditionBoons.Bloodlust })));

        ExpeditionBoons.OfferAtActBreak(run, 3, out replaced);

        Assert.That(replaced, Is.True);
        Assert.That(run.PendingBoonOffer, Is.EqualTo(ExpeditionBoons.RollOffer(1234, 3, new[] { ExpeditionBoons.Bloodlust })));
    }

    [Test]
    public void Fortune_ShiftsTheRolledRarityOneStep_UpToEpic_NeverToUnique()
    {
        string[] fortune = { ExpeditionBoons.Fortune };

        Assert.That(ExpeditionBoons.ShiftRolledRarity(fortune, eLootRarity.Common), Is.EqualTo(eLootRarity.Magic));
        Assert.That(ExpeditionBoons.ShiftRolledRarity(fortune, eLootRarity.Magic), Is.EqualTo(eLootRarity.Rare));
        Assert.That(ExpeditionBoons.ShiftRolledRarity(fortune, eLootRarity.Rare), Is.EqualTo(eLootRarity.Epic));

        // Uniques come only from the roll (and so from the unique catalog), never from a shifted Epic.
        Assert.That(ExpeditionBoons.ShiftRolledRarity(fortune, eLootRarity.Epic), Is.EqualTo(eLootRarity.Epic));
        Assert.That(ExpeditionBoons.ShiftRolledRarity(fortune, eLootRarity.Unique), Is.EqualTo(eLootRarity.Unique));
    }

    [Test]
    public void RolledRarity_IsUnchangedWithoutFortune()
    {
        string[] others = { ExpeditionBoons.BountyHunter, ExpeditionBoons.TreasureSense, ExpeditionBoons.Ironhide };

        Assert.That(ExpeditionBoons.ShiftRolledRarity(others, eLootRarity.Magic), Is.EqualTo(eLootRarity.Magic));
        Assert.That(ExpeditionBoons.ShiftRolledRarity(Array.Empty<string>(), eLootRarity.Rare), Is.EqualTo(eLootRarity.Rare));
        Assert.That(ExpeditionBoons.ShiftRolledRarity((IReadOnlyList<string>) null, eLootRarity.Common), Is.EqualTo(eLootRarity.Common));
    }

    [Test]
    public void RetiredBoons_AreNeverOffered()
    {
        foreach (string id in new[] { ExpeditionBoons.Greed, ExpeditionBoons.Prospector, ExpeditionBoons.Scavenger, ExpeditionBoons.Frostbite })
        {
            Assert.That(ExpeditionBoons.Retired, Does.Contain(id));
            Assert.That(ExpeditionBoons.Find(id), Is.Null);
        }

        for (int seed = 0; seed < 200; seed++)
        {
            for (int act = 1; act <= 4; act++)
            {
                List<string> offer = ExpeditionBoons.RollOffer(seed, act, Array.Empty<string>());

                Assert.That(offer.Intersect(ExpeditionBoons.Retired), Is.Empty);
            }
        }
    }

    [Test]
    public void EveryBoonHasItsOwnBuffBarIcon()
    {
        var icons = ExpeditionBoons.Catalog.Select(boon => BoonIconEffect.IconFor(boon.Id)).ToList();

        Assert.That(icons, Is.All.GreaterThan(0));
        Assert.That(icons.Distinct().Count(), Is.EqualTo(icons.Count), "each boon has a different icon");
    }

    [Test]
    public void Gauntlet_LeavesOutPhoenixFeatherAndTheLootBoons()
    {
        Assert.That(BoonEffects.NotInGauntlet, Does.Contain(ExpeditionBoons.PhoenixFeather));
        Assert.That(BoonEffects.NotInGauntlet, Does.Contain(ExpeditionBoons.Fortune).And.Contain(ExpeditionBoons.BountyHunter).And.Contain(ExpeditionBoons.TreasureSense));
        Assert.That(BoonEffects.NotInGauntlet, Does.Not.Contain(ExpeditionBoons.MarkedPrey).And.Not.Contain(ExpeditionBoons.Executioner));
    }

    [Test]
    public void OwesBoon_WhenFewerBoonsThanActsWonAndNoOfferWaiting()
    {
        var run = new ExpeditionRun { Act = 3 };
        run.Boons.Add(ExpeditionBoons.Fortune);

        Assert.That(ExpeditionBoons.OwesBoon(run), Is.True);

        run.PendingBoonOffer = new List<string> { ExpeditionBoons.Glory };
        Assert.That(ExpeditionBoons.OwesBoon(run), Is.False);

        run.PendingBoonOffer = new List<string>();
        run.Boons.Add(ExpeditionBoons.Glory);
        Assert.That(ExpeditionBoons.OwesBoon(run), Is.False);
    }

    [Test]
    public void LootHooks_ReadTheRunsBoons()
    {
        ExpeditionRun run = new() { Seed = 5, Act = 1 };
        run.Boons.Add(ExpeditionBoons.Fortune);
        run.Boons.Add(ExpeditionBoons.TreasureSense);

        Assert.That(ExpeditionBoons.ShiftRolledRarity(run, eLootRarity.Magic), Is.EqualTo(eLootRarity.Rare));
        Assert.That(ExpeditionBoons.ShiftRolledRarity((ExpeditionRun) null, eLootRarity.Magic), Is.EqualTo(eLootRarity.Magic));
        Assert.That(ExpeditionBoons.UniqueWeightMultiplier(run), Is.EqualTo(2));
        Assert.That(ExpeditionBoons.UniqueWeightMultiplier(null), Is.EqualTo(1));
    }

    [Test]
    public void DescribeBoons_ListsTakenBoonsAndThePendingOffer()
    {
        ExpeditionRun run = new() { Seed = 1, Act = 2 };

        Assert.That(ExpeditionBoons.DescribeBoons(run), Is.Empty);
        Assert.That(ExpeditionBoons.DescribeBoons(null), Is.Empty);

        run.Boons.Add(ExpeditionBoons.Ironhide);
        run.Boons.Add(ExpeditionBoons.Fortune);
        run.PendingBoonOffer = new List<string> { ExpeditionBoons.Glory, ExpeditionBoons.Warding, ExpeditionBoons.Bloodlust };

        List<string> lines = ExpeditionBoons.DescribeBoons(run).ToList();
        string taken = lines.Single(line => line.StartsWith("Boons:", StringComparison.Ordinal));
        string offer = lines.Single(line => line.Contains("/run boons", StringComparison.Ordinal));

        Assert.That(taken, Does.Contain("Ironhide"));
        Assert.That(taken, Does.Contain("Fortune"));
        Assert.That(offer, Does.Contain("Glory"));
        Assert.That(offer, Does.Contain("Warding"));
        Assert.That(offer, Does.Contain("Bloodlust"));
    }

    [Test]
    public void OfferLines_NumberTheOffer()
    {
        ExpeditionRun run = new() { Seed = 1, Act = 2 };
        run.PendingBoonOffer = new List<string> { ExpeditionBoons.Glory, ExpeditionBoons.Warding, ExpeditionBoons.Bloodlust };

        List<string> lines = ExpeditionBoons.OfferLines(run);

        Assert.That(lines.Any(line => line.StartsWith("1. Glory", StringComparison.Ordinal)), Is.True);
        Assert.That(lines.Any(line => line.StartsWith("2. Warding", StringComparison.Ordinal)), Is.True);
        Assert.That(lines.Any(line => line.StartsWith("3. Bloodlust", StringComparison.Ordinal)), Is.True);

        run.PendingBoonOffer = new List<string>();

        Assert.That(ExpeditionBoons.OfferLines(run).First(), Does.StartWith("No boon is waiting"));
    }

    [Test]
    public void RollOffer_PureMelee_NeverOffersCasterOnlyBoons()
    {
        for (int seed = 1; seed <= 300; seed++)
        {
            for (int act = 1; act <= 4; act++)
            {
                List<string> offer = ExpeditionBoons.RollOffer(seed, act, new List<string>(), usesPower: false);

                Assert.That(offer.Any(ExpeditionBoons.CasterOnly.Contains), Is.False, $"seed {seed}, act {act}");
                Assert.That(offer.Count, Is.EqualTo(ExpeditionBoons.OfferSize), $"seed {seed}, act {act}");
            }
        }
    }

    [Test]
    public void RollOffer_NoPet_NeverOffersPetOnlyBoons()
    {
        for (int seed = 1; seed <= 300; seed++)
        {
            for (int act = 1; act <= 4; act++)
            {
                List<string> offer = ExpeditionBoons.RollOffer(seed, act, new List<string>());

                Assert.That(offer.Any(ExpeditionBoons.PetOnly.Contains), Is.False, $"seed {seed}, act {act}");
                Assert.That(offer.Count, Is.EqualTo(ExpeditionBoons.OfferSize), $"seed {seed}, act {act}");
            }
        }

        Assert.That(ExpeditionBoons.PetOnly, Does.Contain(ExpeditionBoons.BeastBond));
        Assert.That(ExpeditionBoons.PetClasses, Does.Contain(eCharacterClass.Hunter).And.Contain(eCharacterClass.Druid).And.Not.Contain(eCharacterClass.Armsman));
    }

    [Test]
    public void RollOffer_Reroll_LeavesOutTheRerolledBoons()
    {
        for (int seed = 1; seed <= 300; seed++)
        {
            List<string> first = ExpeditionBoons.RollOffer(seed, 2, new List<string>());
            List<string> rerolled = ExpeditionBoons.RollOffer(seed, 2, new List<string>(), true, first, 1);
            List<string> again = ExpeditionBoons.RollOffer(seed, 2, new List<string>(), true, first, 1);

            Assert.That(rerolled.Intersect(first), Is.Empty, $"seed {seed}");
            Assert.That(rerolled.Count, Is.EqualTo(ExpeditionBoons.OfferSize), $"seed {seed}");
            Assert.That(again, Is.EqualTo(rerolled), $"seed {seed}: the same seed and reroll give the same offer");
        }
    }

    [Test]
    public void RollOffer_Reroll_FallsBackWhenTooFewBoonsAreLeft()
    {
        List<string> left = ExpeditionBoons.Catalog.Select(boon => boon.Id).Take(4).ToList();
        List<string> taken = ExpeditionBoons.Catalog.Select(boon => boon.Id).Skip(4).ToList();
        List<string> first = left.Take(3).ToList();

        List<string> rerolled = ExpeditionBoons.RollOffer(9, 3, taken, true, first, 1);

        Assert.That(rerolled.Count, Is.EqualTo(3));
        Assert.That(rerolled, Does.Contain(left[3]), "the one fresh boon is always in the rerolled offer");
    }
}
