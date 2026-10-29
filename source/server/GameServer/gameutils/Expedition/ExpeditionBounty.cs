using System;
using System.Collections.Generic;
using System.Reflection;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// A hard run's bounty (owner 2026-10-29): every act has a timer, set when the act begins. Finish every objective before
/// it runs out and the boss comes alone; let it run out and the boss arrives with a helper pack, <see cref="HelperCount"/>
/// monsters of the act's level copied from the camp nearest the boss, that fight beside it. The player is warned at
/// <see cref="FirstWarningSeconds"/> and <see cref="LastWarningSeconds"/> left, and told at the boss's arrival whether the
/// pack came. The timer stands still while the player is away (logged out): <see cref="Pause"/> and <see cref="Resume"/>.
/// </summary>
public static class ExpeditionBounty
{
    /// <summary>The bounty of acts 1 and 2, in minutes.</summary>
    public const int EarlyActMinutes = 25;

    /// <summary>The bounty of acts 3 and 4, in minutes.</summary>
    public const int MiddleActMinutes = 35;

    /// <summary>The bounty of act 5, in minutes.</summary>
    public const int FinalActMinutes = 45;

    /// <summary>The monsters that come with a boss whose bounty ran out.</summary>
    public const int HelperCount = 3;

    /// <summary>The warnings, in seconds left.</summary>
    public const int FirstWarningSeconds = 5 * 60;
    public const int LastWarningSeconds = 60;

    /// <summary>How far from the boss its helpers stand.</summary>
    private const int HelperDistance = 250;

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The bounty of an act: 25 minutes for acts 1-2, 35 for 3-4, 45 for act 5 (and anything later).</summary>
    public static TimeSpan DurationFor(int act)
    {
        if (act <= 2)
        {
            return TimeSpan.FromMinutes(EarlyActMinutes);
        }

        if (act <= 4)
        {
            return TimeSpan.FromMinutes(MiddleActMinutes);
        }

        return TimeSpan.FromMinutes(FinalActMinutes);
    }

    /// <summary>Seconds left before the deadline, never below 0; 0 for no deadline.</summary>
    public static double SecondsLeft(DateTime? deadlineUtc, DateTime nowUtc)
    {
        return deadlineUtc.HasValue ? Math.Max(0, (deadlineUtc.Value - nowUtc).TotalSeconds) : 0;
    }

    /// <summary>True once a set deadline has passed.</summary>
    public static bool HasRunOut(DateTime? deadlineUtc, DateTime nowUtc)
    {
        return deadlineUtc.HasValue && nowUtc >= deadlineUtc.Value;
    }

    /// <summary>
    /// Starts or resumes the act's timer (the tracker's attach: the act's arrival, a login): a plan without a running
    /// deadline gets now + what was left when it paused, or now + the act's whole bounty the first time. Nothing once the
    /// boss has come. True when the plan changed (the caller saves).
    /// </summary>
    public static bool Resume(ActPlan plan, DateTime nowUtc)
    {
        if (plan == null || plan.BossSpawned || plan.BossKilled || plan.BountyDeadlineUtc.HasValue)
        {
            return false;
        }

        TimeSpan left = plan.BountySecondsLeft.HasValue ? TimeSpan.FromSeconds(Math.Max(0, plan.BountySecondsLeft.Value)) : DurationFor(plan.Act);
        plan.BountyDeadlineUtc = nowUtc + left;
        plan.BountySecondsLeft = null;

        return true;
    }

    /// <summary>
    /// Stops the act's timer while the player is away (the tracker's detach): what is left is kept and the deadline
    /// cleared. Nothing once the boss has come (<see cref="Decide"/> has settled it). True when the plan changed.
    /// </summary>
    public static bool Pause(ActPlan plan, DateTime nowUtc)
    {
        if (plan == null || plan.BossSpawned || plan.BossKilled || !plan.BountyDeadlineUtc.HasValue)
        {
            return false;
        }

        plan.BountySecondsLeft = SecondsLeft(plan.BountyDeadlineUtc, nowUtc);
        plan.BountyDeadlineUtc = null;

        return true;
    }

