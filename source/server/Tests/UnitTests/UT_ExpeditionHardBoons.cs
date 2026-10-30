using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.Expedition;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>A hard expedition's boons, uniques and shrines: the pure parts. No server is needed.</summary>
[TestFixture]
public class UT_ExpeditionHardBoons
{
    [Test]
    public void OfferSize_IsTwoOnAHardExpedition_AndThreeOtherwise()
    {
        Assert.That(ExpeditionBoons.OfferSizeFor(false), Is.EqualTo(3));
        Assert.That(ExpeditionBoons.OfferSizeFor(true), Is.EqualTo(2));

        for (int seed = 0; seed < 100; seed++)
        {
            for (int act = 1; act <= 4; act++)
            {
                Assert.That(ExpeditionBoons.RollOffer(seed, act, new List<string>()).Count, Is.EqualTo(3), $"seed {seed}, act {act}");
                Assert.That(ExpeditionBoons.RollOffer(seed, act, new List<string>(), hard: true).Count, Is.EqualTo(2), $"seed {seed}, act {act}");
            }
        }
    }

    [Test]
    public void HardOnlyBoons_OnlyComeOnAHardExpedition_AndPhoenixFeatherNeverDoes()
    {
        var seenHard = new HashSet<string>();

        for (int seed = 0; seed < 2000; seed++)
        {
            for (int act = 1; act <= 4; act++)
            {
                List<string> normal = ExpeditionBoons.RollOffer(seed, act, new List<string>(), hasPet: true, ranged: true);
                List<string> hard = ExpeditionBoons.RollOffer(seed, act, new List<string>(), hasPet: true, ranged: true, hard: true);

                Assert.That(normal.Intersect(ExpeditionBoons.HardOnly), Is.Empty, $"normal run, seed {seed}, act {act}");
                Assert.That(hard, Does.Not.Contain(ExpeditionBoons.PhoenixFeather), $"hard run, seed {seed}, act {act}");
                seenHard.UnionWith(hard);
            }
        }

        Assert.That(seenHard, Is.SupersetOf(ExpeditionBoons.HardOnly), "every hard-only boon is offered on a hard expedition");
    }

    [Test]
    public void HardOnlyBoons_AreInTheCatalog_WithIcons()
    {
        Assert.That(ExpeditionBoons.HardOnly.Count, Is.EqualTo(5));

        foreach (string id in ExpeditionBoons.HardOnly)
        {
            BoonDefinition boon = ExpeditionBoons.Find(id);

            Assert.That(boon, Is.Not.Null, id);
            Assert.That(boon.IsLoot, Is.False, id);
            Assert.That(BoonIconEffect.IconFor(id), Is.GreaterThan(0), id);
        }
    }

    [Test]
    public void Reroll_OnAHardExpedition_StillDrawsTwo()
    {
        for (int seed = 0; seed < 50; seed++)
        {
            List<string> first = ExpeditionBoons.RollOffer(seed, 2, new List<string>(), hard: true);
            List<string> rerolled = ExpeditionBoons.RollOffer(seed, 2, new List<string>(), true, first, 1, hard: true);

            Assert.That(rerolled.Count, Is.EqualTo(2), $"seed {seed}");
            Assert.That(rerolled.Intersect(first), Is.Empty, $"seed {seed}");
        }
    }

    [Test]
    public void OfferAtActBreak_PassesTheHardFlag()
    {
        var run = new ExpeditionRun { Seed = 77, Act = 2, Hard = true };

        Assert.That(ExpeditionBoons.OfferAtActBreak(run, 1, out _, hard: ExpeditionHardMode.IsHard(run)), Is.True);
        Assert.That(run.PendingBoonOffer, Is.EqualTo(ExpeditionBoons.RollOffer(77, 1, new List<string>(), hard: true)));
        Assert.That(run.PendingBoonOffer.Count, Is.EqualTo(2));
    }

    [Test]
    public void RejuvenationAndBlessedArms_AreWeakerOnAHardExpedition()
    {
        Assert.That(BoonEffects.RejuvenationIntervalFor(false), Is.EqualTo(6000));
        Assert.That(BoonEffects.RejuvenationIntervalFor(true), Is.EqualTo(10000));

        var normal = new ExpeditionRun { Act = 1 };
        var hard = new ExpeditionRun { Act = 1, Hard = true };

        Assert.That(ExpeditionBuffs.PercentFor(normal, ExpeditionBuffs.StrengthConstitution), Is.EqualTo(30));
        Assert.That(ExpeditionBuffs.PercentFor(hard, ExpeditionBuffs.StrengthConstitution), Is.EqualTo(18));

        BoonDefinition rejuvenation = ExpeditionBoons.Find(ExpeditionBoons.Rejuvenation);

        Assert.That(ExpeditionBoons.EffectFor(rejuvenation, false), Is.EqualTo(rejuvenation.Effect));
        Assert.That(ExpeditionBoons.EffectFor(rejuvenation, true), Does.Contain("every 10 seconds"));
        Assert.That(ExpeditionBoons.EffectFor(ExpeditionBoons.Find(ExpeditionBoons.BlessedArms), true), Does.Contain("18%"));
    }

