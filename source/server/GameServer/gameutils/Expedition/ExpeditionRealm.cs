using System;
using DOL.GS.ServerProperties;

namespace DOL.GS.Expedition;

/// <summary>
/// Realm points on a run: monsters that are not grey give a few, elite pack members four times as many, finished
/// objectives and act bosses much more, and winning the run a large final award. The realm ranks they bring buy realm
/// abilities in the trainer window (/train works anywhere on a run). What the run earned is banked as Renown at its end
/// (<see cref="ExpeditionLegacy"/>).
/// </summary>
public static class ExpeditionRealm
{
    public const int EliteMultiplier = 4;
    public const int ObjectivePointsPerAct = 250;
    public const int BossPointsPerAct = 1500;
    public const int VictoryPoints = 15000;

    /// <summary>A monster kill: 5 + 3 per monster level, none for a grey monster (more than 5 levels below the player).</summary>
    public static int PointsForKill(int mobLevel, int playerLevel, bool elite)
    {
        if (mobLevel < playerLevel - 5)
        {
            return 0;
        }

        int points = 5 + 3 * Math.Max(0, mobLevel);

        return elite ? points * EliteMultiplier : points;
    }

    public static void OnKill(GamePlayer player, GameLiving target)
    {
        if (!ExpeditionLoot.IsLootableKill(target))
        {
            return;
        }

        Grant(player, PointsForKill(target.Level, player.Level, ExpeditionElites.IsElite(target as GameNPC)));
    }

    public static void OnObjectiveDone(GamePlayer player, int act)
    {
        Grant(player, ObjectivePointsPerAct * Math.Max(1, act));
    }

    public static void OnBossKilled(GamePlayer player, int act, bool wonRun)
    {
        Grant(player, BossPointsPerAct * Math.Max(1, act) + (wonRun ? VictoryPoints : 0));
    }

    /// <summary>
    /// Gives realm points through the normal path (xp-style server rate, realm rank-ups and their messages), scaled by
    /// expedition_rp_rate and the Renowned perk, and adds what was actually gained to the run's total.
    /// </summary>
    private static void Grant(GamePlayer player, int basePoints)
    {
        ExpeditionRun run = ExpeditionManager.GetRun(player);

        if (run == null || basePoints <= 0 || Properties.EXPEDITION_RP_RATE <= 0)
        {
            return;
        }

        long amount = (long) Math.Round(basePoints * Properties.EXPEDITION_RP_RATE * ExpeditionLegacy.RealmPointFactor(player));

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
    }
}
