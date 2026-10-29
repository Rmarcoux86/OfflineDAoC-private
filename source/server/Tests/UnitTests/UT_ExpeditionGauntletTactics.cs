using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS.Expedition;
using DOL.GS.Expedition.Gauntlet;
using DOL.GS.Styles;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>
/// The Gauntlet champions' decisions (owner 2026-10-27, ChampionTactics, ChampionStyles, ChampionSpells.PrefightBuffs):
/// the purge rule, the filler style, the immunity priorities, Ios's back-stun attempt, Drunkstains's shield stuns, the
/// low-health plans and the pre-fight readiness. Pure logic; no server is needed.
/// </summary>
[TestFixture]
public class UT_ExpeditionGauntletTactics
{
    private static Style TestStyle(string name, double growthRate, int toHit, Style.eOpening opening = Style.eOpening.Offensive, int openingValue = 0,
        Style.eAttackResultRequirement result = Style.eAttackResultRequirement.Any, bool stealth = false)
    {
        return new Style(new DbStyle
        {
            Name = name,
            GrowthRate = growthRate,
            BonusToHit = toHit,
            OpeningRequirementType = (int) opening,
            OpeningRequirementValue = openingValue,
            AttackResultRequirement = (int) result,
            StealthRequirement = stealth
        }, null);
    }

    /// <summary>Spells are equal by ID (Skill.Equals), so every test spell gets its own.</summary>
    private static int _nextSpellId = 90_000;

    private static Spell TestSpell(string name, eSpellType type, string target, int level, double castSeconds, int durationSeconds = 0, int concentration = 0,
        double value = 0)
    {
        return new Spell(new DbSpell
        {
            SpellID = ++_nextSpellId,
            Name = name,
            Type = type.ToString(),
            Target = target,
            CastTime = castSeconds,
            Duration = durationSeconds,
            Concentration = concentration,
            Value = value
        }, level);
    }

