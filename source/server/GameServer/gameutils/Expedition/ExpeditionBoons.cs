using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.PacketHandler;

namespace DOL.GS.Expedition;

/// <summary>One boon of the catalog. Loot boons change drops; the others are applied to the player (<see cref="BoonEffects"/>).</summary>
public sealed record BoonDefinition(string Id, string Name, string Effect, bool IsLoot);

/// <summary>
/// Run boons: build-defining picks offered one-of-three at each act break. The offer is drawn from the run seed and
/// the finished act and never repeats a boon already taken; every boon left has the same chance. The choice waits
/// until the player types /run boon &lt;1-3&gt;; boons last the whole run and are applied again at each login.
/// The loot math and the offers are pure functions of boon ids so they can be tested without a server.
/// </summary>
public static class ExpeditionBoons
{
    public const int OfferSize = 3;

    /// <summary>How many times a run may reroll a boon offer (/run reroll).</summary>
    public const int RerollsPerRun = 2;

    public const string Fortune = "fortune";
    public const string Greed = "greed";
    public const string Prospector = "prospector";
    public const string Ironhide = "ironhide";
    public const string Warding = "warding";
    public const string Bloodlust = "bloodlust";
    public const string HuntersEye = "hunters_eye";
    public const string Thornmail = "thornmail";
    public const string Vampiric = "vampiric";
    public const string Rejuvenation = "rejuvenation";
    public const string ArcaneFury = "arcane_fury";
    public const string Executioner = "executioner";
    public const string ChainLightning = "chain_lightning";
    public const string Rupture = "rupture";
    public const string PhoenixFeather = "phoenix";
    public const string SoulHarvest = "soul_harvest";
    public const string Desperation = "desperation";
    public const string GlassCannon = "glass_cannon";
    public const string Unshakable = "unshakable";
    public const string Frostbite = "frostbite";
    public const string Siphon = "siphon";
    public const string BountyHunter = "bounty_hunter";
    public const string MarkedPrey = "marked_prey";
    public const string Evasion = "evasion";
    public const string TreasureSense = "treasure_sense";
    public const string Swiftfoot = "swiftfoot";
    public const string Scavenger = "scavenger";
    public const string Glory = "glory";
    public const string BlessedArms = "blessed_arms";
    public const string SecondWind = "second_wind";
    public const string WardedHide = "warded_hide";
    public const string SecondSkin = "second_skin";
    public const string HuntersInstinct = "hunters_instinct";
    public const string Berserker = "berserker";
    public const string BeastBond = "beast_bond";
    public const string Overreach = "overreach";
    public const string Deadeye = "deadeye";
    public const string Sharpshooter = "sharpshooter";
    public const string Skirmisher = "skirmisher";

    /// <summary>Realm points added by Glory, in percent.</summary>
    public const int GloryRealmPointPercent = 50;

