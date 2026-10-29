using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerTitles;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Expedition mode's run framework: starts, resumes, saves and ends seeded roguelite runs. A run is a hardcore
/// character that starts at level 1 in act 1's camp and goes act by act (<see cref="ActGenerator"/>) to the act-5
/// boss. <see cref="ObjectiveTracker"/> follows the objectives and calls back here when they are done and when the
/// boss dies. Death ends the run and, through the hardcore flag, deletes the character; the run's row stays.
/// Only real players have runs (gamebots and companions are GameNPCs and never reach these handlers as players).
/// </summary>
public static class ExpeditionManager
{
    public const string StatusActive = "active";
    public const string StatusWon = "won";
    public const string StatusDead = "dead";
    public const string StatusAbandoned = "abandoned";

    private const int JournalLogLines = 15;
    private const int NextActTeleportDelayMilliseconds = 15_000;

    /// <summary>How long after its end a death or victory window is still shown at the account's next login.</summary>
    private static readonly TimeSpan PendingSummaryWindow = TimeSpan.FromHours(24);

    /// <summary>ActGenerator's "no previous zone" (zone 0 is Camelot Hills, a real act-1 zone).</summary>
    private const int NoPreviousZone = -1;

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Active runs of characters in the game, by character id.</summary>
    private static readonly ConcurrentDictionary<string, ExpeditionRun> Runs = new(StringComparer.Ordinal);

