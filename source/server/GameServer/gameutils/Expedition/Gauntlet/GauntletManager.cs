using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// The Gauntlet (docs/EXPEDITION-GAUNTLET-PLAN.md), Phase 1 prototype: a character whose expedition is won duels
/// champions in the arena, round after round. /gauntlet start teleports to the arena and begins the saved round; a
/// champion's death is a beaten round, a rest and the next round; a death keeps the character (no DeathCount, no
/// experience or constitution loss, GamePlayer's two hooks), removes the champion and releases the player at the arena
/// entrance with the round unchanged. Round and depth are saved in <see cref="OfflineExpeditionRecord.GauntletJson"/>.
/// The champions come in cycles of five, Preesh last (GauntletChampions.ForRound); <see cref="PrototypeIosOnly"/> makes every round Ios.
/// </summary>
public static class GauntletManager
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>
    /// Phase 1 fights only Ios (melee only, no pet). When false the round's champion is
    /// GauntletChampions.ForRound(seed, round).
    /// </summary>
    public static readonly bool PrototypeIosOnly = false;

    /// <summary>The countdown line during the rest is sent this often.</summary>
    private const int RestAnnounceMs = 30_000;

    /// <summary>Gauntlets of characters in the game, by character id.</summary>
    private static readonly ConcurrentDictionary<string, GauntletState> States = new(StringComparer.Ordinal);

    #region Server and player events

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(OnGameEntered));
        GameEventMgr.AddHandler(GamePlayerEvent.Quit, new DOLEventHandler(OnQuit));
        GameEventMgr.AddHandler(GamePlayerEvent.Linkdeath, new DOLEventHandler(OnQuit));
        GameEventMgr.AddHandler(GamePlayerEvent.RegionChanged, new DOLEventHandler(OnRegionChanged));
        GameEventMgr.AddHandler(GameLivingEvent.Dying, new DOLEventHandler(OnDying));
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(OnGameEntered));
        GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, new DOLEventHandler(OnQuit));
        GameEventMgr.RemoveHandler(GamePlayerEvent.Linkdeath, new DOLEventHandler(OnQuit));
        GameEventMgr.RemoveHandler(GamePlayerEvent.RegionChanged, new DOLEventHandler(OnRegionChanged));
        GameEventMgr.RemoveHandler(GameLivingEvent.Dying, new DOLEventHandler(OnDying));

        foreach (GauntletState state in States.Values)
        {
            lock (state)
            {
                RemoveChampion(state);
                StopRest(state);
                SaveState(state);
            }
        }

        States.Clear();
    }

    /// <summary>A character who logs in inside the arena is put at its entrance; /gauntlet start resumes the round.</summary>
    private static void OnGameEntered(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player || !GauntletArena.IsInside(player))
        {
            return;
        }

        ExpeditionManager.WhenSettled(player, GauntletArena.RegionId, () =>
        {
            if (!GauntletArena.IsInside(player) || States.ContainsKey(player.ObjectId))
            {
                return;
            }

            player.MoveTo(GauntletArena.RegionId, GauntletArena.EntranceX, GauntletArena.EntranceY, GauntletArena.EntranceZ, GauntletArena.EntranceHeading);
            player.Out.SendMessage("You stand at the entrance of the Gauntlet. Type /gauntlet start to resume your round.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        });
    }

    /// <summary>Quit and linkdeath: the champion goes, the round is kept for the next /gauntlet start.</summary>
    private static void OnQuit(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GamePlayer player)
        {
            Stop(player, true);
        }
    }

    /// <summary>Applies the won run's boons for the session (BoonEffects.ApplyForGauntlet). A GM's test entry has none.</summary>
    private static void ApplyRunBoons(GamePlayer player, OfflineExpeditionRecord record)
    {
        if (string.IsNullOrEmpty(record?.BoonsJson))
        {
            return;
        }

        try
        {
            List<string> boons = JsonSerializer.Deserialize<List<string>>(record.BoonsJson, ExpeditionJson.Options);
            BoonEffects.ApplyForGauntlet(player, boons);

            if (boons?.Count > 0)
            {
                player.Out.SendMessage("Your expedition's boons are with you in the Gauntlet (all but Phoenix Feather and the loot boons).", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
        }
        catch (Exception ex)
        {
            if (Log.IsErrorEnabled)
            {
                Log.Error($"[GAUNTLET] Could not apply the boons of {player.Name}.", ex);
            }
        }
    }

    /// <summary>A player who changes region away from the arena leaves the Gauntlet.</summary>
    private static void OnRegionChanged(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player || !States.ContainsKey(player.ObjectId))
        {
            return;
        }

        if (player.CurrentRegionID != GauntletArena.RegionId)
        {
            Stop(player);
        }
    }

    private static void OnDying(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GamePlayer player && IsGauntletDeath(player))
        {
            OnPlayerDied(player);
        }
    }

    #endregion

    #region Death hooks (GamePlayer)

    /// <summary>
    /// True when this player's death is a Gauntlet death: they are in the Gauntlet and anywhere in the arena's region (the
    /// same test as the release point, so a death just past the arena radius is still a Gauntlet death). GamePlayer.
    /// ProcessDeath then skips the experience, DeathCount and constitution penalties.
    /// </summary>
    public static bool IsGauntletDeath(GamePlayer player)
    {
        return player != null && GauntletArena.IsConfigured && States.ContainsKey(player.ObjectId) && player.CurrentRegionID == GauntletArena.RegionId;
    }

    /// <summary>
    /// The release point after a Gauntlet death: the arena entrance, in the arena's own region (a cross-region release
    /// skips GamePlayer's Revive and Released notifications). False when GamePlayer.Release should use its own point.
    /// </summary>
    public static bool TryGetReleasePoint(GamePlayer player, out ushort regionId, out int x, out int y, out int z, out ushort heading)
    {
        regionId = 0;
        x = 0;
        y = 0;
        z = 0;
        heading = 0;

        if (player == null || !GauntletArena.IsConfigured || !States.ContainsKey(player.ObjectId) || player.CurrentRegionID != GauntletArena.RegionId)
        {
            return false;
        }

        regionId = GauntletArena.RegionId;
        x = GauntletArena.EntranceX;
        y = GauntletArena.EntranceY;
        z = GauntletArena.EntranceZ;
        heading = GauntletArena.EntranceHeading;

        return true;
    }

    #endregion

    #region Start, rounds, death, leave

    public static GauntletState GetState(GamePlayer player)
    {
        if (player == null)
        {
            return null;
        }

        return States.TryGetValue(player.ObjectId, out GauntletState state) ? state : null;
    }

    /// <summary>
    /// /gauntlet start: for a character whose expedition is won, alive, with the arena free. Teleports to the arena
    /// centre and begins the saved round once the player has settled there.
    /// </summary>
    public static bool TryStart(GamePlayer player, out string error)
    {
        error = null;

        if (player?.Client == null)
        {
            error = "Only a player in the world can enter the Gauntlet.";

            return false;
        }

        if (!GauntletArena.IsConfigured)
        {
            error = GauntletArena.NotSetUpMessage;

            return false;
        }

        if (!player.IsAlive)
        {
            error = "You must be alive to enter the Gauntlet.";

            return false;
        }

        if (player.HCFlag)
        {
            error = "The Gauntlet is open only to a character that has won its expedition (your Hardcore flag is on).";

            return false;
        }

        GauntletState existing = GetState(player);

        if (existing != null)
        {
            lock (existing)
            {
                if (existing.ChampionOut)
                {
                    error = $"Round {existing.Round} is already under way.";

                    return false;
                }

                if (existing.IsResting)
                {
                    long seconds = Math.Max(1, (existing.RestEndsAt - GameLoop.GameLoopTime + 999) / 1000);
                    error = $"Round {existing.Round} begins in {seconds} seconds.";

                    return false;
                }
            }
        }

        OfflineExpeditionRecord record;

        try
        {
            record = GameServer.Database.FindObjectByKey<OfflineExpeditionRecord>(player.ObjectId);
        }
        catch (Exception ex)
        {
            if (Log.IsErrorEnabled)
            {
                Log.Error($"[GAUNTLET] Could not load the run of {player.Name}.", ex);
            }

            error = "Your expedition could not be read. Please try again.";

            return false;
        }

        // A won run has left ExpeditionManager's active runs (GetRun is null for it), so "won" is read from the row.
        // A GM may enter without one for testing (owner 2026-10-25): no row is used, so nothing is saved.
        bool won = record != null && record.Status == ExpeditionManager.StatusWon;

        if (!won && (GauntletRules.TestEntryForAnyCharacter || player.Client?.Account?.PrivLevel > (uint) ePrivLevel.Player))
        {
            record = null;
            player.Out.SendMessage("Test entry: you have not won an expedition, so this Gauntlet is not saved.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
        else if (!won)
        {
            error = "Only a character that has won its expedition can enter the Gauntlet.";

            return false;
        }

        if (!GauntletArena.TryClaim(player))
        {
            error = "Someone else is in the Gauntlet. Try again later.";

            return false;
        }

        GauntletState state = existing;

        if (state == null)
        {
            GauntletSave save = GauntletSave.Parse(record?.GauntletJson);
            state = States.GetOrAdd(player.ObjectId, _ => new GauntletState
            {
                CharacterId = player.ObjectId,
                Round = save.Round,
                Depth = save.Depth
            });
        }

        lock (state)
        {
            state.Record = record;
            state.Seed = record?.Seed ?? player.ObjectId.GetHashCode();
        }

        // Fork (owner 2026-10-27): the won run's boons carry into the Gauntlet (less Phoenix Feather and the loot boons).
        ApplyRunBoons(player, record);

        if (!GauntletArena.IsInside(player) &&
            !player.MoveTo(GauntletArena.RegionId, GauntletArena.PlayerX, GauntletArena.PlayerY, GauntletArena.PlayerZ, GauntletArena.PlayerHeading))
        {
            GauntletArena.Release(player);
            error = "The Gauntlet could not be reached. Please try again.";

            return false;
        }

        // Players' Gauntlet champions watch from the edge of the arena while the session lasts (GauntletSpectators).
        GauntletSpectators.Show(player);
        player.Out.SendMessage($"You enter the Gauntlet. Round {state.Round} begins shortly: the champion prepares and waits. Target it and type /bow to begin.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

        // Nothing is spawned or shown until the client has loaded the arena (ExpeditionManager.WhenSettled).
        ExpeditionManager.WhenSettled(player, GauntletArena.RegionId, () => BeginRound(player));

        return true;
    }

    /// <summary>
    /// Begins the player's current round: announces it and spawns the champion at the gate; the champion buffs there for
    /// <see cref="GauntletRules.ChampionBuffSeconds"/> and waits there until the player targets it and
    /// bows (<see cref="OnBow"/>) or attacks it (GauntletChampionBrain).
    /// </summary>
    public static bool BeginRound(GamePlayer player)
    {
        GauntletState state = GetState(player);

        if (state == null)
        {
            return false;
        }

        lock (state)
        {
            if (state.ChampionOut)
            {
                return false;
            }

            if (!player.IsAlive || !GauntletArena.IsInside(player))
            {
                player.Out.SendMessage($"Round {state.Round} waits for you. Type /gauntlet start in the arena to begin it.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return false;
            }

            ChampionDefinition definition = PrototypeIosOnly ? GauntletChampions.Get("Ios") : GauntletChampions.ForRound(state.Seed, state.Round);
            RoundTier tier = GauntletEscalation.For(state.Round, definition?.Name);
            GauntletChampion champion;

            try
            {
                champion = new GauntletChampion(player, definition, state.Round, tier);
            }
            catch (Exception ex)
            {
                if (Log.IsErrorEnabled)
                {
                    Log.Error($"[GAUNTLET] The round {state.Round} champion could not be made for {player.Name}.", ex);
                }

                player.Out.SendMessage("The champion could not be summoned. Please report this.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return false;
            }

            champion.X = GauntletArena.GateX;
            champion.Y = GauntletArena.GateY;
            champion.Z = GauntletArena.GateZ;
            champion.Heading = GauntletArena.GateHeading;
            champion.CurrentRegionID = GauntletArena.RegionId;
            champion.BuffUntilTick = GameLoop.GameLoopTime + GauntletRules.ChampionBuffSeconds * 1000L;
            champion.PrefightDeadlineTick = GameLoop.GameLoopTime + GauntletRules.ChampionBuffMaxSeconds * 1000L;

            if (!champion.AddToWorld())
            {
                champion.Remove();
                player.Out.SendMessage("The champion could not enter the arena. Please report this.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                return false;
            }

            // AddToWorld made the gate its spawn point: it waits there, returns there when leashed, and the leash is
            // measured from there (owner 2026-10-25: the champion stays where it appears).
            state.Champion = champion;

            List<string> lines = state.Round <= 1
                ? new List<string>
                {
                    "Congratulations, champion of the expedition!",
                    string.Empty,
                    "Few ever get this far. But your journey is not over: now you face your ultimate test.",
                    string.Empty,
                    $"The {GauntletRules.GuildName} guild.",
                    string.Empty,
                    $"Its {GauntletRules.Rounds} members will face you one at a time, each of a higher realm rank than the last. Defeat them all to finish the Gauntlet.",
                    string.Empty,
                    "How it works:",
                    "- Each challenger prepares its buffs where it stands and waits for you. Prepare yourself too.",
                    $"- When you are ready, target {champion.Name} and type /bow (or /duel challenge) to begin the fight.",
                    "- Attacking it also starts the fight.",
                    "- Win, and the next challenger steps forward after a short rest.",
                    "- If you fall, you return here with no death penalty; type /gauntlet start to face the same challenger again.",
                    "- /gauntlet status shows where you are; /gauntlet leave takes you to your bind point."
                }
                : new List<string>
                {
                    $"Challenger {state.Round} of {GauntletRules.Rounds}",
                    string.Empty,
                    $"{champion.Name} of {GauntletRules.GuildName} steps forward (realm rank {tier.RealmRank}).",
                    string.Empty,
                    $"Target {champion.Name} and type /bow (or /duel challenge) when you are ready."
                };

            player.Out.SendMessage($"The Gauntlet, round {state.Round}: {champion.Name} steps into the arena!", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            player.Out.SendCustomTextWindow("The Gauntlet", lines);

            return true;
        }
    }

    /// <summary>
    /// The champion has finished its pre-fight buffs (owner 2026-10-27: all of them, Gwyraan's pet included, and at least
    /// <see cref="GauntletRules.ChampionBuffSeconds"/>) and waits to be challenged (GauntletChampionBrain); /bow is
    /// refused until then (<see cref="OnBow"/>).
    /// </summary>
    public static void OnChampionReady(GauntletChampion champion)
    {
        GamePlayer player = champion?.Challenger;

        if (player == null)
        {
            return;
        }

        player.Out.SendMessage($"{champion.Name} awaits your challenge. Target {champion.Name} and type /bow when you are ready.", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        player.Out.SendMessage($"{champion.Name} awaits your challenge. Target {champion.Name} and type /bow when you are ready.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    /// <summary>
    /// A /bow (EmoteCommandHandler): bowing to your own waiting champion starts the duel; it bows back. Any other bow
    /// does nothing here.
    /// </summary>
    public static void OnBow(GamePlayer player, GameObject target)
    {
        if (player == null || target is not GauntletChampion champion || champion.Challenger != player || champion.Engaged || !champion.IsAlive)
        {
            return;
        }

        if (!champion.AtCentre)
        {
            player.Out.SendMessage($"{champion.Name} is still preparing. Wait a moment, then bow again.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return;
        }

        champion.Engaged = true;

        foreach (GamePlayer watcher in champion.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
        {
            watcher.Out.SendEmoteAnimation(champion, eEmote.Bow);
        }

        player.Out.SendMessage($"{champion.Name} bows in return. Fight!", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        player.Out.SendMessage($"{champion.Name} bows in return. Fight!", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    /// <summary>
    /// The champion died (GauntletChampion.ProcessDeath, only on a real death): the round is beaten, one Rare-or-better
    /// item is given, the round number goes up and the rest begins.
    /// </summary>
    public static void OnChampionDied(GauntletChampion champion)
    {
        if (champion == null || champion.Health > 0)
        {
            return;
        }

        foreach (GauntletState state in States.Values)
        {
            GamePlayer player;
            int beaten;

            lock (state)
            {
                if (state.Champion != champion)
                {
                    continue;
                }

                state.Champion = null;
                beaten = state.Round;
                state.Depth = Math.Max(state.Depth, beaten);
                state.Round = beaten + 1;
                SaveState(state);
                player = champion.Challenger;
            }

            if (player == null || player.ObjectState != GameObject.eObjectState.Active)
            {
                return;
            }

            player.Out.SendMessage($"Round {beaten} is yours! {champion.Name} is defeated.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
            GauntletSpectators.Cheer();

            if (beaten >= GauntletRules.Rounds)
            {
                Complete(player, state, champion);

                return;
            }

            ExpeditionLoot.DropFor(player, champion, eLootRarity.Rare);
            Refresh(player);
            StartRest(player, state);

            return;
        }
    }

    /// <summary>
    /// The last champion fell: the Gauntlet is won. A unique drops, the box congratulates the player, the next Gauntlet
    /// would start from the first challenger again, and the player is taken to their bind point shortly.
    /// </summary>
    private static void Complete(GamePlayer player, GauntletState state, GauntletChampion champion)
    {
        lock (state)
        {
            state.Round = 1;
            SaveState(state);
        }

        ExpeditionLoot.DropFor(player, champion, eLootRarity.Unique);
        player.Out.SendMessage($"The {GauntletRules.GuildName} guild is defeated! You have passed the ultimate test.", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        player.Out.SendCustomTextWindow("The Gauntlet", new List<string>
        {
            $"You have defeated the {GauntletRules.GuildName} guild!",
            string.Empty,
            $"All {GauntletRules.Rounds} of its members have fallen before you. You have passed the ultimate test.",
            string.Empty,
            $"You are taken to your bind point in {GauntletRules.ReturnAfterVictorySeconds} seconds."
        });

        // Waits (up to a few minutes) while the player is dead or loading, so a victory traded with a death still leaves.
        int tries = 0;

        new ECSGameTimer(player, _ =>
        {
            if (GetState(player) != state)
            {
                return 0;
            }

            if (player.IsAlive && player.ObjectState == GameObject.eObjectState.Active)
            {
                TryLeave(player, out string leaveError);

                return 0;
            }

            return ++tries < RetryTries ? RetryMs : 0;
        }, GauntletRules.ReturnAfterVictorySeconds * 1000);
    }

    /// <summary>
    /// The expedition was just won (ExpeditionManager). Owner 2026-10-27: the winner is not taken to the Gauntlet; they
    /// are told how to go when they are ready (/run gauntlet or /gauntlet start), so they can finish what they are doing.
    /// </summary>
    private const int RetryMs = 5000;
    private const int RetryTries = 36;

    public static void StartAfterExpedition(GamePlayer player)
    {
        if (player == null)
        {
            return;
        }

        player.Out.SendMessage("Your journey is not over: the SmuG guild awaits. Type /run gauntlet when you are ready.", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
        player.Out.SendMessage($"Your journey is not over. Your ultimate test awaits: the {GauntletRules.GuildName} guild. When you are ready, type /run gauntlet (or /gauntlet start) to be taken there.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    /// <summary>
    /// A Gauntlet death: the champion is removed, the run and the character are untouched, the round stays the same,
    /// and the player is released at the arena entrance after <see cref="GauntletRules.AutoReleaseSeconds"/>.
    /// </summary>
    public static void OnPlayerDied(GamePlayer player)
    {
        GauntletState state = GetState(player);

        if (state == null)
        {
            return;
        }

        // The one switch for permadeath (owner decision: off). When on, the Gauntlet session ends with the death and the
        // normal death rules apply, as the state is gone before GamePlayer.ProcessDeath asks IsGauntletDeath. What a
        // Gauntlet death should do to the character then is open question 3 in the plan.
        if (GauntletRules.DeathEndsRun)
        {
            Stop(player);
            player.Out.SendMessage($"You have fallen in round {state.Round}. Your Gauntlet is over.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            return;
        }

        lock (state)
        {
            RemoveChampion(state);
            StopRest(state);
            SaveState(state);
        }

        player.Out.SendMessage($"You have fallen in round {state.Round}. Your character and your run are safe; type /gauntlet start to try the round again.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

        new ECSGameTimer(player, _ =>
        {
            if (!player.IsAlive && player.ObjectState == GameObject.eObjectState.Active && player.Client?.IsPlaying == true)
            {
                player.Release(eReleaseType.Normal, true);
            }

            return 0;
        }, GauntletRules.AutoReleaseSeconds * 1000);
    }

    /// <summary>
    /// /gauntlet leave: removes the champion and returns the player to their bind point. The round and depth are kept.
    /// </summary>
    public static bool TryLeave(GamePlayer player, out string error)
    {
        error = null;

        if (player?.Client == null)
        {
            return false;
        }

        GauntletState state = GetState(player);

        if (state == null && !GauntletArena.IsInside(player))
        {
            error = "You are not in the Gauntlet.";

            return false;
        }

        if (!player.IsAlive)
        {
            error = "Release first; you leave the Gauntlet from its entrance.";

            return false;
        }

        // Fork (owner 2026-10-27): the player goes to their bind point. The won run's act 5 camp in Darkness Falls had no
        // services left and the recall stone does not work there once the run is over.
        Stop(player);
        player.MoveToBind();
        player.Out.SendMessage("You leave the Gauntlet and return to your bind point.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

        return true;
    }

    /// <summary>
    /// Ends the player's Gauntlet session (quit, region change, leave): the champion goes, the round is saved, and the run's
    /// boons come off. <paramref name="quitting"/>: the client is leaving, so no stat updates are sent to it.
    /// </summary>
    public static void Stop(GamePlayer player, bool quitting = false)
    {
        if (player == null)
        {
            return;
        }

        if (States.TryRemove(player.ObjectId, out GauntletState state))
        {
            lock (state)
            {
                RemoveChampion(state);
                StopRest(state);
                SaveState(state);
            }

            // A won run is no longer active, so its boons are only on the player for this session.
            if (ExpeditionManager.GetRun(player) == null)
            {
                BoonEffects.RemoveAll(player, !quitting);
            }
        }

        GauntletArena.Release(player);

        // Any player's quit comes here; the spectators go only when the arena is free.
        if (GauntletArena.Occupant == null)
        {
            GauntletSpectators.Hide();
        }
    }

    /// <summary>The /gauntlet status lines.</summary>
    public static List<string> StatusLines(GamePlayer player)
    {
        List<string> lines = new();
        GauntletState state = GetState(player);

        if (state != null)
        {
            lock (state)
            {
                lines.Add($"Round: {state.Round}");
                lines.Add($"Depth (rounds beaten): {state.Depth}");

                if (state.ChampionOut)
                {
                    lines.Add($"Fighting: {state.Champion.Name}");
                }
                else if (state.IsResting)
                {
                    lines.Add($"Resting: round {state.Round} begins in {Math.Max(1, (state.RestEndsAt - GameLoop.GameLoopTime + 999) / 1000)} seconds");
                }
                else
                {
                    lines.Add("Waiting: type /gauntlet start to begin the round.");
                }
            }

            return lines;
        }

        OfflineExpeditionRecord record = null;

        try
        {
            record = GameServer.Database.FindObjectByKey<OfflineExpeditionRecord>(player.ObjectId);
        }
        catch (Exception ex)
        {
            if (Log.IsErrorEnabled)
            {
                Log.Error($"[GAUNTLET] Could not load the run of {player.Name}.", ex);
            }
        }

        if (record == null || record.Status != ExpeditionManager.StatusWon)
        {
            lines.Add("The Gauntlet opens to a character that has won its expedition.");

            return lines;
        }

        GauntletSave save = GauntletSave.Parse(record.GauntletJson);
        lines.Add($"Round: {save.Round}");
        lines.Add($"Depth (rounds beaten): {save.Depth}");
        lines.Add(GauntletArena.IsConfigured ? "Type /gauntlet start to enter." : GauntletArena.NotSetUpMessage);

        return lines;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Between fights (owner 2026-10-26): the player is fully healed (health, power, endurance), every harmful effect left
    /// on them (DoTs, debuffs, snares) ends, and every ability and spell timer is reset, so each challenger meets them fresh.
    /// </summary>
    private static void Refresh(GamePlayer player)
    {
        if (player == null || !player.IsAlive)
        {
            return;
        }

        foreach (ECSGameSpellEffect effect in player.effectListComponent.GetSpellEffects())
        {
            if (!effect.HasPositiveEffect)
            {
                effect.End();
            }
        }

        player.Health = player.MaxHealth;
        player.Mana = player.MaxMana;
        player.Endurance = player.MaxEndurance;
        player.ResetDisabledSkills();
        player.Out.SendMessage("You are restored: health, power and endurance full, and every timer reset.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    private static void StartRest(GamePlayer player, GauntletState state)
    {
        lock (state)
        {
            StopRest(state);
            state.RestEndsAt = GameLoop.GameLoopTime + GauntletRules.RestSeconds * 1000L;
            state.RestTimer = new ECSGameTimer(player, _ => RestTick(player, state), Math.Min(RestAnnounceMs, GauntletRules.RestSeconds * 1000));
        }

        player.Out.SendMessage($"Rest. Round {state.Round} begins in {GauntletRules.RestSeconds} seconds.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    private static int RestTick(GamePlayer player, GauntletState state)
    {
        if (GetState(player) != state || player.ObjectState != GameObject.eObjectState.Active)
        {
            return 0;
        }

        long remaining;

        lock (state)
        {
            remaining = state.RestEndsAt - GameLoop.GameLoopTime;

            if (remaining <= 500)
            {
                state.RestTimer = null;
            }
        }

        if (remaining <= 500)
        {
            BeginRound(player);

            return 0;
        }

        player.Out.SendMessage($"Round {state.Round} begins in {(remaining + 500) / 1000} seconds.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

        return (int) Math.Min(RestAnnounceMs, remaining);
    }

    /// <summary>Removes the round's champion without counting a kill. Call under the state's lock.</summary>
    private static void RemoveChampion(GauntletState state)
    {
        GauntletChampion champion = state.Champion;
        state.Champion = null;
        champion?.Remove();
    }

    /// <summary>Call under the state's lock.</summary>
    private static void StopRest(GauntletState state)
    {
        state.RestTimer?.Stop();
        state.RestTimer = null;
    }

    /// <summary>Writes round and depth to the won run's row. Call under the state's lock.</summary>
    private static void SaveState(GauntletState state)
    {
        OfflineExpeditionRecord record = state.Record;

        if (record == null)
        {
            return;
        }

        try
        {
            record.GauntletJson = state.ToSave().ToJson();
            GameServer.Database.SaveObject(record);
        }
        catch (Exception ex)
        {
            if (Log.IsErrorEnabled)
            {
                Log.Error($"[GAUNTLET] Could not save the Gauntlet of {record.CharacterName}.", ex);
            }
        }
    }

    #endregion
}
