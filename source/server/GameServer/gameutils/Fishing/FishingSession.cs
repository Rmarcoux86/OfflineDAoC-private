using System;
using System.Numerics;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Fishing;

/// <summary>
/// Runs a player's fishing session: casting, waiting for a bite, reeling in and recasting.
/// The session lives on player.CraftTimer, so the existing "can't quit/stealth while crafting"
/// guards apply and moving, attacking or casting a spell interrupts it like salvaging does.
/// </summary>
public static class FishingManager
{
    private static readonly Logger Log = LoggerManager.Create(typeof(FishingManager));

    private const string SessionProperty = "FISHING_SESSION";
    private const int TickMs = 500;
    private const int ReelWindowMs = 4000;
    private const int RecastPauseMs = 2000;
    private const int GroundTargetMaxRange = 1500;
    private const int NavmeshSearchDepth = 3000;
    private static readonly int[] CastDistances = [150, 300, 450, 600];

    // Detour filter pair: [include flags, exclude flags].
    private static readonly EDtPolyFlags[] SwimFilters = [EDtPolyFlags.Swim, EDtPolyFlags.Disabled];

    private enum ePhase
    {
        Waiting,
        BitePending,
        Resting
    }

    private sealed class FishingSession
    {
        public readonly object Lock = new();
        public GamePlayer Player;
        public ECSGameTimer Timer;
        public bool ActiveMode;
        public ushort RegionId;
        public ePhase Phase;
        public long PhaseEndsAt;
        public long FlavorAt;
        public bool Ended;
    }

    public static bool IsFishing(GamePlayer player)
    {
        return GetLiveSession(player) != null;
    }

