using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// /gauntlet kit (GMs only, owner 2026-10-28): gear for testing the Gauntlet as a fully templated level 50. Into the
/// backpack: 99% armor of the class's best type with no bonuses, a 99% copy of each equipped weapon, and two template
/// pieces (a necklace and a belt) holding every stat, resist and hit point bonus at its level 50 cap
/// (<see cref="GauntletChampion.TemplateCaps"/>) and the class's three ToA bonuses at 10%. Level and realm rank come
/// from /player level and /player rps.
/// </summary>
public static class GauntletTestKit
{
    public const int Quality = 99;
    public const int ToaPercent = 10;
    private const byte Level = 50;

    private static readonly eInventorySlot[] ArmorSlots =
    [
        eInventorySlot.HeadArmor, eInventorySlot.TorsoArmor, eInventorySlot.ArmsArmor,
        eInventorySlot.HandsArmor, eInventorySlot.LegsArmor, eInventorySlot.FeetArmor
    ];

    private static readonly eInventorySlot[] WeaponSlots =
    [
        eInventorySlot.RightHandWeapon, eInventorySlot.LeftHandWeapon, eInventorySlot.TwoHandWeapon, eInventorySlot.DistanceWeapon
    ];

    private static readonly eProperty[] Resists =
    [
        eProperty.Resist_Crush, eProperty.Resist_Slash, eProperty.Resist_Thrust, eProperty.Resist_Heat, eProperty.Resist_Cold,
        eProperty.Resist_Matter, eProperty.Resist_Body, eProperty.Resist_Spirit, eProperty.Resist_Energy
    ];

    /// <summary>The necklace's lines: the four physical stats and the class's casting stat at the cap, hits, and the ToA bonuses.</summary>
    public static List<(eProperty Property, int Value)> NecklaceLines(eStat manaStat, IReadOnlyList<eProperty> toa)
    {
        (int stat, _, int hits) = GauntletChampion.TemplateCaps(Level);
        List<(eProperty, int)> lines =
        [
            (eProperty.Strength, stat), (eProperty.Constitution, stat), (eProperty.Dexterity, stat), (eProperty.Quickness, stat)
        ];

        if (manaStat != eStat.UNDEFINED && (eProperty) manaStat is not (eProperty.Strength or eProperty.Constitution or eProperty.Dexterity or eProperty.Quickness))
        {
            lines.Add(((eProperty) manaStat, stat));
        }

        lines.Add((eProperty.MaxHealth, hits));
        lines.AddRange(toa.Take(3).Select(property => (property, ToaPercent)));

        return lines;
    }

    /// <summary>The belt's lines: every resist at the cap.</summary>
    public static List<(eProperty Property, int Value)> BeltLines()
    {
        (_, int resist, _) = GauntletChampion.TemplateCaps(Level);

        return Resists.Select(property => (property, resist)).ToList();
    }

    /// <summary>Puts the kit in the player's backpack. Returns the message for the player.</summary>
    public static string Give(GamePlayer player)
    {
        if (player?.CharacterClass == null)
        {
            return "No character.";
        }

        eRealm realm = player.Realm;
        eCharacterClass characterClass = (eCharacterClass) player.CharacterClass.ID;
        List<DbItemTemplate> kit = new();
        eObjectType armorType = ExpeditionUniques.ArmorTypeFor(realm, characterClass, Level);

        foreach (eInventorySlot slot in ArmorSlots)
        {
            GeneratedUniqueItem armor = ExpeditionLoot.RollFor(realm, characterClass, Level, armorType, slot, eLootRarity.Epic);

            if (armor == null)
            {
                continue;
            }

            ExpeditionUniques.ClearMagic(armor);
            armor.Name = "Test " + armor.Name;
            kit.Add(armor);
        }

        foreach (eInventorySlot slot in WeaponSlots)
        {
            DbInventoryItem weapon = player.Inventory.GetItem(slot);

            if (weapon?.Template != null)
            {
                kit.Add(new DbItemUnique(weapon.Template) { Name = "Test " + weapon.Name });
            }
        }

        kit.Add(TemplatePiece(player, eUniqueSlot.Necklace, "Test Template Necklace",
            NecklaceLines(player.CharacterClass.ManaStat, ExpeditionUniques.ToaPoolFor(player))));
        kit.Add(TemplatePiece(player, eUniqueSlot.Belt, "Test Template Belt", BeltLines()));

        int given = 0;

        foreach (DbItemTemplate template in kit)
        {
            template.Quality = Quality;
            template.Condition = template.MaxCondition;
            template.Durability = template.MaxDurability;
            template.IsTradable = false;
            template.AllowAdd = true;

            DbInventoryItem item = GameInventoryItem.Create(template);

            if (!player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item))
            {
                return $"Your backpack is full after {given} of {kit.Count} kit items. Make room and run /gauntlet kit again.";
            }

            given++;
        }

        return $"Test kit: {given} items in your backpack (99% armor, 99% copies of your weapons, a template necklace and belt). "
            + "Take off your other jewelry and equip them. For level and rank: /player level 50, /player rps <points>.";
    }

    private static DbItemTemplate TemplatePiece(GamePlayer player, eUniqueSlot slot, string name, List<(eProperty Property, int Value)> lines)
    {
        GeneratedUniqueItem item = new(player.Realm, (eCharacterClass) player.CharacterClass.ID, Level, eObjectType.Magical, ExpeditionUniques.InventorySlotFor(slot));
        item.Model = ExpeditionUniques.ModelFor(slot);
        ExpeditionUniques.ClearMagic(item);

        for (int i = 0; i < lines.Count && i < 10; i++)
        {
            ExpeditionLoot.SetBonus(item, i + 1, lines[i].Property, lines[i].Value);
        }

        item.Name = name;
        item.Description = "Gauntlet test gear (GM).";

        return item;
    }
}
