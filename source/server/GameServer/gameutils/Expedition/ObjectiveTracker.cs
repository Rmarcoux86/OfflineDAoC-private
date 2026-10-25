using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Follows a run's act objectives for one attached player: kills (the player's own, their pets', their companion
/// bots' and their group's), reach-point objectives and the mists boundary that keeps the player in the act's zone.
/// When every objective is done it spawns the act boss (<see cref="ExpeditionBoss"/>) and tells
/// <see cref="ExpeditionManager"/>; when the boss dies it hands out the boss loot (<see cref="ExpeditionLoot.BossDrops"/>) and reports the kill.
/// <para>
/// Kill credit: GameLiving.ProcessDeath (GameLiving.cs:1996) raises EnemyKilled on the owner of a pet or companion
/// bot (BotBrain is an IControlledBrain whose owner is the player) and on group players in range, so the per-player
/// handler sees most kills. A group member that no player owns (an autonomous gamebot in the player's group) only
/// raises EnemyKilled on itself, so a global handler also looks at NPC killers. Both paths end in
/// <see cref="HandleKill"/>, which ignores a target it already counted in the last few seconds.
/// </para>
/// <para>
/// Elite packs: the tick wakes the act's elite camps the player comes near (<see cref="ExpeditionElites.CheckCamps"/>)
/// and Detach restores them (<see cref="ExpeditionElites.RestoreAll(GamePlayer)"/>).
/// </para>
/// Run counters: <see cref="ExpeditionManager"/> already counts <c>run.Kills</c> (its own EnemyKilled handler) and
/// <c>run.BossesKilled</c> (in OnBossKilled), so this tracker does not touch them.
/// </summary>
public static class ObjectiveTracker
{
    private const int TickIntervalMs = 2000;
    private const long GraceMs = 15_000;
    private const long MistsDelayMs = 10_000;
    private const long KillDedupeWindowMs = 5000;
    private const long BossRespawnDelayMs = 30_000;
    private const int DefaultReachRadius = 300;

    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly ConcurrentDictionary<string, TrackerState> States = new(StringComparer.Ordinal);
    private static readonly object GlobalHandlerSync = new();
    private static bool _globalHandlerRegistered;

    private sealed class TrackerState
    {
        public readonly object Sync = new();
        public readonly Dictionary<GameLiving, long> RecentKills = new();
        public GamePlayer Player;
        public string Key;
        public ECSGameTimer Timer;

        /// <summary>The act this attach was made for. When the manager moves the run on, the tracker waits for the next Attach.</summary>
        public ActPlan Plan;
        public long GraceUntil;
        public bool MistsWarned;
        public long MistsWarnedAt;
        public bool Detached;

        public ExpeditionBoss Boss;
        public bool BossPending;
        public long BossRespawnAt;

        /// <summary>Indexes into <c>Plan.Elites</c> of the elite camps this attach has woken (tick only).</summary>
        public readonly HashSet<int> WokenEliteCamps = new();
    }

    #region Contract

