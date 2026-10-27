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

    /// <summary>Seconds the champion spends buffing where it stands before it can be challenged.</summary>
    public const int ChampionBuffSeconds = 10;

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

    /// <summary>The champions' guild, shown under their names: the expedition's ultimate test.</summary>
    public const string GuildName = "SmuG";

    /// <summary>Seconds after the expedition is won before the winner is taken to the Gauntlet (time to read and loot).</summary>
    public const int StartAfterWinSeconds = 20;

    /// <summary>Seconds after the last champion falls before the winner is taken back to camp.</summary>
    public const int ReturnAfterVictorySeconds = 15;
}
