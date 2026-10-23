using System;
using System.Collections.Generic;
using DOL.GS.Expedition;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>Expedition loot's pure logic: rarity rolls, the pity counter, rarity tags and junk rules. No server is needed.</summary>
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
        Assert.That(ExpeditionLoot.KillDropChance(7, 10, 8, 12, 0, Defaults), Is.EqualTo(50), "below the act band but not grey");
        Assert.That(ExpeditionLoot.KillDropChance(0, 1, 1, 10, 0, Defaults), Is.EqualTo(50), "a level-0 blue mob for a level-1 player in act 1");
        Assert.That(ExpeditionLoot.CountsForPity(0, 1, 1, 10), Is.True);
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
    public void DecoratedNames_CarryTheRarity_AndStayShort()
    {
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Common, "Studded Boots"), Is.EqualTo("[Common] Studded Boots"));
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Magic, "Studded Boots"), Is.EqualTo("[Magic] Studded Boots"));
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Rare, " Studded Boots "), Is.EqualTo("[Rare] Studded Boots"));
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Unique, "Hearthstone Band"), Is.EqualTo("Hearthstone Band"));
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Epic, new string('x', 100)).Length, Is.EqualTo(ExpeditionLoot.MaxNameLength));
        Assert.That(ExpeditionLoot.DecorateName(eLootRarity.Common, new string('x', 100)).Length, Is.EqualTo(ExpeditionLoot.MaxNameLength));
    }

    [Test]
    public void TryGetRarity_ReadsTheExpeditionSuffix_AndIgnoresOtherItems()
    {
        Assert.That(ExpeditionLoot.TryParseRarity("Atlas ROG | Armsman | Expedition Common", out eLootRarity common), Is.True);
        Assert.That(common, Is.EqualTo(eLootRarity.Common));
        Assert.That(ExpeditionLoot.TryParseRarity("Atlas ROG | Wizard | Expedition Rare", out eLootRarity rare), Is.True);
        Assert.That(rare, Is.EqualTo(eLootRarity.Rare));
        Assert.That(ExpeditionLoot.TryParseRarity("Some lore | Atlas ROG | Expedition Unique", out eLootRarity unique), Is.True);
        Assert.That(unique, Is.EqualTo(eLootRarity.Unique));

        Assert.That(ExpeditionLoot.TryParseRarity(null, out _), Is.False);
        Assert.That(ExpeditionLoot.TryParseRarity(string.Empty, out _), Is.False);
        Assert.That(ExpeditionLoot.TryParseRarity("Atlas ROG | Armsman", out _), Is.False);
        Assert.That(ExpeditionLoot.TryParseRarity("Atlas ROG | Armsman | Expedition Legendary", out _), Is.False);
        Assert.That(ExpeditionLoot.TryParseRarity("Atlas ROG | Armsman | Expedition 3", out _), Is.False);
        Assert.That(ExpeditionLoot.TryParseRarity("Atlas ROG | Armsman | Expedition magic", out _), Is.False);

        DbInventoryItem expedition = new() { Template = new DbItemTemplate { Description = "Atlas ROG | Cleric | Expedition Magic" } };
        DbInventoryItem stock = new() { Template = new DbItemTemplate { Description = "A plain sword." } };

        Assert.That(ExpeditionLoot.TryGetRarity(expedition, out eLootRarity magic), Is.True);
        Assert.That(magic, Is.EqualTo(eLootRarity.Magic));
        Assert.That(ExpeditionLoot.TryGetRarity(stock, out _), Is.False);
        Assert.That(ExpeditionLoot.TryGetRarity(null, out _), Is.False);
    }

    [Test]
    public void Junk_TakesTheRarityAndBelow_NeverEpicOrUnique()
    {
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Common, eLootRarity.Common), Is.True);
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Magic, eLootRarity.Common), Is.False);
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Common, eLootRarity.Magic), Is.True);
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Magic, eLootRarity.Magic), Is.True);
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Rare, eLootRarity.Magic), Is.False);
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Rare, eLootRarity.Rare), Is.True);
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Epic, eLootRarity.Rare), Is.False);
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Epic, eLootRarity.Epic), Is.False);
        Assert.That(ExpeditionLoot.IsJunk(eLootRarity.Unique, eLootRarity.Unique), Is.False);
    }

    [Test]
    public void AutoJunk_SellsOnlyAtOrBelowTheSetting_AndOnlyCommonOrMagic()
    {
        Assert.That(ExpeditionLoot.ShouldAutoJunk(eLootRarity.Common, null), Is.False);
        Assert.That(ExpeditionLoot.ShouldAutoJunk(eLootRarity.Common, eLootRarity.Common), Is.True);
        Assert.That(ExpeditionLoot.ShouldAutoJunk(eLootRarity.Magic, eLootRarity.Common), Is.False);
        Assert.That(ExpeditionLoot.ShouldAutoJunk(eLootRarity.Common, eLootRarity.Magic), Is.True);
        Assert.That(ExpeditionLoot.ShouldAutoJunk(eLootRarity.Magic, eLootRarity.Magic), Is.True);
        Assert.That(ExpeditionLoot.ShouldAutoJunk(eLootRarity.Rare, eLootRarity.Magic), Is.False);
        Assert.That(ExpeditionLoot.ShouldAutoJunk(eLootRarity.Unique, eLootRarity.Magic), Is.False);
        Assert.That(ExpeditionLoot.ShouldAutoJunk(eLootRarity.Common, eLootRarity.Rare), Is.False, "the setting goes up to Magic only");

        Assert.That(ExpeditionLoot.ParseAutoJunk("common"), Is.EqualTo(eLootRarity.Common));
        Assert.That(ExpeditionLoot.ParseAutoJunk(" Magic "), Is.EqualTo(eLootRarity.Magic));
        Assert.That(ExpeditionLoot.ParseAutoJunk("off"), Is.Null);
        Assert.That(ExpeditionLoot.ParseAutoJunk("rare"), Is.Null);
        Assert.That(ExpeditionLoot.ParseAutoJunk(null), Is.Null);
    }

    [Test]
    public void SellValue_MatchesTheMerchantFormula()
    {
        Assert.That(ExpeditionLoot.SellValue(1000, 1, 1, 50), Is.EqualTo(500));
        Assert.That(ExpeditionLoot.SellValue(1000, 3, 1, 50), Is.EqualTo(1500));
        Assert.That(ExpeditionLoot.SellValue(1000, 0, 0, 50), Is.EqualTo(500));
        Assert.That(ExpeditionLoot.SellValue(1, 1, 1, 50), Is.EqualTo(1), "a 1-copper item still sells for its price");
        Assert.That(ExpeditionLoot.SellValue(0, 1, 1, 50), Is.EqualTo(0));
    }

    [Test]
    public void FortuneBoon_ShiftsTheRolledRarity_OnlyWithTheBoon()
    {
        Assert.That(ExpeditionBoons.ShiftRolledRarity(new[] { ExpeditionBoons.Fortune }, eLootRarity.Common), Is.EqualTo(eLootRarity.Magic));
        Assert.That(ExpeditionBoons.ShiftRolledRarity(Array.Empty<string>(), eLootRarity.Common), Is.EqualTo(eLootRarity.Common));
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

    [Test]
    public void BonusLines_RisePerRarity_AndAreCountedFromTheItem()
    {
        Assert.That(ExpeditionLoot.BonusLinesFor(eLootRarity.Common), Is.EqualTo(0));
        Assert.That(ExpeditionLoot.BonusLinesFor(eLootRarity.Magic), Is.EqualTo(1));
        Assert.That(ExpeditionLoot.BonusLinesFor(eLootRarity.Rare), Is.EqualTo(2));
        Assert.That(ExpeditionLoot.BonusLinesFor(eLootRarity.Unique), Is.EqualTo(2));

        DbItemTemplate item = new();
        Assert.That(ExpeditionLoot.CountBonusLines(item), Is.EqualTo(0));

        item.Bonus3Type = (int) eProperty.Strength;
        item.Bonus3 = 4;
        item.Bonus7Type = (int) eProperty.MaxHealth;
        item.Bonus7 = 12;
        Assert.That(ExpeditionLoot.CountBonusLines(item), Is.EqualTo(2));
    }
}
