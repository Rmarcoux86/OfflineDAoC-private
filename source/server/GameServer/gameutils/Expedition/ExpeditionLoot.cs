using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.Expedition;

public enum eLootRarity
{
    Common = 0,
    Magic = 1,
    Rare = 2,
    Epic = 3,
    Unique = 4
}

/// <summary>How one rarity drives the random-object generator (<see cref="GeneratedUniqueItem"/>). UniqueName: the item comes from the fixed unique catalog (<see cref="ExpeditionUniques"/>).</summary>
public readonly record struct LootProfile(int MinUtility, int MinQuality, int LevelBonus, bool ExtraBonus, bool UniqueName);

/// <summary>The per-kill drop tuning, read from the server properties at each kill (see <see cref="FromProperties"/>).</summary>
public readonly record struct LootTuning(int BaseChance, int GreyChance, int HighConBonus, int PityStep, int PityGuarantee)
{
    public static LootTuning FromProperties()
    {
        return new LootTuning(
            Properties.EXPEDITION_LOOT_CHANCE,
            Properties.EXPEDITION_LOOT_GREY_CHANCE,
            Properties.EXPEDITION_LOOT_HIGH_CON_BONUS,
            Properties.EXPEDITION_LOOT_PITY_STEP,
            Properties.EXPEDITION_LOOT_PITY_GUARANTEE);
    }
}

/// <summary>
/// One player's luck: kills in a row without a drop, and the last target seen and when (one kill can raise
/// EnemyKilled twice; a respawned mob is the same GameNPC object, so the check is time-boxed).
/// </summary>
public sealed class LootLuck
{
    public int DryKills;
    internal GameLiving LastTarget;
    internal long LastKillAt;
}

/// <summary>
/// Expedition loot: Diablo-style rarity drops on top of the normal loot tables. Every credited kill of a run player
/// (<see cref="ObjectiveTracker"/>) may drop a random-object-generation item whose rarity (Common to Unique) raises its
/// utility floor, quality and level; the act boss drops several Rare-or-better items (<see cref="BossDrops"/>).
/// The roll, pity and name logic are pure static methods with an injected <see cref="Random"/>, so they are unit tested.
/// With the server setting expedition_loot_outside_runs on, real players outside a run get the same per-kill loot.
/// </summary>
public static class ExpeditionLoot
{
    /// <summary>Item names are kept short for the client's item windows (the packet limit is 255 bytes, PacketOut.cs:183).</summary>
    public const int MaxNameLength = 60;

    /// <summary>A second report of the same target within this window is the same kill (as ObjectiveTracker.KillDedupeWindowMs).</summary>
    private const long KillDedupeWindowMs = 5000;

    /// <summary>Base weights per drop event, Common..Unique.</summary>
    private static readonly int[] BaseWeights = { 60, 27, 9, 3, 1 };

    /// <summary>Boss weights per drop event: Rare or better only.</summary>
    private static readonly int[] BossWeights = { 0, 0, 50, 35, 15 };

    /// <summary>Spell effect played on the player for a Unique drop (the PowerOfNature quest reward glow, PowerOfNature.cs:549). To be checked in game.</summary>
    private const ushort UniqueGlowEffect = 5005;

    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly ConcurrentDictionary<string, LootLuck> Luck = new(StringComparer.Ordinal);

    /// <summary>The TempProperties key and the character custom-param key of the /junk auto setting.</summary>
    public const string AutoJunkKey = "Expedition.AutoJunk";

    /// <summary>The end of every Expedition item's description: " | Expedition {rarity}" (see <see cref="Generate"/>).</summary>
    private const string RaritySuffix = " | Expedition ";

