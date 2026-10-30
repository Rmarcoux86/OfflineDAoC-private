using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using DOL.AI;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// The hunt (hard mode, owner 2026-10-29): every <see cref="SpawnIntervalMs"/> the player spends in the act's region, a
/// named stalker (<see cref="ExpeditionStalker"/>, one name per zone) is sent after them:
/// (the player's level + <see cref="LevelAbovePlayer"/>, owner 2026-10-30: "Tie it to player"), Swift, aggressive, spawned <see cref="SpawnMinDistance"/>-<see cref="SpawnMaxDistance"/>
/// units away and walking to the player until it dies or the act ends. It drops nothing and gives normal realm points.
/// The player always sees it coming: a warning <see cref="WarningLeadMs"/> before it comes, where it is when it comes,
/// its direction and distance every <see cref="ReportIntervalMs"/>, and "You hear it breathing." when it is close.
/// It leaves on act change, run end (the tracker detaches), the player's death, or after <see cref="GiveUpMs"/> when it
/// never reached them. Time in the act is kept in memory per character and act (a server restart starts it again).
/// Driven by <see cref="ObjectiveTracker"/>'s tick.
/// </summary>
public static class ExpeditionHunt
{
    public const long SpawnIntervalMs = 20 * 60_000;
    public const long WarningLeadMs = 60_000;
    public const long ReportIntervalMs = 30_000;
    public const long GiveUpMs = 10 * 60_000;
    public const int SpawnMinDistance = 4000;
    public const int SpawnMaxDistance = 6000;
    public const int BreathingRadius = 1500;
    public const int LevelAbovePlayer = 2;

    /// <summary>The stalker has reached the player once it is this close (or fighting them): it no longer gives up.</summary>
    public const int ReachRadius = 500;

    /// <summary>A tick longer than this (a stall, a long region load) counts as this much time in the act.</summary>
    public const long MaxTickDeltaMs = 10_000;

    public const string WarningText = "You feel watched. Something is hunting you.";
    public const string BreathingText = "You hear it breathing.";

    private const int StalkerAggroRange = 1500;
    private const int RepathDistance = 300;
    private const int SpawnAttempts = 24;

    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly ConcurrentDictionary<string, HuntState> States = new(StringComparer.Ordinal);

    private sealed class HuntState
    {
        // "seed:act:zone": time in the act starts again when this changes.
        public string ActKey;
        public long TimeInActMs;
        public long LastTickAt;
        public bool SpawnPending;

        public ExpeditionStalker Stalker;
        public long SpawnedAt;
        public long NextReportAt;
        public bool Breathing;
        public bool Reached;
        public Point3D LastPathTarget;
    }

    #region Schedule (pure)

    /// <summary>The time in the act at which the next stalker comes: the next multiple of <see cref="SpawnIntervalMs"/>.</summary>
    public static long NextSpawnAt(long timeInActMs)
    {
        return (Math.Max(0, timeInActMs) / SpawnIntervalMs + 1) * SpawnIntervalMs;
    }

    /// <summary>True when the warning mark (<see cref="WarningLeadMs"/> before the next stalker) falls in (before, after].</summary>
    public static bool WarningDue(long beforeMs, long afterMs)
    {
        long mark = NextSpawnAt(beforeMs) - WarningLeadMs;

        return beforeMs < mark && afterMs >= mark;
    }

    /// <summary>True when the next stalker's time falls in (before, after].</summary>
    public static bool SpawnDue(long beforeMs, long afterMs)
    {
        long mark = NextSpawnAt(beforeMs);

        return beforeMs < mark && afterMs >= mark;
    }

    /// <summary>A distance rounded to the nearest 500 units, at least 500.</summary>
    public static int RoughDistance(int distance)
    {
        return Math.Max(500, (int) Math.Round(distance / 500.0) * 500);
    }

    /// <summary>The zone's stalker: "Stalker of Lyonesse".</summary>
    public static string StalkerName(string zoneName)
    {
        return string.IsNullOrWhiteSpace(zoneName) ? "Stalker" : $"Stalker of {zoneName.Trim()}";
    }

    /// <summary>"The Stalker of Lyonesse closes in from the north-east, about 2,000 units."</summary>
    public static string ReportText(string stalkerName, string direction, int distance)
    {
        return $"The {stalkerName} closes in from the {direction}, about {RoughDistance(distance).ToString("N0", CultureInfo.InvariantCulture)} units.";
    }

    #endregion

    #region Tick

    /// <summary>Called on every tracker tick of the attached player (game loop), alive or dead.</summary>
    internal static void Tick(GamePlayer player, ExpeditionRun run, ActPlan plan)
    {
        // The power meter runs from the first hard tick (ExpeditionPower); it reads only hard-run players' hits.
        if (ExpeditionHardMode.IsHard(run))
        {
            ExpeditionPower.EnsureHandlers();
        }

        string key = player?.ObjectId;

        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!ExpeditionHardMode.IsHard(run) || plan == null)
        {
            Despawn(player);

            return;
        }