    /// <summary>The catalog, in a fixed order: offers index into it, so reordering changes every seed's offers.</summary>
    public static readonly IReadOnlyList<BoonDefinition> Catalog = new List<BoonDefinition>
    {
        new(Fortune, "Fortune", "Every Expedition drop is one rarity better, up to Epic.", true),
        new(Ironhide, "Ironhide", "+10% maximum health and 10% less physical damage taken.", false),
        new(Bloodlust, "Bloodlust", "+15% melee critical hit chance (the 50% cap still applies).", false),
        new(Thornmail, "Thornmail", "Monsters that hit you in melee take heat damage (3 + your level) on every hit.", false),
        new(Vampiric, "Vampiric", "Each hit you land (melee or spell) has a 25% chance to heal you for half its damage.", false),
        new(Rejuvenation, "Rejuvenation", "You regenerate 1% of your maximum health every 6 seconds, even in combat.", false),
        new(ArcaneFury, "Arcane Fury", "+15% spell critical hit chance.", false),
        new(Executioner, "Executioner", "Any hit you land on a monster below 15% health kills it outright; an act boss only below 10%, and only to a direct hit.", false),
        new(PhoenixFeather, "Phoenix Feather", "Once per act, when your health falls below 25% you are instantly healed to 60%.", false),
        new(SoulHarvest, "Soul Harvest", "Each monster you kill restores 8% of your health and 8% of your power.", false),
        new(Desperation, "Desperation", "While below half health you deal 30% more melee and spell damage.", false),
        new(GlassCannon, "Glass Cannon", "+25% melee and spell damage, but 15% less maximum health.", false),
        new(Siphon, "Siphon", "Every hit you land restores 3% of your maximum power.", false),
        new(BountyHunter, "Bounty Hunter", "Elite pack monsters and act bosses drop one extra item.", true),
        new(Glory, "Glory", $"+{GloryRealmPointPercent}% realm points for the rest of the run.", false),
        new(BlessedArms, "Blessed Arms", "A lesser version of every buff for the rest of the run: strength, constitution, dexterity, the two stat pairs, acuity, both armor buffs and haste.", false),
        new(MarkedPrey, "Marked Prey", "Elite pack monsters and act bosses take 20% more damage from you.", false),
        new(Evasion, "Evasion", "Melee attacks on you have a 10% chance to miss outright (you evade them).", false),
        new(TreasureSense, "Treasure Sense", "Unique drops are twice as likely.", true),
        new(Swiftfoot, "Swiftfoot", "+20% run speed out of combat (with or without the Hastener).", false),
        new(SecondSkin, "Second Skin", "+10% to every resist, and your resist cap from items is 5 higher.", false),
        new(HuntersInstinct, "Hunter's Instinct", "Monsters below half health take 15% more damage from you.", false),
        new(Berserker, "Berserker", "+30% melee damage and 15% faster melee swings, but you cannot be healed above 70% health.", false),
        new(BeastBond, "Beast Bond", "Your pets have 20% more health and deal 15% more damage.", false),
        new(Overreach, "Overreach", "+15% spell range and +10% casting speed, on top of your items' bonuses.", false),
        new(Deadeye, "Deadeye", "+15% ranged critical hit chance (the 50% cap still applies).", false),
        new(Sharpshooter, "Sharpshooter", "+25% bow damage and 10% faster draws, but 20% less melee damage.", false),
        new(Skirmisher, "Skirmisher", "+25% melee damage and 10% faster swings, but 20% less bow damage.", false),
    };

    /// <summary>
    /// Boons taken out of the catalog (owner 2026-10-25; Greed and Prospector 2026-10-27: the base drop rate is high
    /// enough that a drop chance bonus barely shows; Scavenger and Frostbite 2026-10-27; Warding 2026-10-28: next to nothing casts
    /// on the player in an expedition).
    /// A run that has one gets a new boon choice instead.
    /// </summary>
    public static readonly IReadOnlySet<string> Retired = new HashSet<string>(StringComparer.Ordinal) { HuntersEye, ChainLightning, Rupture, Unshakable, SecondWind, WardedHide, Greed, Prospector, Scavenger, Frostbite, Warding };

    /// <summary>
    /// At login: takes retired boons off the run. A retired boon the run had taken is replaced by a fresh offer for
    /// the last act won (if none is waiting); an offer that lists one is drawn again. The caller saves when true.
    /// </summary>
    public static bool RemoveRetired(GamePlayer player, ExpeditionRun run)
    {
        if (run == null)
        {
            return false;
        }

        bool hadTaken;
        bool offerHadRetired;

        lock (run.Boons)
        {
            hadTaken = run.Boons.RemoveAll(Retired.Contains) > 0;
            offerHadRetired = run.PendingBoonOffer.Any(Retired.Contains);
        }

        if (!hadTaken && !offerHadRetired)
        {
            return false;
        }

        bool offerWaiting;

        lock (run.Boons)
        {
            offerWaiting = run.PendingBoonOffer.Count > 0 && !offerHadRetired;
        }

        if (!offerWaiting && run.Act > 1)
        {
            OfferAtActBreak(run, run.Act - 1, out _, UsesPower(player), HasPet(player), FightsInMelee(player), IsRanged(player));
        }

        run.AddLog($"Act {run.Act}: a removed boon was taken off the run{(hadTaken ? "; a new boon choice is offered" : "; the offer was drawn again")}.");

        return true;
    }

