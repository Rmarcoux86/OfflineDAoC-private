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

/// <summary>The five Gauntlet champions and the seeded order they are faced in.</summary>
public static class GauntletChampions
{
    /// <summary>The roster, in a fixed order; <see cref="ForRound"/> shuffles it per cycle.</summary>
    public static readonly IReadOnlyList<ChampionDefinition> All = new List<ChampionDefinition>
    {
        // At 50% health: a melee speed and damage burst.
        new("Ios", (int) eCharacterClass.Valewalker, (int) eRace.Celt, (int) eGender.Female, "burst", ExpeditionElites.Swift),

        // Polearm bleeds, one Purge, a taunt that forces its target.
        new("Drunkstains", (int) eCharacterClass.Armsman, (int) eRace.HalfOgre, (int) eGender.Male, "attrition", ExpeditionElites.Armoured),

        // Damage add and endurance regen; mezzes a player who keeps out of melee range.
        new("Toothmouth", (int) eCharacterClass.Bard, (int) eRace.Firbolg, (int) eGender.Male, "range-punish", ExpeditionElites.Stalwart),

        // A nature pet that heals him; nearsight on the player at round start.
        new("Gwyraan", (int) eCharacterClass.Druid, (int) eRace.Celt, (int) eGender.Male, "pet-heal", ExpeditionElites.Venomous),

        // Dual wield, high evade; Vanish once per fight and reappear behind the player.
        new("Preesh", (int) eCharacterClass.Blademaster, (int) eRace.Firbolg, (int) eGender.Male, "vanish", ExpeditionElites.Frenzied)
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
    /// The champion of a round (1-based; below 1 counts as 1). Rounds come in cycles of five: the other four in a seeded
    /// shuffle, then <see cref="FinalChampion"/>. So no champion repeats until all five have been faced, the same (seed,
    /// round) always gives the same champion, and a cycle (ending with the final champion) never starts with it again.
    /// </summary>
    public static ChampionDefinition ForRound(int seed, int round)
    {
        int index = Math.Max(1, round) - 1;
        int place = index % All.Count;

        if (place == All.Count - 1)
        {
            return Get(FinalChampion);
        }

        List<ChampionDefinition> others = new();

        foreach (ChampionDefinition champion in All)
        {
            if (!string.Equals(champion.Name, FinalChampion, StringComparison.Ordinal))
            {
                others.Add(champion);
            }
        }

        int[] order = Shuffle(seed, index / All.Count, others.Count);

        return others[order[place]];
    }

    /// <summary>A Fisher-Yates shuffle of <paramref name="count"/> indices, seeded by the run's seed and the cycle.</summary>
    private static int[] Shuffle(int seed, int cycle, int count)
    {
        Random rng = new(unchecked(seed * 7919 + cycle * 104729 + 31));
        int[] order = new int[count];

        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }
}