        long now = GameLoop.GameLoopTime;
        HuntState state = States.GetOrAdd(key, _ => new HuntState());
        string actKey = $"{run.Seed}:{plan.Act}:{plan.ZoneId}";

        if (state.ActKey != actKey)
        {
            RemoveStalker(player, state, null);
            state.ActKey = actKey;
            state.TimeInActMs = 0;
            state.LastTickAt = now;
            state.SpawnPending = false;

            return;
        }

        long delta = Math.Clamp(now - state.LastTickAt, 0, MaxTickDeltaMs);
        state.LastTickAt = now;

        if (!player.IsAlive)
        {
            RemoveStalker(player, state, null);
            state.SpawnPending = false;

            return;
        }

        if (state.Stalker != null)
        {
            UpdateStalker(player, state, now);
        }

        if (player.ObjectState != GameObject.eObjectState.Active || player.CurrentRegion == null || player.CurrentRegionID != plan.RegionId)
        {
            return;
        }

        long before = state.TimeInActMs;
        state.TimeInActMs += delta;

        // One stalker at a time: a mark that comes while one still hunts is skipped.
        if (WarningDue(before, state.TimeInActMs) && state.Stalker == null)
        {
            Tell(player, WarningText, true);
        }

        if (SpawnDue(before, state.TimeInActMs) && state.Stalker == null)
        {
            state.SpawnPending = true;
        }

