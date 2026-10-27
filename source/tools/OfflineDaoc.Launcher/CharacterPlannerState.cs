using System.Globalization;

namespace OfflineDaoc.Launcher;

/// <summary>
/// The Character Planner's model (no WinForms): the chosen class, character level (1-50), realm rank (1-14) and realm
/// level within the rank (0-9), the trained level of each spec line and of each realm ability, and the point budgets.
/// Spec lines start at level 1 once the character is old enough for them (LevelAcquired), autotrained lines start at
/// their free level (CharacterPlannerRules.AutotrainLevel), and a line can never pass the character level. Lines that
/// are not trainable follow the character level and cost nothing. Realm abilities cost CostPerLevel[level] for the next
/// level and need their prerequisites ("{KeyName}:{level}") trained first; a prerequisite cannot be lowered while a
/// trained realm ability still needs it.
/// </summary>
internal sealed class CharacterPlannerState
{
    public const int MaxRealmLevelWithinRank = 9;

    private readonly Dictionary<string, int> _specLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _realmAbilityLevels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The selected class, or null before one is chosen.</summary>
    public PlannerClass? Class { get; private set; }

    /// <summary>The selected class's spec lines.</summary>
    public IReadOnlyList<PlannerSpec> Specs { get; private set; } = [];

    /// <summary>The selected class's realm abilities.</summary>
    public IReadOnlyList<PlannerRealmAbility> RealmAbilities { get; private set; } = [];

    /// <summary>Character level, 1 to 50 (default 50).</summary>
    public int Level { get; private set; } = CharacterPlannerRules.MaxLevel;

    /// <summary>Realm rank, 1 to 14.</summary>
    public int RealmRank { get; private set; } = 1;

    /// <summary>Realm level within the rank, 0 to 9 (RR5L3 is rank 5, level 3).</summary>
    public int RealmLevel { get; private set; }

    /// <summary>Spec key to trained level (every spec line of the class is listed).</summary>
    public IReadOnlyDictionary<string, int> SpecLevels => _specLevels;

    /// <summary>Realm ability key to trained level (only trained ones are listed).</summary>
    public IReadOnlyDictionary<string, int> RealmAbilityLevels => _realmAbilityLevels;

    public int SpecPointsTotal => Class is null ? 0 : CharacterPlannerRules.SpecPointsAtLevel(Class.SpecPointsMultiplier, Level);

    public int SpecPointsSpent => Specs.Sum(spec => SpecCostOf(spec, SpecLevel(spec.KeyName)));

    /// <summary>Negative when the character level was lowered below what the trained lines need.</summary>
    public int SpecPointsRemaining => SpecPointsTotal - SpecPointsSpent;

    public int RealmPointsTotal => CharacterPlannerRules.RealmPointsAtRank(RealmRank, RealmLevel, Level);

    public int RealmPointsSpent => RealmAbilities.Sum(ability => RealmAbilityCostOf(ability, RealmAbilityLevel(ability.KeyName)));

    /// <summary>Negative when the realm rank was lowered below what the trained realm abilities need.</summary>
    public int RealmPointsRemaining => RealmPointsTotal - RealmPointsSpent;

    /// <summary>Chooses a class from the data source, keeping the level and realm rank; every line and ability starts untrained.</summary>
    public void SelectClass(ICharacterPlannerSource source, PlannerClass playerClass)
    {
        SelectClass(playerClass, source.SpecsFor(playerClass.Id), source.RealmAbilitiesFor(playerClass.Id));
    }

    /// <summary>Chooses a class with its spec lines and realm abilities, keeping the level and realm rank; everything starts untrained.</summary>
    public void SelectClass(PlannerClass playerClass, IReadOnlyList<PlannerSpec> specs, IReadOnlyList<PlannerRealmAbility> realmAbilities)
    {
        Class = playerClass;
        Specs = specs;
        RealmAbilities = realmAbilities;
        Reset();
    }

