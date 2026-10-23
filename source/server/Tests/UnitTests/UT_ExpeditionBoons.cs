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
    public void Offer_AlwaysHoldsALootBoon_WhileOneIsLeft()
    {
        var rng = new Random(77);

        for (int seed = 1; seed <= 500; seed++)
        {
            // Any of the other boons, and all loot boons but one.
            List<string> taken = OtherIds.Where(_ => rng.Next(2) == 0).ToList();
            string spared = LootIds[rng.Next(LootIds.Length)];
            taken.AddRange(LootIds.Where(id => id != spared && rng.Next(2) == 0));

            List<string> offer = ExpeditionBoons.RollOffer(seed, 1 + seed % 4, taken);

            Assert.That(offer.Any(id => ExpeditionBoons.Find(id).IsLoot), Is.True, $"seed {seed}, taken {string.Join(",", taken)}");
        }
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
        string[] others = { ExpeditionBoons.Greed, ExpeditionBoons.Prospector, ExpeditionBoons.Ironhide };

        Assert.That(ExpeditionBoons.ShiftRolledRarity(others, eLootRarity.Magic), Is.EqualTo(eLootRarity.Magic));
        Assert.That(ExpeditionBoons.ShiftRolledRarity(Array.Empty<string>(), eLootRarity.Rare), Is.EqualTo(eLootRarity.Rare));
        Assert.That(ExpeditionBoons.ShiftRolledRarity((IReadOnlyList<string>) null, eLootRarity.Common), Is.EqualTo(eLootRarity.Common));
    }

    [Test]
    public void LootChance_GreedAndProspectorAddPercentagePoints()
    {
        Assert.That(ExpeditionBoons.LootChanceWithBoons(Array.Empty<string>(), 40), Is.EqualTo(40));
        Assert.That(ExpeditionBoons.LootChanceWithBoons(new[] { ExpeditionBoons.Greed }, 40), Is.EqualTo(40 + ExpeditionBoons.GreedChanceBonus));
        Assert.That(ExpeditionBoons.LootChanceWithBoons(new[] { ExpeditionBoons.Prospector }, 40), Is.EqualTo(40 + ExpeditionBoons.ProspectorChanceBonus));
        Assert.That(ExpeditionBoons.LootChanceWithBoons(new[] { ExpeditionBoons.Greed, ExpeditionBoons.Prospector }, 40), Is.EqualTo(40 + ExpeditionBoons.GreedChanceBonus + ExpeditionBoons.ProspectorChanceBonus));
        Assert.That(ExpeditionBoons.LootChanceWithBoons(new[] { ExpeditionBoons.Fortune, ExpeditionBoons.Warding }, 40), Is.EqualTo(40));
        Assert.That(ExpeditionBoons.GreedChanceBonus, Is.EqualTo(25));
    }

    [Test]
    public void LootHooks_ReadTheRunsBoons()
    {
        ExpeditionRun run = new() { Seed = 5, Act = 1 };
        run.Boons.Add(ExpeditionBoons.Fortune);
        run.Boons.Add(ExpeditionBoons.Greed);

        Assert.That(ExpeditionBoons.ShiftRolledRarity(run, eLootRarity.Magic), Is.EqualTo(eLootRarity.Rare));
        Assert.That(ExpeditionBoons.ShiftRolledRarity((ExpeditionRun) null, eLootRarity.Magic), Is.EqualTo(eLootRarity.Magic));
        Assert.That(ExpeditionBoons.AdjustLootChance(null, run, 50), Is.EqualTo(75));
        Assert.That(ExpeditionBoons.AdjustLootChance(null, null, 50), Is.EqualTo(50));
    }

    [Test]
    public void DescribeBoons_ListsTakenBoonsAndThePendingOffer()
    {
        ExpeditionRun run = new() { Seed = 1, Act = 2 };

        Assert.That(ExpeditionBoons.DescribeBoons(run), Is.Empty);
        Assert.That(ExpeditionBoons.DescribeBoons(null), Is.Empty);

        run.Boons.Add(ExpeditionBoons.Ironhide);
        run.Boons.Add(ExpeditionBoons.Fortune);
        run.PendingBoonOffer = new List<string> { ExpeditionBoons.Greed, ExpeditionBoons.Warding, ExpeditionBoons.HuntersEye };

        List<string> lines = ExpeditionBoons.DescribeBoons(run).ToList();
        string taken = lines.Single(line => line.StartsWith("Boons:", StringComparison.Ordinal));
        string offer = lines.Single(line => line.Contains("/run boons", StringComparison.Ordinal));

        Assert.That(taken, Does.Contain("Ironhide"));
        Assert.That(taken, Does.Contain("Fortune"));
        Assert.That(offer, Does.Contain("Greed"));
        Assert.That(offer, Does.Contain("Warding"));
        Assert.That(offer, Does.Contain("Hunter's Eye"));
    }

    [Test]
    public void OfferLines_NumberTheOffer()
    {
        ExpeditionRun run = new() { Seed = 1, Act = 2 };
        run.PendingBoonOffer = new List<string> { ExpeditionBoons.Greed, ExpeditionBoons.Warding, ExpeditionBoons.Bloodlust };

        List<string> lines = ExpeditionBoons.OfferLines(run);

        Assert.That(lines.Any(line => line.StartsWith("1. Greed", StringComparison.Ordinal)), Is.True);
        Assert.That(lines.Any(line => line.StartsWith("2. Warding", StringComparison.Ordinal)), Is.True);
        Assert.That(lines.Any(line => line.StartsWith("3. Bloodlust", StringComparison.Ordinal)), Is.True);

        run.PendingBoonOffer = new List<string>();

        Assert.That(ExpeditionBoons.OfferLines(run).First(), Does.StartWith("No boon is waiting"));
    }
}