    [Test]
    public void Unaware_MeansNotInCombatAndNotAttacking()
    {
        Assert.That(BoonEffects.IsUnaware(false, false), Is.True);
        Assert.That(BoonEffects.IsUnaware(true, false), Is.False, "already fighting");
        Assert.That(BoonEffects.IsUnaware(false, true), Is.False, "aggroed and running at someone");
        Assert.That(BoonEffects.IsUnaware(true, true), Is.False);
    }

    [Test]
    public void Behind_IsTheTargetsBackHalf()
    {
        Assert.That(BoonEffects.IsBehind(180), Is.True);
        Assert.That(BoonEffects.IsBehind(120), Is.True);
        Assert.That(BoonEffects.IsBehind(250), Is.True);
        Assert.That(BoonEffects.IsBehind(0), Is.False, "straight ahead");
        Assert.That(BoonEffects.IsBehind(45), Is.False);
        Assert.That(BoonEffects.IsBehind(90), Is.False, "the flank is not behind");
        Assert.That(BoonEffects.IsBehind(270), Is.False);
        Assert.That(BoonEffects.IsBehind(359), Is.False);
    }

    [Test]
    public void Durations_ScaleByThePercent()
    {
        Assert.That(BoonEffects.ScaleDuration(20000, BoonEffects.PoisonerDurationPercent), Is.EqualTo(30000), "Poisoner: 50% longer");
        Assert.That(BoonEffects.ScaleDuration(9000, BoonEffects.DirtyFightingDurationPercent), Is.EqualTo(11700), "Dirty Fighting: 30% longer");
        Assert.That(BoonEffects.ScaleDuration(0, 130), Is.EqualTo(1));
        Assert.That(BoonEffects.PoisonerDamagePercent, Is.EqualTo(125));
        Assert.That(BoonEffects.JuggernautCrowdControlReduction, Is.EqualTo(50), "half as long, as a Stalwart elite");
    }

    [Test]
    public void HardUniques_JoinTheLatePool_OnlyOnAHardExpedition()
    {
        List<UniqueDefinition> late = ExpeditionUniques.InTier(eUniqueTier.Late);
        List<UniqueDefinition> hardLate = ExpeditionUniques.InTier(eUniqueTier.Late, true);
        List<UniqueDefinition> hardOnly = ExpeditionUniques.InTier(eUniqueTier.Hard);

        Assert.That(hardOnly.Count, Is.EqualTo(3));
        Assert.That(late.Any(definition => definition.Tier == eUniqueTier.Hard), Is.False);
        Assert.That(hardLate, Is.EquivalentTo(late.Concat(hardOnly)), "added to the late pool, not in place of it");
        Assert.That(ExpeditionUniques.InTier(eUniqueTier.Mid, true), Is.EquivalentTo(ExpeditionUniques.InTier(eUniqueTier.Mid)));
        Assert.That(ExpeditionUniques.TierMinLevel(eUniqueTier.Hard), Is.EqualTo(40));
        Assert.That(ExpeditionUniques.TierCapLevel(eUniqueTier.Hard), Is.EqualTo(50));
        Assert.That(ExpeditionUniques.ToaPercentFor(eUniqueTier.Hard), Is.EqualTo(ExpeditionUniques.ToaPercentFor(eUniqueTier.Late)));

        var found = new HashSet<string>();
        var rng = new Random(11);

        for (int i = 0; i < hardLate.Count; i++)
        {
            ExpeditionUniques.Pick(rng, eUniqueTier.Late, found, true);
        }

        Assert.That(found, Is.EquivalentTo(hardLate.Select(definition => definition.Name)), "a hard run finds every late and hard unique before a repeat");

        for (int i = 0; i < 200; i++)
        {
            Assert.That(ExpeditionUniques.Pick(rng, eUniqueTier.Late, null).Tier, Is.EqualTo(eUniqueTier.Late), "a normal run never finds a hard unique");
        }
    }

