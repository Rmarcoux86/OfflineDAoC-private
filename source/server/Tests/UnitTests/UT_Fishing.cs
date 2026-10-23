using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS.Fishing;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_Fishing
{
    private const int Iterations = 10_000;

    private static Dictionary<eFishingRarity, int> RollMany(eRealm realm, int skill, int seed)
    {
        var rng = new Random(seed);
        var counts = Enum.GetValues<eFishingRarity>().ToDictionary(r => r, _ => 0);

        for (int i = 0; i < Iterations; i++)
        {
            counts[FishingCatalog.Roll(realm, skill, rng).Rarity]++;
        }

        return counts;
    }

    [Test]
    public void Templates_HaveUniqueLowercaseIdsAndSaneValues()
    {
        IReadOnlyList<DbItemTemplate> templates = FishingCatalog.AllTemplates();
        HashSet<string> ids = new(StringComparer.Ordinal);

        Assert.That(templates, Is.Not.Empty);

        foreach (DbItemTemplate template in templates)
        {
            Assert.That(ids.Add(template.Id_nb), Is.True, $"duplicate Id_nb {template.Id_nb}");
            Assert.That(template.Id_nb, Does.Match("^[a-z0-9_]+$"), template.Id_nb);
            Assert.That(template.Name, Is.Not.Null.And.Not.Empty, template.Id_nb);
            Assert.That(template.Price, Is.GreaterThan(0), template.Id_nb);
            Assert.That(template.Model, Is.GreaterThan(0), template.Id_nb);
        }

        Assert.That(ids, Does.Contain(FishingCatalog.PoleTemplateId));
        Assert.That(ids, Does.Contain("offline_fish_alb_brown_trout_2"));
    }

    [Test]
    public void Pole_IsATwoHandedGenericWeaponAnyClassCanHold()
    {
        DbItemTemplate pole = FishingCatalog.AllTemplates().Single(t => t.Id_nb == FishingCatalog.PoleTemplateId);

        Assert.That(pole.Object_Type, Is.EqualTo((int)eObjectType.GenericWeapon));
        Assert.That(pole.Item_Type, Is.EqualTo((int)eInventorySlot.TwoHandWeapon));
        Assert.That(pole.Hand, Is.EqualTo(1));
        Assert.That(pole.DPS_AF, Is.EqualTo(1));
        Assert.That(pole.Name, Is.EqualTo("Fishing Pole"));
    }

    [Test]
    public void Roll_AtSkillZero_IsMostlyCommonAndLegendaryIsRare()
    {
        Dictionary<eFishingRarity, int> counts = RollMany(eRealm.Albion, 0, 1234);

        Assert.That(counts[eFishingRarity.Common], Is.GreaterThan(Iterations / 2));
        Assert.That(counts[eFishingRarity.Legendary], Is.LessThan(Iterations * 3 / 100));
        Assert.That(counts.Values.Sum(), Is.EqualTo(Iterations));
    }

    [Test]
    public void Roll_AtSkillHundred_LandsMoreRareAndLegendaryThanANovice()
    {
        Dictionary<eFishingRarity, int> novice = RollMany(eRealm.Midgard, 0, 77);
        Dictionary<eFishingRarity, int> master = RollMany(eRealm.Midgard, 100, 77);

        int noviceTop = novice[eFishingRarity.Rare] + novice[eFishingRarity.Legendary];
        int masterTop = master[eFishingRarity.Rare] + master[eFishingRarity.Legendary];

        Assert.That(masterTop, Is.GreaterThan(noviceTop));
    }

    [TestCase(0)]
    [TestCase(50)]
    [TestCase(100)]
    public void Roll_WeightsStayInsideTheSpeciesRange(int skill)
    {
        var rng = new Random(42 + skill);

        foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia, eRealm.None })
        {
            for (int i = 0; i < Iterations / 4; i++)
            {
                FishingCatch c = FishingCatalog.Roll(realm, skill, rng);

                if (c.Rarity == eFishingRarity.Junk)
                {
                    Assert.That(c.WeightTenths, Is.EqualTo(0));
                    Assert.That(c.SkillGain, Is.EqualTo(0));

                    continue;
                }

                Assert.That(FishingCatalog.TryGetSpeciesWeightRange(c.TemplateId, out int min, out int max), Is.True, c.TemplateId);

                if (c.Rarity == eFishingRarity.Legendary)
                {
                    Assert.That(c.WeightTenths, Is.GreaterThan(max), c.DisplayName);
                    Assert.That(c.TemplateId, Does.EndWith("_4"));
                    Assert.That(c.SkillGain, Is.EqualTo(10));

                    continue;
                }

                Assert.That(c.WeightTenths, Is.InRange(min, max), c.TemplateId);
            }
        }
    }

    [Test]
    public void BiteDelay_StaysInBoundsAndShortensWithSkill()
    {
        var rng = new Random(9);
        long noviceTotal = 0;
        long masterTotal = 0;

        for (int i = 0; i < Iterations; i++)
        {
            int novice = FishingCatalog.RollBiteDelayMs(0, rng);
            int master = FishingCatalog.RollBiteDelayMs(100, rng);

            Assert.That(novice, Is.InRange(6000, 25000));
            Assert.That(master, Is.InRange(6000, 25000));
            noviceTotal += novice;
            masterTotal += master;
        }

        Assert.That(masterTotal, Is.LessThan(noviceTotal));
    }

    [Test]
    public void DescribeCatch_FormatsWeightWithOneDecimal()
    {
        var trout = new FishingCatch { DisplayName = "brown trout", WeightTenths = 32, Rarity = eFishingRarity.Common };
        var eel = new FishingCatch { DisplayName = "eel", WeightTenths = 10, Rarity = eFishingRarity.Uncommon };
        var boot = new FishingCatch { DisplayName = "old boot", WeightTenths = 0, Rarity = eFishingRarity.Junk };
        var pike = new FishingCatch { DisplayName = "Old Bartholomew, the legendary pike", WeightTenths = 335, Rarity = eFishingRarity.Legendary };

        Assert.That(FishingCatalog.DescribeCatch(trout), Is.EqualTo("a brown trout weighing 3.2 lbs"));
        Assert.That(FishingCatalog.DescribeCatch(eel), Is.EqualTo("an eel weighing 1.0 lbs"));
        Assert.That(FishingCatalog.DescribeCatch(boot), Is.EqualTo("an old boot"));
        Assert.That(FishingCatalog.DescribeCatch(pike), Is.EqualTo("Old Bartholomew, the legendary pike weighing 33.5 lbs"));
    }

    [Test]
    public void Roll_ForEveryRealmValue_ReturnsAKnownTemplate()
    {
        HashSet<string> ids = FishingCatalog.AllTemplates().Select(t => t.Id_nb).ToHashSet();
        var rng = new Random(5);

        foreach (eRealm realm in Enum.GetValues<eRealm>())
        {
            for (int i = 0; i < 500; i++)
            {
                FishingCatch c = null;

                Assert.DoesNotThrow(() => c = FishingCatalog.Roll(realm, i % 101, rng));
                Assert.That(ids, Does.Contain(c.TemplateId), $"{realm}: {c.TemplateId}");
                Assert.That(c.DisplayName, Is.Not.Null.And.Not.Empty);
            }
        }
    }

    [Test]
    public void WaitingFlavorLine_IsSometimesNull()
    {
        var rng = new Random(3);
        int lines = 0;

        for (int i = 0; i < Iterations; i++)
        {
            if (FishingCatalog.RollWaitingFlavorLine(rng) != null)
            {
                lines++;
            }
        }

        Assert.That(lines, Is.InRange(Iterations / 5, Iterations * 2 / 5));
    }

    [Test]
    public void Progress_RecordCatch_AwardsSkillGainClampsSkillAndKeepsTheHeaviestCatch()
    {
        var progress = new FishingProgress { Skill = 95 };

        // RecordCatch is the only place skill is awarded; the session adds nothing on top.
        progress.RecordCast();
        progress.RecordCatch(new FishingCatch { DisplayName = "salmon", WeightTenths = 120, Rarity = eFishingRarity.Rare, SkillGain = 4 });

        Assert.That(progress.Skill, Is.EqualTo(99));

        progress.RecordCatch(new FishingCatch { DisplayName = "perch", WeightTenths = 20, Rarity = eFishingRarity.Common, SkillGain = 10 });

        Assert.That(progress.Skill, Is.EqualTo(100));
        Assert.That(progress.Casts, Is.EqualTo(1));
        Assert.That(progress.Catches, Is.EqualTo(2));
        Assert.That(progress.BestWeightTenths, Is.EqualTo(120));
        Assert.That(progress.BestCatchName, Is.EqualTo("salmon"));
    }
}