    /// <summary>
    /// Boons that only help a class with power (spells): never offered to a pure melee class (one with no power stat,
    /// such as Hero, Blademaster, Armsman or Warrior), where they would do nothing.
    /// </summary>
    public static readonly IReadOnlySet<string> CasterOnly = new HashSet<string>(StringComparer.Ordinal) { ArcaneFury, Siphon, Overreach };

    /// <summary>Boons that only help a class that fights in melee: never offered to a pure caster (owner 2026-10-29).</summary>
    public static readonly IReadOnlySet<string> MeleeOnly = new HashSet<string>(StringComparer.Ordinal) { Bloodlust, Berserker };

    /// <summary>Boons that only help an archer (<see cref="RangedClasses"/>): never offered to the others (owner 2026-10-29).</summary>
    public static readonly IReadOnlySet<string> RangedOnly = new HashSet<string>(StringComparer.Ordinal) { Deadeye, Sharpshooter, Skirmisher };

    /// <summary>The archer classes: Scout, Hunter and Ranger.</summary>
    public static readonly IReadOnlySet<eCharacterClass> RangedClasses = new HashSet<eCharacterClass> { eCharacterClass.Scout, eCharacterClass.Hunter, eCharacterClass.Ranger };

    /// <summary>True for an archer (<see cref="RangedClasses"/>).</summary>
    public static bool IsRanged(GamePlayer player)
    {
        return player?.CharacterClass != null && RangedClasses.Contains((eCharacterClass) player.CharacterClass.ID);
    }

    /// <summary>True for a class that fights in melee: everyone but a pure caster (BoonEffects.IsPureCaster).</summary>
    public static bool FightsInMelee(GamePlayer player)
    {
        return !BoonEffects.IsPureCaster(player);
    }

    /// <summary>Boons that only help a class with a pet: never offered to the others (owner 2026-10-27).</summary>
    public static readonly IReadOnlySet<string> PetOnly = new HashSet<string>(StringComparer.Ordinal) { BeastBond };

    /// <summary>
    /// The classes that summon or charm a pet: Albion's Cabalist, Necromancer, Theurgist, Sorcerer and Minstrel;
    /// Midgard's Spiritmaster, Bonedancer and Hunter; Hibernia's Enchanter, Animist, Mentalist, Druid and Sluaghbinder.
    /// </summary>
    public static readonly IReadOnlySet<eCharacterClass> PetClasses = new HashSet<eCharacterClass>
    {
        eCharacterClass.Cabalist, eCharacterClass.Necromancer, eCharacterClass.Theurgist, eCharacterClass.Sorcerer, eCharacterClass.Minstrel,
        eCharacterClass.Spiritmaster, eCharacterClass.Bonedancer, eCharacterClass.Hunter,
        eCharacterClass.Enchanter, eCharacterClass.Animist, eCharacterClass.Mentalist, eCharacterClass.Druid, eCharacterClass.Sluaghbinder
    };

    /// <summary>True for a class with a pet (<see cref="PetClasses"/>).</summary>
    public static bool HasPet(GamePlayer player)
    {
        return player?.CharacterClass != null && PetClasses.Contains((eCharacterClass) player.CharacterClass.ID);
    }

    /// <summary>True for a class that has power to cast with (CharacterClass.ManaStat set); false for pure melee.</summary>
    public static bool UsesPower(GamePlayer player)
    {
        return player?.CharacterClass == null || player.CharacterClass.ManaStat != eStat.UNDEFINED;
    }

    public static BoonDefinition Find(string id)
    {
        return Catalog.FirstOrDefault(boon => string.Equals(boon.Id, id, StringComparison.Ordinal));
    }

    #region Offers

