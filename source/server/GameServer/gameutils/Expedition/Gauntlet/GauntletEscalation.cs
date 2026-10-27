using System;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// How strong a round's champion is (docs/EXPEDITION-GAUNTLET-PLAN.md, Phase 2 table).
/// <paramref name="StatBonusPercent"/> is a percentage (5 means +5%).
/// </summary>
public sealed record RoundTier(int RealmRank, bool RealmAbilities, bool Potions, bool EliteAffix, int Helpers, double StatBonusPercent);

/// <summary>The Gauntlet's escalation table, as data.</summary>
public static class GauntletEscalation
{
    /// <summary>The highest realm rank (the last of a realm's fourteen titles, GlobalConstants.REALM_RANK_NAMES).</summary>
    public const int MaxRealmRank = 14;

    /// <summary>The round from which the stat bonus grows every round.</summary>
    private const int OpenEndedRound = 20;

    /// <summary>Stat bonus per round beyond <see cref="OpenEndedRound"/>, in percent.</summary>
    private const double StatBonusPerRound = 5;

    /// <summary>
    /// The tier of a round (1-based; below 1 counts as 1). Every champion wears a full template without ToA bonuses
    /// (GauntletChampion). Its realm rank is twice the round number (owner 2026-10-25, raised 2026-10-27), up to
    /// <see cref="MaxRealmRank"/>. From round 5 it uses realm abilities; from 10 potions and an Elite
    /// affix; 15-19 one helper; from 20 two helpers and +5% stats for each round over 20.
    /// </summary>
    public static RoundTier For(int round)
    {
        round = Math.Max(1, round);
        // Two ranks a round (owner 2026-10-27: harder champions): RR2, 4, 6, 8, 10 over the five fights.
        int rank = Math.Min(round * 2, MaxRealmRank);

        // Realm abilities from the first round (owner 2026-10-27).
        if (round < 5)
        {
            return new RoundTier(rank, true, false, false, 0, 0);
        }

        if (round < 10)
        {
            return new RoundTier(rank, true, false, false, 0, 0);
        }

        if (round < 15)
        {
            return new RoundTier(rank, true, true, true, 0, 0);
        }

        if (round < OpenEndedRound)
        {
            return new RoundTier(rank, true, true, true, 1, 0);
        }

        return new RoundTier(rank, true, true, true, 2, (round - OpenEndedRound) * StatBonusPerRound);
    }
}
