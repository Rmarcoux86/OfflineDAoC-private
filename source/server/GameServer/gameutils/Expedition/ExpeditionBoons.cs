using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.PacketHandler;

namespace DOL.GS.Expedition;

/// <summary>One boon of the catalog. Loot boons change drops; the others are applied to the player (<see cref="BoonEffects"/>).</summary>
public sealed record BoonDefinition(string Id, string Name, string Effect, bool IsLoot);

/// <summary>
/// Run boons: build-defining picks offered one-of-three at each act break. The offer is drawn from the run seed and
/// the finished act, never repeats a boon already taken, and holds a loot boon while any is left. The choice waits
/// until the player types /run boon &lt;1-3&gt;; boons last the whole run and are applied again at each login.
/// The loot math and the offers are pure functions of boon ids so they can be tested without a server.
/// </summary>
public static class ExpeditionBoons
{
    public const int OfferSize = 3;

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

    /// <summary>Drop chance added by Greed, in percentage points.</summary>
    public const int GreedChanceBonus = 25;

    /// <summary>Drop chance added by Prospector, in percentage points.</summary>
    public const int ProspectorChanceBonus = 10;

    /// <summary>The catalog, in a fixed order: offers index into it, so reordering changes every seed's offers.</summary>
    public static readonly IReadOnlyList<BoonDefinition> Catalog = new List<BoonDefinition>
    {
        new(Fortune, "Fortune", "Every Expedition drop is one rarity better, up to Epic.", true),
        new(Greed, "Greed", $"+{GreedChanceBonus}% drop chance per kill, but monsters hit you 15% harder.", true),
        new(Prospector, "Prospector", $"+{ProspectorChanceBonus}% drop chance per kill.", true),
        new(Ironhide, "Ironhide", "+10% maximum health and 10% less physical damage taken.", false),
        new(Warding, "Warding", "15% less damage taken from spells.", false),
        new(Bloodlust, "Bloodlust", "+15% melee critical hit chance (the 50% cap still applies).", false),
        new(HuntersEye, "Hunter's Eye", "Named monsters within 2000 units are announced to you.", false),
        new(Thornmail, "Thornmail", "Monsters that hit you in melee take heat damage (3 + your level) on every hit.", false),
        new(Vampiric, "Vampiric", "Each hit you land (melee or spell) has a 25% chance to heal you for half its damage.", false),
        new(Rejuvenation, "Rejuvenation", "You regenerate 2% of your maximum health every 3 seconds, even in combat.", false),
        new(ArcaneFury, "Arcane Fury", "+15% spell critical hit chance.", false)
    };

    public static BoonDefinition Find(string id)
    {
        return Catalog.FirstOrDefault(boon => string.Equals(boon.Id, id, StringComparison.Ordinal));
    }

    #region Offers

    /// <summary>
    /// The offer for an act break: up to three boons not in <paramref name="taken"/>, the same for the same seed, act
    /// and taken boons. While any loot boon is left, one of them is in the offer.
    /// </summary>
    public static List<string> RollOffer(int seed, int act, IReadOnlyCollection<string> taken, int size = OfferSize)
    {
        var rng = new Random(unchecked(seed * 131 + act));
        List<BoonDefinition> available = Catalog.Where(boon => taken == null || !taken.Contains(boon.Id)).ToList();
        var offer = new List<BoonDefinition>();
        List<BoonDefinition> loot = available.Where(boon => boon.IsLoot).ToList();

        if (loot.Count > 0)
        {
            BoonDefinition pick = loot[rng.Next(loot.Count)];
            offer.Add(pick);
            available.Remove(pick);
        }

        while (offer.Count < size && available.Count > 0)
        {
            BoonDefinition pick = available[rng.Next(available.Count)];
            offer.Add(pick);
            available.Remove(pick);
        }

        // The loot boon is drawn first; shuffle so it is not always choice 1.
        for (int i = offer.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (offer[i], offer[j]) = (offer[j], offer[i]);
        }

        return offer.Select(boon => boon.Id).ToList();
    }

    /// <summary>
    /// Stores the offer for the act just won in <see cref="ExpeditionRun.PendingBoonOffer"/> and logs it. An offer
    /// still waiting from an earlier act is replaced (<paramref name="replacedUnclaimed"/>). The caller saves the run.
    /// Returns false when every boon is taken.
    /// </summary>
    public static bool OfferAtActBreak(ExpeditionRun run, int finishedAct, out bool replacedUnclaimed)
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
            offer = RollOffer(run.Seed, finishedAct, run.Boons.ToList(), ExpeditionLegacy.OfferSize(run.AccountName));
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

    /// <summary>Lets loot boons change the per-kill drop chance (percent).</summary>
    public static int AdjustLootChance(GamePlayer player, ExpeditionRun run, int chance)
    {
        if (run == null)
        {
            return chance;
        }

        return LootChanceWithBoons(Snapshot(run), chance) + ExpeditionLegacy.LootChanceBonus(player);
    }

    /// <summary>Greed and Prospector add percentage points to the drop chance; the caller clamps to 0-100.</summary>
    public static int LootChanceWithBoons(IReadOnlyList<string> boonIds, int chance)
    {
        if (boonIds == null)
        {
            return chance;
        }

        if (boonIds.Contains(Greed))
        {
            chance += GreedChanceBonus;
        }

        if (boonIds.Contains(Prospector))
        {
            chance += ProspectorChanceBonus;
        }

        return chance;
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
            lines.Add("Choose one boon with /run boon <number>. It lasts the whole run.");
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
        ExpeditionManager.Save(run);

        message = $"You take the boon {name}: {boon?.Effect ?? "unknown boon"}";

        return true;
    }

    #endregion

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