    #region Server and player events

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(OnGameEntered));
        GameEventMgr.AddHandler(GamePlayerEvent.Quit, new DOLEventHandler(OnQuit));
        GameEventMgr.AddHandler(GamePlayerEvent.Linkdeath, new DOLEventHandler(OnLinkdeath));
        GameEventMgr.AddHandler(GameLivingEvent.Dying, new DOLEventHandler(OnDying));
        GameEventMgr.AddHandler(GameLivingEvent.EnemyKilled, new DOLEventHandler(OnEnemyKilled));
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(OnGameEntered));
        GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, new DOLEventHandler(OnQuit));
        GameEventMgr.RemoveHandler(GamePlayerEvent.Linkdeath, new DOLEventHandler(OnLinkdeath));
        GameEventMgr.RemoveHandler(GameLivingEvent.Dying, new DOLEventHandler(OnDying));
        GameEventMgr.RemoveHandler(GameLivingEvent.EnemyKilled, new DOLEventHandler(OnEnemyKilled));

        foreach (ExpeditionRun run in Runs.Values)
        {
            Save(run);
        }

        Runs.Clear();
    }

    private static void OnGameEntered(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player)
        {
            return;
        }

        ShowPendingSummary(player);
        ExpeditionUniques.UpdateOldThreePointOhRings(player);
        ExpeditionLoot.FixOldLevelRequirements(player);
        ExpeditionUniques.UpdateOldUniqueLooks(player);
        ExpeditionUniques.LevelUniques(player);

        OfflineExpeditionRecord record;

        try
        {
            record = GameServer.Database.FindObjectByKey<OfflineExpeditionRecord>(player.ObjectId);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not load the run of {player.Name}.", ex);

            return;
        }

        // A run revived by /recover resumes like an active one (ExpeditionRecovery).
        bool recovered = record?.Status == ExpeditionRecovery.StatusRecovered;

        if (record == null || (record.Status != StatusActive && !recovered))
        {
            return;
        }

        ExpeditionRun run = FromRecord(record, player);

        if (string.IsNullOrEmpty(run.AccountName))
        {
            run.AccountName = player.Client?.Account?.Name ?? string.Empty;
        }

        // HardCoreLogin deletes a hardcore character that has died (GamePlayer.DeathCount > 0) as it enters the game.
        // That death was not seen by this run (server stopped mid-death), so the run ends here.
        if (player.HCFlag && player.DeathCount > 0)
        {
            run.Status = StatusDead;
            run.Deaths++;
            run.EndedUtc = DateTime.UtcNow;
            run.AddLog($"Act {run.Act}: {run.CharacterName} was found dead on returning. The expedition is over.");
            Save(run);

            return;
        }

        if (recovered)
        {
            ExpeditionRecovery.PrepareRecoveredRun(player, run);
        }

        if (run.CurrentAct == null)
        {
            player.Out.SendMessage("Your expedition could not be resumed: its act could not be read. The run is kept as it was.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            return;
        }

        // Fork (owner 2026-10-25): an act not yet begun in a zone since taken out of that act moves to one of its zones.
        bool replanned = !recovered && ExpeditionRecovery.ReplanIfZoneRemoved(player, run, false);

        if (replanned)
        {
            Save(run);
            player.Out.SendMessage($"Your act's zone is no longer used for act {run.Act}; the act now takes place in {run.CurrentAct.ZoneName}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        if (UpdateBossTitle(run, run.CurrentAct))
        {
            Save(run);
        }

        // An act planned under older rules gets what the act would get now (shrines; objectives while its boss is away).
        string upgraded = ActGenerator.UpgradeInProgress(run.CurrentAct, player.Realm, run.Seed);

        if (upgraded != null)
        {
            run.AddLog($"Act {run.Act}: updated to the latest rules ({upgraded}).");
            Save(run);
        }

        // Fork (owner 2026-10-25): a camp planned on top of aggressive monsters is moved before the player can use it.
        if (ExpeditionArrival.EnsureSafeCamp(run.CurrentAct, player.Realm, out bool campMoved))
        {
            if (campMoved)
            {
                run.AddLog($"Act {run.Act}: the camp was moved to a spot clear of aggressive monsters.");
            }

            Save(run);
        }

        Runs[run.CharacterId] = run;

        // Fork (owner 2026-10-25): realm points are fully shared; every login tops the character up to the account's total.
        ExpeditionAccountRealm.SyncToAccount(player, run);
        ObjectiveTracker.Attach(player);

        if (ExpeditionBoons.RemoveRetired(player, run))
        {
            Save(run);
        }

        // Boon effects live on the GamePlayer object only, so each login applies them again.
        ExpeditionBoons.ApplyAll(player, run);

        player.Out.SendMessage($"Expedition resumed: act {run.Act} of {ActGenerator.ActCount}, {run.CurrentAct?.ZoneName}. Type /run journal for your objectives.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        ExpeditionBoons.RemindOffer(player, run);
        WhenSettled(player, player.CurrentRegionID, () =>
        {
            // A boon still waiting from the last act break opens its window at login too.
            if (run.PendingBoonOffer.Count > 0)
            {
                player.Out.SendCustomTextWindow("Expedition boons", ExpeditionBoons.OfferLines(run));
            }

            ExpeditionPromotion.OnRunResumed(player);
            ExpeditionRealm.Backfill(player);
            GiveRecallStone(player);

            if (upgraded != null)
            {
                player.Out.SendMessage($"Your act has been updated to the latest rules: {upgraded}. Type /run journal to see them.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            }

            if (replanned)
            {
                ExpeditionArrival.MoveToCamp(player, run, run.CurrentAct);
            }

            if (recovered)
            {
                player.Out.SendMessage($"Your character was restored and your expedition carries on: act {run.Act}, {run.CurrentAct.ZoneName}. You are taken to the act's camp.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                ExpeditionArrival.MoveToCamp(player, run, run.CurrentAct);
                ExpeditionBoons.RemindOffer(player, run);
            }
        });
    }

    /// <summary>
    /// A death's end-of-run window is sent just before HardCoreLogin.HandleDeath kicks the player, so it is usually
    /// never seen. At the account's next login the most recent run (any character) is shown once, if it ended in the
    /// last 24 hours in death or victory and its window was not seen yet.
    /// </summary>
    private static void ShowPendingSummary(GamePlayer player)
    {
        string accountName = player.Client?.Account?.Name;

        // A hardcore character with a death on record is kicked and deleted by HardCoreLogin as it enters.
        if (string.IsNullOrEmpty(accountName) || (player.HCFlag && player.DeathCount > 0))
        {
            return;
        }

        OfflineExpeditionRecord latest;

        try
        {
            latest = GameServer.Database.SelectObjects<OfflineExpeditionRecord>(DB.Column("AccountName").IsEqualTo(accountName))
                .OrderByDescending(r => ParseUtc(r.StartedUtc) ?? DateTime.MinValue)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the runs of account {accountName}.", ex);

            return;
        }

        if (latest == null || latest.SummaryShown || (latest.Status != StatusDead && latest.Status != StatusWon))
        {
            return;
        }

        DateTime? ended = ParseUtc(latest.EndedUtc);

        if (ended == null || DateTime.UtcNow - ended.Value > PendingSummaryWindow)
        {
            return;
        }

        ExpeditionRun run = FromRecord(latest, null);
        run.SummaryShown = true;
        Save(run);

        string caption = latest.Status == StatusWon ? "Expedition won" : "Expedition over";
        player.Out.SendCustomTextWindow(caption, SummaryLines(run, latest.Status == StatusWon ? "VICTORY" : "RUN OVER"));
    }

    private static DateTime? ParseUtc(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static void OnQuit(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player || !Runs.TryRemove(player.ObjectId, out ExpeditionRun run))
        {
            return;
        }

        ObjectiveTracker.Detach(player);
        ExpeditionBoons.RemoveAll(player, false);
        Save(run);
    }

    /// <summary>
    /// Saves only: a linkdead player stays in the world (and can still die) until the link-death timer quits it,
    /// which fires <see cref="GamePlayerEvent.Quit"/>; the run is detached and uncached there.
    /// </summary>
    private static void OnLinkdeath(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player)
        {
            return;
        }

        ExpeditionRun run = GetRun(player);

        if (run != null)
        {
            Save(run);
        }
    }

    /// <summary>
    /// GameLiving.ProcessDeath fires Dying (GameLiving.cs:2073) from inside GamePlayer.ProcessDeath (base call at
    /// GamePlayer.cs:5931), before the hardcore deletion at the end of it (GamePlayer.cs:6084).
    /// </summary>
    private static void OnDying(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GameNPC npc)
        {
            CreditUntrackedKill(npc, (args as DyingEventArgs)?.Killer);

            return;
        }

        if (sender is not GamePlayer player)
        {
            return;
        }

        ExpeditionRun run = GetRun(player);

        if (run == null)
        {
            return;
        }

        string killer = (args as DyingEventArgs)?.Killer?.Name;

        if (!string.IsNullOrEmpty(killer))
        {
            run.AddLog($"Act {run.Act}: slain by {killer}.");
        }

        OnPlayerDied(player);
    }

    /// <summary>
    /// A monster killed by a run's player who is no longer on its attacker list: a damage-over-time spell only lists its
    /// caster on the first tick (DoTSpellHandler sets CausesCombat on that tick only), and the entry expires after a few
    /// seconds, so a later tick's kill raises no EnemyKilled for the player (GameLiving.ProcessDeath). The run counts the
    /// kill here instead; a kill that is also reported the normal way is ignored as a repeat.
    /// </summary>
    private static void CreditUntrackedKill(GameNPC npc, GameObject killer)
    {
        GamePlayer player = killer as GamePlayer;

        if (player == null && killer is GameNPC pet && pet.Brain is IControlledBrain brain)
        {
            player = brain.GetPlayerOwner();
        }

        if (player == null || GetRun(player) == null || npc.attackComponent?.AttackerTracker == null)
        {
            return;
        }

        if (npc.attackComponent.AttackerTracker.ContainsAttacker(player) || (killer is GameLiving livingKiller && npc.attackComponent.AttackerTracker.ContainsAttacker(livingKiller)))
        {
            return;
        }

        ExpeditionDebug.Report(player, $"{npc.Name} died to {killer?.Name} without listing you as an attacker (damage over time); the kill is counted.");

        OnEnemyKilled(null, player, new EnemyKilledEventArgs(npc));
        ObjectiveTracker.CreditKill(player, npc);
    }

    private static void OnEnemyKilled(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player)
        {
            return;
        }

        ExpeditionRun run = GetRun(player);
        GameLiving target = (args as EnemyKilledEventArgs)?.Target;

        if (run != null)
        {
            ExpeditionDebug.Report(player, $"kill event for {target?.Name} (level {target?.Level}) reached the run; tracker attached: {ObjectiveTracker.IsAttached(player)}.");
        }

        // GameLiving.RemoveFromWorld also raises EnemyKilled on the attackers of a living that merely despawns.
        if (run == null || target == null || !(target.IsBeingHandledByReaperService || target.Health <= 0))
        {
            return;
        }

        // The same object is raised again when a respawned mob dies, so only a repeat within a few seconds is a duplicate.
        long now = GameLoop.GameLoopTime;

        lock (run.KillSync)
        {
            if (run.LastKillTarget == target && now - run.LastKillTick < 5000)
            {
                return;
            }

            run.LastKillTarget = target;
            run.LastKillTick = now;
        }

        Interlocked.Increment(ref run.Kills);
    }

    #endregion

    #region Contract

    /// <summary>
    /// expedition_xp_rate's factor at each player level (index = level - 1). Levelling takes more and more even-con
    /// kills (GamePlayer.XPForLevel against GameLiving.XPForLiving: about 25 at level 5, 75 at 20, 150 at 35, 410 at 40,
    /// 875 at 49), so the factor follows the kills per level from level 10 on, raised to the power 0.8 so later levels
    /// take somewhat longer (owner, 2026-10-24: "close, not the exact same"; then "slightly longer, not excruciating",
    /// with objectives giving experience to make up part of it). 1 through level 10, so act 1 stays at
    /// expedition_xp_rate. At the default rate of 5, even-con kills per level go from 10 at level 10 to about 18 at 49.
    /// </summary>
    private static readonly double[] LevelXpFactors =
    {
        1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0,
        1.04, 1.07, 1.08, 1.14, 1.19, 1.25, 1.27, 1.3, 1.35, 1.38,
        1.46, 1.69, 1.62, 1.7, 1.77, 1.84, 1.87, 1.95, 2.04, 2.11,
        2.14, 2.19, 2.27, 2.33, 2.42, 2.57, 2.71, 2.74, 2.86, 5.38,
        7.76, 7.99, 8.06, 8.47, 8.67, 9.05, 9.31, 9.68, 9.89
    };

    /// <summary>The experience multiplier on a run for a player of <paramref name="level"/>, on top of xp_rate.</summary>
    public static double XpMultiplier(int level, double rate)
    {
        int index = Math.Clamp(level, 1, LevelXpFactors.Length) - 1;

        return Math.Max(0, rate) * LevelXpFactors[index];
    }

    public const string RecallStoneTemplate = "Personal_Bind_Recall_Stone";

    /// <summary>
    /// Every run character carries a Personal Bind Recall Stone: on a run it takes them to the act camp with no reuse
    /// timer (GatewayPersonalBind, SpellHandler). Given at run start and at login when the backpack has none.
    /// </summary>
    private static void GiveRecallStone(GamePlayer player)
    {
        try
        {
            if (GetRun(player) == null || player.Inventory.CountItemTemplate(RecallStoneTemplate, eInventorySlot.Min_Inv, eInventorySlot.Max_Inv) > 0)
            {
                return;
            }

            if (player.ReceiveItem(null, RecallStoneTemplate))
            {
                player.Out.SendMessage("Use your Personal Bind Recall Stone to return to the act's camp at any time out of combat.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not give {player.Name} a recall stone.", ex);
        }
    }

    /// <summary>Experience for a finished objective, in percent of what the player's current level needs.</summary>
    public const int ObjectiveXpPercent = 20;

    /// <summary>Experience for an act boss, in percent of what the player's current level needs.</summary>
    public const int BossXpPercent = 50;

    /// <summary>
    /// Gives <paramref name="percent"/> of the experience the player's current level needs (not multiplied by any
    /// rate: it is already sized to the level). Nothing at the level cap.
    /// </summary>
    public static void GrantLevelShare(GamePlayer player, int percent, string reason)
    {
        if (player == null || percent <= 0 || player.Level >= GamePlayer.MAX_LEVEL)
        {
            return;
        }

        long amount = (player.ExperienceForNextLevel - player.ExperienceForCurrentLevel) * percent / 100;

        if (amount <= 0)
        {
            return;
        }

        player.Out.SendMessage($"{reason}: you gain {amount:N0} experience.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        player.GainExperience(eXPSource.Quest, amount, false);
    }

    public static ExpeditionRun GetRun(GamePlayer player)
    {
        if (player == null)
        {
            return null;
        }

        return Runs.TryGetValue(player.ObjectId, out ExpeditionRun run) && run.IsActive ? run : null;
    }

    public static bool TryStartRun(GamePlayer player, int? seed, out string error, bool hard = false)
    {
        if (hard && !ExpeditionHardMode.IsUnlocked(player))
        {
            error = "A hard expedition opens to an account that has beaten the Gauntlet.";

            return false;
        }

        error = null;

        if (player?.Client == null)
        {
            error = "Only a player in the world can start an expedition.";

            return false;
        }

        if (GetRun(player) != null)
        {
            error = "You are already on an expedition. Type /run status, or /run abandon to give it up.";

            return false;
        }

        if (player.Level != 1)
        {
            error = "You must be level 1 to start an expedition.";

            return false;
        }

        if (player.RealmPoints > 0)
        {
            error = "An expedition can only start on a character with no realm points.";

            return false;
        }

        // A hardcore character with a death on record is deleted at its next login (HardCoreLogin).
        if (player.DeathCount > 0)
        {
            error = "An expedition can only start on a character that has never died.";

            return false;
        }

        if (!player.IsAlive)
        {
            error = "You can't start an expedition while dead.";

            return false;
        }

        int runSeed = seed ?? Random.Shared.Next(1, int.MaxValue);
        ActPlan plan;

        try
        {
            plan = ActGenerator.Generate(1, player.Realm, runSeed, NoPreviousZone, player.Level);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Act 1 could not be generated for {player.Name} (seed {runSeed}).", ex);
            plan = null;
        }

        if (plan == null)
        {
            error = "The expedition could not be planned. Try another seed.";

            return false;
        }

        OfflineExpeditionRecord existing = null;

        try
        {
            existing = GameServer.Database.FindObjectByKey<OfflineExpeditionRecord>(player.ObjectId);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the earlier run of {player.Name}.", ex);
        }

        var run = new ExpeditionRun
        {
            CharacterId = player.ObjectId,
            CharacterName = player.Name,
            AccountName = player.Client.Account?.Name ?? string.Empty,
            Seed = runSeed,
            Act = 1,
            Status = StatusActive,
            CurrentAct = plan,
            StartedUtc = DateTime.UtcNow,
            Hard = hard,
            Record = existing
        };
        run.AddLog(hard ? $"Hard expedition begun by {player.Name} with seed {runSeed}." : $"Expedition begun by {player.Name} with seed {runSeed}.");
        run.AddLog($"Act 1: {plan.ZoneName} (levels {plan.MinLevel}-{plan.MaxLevel}).");

        player.Emote(eEmote.StagFrenzy);
        player.HCFlag = true;
        player.CurrentTitle = new HardCoreTitle();

        Runs[run.CharacterId] = run;
        Save(run);

        ExpeditionAccountRealm.GiveToNewRun(player, run);
        player.Out.SendMessage($"Your expedition begins! Seed {runSeed}. Your HARDCORE flag is ON: death ends the run and deletes this character.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

        // Fork (owner 2026-10-25): every teleport to the camp makes it safe first and gives an arrival grace.
        if (!ExpeditionArrival.MoveToCamp(player, run, plan))
        {
            Log.Warn($"[EXPEDITION] {player.Name} could not be moved to the act 1 camp in region {plan.RegionId} ({plan.CampX}, {plan.CampY}, {plan.CampZ}).");
            player.Out.SendMessage("You could not be taken to the camp. Type /run journal to see where it is.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        ObjectiveTracker.Attach(player);

        // The camp can be in another region (Hibernia's Shrouded Isles, for one): the client is on its loading screen
        // right after MoveTo, and a window or dialog sent then crashed it (owner's friend, 2026-10-23).
        WhenSettled(player, (ushort)plan.RegionId, () =>
        {
            // A hard run opens on its rules and the unique it may inherit (/run journal has the act).
            if (ExpeditionHardMode.IsHard(run))
            {
                ExpeditionHardMode.OnActArrival(player, run);
            }
            else
            {
                player.Out.SendCustomTextWindow("Expedition journal", JournalLines(run).ToList());
            }

            ExpeditionPromotion.OnRunStarted(player);
            GiveRecallStone(player);
        });

        return true;
    }

    /// <summary>How long a player must have stood in the world, after a teleport or login, before windows are sent.</summary>
    private const int SettleDelayMs = 3000;

    /// <summary>
    /// Runs <paramref name="action"/> once the player stands in <paramref name="regionId"/> again, playing, for
    /// <see cref="SettleDelayMs"/>: windows, dialogs and weather sent while the client is still loading a region can
    /// crash it. Gives up after a minute (the player left or never arrived).
    /// </summary>
    internal static void WhenSettled(GamePlayer player, ushort regionId, Action action)
    {
        if (player == null || action == null)
        {
            return;
        }

        long started = GameLoop.GameLoopTime;
        long settledSince = 0;

        new ECSGameTimer(player, _ =>
        {
            GameClient client = player.Client;

            if (client == null || player.ObjectState == GameObject.eObjectState.Deleted || client.ClientState is GameClient.eClientState.Disconnected or GameClient.eClientState.NotConnected)
            {
                return 0;
            }

            long now = GameLoop.GameLoopTime;
            bool inWorld = player.ObjectState == GameObject.eObjectState.Active && player.CurrentRegionID == regionId && client.ClientState == GameClient.eClientState.Playing;

            if (!inWorld)
            {
                settledSince = 0;

                return now - started > 60000 ? 0 : 500;
            }

            if (settledSince == 0)
            {
                settledSince = now;
            }

            if (now - settledSince < SettleDelayMs)
            {
                return 500;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error($"[EXPEDITION] A delayed step failed for {player.Name}.", ex);
            }

            return 0;
        }, 500);
    }

    public static void Save(ExpeditionRun run)
    {
        if (run == null || string.IsNullOrEmpty(run.CharacterId))
        {
            return;
        }

        try
        {
            OfflineExpeditionRecord record = run.Record ?? new OfflineExpeditionRecord { CharacterId = run.CharacterId };
            List<string> log;

            lock (run.Log)
            {
                log = run.Log.ToList();
            }

            record.CharacterName = run.CharacterName ?? string.Empty;
            record.AccountName = run.AccountName ?? string.Empty;
            record.SummaryShown = run.SummaryShown;
            record.Seed = run.Seed;
            record.Act = run.Act;
            record.Status = run.Status ?? StatusActive;
            record.Kills = run.Kills;
            record.Deaths = run.Deaths;
            record.BossesKilled = run.BossesKilled;
            record.LootCommon = run.LootByRarity[(int) eLootRarity.Common];
            record.LootMagic = run.LootByRarity[(int) eLootRarity.Magic];
            record.LootRare = run.LootByRarity[(int) eLootRarity.Rare];
            record.LootEpic = run.LootByRarity[(int) eLootRarity.Epic];
            record.LootUnique = run.LootByRarity[(int) eLootRarity.Unique];
            record.StartedUtc = run.StartedUtc.ToString("o", CultureInfo.InvariantCulture);
            record.EndedUtc = run.EndedUtc?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
            record.ActJson = run.CurrentAct == null ? string.Empty : JsonSerializer.Serialize(run.CurrentAct, ExpeditionJson.Options);
            record.LogJson = JsonSerializer.Serialize(log, ExpeditionJson.Options);
            record.BoonsJson = JsonSerializer.Serialize(run.Boons ?? new List<string>(), ExpeditionJson.Options);
            record.PendingBoonOfferJson = JsonSerializer.Serialize(run.PendingBoonOffer ?? new List<string>(), ExpeditionJson.Options);
            record.RealmPointsEarned = run.RealmPointsEarned;
            record.RerollsUsed = run.RerollsUsed;
            record.PlayedSecondsAtWin = run.PlayedSecondsAtWin;
            record.Hard = run.Hard;
            record.InheritedUnique = run.InheritedUnique ?? string.Empty;

            if (record.IsPersisted)
            {
                GameServer.Database.SaveObject(record);
            }
            else
            {
                GameServer.Database.AddObject(record);
            }

            run.Record = record;
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not save the run of {run.CharacterName}.", ex);
        }
    }

    public static void OnObjectivesComplete(GamePlayer player)
    {
        ExpeditionRun run = GetRun(player);

        if (run == null)
        {
            return;
        }

        BossSpec boss = run.CurrentAct?.Boss;
        string bossName = string.IsNullOrEmpty(boss?.Title) ? boss?.BaseMobName : boss.Title;

        // A hard run's act boss is the act's Gauntlet champion (ExpeditionHardBosses).
        if (run.Hard)
        {
            bossName = ExpeditionHardBosses.ChampionFor(run.Act).Name;
        }

        run.AddLog($"Act {run.Act}: every objective is done. {bossName ?? "The act boss"} has come.");
        Save(run);

        player.Out.SendMessage($"Every objective of act {run.Act} is done. {bossName ?? "The act boss"} has come: defeat it to go on.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    public static void OnBossKilled(GamePlayer player)
    {
        ExpeditionRun run = GetRun(player);

        if (run == null)
        {
            return;
        }

        ActPlan finished = run.CurrentAct;

        if (finished != null)
        {
            finished.BossKilled = true;
        }

        run.BossesKilled++;
        string slain = run.Hard ? ExpeditionHardBosses.ChampionFor(run.Act).Name : finished?.Boss?.Title;
        run.AddLog($"Act {run.Act}: {slain ?? "the act boss"} is slain.");
        ExpeditionRealm.OnBossKilled(player, run.Act, run.Act >= ActGenerator.ActCount);

        if (run.Act >= ActGenerator.ActCount)
        {
            run.Status = StatusWon;
            run.EndedUtc = DateTime.UtcNow;
            run.PlayedSecondsAtWin = player.PlayedTime;
            run.SummaryShown = true;
            run.AddLog($"{run.CharacterName} has won the expedition.");

            // As at level 50 (GamePlayer.cs:4627): the challenge is complete, the character is safe again.
            player.HCFlag = false;
            player.HCCompleted = true;

            // Saved now: a crash before the next save would leave the winner hardcore, and the Gauntlet refuses them.
            player.SaveIntoDatabase();

            Runs.TryRemove(run.CharacterId, out _);
            ObjectiveTracker.Detach(player);
            ExpeditionBoons.RemoveAll(player);
            Save(run);

            player.Out.SendMessage("You have won the expedition! Your Hardcore flag has been disabled.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            player.Out.SendCustomTextWindow("Expedition won", SummaryLines(run, "VICTORY"));

            // Fork (owner 2026-10-25): the winner is taken to the Gauntlet, the ultimate test.
            Gauntlet.GauntletManager.StartAfterExpedition(player);

            return;
        }

        int nextAct = run.Act + 1;
        ActPlan next;

        try
        {
            next = ActGenerator.Generate(nextAct, player.Realm, run.Seed, finished?.ZoneId ?? NoPreviousZone, player.Level);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Act {nextAct} could not be generated for {run.CharacterName} (seed {run.Seed}).", ex);
            next = null;
        }

        if (next == null)
        {
            // The run stays on the finished act, its boss marked slain, so nothing is lost; the error is logged.
            Save(run);
            player.Out.SendMessage($"Act {nextAct} could not be planned. Your run is saved; please report seed {run.Seed}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            return;
        }

        // A run started under the earlier boss rules may have met this name already (the log holds each slain boss).
        UpdateBossTitle(run, next);

        // The boon offer is drawn for the act just won (ExpeditionBoons.RollOffer) and waits for /run boon <n>.
        bool boonOffered = ExpeditionBoons.OfferAtActBreak(run, run.Act, out bool replacedUnclaimedBoon, ExpeditionBoons.UsesPower(player), ExpeditionBoons.HasPet(player),
            ExpeditionBoons.FightsInMelee(player), ExpeditionBoons.IsRanged(player), ExpeditionHardMode.IsHard(run));

        run.Act = nextAct;
        run.CurrentAct = next;
        run.AddLog($"Act {nextAct}: {next.ZoneName} (levels {next.MinLevel}-{next.MaxLevel}).");
        Save(run);

        player.Out.SendMessage($"Act {nextAct - 1} is won! In {NextActTeleportDelayMilliseconds / 1000} seconds you set out for act {nextAct}: {next.ZoneName}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

        if (replacedUnclaimedBoon)
        {
            player.Out.SendMessage("The boon you did not choose at the last act break is gone; a new one is offered.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        if (boonOffered)
        {
            ExpeditionBoons.RemindOffer(player, run);
        }

        // A short pause to loot the boss; then the camp. The tracker is re-attached after the move, so whatever it
        // set up for the finished act (its zone, its mists) is rebuilt for the new one.
        new ECSGameTimer(player, _ =>
        {
            ExpeditionRun current = GetRun(player);

            if (current == null || current.Act != nextAct || !player.IsAlive)
            {
                return 0;
            }

            ObjectiveTracker.Detach(player);

            if (!ExpeditionArrival.MoveToCamp(player, current, next))
            {
                Log.Warn($"[EXPEDITION] {player.Name} could not be moved to the act {nextAct} camp in region {next.RegionId} ({next.CampX}, {next.CampY}, {next.CampZ}).");
                player.Out.SendMessage("You could not be taken to the next camp. Type /run journal to see where it is.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            }

            ObjectiveTracker.Attach(player);
            ExpeditionBoons.ApplyAll(player, current);
            WhenSettled(player, (ushort)next.RegionId, () =>
            {
                // The boon choice is the act break's reward, so it is the window that opens (owner 2026-10-25: the
                // offer was only a chat line and the journal); the journal is a /run journal away.
                if (current.PendingBoonOffer.Count > 0)
                {
                    player.Out.SendCustomTextWindow("Expedition boons", ExpeditionBoons.OfferLines(current));
                }
                else
                {
                    player.Out.SendCustomTextWindow("Expedition journal", JournalLines(current).ToList());
                }

                if (ExpeditionHardMode.IsHard(current))
                {
                    ExpeditionHardMode.OnActArrival(player, current);
                }
            });

            return 0;
        }, NextActTeleportDelayMilliseconds);
    }

    /// <summary>
    /// The run is over. Called from the Dying event, before GamePlayer.ProcessDeath reaches the hardcore deletion,
    /// so the flag is left on: the character is deleted as the hardcore rules say.
    /// </summary>
    /// <summary>
    /// Brings the current act's boss in line with the boss rules (owner 2026-10-25: the boss update applies to runs
    /// already under way): a boss that has not appeared yet takes the name the run should have now, never one the run
    /// already slew. Returns true when it changed (the caller saves).
    /// </summary>
    public static bool UpdateBossTitle(ExpeditionRun run, ActPlan plan)
    {
        if (run == null || plan?.Boss == null || plan.BossSpawned || plan.BossKilled)
        {
            return false;
        }

        HashSet<string> faced = new(StringComparer.Ordinal);

        lock (run.Log)
        {
            foreach (string line in run.Log)
            {
                System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(line ?? string.Empty, @"^Act \d+: (.+) is slain\.$");

                if (match.Success)
                {
                    faced.Add(match.Groups[1].Value);
                }
            }
        }

        string title = ActGenerator.BossTitle(plan.Act, run.Seed, plan.ZoneName, faced);

        if (string.Equals(plan.Boss.Title, title, StringComparison.Ordinal))
        {
            return false;
        }

        plan.Boss.Title = title;

        return true;
    }

    public static void OnPlayerDied(GamePlayer player)
    {
        ExpeditionRun run = GetRun(player);

        if (run == null)
        {
            return;
        }

        run.Status = StatusDead;
        run.Deaths++;
        run.EndedUtc = DateTime.UtcNow;
        run.AddLog($"Act {run.Act}: {run.CharacterName} has fallen. The expedition is over.");

        Runs.TryRemove(run.CharacterId, out _);
        Save(run);

        player.Out.SendCustomTextWindow("Expedition over", SummaryLines(run, "RUN OVER"));
        ObjectiveTracker.Detach(player);
        ExpeditionBoons.RemoveAll(player);
    }

    public static void Abandon(GamePlayer player)
    {
        ExpeditionRun run = GetRun(player);

        if (run == null)
        {
            return;
        }

        run.Status = StatusAbandoned;
        run.EndedUtc = DateTime.UtcNow;
        run.SummaryShown = true;
        run.AddLog($"Act {run.Act}: the expedition was abandoned.");

        player.HCFlag = false;

        if (player.CurrentTitle is HardCoreTitle && !player.HCCompleted)
        {
            player.CurrentTitle = PlayerTitleMgr.ClearTitle;
        }

        Runs.TryRemove(run.CharacterId, out _);
        ObjectiveTracker.Detach(player);
        ExpeditionBoons.RemoveAll(player);
        Save(run);

        player.Out.SendMessage("You have abandoned the expedition. Your Hardcore flag is OFF.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        player.Out.SendCustomTextWindow("Expedition abandoned", SummaryLines(run, "ABANDONED"));
    }

    public static IReadOnlyList<string> JournalLines(ExpeditionRun run)
    {
        var lines = new List<string>();

        if (run == null)
        {
            return lines;
        }

        ActPlan plan = run.CurrentAct;

        // Sections with a heading each, and every /loc on its own indented line: the window is narrow, and long
        // lines wrapped into one block were hard to read (owner, 2026-10-24).
        if (plan == null)
        {
            lines.Add($"ACT {run.Act} OF {ActGenerator.ActCount}");
        }
        else
        {
            lines.Add($"ACT {plan.Act} OF {ActGenerator.ActCount}: {plan.ZoneName}");
            lines.Add($"Levels {plan.MinLevel}-{plan.MaxLevel}");
            AddLoc(lines, "Camp", plan.RegionId, plan.CampX, plan.CampY);

            AddObjectives(lines, plan);
            AddBoss(lines, plan, run.Hard);
            AddElites(lines, plan);
            AddShrines(lines, plan);
            AddServices(lines, plan);
        }

        AddRunSummary(lines, run);

        lines.Add(string.Empty);
        lines.Add("-- RECENT --");

        lock (run.Log)
        {
            foreach (string entry in run.Log.Skip(Math.Max(0, run.Log.Count - JournalLogLines)))
            {
                lines.Add($"- {entry}");
            }
        }

        return lines;
    }

    private const string Indent = "      ";

    private static void AddLoc(List<string> lines, string label, int regionId, int x, int y)
    {
        string loc = ActGenerator.DescribeLoc(regionId, x, y);

        if (loc != null)
        {
            lines.Add(label == null ? $"{Indent}{loc}" : $"{label}: {loc}");
        }
    }

    private static void AddObjectives(List<string> lines, ActPlan plan)
    {
        lines.Add(string.Empty);
        lines.Add("-- OBJECTIVES --");

        foreach (ActObjective objective in plan.Objectives ?? [])
        {
            string mark = objective.Done ? "[done]" : "[    ]";
            lines.Add($"{mark} {objective.Description} ({Math.Min(objective.Progress, objective.Count)}/{objective.Count})");

            if (!objective.Done)
            {
                AddLoc(lines, null, objective.RegionId, objective.X, objective.Y);
            }
        }
    }

    private static void AddBoss(List<string> lines, ActPlan plan, bool hard)
    {
        if (plan.Boss == null)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("-- BOSS --");

        if (hard)
        {
            // A hard run's act boss is the act's Gauntlet champion (ExpeditionHardBosses).
            Gauntlet.ChampionDefinition champion = ExpeditionHardBosses.ChampionFor(plan.Act);
            lines.Add($"{champion.Name}, Gauntlet champion");
            lines.Add($"{Indent}level {ExpeditionHardBosses.LevelFor(plan)} {(eCharacterClass) champion.ClassId}");
        }
        else
        {
            lines.Add($"{plan.Boss.Title}");
            // A named boss keeps the monster it is built on to itself (owner 2026-10-25: it looks like its own race and class).
            bool named = plan.Boss.Title != plan.Boss.BaseMobName && !plan.Boss.Title.StartsWith("Warden of ", StringComparison.Ordinal);
            lines.Add(named ? $"{Indent}level {plan.Boss.Level}" : $"{Indent}level {plan.Boss.Level} {plan.Boss.BaseMobName}");
        }

        string status = plan.BossKilled ? "slain" : plan.BossSpawned ? "has appeared" : "appears when every objective is done";
        lines.Add($"{Indent}{status}");

        if (!plan.BossKilled && plan.BossSpawned)
        {
            AddLoc(lines, null, plan.Boss.RegionId, plan.Boss.X, plan.Boss.Y);
        }
    }

    private static void AddElites(List<string> lines, ActPlan plan)
    {
        if (plan.Elites == null || plan.Elites.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("-- ELITE PACKS (optional) --");
        lines.Add(ExpeditionElites.CommonEffect);

        foreach (EliteCamp camp in plan.Elites)
        {
            if (camp == null)
            {
                continue;
            }

            lines.Add(ExpeditionElites.EliteName(camp.Affix, camp.MobName));
            string effect = ExpeditionElites.AffixEffect(camp.Affix);

            if (effect.Length > 0)
            {
                lines.Add($"{Indent}{effect}");
            }

            lines.Add($"{Indent}{ActGenerator.DescribeFromCamp(plan.CampX, plan.CampY, camp.X, camp.Y)}");
            AddLoc(lines, null, camp.RegionId, camp.X, camp.Y);
        }
    }

    private static void AddShrines(List<string> lines, ActPlan plan)
    {
        if (plan.Shrines == null || plan.Shrines.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("-- SHRINES --");
        lines.Add("(/run shrines: which way each lies from where you stand)");

        foreach (ShrineSpec shrine in plan.Shrines)
        {
            if (shrine == null)
            {
                continue;
            }

            string guarded = shrine.Guarded ? " (guarded by an elite pack)" : string.Empty;

            if (!shrine.Discovered)
            {
                lines.Add($"Undiscovered shrine{guarded}");
                lines.Add($"{Indent}something glimmers {shrine.Direction}");

                continue;
            }

            lines.Add($"{ExpeditionBuffs.ShrineName(shrine.Group)}{guarded}");
            lines.Add($"{Indent}{(shrine.Used ? "blessing received"
                : ExpeditionShrines.IsDormant(shrine) ? $"dormant: slay {shrine.Kills}/{shrine.KillsNeeded} {shrine.CampMob} {ExpeditionShrines.CampWhere(shrine)}"
                : "not yet used")}");
            AddLoc(lines, null, shrine.RegionId, shrine.X, shrine.Y);
        }
    }

    private static void AddServices(List<string> lines, ActPlan plan)
    {
        lines.Add(string.Empty);
        lines.Add("-- AT THE CAMP --");
        lines.Add("Hastener: beside the camp (speed)");
        lines.Add("Supplier: beside the camp (poisons, arrows, bolts, throwing weapons)");

        if (plan.ExchangerPlaced)
        {
            lines.Add($"Reforger: {(plan.ExchangerInTown ? "in the zone's town" : "beside the camp")}");
            lines.Add($"{Indent}reforges unique weapons");
            AddLoc(lines, null, plan.RegionId, plan.ExchangerX, plan.ExchangerY);
        }

        lines.Add("Recall stone: brings you back here");
    }

    private static void AddRunSummary(List<string> lines, ExpeditionRun run)
    {
        lines.Add(string.Empty);
        lines.Add("-- THIS RUN --");
        lines.Add($"Seed {run.Seed}");
        lines.Add($"Kills {run.Kills:N0}   Bosses {run.BossesKilled}");
        lines.Add($"Realm points {run.RealmPointsEarned:N0}");
        lines.Add(run.LootSummary);

        List<string> boons;
        bool offerWaiting;

        lock (run.Boons)
        {
            boons = run.Boons.Select(id => ExpeditionBoons.Find(id)?.Name ?? id).ToList();
            offerWaiting = run.PendingBoonOffer.Count > 0;
        }

        lines.Add(boons.Count == 0 ? "Boons: none yet" : $"Boons: {string.Join(", ", boons)}");

        if (offerWaiting)
        {
            lines.Add($"{Indent}a boon offer is waiting: /run boons");
        }
    }

    #endregion

    /// <summary>The end-of-run window (death, victory and abandon).</summary>
    private static List<string> SummaryLines(ExpeditionRun run, string headline)
    {
        TimeSpan length = (run.EndedUtc ?? DateTime.UtcNow) - run.StartedUtc;
        var lines = new List<string>
        {
            $"*** {headline} ***",
            string.Empty,
            $"{run.CharacterName}, seed {run.Seed}",
            $"Reached act {run.Act} of {ActGenerator.ActCount}" + (run.CurrentAct == null ? string.Empty : $" ({run.CurrentAct.ZoneName})"),
            $"Kills: {run.Kills}",
            $"Bosses slain: {run.BossesKilled}",
            run.LootSummary,
            $"Realm points earned: {run.RealmPointsEarned} (kept on your account: {ExpeditionAccountRealm.Total(run.AccountName)} in all)",
            $"Time: {(int)length.TotalHours}h {length.Minutes}m",
            string.Empty,
            "Journal:"
        };

        lock (run.Log)
        {
            lines.AddRange(run.Log.Skip(Math.Max(0, run.Log.Count - JournalLogLines)));
        }

        return lines;
    }

    private static ExpeditionRun FromRecord(OfflineExpeditionRecord record, GamePlayer player)
    {
        var run = new ExpeditionRun
        {
            CharacterId = record.CharacterId,
            CharacterName = record.CharacterName,
            AccountName = record.AccountName,
            SummaryShown = record.SummaryShown,
            Seed = record.Seed,
            Act = record.Act,
            Status = record.Status,
            Kills = record.Kills,
            Deaths = record.Deaths,
            BossesKilled = record.BossesKilled,
            RealmPointsEarned = record.RealmPointsEarned,
            RerollsUsed = record.RerollsUsed,
            PlayedSecondsAtWin = record.PlayedSecondsAtWin,
            Hard = record.Hard,
            InheritedUnique = string.IsNullOrEmpty(record.InheritedUnique) ? null : record.InheritedUnique,
            LootByRarity = new[] { record.LootCommon, record.LootMagic, record.LootRare, record.LootEpic, record.LootUnique },
            StartedUtc = DateTime.TryParse(record.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime started) ? started : DateTime.UtcNow,
            EndedUtc = DateTime.TryParse(record.EndedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime ended) ? ended : null,
            Record = record
        };

        try
        {
            if (!string.IsNullOrEmpty(record.LogJson))
            {
                run.Log = JsonSerializer.Deserialize<List<string>>(record.LogJson, ExpeditionJson.Options) ?? new List<string>();
            }

            if (!string.IsNullOrEmpty(record.BoonsJson))
            {
                run.Boons = JsonSerializer.Deserialize<List<string>>(record.BoonsJson, ExpeditionJson.Options) ?? new List<string>();
            }

            if (!string.IsNullOrEmpty(record.PendingBoonOfferJson))
            {
                run.PendingBoonOffer = JsonSerializer.Deserialize<List<string>>(record.PendingBoonOfferJson, ExpeditionJson.Options) ?? new List<string>();
            }

            if (!string.IsNullOrEmpty(record.ActJson))
            {
                run.CurrentAct = JsonSerializer.Deserialize<ActPlan>(record.ActJson, ExpeditionJson.Options);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] The saved act of {record.CharacterName} could not be read.", ex);
        }

        // No player: a finished run read for its summary only, which needs no plan.
        if (run.CurrentAct == null && player != null)
        {
            // Same seed and act give the same plan except for the previous-zone rule, which is not saved separately.
            try
            {
                run.CurrentAct = ActGenerator.Generate(run.Act, player.Realm, run.Seed, NoPreviousZone, player.Level);
                run.AddLog($"Act {run.Act}: the saved act could not be read and was planned again.");
            }
            catch (Exception ex)
            {
                Log.Error($"[EXPEDITION] Act {run.Act} of {record.CharacterName} could not be planned again.", ex);
            }
        }

        return run;
    }
}
