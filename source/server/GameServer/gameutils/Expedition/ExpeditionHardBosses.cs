using System;
using System.Reflection;
using DOL.GS.Expedition.Gauntlet;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// A hard run's act bosses (owner 2026-10-29: "make the act bosses the champion versions from the original gauntlet"):
/// in place of the act's ExpeditionBoss, the Gauntlet champion of the act, in the Gauntlet's order
/// (<see cref="GauntletChampions.All"/>: act 1 Gwyraan, 2 Toothmouth, 3 Ios, 4 Drunkstains, 5 Preesh), at the act's top
/// level + 2 and the Gauntlet's realm rank for that round (<see cref="GauntletEscalation.For"/>: RR2, 4, 6, 8, Preesh RR10).
/// It fights the run's player at once, as a boss, where the act's boss would stand (GauntletChampion.ActBoss).
/// </summary>
public static class ExpeditionHardBosses
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The champion of an act (1-based; out-of-range acts clamp to the first or last).</summary>
    public static ChampionDefinition ChampionFor(int act)
    {
        return GauntletChampions.All[Math.Clamp(act, 1, GauntletChampions.All.Count) - 1];
    }

    /// <summary>The act boss champion's level: the act's top level + 2, as an act boss's (act 1, levels 1-10: 12).</summary>
    public static byte LevelFor(ActPlan plan)
    {
        return (byte) Math.Clamp((plan?.MaxLevel ?? 0) + 2, 1, GauntletChampion.SpawnLevel);
    }

    /// <summary>Spawns the act's champion where its boss would stand; null when it cannot be made or placed.</summary>
    public static GauntletChampion Spawn(GamePlayer player, ActPlan plan)
    {
        if (player == null || plan?.Boss == null)
        {
            return null;
        }

        ChampionDefinition definition = ChampionFor(plan.Act);
        RoundTier tier = GauntletEscalation.For(plan.Act, definition.Name);
        GauntletChampion champion = new(player, definition, plan.Act, tier, LevelFor(plan), true);
        BossSpec spec = plan.Boss;

        champion.CurrentRegionID = (ushort) spec.RegionId;
        champion.X = spec.X;
        champion.Y = spec.Y;
        champion.Z = ExpeditionBoss.FloorZ(champion.CurrentRegion, spec.X, spec.Y, spec.Z);
        champion.Heading = 0;

        if (champion.CurrentRegion == null || !champion.AddToWorld())
        {
            Log.Warn($"[EXPEDITION] Act {plan.Act} champion '{champion.Name}' could not be added to region {spec.RegionId} at ({spec.X}, {spec.Y}, {spec.Z}).");
            Remove(champion);

            return null;
        }

        // Preesh fights with Blessed Arms, as in the Gauntlet (GauntletManager.BeginRound).
        if (definition.Name == GauntletChampions.FinalChampion)
        {
            ExpeditionBuffs.ApplyBlessedArms(champion);
        }

        return champion;
    }

    /// <summary>Takes a champion out of the world without a kill (as GauntletManager does): its affix state and its pet go too.</summary>
    public static void Remove(GauntletChampion champion)
    {
        if (champion == null)
        {
            return;
        }

        ExpeditionElites.Forget(champion);
        champion.Remove();
    }
}