    /// <summary>
    /// The offer for an act break: up to three boons not in <paramref name="taken"/>, the same for the same seed, act,
    /// taken boons and reroll. Every boon left is equally likely (owner 2026-10-27: no slot is kept for a loot boon). A reroll
    /// (<paramref name="reroll"/> above 0) draws from boons not in <paramref name="exclude"/> (the offer being
    /// rerolled) and only falls back to them when too few others are left.
    /// </summary>
    public static List<string> RollOffer(int seed, int act, IReadOnlyCollection<string> taken, bool usesPower = true,
        IReadOnlyCollection<string> exclude = null, int reroll = 0, bool hasPet = false, bool melee = true, bool ranged = false)
    {
        var rng = new Random(unchecked(seed * 131 + act + reroll * 7919));
        List<BoonDefinition> available = Catalog
            .Where(boon => taken == null || !taken.Contains(boon.Id))
            .Where(boon => usesPower || !CasterOnly.Contains(boon.Id))
            .Where(boon => hasPet || !PetOnly.Contains(boon.Id))
            .Where(boon => melee || !MeleeOnly.Contains(boon.Id))
            .Where(boon => ranged || !RangedOnly.Contains(boon.Id))
            .ToList();
        List<BoonDefinition> fresh = available.Where(boon => exclude == null || !exclude.Contains(boon.Id)).ToList();
        var offer = new List<BoonDefinition>();

        while (offer.Count < OfferSize && fresh.Count > 0)
        {
            BoonDefinition pick = fresh[rng.Next(fresh.Count)];
            offer.Add(pick);
            available.Remove(pick);
            fresh.Remove(pick);
        }

        // Too few fresh boons left (late in a run): fill from the rerolled offer.
        while (offer.Count < OfferSize && available.Count > 0)
        {
            BoonDefinition pick = available[rng.Next(available.Count)];
            offer.Add(pick);
            available.Remove(pick);
        }

        return offer.Select(boon => boon.Id).ToList();
    }

    /// <summary>
    /// Stores the offer for the act just won in <see cref="ExpeditionRun.PendingBoonOffer"/> and logs it. An offer
    /// still waiting from an earlier act is replaced (<paramref name="replacedUnclaimed"/>). The caller saves the run.
    /// Returns false when every boon is taken.
    /// </summary>
    public static bool OfferAtActBreak(ExpeditionRun run, int finishedAct, out bool replacedUnclaimed, bool usesPower = true, bool hasPet = false,
        bool melee = true, bool ranged = false)
    {
        replacedUnclaimed = false;

        if (run == null)
        {
            return false;
        }

        List<string> offer;

        lock (run.Boons)
        {
            replacedUnclaimed = run.PendingBoonOffer.Count > 0;
            offer = RollOffer(run.Seed, finishedAct, run.Boons.ToList(), usesPower, hasPet: hasPet, melee: melee, ranged: ranged);
            run.PendingBoonOffer = offer;
        }

        if (offer.Count == 0)
        {
            return false;
        }

        run.AddLog($"Act {finishedAct}: a boon is offered ({string.Join(", ", offer.Select(NameOf))}).");

        return true;
    }

    #endregion

    #region Loot

    /// <summary>
    /// Fortune: the rarity a drop rolled is shifted one step up (Epic and Unique stay; a Unique only ever comes from the
    /// roll, so it always goes through the unique catalog). This is Fortune's only effect: a forced floor (elite pack,
    /// first objective kill) is left as it is and the roll above it is shifted, so Fortune counts once per drop.
    /// Called by ExpeditionLoot.DropFor and BossDrops.
    /// </summary>
    /// <summary>Treasure Sense: how many times the Unique weight counts in a rarity roll (2 with the boon, else 1).</summary>
    public static int UniqueWeightMultiplier(ExpeditionRun run)
    {
        return run != null && Snapshot(run).Contains(TreasureSense) ? 2 : 1;
    }

    public static eLootRarity ShiftRolledRarity(ExpeditionRun run, eLootRarity rolled)
    {
        if (run == null)
        {
            return rolled;
        }

        return ShiftRolledRarity(Snapshot(run), rolled);
    }

    public static eLootRarity ShiftRolledRarity(IReadOnlyList<string> boonIds, eLootRarity rolled)
    {
        if (boonIds == null || !boonIds.Contains(Fortune) || rolled >= eLootRarity.Epic)
        {
            return rolled;
        }

        return rolled + 1;
    }

