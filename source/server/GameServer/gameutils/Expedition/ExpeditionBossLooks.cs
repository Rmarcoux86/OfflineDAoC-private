namespace DOL.GS.Expedition;

/// <summary>
/// The named act bosses dressed as the race and class the owner gave them (docs/EXPEDITION-CLASS-BOSSES.md), for looks
/// only (owner 2026-10-25): a race body, the class's armor and weapon models. They stay ordinary monsters: no styles,
/// spells or pets, and an NPC's equipment carries no stats (its damage comes from its level). Preesh alone also swings
/// his left-hand mace (dual wield, for the Gauntlet's Blademaster), which applies to the act-5 boss as well.
/// </summary>
public static class ExpeditionBossLooks
{
    // Race bodies (Enums/eLivingModel.cs).
    private const ushort FirbolgMale = 286;
    private const ushort CeltMale = 302;
    private const ushort CeltFemale = 310;
    private const ushort HalfOgreMale = 1008;

    /// <summary>The cloak every designed boss wears, hood up.</summary>
    public const ushort CloakModel = 559;

    /// <summary>A little larger than a player, so the boss stands out.</summary>
    private const byte BossSize = 60;

    /// <summary>Dresses the boss when its name is one of the designed bosses. Returns true when it did.</summary>
    public static bool Apply(GameNPC boss, string title)
    {
        if (boss == null || string.IsNullOrEmpty(title))
        {
            return false;
        }

        GameNpcInventoryTemplate template = new();
        eActiveWeaponSlot weaponSlot;

        switch (title)
        {
            case "Toothmouth":
            {
                // Battle Bard: Firbolg in plain Celtic leather, blade and buckler.
                boss.Model = FirbolgMale;
                template.AddNPCEquipment(eInventorySlot.TorsoArmor, 393);
                template.AddNPCEquipment(eInventorySlot.LegsArmor, 394);
                template.AddNPCEquipment(eInventorySlot.ArmsArmor, 395);
                template.AddNPCEquipment(eInventorySlot.HandsArmor, 396);
                template.AddNPCEquipment(eInventorySlot.FeetArmor, 397);
                template.AddNPCEquipment(eInventorySlot.HeadArmor, 438);
                template.AddNPCEquipment(eInventorySlot.RightHandWeapon, 903);
                template.AddNPCEquipment(eInventorySlot.LeftHandWeapon, 1048);
                weaponSlot = eActiveWeaponSlot.Standard;

                break;
            }
            case "Ios":
            {
                // Valewalker: Celt woman in the Valewalker epic, war scythe.
                boss.Model = CeltFemale;
                template.AddNPCEquipment(eInventorySlot.TorsoArmor, 1003);
                template.AddNPCEquipment(eInventorySlot.LegsArmor, 379);
                template.AddNPCEquipment(eInventorySlot.ArmsArmor, 380);
                template.AddNPCEquipment(eInventorySlot.HandsArmor, 381);
                template.AddNPCEquipment(eInventorySlot.FeetArmor, 382);
                template.AddNPCEquipment(eInventorySlot.TwoHandWeapon, 932);
                weaponSlot = eActiveWeaponSlot.TwoHanded;

                break;
            }
            case "Preesh":
            {
                // Blademaster: Firbolg in the Blademaster epic and a Celtic scale helm, a mace in each hand (looks only:
                // the act-5 boss does not swing the left mace; a Gauntlet champion dual wields through its own spec).
                boss.Model = FirbolgMale;
                template.AddNPCEquipment(eInventorySlot.TorsoArmor, 782);
                template.AddNPCEquipment(eInventorySlot.LegsArmor, 783);
                template.AddNPCEquipment(eInventorySlot.ArmsArmor, 784);
                template.AddNPCEquipment(eInventorySlot.HandsArmor, 785);
                template.AddNPCEquipment(eInventorySlot.FeetArmor, 786);
                template.AddNPCEquipment(eInventorySlot.HeadArmor, 838);
                template.AddNPCEquipment(eInventorySlot.RightHandWeapon, 914);
                template.AddNPCEquipment(eInventorySlot.LeftHandWeapon, 914);
                weaponSlot = eActiveWeaponSlot.Standard;

                break;
            }
            case "Drunkstains":
            {
                // Armsman: Half Ogre in the Armsman epic and an Albion plate helm, halberd.
                boss.Model = HalfOgreMale;
                template.AddNPCEquipment(eInventorySlot.TorsoArmor, 688);
                template.AddNPCEquipment(eInventorySlot.LegsArmor, 689);
                template.AddNPCEquipment(eInventorySlot.ArmsArmor, 690);
                template.AddNPCEquipment(eInventorySlot.HandsArmor, 691);
                template.AddNPCEquipment(eInventorySlot.FeetArmor, 692);
                template.AddNPCEquipment(eInventorySlot.HeadArmor, 93);
                template.AddNPCEquipment(eInventorySlot.TwoHandWeapon, 67);
                weaponSlot = eActiveWeaponSlot.TwoHanded;

                break;
            }
            case "Gwyraan":
            {
                // Nature Druid: Celt in the Druid epic, staff (no pet).
                boss.Model = CeltMale;
                template.AddNPCEquipment(eInventorySlot.TorsoArmor, 739);
                template.AddNPCEquipment(eInventorySlot.LegsArmor, 740);
                template.AddNPCEquipment(eInventorySlot.ArmsArmor, 741);
                template.AddNPCEquipment(eInventorySlot.HandsArmor, 742);
                template.AddNPCEquipment(eInventorySlot.FeetArmor, 743);
                template.AddNPCEquipment(eInventorySlot.TwoHandWeapon, 1173);
                weaponSlot = eActiveWeaponSlot.TwoHanded;

                break;
            }
            default:
            {
                return false;
            }
        }

        // Every designed boss wears a cloak with the hood up (owner 2026-10-25). 559 is the cloak model the uniques use.
        template.AddNPCEquipment(eInventorySlot.Cloak, CloakModel);
        boss.IsCloakHoodUp = true;
        boss.Size = BossSize;
        boss.Inventory = template.CloseTemplate();
        boss.SwitchWeapon(weaponSlot);

        return true;
    }
}
