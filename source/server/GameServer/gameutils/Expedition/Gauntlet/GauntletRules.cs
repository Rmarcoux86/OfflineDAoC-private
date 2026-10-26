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
    public const int RestSeconds = 90;

    /// <summary>How far (world units) the champion may chase from the arena centre before it is pulled back.</summary>
    public const int LeashRadius = 1500;

    /// <summary>Seconds the champion spends buffing at the arena centre before it attacks.</summary>
    public const int ChampionBuffSeconds = 10;

    /// <summary>Seconds after a Gauntlet death before the player is released to the arena entrance automatically.</summary>
    public const int AutoReleaseSeconds = 10;
}