    [Test]
    public void CampMobMatch_StripsAffixesAndTheDreadPrefix()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionShrines.MatchesCampMob("goblin", "goblin", out int affixes), Is.True);
            Assert.That(affixes, Is.EqualTo(0));
            Assert.That(ExpeditionShrines.MatchesCampMob("Swift goblin", "goblin", out affixes), Is.True);
            Assert.That(affixes, Is.EqualTo(1));
            Assert.That(ExpeditionShrines.MatchesCampMob("Swift Venomous goblin", "goblin", out affixes), Is.True);
            Assert.That(affixes, Is.EqualTo(2));
            Assert.That(ExpeditionShrines.MatchesCampMob("Swift Venomous Dread goblin", "goblin", out affixes), Is.True);
            Assert.That(affixes, Is.EqualTo(2));
            Assert.That(ExpeditionShrines.MatchesCampMob("Dread goblin", "goblin", out affixes), Is.True);
            Assert.That(affixes, Is.EqualTo(0), "a dread monster is not an elite");
            Assert.That(ExpeditionShrines.MatchesCampMob("Stalwart Dread Essence Shredder", "essence shredder", out affixes), Is.True, "case-insensitive");
            Assert.That(affixes, Is.EqualTo(1));
            Assert.That(ExpeditionShrines.MatchesCampMob("Swift hobgoblin", "goblin", out _), Is.False, "another monster");
            Assert.That(ExpeditionShrines.MatchesCampMob("big goblin", "goblin", out _), Is.False, "only affixes are stripped");
            Assert.That(ExpeditionShrines.MatchesCampMob("swift goblin", "goblin", out _), Is.False, "affixes are capitalised");
            Assert.That(ExpeditionShrines.MatchesCampMob("Dread Dread goblin", "goblin", out _), Is.False, "one Dread prefix");
            Assert.That(ExpeditionShrines.MatchesCampMob("Swift Spectral warrior", "Spectral warrior", out affixes), Is.True, "a camp monster named like an affix");
            Assert.That(affixes, Is.EqualTo(1));
            Assert.That(ExpeditionShrines.MatchesCampMob(null, "goblin", out _), Is.False);
        });
    }

    [Test]
    public void AHardShrine_CountsItsPacksKills()
    {
        ShrineSpec shrine = new()
        {
            RegionId = 100,
            CampMob = "goblin",
            CampX = 30000,
            CampY = 30000,
            KillsNeeded = 5,
            Placed = true,
            HardPack = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(ExpeditionShrines.IsDormant(shrine), Is.True);
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Swift goblin", 100, 30500, 30000), Is.True, "an elite near the camp");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Swift Venomous Dread goblin", 100, 30000, 30000), Is.True, "two affixes and the dread prefix");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "goblin", 100, 30000, 30000), Is.True, "a plain respawn of the pack counts too");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Dread goblin", 100, 30000, 30000), Is.True, "a Dread respawn too");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Swift goblin", 100, 35000, 30000), Is.False, "far away and not of this pack");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Swift goblin", 100, 35000, 30000, true), Is.True, "of this pack, pulled far away");
            Assert.That(ExpeditionShrines.CountsFor(shrine, "Swift goblin", 101, 30000, 30000, true), Is.False, "another region");
        });
    }

    [Test]
    public void HardShrines_StandBesideTwoElitePacks_SpreadApart()
    {
        EliteCamp a = new() { MobName = "goblin", X = 0, Y = 0 };
        EliteCamp b = new() { MobName = "wolf", X = 1000, Y = 0 };
        EliteCamp c = new() { MobName = "bear", X = 20000, Y = 0 };

        Assert.That(ExpeditionShrines.HardShrinesPerAct, Is.EqualTo(2));
        Assert.That(ExpeditionShrines.ChooseHardCamps(new[] { a, b, c }, 2), Is.EqualTo(new[] { a, c }));
        Assert.That(ExpeditionShrines.ChooseHardCamps(new[] { a }, 2), Is.EqualTo(new[] { a }), "fewer packs, fewer shrines");
        Assert.That(ExpeditionShrines.ChooseHardCamps(new EliteCamp[0], 2), Is.Empty);
        Assert.That(ExpeditionShrines.ChooseHardCamps(null, 2), Is.Empty);
    }

    [Test]
    public void RulesLines_SayTheBoonRules()
    {
        string rules = string.Join("\n", ExpeditionHardMode.RulesLines());

        Assert.That(rules, Does.Contain("Phoenix Feather"));
        Assert.That(rules, Does.Contain("2 choices"));
        Assert.That(rules, Does.Contain("Juggernaut"));
        Assert.That(rules, Does.Contain("elite pack"));
    }
}
