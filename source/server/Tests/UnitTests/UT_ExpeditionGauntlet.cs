using System.Collections.Generic;
using System.Linq;
using DOL.GS.Expedition;
using DOL.GS.Expedition.Gauntlet;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>The Gauntlet's pure data: the champion roster and round order, the escalation table and the rules. No server is needed.</summary>
[TestFixture]
public class UT_ExpeditionGauntlet
{
    private static readonly int[] Seeds = [0, 1, 2, 7, 42, 1234, -99, 987654321, int.MaxValue, int.MinValue];

    private const int Cycles = 20;

    [Test]
    public void Roster_IsTheFiveChampions_WithTheirClassesAndBodies()
    {
        Assert.That(GauntletChampions.All.Select(champion => champion.Name),
            Is.EquivalentTo(new[] { "Ios", "Drunkstains", "Toothmouth", "Gwyraan", "Preesh" }));
        Assert.That(GauntletChampions.All.Select(champion => champion.Signature), Is.Unique);

        ChampionDefinition ios = GauntletChampions.Get("Ios");
        ChampionDefinition drunkstains = GauntletChampions.Get("Drunkstains");
        ChampionDefinition preesh = GauntletChampions.Get("Preesh");

        Assert.Multiple(() =>
        {
            Assert.That(ios.ClassId, Is.EqualTo((int) eCharacterClass.Valewalker));
            Assert.That(ios.RaceId, Is.EqualTo((int) eRace.Celt));
            Assert.That(ios.Gender, Is.EqualTo((int) eGender.Female));
            Assert.That(ios.Signature, Is.EqualTo("burst"));
            Assert.That(drunkstains.ClassId, Is.EqualTo((int) eCharacterClass.Armsman));
            Assert.That(drunkstains.RaceId, Is.EqualTo((int) eRace.HalfOgre));
            Assert.That(preesh.ClassId, Is.EqualTo((int) eCharacterClass.Blademaster));
            Assert.That(preesh.Signature, Is.EqualTo("vanish"));
        });
    }

