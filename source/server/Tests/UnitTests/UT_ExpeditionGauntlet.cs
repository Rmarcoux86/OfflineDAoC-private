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
    public void ForRound_IsTheFixedOrder_ForEverySeed()
    {
        foreach (int seed in Seeds)
        {
            List<string> order = Enumerable.Range(1, 10).Select(round => GauntletChampions.ForRound(seed, round).Name).ToList();

            Assert.That(order, Is.EqualTo(new[] { "Gwyraan", "Toothmouth", "Ios", "Drunkstains", "Preesh", "Gwyraan", "Toothmouth", "Ios", "Drunkstains", "Preesh" }),
                $"seed {seed}");
        }
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
    public void ForRound_PreeshIsTheFinalChampionOfEveryCycle()
    {
        foreach (int seed in Seeds)
        {
            for (int cycle = 0; cycle < Cycles; cycle++)
            {
                Assert.That(GauntletChampions.ForRound(seed, cycle * 5 + 5).Name, Is.EqualTo("Preesh"), $"seed {seed} cycle {cycle}");

                for (int place = 1; place <= 4; place++)
                {
                    Assert.That(GauntletChampions.ForRound(seed, cycle * 5 + place).Name, Is.Not.EqualTo("Preesh"), $"seed {seed} cycle {cycle} place {place}");
                }
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
            Assert.That(GauntletEscalation.For(1), Is.EqualTo(new RoundTier(2, true, false, false, 0, 0)));
            Assert.That(GauntletEscalation.For(4), Is.EqualTo(new RoundTier(8, true, false, false, 0, 0)));
            Assert.That(GauntletEscalation.For(5, GauntletChampions.FinalChampion), Is.EqualTo(new RoundTier(10, true, false, false, 0, 0)));
            Assert.That(GauntletEscalation.For(9), Is.EqualTo(new RoundTier(14, true, false, false, 0, 0)));
            Assert.That(GauntletEscalation.For(10, GauntletChampions.FinalChampion), Is.EqualTo(new RoundTier(10, true, true, true, 0, 0)));
            Assert.That(GauntletEscalation.For(14), Is.EqualTo(new RoundTier(14, true, true, true, 0, 0)));
            Assert.That(GauntletEscalation.For(5), Is.EqualTo(new RoundTier(10, true, false, false, 0, 0)), "round 5 by another champion");
            Assert.That(GauntletEscalation.For(1, GauntletChampions.FinalChampion).RealmRank, Is.EqualTo(10), "Preesh is always RR10");
            Assert.That(new[] { 1, 2, 3, 4, 5 }.Select(GauntletEscalation.WeakenedPercent), Is.EqualTo(new[] { 20, 13, 7, 0, 0 }), "weaker early rounds");
            Assert.That(GauntletEscalation.For(15), Is.EqualTo(new RoundTier(14, true, true, true, 1, 0)));
            Assert.That(GauntletEscalation.For(19), Is.EqualTo(new RoundTier(14, true, true, true, 1, 0)));
            Assert.That(GauntletEscalation.For(20), Is.EqualTo(new RoundTier(14, true, true, true, 2, 0)));
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
            Assert.That(GauntletEscalation.For(25).RealmRank, Is.EqualTo(GauntletEscalation.MaxRealmRank));
            Assert.That(GauntletEscalation.For(25).Helpers, Is.EqualTo(2));
        });
    }

    [Test]
    public void Rules_AreSane()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletRules.DeathEndsRun, Is.False, "owner decision: no permadeath in the Gauntlet for now");
            Assert.That(GauntletRules.RestSeconds, Is.EqualTo(15));
            Assert.That(GauntletRules.LeashRadius, Is.GreaterThan(0));
            Assert.That(GauntletRules.ChampionBuffSeconds, Is.GreaterThan(0));
            Assert.That(GauntletRules.AutoReleaseSeconds, Is.GreaterThan(0));
        });
    }

    [Test]
    public void TestKit_TemplatePieces_HoldEveryCapAndTheToa()
    {
        eProperty[] toa = { eProperty.MeleeDamage, eProperty.StyleDamage, eProperty.MeleeSpeed };
        var melee = DOL.GS.Expedition.Gauntlet.GauntletTestKit.NecklaceLines(eStat.UNDEFINED, toa);
        var caster = DOL.GS.Expedition.Gauntlet.GauntletTestKit.NecklaceLines(eStat.INT, toa);
        var belt = DOL.GS.Expedition.Gauntlet.GauntletTestKit.BeltLines();

        Assert.Multiple(() =>
        {
            Assert.That(melee.Count, Is.EqualTo(8), "four stats, hits, three ToA");
            Assert.That(caster.Count, Is.EqualTo(9), "and the casting stat");
            Assert.That(caster, Does.Contain((eProperty.Intelligence, 75)));
            Assert.That(melee, Does.Contain((eProperty.MaxHealth, 200)));
            Assert.That(melee, Does.Contain((eProperty.MeleeSpeed, 10)));
            Assert.That(belt.Count, Is.EqualTo(9));
            Assert.That(belt.All(line => line.Value == 26), Is.True);
            Assert.That(caster.Count, Is.LessThanOrEqualTo(10), "an item holds ten lines");
            Assert.That(ExpeditionUniques.FindByName(DOL.GS.Expedition.Gauntlet.GauntletTestKit.UniqueWeaponName)?.Slot, Is.EqualTo(eUniqueSlot.Weapon));
        });
    }

    [Test]
    public void PairFor_IsDeterministic_TwoDifferentChampions_NeverPreesh()
    {
        foreach (int seed in Seeds)
        {
            List<string> pair = GauntletChampions.PairFor(seed).Select(champion => champion.Name).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(pair, Has.Count.EqualTo(2), $"seed {seed}");
                Assert.That(pair, Is.Unique, $"seed {seed}");
                Assert.That(pair, Does.Not.Contain(GauntletChampions.FinalChampion), $"seed {seed}");
                Assert.That(GauntletChampions.PairFor(seed).Select(champion => champion.Name), Is.EqualTo(pair), $"seed {seed}: the same pair on a retry");
            });
        }
    }

    [Test]
    public void PairFor_EveryPairCanComeUp()
    {
        HashSet<string> pairs = new();

        for (int seed = 0; seed < 200; seed++)
        {
            pairs.Add(string.Join("+", GauntletChampions.PairFor(seed).Select(champion => champion.Name).OrderBy(name => name)));
        }

        Assert.That(pairs, Has.Count.EqualTo(6), "four champions make six pairs");
    }

    [Test]
    public void RoundsFor_HardWinnerHasSix_OthersFive()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletRules.Rounds, Is.EqualTo(5));
            Assert.That(GauntletRules.HardRounds, Is.EqualTo(6));
            Assert.That(GauntletRules.RoundsFor(false), Is.EqualTo(5));
            Assert.That(GauntletRules.RoundsFor(true), Is.EqualTo(6));
            Assert.That(GauntletRules.RoundsFor(new GauntletState()), Is.EqualTo(5));
            Assert.That(GauntletRules.RoundsFor(new GauntletState { Hard = true }), Is.EqualTo(6));
            Assert.That(GauntletRules.RoundsFor((GauntletState) null), Is.EqualTo(5));
            Assert.That(GauntletRules.IsPairRound(new GauntletState { Hard = true }, 6), Is.True);
            Assert.That(GauntletRules.IsPairRound(new GauntletState { Hard = true }, 5), Is.False);
            Assert.That(GauntletRules.IsPairRound(new GauntletState(), 6), Is.False);
        });
    }

    [Test]
    public void Escalation_PairRound_IsRealmRankTen()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletEscalation.ForPair(6), Is.EqualTo(new RoundTier(GauntletEscalation.FinalChampionRealmRank, true, false, false, 0, 0)));
            Assert.That(GauntletEscalation.ForPair(6).RealmRank, Is.EqualTo(10));
            Assert.That(GauntletEscalation.WeakenedPercent(6), Is.EqualTo(0));
        });
    }

    [Test]
    public void PairSpawn_IsEitherSideOfTheGate_AcrossTheLineToThePlayer()
    {
        (int lx, int ly) = GauntletArena.PairSpawn(-1);
        (int rx, int ry) = GauntletArena.PairSpawn(1);
        double towardX = GauntletArena.PlayerX - GauntletArena.GateX;
        double towardY = GauntletArena.PlayerY - GauntletArena.GateY;
        double length = System.Math.Sqrt(towardX * towardX + towardY * towardY);

        Assert.Multiple(() =>
        {
            Assert.That(System.Math.Sqrt(System.Math.Pow(lx - GauntletArena.GateX, 2) + System.Math.Pow(ly - GauntletArena.GateY, 2)), Is.EqualTo(GauntletArena.PairOffset).Within(1));
            Assert.That(System.Math.Sqrt(System.Math.Pow(rx - GauntletArena.GateX, 2) + System.Math.Pow(ry - GauntletArena.GateY, 2)), Is.EqualTo(GauntletArena.PairOffset).Within(1));
            Assert.That(lx + rx, Is.EqualTo(2 * GauntletArena.GateX).Within(1), "mirrored about the gate");
            Assert.That(ly + ry, Is.EqualTo(2 * GauntletArena.GateY).Within(1), "mirrored about the gate");
            Assert.That(((rx - lx) * towardX + (ry - ly) * towardY) / length, Is.EqualTo(0).Within(2), "perpendicular to the gate-to-player line");
        });
    }

    [Test]
    public void Champions_RealmRanks_AndAFullTemplate()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Enumerable.Range(1, 5).Select(round => GauntletEscalation.For(round, round == 5 ? GauntletChampions.FinalChampion : null).RealmRank), Is.EqualTo(new[] { 2, 4, 6, 8, 10 }));
            Assert.That(DOL.GS.Expedition.Gauntlet.GauntletChampion.TemplateCaps(50), Is.EqualTo((75, 26, 200)));
        });
    }
}