    /// <summary>
    /// The boss's coming (ObjectiveTracker.SpawnBoss) settles the bounty: missed when the deadline had passed (or was already marked missed),
    /// met otherwise. The deadline is then cleared, so a respawn (a login, a boss slain by another hand) keeps the
    /// decision instead of reading a deadline long past.
    /// </summary>
    public static void Decide(ActPlan plan, DateTime nowUtc)
    {
        if (plan == null)
        {
            return;
        }

        if (HasRunOut(plan.BountyDeadlineUtc, nowUtc))
        {
            plan.BountyMissed = true;
        }

        plan.BountyDeadlineUtc = null;
        plan.BountySecondsLeft = null;
    }

    /// <summary>
    /// The warning due now, in seconds (<see cref="FirstWarningSeconds"/> or <see cref="LastWarningSeconds"/>), or 0 for
    /// none. <paramref name="lastWarned"/> is the smallest threshold already warned of (int.MaxValue for none). A player
    /// who comes back with less than a minute left gets the last warning only.
    /// </summary>
    public static int WarningDue(double secondsLeft, int lastWarned)
    {
        if (secondsLeft <= 0)
        {
            return 0;
        }

        if (secondsLeft <= LastWarningSeconds && lastWarned > LastWarningSeconds)
        {
            return LastWarningSeconds;
        }

        if (secondsLeft <= FirstWarningSeconds && lastWarned > FirstWarningSeconds)
        {
            return FirstWarningSeconds;
        }

        return 0;
    }

    /// <summary>"12:05" (minutes and seconds left, rounded up to the second).</summary>
    public static string FormatLeft(double seconds)
    {
        int total = (int) Math.Ceiling(Math.Max(0, seconds));

        return $"{total / 60}:{total % 60:00}";
    }

    /// <summary>The bounty's part of /run status: the time left, or what came of it.</summary>
    public static string StatusText(ActPlan plan, DateTime nowUtc)
    {
        if (plan == null)
        {
            return null;
        }

        if (plan.BossSpawned || plan.BossKilled)
        {
            return plan.BountyMissed ? "missed: the boss came with its helper pack" : "met: the boss came alone";
        }

        if (plan.BountyMissed || HasRunOut(plan.BountyDeadlineUtc, nowUtc))
        {
            return "run out: the boss will come with a helper pack";
        }

        if (plan.BountyDeadlineUtc.HasValue)
        {
            return $"{FormatLeft(SecondsLeft(plan.BountyDeadlineUtc, nowUtc))} left";
        }

        return plan.BountySecondsLeft.HasValue ? $"{FormatLeft(plan.BountySecondsLeft.Value)} left (paused)" : null;
    }

