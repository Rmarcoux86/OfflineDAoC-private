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

    /// <summary>The final champion's realm rank, whatever the round (owner 2026-10-28: "make preesh rr10 again").</summary>
    public const int FinalChampionRealmRank = 10;

    /// <summary>The round from which the stat bonus grows every round.</summary>
    private const int OpenEndedRound = 20;

    /// <summary>Stat bonus per round beyond <see cref="OpenEndedRound"/>, in percent.</summary>
    private const double StatBonusPerRound = 5;

    /// <summary>
    /// The tier of a round (1-based; below 1 counts as 1). Every champion wears a full template without ToA bonuses
    /// (GauntletChampion). Its realm rank is twice the round (owner 2026-10-29), up to
    /// <see cref="MaxRealmRank"/>; the final champion's is always <see cref="FinalChampionRealmRank"/>. From round 5 it uses realm abilities; from 10 potions and an Elite
    /// affix; 15-19 one helper; from 20 two helpers and +5% stats for each round over 20.
    /// </summary>
    /// <summary>
    /// How much weaker than full strength a round's champion is, in percent of its health and its melee and spell damage
    /// (owner 2026-10-28: round 1 Ios was too strong): 20 in round 1, 13 in round 2, 7 in round 3, none from round 4.
    /// </summary>
    public static int WeakenedPercent(int round)
    {
        return Math.Max(1, round) switch
        {
            1 => 20,
            2 => 13,
            3 => 7,
            _ => 0
        };
    }

    /// <summary>
    /// The tier of each champion of a hard winner's two-champion round (owner 2026-10-29): the round's own tier at the
    /// final champion's realm rank, <see cref="FinalChampionRealmRank"/>.
    /// </summary>
    public static RoundTier ForPair(int round)
    {
        return For(round, GauntletChampions.FinalChampion);
    }

    public static RoundTier For(int round, string championName = null)
    {
        round = Math.Max(1, round);

        // Two ranks a round (owner 2026-10-29: "do rr2, rr4, rr6, rr8, and then rr10"): RR2 to RR8 for the first four
        // fights; the final champion is always RR10.
        int rank = string.Equals(championName, GauntletChampions.FinalChampion, StringComparison.Ordinal)
            ? FinalChampionRealmRank
            : Math.Min(round * 2, MaxRealmRank);

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