    #endregion

    #region Journal and choice

    /// <summary>Journal lines about the run's boons and a pending offer; empty when the run has none.</summary>
    public static IReadOnlyList<string> DescribeBoons(ExpeditionRun run)
    {
        var lines = new List<string>();

        if (run == null)
        {
            return lines;
        }

        List<string> taken;
        List<string> offer;

        lock (run.Boons)
        {
            taken = run.Boons.ToList();
            offer = run.PendingBoonOffer.ToList();
        }

        if (taken.Count > 0)
        {
            lines.Add($"Boons: {string.Join(", ", taken.Select(NameOf))}");
        }

        if (offer.Count > 0)
        {
            lines.Add($"A boon awaits (type /run boons): {string.Join(", ", offer.Select(NameOf))}");
        }

        return lines;
    }

    /// <summary>The /run boons window: the waiting offer, numbered, then the boons already taken.</summary>
    public static List<string> OfferLines(ExpeditionRun run)
    {
        var lines = new List<string>();
        List<string> taken;
        List<string> offer;

        lock (run.Boons)
        {
            taken = run.Boons.ToList();
            offer = run.PendingBoonOffer.ToList();
        }

        if (offer.Count > 0)
        {
            int rerollsLeft = Math.Max(0, RerollsPerRun - run.RerollsUsed);
            lines.Add("Choose one boon with /run boon <number>. It lasts the whole run.");
            lines.Add(rerollsLeft > 0
                ? $"Don't like these? /run reroll draws 3 different ones ({rerollsLeft} reroll{(rerollsLeft == 1 ? string.Empty : "s")} left this run)."
                : "No rerolls left this run.");
            lines.Add(string.Empty);

            for (int i = 0; i < offer.Count; i++)
            {
                BoonDefinition boon = Find(offer[i]);
                lines.Add($"{i + 1}. {boon?.Name ?? offer[i]}{(boon?.IsLoot == true ? " (loot)" : string.Empty)}: {boon?.Effect ?? "unknown boon"}");
            }
        }
        else
        {
            lines.Add("No boon is waiting. A boon is offered each time you defeat an act boss.");
        }

        lines.Add(string.Empty);
        lines.Add(taken.Count == 0 ? "Boons taken: none" : "Boons taken:");

        foreach (string id in taken)
        {
            BoonDefinition boon = Find(id);
            lines.Add($"- {boon?.Name ?? id}: {boon?.Effect ?? "unknown boon"}");
        }

        return lines;
    }

    /// <summary>
    /// Takes choice <paramref name="choice"/> (1-based) of the waiting offer: the boon is added to the run, the offer
    /// cleared, the effect applied, the run logged and saved. Returns the message for the player.
    /// </summary>
    public static bool TryChoose(GamePlayer player, ExpeditionRun run, int choice, out string message)
    {
        if (player == null || run == null)
        {
            message = "You are not on an expedition.";

            return false;
        }

        string id;

        lock (run.Boons)
        {
            if (run.PendingBoonOffer.Count == 0)
            {
                message = "No boon is waiting. A boon is offered each time you defeat an act boss.";

                return false;
            }

            if (choice < 1 || choice > run.PendingBoonOffer.Count)
            {
                message = $"Choose a number from 1 to {run.PendingBoonOffer.Count}. Type /run boons to see the offer.";

                return false;
            }

            id = run.PendingBoonOffer[choice - 1];

            if (!run.Boons.Contains(id))
            {
                run.Boons.Add(id);
            }

            run.PendingBoonOffer = new List<string>();
        }

        BoonDefinition boon = Find(id);
        string name = boon?.Name ?? id;

        run.AddLog($"Act {run.Act}: took the boon {name}.");
        BoonEffects.ApplyAll(player, run);

        // One boon per act won: a run still owed one (a retired boon was taken off while this offer was waiting) gets
        // its next choice straight away.
        bool owed = OwesBoon(run) && OfferAtActBreak(run, run.Act - 1, out _, UsesPower(player), HasPet(player), FightsInMelee(player), IsRanged(player));
        ExpeditionManager.Save(run);

        message = $"You take the boon {name}: {boon?.Effect ?? "unknown boon"}";

        if (owed)
        {
            message += " Another boon awaits: type /run boons.";
        }

        return true;
    }