    [Test]
    public void Purge_MezAndOtherStuns_AtOnce_NumbOnlyOnItsCoin()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChampionTactics.PurgeWanted(true, false, false, false), Is.True, "mez");
            Assert.That(ChampionTactics.PurgeWanted(false, true, false, false), Is.True, "another stun");
            Assert.That(ChampionTactics.PurgeWanted(false, true, true, true), Is.True, "Numb, coin says purge");
            Assert.That(ChampionTactics.PurgeWanted(false, true, true, false), Is.False, "Numb, coin says wait");
            Assert.That(ChampionTactics.PurgeWanted(false, false, false, true), Is.False, "nothing to purge");
        });
    }

    [Test]
    public void Filler_IsTheBestAnytimeStyle_ByGrowthRateThenToHit()
    {
        Style[] styles =
        [
            TestStyle("Low", 0.5, 20),
            TestStyle("High", 0.9, 0),
            TestStyle("High, more to-hit", 0.9, 15),
            TestStyle("Reactive", 1.5, 20, Style.eOpening.Defensive, 0, Style.eAttackResultRequirement.Parry),
            TestStyle("Follow-up", 1.5, 20, Style.eOpening.Offensive, 42),
            TestStyle("Positional", 1.5, 20, Style.eOpening.Positional, 0),
            TestStyle("After a miss", 1.5, 20, Style.eOpening.Offensive, 0, Style.eAttackResultRequirement.Miss),
            TestStyle("Stealth", 1.5, 20, stealth: true)
        ];

        Assert.Multiple(() =>
        {
            Assert.That(ChampionStyles.BestFiller(styles, null, _ => true)?.Name, Is.EqualTo("High, more to-hit"));
            Assert.That(ChampionStyles.BestFiller(styles, null, style => style.Name != "High, more to-hit")?.Name, Is.EqualTo("High"));
            Assert.That(ChampionStyles.BestFiller(styles, "Low", _ => true)?.Name, Is.EqualTo("Low"), "a named filler");
            Assert.That(ChampionStyles.BestFiller(styles, "Low", style => style.Name != "Low"), Is.Null, "the named filler or nothing");
            Assert.That(ChampionStyles.BestFiller(styles, null, _ => false), Is.Null);
        });
    }

    [Test]
    public void Priority_SwitchesToTheImmunityChain_OnlyWhereThereIsOne()
    {
        ChampionSignature preesh = GauntletChampion.SignatureFor("Preesh");
        ChampionSignature ios = GauntletChampion.SignatureFor("Ios");
        ChampionSignature drunkstains = GauntletChampion.SignatureFor("Drunkstains");

        Assert.Multiple(() =>
        {
            Assert.That(ChampionTactics.BasePriority(preesh, false), Is.EqualTo(new[] { "Devastating Blow", "Stunning Blow" }));
            Assert.That(ChampionTactics.BasePriority(preesh, true), Is.EqualTo(new[] { "Twin Star", "Meteor Shower", "Tornado" }));
            Assert.That(ChampionTactics.BasePriority(ios, false), Is.Empty);
            Assert.That(ChampionTactics.BasePriority(ios, true), Is.EqualTo(new[] { "Conflagration", "Flaming Scythe", "Foxfire", "Arboreal Fire" }));
            Assert.That(ChampionTactics.BasePriority(drunkstains, true), Is.EqualTo(new[] { "Defender's Rage" }));
            Assert.That(ChampionTactics.BasePriority(null, true), Is.Empty);
        });
    }

    [Test]
    public void BackStun_KeepsGoingUntilItLands_AndIsDroppedOnImmunity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChampionTactics.NextBackStunMove(true, false, false, false, true, false), Is.EqualTo(BackStunMove.Landed));
            Assert.That(ChampionTactics.NextBackStunMove(false, true, false, false, true, false), Is.EqualTo(BackStunMove.Landed), "stunned");

            // A miss (not landed, not stunned) keeps the attempt going.
            Assert.That(ChampionTactics.NextBackStunMove(false, false, false, false, true, false), Is.EqualTo(BackStunMove.RunThrough), "faces her");
            Assert.That(ChampionTactics.NextBackStunMove(false, false, false, false, false, false), Is.EqualTo(BackStunMove.MoveBehind), "turned away");
            Assert.That(ChampionTactics.NextBackStunMove(false, false, false, false, false, true), Is.EqualTo(BackStunMove.MoveBehind), "already behind");
            Assert.That(ChampionTactics.NextBackStunMove(false, false, false, false, true, true), Is.EqualTo(BackStunMove.MoveBehind), "behind, turning");

            Assert.That(ChampionTactics.NextBackStunMove(false, false, true, false, true, false), Is.EqualTo(BackStunMove.Drop), "immune");
            Assert.That(ChampionTactics.NextBackStunMove(false, false, false, true, true, false), Is.EqualTo(BackStunMove.Drop), "mezzed");

            Assert.That(ChampionTactics.BackStunSpeedFactor(true), Is.EqualTo(4.0));
            Assert.That(ChampionTactics.BackStunSpeedFactor(false), Is.EqualTo(1.0));
        });
    }

    [Test]
    public void BackStun_AttemptSequence_EndsOnlyWhenItLandsOrOnImmunity()
    {
        // Miss, miss (the challenger turns between), land: the attempt survives both misses and keeps its boost.
        (bool Landed, bool Immune, bool Faces, bool Behind)[] swings =
        [
            (false, false, true, false),
            (false, false, false, false),
            (false, false, false, true),
            (true, false, false, true)
        ];
        int moves = 0;

        foreach ((bool landed, bool immune, bool faces, bool behind) in swings)
        {
            BackStunMove move = ChampionTactics.NextBackStunMove(landed, landed, immune, false, faces, behind);
            moves++;

            if (move is BackStunMove.Landed or BackStunMove.Drop)
            {
                Assert.That(move, Is.EqualTo(BackStunMove.Landed));

                break;
            }

            Assert.That(ChampionTactics.BackStunSpeedFactor(true), Is.GreaterThan(1.3), "boosted above sprint while attempting");
        }

        Assert.That(moves, Is.EqualTo(4));
    }

    [Test]
    public void ShieldStun_SlamFirst_ThenSlamOrNumb_NeverOnAStunnedOrImmuneChallenger()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChampionTactics.ShieldStun(false, false, false, true), Is.EqualTo("Slam"), "opener");
            Assert.That(ChampionTactics.ShieldStun(false, false, true, true), Is.EqualTo("Numb"));
            Assert.That(ChampionTactics.ShieldStun(false, false, true, false), Is.EqualTo("Slam"));
            Assert.That(ChampionTactics.ShieldStun(true, false, true, true), Is.Null, "immune");
            Assert.That(ChampionTactics.ShieldStun(false, true, true, true), Is.Null, "stunned");
        });

        // Even odds over many coins.
        Random random = new(11);
        int numbs = Enumerable.Range(0, 2_000).Count(_ => ChampionTactics.ShieldStun(false, false, true, random.Next(2) == 0) == "Numb");
        Assert.That(numbs, Is.InRange(900, 1_100));
    }

    [Test]
    public void LowHealth_Plans()
    {
        Assert.Multiple(() =>
        {
            // Ios: once, at 40%.
            Assert.That(ChampionTactics.IosLowHealth(41, false, true), Is.EqualTo(LowHealthAction.None));
            Assert.That(ChampionTactics.IosLowHealth(40, false, true), Is.EqualTo(LowHealthAction.Kite));
            Assert.That(ChampionTactics.IosLowHealth(30, false, false), Is.EqualTo(LowHealthAction.IgnorePain));
            Assert.That(ChampionTactics.IosLowHealth(20, true, true), Is.EqualTo(LowHealthAction.None), "once");

            // Gwyraan: the root escape whenever possible, and no instant heal.
            Assert.That(ChampionTactics.GwyraanLowHealth(50, true, false), Is.EqualTo(LowHealthAction.None));
            Assert.That(ChampionTactics.GwyraanLowHealth(49, true, false), Is.EqualTo(LowHealthAction.RootEscape));
            Assert.That(ChampionTactics.GwyraanLowHealth(49, true, true), Is.EqualTo(LowHealthAction.None), "root immune");
            Assert.That(ChampionTactics.GwyraanLowHealth(49, false, false), Is.EqualTo(LowHealthAction.None), "root on cooldown");

            // Toothmouth: a mez retreat whenever a mez is ready, else Speed of Sound.
            Assert.That(ChampionTactics.ToothmouthLowHealth(50, true, false, false, true), Is.EqualTo(LowHealthAction.None));
            Assert.That(ChampionTactics.ToothmouthLowHealth(49, true, false, false, true), Is.EqualTo(LowHealthAction.MezRetreat));
            Assert.That(ChampionTactics.ToothmouthLowHealth(49, true, true, false, true), Is.EqualTo(LowHealthAction.SpeedOfSoundRetreat), "mez immune");
            Assert.That(ChampionTactics.ToothmouthLowHealth(49, false, false, false, true), Is.EqualTo(LowHealthAction.SpeedOfSoundRetreat));
            Assert.That(ChampionTactics.ToothmouthLowHealth(49, false, false, false, false), Is.EqualTo(LowHealthAction.None));
        });
    }

    [Test]
    public void Prefight_WaitsForEveryBuff_AfterTheMinimum_WithADeadline()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChampionTactics.PrefightDone(9_999, 10_000, 90_000, false, false), Is.False, "the 10 s minimum");
            Assert.That(ChampionTactics.PrefightDone(10_000, 10_000, 90_000, false, false), Is.True);
            Assert.That(ChampionTactics.PrefightDone(30_000, 10_000, 90_000, true, false), Is.False, "still casting");
            Assert.That(ChampionTactics.PrefightDone(30_000, 10_000, 90_000, false, true), Is.False, "a buff (or the pet) missing");
            Assert.That(ChampionTactics.PrefightDone(90_000, 10_000, 90_000, true, true), Is.True, "the deadline");
            Assert.That(ChampionTactics.PrefightDone(900_000, 10_000, 0, false, true), Is.False, "no deadline");
        });
    }

    [Test]
    public void PrefightBuffs_SpecBuffsFirst_LastingOnly()
    {
        Spell[] spells =
        [
            TestSpell("Greater Skin of the Redwood", eSpellType.BaseArmorFactorBuff, "Realm", 26, 3, 0, 9),
            TestSpell("Strength of the Wood", eSpellType.StrengthConstitutionBuff, "Realm", 25, 3, 0, 17),
            TestSpell("Squall's Dance", eSpellType.DexterityQuicknessBuff, "Realm", 19, 3, 0, 13),
            TestSpell("Hunter's Offense", eSpellType.CombatSpeedBuff, "Realm", 20, 3.5, 0, 7),
            TestSpell("Earth Affinity", eSpellType.MatterResistBuff, "Group", 26, 3, 1200),
            TestSpell("Hooks of the Wild", eSpellType.DamageAdd, "Self", 46, 3, 1200),
            TestSpell("Speed of Kudzu", eSpellType.CombatSpeedBuff, "Self", 3, 0, 30),
            TestSpell("Degeneration", eSpellType.DamageOverTime, "Enemy", 41, 3, 24),
            TestSpell("Major Restoration", eSpellType.Heal, "Realm", 20, 3.2),
            TestSpell("Defedation", eSpellType.DamageOverTime, "Enemy", 48, 3, 24),
            TestSpell("Too high", eSpellType.StrengthBuff, "Realm", 51, 3, 0, 5)
        ];

        Assert.That(ChampionSpells.PrefightBuffs(spells, 50).Select(spell => spell.Name), Is.EqualTo(new[]
        {
            "Strength of the Wood", "Squall's Dance", "Hunter's Offense", "Hooks of the Wild", "Earth Affinity", "Greater Skin of the Redwood"
        }));
    }

    [Test]
    public void GwyraanAndIosSpells_ArePickedByNameOrBest()
    {
        Spell[] spells =
        [
            TestSpell("Sprouting Undergrowth", eSpellType.SpeedDecrease, "Enemy", 28, 0, 46, 0, 99),
            TestSpell("Bursting Undergrowth", eSpellType.SpeedDecrease, "Enemy", 36, 0, 56, 0, 99),
            TestSpell("Field of Tangleweed", eSpellType.SpeedDecrease, "Enemy", 39, 2.5, 60, 0, 99),
            TestSpell("Veil Path", eSpellType.SpeedDecrease, "Enemy", 32, 0, 49, 0, 35),
            TestSpell("Major Restoration", eSpellType.Heal, "Realm", 20, 3.2, 0, 0, 190),
            TestSpell("Greater Regrowth", eSpellType.Heal, "Realm", 19, 3.4, 0, 0, 263),
            TestSpell("Bith's Benison", eSpellType.Heal, "Group", 23, 0, 0, 0, -50)
        ];

        Assert.Multiple(() =>
        {
            Assert.That(ChampionSpells.BestInstantRoot(spells, 50)?.Name, Is.EqualTo("Bursting Undergrowth"));
            Assert.That(ChampionSpells.Named(spells, "Major Restoration")?.Name, Is.EqualTo("Major Restoration"));
            Assert.That(ChampionSpells.Named(spells, "Cycle of Death"), Is.Null);
            Assert.That(ChampionSpells.BestSelfHeal(spells, 50)?.Name, Is.EqualTo("Greater Regrowth"), "the best by value, which is why his is named");
            Assert.That(ChampionSpells.Best(spells, eSpellType.SpeedDecrease, 34, true)?.Name, Is.EqualTo("Veil Path"));
        });
    }

    [Test]
    public void Preesh_UsesTripleWieldAfterHisParryStun_AndFlurry()
    {
        ChampionSignature preesh = GauntletChampion.SignatureFor("Preesh");

        Assert.Multiple(() =>
        {
            Assert.That(preesh.BladeWork, Is.True);
            Assert.That(preesh.Priority, Does.Contain(GauntletChampion.TripleWieldTriggerStyle));
            Assert.That(GauntletChampion.SignatureFor("Ios").BladeWork, Is.False);
            Assert.That(preesh.Filler, Is.EqualTo("Hurricane"));
            Assert.That(preesh.FillerFollowup, Is.EqualTo("Solar Flare"));
            Assert.That(GauntletChampion.SignatureFor("Ios").BackStunAnywhere, Is.True, "her back stun lands from any side");
            Assert.That(preesh.BackStunAnywhere, Is.False);
        });
    }

    [Test]
    public void EveryChampion_HasItsEliteAffix()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletChampions.Get("Ios").Affix, Is.EqualTo(ExpeditionElites.Swift));
            Assert.That(GauntletChampions.Get("Toothmouth").Affix, Is.EqualTo(ExpeditionElites.Stalwart));
            Assert.That(GauntletChampions.Get("Gwyraan").Affix, Is.EqualTo(ExpeditionElites.Venomous));
            Assert.That(GauntletChampions.Get("Drunkstains").Affix, Is.EqualTo(ExpeditionElites.Armoured));
            Assert.That(GauntletChampions.Get("Preesh").Affix, Is.EqualTo(ExpeditionElites.Frenzied));
            Assert.That(ExpeditionElites.EliteName(ExpeditionElites.Swift, "Ios Raven Ardent"), Is.EqualTo("Swift Ios Raven Ardent"));
        });
    }

    [Test]
    public void Drunkstains_ShieldBetweenSwings_PolearmWhenTheSwingIsDue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChampionTactics.ShieldOrPolearm(false, 3000, 600), Is.EqualTo(eActiveWeaponSlot.Standard), "swing far off");
            Assert.That(ChampionTactics.ShieldOrPolearm(false, 400, 600), Is.EqualTo(eActiveWeaponSlot.TwoHanded), "swing due");
            Assert.That(ChampionTactics.ShieldOrPolearm(true, 400, 600), Is.EqualTo(eActiveWeaponSlot.Standard), "shield stun first");
        });
    }
}
