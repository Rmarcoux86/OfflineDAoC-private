namespace OfflineDaoc.Launcher;

/// <summary>
/// Realm ability costs, levels and prerequisites, lifted from the server's handlers because the database only holds names
/// and descriptions. Keyed by the handler class the server instantiates: the last segment of Ability.Implementation
/// (SkillBase.GetNewAbilityInstance resolves it ignoring case, hence the case-insensitive table).
/// Sources: source/server/GameServer/realmabilities_atlasOF/handlers/*.cs (CostForUpgrade, MaxLevel, CheckRequirement),
/// realmabilities_atlasOF/AtlasOF_RAHelpers.cs (GetCommonUpgradeCostFor5LevelsRA = 1/3/6/10/14,
/// GetCommonUpgradeCostFor3LevelsRA = 3/6/10) and the bases in realmabilities/handlers (RealmAbility.cs: TimedRealmAbility
/// is the active base and has MaxLevel 3, L5RealmAbility the passive base with MaxLevel 5; MasteryOfStealthAbility.cs,
/// AvoidanceOfMagicAbility).
/// Those maximum levels are the ones with use_new_passives_ras_scaling and use_new_actives_ras_scaling False, the default
/// (ServerProperties.cs) and the value in this fork's database. With either True the bases raise MaxLevel to 9 / 5, but
/// every handler that inherits that MaxLevel charges 1000 points for the extra levels, so nothing below is reachable
/// differently; CharacterPlannerData only warns about it.
/// Prerequisites name the handler class of the required ability (GetAbility&lt;T&gt; in the handlers, see
/// AtlasRAHelpers.GetAug*Level) and the level it needs; <see cref="CharacterLevelRequirement"/> is the character's level.
/// </summary>
internal static class CharacterPlannerRealmAbilities
{
    internal sealed record Entry(bool Passive, IReadOnlyList<int> CostPerLevel,
        IReadOnlyList<(string Requirement, int Level)> Prerequisites);

    /// <summary>A prerequisite on the character's level rather than on another realm ability (Striking the Soul).</summary>
    public const string CharacterLevelRequirement = "Level";

    // AtlasRAHelpers.GetCommonUpgradeCostFor5LevelsRA, over L5RealmAbility's MaxLevel 5.
    private static readonly int[] Passive5 = [1, 3, 6, 10, 14];

    // AtlasRAHelpers.GetCommonUpgradeCostFor3LevelsRA, over MaxLevel 3.
    private static readonly int[] Active3 = [3, 6, 10];

    private const string AugStr = "AtlasOF_RAStrengthEnhancer";
    private const string AugCon = "AtlasOF_RAConstitutionEnhancer";
    private const string AugDex = "AtlasOF_RADexterityEnhancer";
    private const string AugQui = "AtlasOF_RAQuicknessEnhancer";
    private const string AugAcuity = "AtlasOF_RAAcuityEnhancer";

    private static Entry Passive(int[] costs, params (string Requirement, int Level)[] prerequisites) =>
        new(true, costs, prerequisites);

    private static Entry Active(int[] costs, params (string Requirement, int Level)[] prerequisites) =>
        new(false, costs, prerequisites);

