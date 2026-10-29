namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// The Gauntlet's fixed rules and timings (docs/EXPEDITION-GAUNTLET-PLAN.md). Each rule is one switch here so it can be
/// changed without hunting through the round loop.
/// </summary>
public static class GauntletRules
{
    /// <summary>
    /// Whether dying in the Gauntlet ends the run. False is an owner decision (2026-10-25) so the Gauntlet can be
    /// tested: the character is kept, the champion is removed, the player releases at the arena entrance and the round
    /// restarts. Permadeath may come back later by flipping this.
    /// </summary>
    public const bool DeathEndsRun = false;

    /// <summary>Seconds of rest between a champion's death and the next round's champion.</summary>
    public const int RestSeconds = 15;

    /// <summary>How far (world units) the champion may chase from where it appeared before it is pulled back (covers the player spot, about 1,700 away).</summary>
    public const int LeashRadius = 2500;

    /// <summary>
    /// The least time the champion spends buffing where it stands before it can be challenged; it is ready only once
    /// every pre-fight buff it can cast is up (owner 2026-10-27, GauntletChampionBrain).
    /// </summary>
    /// <remarks>Its buffs land at once (owner 2026-10-28: "speed up gwyraan's buff phase"), so this is short.</remarks>
    public const int ChampionBuffSeconds = 3;

    /// <summary>
    /// A safety net: after this long the champion is ready whatever buff is still missing (a buff that can never land
    /// would otherwise hold the round forever). Gwyraan needs about 10 s (his pet's summon, then every buff at once).
    /// </summary>
    public const int ChampionBuffMaxSeconds = 90;

    /// <summary>Seconds after a Gauntlet death before the player is released to the arena entrance automatically.</summary>
    public const int AutoReleaseSeconds = 10;

    /// <summary>
    /// While the Gauntlet was being tested (owner 2026-10-25), any character without the Hardcore flag could enter, not
    /// only one that has won its expedition; such a session is not saved. Off since 2026-10-27 (owner: winners only, it
    /// was an escape teleport and a way to farm drops). A GM may still enter without a won run.
    /// </summary>
    public const bool TestEntryForAnyCharacter = false;

    /// <summary>
    /// The Gauntlet is one fight against each champion (owner 2026-10-25: "it should just end after fighting each boss
    /// once"): four in a seeded order, then Preesh.
    /// </summary>
    public const int Rounds = 5;

    /// <summary>
    /// The rounds of a hard-mode winner's Gauntlet (owner 2026-10-29: a sixth round for a hard run's winner): the five,
    /// then two champions at once (GauntletChampions.PairFor). Beating <see cref="Rounds"/> still counts as beating the
    /// Gauntlet for the hard-mode unlock (ExpeditionHardMode.IsUnlocked); the sixth is the hard winner's extra.
    /// </summary>
    public const int HardRounds = 6;

    /// <summary>The rounds of a Gauntlet whose won run was a hard run (<see cref="HardRounds"/>) or not (<see cref="Rounds"/>).</summary>
    public static int RoundsFor(bool hard)
    {
        return hard ? HardRounds : Rounds;
    }

    /// <summary>The rounds of this session's Gauntlet (<see cref="GauntletState.Hard"/>); a null state has the normal five.</summary>
    public static int RoundsFor(GauntletState state)
    {
        return RoundsFor(state?.Hard == true);
    }

    /// <summary>True when this round of the session is the hard winner's two-champion round.</summary>
    public static bool IsPairRound(GauntletState state, int round)
    {
        return state?.Hard == true && round == HardRounds;
    }

    /// <summary>The champions' guild, shown under their names: the expedition's ultimate test.</summary>
    public const string GuildName = "SmuG";

    /// <summary>Seconds after the last champion falls before the winner is taken back to camp.</summary>
    public const int ReturnAfterVictorySeconds = 15;
}