    /// <summary>Sets the character level (clamped to 1-50). Lines above it drop to it; autotrained lines rise to their new free level.</summary>
    public void SetLevel(int level)
    {
        Level = Math.Clamp(level, 1, CharacterPlannerRules.MaxLevel);

        foreach (PlannerSpec spec in Specs)
        {
            _specLevels[spec.KeyName] = Math.Clamp(SpecLevel(spec.KeyName), SpecMinimum(spec.KeyName), SpecMaximum(spec.KeyName));
        }
    }

    /// <summary>Sets the realm rank (clamped to 1-14) and the realm level within it (clamped to 0-9). Trained abilities are kept.</summary>
    public void SetRealmRank(int realmRank, int realmLevel)
    {
        RealmRank = Math.Clamp(realmRank, 1, CharacterPlannerRules.MaxRealmRank);
        RealmLevel = Math.Clamp(realmLevel, 0, MaxRealmLevelWithinRank);
    }

    /// <summary>Untrains every line (back to its floor) and every realm ability; the class, level and realm rank stay.</summary>
    public void Reset()
    {
        _specLevels.Clear();
        _realmAbilityLevels.Clear();

        foreach (PlannerSpec spec in Specs)
        {
            _specLevels[spec.KeyName] = SpecMinimum(spec.KeyName);
        }
    }

    /// <summary>Forgets the class and returns to the defaults: no class, level 50, RR1L0.</summary>
    public void Clear()
    {
        Class = null;
        Specs = [];
        RealmAbilities = [];
        Level = CharacterPlannerRules.MaxLevel;
        RealmRank = 1;
        RealmLevel = 0;
        Reset();
    }

