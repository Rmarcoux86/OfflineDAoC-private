using System;
using System.Linq;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;

namespace DOL.GS.Expedition;

/// <summary>
/// Realm points on a run: monsters that are not grey give a few, elite pack members about as many more as their extra
/// health (<see cref="ExpeditionElites.RealmPointMultiplier"/>: 2.2x, Stalwart 3.3x; a repeat kill 1x), finished
/// objectives and act bosses much more, and winning the run a large final award. The realm ranks they bring buy realm
/// abilities in the trainer window (/train works anywhere on a run). Everything earned is also kept on the account
/// (<see cref="ExpeditionAccountRealm"/>).
/// </summary>
public static class ExpeditionRealm
{
    public const int ObjectivePointsPerAct = 250;
    public const int BossPointsPerAct = 1500;
    public const int VictoryPoints = 15000;

    /// <summary>
    /// A monster kill: 5 + 3 per monster level, times <paramref name="multiplier"/> (an elite's, rounded), none for a grey
    /// monster (more than 5 levels below the player).
    /// </summary>
    public static int PointsForKill(int mobLevel, int playerLevel, double multiplier = 1.0)
    {
        if (mobLevel < playerLevel - 5)
        {
            return 0;
        }

        int points = 5 + 3 * Math.Max(0, mobLevel);

        return (int) Math.Round(points * Math.Max(1.0, multiplier));
    }

    public static void OnKill(GamePlayer player, GameLiving target)
    {
        if (!ExpeditionLoot.IsLootableKill(target))
        {
            return;
        }

        Grant(player, PointsForKill(target.Level, player.Level, ExpeditionElites.RealmPointMultiplier(target as GameNPC)));
    }

    public static void OnObjectiveDone(GamePlayer player, int act)
    {
        ExpeditionManager.GrantLevelShare(player, ExpeditionManager.ObjectiveXpPercent, "Objective complete");
        Grant(player, ObjectivePointsPerAct * Math.Max(1, act));
    }

    public static void OnBossKilled(GamePlayer player, int act, bool wonRun)
    {
        ExpeditionManager.GrantLevelShare(player, ExpeditionManager.BossXpPercent, "Act boss slain");
        Grant(player, BossPointsPerAct * Math.Max(1, act) + (wonRun ? VictoryPoints : 0));
    }

    /// <summary>
    /// Realm points a run started before realm points existed would have earned: its kills at about the player's
    /// level, two objectives and the boss of each finished act, and the current act's finished objectives.
    /// </summary>
    public static long BackfillEstimate(int kills, int playerLevel, int bossesKilled, int currentAct, int currentActObjectivesDone)
    {
        long points = (long) Math.Max(0, kills) * PointsForKill(Math.Max(1, playerLevel - 1), playerLevel);

        for (int act = 1; act <= Math.Max(0, bossesKilled); act++)
        {
            points += 2L * ObjectivePointsPerAct * act + (long) BossPointsPerAct * act;
        }

        points += (long) Math.Max(0, currentActObjectivesDone) * ObjectivePointsPerAct * Math.Max(1, currentAct);

        return points;
    }

    /// <summary>
    /// A run from before realm points (it has kills but has earned none, and the character has none) gets the realm
    /// points it would have earned so far, once: afterwards <see cref="ExpeditionRun.RealmPointsEarned"/> is above 0.
    /// Called once the player has settled in the world after logging in.
    /// </summary>
    public static void Backfill(GamePlayer player)
    {
        ExpeditionRun run = ExpeditionManager.GetRun(player);

        if (run == null || run.RealmPointsEarned > 0 || player.RealmPoints > 0 || (run.Kills <= 0 && run.BossesKilled <= 0))
        {
            return;
        }

        int objectivesDone = run.CurrentAct?.Objectives?.Count(objective => objective != null && objective.Done) ?? 0;
        long estimate = BackfillEstimate(run.Kills, player.Level, run.BossesKilled, run.Act, objectivesDone);

        if (estimate <= 0)
        {
            return;
        }

        Grant(player, (int) Math.Min(int.MaxValue, estimate));

        if (run.RealmPointsEarned > 0)
        {
            run.AddLog($"Realm points for the run so far: {run.RealmPointsEarned}.");
            ExpeditionManager.Save(run);
            player.Out.SendMessage($"Your expedition's earlier deeds are counted: {run.RealmPointsEarned} realm points. Spend realm ability points with /train.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }
    }

    /// <summary>
    /// Gives realm points through the normal path (xp-style server rate, realm rank-ups and their messages), scaled by
    /// expedition_rp_rate and the Glory boon, and adds what was actually gained to the run's total and to the account (ExpeditionAccountRealm).
    /// </summary>
    private static void Grant(GamePlayer player, int basePoints)
    {
        ExpeditionRun run = ExpeditionManager.GetRun(player);

        if (run == null || basePoints <= 0 || Properties.EXPEDITION_RP_RATE <= 0)
        {
            return;
        }

        long amount = (long) Math.Round(basePoints * Properties.EXPEDITION_RP_RATE * ExpeditionBoons.RealmPointFactor(run));

        if (amount <= 0)
        {
            return;
        }

        long before = player.RealmPoints;

        player.GainRealmPoints(amount, true, true);

        long gained = Math.Max(0, player.RealmPoints - before);

        lock (run.KillSync)
        {
            run.RealmPointsEarned += gained;
        }

        ExpeditionAccountRealm.Add(run.AccountName, gained);
    }
}
