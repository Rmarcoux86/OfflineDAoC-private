namespace OfflineDaoc.Launcher;

/// <summary>
/// The Character Planner's data contract: what the panel (CharacterPlannerPanel) shows and what the loader
/// (CharacterPlannerData, from the game's SQLite database and tables lifted from the server) provides.
/// </summary>

/// <summary>A playable class. Realm is "Albion", "Midgard" or "Hibernia".</summary>
internal sealed record PlannerClass(int Id, string Name, string Realm, string BaseClassName, int SpecPointsMultiplier,
    IReadOnlyList<string> AutotrainSpecKeys);

/// <summary>A specialization line the class can train. Trainable is false for lines granted at a fixed level only.</summary>
internal sealed record PlannerSpec(string KeyName, string Name, int LevelAcquired, bool Trainable);

/// <summary>A spell in a spec's spell list (or baseline list). Level is the spec (or class) level it unlocks at.</summary>
internal sealed record PlannerSpell(string Name, int Level, string LineName, string Description, string Kind);

/// <summary>A combat style. Level is the spec level it unlocks at; Opening describes the positional/reactionary requirement.</summary>
internal sealed record PlannerStyle(string Name, int Level, string Opening, string Description);

/// <summary>An ability granted by a spec (Evade, Protect, ...). Level is the spec level it unlocks at.</summary>
internal sealed record PlannerAbility(string Name, int Level);

/// <summary>
/// A realm ability the class can buy. CostPerLevel[i] is the cost of level i+1 (so Length is MaxLevel).
/// Prerequisites are "{KeyName}:{level}" pairs that must be trained first; empty for most.
/// </summary>
internal sealed record PlannerRealmAbility(string KeyName, string Name, string Description, bool Passive,
    IReadOnlyList<int> CostPerLevel, IReadOnlyList<string> Prerequisites)
{
    public int MaxLevel => CostPerLevel.Count;
}

internal interface ICharacterPlannerSource
{
    IReadOnlyList<PlannerClass> Classes { get; }
    IReadOnlyList<PlannerSpec> SpecsFor(int classId);
    IReadOnlyList<PlannerSpell> SpellsFor(int classId, string specKey);
    IReadOnlyList<PlannerStyle> StylesFor(int classId, string specKey);
    IReadOnlyList<PlannerAbility> AbilitiesFor(int classId, string specKey);
    IReadOnlyList<PlannerRealmAbility> RealmAbilitiesFor(int classId);
}

/// <summary>The training rules, as GamePlayer.VerifySpecPoints and the trainer apply them. Pure; unit tested.</summary>
internal static class CharacterPlannerRules
{
    public const int MaxLevel = 50;
    public const int MaxRealmRank = 14;

    /// <summary>The highest realm level: GamePlayer.REALMPOINTS_FOR_LEVEL has entries up to 130, which is RR14 L0.</summary>
    public const int MaxRealmLevel = 130;

    /// <summary>
    /// Total spec points a class has earned by this level (GamePlayer.VerifySpecPoints, full levels only). The server
    /// starts the total at -1, so a level 1 character has 0 points (every line starts at 1 for free).
    /// </summary>
    public static int SpecPointsAtLevel(int specPointsMultiplier, int level)
    {
        int points = -1;

        for (int i = 1; i <= level; i++)
        {
            if (i <= 5)
            {
                points += i;
            }

            if (i > 5)
            {
                points += specPointsMultiplier * i / 10;
            }

            if (i > 40)
            {
                points += specPointsMultiplier * (i - 1) / 20;
            }
        }

        return Math.Max(0, points);
    }

    /// <summary>Points spent to hold a spec at this level: 2 + 3 + ... + level (GamePlayer.VerifySpecPoints).</summary>
    public static int SpecCost(int specLevel)
    {
        return specLevel < 2 ? 0 : (specLevel * (specLevel + 1) - 2) / 2;
    }

    /// <summary>
    /// The free levels an autotrained line gets at this character level: level / 4, at least 1
    /// (GamePlayer.GetAutoTrainPoints max_autotrain; level 1 is free anyway, SpecCost(1) is 0).
    /// </summary>
    public static int AutotrainLevel(int level)
    {
        return Math.Max(1, level / 4);
    }

    /// <summary>Points spent on an autotrained line: only the levels above the free ones cost anything.</summary>
    public static int SpecCost(int specLevel, bool autotrain, int level)
    {
        if (!autotrain)
        {
            return SpecCost(specLevel);
        }

        int free = AutotrainLevel(level);

        return Math.Max(0, SpecCost(specLevel) - SpecCost(free));
    }

    /// <summary>Realm ability points at a realm rank, rank 1 to 14: one per realm level, ten levels per rank (RR1 L0 = 0).</summary>
    public static int RealmPointsAtRank(int realmRank, int realmLevelWithinRank)
    {
        return Math.Max(0, (realmRank - 1) * 10 + realmLevelWithinRank);
    }

    /// <summary>
    /// Realm ability points as AbstractServerRules.GetPlayerRealmPointsTotal grants them (the fork runs the Normal rules):
    /// the realm level, but at least 1 once the character is past level 19.
    /// </summary>
    public static int RealmPointsAtRank(int realmRank, int realmLevelWithinRank, int characterLevel)
    {
        int points = RealmPointsAtRank(realmRank, realmLevelWithinRank);

        return characterLevel > 19 ? Math.Max(1, points) : points;
    }
}