    public static void Attach(GamePlayer player)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId))
        {
            return;
        }

        // Attach is idempotent: a second call rebuilds the state for the run's current act.
        Detach(player);
        ExpeditionLoot.ResetLuck(player);

        ExpeditionRun run = ExpeditionManager.GetRun(player);
        ActPlan plan = run?.CurrentAct;

        if (plan == null)
        {
            return;
        }

        EnsureGlobalHandler();

        TrackerState state = new()
        {
            Player = player,
            Key = player.ObjectId,
            Plan = plan,
            GraceUntil = GameLoop.GameLoopTime + GraceMs
        };

        States[state.Key] = state;
        GameEventMgr.AddHandler(player, GameLivingEvent.EnemyKilled, OnPlayerEnemyKilled);
        state.Timer = new ECSGameTimer(player, _ => Tick(state), TickIntervalMs);

        // A boss that was up when the player left is spawned fresh; an act whose objectives were all done but whose
        // boss never came (a crash in between) is completed now.
        bool spawnBoss = false;
        bool completedNow = false;

        lock (state.Sync)
        {
            if (plan.BossSpawned && !plan.BossKilled)
            {
                state.BossPending = true;
                spawnBoss = true;
            }
            else if (!plan.BossSpawned && AllObjectivesDone(plan))
            {
                plan.BossSpawned = true;
                state.BossPending = true;
                spawnBoss = true;
                completedNow = true;
            }
        }

        if (spawnBoss)
        {
            SpawnBoss(state, plan);
        }

        if (completedNow)
        {
            ExpeditionManager.OnObjectivesComplete(player);
        }
    }

    public static bool IsAttached(GamePlayer player)
    {
        return player != null && States.TryGetValue(player.ObjectId ?? string.Empty, out TrackerState state) && state.Player == player && !state.Detached;
    }

    public static void Detach(GamePlayer player)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId))
        {
            return;
        }

        ExpeditionShrines.Despawn(player);
        ExpeditionExchanger.Despawn(player);

        if (!States.TryRemove(player.ObjectId, out TrackerState state))
        {
            return;
        }

        ExpeditionBoss boss;

        lock (state.Sync)
        {
            state.Detached = true;
            boss = state.Boss;
            state.Boss = null;
            state.RecentKills.Clear();
        }

        GameEventMgr.RemoveHandler(state.Player, GameLivingEvent.EnemyKilled, OnPlayerEnemyKilled);
        state.Timer?.Stop();

        // Elite packs woken for this player turn back into the world's ordinary monsters.
        ExpeditionElites.RestoreAll(state.Player);

        // The boss leaves with the player; Attach spawns it fresh (BossSpawned is saved, BossKilled is not set).
        if (boss != null)
        {
            boss.Release();

            if (boss.ObjectState == GameObject.eObjectState.Active)
            {
                boss.Delete();
            }
        }
    }

    #endregion

    #region Kill credit

    /// <summary>
    /// True when <paramref name="source"/> is the player, their pet or companion bot, a member of their group, or a
    /// pet of a group member.
    /// </summary>
    internal static bool IsCreditedTo(GamePlayer player, GameObject source)
    {
        if (player == null || source == null)
        {
            return false;
        }

        if (source == player)
        {
            return true;
        }

        Group group = player.Group;

        if (source is GameLiving living && group != null && group.IsInTheGroup(living))
        {
            return true;
        }

        if (source is GameNPC npc && npc.Brain is IControlledBrain controlled)
        {
            if (controlled.GetPlayerOwner() == player)
            {
                return true;
            }

            GameLiving owner = controlled.GetLivingOwner();

            if (owner == player)
            {
                return true;
            }

            if (owner != null && group != null && group.IsInTheGroup(owner))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureGlobalHandler()
    {
        lock (GlobalHandlerSync)
        {
            if (_globalHandlerRegistered)
            {
                return;
            }

            GameEventMgr.AddHandler(GameLivingEvent.EnemyKilled, OnGlobalEnemyKilled);
            _globalHandlerRegistered = true;
        }
    }

    /// <summary>Per-player handler: the sender is the attached player (GamePlayer.EnemyKilled, GameLiving.EnemyKilled).</summary>
    private static void OnPlayerEnemyKilled(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GamePlayer player && args is EnemyKilledEventArgs killedArgs)
        {
            CreditKill(player, killedArgs.Target);
        }
    }

    /// <summary>
    /// Counts a kill for the player's objectives (and its loot and realm points). Reached from the player's EnemyKilled
    /// event, and from <see cref="ExpeditionManager"/> for a kill the game did not credit to the player (a monster that
    /// died to a damage-over-time tick after the player's attacker entry expired). HandleKill ignores a repeat.
    /// </summary>
    internal static void CreditKill(GamePlayer player, GameLiving target)
    {
        EnemyKilledEventArgs killedArgs = new(target);

        if (!States.TryGetValue(player.ObjectId, out TrackerState state) || state.Player != player)
        {
            ExpeditionDebug.Report(player, $"kill of {killedArgs.Target?.Name} ignored: the tracker is not attached to this player (state found: {state != null}).");

            return;
        }

        try
        {
            HandleKill(state, killedArgs.Target);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Objective kill tracking failed for {player.Name}.", ex);
        }
    }

    /// <summary>Global handler for NPC killers (pets, companion bots, unowned group bots) of attached players.</summary>
    private static void OnGlobalEnemyKilled(DOLEvent e, object sender, EventArgs args)
    {
        if (States.IsEmpty || sender is not GameNPC killer || args is not EnemyKilledEventArgs killedArgs)
        {
            return;
        }

        foreach (TrackerState state in States.Values)
        {
            if (!IsCreditedTo(state.Player, killer))
            {
                continue;
            }

            try
            {
                HandleKill(state, killedArgs.Target);
            }
            catch (Exception ex)
            {
                log.Error($"[EXPEDITION] Objective kill tracking failed for {state.Player?.Name}.", ex);
            }
        }
    }

    private static void HandleKill(TrackerState state, GameLiving target)
    {
        // GameLiving.RemoveFromWorld (GameLiving.cs:3591) also calls EnemyKilled on every attacker of a living that
        // merely despawns; only a living that is really dying (handled by the ReaperService) or at 0 health counts.
        GamePlayer player = state.Player;

        if (target == null || target is ExpeditionBoss || !(target.IsBeingHandledByReaperService || target.Health <= 0))
        {
            ExpeditionDebug.Report(player, $"kill of {target?.Name} ignored: not a dying monster (boss {target is ExpeditionBoss}, reaper {target?.IsBeingHandledByReaperService}, health {target?.Health}).");

            return;
        }

        ExpeditionRun run = ExpeditionManager.GetRun(player);
        ActPlan plan = run?.CurrentAct;

        if (plan == null || plan != state.Plan || plan.Objectives == null)
        {
            ExpeditionDebug.Report(player, $"kill of {target.Name} ignored: run {(run == null ? "missing" : "found")}, plan {(plan == null ? "missing" : "found")}, same plan as the tracker {plan == state.Plan}.");

            return;
        }

        long now = GameLoop.GameLoopTime;
        string progressMessage = null;
        string doneMessage = null;
        bool completedNow = false;
        bool firstObjectiveKill = false;
        ActObjective objective;

        lock (state.Sync)
        {
            if (state.Detached)
            {
                ExpeditionDebug.Report(player, $"kill of {target.Name} ignored: the tracker was detached.");

                return;
            }

            if (state.RecentKills.TryGetValue(target, out long countedAt) && now - countedAt < KillDedupeWindowMs)
            {
                ExpeditionDebug.Report(player, $"kill of {target.Name} ignored: already counted {now - countedAt} ms ago.");

                return;
            }

            state.RecentKills[target] = now;

            // One kill advances one objective: the first open one it matches.
            objective = FindKillObjective(plan, target);

            if (objective != null)
            {
                // The run's first objective kill: act 1, and no kill objective has any progress yet.
                firstObjectiveKill = run.Act == 1 && !AnyKillProgress(plan);

                objective.Progress = Math.Min(objective.Count, objective.Progress + 1);
                progressMessage = $"Objective: {objective.Description} ({objective.Progress}/{objective.Count})";

                if (objective.Done)
                {
                    doneMessage = $"Objective complete: {objective.Description}";
                    run.AddLog($"Act {plan.Act}: objective complete - {objective.Description}.");
                }

                if (!plan.BossSpawned && AllObjectivesDone(plan))
                {
                    plan.BossSpawned = true;
                    state.BossPending = true;
                    completedNow = true;
                }
            }
        }

        // Every credited kill of a monster may drop Expedition loot; the run's first objective kill always drops Magic+.
        bool dropped = ExpeditionLoot.OnKill(player, target, firstObjectiveKill ? eLootRarity.Magic : null);
        ExpeditionRealm.OnKill(player, target);
        BoonEffects.OnKill(player, target);

        if (objective == null)
        {
            ExpeditionDebug.Report(player, $"kill of {target.Name} matched no open objective (zone {target.CurrentZone?.Description}).");
            TellIfWrongCamp(player, plan, target);

            if (dropped)
            {
                ExpeditionManager.Save(run);
            }

            return;
        }

        Tell(player, progressMessage, false);

        if (doneMessage != null)
        {
            Tell(player, doneMessage, true);
            ExpeditionRealm.OnObjectiveDone(player, plan.Act);
        }

        FinishProgress(state, run, plan, completedNow);
    }

    private static Zone ObjectiveZone(ActObjective objective)
    {
        try
        {
            return WorldMgr.GetRegion((ushort) objective.RegionId)?.GetZone(objective.X, objective.Y);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A kill that matched an open objective's monster name but not its camp: says where the objective's camp is, so a
    /// player fighting the same monster in another camp knows why nothing counted.
    /// </summary>
    private static void TellIfWrongCamp(GamePlayer player, ActPlan plan, GameLiving target)
    {
        foreach (ActObjective objective in plan.Objectives)
        {
            if (objective == null || objective.Done || objective.Kind == eObjectiveKind.ReachPoint)
            {
                continue;
            }

            if (!string.Equals(target.Name, objective.MobName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string loc = ActGenerator.DescribeLoc(objective.RegionId, objective.X, objective.Y);
            string where = loc == null ? "elsewhere" : $"at {loc}";
            int distance = (int) Math.Sqrt(Math.Pow(target.X - objective.X, 2) + Math.Pow(target.Y - objective.Y, 2));

            Tell(player, $"That {target.Name} is outside the objective's zone. Its camp is {where}, about {distance} units from here.", false);

            return;
        }
    }

    /// <summary>True when a kill objective of the act already has progress. The caller holds <c>state.Sync</c>.</summary>
    private static bool AnyKillProgress(ActPlan plan)
    {
        foreach (ActObjective objective in plan.Objectives)
        {
            if (objective != null && objective.Kind != eObjectiveKind.ReachPoint && objective.Progress > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static ActObjective FindKillObjective(ActPlan plan, GameLiving target)
    {
        foreach (ActObjective objective in plan.Objectives)
        {
            if (objective == null || objective.Done)
            {
                continue;
            }

            if (objective.Kind != eObjectiveKind.ClearCamp && objective.Kind != eObjectiveKind.KillNamed)
            {
                continue;
            }

            if (string.IsNullOrEmpty(objective.MobName) || !string.Equals(target.Name, objective.MobName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (target.CurrentRegionID != objective.RegionId)
            {
                continue;
            }

            // No radius: anywhere in the region counts (KillNamed by contract; ClearCamp too, so a camp with a
            // missing radius can still be finished).
            if (objective.Radius <= 0)
            {
                return objective;
            }

            // Anywhere in the objective's zone counts: a zone often has several camps of the same monster, and the
            // area words ("the heart of Connacht") cannot say which one the generator picked.
            Zone objectiveZone = ObjectiveZone(objective);

            if (objectiveZone != null && target.CurrentZone != null)
            {
                if (target.CurrentZone.ID == objectiveZone.ID)
                {
                    return objective;
                }

                continue;
            }

            // No world (unit tests): 2D radius around the camp; camps sit on slopes, and Z is a single spot height.
            if (target.IsWithinRadius(new Point3D(objective.X, objective.Y, objective.Z), objective.Radius, true))
            {
                return objective;
            }
        }

        return null;
    }

    #endregion

    #region Timer: reach points, mists, boss upkeep

    private static int Tick(TrackerState state)
    {
        try
        {
            return TickInternal(state);
        }
        catch (Exception ex)
        {
            // An exception escaping a timer owned by a player kicks that player (GameServiceUtils.HandleServiceException).
            log.Error($"[EXPEDITION] Objective tick failed for {state.Player?.Name}.", ex);

            return TickIntervalMs;
        }
    }

    private static int TickInternal(TrackerState state)
    {
        if (state.Detached)
        {
            return 0;
        }

        GamePlayer player = state.Player;
        GameClient client = player.Client;

        // Fallback cleanup: the manager detaches on quit, but never keep a timer for a player who is gone.
        if (client == null || player.ObjectState == GameObject.eObjectState.Deleted ||
            client.ClientState is GameClient.eClientState.Disconnected or GameClient.eClientState.NotConnected)
        {
            Detach(player);

            return 0;
        }

        long now = GameLoop.GameLoopTime;

        lock (state.Sync)
        {
            List<GameLiving> expired = null;

            foreach (KeyValuePair<GameLiving, long> entry in state.RecentKills)
            {
                if (now - entry.Value >= KillDedupeWindowMs)
                {
                    expired ??= new List<GameLiving>();
                    expired.Add(entry.Key);
                }
            }

            if (expired != null)
            {
                foreach (GameLiving living in expired)
                {
                    state.RecentKills.Remove(living);
                }
            }
        }

        ExpeditionRun run = ExpeditionManager.GetRun(player);
        ActPlan plan = run?.CurrentAct;

        // The run moved on to another act (the manager re-attaches after its move) or ended: wait.
        if (plan == null || plan != state.Plan)
        {
            RemoveStaleBoss(state);
            state.MistsWarned = false;

            return TickIntervalMs;
        }

        UpkeepBoss(state, plan, now);

        // Mid region change (RemoveFromWorld until the client has loaded) or linkdead: nothing to check.
        if (player.ObjectState != GameObject.eObjectState.Active || client.ClientState != GameClient.eClientState.Playing || player.CurrentRegion == null)
        {
            state.MistsWarned = false;

            return TickIntervalMs;
        }

        if (player.IsAlive)
        {
            CheckReachPoints(state, run, plan, player);

            // A reach point can finish the act; a detached state must not wake packs nobody would restore.
            if (!state.Detached)
            {
                ExpeditionElites.CheckCamps(player, plan, state.WokenEliteCamps);
                ExpeditionShrines.Tick(player, run, plan);
                ExpeditionExchanger.Tick(player, run, plan);
            }
        }

        CheckMists(state, plan, player, now);

        return TickIntervalMs;
    }

    private static void CheckReachPoints(TrackerState state, ExpeditionRun run, ActPlan plan, GamePlayer player)
    {
        if (plan.Objectives == null)
        {
            return;
        }

        List<string> doneMessages = null;
        bool completedNow = false;

        lock (state.Sync)
        {
            if (state.Detached)
            {
                return;
            }

            foreach (ActObjective objective in plan.Objectives)
            {
                if (objective == null || objective.Done || objective.Kind != eObjectiveKind.ReachPoint)
                {
                    continue;
                }

                if (player.CurrentRegionID != objective.RegionId)
                {
                    continue;
                }

                int radius = objective.Radius > 0 ? objective.Radius : DefaultReachRadius;

                if (!player.IsWithinRadius(new Point3D(objective.X, objective.Y, objective.Z), radius, true))
                {
                    continue;
                }

                objective.Progress = Math.Max(objective.Count, 1);
                doneMessages ??= new List<string>();
                doneMessages.Add($"Objective complete: {objective.Description}");
                run.AddLog($"Act {plan.Act}: objective complete - {objective.Description}.");
            }

            if (doneMessages == null)
            {
                return;
            }

            if (!plan.BossSpawned && AllObjectivesDone(plan))
            {
                plan.BossSpawned = true;
                state.BossPending = true;
                completedNow = true;
            }
        }

        foreach (string message in doneMessages)
        {
            Tell(player, message, true);
            ExpeditionRealm.OnObjectiveDone(player, plan.Act);
        }

        FinishProgress(state, run, plan, completedNow);
    }

    private static void CheckMists(TrackerState state, ActPlan plan, GamePlayer player, long now)
    {
        if (now < state.GraceUntil)
        {
            state.MistsWarned = false;

            return;
        }

        Zone zone = player.CurrentZone;
        bool outside = player.CurrentRegionID != plan.RegionId || (zone != null && zone.ID != plan.ZoneId);

        // Dead players are left to the death/release flow; instances are their own temporary regions.
        if (!outside || !player.IsAlive || player.CurrentRegion.IsInstance)
        {
            if (state.MistsWarned && !outside)
            {
                player.Out.SendMessage("The mists recede.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            }

            state.MistsWarned = false;

            return;
        }

        if (!state.MistsWarned)
        {
            state.MistsWarned = true;
            state.MistsWarnedAt = now;
            Tell(player, $"The mists close in... Return to {plan.ZoneName} or they will carry you back to camp.", true);

            return;
        }

        if (now - state.MistsWarnedAt < MistsDelayMs)
        {
            return;
        }

        state.MistsWarned = false;
        state.GraceUntil = now + GraceMs;

        if (!ExpeditionArrival.MoveToCamp(player, ExpeditionManager.GetRun(player), plan))
        {
            log.Warn($"[EXPEDITION] The mists could not move {player.Name} to the act {plan.Act} camp in region {plan.RegionId} ({plan.CampX}, {plan.CampY}, {plan.CampZ}).");

            return;
        }

        Tell(player, "The mists carry you back to camp.", true);
    }

    #endregion

    #region Completion and boss

    /// <summary>Saves progress, and on completion spawns the boss and tells the manager (which saves itself).</summary>
    private static void FinishProgress(TrackerState state, ExpeditionRun run, ActPlan plan, bool completedNow)
    {
        if (!completedNow)
        {
            ExpeditionManager.Save(run);

            return;
        }

        SpawnBoss(state, plan);
        ExpeditionManager.OnObjectivesComplete(state.Player);
    }

    private static bool AllObjectivesDone(ActPlan plan)
    {
        if (plan.Objectives == null)
        {
            return true;
        }

        foreach (ActObjective objective in plan.Objectives)
        {
            if (objective != null && !objective.Done)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Spawns the act boss. The caller has set <c>state.BossPending</c> under the lock.</summary>
    private static void SpawnBoss(TrackerState state, ActPlan plan)
    {
        GamePlayer player = state.Player;
        ExpeditionBoss boss = null;

        try
        {
            if (plan.Boss != null)
            {
                boss = ExpeditionBoss.Spawn(player, plan.Boss);
            }
            else
            {
                log.Warn($"[EXPEDITION] Act {plan.Act} of {player.Name} has no boss spec.");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] The act {plan.Act} boss could not be spawned for {player.Name}.", ex);
            boss = null;
        }

        bool discard = false;

        lock (state.Sync)
        {
            state.BossPending = false;

            if (boss == null)
            {
                state.BossRespawnAt = GameLoop.GameLoopTime + BossRespawnDelayMs;
            }
            else if (state.Detached || state.Plan != plan)
            {
                discard = true;
            }
            else
            {
                state.Boss = boss;
            }
        }

        if (boss == null)
        {
            return;
        }

        if (discard)
        {
            boss.Release();
            boss.Delete();

            return;
        }

        string bossLoc = ActGenerator.DescribeLoc(plan.RegionId, boss.X, boss.Y);

        Tell(player, bossLoc == null ? $"{boss.Name} has appeared at {plan.ZoneName}!" : $"{boss.Name} has appeared at {plan.ZoneName}, at {bossLoc}!", true);

        // A named boss with a line shouts it as it appears; the player hears it wherever they are in the zone.
        string quote = ActGenerator.BossQuote(plan.Boss?.Title);

        if (quote != null)
        {
            // Players within yelling distance hear the yell itself; one farther off is told directly.
            boss.Yell(quote);

            if (!player.IsWithinRadius(boss, WorldMgr.YELL_DISTANCE))
            {
                player.Out.SendMessage($"{boss.Name} shouts, \"{quote}\"", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            }
        }
    }

    /// <summary>Respawns a boss that should be up but is not (the player came back, or it died to someone else).</summary>
    private static void UpkeepBoss(TrackerState state, ActPlan plan, long now)
    {
        bool spawn = false;

        lock (state.Sync)
        {
            if (state.Detached || !plan.BossSpawned || plan.BossKilled || state.BossPending || now < state.BossRespawnAt)
            {
                return;
            }

            if (state.Boss != null && state.Boss.ObjectState == GameObject.eObjectState.Active)
            {
                return;
            }

            state.Boss = null;
            state.BossPending = true;
            spawn = true;
        }

        if (spawn)
        {
            SpawnBoss(state, plan);
        }
    }

    private static void RemoveStaleBoss(TrackerState state)
    {
        ExpeditionBoss boss;

        lock (state.Sync)
        {
            boss = state.Boss;

            if (boss == null || !boss.IsAlive)
            {
                return;
            }

            state.Boss = null;
        }

        boss.Release();
        boss.Delete();
    }

    /// <summary>Called by <see cref="ExpeditionBoss.ProcessDeath"/> after the base death processing.</summary>
    internal static void OnBossDied(ExpeditionBoss boss, bool credited)
    {
        if (boss?.OwnerKey == null || !States.TryGetValue(boss.OwnerKey, out TrackerState state))
        {
            return;
        }

        GamePlayer player = state.Player;
        ExpeditionRun run = ExpeditionManager.GetRun(player);
        ActPlan plan = run?.CurrentAct;

        lock (state.Sync)
        {
            if (state.Detached || state.Boss != boss)
            {
                return;
            }

            state.Boss = null;

            if (plan == null || plan != state.Plan || plan.BossKilled)
            {
                return;
            }

            if (!credited)
            {
                state.BossRespawnAt = GameLoop.GameLoopTime + BossRespawnDelayMs;
            }
            else
            {
                // The journal line is written by ExpeditionManager.OnBossKilled.
                plan.BossKilled = true;
            }
        }

        if (!credited)
        {
            Tell(player, $"{boss.Name} was slain by another hand. It will return to {plan.ZoneName}.", true);

            return;
        }

        Tell(player, $"{boss.Name} is slain!", true);
        ExpeditionLoot.BossDrops(player, boss);

        // The manager saves the run, advances or ends it, and may Detach this player.
        ExpeditionManager.OnBossKilled(player);
    }

    #endregion

    /// <summary>Screen-centre headline (shown by clients whose packet lib supports it) plus the system-window line.</summary>
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
