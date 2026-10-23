using System;
using System.Collections.Generic;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Expedition loot's pure logic: rarity rolls, the pity counter and unique names. No server is needed.</summary>
[TestFixture]
public class UT_ExpeditionLoot
{
    private const int Samples = 20_000;

    /// <summary>The shipped defaults (ServerProperties EXPEDITION_LOOT_*).</summary>
    private static readonly LootTuning Defaults = new(BaseChance: 50, GreyChance: 15, HighConBonus: 15, PityStep: 10, PityGuarantee: 4);

    [Test]
    public void RarityRoll_MatchesBaseWeights()
    {
        Random rng = new(1234);
        int[] counts = new int[5];

        for (int i = 0; i < Samples; i++)
        {
            counts[(int) ExpeditionLoot.RollRarity(rng, false)]++;
        }

        double[] expected = { 0.60, 0.27, 0.09, 0.03, 0.01 };

        for (int i = 0; i < expected.Length; i++)
        {
            double share = counts[i] / (double) Samples;

            Assert.That(share, Is.EqualTo(expected[i]).Within(0.015), $"{(eLootRarity) i}");
        }
    }

    [Test]
    public void BossRoll_IsAlwaysRareOrBetter_AndMatchesBossWeights()
    {
        Random rng = new(99);
        int[] counts = new int[5];

        for (int i = 0; i < Samples; i++)
        {
            eLootRarity rarity = ExpeditionLoot.RollRarity(rng, true, eLootRarity.Rare);

            Assert.That(rarity, Is.GreaterThanOrEqualTo(eLootRarity.Rare));
            counts[(int) rarity]++;
        }

        Assert.That(counts[(int) eLootRarity.Epic] / (double) Samples, Is.EqualTo(0.35).Within(0.015));
        Assert.That(counts[(int) eLootRarity.Unique] / (double) Samples, Is.EqualTo(0.15).Within(0.015));
    }

    [Test]
    public void BossRoll_WithCommonFloor_StillNeverCommonOrMagic()
    {
        Random rng = new(7);

        for (int i = 0; i < Samples; i++)
        {
            Assert.That(ExpeditionLoot.RollRarity(rng, true), Is.GreaterThanOrEqualTo(eLootRarity.Rare));
        }
    }

    [Test]
    public void FloorRoll_NeverBelowFloor()
    {
        Random rng = new(11);

        for (int i = 0; i < Samples; i++)
        {
            Assert.That(ExpeditionLoot.RollRarity(rng, false, eLootRarity.Magic), Is.GreaterThanOrEqualTo(eLootRarity.Magic));
        }
    }

    [Test]
    public void BossFloor_LastDropIsEpicFromAct3_AndUniqueOnAct5()
    {
        Assert.That(ExpeditionLoot.BossFloor(0, 3, 5), Is.EqualTo(eLootRarity.Rare));
        Assert.That(ExpeditionLoot.BossFloor(2, 3, 1), Is.EqualTo(eLootRarity.Rare));
        Assert.That(ExpeditionLoot.BossFloor(2, 3, 3), Is.EqualTo(eLootRarity.Epic));
        Assert.That(ExpeditionLoot.BossFloor(2, 3, 4), Is.EqualTo(eLootRarity.Epic));
        Assert.That(ExpeditionLoot.BossFloor(2, 3, 5), Is.EqualTo(eLootRarity.Unique));
    }

    [Test]
    public void PityChance_RisesMonotonically_UntilGuaranteed()
    {
        int previous = -1;

        for (int dry = 0; dry < 10; dry++)
        {
            int chance = ExpeditionLoot.KillDropChance(10, 10, 8, 12, dry, Defaults);

            Assert.That(chance, Is.GreaterThanOrEqualTo(previous), $"dry {dry}");
            previous = chance;
        }

        Assert.That(ExpeditionLoot.KillDropChance(10, 10, 8, 12, 0, Defaults), Is.EqualTo(50));
        Assert.That(ExpeditionLoot.KillDropChance(10, 10, 8, 12, 1, Defaults), Is.EqualTo(60));
        Assert.That(ExpeditionLoot.KillDropChance(10, 10, 8, 12, 3, Defaults), Is.EqualTo(100));
    }

