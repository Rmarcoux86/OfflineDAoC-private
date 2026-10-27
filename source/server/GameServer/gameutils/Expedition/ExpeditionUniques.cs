using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>The level band a unique belongs to: Early (levels 1-19), Mid (20-39), Late (40+).</summary>
public enum eUniqueTier
{
    Early = 0,
    Mid = 1,
    Late = 2
}

/// <summary>
/// Where a unique is worn. The jewelry slots fit every class; Chest, Helm and Weapon are turned into the finder's own
/// armor or weapon type when the unique drops (<see cref="ExpeditionUniques.Build"/>).
/// </summary>
public enum eUniqueSlot
{
    Necklace,
    Cloak,
    Jewel,
    Belt,
    Ring,
    Bracer,
    Chest,
    Helm,
    Weapon
}

/// <summary>The kind of proc a unique carries, as the stock generator rolls them (GeneratedUniqueItem.GenerateProc).</summary>
public enum eUniqueProc
{
    None,
    Lifetap,
    Damage,
    Heal,
    Absorb
}

public readonly record struct UniqueLine(eProperty Property, int Value);

/// <summary>One hand-made unique: a fixed name, lore line, slot and bonus lines.</summary>
public sealed record UniqueDefinition(string Name, eUniqueTier Tier, eUniqueSlot Slot, string Lore, eUniqueProc Proc, UniqueLine[] Lines);

/// <summary>
/// Expedition uniques, Diablo 2 style: a fixed catalog of named items with fixed bonuses, six per level tier. A Unique
/// drop picks from the tier that matches the finder's level and skips the uniques already found on the run until the
/// whole tier was found.
/// <para>
/// The bonuses are sized a bit under the item caps of the tier's top level (stats level * 1.5, StatCalculator.cs:157;
/// hit points level * 4, MaxHealthCalculator.cs:153; resists level / 2 + 1, ResistCalculator.cs:99; skills level / 5 + 1,
/// SkillLevelCalculator.cs:35). Below that level the server simply clamps the total item bonus to the player's own cap,
/// so a unique found early grows with its owner.
/// </para>
/// </summary>
public static class ExpeditionUniques
{
    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    public const int LinesMin = 4;
    public const int LinesMax = 6;

    /// <summary>The proc chance the stock generator gives its procs (GeneratedUniqueItem.GenerateProc).</summary>
    private const byte ProcChance = 10;

    /// <summary>
    /// Unique names found per run (key: <see cref="RunKey"/>). Kept in memory only, so it resets on a server restart:
    /// a unique found before the restart can then drop once more on the same run.
    /// </summary>
    private static readonly ConcurrentDictionary<string, HashSet<string>> FoundByRun = new(StringComparer.Ordinal);

