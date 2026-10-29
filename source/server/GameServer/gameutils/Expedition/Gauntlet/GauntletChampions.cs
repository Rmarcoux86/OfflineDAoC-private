using System;
using System.Collections.Generic;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// One Gauntlet champion: an act boss as a real class character (docs/EXPEDITION-CLASS-BOSSES.md).
/// <paramref name="ClassId"/> is an <see cref="eCharacterClass"/> value, <paramref name="RaceId"/> an <see cref="eRace"/>
/// value and <paramref name="Gender"/> an <see cref="eGender"/> value, as GameBot's constructor takes them (raceId,
/// genderId). <paramref name="Signature"/> names the champion's signature move (plan, Phase 3).
/// <paramref name="Affix"/> is the champion's elite affix (owner 2026-10-28, <see cref="ExpeditionElites"/>): its effect and
/// its name before the champion's (the affix's own effect only, not an elite's levels, size or health; Stalwart's triple
/// health is the champion's MaxHealth).
/// </summary>
public sealed record ChampionDefinition(string Name, int ClassId, int RaceId, int Gender, string Signature, string Affix = null);

/// <summary>The five Gauntlet champions and the order they are faced in.</summary>
public static class GauntletChampions
{
    /// <summary>The roster, in the order they are faced (owner 2026-10-28: Gwyraan, Toothmouth, Ios, Drunkstains, Preesh).</summary>
    public static readonly IReadOnlyList<ChampionDefinition> All = new List<ChampionDefinition>
    {
        // A nature pet that heals him; nearsight on the player at round start.
        new("Gwyraan", (int) eCharacterClass.Druid, (int) eRace.Celt, (int) eGender.Male, "pet-heal", ExpeditionElites.Venomous),

        // Damage add and endurance regen; mezzes a player who keeps out of melee range.
        new("Toothmouth", (int) eCharacterClass.Bard, (int) eRace.Firbolg, (int) eGender.Male, "range-punish", ExpeditionElites.Stalwart),

        // At 50% health: a melee speed and damage burst.
        new("Ios", (int) eCharacterClass.Valewalker, (int) eRace.Celt, (int) eGender.Female, "burst", ExpeditionElites.Frenzied),

        // Polearm bleeds, one Purge, a taunt that forces its target.
        new("Drunkstains", (int) eCharacterClass.Armsman, (int) eRace.HalfOgre, (int) eGender.Male, "attrition", ExpeditionElites.Armoured),

        // Dual wield, high evade; Vanish once per fight and reappear behind the player.
        new("Preesh", (int) eCharacterClass.Blademaster, (int) eRace.Firbolg, (int) eGender.Male, "vanish", ExpeditionElites.Swift)
    };

    /// <summary>The champion with this name (case-insensitive), or null when there is none.</summary>
    public static ChampionDefinition Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (ChampionDefinition champion in All)
        {
            if (string.Equals(champion.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return champion;
            }
        }

        return null;
    }

    /// <summary>The champion every cycle ends with (owner 2026-10-25: "Preesh is the final champion").</summary>
    public const string FinalChampion = "Preesh";

    /// <summary>
    /// The champion of a round (1-based; below 1 counts as 1): <see cref="All"/> in order, again from the start after
    /// <see cref="FinalChampion"/> (owner 2026-10-28: a fixed order, no longer a seeded shuffle). The seed is not used.
    /// </summary>
    public static ChampionDefinition ForRound(int seed, int round)
    {
        return All[(Math.Max(1, round) - 1) % All.Count];
    }
}