    /// <summary>
    /// The helper pack: <see cref="HelperCount"/> copies of the monster of the zone's camp nearest the boss, at the act's
    /// level (its top level), in a ring around the boss. They aggro as the boss does (its aggro range) and the tracker
    /// sets them on whatever the boss fights (<see cref="Assist"/>). They never respawn and are never saved.
    /// </summary>
    public static List<GameNPC> SpawnPack(GameNPC boss, ActPlan plan)
    {
        List<GameNPC> pack = new();

        if (boss?.CurrentRegion == null || plan == null)
        {
            return pack;
        }

        DbMob template = NearestCampMob(boss, plan);

        if (template == null)
        {
            Log.Warn($"[EXPEDITION] No camp monster found for the act {plan.Act} boss's helper pack in zone {plan.ZoneId}.");

            return pack;
        }

        for (int i = 0; i < HelperCount; i++)
        {
            double angle = 2 * Math.PI * i / HelperCount;
            int x = boss.X + (int) Math.Round(Math.Cos(angle) * HelperDistance);
            int y = boss.Y + (int) Math.Round(Math.Sin(angle) * HelperDistance);

            try
            {
                GameNPC helper = new();
                helper.LoadFromDatabase(template);

                // A copy, not the world's spawn: its own id, never saved, never respawned.
                helper.InternalID = Guid.NewGuid().ToString();
                helper.LoadedFromScript = true;
                helper.RespawnInterval = -1;
                helper.RoamingRange = 0;
                helper.SetOwnBrain(new StandardMobBrain { AggroLevel = ExpeditionBoss.BossAggroLevel, AggroRange = ExpeditionBoss.BossAggroRange });

                // As ExpeditionBoss.Spawn: the stats are cleared so the Level setter sets them for the act's level.
                helper.Strength = 0;
                helper.Constitution = 0;
                helper.Dexterity = 0;
                helper.Quickness = 0;
                helper.Intelligence = 0;
                helper.Empathy = 0;
                helper.Piety = 0;
                helper.Charisma = 0;
                helper.Level = (byte) Math.Clamp(plan.MaxLevel, 1, 255);
                helper.Health = helper.MaxHealth;

                helper.CurrentRegionID = boss.CurrentRegionID;
                helper.X = x;
                helper.Y = y;
                helper.Z = ExpeditionBoss.FloorZ(boss.CurrentRegion, x, y, boss.Z);
                helper.Heading = boss.Heading;

                if (helper.AddToWorld())
                {
                    pack.Add(helper);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[EXPEDITION] A helper ({template.Name}) of the act {plan.Act} boss could not be spawned.", ex);
            }
        }

        return pack;
    }

    /// <summary>The helpers join whatever the boss fights.</summary>
    public static void Assist(GameNPC boss, IEnumerable<GameNPC> pack)
    {
        if (boss == null || !boss.IsAlive || pack == null || boss.TargetObject is not GameLiving target || !target.IsAlive || !boss.InCombat)
        {
            return;
        }

        foreach (GameNPC helper in pack)
        {
            if (helper == null || !helper.IsAlive || helper.ObjectState != GameObject.eObjectState.Active || helper.InCombat)
            {
                continue;
            }

            if (helper.Brain is StandardMobBrain brain)
            {
                brain.AddToAggroList(target, 1);
            }
        }
    }

    /// <summary>Takes the pack out of the world (the boss left with the player, or the act moved on).</summary>
    public static void Despawn(IEnumerable<GameNPC> pack)
    {
        if (pack == null)
        {
            return;
        }

        foreach (GameNPC helper in pack)
        {
            if (helper != null && helper.ObjectState == GameObject.eObjectState.Active)
            {
                helper.Delete();
            }
        }
    }

    /// <summary>
    /// The Mob row of the zone's camp nearest the boss (WorldCampSource: the camps the act was planned from), one in the
    /// act's region first; the boss's own base monster when the zone has no camp.
    /// </summary>
    private static DbMob NearestCampMob(GameNPC boss, ActPlan plan)
    {
        string name = plan.Boss?.BaseMobName;

        try
        {
            Zone zone = WorldMgr.GetZone((ushort) plan.ZoneId);
            long best = long.MaxValue;

            if (zone != null)
            {
                foreach (AutonomousCapnBryGoalCatalog.Entry camp in WorldCampSource.Instance.GetZoneCamps(plan.RegionId, plan.ZoneId))
                {
                    long dx = zone.XOffset + camp.LocalX - boss.X;
                    long dy = zone.YOffset + camp.LocalY - boss.Y;
                    long distance = dx * dx + dy * dy;

                    if (distance < best && !string.IsNullOrWhiteSpace(camp.Name))
                    {
                        best = distance;
                        name = camp.Name;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            DbMob mob = GameServer.Database.SelectObject<DbMob>(DB.Column("Name").IsEqualTo(name).And(DB.Column("Region").IsEqualTo(plan.RegionId)));

            return mob ?? GameServer.Database.SelectObject<DbMob>(DB.Column("Name").IsEqualTo(name));
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Mob table lookup failed for the helper pack ({name}).", ex);

            return null;
        }
    }
}