    public static bool HasFishingPole(GamePlayer player)
    {
        DbInventoryItem twoHand = player.Inventory.GetItem(eInventorySlot.TwoHandWeapon);
        DbInventoryItem rightHand = player.Inventory.GetItem(eInventorySlot.RightHandWeapon);

        if (IsPole(twoHand) || IsPole(rightHand))
        {
            return true;
        }

        foreach (DbInventoryItem item in player.Inventory.GetItemRange(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack))
        {
            if (IsPole(item))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPole(DbInventoryItem item)
    {
        return item != null && item.Template != null && item.Template.Id_nb == FishingCatalog.PoleTemplateId;
    }

    /// <summary>
    /// Starts fishing. Returns false (after telling the player why) when they can't fish here.
    /// </summary>
    public static bool TryStart(GamePlayer player, bool activeMode)
    {
        if (player == null)
        {
            return false;
        }

        if (!player.IsAlive)
        {
            player.Out.SendMessage("You can't fish while dead.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return false;
        }

        if (player.InCombat)
        {
            player.Out.SendMessage("You can't fish while in combat.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return false;
        }

        if (player.IsMoving || player.IsStrafing)
        {
            player.Out.SendMessage("You must stand still to fish.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return false;
        }

        if (player.IsSwimming)
        {
            player.Out.SendMessage("You can't fish while swimming. Find a spot on the shore.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return false;
        }

        if (IsFishing(player))
        {
            player.Out.SendMessage("You are already fishing. Type /fish stop to stop.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return false;
        }

        if (player.IsCrafting || (player.CraftTimer != null && player.CraftTimer.IsAlive))
        {
            player.Out.SendMessage("You must finish what you are working on before you can fish.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return false;
        }

        if (!HasFishingPole(player))
        {
            player.Out.SendMessage("You need a fishing pole to fish. Type /fish pole to get one.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return false;
        }

        if (!FacesOpenWater(player))
        {
            player.Out.SendMessage("You need to face open water to cast your line.", eChatType.CT_System, eChatLoc.CL_SystemWindow);

            return false;
        }

        player.Stealth(false);

        FishingSession session = new()
        {
            Player = player,
            ActiveMode = activeMode,
            RegionId = player.CurrentRegion.ID
        };

        session.Timer = new ECSGameTimer(player)
        {
            Callback = new ECSGameTimer.ECSTimerCallback(OnTick)
        };
        session.Timer.Properties.SetProperty(SessionProperty, session);

        player.TempProperties.SetProperty(SessionProperty, session);
        player.CraftTimer = session.Timer;

        string modeText = activeMode ? " Watch for a bite and type /fish to reel it in." : string.Empty;
        player.Out.SendMessage("You settle in and cast your line." + modeText, eChatType.CT_System, eChatLoc.CL_SystemWindow);

        lock (session.Lock)
        {
            Cast(session);
        }

        session.Timer.Start(TickMs);

        return true;
    }

    /// <summary>
    /// Handles a bare /fish while a session exists. Returns true if it reeled in an active-mode bite.
    /// </summary>
    public static bool TryReelIn(GamePlayer player)
    {
        FishingSession session = GetLiveSession(player);

        if (session == null)
        {
            return false;
        }

        lock (session.Lock)
        {
            if (session.Ended || session.Phase != ePhase.BitePending)
            {
                return false;
            }

            if (GameLoop.GameLoopTime > session.PhaseEndsAt)
            {
                return false;
            }

            Land(session);
        }

        return true;
    }

    public static void Stop(GamePlayer player, string reason)
    {
        if (player == null)
        {
            return;
        }

        FishingSession session = player.TempProperties.GetProperty<FishingSession>(SessionProperty);

        if (session == null)
        {
            return;
        }

        lock (session.Lock)
        {
            End(session, reason);
        }
    }

    // Returns the session only while it still owns the player's craft timer; anything else
    // (moving, attacking or casting stops CraftTimer from GamePlayer) leaves a stale session to discard.
    private static FishingSession GetLiveSession(GamePlayer player)
    {
        FishingSession session = player.TempProperties.GetProperty<FishingSession>(SessionProperty);

        if (session == null)
        {
            return null;
        }

        if (!session.Ended && session.Timer.IsAlive && player.CraftTimer == session.Timer)
        {
            return session;
        }

        lock (session.Lock)
        {
            End(session, null);
        }

        return null;
    }

    private static void End(FishingSession session, string reason)
    {
        if (session.Ended)
        {
            return;
        }

        session.Ended = true;
        GamePlayer player = session.Player;

        // Only touch the craft timer and its timer window while they are still ours: a salvage or repair that
        // replaced this session owns them now.
        if (player.CraftTimer == session.Timer)
        {
            player.CraftTimer = null;
            player.Out.SendCloseTimerWindow();
        }

        session.Timer.Stop();
        player.TempProperties.RemoveProperty(SessionProperty);
        FishingProgressStore.Save(FishingProgressStore.Load(player));

        if (!string.IsNullOrEmpty(reason))
        {
            player.Out.SendMessage(reason, eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
    }

    private static int OnTick(ECSGameTimer timer)
    {
        FishingSession session = timer.Properties.GetProperty<FishingSession>(SessionProperty);

        if (session == null)
        {
            return 0;
        }

        lock (session.Lock)
        {
            if (session.Ended)
            {
                return 0;
            }

            GamePlayer player = session.Player;

            if (player.CraftTimer != timer || !player.IsAlive || player.ObjectState != GameObject.eObjectState.Active || player.CurrentRegion == null || player.CurrentRegion.ID != session.RegionId)
            {
                End(session, null);

                return 0;
            }

            // Crafting (craftComponent) never checks CraftTimer, so a player can start it mid-session. Its progress
            // bar has replaced ours: release the craft timer first so End leaves that window open.
            if (player.IsCrafting)
            {
                player.CraftTimer = null;
                End(session, "You stop fishing.");

                return 0;
            }

            if (player.IsMoving || player.IsStrafing || player.InCombat || player.IsSwimming)
            {
                End(session, "You stop fishing.");

                return 0;
            }

            long now = GameLoop.GameLoopTime;

            switch (session.Phase)
            {
                case ePhase.Waiting:
                {
                    if (session.FlavorAt > 0 && now >= session.FlavorAt)
                    {
                        session.FlavorAt = 0;
                        string line = FishingCatalog.RollWaitingFlavorLine(Random.Shared);

                        if (!string.IsNullOrEmpty(line))
                        {
                            player.Out.SendMessage(line, eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        }
                    }

                    if (now < session.PhaseEndsAt)
                    {
                        break;
                    }

                    if (session.ActiveMode)
                    {
                        session.Phase = ePhase.BitePending;
                        session.PhaseEndsAt = now + ReelWindowMs;
                        player.Out.SendTimerWindow("Reel it in!", ReelWindowMs / 1000);
                        player.Out.SendMessage("Something bites! Type /fish to reel it in!", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }
                    else
                    {
                        Land(session);
                    }

                    break;
                }
                case ePhase.BitePending:
                {
                    if (now <= session.PhaseEndsAt)
                    {
                        break;
                    }

                    player.Out.SendCloseTimerWindow();
                    player.Out.SendMessage("The fish got away.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    session.Phase = ePhase.Resting;
                    session.PhaseEndsAt = now + RecastPauseMs;

                    break;
                }
                case ePhase.Resting:
                {
                    if (now >= session.PhaseEndsAt)
                    {
                        Cast(session);
                    }

                    break;
                }
            }

            return session.Ended ? 0 : TickMs;
        }
    }

    // Caller holds session.Lock.
    private static void Cast(FishingSession session)
    {
        GamePlayer player = session.Player;
        FishingProgress progress = FishingProgressStore.Load(player);
        progress.RecordCast();
        FishingProgressStore.Save(progress);

        int biteDelay = FishingCatalog.RollBiteDelayMs(progress.Skill, Random.Shared);
        long now = GameLoop.GameLoopTime;

        session.Phase = ePhase.Waiting;
        session.PhaseEndsAt = now + biteDelay;
        session.FlavorAt = now + biteDelay / 2;

        player.Out.SendTimerWindow("Fishing...", Math.Max(1, biteDelay / 1000));

        foreach (GamePlayer viewer in player.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
        {
            viewer.Out.SendEmoteAnimation(player, eEmote.Ponder);
        }
    }

    // Caller holds session.Lock. Rolls the catch, bags it and schedules the recast.
    private static void Land(FishingSession session)
    {
        GamePlayer player = session.Player;
        player.Out.SendCloseTimerWindow();

        FishingProgress progress = FishingProgressStore.Load(player);
        int skillBefore = progress.Skill;
        FishingCatch fish = FishingCatalog.Roll(player.Realm, skillBefore, Random.Shared);

        if (fish == null)
        {
            player.Out.SendMessage("Your line comes up empty.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            session.Phase = ePhase.Resting;
            session.PhaseEndsAt = GameLoop.GameLoopTime + RecastPauseMs;

            return;
        }

        DbItemTemplate template = GameServer.Database.FindObjectByKey<DbItemTemplate>(fish.TemplateId);

        if (template == null)
        {
            Log.Error($"Fishing: item template '{fish.TemplateId}' is missing from the database.");
            End(session, "Something went wrong with your catch. You stop fishing.");

            return;
        }

        string description = FishingCatalog.DescribeCatch(fish);

        if (fish.Rarity == eFishingRarity.Junk)
        {
            player.Out.SendMessage($"You reel in {description}. Not quite what you were hoping for.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }
        else
        {
            player.Out.SendMessage($"You reel in {description}!", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        DbInventoryItem item = GameInventoryItem.Create(template);
        bool bagged = player.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item);

        if (bagged)
        {
            InventoryLogging.LogInventoryAction("(fishing)", player, eInventoryActionType.Loot, item.Template, item.Count);
        }
        else
        {
            player.Out.SendMessage($"Your backpack is full, so you let the {fish.DisplayName} go.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        // RecordCatch counts the catch, keeps the best one and awards fish.SkillGain (capped at 100).
        progress.RecordCatch(fish);
        FishingProgressStore.Save(progress);

        if (progress.Skill > skillBefore)
        {
            player.Out.SendMessage($"Your fishing skill increases to {progress.Skill}.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        if (fish.Rarity == eFishingRarity.Legendary)
        {
            foreach (GamePlayer viewer in player.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
            {
                viewer.Out.SendEmoteAnimation(player, eEmote.Cheer);

                if (viewer != player)
                {
                    viewer.Out.SendMessage($"{player.Name} has landed a legendary {fish.DisplayName}!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
                }
            }
        }

        if (!bagged)
        {
            End(session, "You stop fishing until you make room in your backpack.");

            return;
        }

        session.Phase = ePhase.Resting;
        session.PhaseEndsAt = GameLoop.GameLoopTime + RecastPauseMs;
    }

    /// <summary>
    /// True if the player's ground target or a point 150-600 units ahead of them is water.
    /// </summary>
    private static bool FacesOpenWater(GamePlayer player)
    {
        Region region = player.CurrentRegion;

        if (region == null)
        {
            return false;
        }

        GroundTarget groundTarget = player.GroundTarget;

        if (groundTarget.IsValid && player.IsWithinRadius(groundTarget, GroundTargetMaxRange, true))
        {
            Zone targetZone = region.GetZone(groundTarget.X, groundTarget.Y);

            // A ground target clicked on the water sits at (or just under) the water surface.
            if (targetZone != null && targetZone.IsUnderwater(groundTarget.X, groundTarget.Y, groundTarget.Z - 16))
            {
                return true;
            }
        }

        IPathfindingMgr pathing = PathfindingProvider.Instance;

        foreach (int distance in CastDistances)
        {
            Point2D point = player.GetPointFromHeading(player.Heading, distance);
            Zone zone = region.GetZone(point.X, point.Y);

            if (zone == null)
            {
                continue;
            }

            // Zone.IsUnderwater compares z against the zone's water level, so test the ground under
            // the cast point, not the player's own height (a player on the shore is above the water).
            if (pathing.HasNavmesh(zone))
            {
                Vector3 probe = new(point.X, point.Y, player.Z + 200);
                Vector3 searchMin = new(-8, -8, -NavmeshSearchDepth);
                Vector3 searchMax = new(8, 8, 20);

                if (pathing.GetClosestPointInBounds(zone, probe, searchMin, searchMax, SwimFilters).HasValue)
                {
                    return true;
                }

                Vector3? floor = pathing.GetFloorBeneath(zone, probe, NavmeshSearchDepth, pathing.DefaultFilters);

                if (floor.HasValue && zone.IsUnderwater(point.X, point.Y, (int) floor.Value.Z))
                {
                    return true;
                }

                continue;
            }

            // No navmesh: only works when the player stands at about the waterline.
            if (zone.IsUnderwater(point.X, point.Y, player.Z - 64))
            {
                return true;
            }
        }

        return false;
    }
}