    public static readonly IReadOnlyList<UniqueDefinition> Catalog = new UniqueDefinition[]
    {
        // Early: usable from level 1, sized for level 20 (caps: stats 30, hit points 80, resists 11, skills 5).
        new("Torc of the Barrow Kings", eUniqueTier.Early, eUniqueSlot.Necklace,
            "Pried from a cairn in the drowned hills. On still nights the dead kings can be heard arguing over it.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 24), new(eProperty.MaxHealth, 60), new(eProperty.Resist_Body, 9), new(eProperty.Resist_Spirit, 9), new(eProperty.AllSkills, 3) }),
        new("Mantle of the Grey Lough", eUniqueTier.Early, eUniqueSlot.Cloak,
            "Woven from lake mist by a fisher-witch who never once got wet.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Dexterity, 22), new(eProperty.Quickness, 22), new(eProperty.Resist_Cold, 10), new(eProperty.Resist_Heat, 9) }),
        new("Hearthstone Band", eUniqueTier.Early, eUniqueSlot.Ring,
            "Its stone stays warm, as if a fire still burned in the house it came from.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Strength, 22), new(eProperty.Constitution, 20), new(eProperty.MaxHealth, 50), new(eProperty.Resist_Crush, 9) }),
        new("Wolfsinew Cuff", eUniqueTier.Early, eUniqueSlot.Bracer,
            "Bound with the sinew of the pack leader that hunted the border roads for nine winters.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Strength, 24), new(eProperty.Dexterity, 20), new(eProperty.Resist_Slash, 9), new(eProperty.Resist_Thrust, 9) }),
        new("Girdle of the Lantern-Bearer", eUniqueTier.Early, eUniqueSlot.Belt,
            "Worn by the guide who led travellers through the bog and always came back alone.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 22), new(eProperty.Quickness, 15), new(eProperty.MaxHealth, 70), new(eProperty.Resist_Energy, 9), new(eProperty.Resist_Matter, 9) }),
        new("Fenwatch Fang", eUniqueTier.Early, eUniqueSlot.Weapon,
            "The marsh wardens' last weapon. It drinks a little of whatever it cuts.",
            eUniqueProc.Lifetap,
            new UniqueLine[] { new(eProperty.Strength, 20), new(eProperty.Dexterity, 20), new(eProperty.Quickness, 18), new(eProperty.MaxHealth, 50), new(eProperty.AllSkills, 3) }),

        // Mid: usable from level 20, level 40 caps: stats 60, hit points 160, resists 21, skills 9. Halved (owner 2026-10-25:
        // "the values seem kind of high"): each line about half of what the caps allow.
        new("Eye of the Sleeping Giant", eUniqueTier.Mid, eUniqueSlot.Jewel,
            "Chipped from the brow of a stone giant who has not woken in a thousand years. Yet.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 23), new(eProperty.MaxHealth, 65), new(eProperty.Resist_Crush, 8), new(eProperty.Resist_Slash, 8), new(eProperty.AllSkills, 3) }),
        new("Oathbinder's Signet", eUniqueTier.Mid, eUniqueSlot.Ring,
            "Every oath sworn on this ring was kept. The ones who broke them were not.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Strength, 21), new(eProperty.Dexterity, 20), new(eProperty.Resist_Thrust, 8), new(eProperty.AllSkills, 3) }),
        new("Stormcaller's Wristguard", eUniqueTier.Mid, eUniqueSlot.Bracer,
            "The air crackles around the wearer's hand, and gulls flee before them.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Quickness, 22), new(eProperty.Dexterity, 19), new(eProperty.Resist_Energy, 9), new(eProperty.Resist_Cold, 8) }),
        new("Shroud of the Pale Tide", eUniqueTier.Mid, eUniqueSlot.Cloak,
            "Drawn up from the sea on the night the tide came in white and took the coast villages.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 20), new(eProperty.MaxHealth, 60), new(eProperty.Resist_Spirit, 9), new(eProperty.Resist_Body, 8), new(eProperty.Resist_Heat, 8) }),
        new("Choker of the Wild Hunt", eUniqueTier.Mid, eUniqueSlot.Necklace,
            "Hear the horns once and you join the hunt. Hear them twice and you lead it.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Strength, 23), new(eProperty.Quickness, 20), new(eProperty.MaxHealth, 50), new(eProperty.Resist_Matter, 8), new(eProperty.AllSkills, 3) }),
        new("Crown of the Cairnwatcher", eUniqueTier.Mid, eUniqueSlot.Helm,
            "The watcher on the cairn never slept. Its helm still keeps one eye open.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 22), new(eProperty.Dexterity, 18), new(eProperty.MaxHealth, 55), new(eProperty.Resist_Body, 8), new(eProperty.AllSkills, 3) }),

        // Late: usable from level 40, sized for level 50 (caps: stats 75, hit points 200, resists 26, skills 11).
        new("Heartwood of the First Grove", eUniqueTier.Late, eUniqueSlot.Chest,
            "Grown, not forged, from the oldest tree in the world. It mends what strikes it.",
            eUniqueProc.Heal,
            new UniqueLine[] { new(eProperty.Strength, 55), new(eProperty.Constitution, 60), new(eProperty.MaxHealth, 170), new(eProperty.Resist_Crush, 22), new(eProperty.Resist_Slash, 22), new(eProperty.AllSkills, 8) }),
        new("Wyrmbane, the Last Ember", eUniqueTier.Late, eUniqueSlot.Weapon,
            "Quenched in a dragon's heart. The ember in its core has burned for three ages.",
            eUniqueProc.Damage,
            new UniqueLine[] { new(eProperty.Strength, 60), new(eProperty.Dexterity, 55), new(eProperty.Quickness, 50), new(eProperty.MaxHealth, 150), new(eProperty.Resist_Heat, 22), new(eProperty.AllSkills, 9) }),
        new("Pendant of the Starless Deep", eUniqueTier.Late, eUniqueSlot.Necklace,
            "Brought up from a sea with no bottom and no stars. It is always cold to the touch.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 62), new(eProperty.MaxHealth, 180), new(eProperty.Resist_Cold, 23), new(eProperty.Resist_Spirit, 23), new(eProperty.AllSkills, 8) }),
        new("Ring of the Hollow Crown", eUniqueTier.Late, eUniqueSlot.Ring,
            "All that remains of a king who traded his kingdom for a longer life.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Strength, 58), new(eProperty.Quickness, 55), new(eProperty.Resist_Energy, 22), new(eProperty.Resist_Matter, 22), new(eProperty.AllSkills, 8) }),
        new("Ashen Dawn Gem", eUniqueTier.Late, eUniqueSlot.Jewel,
            "It caught the first light of the morning the old world burned, and never let it go.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Dexterity, 60), new(eProperty.Constitution, 52), new(eProperty.MaxHealth, 160), new(eProperty.Resist_Thrust, 23), new(eProperty.Resist_Body, 22) }),
        new("Belt of the Restless Dead", eUniqueTier.Late, eUniqueSlot.Belt,
            "Every buckle is a finger bone. They tap, softly, when danger is near.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Strength, 52), new(eProperty.Constitution, 52), new(eProperty.Quickness, 48), new(eProperty.MaxHealth, 160), new(eProperty.Resist_Spirit, 22), new(eProperty.AllSkills, 7) })
    };

    #region Tiers and caps

    /// <summary>The tier for a finder's level: Early below 20, Mid from 20 to 39, Late from 40.</summary>
    public static eUniqueTier TierFor(int level)
    {
        if (level < 20)
        {
            return eUniqueTier.Early;
        }

        return level < 40 ? eUniqueTier.Mid : eUniqueTier.Late;
    }

    /// <summary>The lowest level of a tier: the item's level for jewelry, so every finder of that tier can wear it.</summary>
    public static int TierMinLevel(eUniqueTier tier)
    {
        return tier switch
        {
            eUniqueTier.Mid => 20,
            eUniqueTier.Late => 40,
            _ => 1
        };
    }

    /// <summary>The level a tier's bonuses are sized for.</summary>
    public static int TierCapLevel(eUniqueTier tier)
    {
        return tier switch
        {
            eUniqueTier.Mid => 40,
            eUniqueTier.Late => 50,
            _ => 20
        };
    }

    /// <summary>
    /// The total item bonus a player of <paramref name="level"/> can use for a property, as the property calculators
    /// cap it (without cap-increase bonuses). 0 for a property the catalog does not use.
    /// </summary>
    public static int LineCap(eProperty property, int level)
    {
        if (property >= eProperty.Stat_First && property <= eProperty.Stat_Last)
        {
            return (int) (level * 1.5);
        }

        if (property == eProperty.MaxHealth)
        {
            return level * 4;
        }

        if (property >= eProperty.Resist_First && property <= eProperty.Resist_Last)
        {
            return level / 2 + 1;
        }

        if (property == eProperty.AllSkills || (property >= eProperty.Skill_First && property <= eProperty.Skill_Last))
        {
            return level / 5 + 1;
        }

        return 0;
    }

    public static List<UniqueDefinition> InTier(eUniqueTier tier)
    {
        List<UniqueDefinition> list = new();

        foreach (UniqueDefinition definition in Catalog)
        {
            if (definition.Tier == tier)
            {
                list.Add(definition);
            }
        }

        return list;
    }

    #endregion

    #region Selection

    /// <summary>
    /// Picks a unique of the tier that is not in <paramref name="found"/> and adds its name there. When every unique of
    /// the tier was found, any of them can come again.
    /// </summary>
    public static UniqueDefinition Pick(Random rng, eUniqueTier tier, ISet<string> found)
    {
        List<UniqueDefinition> all = InTier(tier);
        List<UniqueDefinition> fresh = new();

        foreach (UniqueDefinition definition in all)
        {
            if (found == null || !found.Contains(definition.Name))
            {
                fresh.Add(definition);
            }
        }

        List<UniqueDefinition> pool = fresh.Count > 0 ? fresh : all;
        UniqueDefinition picked = pool[rng.Next(pool.Count)];
        found?.Add(picked.Name);

        return picked;
    }

    /// <summary>The run's key for the found-uniques list: the character and the run's start, or the player outside runs.</summary>
    public static string RunKey(ExpeditionRun run, GamePlayer player)
    {
        if (run != null)
        {
            return $"{run.CharacterId}:{run.StartedUtc.Ticks}";
        }

        return $"{player?.ObjectId}:outside";
    }

    /// <summary>Picks the player's unique for this drop and remembers it as found on the run.</summary>
    public const string ThreePointOhRingName = "Three Point Oh's Ring";

    /// <summary>How much of each attribute Three Point Oh's Ring gives (owner, 2026-10-24: +1, then +5, then +3).</summary>
    public const int ThreePointOhRingBonus = 3;

    /// <summary>How much of each resist Three Point Oh's Ring gives. Applied in code while worn (the item has no room).</summary>
    public const int ThreePointOhRingResistBonus = 3;

    /// <summary>The nine resists Three Point Oh's Ring raises while worn.</summary>
    public static readonly eProperty[] ThreePointOhRingResists =
    {
        eProperty.Resist_Body, eProperty.Resist_Cold, eProperty.Resist_Energy, eProperty.Resist_Heat, eProperty.Resist_Matter,
        eProperty.Resist_Spirit, eProperty.Resist_Crush, eProperty.Resist_Slash, eProperty.Resist_Thrust
    };

    private static readonly string ThreePointOhRingLore = $"\"You want this ring? It's got...\" +{ThreePointOhRingResistBonus} to all resistances while worn";

    /// <summary>The ring's full description, as the drop and the login update write it.</summary>
    public static readonly string ThreePointOhRingDescription = $"{ThreePointOhRingLore} | Atlas ROG | Expedition {eLootRarity.Unique}";

    public static bool IsThreePointOhRing(DbInventoryItem item)
    {
        return item != null && item.Name == ThreePointOhRingName;
    }

    /// <summary>
    /// Adds (sign 1) or removes (sign -1) the ring's resists. Called from GamePlayer's equip, unequip and refresh of item
    /// bonuses, next to where the item's own bonus lines are applied.
    /// </summary>
    public static void ApplyThreePointOhRingResists(GamePlayer player, DbInventoryItem item, int sign)
    {
        if (player == null || !IsThreePointOhRing(item))
        {
            return;
        }

        foreach (eProperty resist in ThreePointOhRingResists)
        {
            player.ItemBonus[resist] += sign * ThreePointOhRingResistBonus;
        }
    }

    /// <summary>
    /// Brings rings picked up before a change up to date: sets the eight attribute lines to the current bonus and the
    /// description to the current text. A worn ring is taken off and put back on around the change so the player's
    /// bonuses stay right. Runs at login for every player.
    /// </summary>
    public static void UpdateOldThreePointOhRings(GamePlayer player)
    {
        if (player?.Inventory == null)
        {
            return;
        }

        foreach (DbInventoryItem item in player.Inventory.AllItems.ToList())
        {
            if (!IsThreePointOhRing(item) || IsCurrentThreePointOhRing(item))
            {
                continue;
            }

            bool worn = item.SlotPosition >= (int) eInventorySlot.MinEquipable && item.SlotPosition <= (int) eInventorySlot.MaxEquipable;
            eInventorySlot slot = (eInventorySlot) item.SlotPosition;

            if (worn)
            {
                player.OnItemUnequipped(item, slot);
            }

            for (int i = 0; i < ThreePointOhRingStats.Length; i++)
            {
                ExpeditionLoot.SetBonus(item.Template, i + 1, ThreePointOhRingStats[i], ThreePointOhRingBonus);
            }

            item.Template.Description = ThreePointOhRingDescription;

            if (worn)
            {
                player.OnItemEquipped(item, slot);
            }

            try
            {
                GameInventoryObjectExtensions.SaveItem(item);

                if (item.Template is DbItemUnique unique)
                {
                    GameServer.Database.SaveObject(unique);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[EXPEDITION] Could not save the updated {ThreePointOhRingName} of {player.Name}.", ex);
            }

            player.Out.SendInventoryItemsUpdate(new[] { item });
            player.Out.SendMessage($"Your {ThreePointOhRingName} has been updated: +{ThreePointOhRingBonus} to all attributes and +{ThreePointOhRingResistBonus} to all resistances.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }
    }

    private static bool IsCurrentThreePointOhRing(DbInventoryItem item)
    {
        for (int i = 0; i < ThreePointOhRingStats.Length; i++)
        {
            if (BonusOf(item, i + 1) != ThreePointOhRingBonus)
            {
                return false;
            }
        }

        return item.Description == ThreePointOhRingDescription;
    }

    private static int BonusOf(DbInventoryItem item, int line)
    {
        return line switch
        {
            1 => item.Bonus1,
            2 => item.Bonus2,
            3 => item.Bonus3,
            4 => item.Bonus4,
            5 => item.Bonus5,
            6 => item.Bonus6,
            7 => item.Bonus7,
            8 => item.Bonus8,
            _ => 0
        };
    }

    /// <summary>The eight attributes, +<see cref="ThreePointOhRingBonus"/> each, on Three Point Oh's Ring.</summary>
    public static readonly eProperty[] ThreePointOhRingStats =
    {
        eProperty.Strength, eProperty.Constitution, eProperty.Dexterity, eProperty.Quickness,
        eProperty.Intelligence, eProperty.Piety, eProperty.Empathy, eProperty.Charisma
    };

    /// <summary>
    /// The ring Three Point Oh (every run's act 1 boss) always drops on top of its normal drops: +3 to each attribute,
    /// and +3 to each resist while worn (<see cref="ApplyThreePointOhRingResists"/>).
    /// Marked Unique so /junk never touches it.
    /// </summary>
    public static GeneratedUniqueItem BuildThreePointOhRing(GamePlayer player)
    {
        GeneratedUniqueItem item = new(player.Realm, (eCharacterClass) player.CharacterClass.ID, 1, eObjectType.Magical, InventorySlotFor(eUniqueSlot.Ring));
        item.Model = ModelFor(eUniqueSlot.Ring);
        ClearMagic(item);

        for (int i = 0; i < ThreePointOhRingStats.Length; i++)
        {
            ExpeditionLoot.SetBonus(item, i + 1, ThreePointOhRingStats[i], ThreePointOhRingBonus);
        }

        item.Name = ThreePointOhRingName;
        item.Description = ThreePointOhRingDescription;
        item.Quality = 100;
        item.Condition = item.MaxCondition;
        item.Durability = item.MaxDurability;
        item.IsTradable = true;
        item.IsDropable = true;
        item.IsPickable = true;
        item.Price = Money.SetAutoPrice(5, 100);
        item.AllowAdd = true;

        return item;
    }

    /// <summary>
    /// Dragonslayer armor for unique chest pieces and helms (owner 2026-10-25: "make all armor pieces dragon slayer"), by
    /// armor type and realm, from the model changer (scripts/customnpc/ItemModel.cs). Plate and scale have one model
    /// for every realm. Cosmetic only; stats come from the unique.
    /// </summary>
    private static readonly Dictionary<(eObjectType Type, eRealm Realm), (int Torso, int Helm)> DragonslayerLooks = new()
    {
        [(eObjectType.Cloth, eRealm.Albion)] = (4015, 4056),
        [(eObjectType.Cloth, eRealm.Midgard)] = (4046, 4070),
        [(eObjectType.Cloth, eRealm.Hibernia)] = (4099, 4063),
        [(eObjectType.Leather, eRealm.Albion)] = (3990, 4054),
        [(eObjectType.Leather, eRealm.Midgard)] = (4021, 4068),
        [(eObjectType.Leather, eRealm.Hibernia)] = (4074, 4061),
        [(eObjectType.Studded, eRealm.Albion)] = (4010, 4055),
        [(eObjectType.Studded, eRealm.Midgard)] = (4041, 4069),
        [(eObjectType.Reinforced, eRealm.Hibernia)] = (4094, 4062),
        [(eObjectType.Chain, eRealm.Albion)] = (3995, 4057),
        [(eObjectType.Chain, eRealm.Midgard)] = (4026, 4072),
        [(eObjectType.Scale, eRealm.Hibernia)] = (4089, 4066),
        [(eObjectType.Plate, eRealm.Albion)] = (4000, 4053)
    };

    /// <summary>The Dragonslayer model for a unique chest piece or helm of this armor type and realm, or 0 to keep the generated one.</summary>
    public static int DragonslayerModelFor(eObjectType armorType, eRealm realm, eUniqueSlot slot)
    {
        if (!DragonslayerLooks.TryGetValue((armorType, realm), out (int Torso, int Helm) look))
        {
            return 0;
        }

        return slot switch
        {
            eUniqueSlot.Chest => look.Torso,
            eUniqueSlot.Helm => look.Helm,
            _ => 0
        };
    }

    /// <summary>
    /// Special models for unique cloaks (owner 2026-10-25: uniques should look the part), from the artifact cloaks the
    /// model changer offers (scripts/customnpc/ItemModel.cs): Shades of Mist for the lake-mist mantle, Oceanus for the
    /// sea shroud.
    /// </summary>
    private static readonly Dictionary<string, int> SpecialLooks = new(StringComparer.Ordinal)
    {
        ["Mantle of the Grey Lough"] = 1726,
        ["Shroud of the Pale Tide"] = 1722
    };

    /// <summary>
    /// The weapon glows a unique weapon wears (owner 2026-10-25: uniques should look the part). The values are item effects
    /// real artifact and dungeon weapons in the item table carry (owner 2026-10-25: the first set, 30/48/49, looked bugged;
    /// 30 is on no weapon in the table, 48 and 49 only on fist wraps): 61 the Balefire weapons, 53 Dragonfire, 41 the
    /// Infernal weapons, 69 the Azure and Frozen ones.
    /// </summary>
    public static readonly int[] WeaponGlows = [61, 53, 41, 69];

    /// <summary>A glow chosen for a unique by its theme; the others pick from <see cref="WeaponGlows"/> by name.</summary>
    private static readonly Dictionary<string, int> ThemedGlows = new(StringComparer.Ordinal)
    {
        ["Fenwatch Fang"] = 61,
        ["Wyrmbane, the Last Ember"] = 53
    };

    /// <summary>The same glow for a unique every time: its themed glow, else one picked from its name.</summary>
    public static int WeaponGlowFor(string name)
    {
        if (name != null && ThemedGlows.TryGetValue(name, out int themed))
        {
            return themed;
        }

        int sum = 0;

        foreach (char c in name ?? string.Empty)
        {
            sum = unchecked(sum * 31 + c);
        }

        return WeaponGlows[(sum & int.MaxValue) % WeaponGlows.Length];
    }

    /// <summary>
    /// Trials of Atlantis style percentage bonuses on every unique (owner 2026-10-25), on the lines after the unique's
    /// own: melee classes get melee damage, style damage or melee speed, list casters spell damage, casting speed or
    /// spell duration. The server caps each of these at 10% from items (spell duration at 25%).
    /// </summary>
    public static int ToaPercentFor(eUniqueTier tier)
    {
        return tier switch
        {
            eUniqueTier.Early => 3,
            eUniqueTier.Mid => 5,
            _ => 7
        };
    }

    /// <summary>The ToA bonuses a unique can carry: melee classes, list casters, and casters who fight in melee.</summary>
    private static readonly eProperty[] MeleeToa = [eProperty.MeleeDamage, eProperty.StyleDamage, eProperty.MeleeSpeed];
    private static readonly eProperty[] CasterToa = [eProperty.SpellDamage, eProperty.CastingSpeed, eProperty.SpellDuration];

    /// <summary>
    /// Valewalkers and Vampiirs cast and fight in melee (owner 2026-10-25: a Valewalker's uniques only gave cast speed
    /// and spell damage): melee and caster bonuses take turns, so a unique with two or more has one of each.
    /// </summary>
    private static readonly eProperty[] MixedToa = [eProperty.MeleeDamage, eProperty.SpellDamage, eProperty.MeleeSpeed, eProperty.CastingSpeed, eProperty.StyleDamage, eProperty.SpellDuration];

    /// <summary>
    /// The ToA properties this unique carries (owner 2026-10-25: not the same one on every unique, and more on higher
    /// tiers). Each unique starts at its own place in the three by its place in the catalog.
    /// </summary>
    public static eProperty[] ToaPropertiesFor(UniqueDefinition definition, eProperty[] pool)
    {
        int index = 0;

        for (int i = 0; i < Catalog.Count; i++)
        {
            if (ReferenceEquals(Catalog[i], definition) || Catalog[i].Name == definition.Name)
            {
                index = i;

                break;
            }
        }

        // Higher tiers carry more of them (owner 2026-10-25): early 1, mid 2, late all 3; a weapon one more, up to 3.
        int count = definition.Tier switch
        {
            eUniqueTier.Early => 1,
            eUniqueTier.Mid => 2,
            _ => 3
        };

        if (definition.Slot == eUniqueSlot.Weapon)
        {
            count++;
        }

        count = Math.Min(count, Math.Min(pool.Length, 3));
        eProperty[] properties = new eProperty[count];

        // A mixed pool starts on a melee bonus as often as on a caster one, then alternates.
        int start = pool == MixedToa ? index % 2 : index;

        for (int i = 0; i < count; i++)
        {
            properties[i] = pool[(start + i) % pool.Length];
        }

        return properties;
    }

    /// <summary>
    /// The ToA bonuses for the finder's class: Valewalkers and Vampiirs both kinds, other list casters (wizards,
    /// animists, bainshees...) the caster ones, every other class the melee ones.
    /// </summary>
    public static eProperty[] ToaPoolFor(GamePlayer player)
    {
        int classId = player?.CharacterClass?.ID ?? 0;

        if (classId is (int) eCharacterClass.Valewalker or (int) eCharacterClass.Vampiir)
        {
            return MixedToa;
        }

        return player?.CharacterClass?.ClassType == eClassType.ListCaster ? CasterToa : MeleeToa;
    }

    /// <summary>
    /// Writes the unique's ToA lines after its own lines. Returns true when a line changed (the caller re-applies a worn
    /// item's bonuses and saves).
    /// </summary>
    public static bool ApplyToaLines(DbItemTemplate item, UniqueDefinition definition, eProperty[] pool)
    {
        eProperty[] properties = ToaPropertiesFor(definition, pool);
        int value = ToaPercentFor(definition.Tier);
        bool changed = false;

        for (int i = 0; i < properties.Length; i++)
        {
            int line = Math.Min(definition.Lines.Length, 8) + i + 1;

            if (line > 10)
            {
                break;
            }

            (int type, int amount) = BonusLine(item, line);

            if (type == (int) properties[i] && amount == value)
            {
                continue;
            }

            ExpeditionLoot.SetBonus(item, line, properties[i], value);
            changed = true;
        }

        return changed;
    }

    private static (int Type, int Amount) BonusLine(DbItemTemplate item, int line)
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
            _ => (item.Bonus10Type, item.Bonus10)
        };
    }

    /// <summary>
    /// Brings uniques found before the latest changes up to date at login: their look (artifact, special or
    /// Dragonslayer model; weapon glow) and their ToA percentage lines. The item is re-sent so the bag shows it at once.
    /// </summary>
    public static void UpdateOldUniqueLooks(GamePlayer player)
    {
        if (player?.Inventory == null)
        {
            return;
        }

        foreach (DbInventoryItem item in player.Inventory.AllItems.ToList())
        {
            UniqueDefinition definition = item == null ? null : FindByName(item.Name);

            if (definition == null)
            {
                continue;
            }

            int lookModel = LookModelFor(item.Template, definition, player.Realm);
            int glow = definition.Slot == eUniqueSlot.Weapon ? WeaponGlowFor(definition.Name) : item.Effect;
            int proc = item.Template.ProcSpellID != 0 && definition.Proc != eUniqueProc.None ? ProcSpellFor(definition.Proc, item.Level) : item.Template.ProcSpellID;
            bool lookChanged = (lookModel > 0 && item.Model != lookModel) || item.Effect != glow || item.Template.ProcSpellID != proc;
            bool worn = item.SlotPosition >= (int) eInventorySlot.MinEquipable && item.SlotPosition <= (int) eInventorySlot.MaxEquipable;
            eInventorySlot slot = (eInventorySlot) item.SlotPosition;

            // The ToA lines change the item's bonuses: a worn item is taken off and put back on around the change.
            DbItemTemplate preview = new();
            CopyBonusLines(item.Template, preview);
            bool bonusChanged = ApplyToaLines(preview, definition, ToaPoolFor(player));

            // The catalog's own lines too (owner 2026-10-25: mid-tier uniques halved).
            for (int i = 0; i < definition.Lines.Length && i < 8; i++)
            {
                (int type, int amount) = BonusLine(item.Template, i + 1);

                if (type != (int) definition.Lines[i].Property || amount != definition.Lines[i].Value)
                {
                    bonusChanged = true;
                }
            }

            if (!lookChanged && !bonusChanged)
            {
                continue;
            }

            if (bonusChanged && worn)
            {
                player.OnItemUnequipped(item, slot);
            }

            if (bonusChanged)
            {
                for (int i = 0; i < definition.Lines.Length && i < 8; i++)
                {
                    ExpeditionLoot.SetBonus(item.Template, i + 1, definition.Lines[i].Property, definition.Lines[i].Value);
                }

                ApplyToaLines(item.Template, definition, ToaPoolFor(player));
            }

            if (lookModel > 0)
            {
                item.Template.Model = lookModel;
            }

            item.Template.Effect = glow;
            item.Template.ProcSpellID = proc;

            if (bonusChanged && worn)
            {
                player.OnItemEquipped(item, slot);
            }

            try
            {
                GameInventoryObjectExtensions.SaveItem(item);

                if (item.Template is DbItemUnique unique)
                {
                    GameServer.Database.SaveObject(unique);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[EXPEDITION] Could not save the update of {item.Name} for {player.Name}.", ex);
            }

            player.Out.SendInventoryItemsUpdate(new[] { item });
        }
    }

    /// <summary>
    /// Trials of Atlantis artifact looks for uniques (owner 2026-10-25: "make every unique look like an artifact"),
    /// from the artifact models the model changer offers (scripts/customnpc/ItemModel.cs). A unique picks one of the
    /// fitting models by its name, so each unique always has the same look. Jewelry's artifact model is ModelFor's.
    /// Armor models are Albion / Hibernia / Midgard.
    /// </summary>
    private static readonly int[][] ArtifactChests =
    [
        [2226, 2228, 2227],  // Eirene's Chest
        [2475, 2476, 2477],  // Guard of Valor
        [2187, 2189, 2188]   // Golden Scarab Vest
    ];

    private static readonly int[] NaliahsRobe = [2516, 2517, 2518];

    private static readonly int[][] ArtifactHelms =
    [
        [1839, 1840, 1841],  // Crown of Zahur
        [1842, 1843, 1844],  // Crown of Zahur (variant)
        [2223, 2225, 2224]   // Winged Helm
    ];

    /// <summary>The artifact model for this unique (by its type, hand and damage type, or armor slot), or 0 for none.</summary>
    public static int ArtifactModelFor(DbItemTemplate item, UniqueDefinition definition, eRealm realm)
    {
        int realmIndex = realm switch
        {
            eRealm.Hibernia => 1,
            eRealm.Midgard => 2,
            _ => 0
        };

        if (definition.Slot == eUniqueSlot.Chest)
        {
            return (eObjectType) item.Object_Type == eObjectType.Cloth ? NaliahsRobe[realmIndex] : Pick(ArtifactChests, definition.Name)[realmIndex];
        }

        if (definition.Slot == eUniqueSlot.Helm)
        {
            return Pick(ArtifactHelms, definition.Name)[realmIndex];
        }

        if (definition.Slot != eUniqueSlot.Weapon)
        {
            return 0;
        }

        int[] models = (eObjectType) item.Object_Type switch
        {
            eObjectType.Flexible => [2119],                                   // Snakecharmer's Whip
            eObjectType.HandToHand or eObjectType.FistWraps => [2469],        // Snakecharmer's Fist
            eObjectType.Staff => [1659, 1660, 1658],                          // Traldor's Oracle, Trident of the Gods, Tartaros Gift
            eObjectType.Scythe => [3450, 2111],                               // Scythe of Kings, Snakecharmer's Scythe
            eObjectType.PolearmWeapon => [1661, 1662],                        // Pole of Kings, Golden Pole
            eObjectType.Spear or eObjectType.CelticSpear => [1662],           // Golden Spear
            eObjectType.Shield => [1663, 1664, 1665],                         // Aten's Shield, Cyclop's Eye, Shield of Khaos
            eObjectType.CompositeBow or eObjectType.Longbow or eObjectType.RecurvedBow or eObjectType.Fired => [1666, 1667], // Fool's Bow, Braggart's Bow
            _ => null
        };

        if (models == null && item.Item_Type == Slot.TWOHAND)
        {
            models = (eDamageType) item.Type_Damage switch
            {
                eDamageType.Slash => [2110, 1670],                            // Malice Axe, Battler Sword
                eDamageType.Crush => [2113, 3448, 3449],                      // Bruiser, Battler and Malice Hammers
                _ => null
            };
        }
        else if (models == null && item.Item_Type is Slot.RIGHTHAND or Slot.LEFTHAND)
        {
            models = (eDamageType) item.Type_Damage switch
            {
                eDamageType.Thrust => [1668, 1669, 1807],                     // Traitor's Dagger, Croc Tooth Dagger, Golden Spear
                eDamageType.Slash => [3452, 3451, 2109, 2112],                // Traitor's Axe, Croc Tooth Axe, Malice Axe, Battler Sword
                eDamageType.Crush => [3447, 1671, 3453, 1672],                // Malice, Bruiser and Battler Hammers, Scepter of the Meritorious
                _ => null
            };
        }

        return models == null ? 0 : Pick(models, definition.Name);
    }

    private static T Pick<T>(T[] options, string name)
    {
        int sum = 0;

        foreach (char c in name ?? string.Empty)
        {
            sum = unchecked(sum * 31 + c);
        }

        return options[(sum & int.MaxValue) % options.Length];
    }

    /// <summary>The model a unique should wear: its special model, an artifact model, else Dragonslayer armor (0: keep).</summary>
    public static int LookModelFor(DbItemTemplate item, UniqueDefinition definition, eRealm realm)
    {
        if (SpecialLooks.TryGetValue(definition.Name, out int special))
        {
            return special;
        }

        // Jewelry (not the cloak): the artifact jewelry model of its slot, so uniques found earlier change too at login.
        if (IsJewelry(definition.Slot) && definition.Slot != eUniqueSlot.Cloak)
        {
            return ModelFor(definition.Slot);
        }

        int artifact = ArtifactModelFor(item, definition, realm);

        if (artifact > 0)
        {
            return artifact;
        }

        return DragonslayerModelFor((eObjectType) item.Object_Type, realm, definition.Slot);
    }

    private static void CopyBonusLines(DbItemTemplate from, DbItemTemplate to)
    {
        for (int line = 1; line <= 10; line++)
        {
            (int type, int amount) = BonusLine(from, line);
            ExpeditionLoot.SetBonus(to, line, (eProperty) type, amount);
        }
    }

    /// <summary>
    /// The unique's description: its lore, then how far it grows (owner 2026-10-25: "make it clear what level it caps
    /// out at"), then the Atlas ROG and rarity markers that salvage and /junk read.
    /// </summary>
    public static string DescriptionFor(UniqueDefinition definition)
    {
        return $"{definition.Lore} Grows with you up to level {TierCapLevel(definition.Tier)}. | Atlas ROG | Expedition {eLootRarity.Unique}";
    }

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.AddHandler(GamePlayerEvent.LevelUp, new DOLEventHandler(OnLevelUp));
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.LevelUp, new DOLEventHandler(OnLevelUp));
    }

    private static void OnLevelUp(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GamePlayer player)
        {
            LevelUniques(player);
        }
    }

    /// <summary>
    /// Uniques level with their owner (owner 2026-10-25): each unique the player carries rises to the player's level,
    /// up to its tier's cap (early 20, mid 40, late 50): item level, weapon DPS or armor AF (the generator's formulas,
    /// GeneratedUniqueItem), proc strength and required level. Called on level-up and at login.
    /// </summary>
    public static void LevelUniques(GamePlayer player)
    {
        if (player?.Inventory == null)
        {
            return;
        }

        foreach (DbInventoryItem item in player.Inventory.AllItems.ToList())
        {
            UniqueDefinition definition = item == null ? null : FindByName(item.Name);

            if (definition == null)
            {
                continue;
            }

            int cap = TierCapLevel(definition.Tier);
            int wanted = Math.Clamp((int) player.Level, TierMinLevel(definition.Tier), cap);
            bool describe = item.Description != DescriptionFor(definition);

            if (item.Level >= wanted && !describe)
            {
                continue;
            }

            bool grows = item.Level < wanted;
            bool worn = item.SlotPosition >= (int) eInventorySlot.MinEquipable && item.SlotPosition <= (int) eInventorySlot.MaxEquipable;
            eInventorySlot slot = (eInventorySlot) item.SlotPosition;

            if (grows && worn)
            {
                player.OnItemUnequipped(item, slot);
            }

            DbItemTemplate template = item.Template;
            template.Description = DescriptionFor(definition);

            if (grows)
            {
                eObjectType type = (eObjectType) template.Object_Type;
                template.Level = wanted;

                if (type >= eObjectType._FirstArmor && type <= eObjectType._LastArmor)
                {
                    template.DPS_AF = type == eObjectType.Cloth ? wanted : wanted * 2;
                }
                else if ((type >= eObjectType._FirstWeapon && type <= eObjectType._LastWeapon) || type == eObjectType.Shield)
                {
                    template.DPS_AF = (int) ((wanted * 0.3 + 1.2) * 10);
                }

                if (template.ProcSpellID != 0 && definition.Proc != eUniqueProc.None)
                {
                    template.ProcSpellID = ProcSpellFor(definition.Proc, wanted);
                }

                template.LevelRequirement = Math.Max(1, Math.Min(wanted, (int) player.Level));
            }

            if (grows && worn)
            {
                player.OnItemEquipped(item, slot);
            }

            try
            {
                GameInventoryObjectExtensions.SaveItem(item);

                if (template is DbItemUnique unique)
                {
                    GameServer.Database.SaveObject(unique);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[EXPEDITION] Could not save the level of {item.Name} for {player.Name}.", ex);
            }

            player.Out.SendInventoryItemsUpdate(new[] { item });

            if (grows)
            {
                string note = wanted >= cap ? $" It has reached its full strength (level {cap})." : $" It keeps growing with you up to level {cap}.";
                player.Out.SendMessage($"Your {item.Name} grows to level {wanted}.{note}", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            }
        }
    }

    public static UniqueDefinition FindByName(string name)
    {
        return Catalog.FirstOrDefault(definition => string.Equals(definition.Name, name, StringComparison.Ordinal));
    }

    public static UniqueDefinition PickFor(GamePlayer player, ExpeditionRun run)
    {
        HashSet<string> found = FoundByRun.GetOrAdd(RunKey(run, player), _ => new HashSet<string>(StringComparer.Ordinal));

        lock (found)
        {
            return Pick(Random.Shared, TierFor(player.Level), found);
        }
    }

    #endregion

    #region Building the item

    /// <summary>
    /// The proc spell for a kind at an item level: the ids and level brackets the stock generator uses
    /// (GeneratedUniqueItem.GenerateProc: lifetap 8010-8017, damage 8020-8027, heal 8030-8037, absorb 8040-8047).
    /// Above level 39 the top spell of the kind is used.
    /// </summary>
    public static int ProcSpellFor(eUniqueProc proc, int level)
    {
        // The damage proc is a heat bolt (owner 2026-10-25: Wyrmbane's proc read "heals for 0%"; the generator's 8020-8027
        // "DD procs" are body-damage lifedrains with no heal). These are the item table's instant heat damage procs:
        // ProcDirectDamageHeat20/25/30/35/40/47, 41 to 95 damage.
        if (proc == eUniqueProc.Damage)
        {
            return level switch
            {
                >= 47 => 32124,
                >= 40 => 32116,
                >= 35 => 32112,
                >= 30 => 32108,
                >= 25 => 32104,
                _ => 32100
            };
        }

        int first = proc switch
        {
            eUniqueProc.Lifetap => 8010,
            eUniqueProc.Damage => 8020,
            eUniqueProc.Heal => 8030,
            eUniqueProc.Absorb => 8040,
            _ => 0
        };

        if (first == 0)
        {
            return 0;
        }

        int step = level < 10 ? 0 : Math.Min(7, (level - 5) / 5);

        return first + step;
    }

    /// <summary>The equipment slot of a jewelry or armor archetype (weapons get theirs from the generator).</summary>
    public static eInventorySlot InventorySlotFor(eUniqueSlot slot)
    {
        return slot switch
        {
            eUniqueSlot.Necklace => eInventorySlot.Neck,
            eUniqueSlot.Cloak => eInventorySlot.Cloak,
            eUniqueSlot.Jewel => eInventorySlot.Jewelry,
            eUniqueSlot.Belt => eInventorySlot.Waist,
            eUniqueSlot.Ring => eInventorySlot.LeftRing,
            eUniqueSlot.Bracer => eInventorySlot.LeftBracer,
            eUniqueSlot.Chest => eInventorySlot.TorsoArmor,
            eUniqueSlot.Helm => eInventorySlot.HeadArmor,
            _ => eInventorySlot.RightHandWeapon
        };
    }

    /// <summary>
    /// The jewelry model. Owner 2026-10-27: unique jewelry looks like Trials of Atlantis artifacts on the paper doll, so
    /// every unique worn stands out at a glance. The item table's artifact jewelry models: Goddess Necklace 1887, Gem of
    /// Lost Memories 1886, Jacina's Sash 1884, Crocodile Tear Ring 1888, Band of Stars 1885. The cloak keeps the
    /// generator's 559 (its look comes from <see cref="SpecialLooks"/> or the artifact cloaks).
    /// </summary>
    public static int ModelFor(eUniqueSlot slot)
    {
        return slot switch
        {
            eUniqueSlot.Necklace => 1887,
            eUniqueSlot.Cloak => 559,
            eUniqueSlot.Jewel => 1886,
            eUniqueSlot.Belt => 1884,
            eUniqueSlot.Ring => 1888,
            eUniqueSlot.Bracer => 1885,
            _ => 0
        };
    }

    public static bool IsJewelry(eUniqueSlot slot)
    {
        return slot is eUniqueSlot.Necklace or eUniqueSlot.Cloak or eUniqueSlot.Jewel or eUniqueSlot.Belt or eUniqueSlot.Ring or eUniqueSlot.Bracer;
    }

    /// <summary>
    /// The item for a unique and this finder. Jewelry is item level <see cref="TierMinLevel"/>. Chest, Helm and Weapon are
    /// made at the finder's level (kept inside the tier) as the generator makes them for the finder's class: the best
    /// armor the class wears, or a melee weapon the class uses, with the generator's armor factor, speed, hand and damage
    /// type. Name, lore and bonus lines are always the catalog's. Bonuses above the finder's current caps are clamped by
    /// the server, so the item grows with its owner.
    /// </summary>
    /// <param name="weaponType">A weapon unique: this weapon type and hand instead of a random one the class uses (the exchanger).</param>
    /// <param name="itemLevel">The item level to build at instead of the finder's (the exchanger keeps the original's).</param>
    public static GeneratedUniqueItem Build(GamePlayer player, UniqueDefinition definition, (eObjectType Type, eInventorySlot Hand)? weaponType = null,
        byte? itemLevel = null)
    {
        eRealm realm = player.Realm;
        eCharacterClass characterClass = (eCharacterClass) player.CharacterClass.ID;
        int min = TierMinLevel(definition.Tier);
        byte level = itemLevel ?? (byte) (IsJewelry(definition.Slot) ? min : Math.Clamp(player.Level, min, TierCapLevel(definition.Tier)));
        GeneratedUniqueItem item;

        if (IsJewelry(definition.Slot))
        {
            item = new GeneratedUniqueItem(realm, characterClass, level, eObjectType.Magical, InventorySlotFor(definition.Slot));
            item.Model = ModelFor(definition.Slot);
        }
        else if (definition.Slot == eUniqueSlot.Weapon && weaponType.HasValue)
        {
            item = new GeneratedUniqueItem(realm, characterClass, level, weaponType.Value.Type, weaponType.Value.Hand);
        }
        else if (definition.Slot == eUniqueSlot.Weapon)
        {
            eObjectType type = WeaponTypeFor(player, realm, characterClass);
            item = new GeneratedUniqueItem(realm, characterClass, level, type, GeneratedUniqueItem.GenerateItemType(type));
        }
        else
        {
            eObjectType type = ArmorTypeFor(realm, characterClass, level);
            item = new GeneratedUniqueItem(realm, characterClass, level, type, InventorySlotFor(definition.Slot));
        }

        ClearMagic(item);

        for (int i = 0; i < definition.Lines.Length && i < 10; i++)
        {
            ExpeditionLoot.SetBonus(item, i + 1, definition.Lines[i].Property, definition.Lines[i].Value);
        }

        ApplyToaLines(item, definition, ToaPoolFor(player));

        if (definition.Proc != eUniqueProc.None)
        {
            item.ProcSpellID = ProcSpellFor(definition.Proc, item.Level);
            item.ProcChance = ProcChance;
        }

        item.Name = definition.Name;

        if (definition.Slot == eUniqueSlot.Weapon)
        {
            item.Effect = WeaponGlowFor(definition.Name);
        }

        int lookModel = LookModelFor(item, definition, realm);

        if (lookModel > 0)
        {
            item.Model = lookModel;
        }

        // Salvage caps "Atlas ROG" items (Salvage.cs:708); the suffix marks the rarity for /junk (ExpeditionLoot.TryGetRarity).
        item.Description = DescriptionFor(definition);
        item.Quality = 100;
        item.Condition = item.MaxCondition;
        item.Durability = item.MaxDurability;
        item.IsTradable = true;
        item.IsDropable = true;
        item.IsPickable = true;
        item.Price = Money.SetAutoPrice(TierCapLevel(definition.Tier), 100);
        item.AllowAdd = true;

        // Fork (owner 2026-10-25): its proc fires for the finder from the start (ExpeditionLoot.Generate has the reason).
        item.LevelRequirement = Math.Max(1, Math.Min((int) item.Level, (int) player.Level));

        return item;
    }

    /// <summary>The best armor the class wears at this level (the generator's per-realm tables).</summary>
    private static eObjectType ArmorTypeFor(eRealm realm, eCharacterClass characterClass, byte level)
    {
        return realm switch
        {
            eRealm.Midgard => GeneratedUniqueItem.GetMidgardArmorType(characterClass, level),
            eRealm.Hibernia => GeneratedUniqueItem.GetHiberniaArmorType(characterClass, level),
            _ => GeneratedUniqueItem.GetAlbionArmorType(characterClass, level)
        };
    }

    /// <summary>
    /// A weapon the class uses (the generator's per-realm tables), re-rolled a few times to get a one- or two-handed
    /// melee weapon rather than a shield, instrument or ranged weapon, preferring one the finder can wield now. A class
    /// with no usable melee weapon keeps the first melee roll, else the last roll.
    /// </summary>
    private static eObjectType WeaponTypeFor(GamePlayer player, eRealm realm, eCharacterClass characterClass)
    {
        eObjectType type = eObjectType.Staff;
        eObjectType firstMelee = eObjectType.GenericItem;

        for (int attempt = 0; attempt < 30; attempt++)
        {
            type = realm switch
            {
                eRealm.Midgard => GeneratedUniqueItem.GetMidgardWeapon(characterClass),
                eRealm.Hibernia => GeneratedUniqueItem.GetHiberniaWeapon(characterClass),
                _ => GeneratedUniqueItem.GetAlbionWeapon(characterClass)
            };

            if (!IsMeleeWeapon(type))
            {
                continue;
            }

            // Fork (owner 2026-10-25): a weapon the finder can wield now (a level-2 Reaver cannot use a whip yet).
            if (player == null || ExpeditionLoot.CanUseNow(player, new DbItemTemplate { Object_Type = (int) type, Realm = (int) realm }))
            {
                return type;
            }

            if (firstMelee == eObjectType.GenericItem)
            {
                firstMelee = type;
            }
        }

        return firstMelee != eObjectType.GenericItem ? firstMelee : type;
    }

    public static bool IsMeleeWeapon(eObjectType type)
    {
        if (type < eObjectType._FirstWeapon || type > eObjectType._LastWeapon)
        {
            return false;
        }

        return type is not (eObjectType.Fired or eObjectType.Longbow or eObjectType.Crossbow or eObjectType.CompositeBow or eObjectType.RecurvedBow or eObjectType.Thrown);
    }

    /// <summary>Removes every bonus, proc and charge the generator rolled, so only the catalog's lines remain.</summary>
    private static void ClearMagic(DbItemTemplate item)
    {
        for (int line = 1; line <= 10; line++)
        {
            ExpeditionLoot.SetBonus(item, line, eProperty.Undefined, 0);
        }

        item.ExtraBonus = 0;
        item.ExtraBonusType = 0;
        item.ProcSpellID = 0;
        item.ProcSpellID1 = 0;
        item.ProcChance = 0;
        item.SpellID = 0;
        item.SpellID1 = 0;
        item.Charges = 0;
        item.MaxCharges = 0;
        item.Charges1 = 0;
        item.MaxCharges1 = 0;
        item.LevelRequirement = 0;
    }

    #endregion
}