    public static readonly IReadOnlyDictionary<string, Entry> ByImplementation =
        new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
        {
            // Passives (L5RealmAbility / RAPropertyEnhancer / RAStatEnhancer). AtlasOF_RAStatEnhancer.cs, AtlasOF_Masteries.cs,
            // AtlasOF_*Ability.cs.
            [AugStr] = Passive(Passive5),
            [AugCon] = Passive(Passive5),
            [AugDex] = Passive(Passive5),
            [AugQui] = Passive(Passive5),
            [AugAcuity] = Passive(Passive5),
            ["AtlasOF_RAEndRegenEnhancer"] = Passive([1]), // Tireless: MaxLevel 1, no NF level-40 requirement.
            ["AvoidanceOfMagicAbility"] = Passive(Passive5), // realmabilities/handlers: its own 1/3/6/10/14 switch.
            ["AtlasOF_ToughnessAbility"] = Passive(Passive5),
            ["AtlasOF_LifterAbility"] = Passive(Passive5),
            ["AtlasOF_LongWindAbility"] = Passive(Passive5),
            ["AtlasOF_Regeneration"] = Passive(Passive5),
            ["AtlasOF_VeilRecovery"] = Passive(Passive5),
            ["AtlasOF_ArrowSalvaging"] = Passive(Passive5),
            ["AtlasOF_MasteryOfWater"] = Passive(Passive5),
            ["AtlasOF_DeterminationAbility"] = Passive([1, 2, 3, 6, 10]), // "OF Det circa 2003" costs, MaxLevel 5.
            ["AtlasOF_DeterminationHybridAbility"] = Passive([1, 2, 3]), // Same costs, MaxLevel 3.
            ["AtlasOF_Dodger"] = Passive(Passive5, (AugQui, 2)),
            ["AtlasOF_DualistsReflexes"] = Passive(Passive5, (AugDex, 2)),
            ["AtlasOF_FalconsEye"] = Passive(Passive5, (AugDex, 2)),
            ["AtlasOF_MasteryOfBlocking"] = Passive(Passive5, (AugDex, 2)),
            ["AtlasOF_MasteryOfPain"] = Passive(Passive5, (AugDex, 2)),
            ["AtlasOF_MasteryOfParrying"] = Passive(Passive5, (AugDex, 2)),
            ["AtlasOF_MasteryOfArchery"] = Passive(Passive5, (AugDex, 3)),
            ["AtlasOF_MasteryOfArms"] = Passive(Passive5, (AugStr, 3)), // Friar: AugDex 3, see ClassPrerequisites.
            ["AtlasOF_MasteryOfHealing"] = Passive(Passive5, (AugAcuity, 2)),
            ["AtlasOF_MasteryOfMagery"] = Passive(Passive5, (AugAcuity, 2)),
            ["AtlasOF_MasteryOfTheArcane"] = Passive(Passive5, (AugAcuity, 2)),
            ["AtlasOF_MasteryOfTheArt"] = Passive(Passive5, (AugAcuity, 3)),
            ["AtlasOF_SerenityAbility"] = Passive(Passive5, (AugAcuity, 2)),
            ["AtlasOF_WildArcanaAbility"] = Passive(Passive5, (AugAcuity, 2)),
            ["AtlasOF_WildHealingAbility"] = Passive(Passive5, (AugAcuity, 2)),
            ["AtlasOF_WildMinionAbility"] = Passive(Passive5, (AugAcuity, 2)),
            ["AtlasOF_WildPowerAbility"] = Passive(Passive5, (AugAcuity, 2)),
            ["AtlasOF_EtherealBondAbility"] = Passive(Passive5, ("AtlasOF_SerenityAbility", 2)),

            // The database points AtlasOF_MasteryOfStealth at the base MasteryOfStealthAbility (MaxLevel 3, costs
            // 3/6/10, L5RealmAbility's always-true requirement), not at AtlasOF_MasteryOfStealth (same costs, AugQui 2).
            ["MasteryOfStealthAbility"] = Passive([3, 6, 10]),
            ["AtlasOF_MasteryOfStealth"] = Passive(Active3, (AugQui, 2)),

            // Plain RealmAbility subclasses (not timed): passive.
            ["AtlasOF_PreventFlight"] = Passive([14]),
            ["AtlasOF_SeeHidden"] = Passive([8]),

            // Actives (TimedRealmAbility) with three levels.
            ["AtlasOF_FirstAid"] = Active(Active3),
            ["AtlasOF_EmptyMind"] = Active(Active3),
            ["AtlasOF_MajesticWill"] = Active(Active3),
            ["AtlasOF_MysticCrystalLoreAbility"] = Active(Active3), // TimedRealmAbility's MaxLevel 3.
            ["AtlasOF_RainOfAnnihilation"] = Active(Active3), // AtlasOF_RainOfBase.
            ["AtlasOF_RainOfFire"] = Active(Active3),
            ["AtlasOF_RainOfIce"] = Active(Active3),
            ["AtlasOF_ArmorOfFaith"] = Active(Active3, (AugCon, 3)),
            ["AtlasOF_AvoidPain"] = Active(Active3, (AugCon, 3)),
            ["AtlasOF_HailOfBlows"] = Active(Active3, (AugDex, 3)),
            ["AtlasOF_WhirlingDervish"] = Active(Active3, (AugDex, 3)),

            // Actives with one level.
            ["AtlasOF_PurgeAbility"] = Active([10]),
            ["AtlasOF_PurgeAbilityReduced"] = Active([4]),
            ["AtlasOF_GroupPurge"] = Active([14]),
            ["AtlasOF_SecondWind"] = Active([10], (AugCon, 3)),
            ["AtlasOF_BattleYell"] = Active([14], (AugCon, 3)),
            ["AtlasOF_Bladedance"] = Active([14], (AugDex, 3)),
            ["AtlasOF_Concentration"] = Active([10], (AugAcuity, 3)),
            ["AtlasOF_MasteryOfConcentration"] = Active([14], (AugAcuity, 3)), // Class is AtlasOF_MasteryofConcentration.
            ["AtlasOF_IgnorePain"] = Active([14], ("AtlasOF_FirstAid", 2)),
            ["AtlasOF_IgnorePainTank"] = Active([8], ("AtlasOF_FirstAid", 2)), // Inherits AtlasOF_IgnorePain's requirement.
            ["AtlasOF_Trip"] = Active([10]),
            ["AtlasOF_Grapple"] = Active([14], ("AtlasOF_Trip", 1)), // HasAbilityType(typeof(AtlasOF_Trip)).
            ["AtlasOF_Longshot"] = Active([6]),
            ["AtlasOF_Volley"] = Active([8], ("AtlasOF_Longshot", 1)),
            ["AtlasOF_RagingPower"] = Active([10], ("AtlasOF_MysticCrystalLoreAbility", 2)), // MCL level > 1.
            ["AtlasOF_StrikingTheSoul"] = Active([10], (CharacterLevelRequirement, 40)), // player.Level >= 40.
            ["AtlasOF_AmelioratingMelodies"] = Active([14]),
            ["AtlasOF_BatteryOfLife"] = Active([10]),
            ["AtlasOF_BrilliantAura"] = Active([14]),
            ["AtlasOF_BunkerOfFaith"] = Active([10]),
            ["AtlasOF_CorporealDisintegration"] = Active([14]),
            ["AtlasOF_CripplingPlague"] = Active([10]),
            ["AtlasOF_DefenderOfTheVale"] = Active([10]),
            ["AtlasOF_ExcitedFrenzy"] = Active([6]),
            ["AtlasOF_FaithHealing"] = Active([14]),
            ["AtlasOF_ForestheartAmbusher"] = Active([10]),
            ["AtlasOF_FuryOfTheGods"] = Active([14]),
            ["AtlasOF_Ichor"] = Active([14]),
            ["AtlasOF_Juggernaut"] = Active([14]),
            ["AtlasOF_NegativeMaelstrom"] = Active([14]),
            ["AtlasOF_PerfectRecovery"] = Active([14]),
            ["AtlasOF_ReflexAttack"] = Active([14]),
            ["AtlasOF_ResilienceOfDeath"] = Active([10]),
            ["AtlasOF_RuneOfDecimation"] = Active([14]),
            ["AtlasOF_ShadowRun"] = Active([10]),
            ["AtlasOF_SiegeBolt"] = Active([14]),
            ["AtlasOF_SoldiersBarricade"] = Active([10]),
            ["AtlasOF_SpeedOfSound"] = Active([10]),
            ["AtlasOF_StaticTempest"] = Active([14]),
            ["AtlasOF_ThornweedField"] = Active([14]),
            ["AtlasOF_TrueSight"] = Active([10]),
            ["AtlasOF_UnquenchableThirst"] = Active([10]),
            ["AtlasOF_Vanish"] = Active([10]),
            ["AtlasOF_Viper"] = Active([14]),
            ["AtlasOF_VolcanicPillar"] = Active([14]),
            ["AtlasOF_WhipOfEncouragement"] = Active([6]),
            ["AtlasOF_WrathOfTheChampion"] = Active([10]),

            // StyleRealmAbility: MaxLevel 1, cost 10.
            ["AtlasOF_StyleDoombringer"] = Active([10]),
            ["AtlasOF_StyleRavager"] = Active([10]),
            ["AtlasOF_StyleRazorback"] = Active([10]),
            ["AtlasOF_StyleTundra"] = Active([10]),
            ["AtlasOF_StyleVoid"] = Active([10]),
            ["AtlasOF_StyleWinterMoon"] = Active([10]),
        };

    /// <summary>
    /// Class-specific requirements that replace the table's: AtlasOF_MasteryOfArms.CheckRequirement asks a Friar for
    /// Augmented Dexterity 3 instead of Augmented Strength 3.
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Implementation, int ClassId), IReadOnlyList<(string Requirement, int Level)>>
        ClassPrerequisites = new Dictionary<(string Implementation, int ClassId), IReadOnlyList<(string Requirement, int Level)>>
        {
            [("AtlasOF_MasteryOfArms", 10)] = [(AugDex, 3)],
        };
}
