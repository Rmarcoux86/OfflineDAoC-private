using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// /gauntlet kit (GMs only, owner 2026-10-28): gear for testing the Gauntlet as a fully templated level 50. Into the
/// backpack: 99% armor of the class's best type with no bonuses, a 99% weapon of every type the class can use (two of each
/// one-hander for a class that fights with a weapon in each hand, a shield and the realm's bow when it uses them), and two template
/// pieces (a necklace and a belt) holding every stat, resist and hit point bonus at its level 50 cap
/// (<see cref="GauntletChampion.TemplateCaps"/>) and the class's three ToA bonuses at 10%. Level and realm rank come
/// from /player level and /player rps.
/// </summary>
public static class GauntletTestKit
{
    public const int Quality = 99;

    /// <summary>The unique weapon the kit makes in every weapon type the class can use: the late tier's.</summary>
    public const string UniqueWeaponName = "Wyrmbane, the Last Ember";
    public const int ToaPercent = 10;
    private const byte Level = 50;

    private static readonly eInventorySlot[] ArmorSlots =
    [
        eInventorySlot.HeadArmor, eInventorySlot.TorsoArmor, eInventorySlot.ArmsArmor,
        eInventorySlot.HandsArmor, eInventorySlot.LegsArmor, eInventorySlot.FeetArmor
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

        UniqueDefinition unique = ExpeditionUniques.FindByName(UniqueWeaponName);

        foreach ((eObjectType type, eInventorySlot hand, int copies) in WeaponsFor(player))
        {
            for (int i = 0; i < copies; i++)
            {
                GeneratedUniqueItem weapon = new(realm, characterClass, Level, type, hand);
                ExpeditionUniques.ClearMagic(weapon);
                weapon.Name = "Test " + weapon.Name;
                kit.Add(weapon);

                // The late-tier unique weapon in the same type and hand (owner 2026-10-28), with its lines and ToA bonuses.
                if (unique != null && type != eObjectType.Shield)
                {
                    kit.Add(ExpeditionUniques.Build(player, unique, (type, hand), Level));
                }
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

        return $"Test kit: {given} items in your backpack (99% armor, 99% weapons of every type you can use and Wyrmbane in each, a template necklace and belt). "
            + "Take off your other jewelry and equip them. For level and rank: /player level 50, /player rps <points>.";
    }

    /// <summary>
    /// The weapons to make: every type of the realm's list the player can use (ExpeditionExchanger.RealmWeapons, bows
    /// included). A one-hander is made for the left hand, twice, when the player fights with a weapon in each hand (a
    /// left-hand item goes in either hand; GamePlayerInventory's left slot takes only those), else once for the right.
    /// Plus a shield, and Midgard's left axe, when the player can use them.
    /// </summary>
    private static List<(eObjectType Type, eInventorySlot Hand, int Copies)> WeaponsFor(GamePlayer player)
    {
        eRealm realm = player.Realm;
        bool dualWields = player.attackComponent.CanUseLefthandedWeapon;
        List<(eObjectType, eInventorySlot, int)> weapons = new();

        bool CanUse(eObjectType type, eInventorySlot hand)
        {
            return player.HasAbilityToUseItem(new DbItemTemplate { Object_Type = (int) type, Item_Type = (int) hand, Realm = (int) realm });
        }

        foreach ((eObjectType type, eInventorySlot hand, _) in ExpeditionExchanger.RealmWeapons(realm))
        {
            bool oneHanded = hand == eInventorySlot.RightHandWeapon;
            eInventorySlot made = oneHanded && dualWields ? eInventorySlot.LeftHandWeapon : hand;

            if (CanUse(type, made))
            {
                weapons.Add((type, made, oneHanded && dualWields ? 2 : 1));
            }
        }

        if (realm == eRealm.Midgard && CanUse(eObjectType.LeftAxe, eInventorySlot.LeftHandWeapon))
        {
            weapons.Add((eObjectType.LeftAxe, eInventorySlot.LeftHandWeapon, 1));
        }

        if (CanUse(eObjectType.Shield, eInventorySlot.LeftHandWeapon))
        {
            weapons.Add((eObjectType.Shield, eInventorySlot.LeftHandWeapon, 1));
        }

        return weapons;
    }

    /// <summary>
    /// /gauntlet testwinner (GMs only, owner 2026-10-28): marks a character with no active run as having won its
    /// expedition, so it enters the Gauntlet as a player would: no boons, Gauntlet progress from round 1, Hardcore off. Its
    /// EndedUtc stays empty so the launcher's Hall of Fame never lists it (MainForm.ReadLocalExpeditionWins). Returns the message.
    /// <paramref name="hard"/> (/gauntlet testwinner hard) marks the run as a hard run, so its Gauntlet has the sixth round
    /// (GauntletRules.HardRounds); without it the record is not hard.
    /// </summary>
    public static string MarkWinner(GamePlayer target, bool hard = false)
    {
        if (target == null || string.IsNullOrEmpty(target.ObjectId))
        {
            return "No character.";
        }

        if (ExpeditionManager.GetRun(target) != null)
        {
            return $"{target.Name} is on an expedition. End it first (/run abandon), then mark them again.";
        }

        try
        {
            OfflineExpeditionRecord record = GameServer.Database.FindObjectByKey<OfflineExpeditionRecord>(target.ObjectId);
            bool isNew = record == null;
            record ??= new OfflineExpeditionRecord { CharacterId = target.ObjectId };
            record.CharacterName = target.Name;
            record.AccountName = target.Client?.Account?.Name ?? string.Empty;
            record.Status = ExpeditionManager.StatusWon;
            record.SummaryShown = true;
            record.Seed = Random.Shared.Next();
            record.Act = ActGenerator.ActCount;
            record.StartedUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            record.EndedUtc = string.Empty;
            record.ActJson = string.Empty;
            record.LogJson = "[]";
            record.BoonsJson = "[]";
            record.PendingBoonOfferJson = "[]";
            record.GauntletJson = string.Empty;
            record.Hard = hard;

            if (isNew)
            {
                GameServer.Database.AddObject(record);
            }
            else
            {
                GameServer.Database.SaveObject(record);
            }

            target.HCFlag = false;
            target.SaveIntoDatabase();
        }
        catch (Exception ex)
        {
            return $"{target.Name} could not be marked: {ex.Message}";
        }

        return $"{target.Name} is now a {(hard ? "hard-run " : string.Empty)}test winner: /gauntlet start works for them without GM, with no boons, from round 1"
            + (hard ? $", through {GauntletRules.HardRounds} rounds. " : ". ")
            + "Test winners are never listed in the Hall of Fame.";
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