    #region Server hook (outside runs)

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.AddHandler(GameLivingEvent.EnemyKilled, new DOLEventHandler(OnEnemyKilledOutsideRuns));
        ApplyRetunedDefaults();
    }

    // Loot settings whose default changed after release, with the earlier default. The database keeps the value from the
    // first start, so a new default would never reach an existing game.
    private static readonly (string Key, string OldDefault)[] RetunedDefaults =
    [
        ("expedition_loot_chance", "50"),
        ("expedition_loot_grey_chance", "15"),
        ("expedition_loot_pity_step", "10"),
        ("expedition_loot_pity_guarantee", "4")
    ];

    /// <summary>
    /// Moves a loot setting to its new default when the saved value is still the old default, i.e. nobody changed it by
    /// hand. A value someone set on purpose is kept.
    /// </summary>
    private static void ApplyRetunedDefaults()
    {
        try
        {
            var properties = Properties.AllDomainProperties;

            foreach ((string key, string oldDefault) in RetunedDefaults)
            {
                if (!properties.TryGetValue(key, out var entry))
                {
                    continue;
                }

                ServerPropertyAttribute attribute = entry.Item1;
                DbServerProperty row = entry.Item3;
                string newDefault = Convert.ToString(attribute.DefaultValue, System.Globalization.CultureInfo.InvariantCulture);

                if (!row.IsPersisted || row.Value != oldDefault || row.DefaultValue != oldDefault || newDefault == oldDefault)
                {
                    continue;
                }

                row.Value = newDefault;
                row.DefaultValue = newDefault;
                GameServer.Database.SaveObject(row);
                entry.Item2.SetValue(null, Convert.ChangeType(newDefault, attribute.DefaultValue.GetType(), System.Globalization.CultureInfo.InvariantCulture));

                if (log.IsInfoEnabled)
                {
                    log.Info($"[EXPEDITION] Server property {key} moved from its old default {oldDefault} to {newDefault}.");
                }
            }
        }
        catch (Exception ex)
        {
            log.Error("[EXPEDITION] Could not update the loot settings to their new defaults.", ex);
        }
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GameLivingEvent.EnemyKilled, new DOLEventHandler(OnEnemyKilledOutsideRuns));
    }

    /// <summary>
    /// Real players outside a run, only when expedition_loot_outside_runs is on. A run player is skipped here: its
    /// kills reach <see cref="OnKill"/> through <see cref="ObjectiveTracker"/>, so it never gets both.
    /// </summary>
    private static void OnEnemyKilledOutsideRuns(DOLEvent e, object sender, EventArgs args)
    {
        if (!Properties.EXPEDITION_LOOT_OUTSIDE_RUNS)
        {
            return;
        }

        if (sender is not GamePlayer player || player.Client == null || args is not EnemyKilledEventArgs killedArgs)
        {
            return;
        }

        GameLiving target = killedArgs.Target;

        // GameLiving.RemoveFromWorld also raises EnemyKilled on the attackers of a living that merely despawns.
        if (target == null || !(target.IsBeingHandledByReaperService || target.Health <= 0))
        {
            return;
        }

        if (ExpeditionManager.GetRun(player) != null)
        {
            return;
        }

        try
        {
            OnKill(player, target);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Loot failed for {player.Name}.", ex);
        }
    }

    #endregion

    #region Per kill

    /// <summary>
    /// A credited kill of a real monster (not a pet, bot, player or act boss). Rolls the drop with the pity counter and
    /// delivers it. <paramref name="minimum"/> forces a drop of at least that rarity. Returns true when an item dropped.
    /// </summary>
    public static bool OnKill(GamePlayer player, GameLiving target, eLootRarity? minimum = null)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId) || !IsLootableKill(target))
        {
            GameNPC npc = target as GameNPC;
            ExpeditionDebug.Report(player, $"no loot roll for {target?.Name}: not lootable (npc {npc != null}, can drop {npc?.CanDropLoot}, controlled {npc?.Brain is IControlledBrain}, bot {npc is GameBot}).");

            return false;
        }

        LootLuck luck = Luck.GetOrAdd(player.ObjectId, _ => new LootLuck());
        ExpeditionRun run = ExpeditionManager.GetRun(player);
        ActPlan plan = run?.CurrentAct;
        long now = GameLoop.GameLoopTime;
        bool dropped;

        // An elite pack member always drops, Rare or better (ExpeditionElites). Fortune shifts the roll, not this floor (DropFor).
        if (ExpeditionElites.IsElite(target as GameNPC))
        {
            minimum = minimum.HasValue && minimum.Value > eLootRarity.Rare ? minimum : eLootRarity.Rare;
        }

        lock (luck)
        {
            // GamePlayer.EnemyKilled and GameLiving.ProcessDeath can both report one kill; a respawn of the same mob later is a new kill.
            if (luck.LastTarget == target && now - luck.LastKillAt < KillDedupeWindowMs)
            {
                ExpeditionDebug.Report(player, $"no loot roll for {target.Name}: the same kill was rolled {now - luck.LastKillAt} ms ago.");

                return false;
            }

            luck.LastTarget = target;
            luck.LastKillAt = now;

            if (minimum.HasValue)
            {
                luck.DryKills = 0;
                dropped = true;
            }
            else
            {
                int bandMin = plan?.MinLevel ?? 0;
                int bandMax = plan?.MaxLevel ?? 0;
                LootTuning tuning = LootTuning.FromProperties();
                int chance = KillDropChance(target.Level, player.Level, bandMin, bandMax, luck.DryKills, tuning);
                chance = Math.Clamp(ExpeditionBoons.AdjustLootChance(player, run, chance), 0, 100);
                bool counts = CountsForPity(target.Level, player.Level, bandMin, bandMax);
                int dryBefore = luck.DryKills;

                dropped = RollKill(Random.Shared, luck, chance, counts);
                ExpeditionDebug.Report(player, $"loot roll for {target.Name} (level {target.Level}, you {player.Level}, band {bandMin}-{bandMax}): chance {chance}%, counts for pity {counts}, dry kills {dryBefore}, dropped {dropped}. Settings: base {tuning.BaseChance}, pity step {tuning.PityStep}, guarantee {tuning.PityGuarantee}.");
            }
        }

        if (!dropped)
        {
            return false;
        }

        DropFor(player, target as GameNPC, minimum);

        return true;
    }

    /// <summary>
    /// The run's pity starts fresh when the tracker attaches (login, new run, new act). Called by
    /// <see cref="ObjectiveTracker.Attach"/>.
    /// </summary>
    public static void ResetLuck(GamePlayer player)
    {
        if (player != null && !string.IsNullOrEmpty(player.ObjectId))
        {
            Luck.TryRemove(player.ObjectId, out _);
        }
    }

    /// <summary>
    /// A monster: not a pet or companion (controlled brain), not a bot, not a player, not the act boss, and not a
    /// mob made never to drop loot (GameNPC.CanDropLoot, e.g. summoned boss adds).
    /// </summary>
    public static bool IsLootableKill(GameLiving target)
    {
        return target is GameNPC npc && npc.CanDropLoot && npc.Brain is not IControlledBrain && npc is not GameBot && npc is not ExpeditionBoss;
    }

    /// <summary>
    /// The drop chance in percent. Grey mobs (level below player level - 5) and mobs below the act band get the grey
    /// chance and no pity. Others get the base chance, a bonus for orange/red mobs (two levels or more above the
    /// player) and the pity step per kill without a drop; the kill that would be the guarantee-th dry one always drops.
    /// No band (bandMax &lt;= 0, outside runs) means every non-grey mob counts as in band.
    /// </summary>
    public static int KillDropChance(int mobLevel, int playerLevel, int bandMin, int bandMax, int dryKills, LootTuning tuning)
    {
        if (!CountsForPity(mobLevel, playerLevel, bandMin, bandMax))
        {
            return Math.Clamp(tuning.GreyChance, 0, 100);
        }

        if (tuning.PityGuarantee > 0 && dryKills >= tuning.PityGuarantee - 1)
        {
            return 100;
        }

        int chance = tuning.BaseChance + Math.Max(0, dryKills) * tuning.PityStep;

        if (mobLevel >= playerLevel + 2)
        {
            chance += tuning.HighConBonus;
        }

        return Math.Clamp(chance, 0, 100);
    }

    /// <summary>
    /// True for a kill that is not grey to the player: it gets the full chance and can raise and trigger the pity.
    /// Only the con colour decides, not the act band: at level 1 the band starts at 1 while blue mobs are level 0
    /// (owner 2026-10-23, "annoying lucradan (level 0)" rolled the grey 15%). The band parameters are kept for callers.
    /// </summary>
    public static bool CountsForPity(int mobLevel, int playerLevel, int bandMin, int bandMax)
    {
        return mobLevel >= playerLevel - 5;
    }

    /// <summary>Rolls one kill: a drop resets the luck; a counted kill without one raises it.</summary>
    public static bool RollKill(Random rng, LootLuck luck, int chance, bool countsForPity)
    {
        bool dropped = rng.Next(100) < chance;

        if (dropped)
        {
            luck.DryKills = 0;
        }
        else if (countsForPity)
        {
            luck.DryKills++;
        }

        return dropped;
    }

    #endregion

    #region Rarity and profiles

    /// <summary>
    /// One drop event's rarity. Normal events use 60/27/9/3/1, boss events 0/0/50/35/15. Rarities below
    /// <paramref name="floor"/> are removed and the rest keep their relative weights.
    /// </summary>
    public static eLootRarity RollRarity(Random rng, bool boss, eLootRarity floor = eLootRarity.Common)
    {
        int[] weights = boss ? BossWeights : BaseWeights;
        int total = 0;

        for (int i = (int) floor; i < weights.Length; i++)
        {
            total += weights[i];
        }

        if (total <= 0)
        {
            return floor;
        }

        int roll = rng.Next(total);

        for (int i = (int) floor; i < weights.Length; i++)
        {
            if (roll < weights[i])
            {
                return (eLootRarity) i;
            }

            roll -= weights[i];
        }

        return floor;
    }

    /// <summary>The floor of a boss drop event: Rare, and the last event Epic from act 3 and Unique on act 5.</summary>
    public static eLootRarity BossFloor(int index, int count, int act)
    {
        if (index < count - 1)
        {
            return eLootRarity.Rare;
        }

        if (act >= ActGenerator.ActCount)
        {
            return eLootRarity.Unique;
        }

        return act >= 3 ? eLootRarity.Epic : eLootRarity.Rare;
    }

    public static LootProfile ProfileFor(eLootRarity rarity)
    {
        return rarity switch
        {
            eLootRarity.Magic => new LootProfile(25, 90, 0, false, false),
            eLootRarity.Rare => new LootProfile(35, 95, 1, false, false),
            eLootRarity.Epic => new LootProfile(45, 99, 2, true, false),
            eLootRarity.Unique => new LootProfile(45, 100, 2, true, true),
            _ => new LootProfile(15, 0, 0, false, false)
        };
    }

    #endregion

    #region Names and rarity tags

    /// <summary>"[Common] ", "[Magic] ", "[Rare] " or "[Epic] " before the name; a unique keeps its own name. Never over <see cref="MaxNameLength"/>.</summary>
    public static string DecorateName(eLootRarity rarity, string name)
    {
        string prefix = rarity switch
        {
            eLootRarity.Common => "[Common] ",
            eLootRarity.Magic => "[Magic] ",
            eLootRarity.Rare => "[Rare] ",
            eLootRarity.Epic => "[Epic] ",
            _ => string.Empty
        };

        string full = prefix + (name ?? string.Empty).Trim();

        return full.Length > MaxNameLength ? full[..MaxNameLength].TrimEnd() : full;
    }

    public static bool HasRepeatedWord(string text)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!seen.Add(word))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The rarity of an Expedition item, read from the " | Expedition {rarity}" end of its description. False for any other item.</summary>
    public static bool TryGetRarity(DbInventoryItem item, out eLootRarity rarity)
    {
        rarity = eLootRarity.Common;

        if (item == null || item.Template == null)
        {
            return false;
        }

        return TryParseRarity(item.Description, out rarity);
    }

    /// <summary>The pure part of <see cref="TryGetRarity"/>: the rarity named after the last " | Expedition " of a description.</summary>
    public static bool TryParseRarity(string description, out eLootRarity rarity)
    {
        rarity = eLootRarity.Common;

        if (string.IsNullOrEmpty(description))
        {
            return false;
        }

        int at = description.LastIndexOf(RaritySuffix, StringComparison.Ordinal);

        if (at < 0)
        {
            return false;
        }

        string name = description[(at + RaritySuffix.Length)..].Trim();

        foreach (eLootRarity value in Enum.GetValues<eLootRarity>())
        {
            if (string.Equals(value.ToString(), name, StringComparison.Ordinal))
            {
                rarity = value;

                return true;
            }
        }

        return false;
    }

    #endregion

    #region Junk

    /// <summary>What a merchant pays for an item (GameMerchant.OnPlayerAppraise): price * count / pack size * sell ratio %, at least the price of a 1-copper item.</summary>
    public static long SellValue(long price, int count, int packSize, int sellRatio)
    {
        int itemCount = Math.Max(1, count);
        int pack = Math.Max(1, packSize);
        long value = price * itemCount / pack * sellRatio / 100;

        if (price == 1 && value == 0)
        {
            value = price * itemCount / pack;
        }

        return Math.Max(0, value);
    }

    /// <summary>/junk &lt;rarity&gt;: an item of that rarity or below is junk. Only Common, Magic and Rare can be junked; Epic and Unique never.</summary>
    public static bool IsJunk(eLootRarity itemRarity, eLootRarity threshold)
    {
        return threshold <= eLootRarity.Rare && itemRarity <= threshold;
    }

    /// <summary>/junk auto: a drop at or below the setting (Common or Magic only) is sold instead of bagged. No setting: never.</summary>
    public static bool ShouldAutoJunk(eLootRarity rarity, eLootRarity? setting)
    {
        return setting.HasValue && setting.Value <= eLootRarity.Magic && rarity <= setting.Value;
    }

    /// <summary>The stored /junk auto value ("common", "magic"; anything else is off).</summary>
    public static eLootRarity? ParseAutoJunk(string value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "common" => eLootRarity.Common,
            "magic" => eLootRarity.Magic,
            _ => null
        };
    }

    /// <summary>
    /// The player's /junk auto setting. It is saved per character as a DOLCharactersXCustomParam row (as BountyQuest
    /// saves its history) and cached in TempProperties, so the database is read once per login.
    /// </summary>
    public static eLootRarity? GetAutoJunk(GamePlayer player)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId))
        {
            return null;
        }

        string cached = player.TempProperties.GetProperty<string>(AutoJunkKey);

        if (cached != null)
        {
            return ParseAutoJunk(cached);
        }

        string value = "off";

        try
        {
            DbCoreCharacterXCustomParam record = DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
                DB.Column("DOLCharactersObjectId").IsEqualTo(player.ObjectId).And(DB.Column("KeyName").IsEqualTo(AutoJunkKey)));

            if (!string.IsNullOrWhiteSpace(record?.Value))
            {
                value = record.Value;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"[EXPEDITION] Could not read the /junk auto setting of {player.Name}.", ex);
        }

        player.TempProperties.SetProperty(AutoJunkKey, value);

        return ParseAutoJunk(value);
    }

    /// <summary>Saves the player's /junk auto setting (null: off). The in-memory value applies even if the save fails.</summary>
    public static void SetAutoJunk(GamePlayer player, eLootRarity? setting)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId))
        {
            return;
        }

        string value = setting switch
        {
            eLootRarity.Common => "common",
            eLootRarity.Magic => "magic",
            _ => "off"
        };

        player.TempProperties.SetProperty(AutoJunkKey, value);

        try
        {
            DbCoreCharacterXCustomParam record = DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
                DB.Column("DOLCharactersObjectId").IsEqualTo(player.ObjectId).And(DB.Column("KeyName").IsEqualTo(AutoJunkKey)));

            if (record == null)
            {
                GameServer.Database.AddObject(new DbCoreCharacterXCustomParam(player.ObjectId, AutoJunkKey, value));
            }
            else
            {
                record.Value = value;
                GameServer.Database.SaveObject(record);
            }
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Could not save the /junk auto setting of {player.Name}.", ex);
        }
    }

    #endregion

    #region Bosses and delivery

    /// <summary>The act boss's reward: expedition_boss_drops events, each Rare or better (see <see cref="BossFloor"/>).</summary>
    public static void BossDrops(GamePlayer player, GameNPC boss)
    {
        if (player == null)
        {
            return;
        }

        int count = Math.Max(1, Properties.EXPEDITION_BOSS_DROPS);
        ExpeditionRun run = ExpeditionManager.GetRun(player);
        int act = run?.Act ?? 1;

        for (int i = 0; i < count; i++)
        {
            eLootRarity rarity = RollRarity(Random.Shared, true, BossFloor(i, count, act));
            rarity = ExpeditionBoons.ShiftRolledRarity(run, rarity);

            Deliver(player, boss, rarity);
        }
    }

    /// <summary>
    /// Generates one item and gives it to the player (the ground when the backpack is full). <paramref name="forced"/>
    /// is the minimum rarity of the roll (null: a normal roll).
    /// </summary>
    public static void DropFor(GamePlayer player, GameNPC killed, eLootRarity? forced)
    {
        eLootRarity rarity = RollRarity(Random.Shared, false, forced ?? eLootRarity.Common);
        rarity = ExpeditionBoons.ShiftRolledRarity(ExpeditionManager.GetRun(player), rarity);

        Deliver(player, killed, rarity);
    }

    private static void Deliver(GamePlayer player, GameNPC killed, eLootRarity rarity)
    {
        if (player?.Client == null || player.CharacterClass == null)
        {
            return;
        }

        try
        {
            ExpeditionRun run = ExpeditionManager.GetRun(player);
            UniqueDefinition unique = null;
            GeneratedUniqueItem item;

            // Uniques are sized for the top of their tier; below level 10 they would dwarf everything else, so an early
            // Unique roll becomes an Epic until then.
            if (rarity == eLootRarity.Unique && player.Level < UniqueMinPlayerLevel)
            {
                rarity = eLootRarity.Epic;
            }

            if (rarity == eLootRarity.Unique)
            {
                unique = ExpeditionUniques.PickFor(player, run);
                item = ExpeditionUniques.Build(player, unique);
            }
            else
            {
                item = Generate(player, rarity);
            }

            if (run != null)
            {
                Interlocked.Increment(ref run.LootByRarity[(int) rarity]);
            }

            // /junk auto: the drop never reaches the bag; the player gets what a merchant would pay for it.
            if (Properties.EXPEDITION_AUTO_JUNK && ShouldAutoJunk(rarity, GetAutoJunk(player)))
            {
                long value = SellValue(item.Price, 1, 1, Properties.ITEM_SELL_RATIO);

                player.AddMoney(value);
                InventoryLogging.LogInventoryAction("(expedition auto-junk)", player, eInventoryActionType.Merchant, value);
                player.Out.SendMessage($"Sold a {item.Name} for {Money.GetString(value)}.", eChatType.CT_Loot, eChatLoc.CL_SystemWindow);

                return;
            }

            DbInventoryItem invItem = GameInventoryItem.Create(item);
            invItem.IsROG = true;

            if (killed != null)
            {
                invItem.Creator = killed.Name;
            }

            bool inBackpack = player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, invItem);

            if (!inBackpack)
            {
                player.CreateItemOnTheGround(invItem);
            }

            if (run != null && rarity >= eLootRarity.Rare)
            {
                run.AddLog($"Act {run.Act}: found {invItem.Name} ({rarity}).");
            }

            Announce(player, invItem.Name, rarity, inBackpack, unique?.Lore);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] A {rarity} drop could not be generated for {player.Name}.", ex);
        }
    }

    /// <summary>
    /// The ROG item for Common to Epic, built as AtlasROGManager.GenerateROG does (AtlasROGManager.cs:25): a
    /// GeneratedUniqueItem for the player's realm and class, AllowAdd so its ItemUnique row is added with the
    /// inventory item (the DbInventoryItem.IUWrapper relation), then raised to the rarity's quality, bonus and name.
    /// Uniques come from the fixed catalog instead (<see cref="ExpeditionUniques.Build"/>).
    /// </summary>
    private static GeneratedUniqueItem Generate(GamePlayer player, eLootRarity rarity)
    {
        LootProfile profile = ProfileFor(rarity);
        byte level = (byte) Math.Clamp(player.Level + profile.LevelBonus, 1, 51);
        int wantedLines = BonusLinesFor(rarity);
        GeneratedUniqueItem item = new(player.Realm, (eCharacterClass) player.CharacterClass.ID, level, profile.MinUtility);

        // The stock generator rolls a magical bonus only with a chance of 60% + level * 2, so at low levels a third of
        // its items are plain (owner 2026-10-23: a "[Rare] leather sleeves" with 0 utility). A rarity that promises
        // bonuses re-rolls until it has them, then a stat line is written as the last resort.
        for (int attempt = 0; attempt < 8 && CountBonusLines(item) < wantedLines; attempt++)
        {
            item = new GeneratedUniqueItem(player.Realm, (eCharacterClass) player.CharacterClass.ID, level, profile.MinUtility);
        }

        while (CountBonusLines(item) < wantedLines)
        {
            if (!AddStatLine(item))
            {
                break;
            }
        }

        item.AllowAdd = true;
        item.IsTradable = true;

        // The price follows the quality, as GeneratedUniqueItem.GenerateItemQuality sets it.
        if (item.Quality < profile.MinQuality)
        {
            item.Quality = profile.MinQuality;
            item.Price = Math.Max(2, Money.SetAutoPrice(item.Level, item.Quality) / 8);
        }

        if (profile.ExtraBonus)
        {
            AddExtraBonus(item);
        }

        // The stock generator lets any item reach 15 utility (about 22 stat points) and the rarity floors push higher,
        // so a level-2 drop could carry +21 Dexterity (owner 2026-10-23). Every line is held to what a character of the
        // item's level can use, and the item's total to a budget that grows with level and rarity.
        FitToLevel(item, item.Level, rarity);

        // Salvage looks for "Atlas ROG" in the description (Salvage.cs:708), so the rarity is appended to it; /junk reads it back (TryGetRarity).
        item.Description = $"{item.Description}{RaritySuffix}{rarity}";
        item.Name = DecorateName(rarity, item.Name);

        return item;
    }

    /// <summary>
    /// One more bonus line past the generator's utility cap: hit points (as the generator rolls them,
    /// GeneratedUniqueItem.GetBonusAmount: level/2..level), or constitution (ceil(level/3)/2..ceil(level/3)) when the
    /// item already has hit points. Written to the first empty line of Bonus1..Bonus10.
    /// </summary>
    private static void AddExtraBonus(GeneratedUniqueItem item)
    {
        int line = 0;
        bool hasHealth = false;
        bool hasConstitution = false;

        for (int i = 10; i >= 1; i--)
        {
            (int type, int value) = GetBonus(item, i);

            if (type == (int) eProperty.MaxHealth)
            {
                hasHealth = true;
            }

            if (type == (int) eProperty.Constitution)
            {
                hasConstitution = true;
            }

            if (type == 0 && value == 0)
            {
                line = i;
            }
        }

        if (line == 0 || (hasHealth && hasConstitution))
        {
            return;
        }

        int level = Math.Max(1, item.Level);

        if (!hasHealth)
        {
            SetBonus(item, line, eProperty.MaxHealth, Util.Random((int) Math.Ceiling(level / 2.0), level));

            return;
        }

        int max = (int) Math.Ceiling(level / 3.0);
        SetBonus(item, line, eProperty.Constitution, Util.Random((int) Math.Ceiling(max / 2.0), max));
    }

    /// <summary>
    /// During a run, stock gear from a monster's loot tables (anything that goes in an equipment slot: weapons, armor,
    /// jewelry) is not dropped, so every piece of equipment found comes from Expedition loot with a rarity (owner
    /// 2026-10-23: "loot coming through without a rarity"). Money is handled before this; quest items, crafting
    /// materials and trophies are not equipment and still drop. The killer may be the player's pet or companion.
    /// Off with the server setting expedition_replace_stock_gear.
    /// </summary>
    public static bool SuppressStockGear(GameObject killer, DbItemTemplate item)
    {
        if (!Properties.EXPEDITION_REPLACE_STOCK_GEAR || item == null || !IsEquipment(item))
        {
            return false;
        }

        GamePlayer owner = killer as GamePlayer;

        if (owner == null && killer is GameNPC npc && npc.Brain is IControlledBrain brain)
        {
            owner = brain.GetPlayerOwner();
        }

        return owner != null && ExpeditionManager.GetRun(owner) != null;
    }

    /// <summary>True for an item that goes in an equipment slot (eInventorySlot.MinEquipable..MaxEquipable).</summary>
    public static bool IsEquipment(DbItemTemplate item)
    {
        return item.Item_Type >= (int) eInventorySlot.MinEquipable && item.Item_Type <= (int) eInventorySlot.MaxEquipable;
    }

    /// <summary>The player level from which a Unique roll gives a catalog unique (below it, an Epic).</summary>
    public const int UniqueMinPlayerLevel = 10;

    /// <summary>
    /// The item's total bonus utility (stats 2/3 per point, hit points 1/4, power 2, resists 2, skills 5, all-skills 10,
    /// as GeneratedUniqueItem counts it) allowed at an item level: about level x 0.45 for Common up to x 0.9 for Epic,
    /// never below 1. At level 50 that is roughly 22 / 30 / 38 / 46 utility; at level 2, one or two points of stat.
    /// </summary>
    public static double UtilityBudget(int itemLevel, eLootRarity rarity)
    {
        double factor = rarity switch
        {
            eLootRarity.Magic => 0.6,
            eLootRarity.Rare => 0.75,
            eLootRarity.Epic => 0.9,
            eLootRarity.Unique => 0.9,
            _ => 0.45
        };

        return Math.Max(1.0, itemLevel * factor);
    }

    public static double LineUtility(eProperty property, int value)
    {
        if (value == 0)
        {
            return 0.0;
        }

        if ((property >= eProperty.Stat_First && property <= eProperty.Stat_Last) || property == eProperty.Acuity)
        {
            return value * 2 / 3.0;
        }

        if (property == eProperty.MaxHealth)
        {
            return value * 0.25;
        }

        if (property == eProperty.MaxMana || (property >= eProperty.Resist_First && property <= eProperty.Resist_Last))
        {
            return value * 2.0;
        }

        if (property >= eProperty.Skill_First && property <= eProperty.Skill_Last)
        {
            return value * 5.0;
        }

        if (property is eProperty.AllMagicSkills or eProperty.AllMeleeWeaponSkills or eProperty.AllDualWieldingSkills or eProperty.AllArcherySkills or eProperty.AllSkills)
        {
            return value * 10.0;
        }

        return 0.0;
    }

    /// <summary>
    /// Holds every bonus line to the cap a character of <paramref name="itemLevel"/> can use (ExpeditionUniques.LineCap;
    /// stat and all-stat lines included), then, while the total is over <see cref="UtilityBudget"/>, lowers the
    /// biggest line by one point. Lines never drop below 1 and are never removed, so the rarity's promised lines stay.
    /// </summary>
    public static void FitToLevel(DbItemTemplate item, int itemLevel, eLootRarity rarity)
    {
        int level = Math.Max(1, itemLevel);

        for (int i = 1; i <= 10; i++)
        {
            (int type, int value) = GetBonus(item, i);

            if (type == 0 || value <= 0)
            {
                continue;
            }

            eProperty property = (eProperty) type;
            int cap = property == eProperty.Acuity ? ExpeditionUniques.LineCap(eProperty.Strength, level) : ExpeditionUniques.LineCap(property, level);

            if (cap > 0 && value > cap)
            {
                SetBonus(item, i, property, Math.Max(1, cap));
            }
        }

        double budget = UtilityBudget(level, rarity);

        for (int guard = 0; guard < 500; guard++)
        {
            double total = 0.0;
            int biggestLine = 0;
            double biggestUtility = 0.0;

            for (int i = 1; i <= 10; i++)
            {
                (int type, int value) = GetBonus(item, i);
                double utility = LineUtility((eProperty) type, value);
                total += utility;

                if (value > 1 && utility > biggestUtility)
                {
                    biggestUtility = utility;
                    biggestLine = i;
                }
            }

            if (total <= budget || biggestLine == 0)
            {
                return;
            }

            (int lineType, int lineValue) = GetBonus(item, biggestLine);
            int step = (eProperty) lineType == eProperty.MaxHealth ? Math.Max(1, lineValue / 10) : 1;
            SetBonus(item, biggestLine, (eProperty) lineType, Math.Max(1, lineValue - step));
        }
    }

    /// <summary>How many bonus lines a rarity promises: Magic at least one, Rare and above at least two.</summary>
    public static int BonusLinesFor(eLootRarity rarity)
    {
        return rarity switch
        {
            eLootRarity.Common => 0,
            eLootRarity.Magic => 1,
            _ => 2
        };
    }

    public static int CountBonusLines(DbItemTemplate item)
    {
        int count = 0;

        for (int i = 1; i <= 10; i++)
        {
            (int type, int value) = GetBonus(item, i);

            if (type != 0 && value != 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Writes one stat line the item lacks (Strength, Constitution, Dexterity or Quickness, in that order), sized like
    /// the generator's own stat rolls at this level (about level/3, at least 1). False when every line is taken.
    /// </summary>
    private static bool AddStatLine(DbItemTemplate item)
    {
        int line = 0;

        for (int i = 10; i >= 1; i--)
        {
            (int type, int value) = GetBonus(item, i);

            if (type == 0 && value == 0)
            {
                line = i;
            }
        }

        if (line == 0)
        {
            return false;
        }

        foreach (eProperty stat in new[] { eProperty.Strength, eProperty.Constitution, eProperty.Dexterity, eProperty.Quickness })
        {
            bool taken = false;

            for (int i = 1; i <= 10; i++)
            {
                if (GetBonus(item, i).type == (int) stat)
                {
                    taken = true;

                    break;
                }
            }

            if (taken)
            {
                continue;
            }

            int level = Math.Max(1, item.Level);
            int amount = Math.Max(1, (int) Math.Ceiling(level / 3.0));
            SetBonus(item, line, stat, Util.Random(Math.Max(1, amount / 2), amount));

            return true;
        }

        return false;
    }

    internal static (int type, int value) GetBonus(DbItemTemplate item, int line)
    {
        return line switch
        {
            1 => (item.Bonus1Type, item.Bonus1),
            2 => (item.Bonus2Type, item.Bonus2),
            3 => (item.Bonus3Type, item.Bonus3),
            4 => (item.Bonus4Type, item.Bonus4),
            5 => (item.Bonus5Type, item.Bonus5),
            6 => (item.Bonus6Type, item.Bonus6),
            7 => (item.Bonus7Type, item.Bonus7),
            8 => (item.Bonus8Type, item.Bonus8),
            9 => (item.Bonus9Type, item.Bonus9),
            10 => (item.Bonus10Type, item.Bonus10),
            _ => (0, 0)
        };
    }

    internal static void SetBonus(DbItemTemplate item, int line, eProperty property, int value)
    {
        switch (line)
        {
            case 1: item.Bonus1Type = (int) property; item.Bonus1 = value; break;
            case 2: item.Bonus2Type = (int) property; item.Bonus2 = value; break;
            case 3: item.Bonus3Type = (int) property; item.Bonus3 = value; break;
            case 4: item.Bonus4Type = (int) property; item.Bonus4 = value; break;
            case 5: item.Bonus5Type = (int) property; item.Bonus5 = value; break;
            case 6: item.Bonus6Type = (int) property; item.Bonus6 = value; break;
            case 7: item.Bonus7Type = (int) property; item.Bonus7 = value; break;
            case 8: item.Bonus8Type = (int) property; item.Bonus8 = value; break;
            case 9: item.Bonus9Type = (int) property; item.Bonus9 = value; break;
            case 10: item.Bonus10Type = (int) property; item.Bonus10 = value; break;
        }
    }

    /// <summary>The drop's chat line; a unique also gets the screen-centre line, its lore and the glow.</summary>
    private static void Announce(GamePlayer player, string name, eLootRarity rarity, bool inBackpack, string lore = null)
    {
        string where = inBackpack ? string.Empty : " Your backpack is full: it lies on the ground.";

        if (rarity < eLootRarity.Rare)
        {
            player.Out.SendMessage($"You find {name}.{where}", eChatType.CT_Loot, eChatLoc.CL_SystemWindow);

            return;
        }

        string flourish = rarity switch
        {
            eLootRarity.Rare => "A rare item!",
            eLootRarity.Epic => "An epic item!",
            _ => "A UNIQUE treasure!"
        };

        string text = $"{flourish} You find {name}.{where}";

        if (rarity == eLootRarity.Unique)
        {
            player.Out.SendMessage(text, eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);

            if (!string.IsNullOrWhiteSpace(lore))
            {
                player.Out.SendMessage(lore, eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
            }

            player.Out.SendSpellEffectAnimation(player, player, UniqueGlowEffect, 0, false, 1);
        }

        player.Out.SendMessage(text, eChatType.CT_Important, eChatLoc.CL_SystemWindow);

        if (rarity == eLootRarity.Unique && !string.IsNullOrWhiteSpace(lore))
        {
            player.Out.SendMessage($"\"{lore}\"", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }
    }

    #endregion
}