    [Test]
    public void GreyAndHighConMobs_UseTheirChances()
    {
        Assert.That(ExpeditionLoot.KillDropChance(4, 10, 0, 0, 3, Defaults), Is.EqualTo(15));
        Assert.That(ExpeditionLoot.KillDropChance(12, 10, 8, 14, 0, Defaults), Is.EqualTo(65));
        Assert.That(ExpeditionLoot.KillDropChance(7, 10, 8, 12, 0, Defaults), Is.EqualTo(15), "below the act band");
    }

    [Test]
    public void PityCounter_ResetsOnDrop_AndNeverAllowsFourDryInBandKills()
    {
        Random rng = new(2024);
        LootLuck luck = new();
        int dryRun = 0;
        int worstDryRun = 0;

        for (int i = 0; i < Samples; i++)
        {
            int before = luck.DryKills;
            int chance = ExpeditionLoot.KillDropChance(10, 10, 8, 12, luck.DryKills, Defaults);
            bool dropped = ExpeditionLoot.RollKill(rng, luck, chance, true);

            if (dropped)
            {
                Assert.That(luck.DryKills, Is.EqualTo(0));
                dryRun = 0;
            }
            else
            {
                Assert.That(luck.DryKills, Is.EqualTo(before + 1));
                dryRun++;
                worstDryRun = Math.Max(worstDryRun, dryRun);
            }
        }

        Assert.That(worstDryRun, Is.LessThan(4));
    }

    [Test]
    public void GreyKills_DoNotRaiseThePity()
    {
        LootLuck luck = new();

        ExpeditionLoot.RollKill(new Random(1), luck, 0, false);

        Assert.That(luck.DryKills, Is.EqualTo(0));
    }

    [Test]
    public void UniqueNames_AreNonEmpty_NoRepeatedWords_AndShort()
    {
        Random rng = new(42);
        string[] nouns = { "Blade", "Helm", "Ring", "Greatsword", "Gauntlets", "Relic" };
        string[] zones = { "Lough Derg", "Isle of Glass", "Campacorentin Forest", string.Empty, null };

        for (int i = 0; i < 1000; i++)
        {
            string name = ExpeditionLoot.GenerateUniqueName(rng, nouns[i % nouns.Length], zones[i % zones.Length]);

            Assert.That(name, Is.Not.Null.And.Not.Empty);
            Assert.That(name.Trim(), Is.EqualTo(name));
            Assert.That(name.Length, Is.LessThanOrEqualTo(ExpeditionLoot.MaxNameLength), name);
            Assert.That(ExpeditionLoot.HasRepeatedWord(name), Is.False, name);
        }
    }

    [Test]
    public void DecoratedNames_CarryTheRarity_AndStayShort()
    {
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Common, "Studded Boots"), Is.EqualTo("Studded Boots"));
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Magic, "Studded Boots"), Is.EqualTo("[Magic] Studded Boots"));
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Epic, new string('x', 100)).Length, Is.EqualTo(ExpeditionLoot.MaxNameLength));
    }

    [Test]
    public void Profiles_RiseWithRarity()
    {
        List<LootProfile> profiles = new();

        foreach (eLootRarity rarity in Enum.GetValues<eLootRarity>())
        {
            profiles.Add(ExpeditionLoot.ProfileFor(rarity));
        }

        for (int i = 1; i < profiles.Count; i++)
        {
            Assert.That(profiles[i].MinUtility, Is.GreaterThanOrEqualTo(profiles[i - 1].MinUtility));
            Assert.That(profiles[i].MinQuality, Is.GreaterThanOrEqualTo(profiles[i - 1].MinQuality));
            Assert.That(profiles[i].LevelBonus, Is.GreaterThanOrEqualTo(profiles[i - 1].LevelBonus));
        }

        Assert.That(ExpeditionLoot.ProfileFor(eLootRarity.Unique).UniqueName, Is.True);
    }
}
