namespace OfflineDaoc.Launcher;

/// <summary>The Character Planner's texts and colours, kept out of the panel so they are unit tested without a window.</summary>
internal static class CharacterPlannerFormat
{
    /// <summary>A style's opening for its narrow column: "Follow-up of Javelin" becomes "Follows Javelin".</summary>
    public static string StyleOpeningShort(string opening)
    {
        const string followUp = "Follow-up of ";

        return opening.StartsWith(followUp, StringComparison.Ordinal) ? "Follows " + opening[followUp.Length..] : opening;
    }

    /// <summary>A style bonus with its sign ("+10", "-5"), or nothing when it is 0.</summary>
    public static string Bonus(int value)
    {
        return value == 0 ? string.Empty : value.ToString("+0;-0", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A style's growth rate with two decimals.</summary>
    public static string GrowthRate(double value)
    {
        return value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    public const string LoadingText = "Loading character data...";
    public const string EmptyText = "Character data not found — is the game installed?";
    public const string HintText = "Click a specialization to see what it unlocks. Click a realm ability for its description.";

    /// <summary>"Spec points: 120/154".</summary>
    public static string PointsText(string label, int spent, int total) => $"{label}: {spent}/{total}";

    /// <summary>Over budget (the level or realm rank was lowered after training) shows in red.</summary>
    public static Color PointsColor(int spent, int total) => spent > total ? DaocTheme.Danger : DaocTheme.GoldLight;

    /// <summary>"Polearm" or, for an autotrained line, "Polearm (auto 12)".</summary>
    public static string SpecNameText(string name, int? autotrainFloor) => autotrainFloor is int floor ? $"{name} (auto {floor})" : name;

    /// <summary>The spec row's cost column: the next level's cost, "max", "fixed" for a line that is not trainable, or "L20" before it is acquired.</summary>
    public static string SpecCostText(bool trainable, bool acquired, int levelAcquired, bool atMaximum, int nextCost)
    {
        if (!acquired)
        {
            return $"L{levelAcquired}";
        }

        if (!trainable)
        {
            return "fixed";
        }

        return atMaximum ? "max" : $"{nextCost} pts";
    }

    /// <summary>"2/5".</summary>
    public static string RealmAbilityLevelText(int level, int maxLevel) => $"{level}/{maxLevel}";

    /// <summary>The next level's cost, "max", or "n/a" for an ability without a cost table (it cannot be bought).</summary>
    public static string RealmAbilityCostText(int level, int maxLevel, int nextCost)
    {
        if (maxLevel == 0)
        {
            return "n/a";
        }

        return level >= maxLevel ? "max" : $"{nextCost} pts";
    }

    /// <summary>A spell, style or ability is available once the line is trained to its level.</summary>
    public static bool IsUnlocked(int unlockLevel, int trainedLevel) => unlockLevel <= trainedLevel;

    /// <summary>Baseline spells (the loader marks their line name) unlock at the character's level, not the spec's.</summary>
    public static bool UnlocksAtCharacterLevel(string lineName) =>
        lineName.EndsWith(CharacterPlannerData.BaselineSuffix, StringComparison.Ordinal);

    /// <summary>Unlocked rows in the normal text colour, the ones still to buy muted.</summary>
    public static Color UnlockColor(int unlockLevel, int trainedLevel) => IsUnlocked(unlockLevel, trainedLevel) ? DaocTheme.Text : DaocTheme.Muted;

    /// <summary>"Requires Determination 2, Purge 1" (names looked up by key), or empty without prerequisites.</summary>
    public static string PrerequisiteText(IReadOnlyList<string> prerequisites, Func<string, string> nameOf)
    {
        if (prerequisites.Count == 0)
        {
            return string.Empty;
        }

        IEnumerable<string> parts = prerequisites.Select(CharacterPlannerState.ParsePrerequisite).Select(prerequisite => prerequisite.Key == CharacterPlannerRealmAbilities.CharacterLevelRequirement
            ? $"character level {prerequisite.Level}"
            : $"{nameOf(prerequisite.Key)} {prerequisite.Level}");

        return "Requires " + string.Join(", ", parts);
    }
}
