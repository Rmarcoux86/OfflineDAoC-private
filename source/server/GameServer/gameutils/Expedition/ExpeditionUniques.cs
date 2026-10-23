using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using DOL.Database;

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

        // Mid: usable from level 20, sized for level 40 (caps: stats 60, hit points 160, resists 21, skills 9).
        new("Eye of the Sleeping Giant", eUniqueTier.Mid, eUniqueSlot.Jewel,
            "Chipped from the brow of a stone giant who has not woken in a thousand years. Yet.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 45), new(eProperty.MaxHealth, 130), new(eProperty.Resist_Crush, 16), new(eProperty.Resist_Slash, 16), new(eProperty.AllSkills, 6) }),
        new("Oathbinder's Signet", eUniqueTier.Mid, eUniqueSlot.Ring,
            "Every oath sworn on this ring was kept. The ones who broke them were not.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Strength, 42), new(eProperty.Dexterity, 40), new(eProperty.Resist_Thrust, 16), new(eProperty.AllSkills, 5) }),
        new("Stormcaller's Wristguard", eUniqueTier.Mid, eUniqueSlot.Bracer,
            "The air crackles around the wearer's hand, and gulls flee before them.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Quickness, 44), new(eProperty.Dexterity, 38), new(eProperty.Resist_Energy, 17), new(eProperty.Resist_Cold, 15) }),
        new("Shroud of the Pale Tide", eUniqueTier.Mid, eUniqueSlot.Cloak,
            "Drawn up from the sea on the night the tide came in white and took the coast villages.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 40), new(eProperty.MaxHealth, 120), new(eProperty.Resist_Spirit, 17), new(eProperty.Resist_Body, 16), new(eProperty.Resist_Heat, 15) }),
        new("Choker of the Wild Hunt", eUniqueTier.Mid, eUniqueSlot.Necklace,
            "Hear the horns once and you join the hunt. Hear them twice and you lead it.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Strength, 45), new(eProperty.Quickness, 40), new(eProperty.MaxHealth, 100), new(eProperty.Resist_Matter, 15), new(eProperty.AllSkills, 5) }),
        new("Crown of the Cairnwatcher", eUniqueTier.Mid, eUniqueSlot.Helm,
            "The watcher on the cairn never slept. Its helm still keeps one eye open.",
            eUniqueProc.None,
            new UniqueLine[] { new(eProperty.Constitution, 44), new(eProperty.Dexterity, 36), new(eProperty.MaxHealth, 110), new(eProperty.Resist_Body, 16), new(eProperty.AllSkills, 6) }),

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

    /// <summary>The jewelry model, one of the generator's own (GeneratedUniqueItem.GenerateItemNameModel, Magical); 0 otherwise.</summary>
    public static int ModelFor(eUniqueSlot slot)
    {
        return slot switch
        {
            eUniqueSlot.Necklace => 101,
            eUniqueSlot.Cloak => 559,
            eUniqueSlot.Jewel => 115,
            eUniqueSlot.Belt => 597,
            eUniqueSlot.Ring => 103,
            eUniqueSlot.Bracer => 598,
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
    public static GeneratedUniqueItem Build(GamePlayer player, UniqueDefinition definition)
    {
        eRealm realm = player.Realm;
        eCharacterClass characterClass = (eCharacterClass) player.CharacterClass.ID;
        int min = TierMinLevel(definition.Tier);
        byte level = (byte) (IsJewelry(definition.Slot) ? min : Math.Clamp(player.Level, min, TierCapLevel(definition.Tier)));
        GeneratedUniqueItem item;

        if (IsJewelry(definition.Slot))
        {
            item = new GeneratedUniqueItem(realm, characterClass, level, eObjectType.Magical, InventorySlotFor(definition.Slot));
            item.Model = ModelFor(definition.Slot);
        }
        else if (definition.Slot == eUniqueSlot.Weapon)
        {
            eObjectType type = WeaponTypeFor(realm, characterClass);
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

        if (definition.Proc != eUniqueProc.None)
        {
            item.ProcSpellID = ProcSpellFor(definition.Proc, item.Level);
            item.ProcChance = ProcChance;
        }

        item.Name = definition.Name;

        // Salvage caps "Atlas ROG" items (Salvage.cs:708); the suffix marks the rarity for /junk (ExpeditionLoot.TryGetRarity).
        item.Description = $"{definition.Lore} | Atlas ROG | Expedition {eLootRarity.Unique}";
        item.Quality = 100;
        item.Condition = item.MaxCondition;
        item.Durability = item.MaxDurability;
        item.IsTradable = true;
        item.IsDropable = true;
        item.IsPickable = true;
        item.Price = Money.SetAutoPrice(TierCapLevel(definition.Tier), 100);
        item.AllowAdd = true;

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
    /// melee weapon rather than a shield, instrument or ranged weapon. A class with only those keeps the last roll.
    /// </summary>
    private static eObjectType WeaponTypeFor(eRealm realm, eCharacterClass characterClass)
    {
        eObjectType type = eObjectType.Staff;

        for (int attempt = 0; attempt < 12; attempt++)
        {
            type = realm switch
            {
                eRealm.Midgard => GeneratedUniqueItem.GetMidgardWeapon(characterClass),
                eRealm.Hibernia => GeneratedUniqueItem.GetHiberniaWeapon(characterClass),
                _ => GeneratedUniqueItem.GetAlbionWeapon(characterClass)
            };

            if (IsMeleeWeapon(type))
            {
                return type;
            }
        }

        return type;
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
