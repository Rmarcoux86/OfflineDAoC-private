using System;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// How strong a round's champion is (docs/EXPEDITION-GAUNTLET-PLAN.md, Phase 2 table).
/// <paramref name="StatBonusPercent"/> is a percentage (5 means +5%).
/// </summary>
public sealed record RoundTier(int RealmRank, eLootRarity GearRarity, bool RealmAbilities, bool Potions, bool EliteAffix, int Helpers, double StatBonusPercent);

/// <summary>The Gauntlet's escalation table, as data.</summary>
public static class GauntletEscalation
{
    /// <summary>The round from which the stat bonus grows every round.</summary>
    private const int OpenEndedRound = 20;

    /// <summary>Stat bonus per round beyond <see cref="OpenEndedRound"/>, in percent.</summary>
    private const double StatBonusPerRound = 5;

    /// <summary>
    /// The tier of a round (1-based; below 1 counts as 1): rounds 1-4 RR1 Magic gear, styles only; 5-9 RR3 Rare gear
    /// and realm abilities; 10-14 RR5 Epic gear, potions and an Elite affix; 15-19 RR7 Unique gear and one helper;
    /// 20+ RR10, two helpers and +5% stats for each round over 20.
    /// </summary>
    public static RoundTier For(int round)
    {
        round = Math.Max(1, round);

        if (round < 5)
        {
            return new RoundTier(1, eLootRarity.Magic, false, false, false, 0, 0);
        }

        if (round < 10)
        {
            return new RoundTier(3, eLootRarity.Rare, true, false, false, 0, 0);
        }

        if (round < 15)
        {
            return new RoundTier(5, eLootRarity.Epic, true, true, true, 0, 0);
        }

        if (round < OpenEndedRound)
        {
            return new RoundTier(7, eLootRarity.Unique, true, true, true, 1, 0);
        }

        return new RoundTier(10, eLootRarity.Unique, true, true, true, 2, (round - OpenEndedRound) * StatBonusPerRound);
    }
}
