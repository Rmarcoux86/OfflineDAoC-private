using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.GS.Expedition.Gauntlet;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Hard mode (owner 2026-10-29): a second expedition on a new character, for an account that has beaten the Gauntlet,
/// with everything stronger and the safety nets gone. Every hard-mode rule reads <see cref="IsHard"/> and is listed in
/// <see cref="RulesLines"/>, so a death reads as a mistake and not a surprise.
/// </summary>
public static class ExpeditionHardMode
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>True for a hard-mode run.</summary>
    public static bool IsHard(ExpeditionRun run)
    {
        return run?.Hard == true;
    }

    /// <summary>True while the player is on a hard-mode run.</summary>
    public static bool IsHard(GamePlayer player)
    {
        return IsHard(ExpeditionManager.GetRun(player));
    }

    /// <summary>
    /// True when a run on the player's account won its expedition and then beat the Gauntlet (every round reached; a
    /// /gauntlet testwinner record, with no end time, does not count).
    /// </summary>
    public static bool IsUnlocked(GamePlayer player)
    {
        string account = player?.Client?.Account?.Name;

        if (string.IsNullOrEmpty(account))
        {
            return false;
        }

        try
        {
            return GameServer.Database.SelectObjects<OfflineExpeditionRecord>(DB.Column("AccountName").IsEqualTo(account))
                .Any(record => record.Status == ExpeditionManager.StatusWon && !string.IsNullOrEmpty(record.EndedUtc) &&
                    // Five rounds beaten unlocks it for everyone; a hard winner's sixth round is their extra.
                    GauntletSave.Parse(record.GauntletJson).Depth >= GauntletRules.Rounds);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the runs of account {account} for the hard-mode unlock.", ex);

            return false;
        }
    }

    /// <summary>The rules of a hard expedition, for /run rules and the first act's arrival.</summary>
    public static List<string> RulesLines()
    {
        return new List<string>
        {
            "A hard expedition. Everything that follows is stronger than you remember:",
            string.Empty,
            "- Opens to an account that has beaten the Gauntlet.",
            // core
            "- Inherit one unique from your Gauntlet winner (/run inherit), once: it starts at level 1 and grows with you.",
            "- No recovery: /recover cannot bring back a character that died on a hard expedition.",
            "- Realm points are doubled for the whole run.",

            // gauntlet
            $"- Win a hard expedition and your Gauntlet has a sixth round: two {GauntletRules.GuildName} champions at once, both at realm rank 10.",

            // boons
            $"- Boon offers hold {ExpeditionBoons.HardOfferSize} choices instead of {ExpeditionBoons.OfferSize}, and Phoenix Feather is never offered.",
            $"- Rejuvenation heals 1% every {BoonEffects.RejuvenationIntervalFor(true) / 1000} seconds (not {BoonEffects.RejuvenationIntervalFor(false) / 1000}), and Blessed Arms' buffs are weaker ({ExpeditionBuffs.HardBlessedArmsPercent}% of the cap, not {ExpeditionBuffs.BlessedArmsPercent}%).",
            "- Five boons appear only on a hard expedition: Juggernaut, Ambush, Silent Death, Poisoner and Dirty Fighting.",
            "- Three uniques drop only on a hard expedition, from level 40, alongside the late ones.",
            $"- Each act has {ExpeditionShrines.HardShrinesPerAct} shrines, each beside an elite pack and dormant until that whole pack is slain.",

            // bosses
            "- The act bosses are the Gauntlet's champions, in its order (Gwyraan, Toothmouth, Ios, Drunkstains, Preesh), at the act's top level + 2 with their realm ranks, realm abilities and signature moves. They attack on sight: no bow.",
            "- A champion boss has the act boss's health (3 + the act, times its own) and is never weakened; Stalwart adds nothing more to it.",
            $"- Each act has a bounty timer ({ExpeditionBounty.EarlyActMinutes} minutes for acts 1-2, {ExpeditionBounty.MiddleActMinutes} for 3-4, {ExpeditionBounty.FinalActMinutes} for act 5; /run status shows it, and it stops while you are logged out). Miss it and the boss arrives with a pack of {ExpeditionBounty.HelperCount} monsters of the zone.",
            "- A dark storm hangs over the act's zone.",

            // monsters
            $"- Every monster of the act is Dread: {ExpeditionHardMonsters.DreadLevelBonus} level above its kind.",
            "- Every camp of the act is an elite pack (save the objectives' and the boss's monsters), and every elite rolls two affixes (\"Swift Venomous Dread goblin\").",
            $"- The act boss has {ExpeditionHardMonsters.BossAffixCount} affixes, and its leash is {ExpeditionHardMonsters.BossLeashMultiplier}x as long: it cannot be kited home to reset.",
            $"- The hunt: every {ExpeditionHunt.SpawnIntervalMs / 60_000} minutes you spend in an act, a Swift stalker {ExpeditionHunt.LevelAboveAct} levels above the act is sent after you. You are warned {ExpeditionHunt.WarningLeadMs / 1000} seconds before, told where it is every {ExpeditionHunt.ReportIntervalMs / 1000} seconds, and it gives up after {ExpeditionHunt.GiveUpMs / 60_000} minutes if it never reaches you. It drops nothing.",
        };
    }

    #region core: realm points, arrival taunts, inherited unique, recovery

    /// <summary>Realm points on a hard run (owner 2026-10-29: "Doubled realm points for the rest of the run").</summary>
    public const double HardRealmPointMultiplier = 2.0;

    /// <summary>The realm point multiplier of the run's mode: 2 on a hard run, else 1. Stacks with Glory.</summary>
    public static double RealmPointFactor(ExpeditionRun run)
    {
        return IsHard(run) ? HardRealmPointMultiplier : 1.0;
    }

    /// <summary>
    /// The Gauntlet guild's word at each act's arrival on a hard run, one champion per act in the order they are faced
    /// (Gwyraan, Toothmouth, Ios, Drunkstains, Preesh).
    /// </summary>
    public static readonly string[] ArrivalTaunts =
    [
        $"Gwyraan of {GauntletRules.GuildName} sends his regards. These woods are not yours yet.",
        $"Toothmouth of {GauntletRules.GuildName} is already humming your dirge. Keep walking.",
        $"Ios of {GauntletRules.GuildName} says you won't see her coming. You never do.",
        $"Drunkstains of {GauntletRules.GuildName} bet you would not get this far. He hates losing bets.",
        $"Preesh of {GauntletRules.GuildName} waits beyond the dark. Bow when you get there."
    ];

    /// <summary>The taunt for an act (1-5); the act is clamped into range.</summary>
    public static string ArrivalTaunt(int act)
    {
        return ArrivalTaunts[Math.Clamp(act, 1, ArrivalTaunts.Length) - 1];
    }

    /// <summary>
    /// A hard run has reached an act's camp and settled: the champion's taunt, and on act 1 the rules with the unique
    /// it may inherit, in one window.
    /// </summary>
    public static void OnActArrival(GamePlayer player, ExpeditionRun run)
    {
        if (player == null || !IsHard(run))
        {
            return;
        }

        string taunt = ArrivalTaunt(run.Act);
        player.Out.SendMessage(taunt, eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        player.Out.SendMessage(taunt, eChatType.CT_Important, eChatLoc.CL_SystemWindow);

        if (run.Act != 1)
        {
            return;
        }

        List<string> lines = RulesLines();
        List<string> offer = InheritOfferLines(player, run);

        if (offer.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(offer);
        }

        lines.Add(string.Empty);
        lines.Add("Type /run journal for your objectives.");
        player.Out.SendCustomTextWindow("Hard expedition", lines);
    }

    /// <summary>A unique the account's Gauntlet winner carries: its name and who carries it.</summary>
    public sealed record InheritableUnique(string Name, string Owner, eUniqueTier Tier);

    /// <summary>
    /// The uniques the account's Gauntlet winners (won runs that beat every round, as <see cref="IsUnlocked"/> reads them)
    /// still carry, one of each name, by name. The winner must still exist; its items are read from the database.
    /// </summary>
    public static List<InheritableUnique> InheritableUniques(GamePlayer player)
    {
        var result = new List<InheritableUnique>();
        string account = player?.Client?.Account?.Name;

        if (string.IsNullOrEmpty(account))
        {
            return result;
        }

        try
        {
            IEnumerable<OfflineExpeditionRecord> winners = GameServer.Database.SelectObjects<OfflineExpeditionRecord>(DB.Column("AccountName").IsEqualTo(account))
                .Where(record => record.Status == ExpeditionManager.StatusWon && !string.IsNullOrEmpty(record.EndedUtc) &&
                    GauntletSave.Parse(record.GauntletJson).Depth >= GauntletRules.Rounds && record.CharacterId != player.ObjectId);
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (OfflineExpeditionRecord winner in winners)
            {
                DbCoreCharacter character = GameServer.Database.FindObjectByKey<DbCoreCharacter>(winner.CharacterId);

                if (character == null)
                {
                    continue;
                }

                foreach (DbInventoryItem item in GameServer.Database.SelectObjects<DbInventoryItem>(DB.Column("OwnerID").IsEqualTo(character.ObjectId)))
                {
                    UniqueDefinition definition = item?.Template == null ? null : ExpeditionUniques.FindByName(item.Name);

                    if (definition != null && seen.Add(definition.Name))
                    {
                        result.Add(new InheritableUnique(definition.Name, character.Name, definition.Tier));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the Gauntlet winners' uniques of account {account}.", ex);
        }

        return result.OrderBy(unique => unique.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The numbered inherit offer for the act 1 window; empty (and logged) when there is nothing to inherit.</summary>
    public static List<string> InheritOfferLines(GamePlayer player, ExpeditionRun run)
    {
        var lines = new List<string>();

        if (!IsHard(run) || !string.IsNullOrEmpty(run.InheritedUnique))
        {
            return lines;
        }

        List<InheritableUnique> uniques = InheritableUniques(player);

        if (uniques.Count == 0)
        {
            Log.Info($"[EXPEDITION] {player?.Name}'s hard run has no unique to inherit: the account's Gauntlet winners carry none.");

            return lines;
        }

        lines.Add("-- INHERIT ONE UNIQUE (/run inherit <n>, once) --");

        for (int i = 0; i < uniques.Count; i++)
        {
            lines.Add($"{i + 1}. {uniques[i].Name} ({uniques[i].Tier}, carried by {uniques[i].Owner})");
        }

        lines.Add("It starts at level 1 and grows with you up to its usual cap; the original stays with its owner.");

        return lines;
    }

    /// <summary>
    /// /run inherit &lt;n&gt;: a fresh copy of the n-th unique of <see cref="InheritableUniques"/>, built at the early tier's
    /// level and grown to the player's level by <see cref="ExpeditionUniques.LevelUniques"/>, which caps it at its own
    /// tier. Once per hard run. Returns the message for the player.
    /// </summary>
    public static string TryInherit(GamePlayer player, ExpeditionRun run, int choice)
    {
        if (!IsHard(run))
        {
            return "Only a hard expedition inherits a unique.";
        }

        if (!string.IsNullOrEmpty(run.InheritedUnique))
        {
            return $"This expedition has already inherited {run.InheritedUnique}.";
        }

        List<InheritableUnique> uniques = InheritableUniques(player);

        if (uniques.Count == 0)
        {
            return "Your account's Gauntlet winners carry no unique to inherit.";
        }

        if (choice < 1 || choice > uniques.Count)
        {
            return $"Choose a unique from 1 to {uniques.Count}: type /run inherit to see them.";
        }

        UniqueDefinition definition = ExpeditionUniques.FindByName(uniques[choice - 1].Name);

        if (definition == null)
        {
            return "That unique is no longer known.";
        }

        try
        {
            GeneratedUniqueItem item = ExpeditionUniques.Build(player, definition, itemLevel: (byte) ExpeditionUniques.TierMinLevel(eUniqueTier.Early));
            DbInventoryItem invItem = GameInventoryItem.Create(item);
            invItem.IsROG = true;
            invItem.Creator = uniques[choice - 1].Owner;

            if (!player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, invItem))
            {
                return "Your backpack is full: make room and try again.";
            }

            run.InheritedUnique = definition.Name;
            run.AddLog($"Act {run.Act}: inherited {definition.Name} from {uniques[choice - 1].Owner}.");
            ExpeditionManager.Save(run);

            // Grows to the player's level at once when the pick comes later in the run.
            ExpeditionUniques.LevelUniques(player);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] {player.Name} could not inherit {definition.Name}.", ex);

            return "The unique could not be made. The server log has the details.";
        }

        return $"You inherit {definition.Name} from {uniques[choice - 1].Owner}. It grows with you up to level {ExpeditionUniques.TierCapLevel(definition.Tier)}.";
    }

    /// <summary>
    /// The unique this character's run inherited, or null: the run in memory, else its saved record (the login levels
    /// uniques before the run is cached). LevelUniques lets it start below its own tier.
    /// </summary>
    public static string InheritedUniqueOf(GamePlayer player)
    {
        ExpeditionRun run = ExpeditionManager.GetRun(player);

        if (run != null)
        {
            return run.InheritedUnique;
        }

        if (player == null)
        {
            return null;
        }

        try
        {
            string name = GameServer.Database.FindObjectByKey<OfflineExpeditionRecord>(player.ObjectId)?.InheritedUnique;

            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the inherited unique of {player.Name}.", ex);

            return null;
        }
    }

    /// <summary>True when the character died on a hard run: a hard expedition has no recovery (/recover refuses it).</summary>
    public static bool DiedOnHardRun(string characterId)
    {
        if (string.IsNullOrEmpty(characterId))
        {
            return false;
        }

        try
        {
            OfflineExpeditionRecord record = GameServer.Database.FindObjectByKey<OfflineExpeditionRecord>(characterId);

            return record != null && record.Hard && record.Status == ExpeditionManager.StatusDead;
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the run of character {characterId} for recovery.", ex);

            return false;
        }
    }

    #endregion
}