    [Test]
    public void Get_IsCaseInsensitive_AndNullForUnknown()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletChampions.Get("ios")?.Name, Is.EqualTo("Ios"));
            Assert.That(GauntletChampions.Get("PREESH")?.Name, Is.EqualTo("Preesh"));
            Assert.That(GauntletChampions.Get("gWyRaAn")?.Name, Is.EqualTo("Gwyraan"));
            Assert.That(GauntletChampions.Get("Scob Mahob"), Is.Null);
            Assert.That(GauntletChampions.Get(""), Is.Null);
            Assert.That(GauntletChampions.Get(null), Is.Null);
        });
    }

    [Test]
    public void ForRound_IsDeterministicPerSeedAndRound()
    {
        bool seedChangesOrder = false;
        List<string> firstSeedOrder = Enumerable.Range(1, 5).Select(round => GauntletChampions.ForRound(Seeds[0], round).Name).ToList();

        foreach (int seed in Seeds)
        {
            for (int round = 1; round <= Cycles * 5; round++)
            {
                Assert.That(GauntletChampions.ForRound(seed, round), Is.SameAs(GauntletChampions.ForRound(seed, round)), $"seed {seed} round {round}");
            }

            List<string> order = Enumerable.Range(1, 5).Select(round => GauntletChampions.ForRound(seed, round).Name).ToList();

            if (!order.SequenceEqual(firstSeedOrder))
            {
                seedChangesOrder = true;
            }
        }

        Assert.That(seedChangesOrder, Is.True, "the seed should take part in the order");
    }

    [Test]
    public void ForRound_FacesAllFiveBeforeAnyRepeat_InEveryCycle()
    {
        foreach (int seed in Seeds)
        {
            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                List<string> names = Enumerable.Range(cycle * 5 + 1, 5).Select(round => GauntletChampions.ForRound(seed, round).Name).ToList();

                Assert.That(names, Is.Unique, $"seed {seed} cycle {cycle}");
                Assert.That(names, Is.EquivalentTo(GauntletChampions.All.Select(champion => champion.Name)), $"seed {seed} cycle {cycle}");
            }
        }
    }

    [Test]
    public void ForRound_NeverRepeatsAcrossACycleBoundary()
    {
        foreach (int seed in Seeds)
        {
            for (int round = 2; round <= Cycles * 5; round++)
            {
                Assert.That(GauntletChampions.ForRound(seed, round), Is.Not.SameAs(GauntletChampions.ForRound(seed, round - 1)),
                    $"seed {seed} rounds {round - 1} and {round}");
            }
        }
    }

    [Test]
    public void ForRound_BelowOne_IsRoundOne()
    {
        Assert.That(GauntletChampions.ForRound(42, 0), Is.SameAs(GauntletChampions.ForRound(42, 1)));
        Assert.That(GauntletChampions.ForRound(42, -5), Is.SameAs(GauntletChampions.ForRound(42, 1)));
    }

    [Test]
    public void Escalation_BandBoundaries()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletEscalation.For(1), Is.EqualTo(new RoundTier(1, eLootRarity.Magic, false, false, false, 0, 0)));
            Assert.That(GauntletEscalation.For(4), Is.EqualTo(new RoundTier(1, eLootRarity.Magic, false, false, false, 0, 0)));
            Assert.That(GauntletEscalation.For(5), Is.EqualTo(new RoundTier(3, eLootRarity.Rare, true, false, false, 0, 0)));
            Assert.That(GauntletEscalation.For(9), Is.EqualTo(new RoundTier(3, eLootRarity.Rare, true, false, false, 0, 0)));
            Assert.That(GauntletEscalation.For(10), Is.EqualTo(new RoundTier(5, eLootRarity.Epic, true, true, true, 0, 0)));
            Assert.That(GauntletEscalation.For(14), Is.EqualTo(new RoundTier(5, eLootRarity.Epic, true, true, true, 0, 0)));
            Assert.That(GauntletEscalation.For(15), Is.EqualTo(new RoundTier(7, eLootRarity.Unique, true, true, true, 1, 0)));
            Assert.That(GauntletEscalation.For(19), Is.EqualTo(new RoundTier(7, eLootRarity.Unique, true, true, true, 1, 0)));
            Assert.That(GauntletEscalation.For(20), Is.EqualTo(new RoundTier(10, eLootRarity.Unique, true, true, true, 2, 0)));
        });
    }

    [Test]
    public void Escalation_RoundZeroOrBelow_IsRoundOne()
    {
        Assert.That(GauntletEscalation.For(0), Is.EqualTo(GauntletEscalation.For(1)));
        Assert.That(GauntletEscalation.For(-3), Is.EqualTo(GauntletEscalation.For(1)));
    }

    [Test]
    public void Escalation_StatBonus_GrowsFivePercentPerRoundOverTwenty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletEscalation.For(19).StatBonusPercent, Is.EqualTo(0));
            Assert.That(GauntletEscalation.For(20).StatBonusPercent, Is.EqualTo(0));
            Assert.That(GauntletEscalation.For(21).StatBonusPercent, Is.EqualTo(5));
            Assert.That(GauntletEscalation.For(25).StatBonusPercent, Is.EqualTo(25));
            Assert.That(GauntletEscalation.For(25).RealmRank, Is.EqualTo(10));
            Assert.That(GauntletEscalation.For(25).Helpers, Is.EqualTo(2));
        });
    }

    [Test]
    public void Rules_AreSane()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletRules.DeathEndsRun, Is.False, "owner decision: no permadeath in the Gauntlet for now");
            Assert.That(GauntletRules.RestSeconds, Is.EqualTo(90));
            Assert.That(GauntletRules.LeashRadius, Is.GreaterThan(0));
            Assert.That(GauntletRules.ChampionBuffSeconds, Is.GreaterThan(0));
            Assert.That(GauntletRules.AutoReleaseSeconds, Is.GreaterThan(0));
        });
    }
}
