namespace OfflineDaoc.Launcher;

/// <summary>
/// The Character Planner's playable classes, lifted from the server's class scripts because the database has no class
/// table: source/server/GameServer/playerclasses/{albion,midgard,hibernia}/Class*.cs. Id, Name and BaseClassName are the
/// arguments of each script's [CharacterClass(...)] attribute (ids from Enums/eCharacterClass.cs); SpecPointsMultiplier
/// is its m_specializationMultiplier; AutotrainSpecKeys is what its GetAutotrainableSkills() returns (Specs constants from
/// gameutils/SkillConstants.cs, e.g. Specs.Longbow = "Longbows"). Hunter declares Archery/Composite Bow but its override is
/// commented out, so it inherits CharacterClassBase's empty list.
/// The roster is this fork's Classic + Shrouded Isles one (launcher BotCharacterGenerator.Choices; the server's
/// ExpeditionPromotion.Post165Classes leaves out Heretic, Valkyrie, Warlock, Bainshee, Vampiir and the three Maulers),
/// plus the experimental Sluaghbinder, which CharacterPlannerData drops when the database has no career rows for it.
/// </summary>
internal static class CharacterPlannerClasses
{
    public static readonly IReadOnlyList<PlannerClass> All =
    [
        // Albion (playerclasses/albion)
        new(1, "Paladin", "Albion", "Fighter", 20, ["Slash", "Chants"]),
        new(2, "Armsman", "Albion", "Fighter", 20, ["Slash", "Thrust"]),
        new(3, "Scout", "Albion", "Rogue", 20, ["Archery", "Longbows"]),
        new(4, "Minstrel", "Albion", "Rogue", 15, ["Instruments"]),
        new(5, "Theurgist", "Albion", "Elementalist", 10, []),
        new(6, "Cleric", "Albion", "Acolyte", 10, []),
        new(7, "Wizard", "Albion", "Elementalist", 10, []),
        new(8, "Sorcerer", "Albion", "Mage", 10, []),
        new(9, "Infiltrator", "Albion", "Rogue", 25, ["Stealth"]),
        new(10, "Friar", "Albion", "Acolyte", 15, []),
        new(11, "Mercenary", "Albion", "Fighter", 20, ["Slash", "Thrust"]),
        new(12, "Necromancer", "Albion", "Disciple", 10, []),
        new(13, "Cabalist", "Albion", "Mage", 10, []),
        new(19, "Reaver", "Albion", "Fighter", 20, ["Slash", "Flexible"]),

        // Midgard (playerclasses/midgard)
        new(21, "Thane", "Midgard", "Viking", 20, []),
        new(22, "Warrior", "Midgard", "Viking", 20, ["Axe", "Hammer", "Sword"]),
        new(23, "Shadowblade", "Midgard", "Rogue", 22, ["Stealth"]),
        new(24, "Skald", "Midgard", "Viking", 15, []),
        new(25, "Hunter", "Midgard", "Rogue", 20, []),
        new(26, "Healer", "Midgard", "Seer", 10, []),
        new(27, "Spiritmaster", "Midgard", "Mystic", 10, []),
        new(28, "Shaman", "Midgard", "Seer", 10, []),
        new(29, "Runemaster", "Midgard", "Mystic", 10, []),
        new(30, "Bonedancer", "Midgard", "Mystic", 10, []),
        new(31, "Berserker", "Midgard", "Viking", 20, []),
        new(32, "Savage", "Midgard", "Viking", 15, []),

        // Hibernia (playerclasses/hibernia)
        new(40, "Eldritch", "Hibernia", "Magician", 10, []),
        new(41, "Enchanter", "Hibernia", "Magician", 10, []),
        new(42, "Mentalist", "Hibernia", "Magician", 10, []),
        new(43, "Blademaster", "Hibernia", "Guardian", 20, []),
        new(44, "Hero", "Hibernia", "Guardian", 20, []),
        new(45, "Champion", "Hibernia", "Guardian", 20, []),
        new(46, "Warden", "Hibernia", "Naturalist", 15, []),
        new(47, "Druid", "Hibernia", "Naturalist", 10, []),
        new(48, "Bard", "Hibernia", "Naturalist", 15, []),
        new(49, "Nightshade", "Hibernia", "Stalker", 22, ["Stealth"]),
        new(50, "Ranger", "Hibernia", "Stalker", 20, ["Archery", "Recurve Bow"]),
        new(55, "Animist", "Hibernia", "Forester", 10, []),
        new(56, "Valewalker", "Hibernia", "Forester", 15, []),
        new(63, "Sluaghbinder", "Hibernia", "Acolyte", 10, []),
    ];
}