        if (state.SpawnPending && state.Stalker == null)
        {
            TrySpawn(player, plan, state, now);
        }
    }

    /// <summary>Removes the player's stalker (tracker detach: act change, run end, logout). The time in the act is kept.</summary>
    public static void Despawn(GamePlayer player)
    {
        if (!string.IsNullOrEmpty(player?.ObjectId) && States.TryGetValue(player.ObjectId, out HuntState state))
        {
            RemoveStalker(player, state, null);
        }
    }

    private static void UpdateStalker(GamePlayer player, HuntState state, long now)
    {
        ExpeditionStalker stalker = state.Stalker;

        // Slain (its kill is an ordinary one) or gone.
        if (!stalker.IsAlive || stalker.ObjectState != GameObject.eObjectState.Active)
        {
            state.Stalker = null;

            return;
        }

        if (stalker.CurrentRegion != player.CurrentRegion)
        {
            RemoveStalker(player, state, $"The {stalker.Name} loses your scent.");

            return;
        }

        int distance = (int) Math.Sqrt(DistanceSquared(player.X, player.Y, stalker.X, stalker.Y));

        if (distance <= ReachRadius || stalker.TargetObject == player)
        {
            state.Reached = true;
        }

        if (!state.Reached && now - state.SpawnedAt >= GiveUpMs)
        {
            RemoveStalker(player, state, $"The {stalker.Name} loses your scent and turns back.");

            return;
        }

        if (distance <= BreathingRadius)
        {
            if (!state.Breathing)
            {
                state.Breathing = true;
                Tell(player, BreathingText, false);
            }
        }
        else if (now >= state.NextReportAt)
        {
            state.NextReportAt = now + ReportIntervalMs;
            Tell(player, ReportText(stalker.Name, ActGenerator.CompassDirection(player.X, player.Y, stalker.X, stalker.Y), distance), false);
        }

        Pursue(player, state, stalker, distance);
    }

    /// <summary>Walks the stalker to the player; close by, the player goes on its aggro list and the mob AI fights.</summary>
    private static void Pursue(GamePlayer player, HuntState state, ExpeditionStalker stalker, int distance)
    {
        if (stalker.Brain is not StandardMobBrain brain)
        {
            return;
        }

        if (brain.HasAggro)
        {
            return;
        }

        if (distance <= StalkerAggroRange)
        {
            brain.AddToAggroList(player, 1);

            if (brain.FSM != null && brain.FSM.GetCurrentState()?.StateType != eFSMStateType.AGGRO)
            {
                brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
            }

            return;
        }

        Point3D target = new(player.X, player.Y, player.Z);
        bool moved = state.LastPathTarget == null || DistanceSquared(target.X, target.Y, state.LastPathTarget.X, state.LastPathTarget.Y) > (long) RepathDistance * RepathDistance;

        if (moved || !stalker.IsMoving)
        {
            stalker.PathTo(target, stalker.MaxSpeed);
            state.LastPathTarget = target;
        }
    }

    private static void TrySpawn(GamePlayer player, ActPlan plan, HuntState state, long now)
    {
        ExpeditionStalker stalker;

        try
        {
            stalker = Spawn(player, plan);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] The hunt's stalker could not be spawned for {player.Name}.", ex);
            state.SpawnPending = false;

            return;
        }

        // No spot this tick (no floor in reach): tried again on the next.
        if (stalker == null)
        {
            return;
        }

        state.SpawnPending = false;
        state.Stalker = stalker;
        state.SpawnedAt = now;
        state.NextReportAt = now + ReportIntervalMs;
        state.Breathing = false;
        state.Reached = false;
        state.LastPathTarget = null;

        string direction = ActGenerator.CompassDirection(player.X, player.Y, stalker.X, stalker.Y);
        Tell(player, $"The hunt begins: the {stalker.Name} has your scent, {direction} of you.", true);
        player.Out.SendMessage($"It is level {stalker.Level} and Swift: {ExpeditionElites.AffixEffect(ExpeditionElites.Swift)}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        ExpeditionDebug.Report(player, $"hunt: {stalker.Name} (level {stalker.Level}) spawned at ({stalker.X}, {stalker.Y}, {stalker.Z}).");

        Pursue(player, state, stalker, (int) Math.Sqrt(DistanceSquared(player.X, player.Y, stalker.X, stalker.Y)));
    }

    private static void RemoveStalker(GamePlayer player, HuntState state, string message)
    {
        ExpeditionStalker stalker = state.Stalker;
        state.Stalker = null;
        state.Breathing = false;
        state.Reached = false;
        state.LastPathTarget = null;

        if (stalker == null || stalker.ObjectState != GameObject.eObjectState.Active)
        {
            return;
        }

        ExpeditionPower.Forget(stalker);
        stalker.Delete();

        if (message != null && player?.Client != null)
        {
            Tell(player, message, false);
        }
    }

    #endregion

    #region Spawning

    private static ExpeditionStalker Spawn(GamePlayer player, ActPlan plan)
    {
        (int X, int Y, int Z)? spot = FindSpot(player, plan);

        if (spot == null)
        {
            return null;
        }

        StandardMobBrain brain = new()
        {
            AggroLevel = 100,
            AggroRange = StalkerAggroRange
        };

        ExpeditionStalker stalker = new(brain);
        DbMob template = FindTemplate(plan);

        // The zone's own monster family: the camp objective's monster (else the boss's) as the Mob table has it.
        if (template != null)
        {
            stalker.Model = template.Model;
            stalker.Size = template.Size;
            stalker.Race = (short) template.Race;
            stalker.BodyType = (ushort) template.BodyType;

            if (template.MeleeDamageType > 0)
            {
                stalker.MeleeDamageType = (eDamageType) template.MeleeDamageType;
            }

            if (template.Speed > 0)
            {
                stalker.MaxSpeedBase = (short) template.Speed;
            }

            stalker.LoadEquipmentTemplateFromDatabase(template.EquipmentTemplateID);
        }
        else
        {
            stalker.Model = ExpeditionBoss.FallbackModel;
            stalker.Size = ExpeditionBoss.FallbackSize;
        }

        stalker.Name = StalkerName(plan.ZoneName);
        stalker.Realm = eRealm.None;
        stalker.RespawnInterval = -1;
        stalker.RoamingRange = 0;

        // As ExpeditionBoss.Spawn: cleared stats are auto-set by the Level setter for the stalker's level.
        stalker.Strength = 0;
        stalker.Constitution = 0;
        stalker.Dexterity = 0;
        stalker.Quickness = 0;
        stalker.Intelligence = 0;
        stalker.Empathy = 0;
        stalker.Piety = 0;
        stalker.Charisma = 0;
        // Tied to the player (owner 2026-10-30): orange for most of the game, where the act's top + 3 was purple for most of an act.
        stalker.Level = (byte) Math.Clamp(player.Level + LevelAbovePlayer, 1, 255);

        // Power-matched (owner 2026-10-30, ExpeditionPower): its health from the player's measured damage, its hits from the
        // player's health, at 90% of an even match; level-based until the meter has seen enough fighting.
        stalker.MatchedHealth = ExpeditionPower.StalkerHealth(ExpeditionPower.DpsOf(player));
        stalker.PowerMatched = stalker.MatchedHealth > 0;
        stalker.Health = stalker.MaxHealth;

        // Swift, as an elite's (ExpeditionElites.ApplyAffix): faster runs and swings. It is never restored: it is deleted.
        stalker.MaxSpeedBase = (short) Math.Min(short.MaxValue, Math.Round(stalker.MaxSpeedBase * ExpeditionElites.SwiftRunSpeedFactor));
        stalker.BaseBuffBonusCategory[eProperty.MeleeSpeed] += ExpeditionElites.SwiftMeleeSpeedBonus;

        stalker.CurrentRegionID = player.CurrentRegionID;
        stalker.X = spot.Value.X;
        stalker.Y = spot.Value.Y;
        stalker.Z = spot.Value.Z;
        stalker.Heading = (ushort) (new Point2D(spot.Value.X, spot.Value.Y).GetHeading(player.X, player.Y) % 4096);

        if (stalker.CurrentRegion == null || !stalker.AddToWorld())
        {
            log.Warn($"[EXPEDITION] The hunt's stalker could not be added to region {player.CurrentRegionID} at {spot.Value}.");

            return null;
        }

        return stalker;
    }

    /// <summary>A floor spot of the act's zone <see cref="SpawnMinDistance"/>-<see cref="SpawnMaxDistance"/> units from the player.</summary>
    private static (int X, int Y, int Z)? FindSpot(GamePlayer player, ActPlan plan)
    {
        Region region = player.CurrentRegion;
        IPathfindingMgr nav = PathfindingProvider.Instance;

        if (region == null)
        {
            return null;
        }

        long minSquared = (long) (SpawnMinDistance - 200) * (SpawnMinDistance - 200);
        long maxSquared = (long) (SpawnMaxDistance + 200) * (SpawnMaxDistance + 200);

        for (int i = 0; i < SpawnAttempts; i++)
        {
            double angle = Random.Shared.NextDouble() * 2 * Math.PI;
            int radius = Random.Shared.Next(SpawnMinDistance, SpawnMaxDistance + 1);
            int x = player.X + (int) Math.Round(radius * Math.Cos(angle));
            int y = player.Y + (int) Math.Round(radius * Math.Sin(angle));
            Zone zone = region.GetZone(x, y);

            if (zone == null || zone.ID != plan.ZoneId)
            {
                continue;
            }

            if (nav == null || !nav.IsAvailable || !nav.HasNavmesh(zone))
            {
                return (x, y, player.Z);
            }

            Vector3? floor = nav.GetClosestPoint(zone, new Vector3(x, y, player.Z), 128, 128, 2048, nav.DefaultFilters);

            if (!floor.HasValue)
            {
                continue;
            }

            long distanceSquared = DistanceSquared(player.X, player.Y, (int) floor.Value.X, (int) floor.Value.Y);

            if (distanceSquared < minSquared || distanceSquared > maxSquared)
            {
                continue;
            }

            return ((int) floor.Value.X, (int) floor.Value.Y, (int) floor.Value.Z);
        }

        return null;
    }

    private static DbMob FindTemplate(ActPlan plan)
    {
        string name = null;

        foreach (ActObjective objective in plan.Objectives ?? [])
        {
            if (objective != null && objective.Kind == eObjectiveKind.ClearCamp && !string.IsNullOrEmpty(objective.MobName))
            {
                name = objective.MobName;

                break;
            }
        }

        name ??= plan.Boss?.BaseMobName;

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            DbMob mob = GameServer.Database.SelectObject<DbMob>(DB.Column("Name").IsEqualTo(name).And(DB.Column("Region").IsEqualTo(plan.RegionId)));

            return mob ?? GameServer.Database.SelectObject<DbMob>(DB.Column("Name").IsEqualTo(name));
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Mob table lookup failed for the hunt's stalker ('{name}').", ex);

            return null;
        }
    }

    #endregion

    private static long DistanceSquared(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;

        return dx * dx + dy * dy;
    }

    /// <summary>Screen-centre headline (when asked) plus the system-window line.</summary>
    private static void Tell(GamePlayer player, string text, bool center)
    {
        if (string.IsNullOrEmpty(text) || player?.Client == null)
        {
            return;
        }

        if (center)
        {
            player.Out.SendMessage(text, eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        }

        player.Out.SendMessage(text, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }
}

/// <summary>
/// The hunt's stalker (<see cref="ExpeditionHunt"/>): never saved, never respawned, drops nothing. Its home is wherever it
/// stands, so the mob AI never walks it back to where it was spawned.
/// </summary>
public sealed class ExpeditionStalker : GameNPC
{
    public ExpeditionStalker(ABrain brain) : base(brain)
    {
    }

    public override bool CanDropLoot => false;

    /// <summary>Its health from the player's measured power (ExpeditionPower); 0 for the level-based health.</summary>
    public int MatchedHealth { get; set; }

    /// <summary>True when its health and hits are matched to the player's power (ExpeditionPower).</summary>
    public bool PowerMatched { get; set; }

    public override int MaxHealth => MatchedHealth > 0 ? MatchedHealth : base.MaxHealth;

    public override Point3D SpawnPoint
    {
        get => movementComponent == null ? base.SpawnPoint : new Point3D(X, Y, Z);
        set => base.SpawnPoint = value;
    }
}