    public bool IsAutotrained(string specKey) =>
        Class is not null && Class.AutotrainSpecKeys.Contains(specKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>The free level an autotrained line has at the current character level, or null for other lines.</summary>
    public int? AutotrainFloor(string specKey) => IsAutotrained(specKey) ? CharacterPlannerRules.AutotrainLevel(Level) : null;

    /// <summary>True when the character is old enough for the line (LevelAcquired at or below the level).</summary>
    public bool IsAcquired(string specKey)
    {
        PlannerSpec? spec = FindSpec(specKey);

        return spec is not null && spec.LevelAcquired <= Level;
    }

    /// <summary>The lowest level the line can hold: 0 before it is acquired, the character level for a line that is not trainable, else 1 or the autotrain floor.</summary>
    public int SpecMinimum(string specKey)
    {
        PlannerSpec? spec = FindSpec(specKey);

        if (spec is null || spec.LevelAcquired > Level)
        {
            return 0;
        }

        if (!spec.Trainable)
        {
            return Level;
        }

        return Math.Max(1, IsAutotrained(specKey) ? CharacterPlannerRules.AutotrainLevel(Level) : 1);
    }

    /// <summary>The highest level the line can hold: the character level once acquired, else 0.</summary>
    public int SpecMaximum(string specKey) => IsAcquired(specKey) ? Level : 0;

    public int SpecLevel(string specKey) => _specLevels.TryGetValue(specKey, out int level) ? level : 0;

    /// <summary>The points spent on this line at its trained level (free levels of an autotrained line excluded).</summary>
    public int SpecPointsIn(string specKey)
    {
        PlannerSpec? spec = FindSpec(specKey);

        return spec is null ? 0 : SpecCostOf(spec, SpecLevel(specKey));
    }

    /// <summary>The points the next level of this line costs, or 0 when it cannot go higher.</summary>
    public int SpecRaiseCost(string specKey)
    {
        PlannerSpec? spec = FindSpec(specKey);
        int level = SpecLevel(specKey);

        if (spec is null || !spec.Trainable || level >= SpecMaximum(specKey))
        {
            return 0;
        }

        return SpecCostOf(spec, level + 1) - SpecCostOf(spec, level);
    }

    /// <summary>True when the line is trainable, below the character level and the next level is affordable.</summary>
    public bool CanRaiseSpec(string specKey)
    {
        PlannerSpec? spec = FindSpec(specKey);

        return spec is not null && spec.Trainable && SpecLevel(specKey) < SpecMaximum(specKey) && SpecRaiseCost(specKey) <= SpecPointsRemaining;
    }

    /// <summary>True when the line is trainable and above its floor (1, or the autotrain level).</summary>
    public bool CanLowerSpec(string specKey)
    {
        PlannerSpec? spec = FindSpec(specKey);

        return spec is not null && spec.Trainable && SpecLevel(specKey) > SpecMinimum(specKey);
    }

    public bool RaiseSpec(string specKey)
    {
        if (!CanRaiseSpec(specKey))
        {
            return false;
        }

        PlannerSpec spec = FindSpec(specKey)!;
        _specLevels[spec.KeyName] = SpecLevel(specKey) + 1;

        return true;
    }

    public bool LowerSpec(string specKey)
    {
        if (!CanLowerSpec(specKey))
        {
            return false;
        }

        PlannerSpec spec = FindSpec(specKey)!;
        _specLevels[spec.KeyName] = SpecLevel(specKey) - 1;

        return true;
    }

    /// <summary>
    /// Sets a line's level, clamped between its floor and the character level, and (when raising) to the highest level the
    /// remaining points pay for. Returns the level the line ends at (unchanged for an unknown or untrainable line).
    /// </summary>
    public int SetSpec(string specKey, int level)
    {
        PlannerSpec? spec = FindSpec(specKey);

        if (spec is null || !spec.Trainable)
        {
            return SpecLevel(specKey);
        }

        int current = SpecLevel(specKey);
        int target = Math.Clamp(level, SpecMinimum(specKey), SpecMaximum(specKey));
        int remaining = SpecPointsRemaining;

        while (target > current && SpecCostOf(spec, target) - SpecCostOf(spec, current) > remaining)
        {
            target--;
        }

        _specLevels[spec.KeyName] = target;

        return target;
    }

    public int RealmAbilityLevel(string abilityKey) => _realmAbilityLevels.TryGetValue(abilityKey, out int level) ? level : 0;

    /// <summary>The points the next level of this realm ability costs, or 0 at its maximum.</summary>
    public int RealmAbilityRaiseCost(string abilityKey)
    {
        PlannerRealmAbility? ability = FindRealmAbility(abilityKey);
        int level = RealmAbilityLevel(abilityKey);

        return ability is null || level >= ability.MaxLevel ? 0 : ability.CostPerLevel[level];
    }

    /// <summary>True when every prerequisite is trained to its level (a prerequisite the class cannot buy is never met).</summary>
    public bool PrerequisitesMet(string abilityKey)
    {
        PlannerRealmAbility? ability = FindRealmAbility(abilityKey);

        if (ability is null)
        {
            return false;
        }

        foreach (string prerequisite in ability.Prerequisites)
        {
            (string key, int level) = ParsePrerequisite(prerequisite);
            int have = key == CharacterPlannerRealmAbilities.CharacterLevelRequirement ? Level : RealmAbilityLevel(key);

            if (have < level)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the ability is below its maximum, its prerequisites are met and the next level is affordable.</summary>
    public bool CanRaiseRealmAbility(string abilityKey)
    {
        PlannerRealmAbility? ability = FindRealmAbility(abilityKey);

        return ability is not null && RealmAbilityLevel(abilityKey) < ability.MaxLevel && PrerequisitesMet(abilityKey)
            && RealmAbilityRaiseCost(abilityKey) <= RealmPointsRemaining;
    }

    /// <summary>True when the ability is trained and no other trained ability needs its current level.</summary>
    public bool CanLowerRealmAbility(string abilityKey)
    {
        int level = RealmAbilityLevel(abilityKey);

        return level > 0 && RealmAbilitiesNeeding(abilityKey, level - 1).Count == 0;
    }

    /// <summary>The trained realm abilities whose prerequisites would break if this ability were at the given level.</summary>
    public IReadOnlyList<PlannerRealmAbility> RealmAbilitiesNeeding(string abilityKey, int level)
    {
        var needing = new List<PlannerRealmAbility>();

        foreach (PlannerRealmAbility ability in RealmAbilities)
        {
            if (RealmAbilityLevel(ability.KeyName) == 0 || string.Equals(ability.KeyName, abilityKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool needs = ability.Prerequisites.Select(ParsePrerequisite)
                .Any(prerequisite => string.Equals(prerequisite.Key, abilityKey, StringComparison.OrdinalIgnoreCase) && prerequisite.Level > level);

            if (needs)
            {
                needing.Add(ability);
            }
        }

        return needing;
    }

    public bool RaiseRealmAbility(string abilityKey)
    {
        if (!CanRaiseRealmAbility(abilityKey))
        {
            return false;
        }

        PlannerRealmAbility ability = FindRealmAbility(abilityKey)!;
        _realmAbilityLevels[ability.KeyName] = RealmAbilityLevel(abilityKey) + 1;

        return true;
    }

    /// <summary>Lowers the ability one level; refused when untrained or when another trained ability depends on it.</summary>
    public bool LowerRealmAbility(string abilityKey)
    {
        if (!CanLowerRealmAbility(abilityKey))
        {
            return false;
        }

        PlannerRealmAbility ability = FindRealmAbility(abilityKey)!;
        int level = RealmAbilityLevel(abilityKey) - 1;

        if (level == 0)
        {
            _realmAbilityLevels.Remove(ability.KeyName);

            return true;
        }

        _realmAbilityLevels[ability.KeyName] = level;

        return true;
    }

    /// <summary>
    /// The build on one line for the clipboard: "Armsman L50 RR5L3 — Polearm 50, Shield 42, Parry 28 — Determination 5,
    /// Purge 1". Lines above level 1 and trained realm abilities, highest first.
    /// </summary>
    public string Summary()
    {
        if (Class is null)
        {
            return "No class selected";
        }

        string specs = string.Join(", ", Specs
            .Where(spec => spec.Trainable && SpecLevel(spec.KeyName) > 1)
            .OrderByDescending(spec => SpecLevel(spec.KeyName))
            .ThenBy(spec => spec.Name, StringComparer.OrdinalIgnoreCase)
            .Select(spec => $"{spec.Name} {SpecLevel(spec.KeyName)}"));
        string abilities = string.Join(", ", RealmAbilities
            .Where(ability => RealmAbilityLevel(ability.KeyName) > 0)
            .OrderByDescending(ability => RealmAbilityLevel(ability.KeyName))
            .ThenBy(ability => ability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ability => $"{ability.Name} {RealmAbilityLevel(ability.KeyName)}"));

        return $"{Class.Name} L{Level} RR{RealmRank}L{RealmLevel} — {(specs.Length > 0 ? specs : "no specs trained")}"
            + $" — {(abilities.Length > 0 ? abilities : "no realm abilities")}";
    }

    /// <summary>Reads a "{KeyName}:{level}" prerequisite. A missing or unreadable level means level 1.</summary>
    public static (string Key, int Level) ParsePrerequisite(string prerequisite)
    {
        int colon = prerequisite.LastIndexOf(':');

        if (colon < 0)
        {
            return (prerequisite.Trim(), 1);
        }

        string key = prerequisite[..colon].Trim();
        bool parsed = int.TryParse(prerequisite[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level);

        return (key, parsed && level > 0 ? level : 1);
    }

    private PlannerSpec? FindSpec(string specKey) =>
        Specs.FirstOrDefault(spec => string.Equals(spec.KeyName, specKey, StringComparison.OrdinalIgnoreCase));

    private PlannerRealmAbility? FindRealmAbility(string abilityKey) =>
        RealmAbilities.FirstOrDefault(ability => string.Equals(ability.KeyName, abilityKey, StringComparison.OrdinalIgnoreCase));

    private int SpecCostOf(PlannerSpec spec, int level) =>
        spec.Trainable ? CharacterPlannerRules.SpecCost(level, IsAutotrained(spec.KeyName), Level) : 0;

    private static int RealmAbilityCostOf(PlannerRealmAbility ability, int level) =>
        ability.CostPerLevel.Take(Math.Min(level, ability.MaxLevel)).Sum();
}