    #endregion

    /// <summary>True when no offer is waiting and the run has fewer boons than acts won.</summary>
    public static bool OwesBoon(ExpeditionRun run)
    {
        lock (run.Boons)
        {
            return run.PendingBoonOffer.Count == 0 && run.Boons.Count < run.Act - 1;
        }
    }

    /// <summary>Glory: the realm point multiplier for the run (1.5 with the boon, else 1).</summary>
    public static double RealmPointFactor(ExpeditionRun run)
    {
        return run != null && Snapshot(run).Contains(Glory) ? 1.0 + GloryRealmPointPercent / 100.0 : 1.0;
    }

    private static IReadOnlyList<string> Snapshot(ExpeditionRun run)
    {
        lock (run.Boons)
        {
            return run.Boons.ToArray();
        }
    }

    private static string NameOf(string id)
    {
        return Find(id)?.Name ?? id;
    }

    /// <summary>Applies every taken boon to the player; safe to call again (login, new act). See <see cref="BoonEffects"/>.</summary>
    public static void ApplyAll(GamePlayer player, ExpeditionRun run)
    {
        BoonEffects.ApplyAll(player, run);
    }

    /// <summary>Takes every applied boon effect off the player (the run ended, or the player quit).</summary>
    public static void RemoveAll(GamePlayer player, bool sendUpdates = true)
    {
        BoonEffects.RemoveAll(player, sendUpdates);

        // The run's shrine and boon buffs end with it; a quitting player's effects go with the player object.
        if (sendUpdates)
        {
            ExpeditionBuffs.CancelAll(player);
        }
    }

    /// <summary>
    /// /run reroll: replaces the waiting offer with a new one that leaves out the boons just offered, using one of the
    /// run's <see cref="RerollsPerRun"/> rerolls. The offer was drawn for the act just won (run.Act - 1). Saves the run.
    /// </summary>
    public static bool TryReroll(GamePlayer player, ExpeditionRun run, out string message)
    {
        if (player == null || run == null)
        {
            message = "You are not on an expedition.";

            return false;
        }

        int rerollNumber;

        lock (run.Boons)
        {
            if (run.PendingBoonOffer.Count == 0)
            {
                message = "No boon is waiting to reroll. A boon is offered each time you defeat an act boss.";

                return false;
            }

            if (run.RerollsUsed >= RerollsPerRun)
            {
                message = $"You have used all {RerollsPerRun} rerolls for this run.";

                return false;
            }

            rerollNumber = run.RerollsUsed + 1;
            List<string> offer = RollOffer(run.Seed, Math.Max(1, run.Act - 1), run.Boons.ToList(), UsesPower(player),
                run.PendingBoonOffer.ToList(), rerollNumber, HasPet(player), FightsInMelee(player), IsRanged(player));

            if (offer.Count == 0)
            {
                message = "There are no other boons left to offer.";

                return false;
            }

            run.PendingBoonOffer = offer;
            run.RerollsUsed = rerollNumber;
        }

        run.AddLog($"Act {run.Act}: the boon offer was rerolled ({string.Join(", ", run.PendingBoonOffer.Select(NameOf))}).");
        ExpeditionManager.Save(run);
        player.Out.SendCustomTextWindow("Expedition boons", OfferLines(run));

        int left = RerollsPerRun - rerollNumber;
        message = $"The boon offer is rerolled. {left} reroll{(left == 1 ? string.Empty : "s")} left this run.";

        return true;
    }

    /// <summary>Tells the player about a waiting offer (act break and login).</summary>
    public static void RemindOffer(GamePlayer player, ExpeditionRun run)
    {
        if (player?.Out == null || run == null || run.PendingBoonOffer.Count == 0)
        {
            return;
        }

        player.Out.SendMessage("A boon awaits: type /run boons to see it and /run boon <number> to take it.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }
}
