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

/// <summary>How one rarity drives the random-object generator (<see cref="GeneratedUniqueItem"/>).</summary>
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

    private static readonly string[] UniquePrefixes =
    {
        "Mistwarden's", "Stormcaller's", "Ashborn's", "Gravewalker's", "Thornking's", "Duskweaver's", "Ironhowl's",
        "Frostmantle's", "Emberheart's", "Wyrmbane's", "Oathkeeper's", "Moonhollow's", "Sunderer's", "Cairnwatcher's",
        "Briarwitch's", "Seawrath's", "Ravenmoor's", "Glimmerfell's"
    };

    private static readonly string[] UniqueSuffixes =
    {
        "Lough", "Drowned Hills", "Barrow Kings", "Last Ember", "Silent Fen", "Broken Oath", "Pale Tide",
        "Hollow Crown", "Northern Gale", "Sunken Isle", "Wild Hunt", "Sleeping Giant", "Ashen Dawn", "Restless Dead",
        "Mists", "Starless Deep"
    };

    #region Server hook (outside runs)

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.AddHandler(GameLivingEvent.EnemyKilled, new DOLEventHandler(OnEnemyKilledOutsideRuns));
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
            return false;
        }

        LootLuck luck = Luck.GetOrAdd(player.ObjectId, _ => new LootLuck());
        ExpeditionRun run = ExpeditionManager.GetRun(player);
        ActPlan plan = run?.CurrentAct;
        long now = GameLoop.GameLoopTime;
        bool dropped;

        lock (luck)
        {
            // GamePlayer.EnemyKilled and GameLiving.ProcessDeath can both report one kill; a respawn of the same mob later is a new kill.
            if (luck.LastTarget == target && now - luck.LastKillAt < KillDedupeWindowMs)
            {
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
                bool counts = CountsForPity(target.Level, player.Level, bandMin, bandMax);

                dropped = RollKill(Random.Shared, luck, chance, counts);
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

    /// <summary>True for a kill that is not grey and not below the act band: it can raise and trigger the pity.</summary>
    public static bool CountsForPity(int mobLevel, int playerLevel, int bandMin, int bandMax)
    {
        if (mobLevel < playerLevel - 5)
        {
            return false;
        }

        return bandMax <= 0 || mobLevel >= bandMin;
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

    #region Names

    /// <summary>"[Magic] ", "[Rare] " or "[Epic] " before the name; Common and Unique names are left as they are. Never over <see cref="MaxNameLength"/>.</summary>
    public static string DecorateName(eLootRarity rarity, string name)
    {
        string prefix = rarity switch
        {
            eLootRarity.Magic => "[Magic] ",
            eLootRarity.Rare => "[Rare] ",
            eLootRarity.Epic => "[Epic] ",
            _ => string.Empty
        };

        string full = prefix + (name ?? string.Empty).Trim();

        return full.Length > MaxNameLength ? full[..MaxNameLength].TrimEnd() : full;
    }

    /// <summary>
    /// A unique's name: "Prefix's Noun of the Suffix" from the word banks, or now and then "Prefix's Noun of Zone".
    /// Never empty, never a repeated word, never over <see cref="MaxNameLength"/>.
    /// </summary>
    public static string GenerateUniqueName(Random rng, string noun, string zoneName)
    {
        noun = string.IsNullOrWhiteSpace(noun) ? "Relic" : noun.Trim();
        string head = $"{UniquePrefixes[rng.Next(UniquePrefixes.Length)]} {noun}";

        if (!string.IsNullOrWhiteSpace(zoneName) && rng.Next(4) == 0)
        {
            string zoned = $"{head} of {zoneName.Trim()}";

            if (zoned.Length <= MaxNameLength && !HasRepeatedWord(zoned))
            {
                return zoned;
            }
        }

        string name = $"{head} of the {UniqueSuffixes[rng.Next(UniqueSuffixes.Length)]}";

        if (name.Length <= MaxNameLength && !HasRepeatedWord(name))
        {
            return name;
        }

        return head.Length > MaxNameLength ? head[..MaxNameLength].TrimEnd() : head;
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

    /// <summary>The noun a unique is named after, from the generated item's object type and slot.</summary>
    public static string TypeNoun(eObjectType type, eInventorySlot slot)
    {
        if (type == eObjectType.Magical)
        {
            return slot switch
            {
                eInventorySlot.Cloak => "Cloak",
                eInventorySlot.Neck => "Amulet",
                eInventorySlot.Waist => "Belt",
                eInventorySlot.LeftBracer or eInventorySlot.RightBracer => "Bracer",
                eInventorySlot.LeftRing or eInventorySlot.RightRing => "Ring",
                eInventorySlot.Jewelry => "Gem",
                _ => "Charm"
            };
        }

        if (type >= eObjectType._FirstArmor && type <= eObjectType._LastArmor)
        {
            return slot switch
            {
                eInventorySlot.HeadArmor => "Helm",
                eInventorySlot.HandsArmor => "Gauntlets",
                eInventorySlot.FeetArmor => "Boots",
                eInventorySlot.TorsoArmor => type == eObjectType.Cloth ? "Robe" : "Hauberk",
                eInventorySlot.LegsArmor => "Leggings",
                eInventorySlot.ArmsArmor => "Sleeves",
                _ => "Armor"
            };
        }

        return type switch
        {
            eObjectType.Shield => "Shield",
            eObjectType.Instrument => "Lyre",
            eObjectType.Staff or eObjectType.MaulerStaff => "Staff",
            eObjectType.Longbow or eObjectType.CompositeBow or eObjectType.RecurvedBow or eObjectType.Fired => "Bow",
            eObjectType.Crossbow => "Crossbow",
            eObjectType.Axe or eObjectType.LeftAxe => "Axe",
            eObjectType.Hammer => "Hammer",
            eObjectType.CrushingWeapon => "Mace",
            eObjectType.Blunt => "Club",
            eObjectType.Sword or eObjectType.SlashingWeapon or eObjectType.Blades => "Blade",
            eObjectType.ThrustWeapon or eObjectType.Piercing => "Dagger",
            eObjectType.TwoHandedWeapon or eObjectType.LargeWeapons => "Greatsword",
            eObjectType.PolearmWeapon => "Halberd",
            eObjectType.Spear or eObjectType.CelticSpear => "Spear",
            eObjectType.Scythe => "Scythe",
            eObjectType.Flexible => "Whip",
            eObjectType.HandToHand or eObjectType.FistWraps => "Claws",
            eObjectType.Thrown => "Javelin",
            _ => "Relic"
        };
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
        int act = ExpeditionManager.GetRun(player)?.Act ?? 1;

        for (int i = 0; i < count; i++)
        {
            eLootRarity rarity = RollRarity(Random.Shared, true, BossFloor(i, count, act));

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
            GeneratedUniqueItem item = Generate(player, rarity, run?.CurrentAct?.ZoneName);
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

            if (run != null)
            {
                Interlocked.Increment(ref run.LootByRarity[(int) rarity]);

                if (rarity >= eLootRarity.Rare)
                {
                    run.AddLog($"Act {run.Act}: found {invItem.Name} ({rarity}).");
                }
            }

            Announce(player, invItem.Name, rarity, inBackpack);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] A {rarity} drop could not be generated for {player.Name}.", ex);
        }
    }

    /// <summary>
    /// The ROG item for a rarity, built as AtlasROGManager.GenerateROG does (AtlasROGManager.cs:25): a
    /// GeneratedUniqueItem for the player's realm and class, AllowAdd so its ItemUnique row is added with the
    /// inventory item (the DbInventoryItem.IUWrapper relation), then raised to the rarity's quality, bonus and name.
    /// </summary>
    private static GeneratedUniqueItem Generate(GamePlayer player, eLootRarity rarity, string zoneName)
    {
        LootProfile profile = ProfileFor(rarity);
        byte level = (byte) Math.Clamp(player.Level + profile.LevelBonus, 1, 51);
        GeneratedUniqueItem item = new(player.Realm, (eCharacterClass) player.CharacterClass.ID, level, profile.MinUtility);
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

        // Salvage looks for "Atlas ROG" in the description (Salvage.cs:708), so the rarity is appended to it.
        item.Description = $"{item.Description} | Expedition {rarity}";

        if (profile.UniqueName)
        {
            item.Name = GenerateUniqueName(Random.Shared, TypeNoun((eObjectType) item.Object_Type, (eInventorySlot) item.Item_Type), zoneName);
        }
        else
        {
            item.Name = DecorateName(rarity, item.Name);
        }

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

    private static (int type, int value) GetBonus(DbItemTemplate item, int line)
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

    private static void SetBonus(DbItemTemplate item, int line, eProperty property, int value)
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

    private static void Announce(GamePlayer player, string name, eLootRarity rarity, bool inBackpack)
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
            player.Out.SendSpellEffectAnimation(player, player, UniqueGlowEffect, 0, false, 1);
        }

        player.Out.SendMessage(text, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    #endregion
}
