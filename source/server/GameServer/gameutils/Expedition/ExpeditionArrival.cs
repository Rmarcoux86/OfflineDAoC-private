using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Safe arrival at the act camp (owner 2026-10-25: a Midgard run was teleported into Cursed Tomb's aggressive monsters
/// and the hardcore character died on arrival). ActGenerator puts a computed camp on a monster camp's spawn point, which
/// is harmless outdoors when the lowest monsters are not aggressive but deadly in a dungeon. Before every teleport to
/// the camp, <see cref="EnsureSafeCamp(ActPlan, eRealm)"/> checks the camp once per plan (<see cref="CampSafetyRules"/>,
/// saved in <see cref="ActPlan.CampSafetyVersion"/>), so acts already planned and saved are fixed too:
/// - in a dungeon region the camp is the dungeon's real entrance (the zone point leading into it, the player's realm's
///   own one first, as Darkness Falls has one per realm);
/// - anywhere, the spot must be clear of aggressive monsters: no live one, and no spawn point of one (a camp cleared and
///   left respawns), within its aggro range plus <see cref="SafetyMargin"/>. Otherwise the nearest clear point on rings
///   around it, on the navigation mesh and in the act's zone, is used. When none is clear, a dungeon keeps its entrance
///   and an outdoor camp is left as it was, with a warning in the server log.
/// A teleport to the camp also gives <see cref="ArrivalGraceMs"/> of the server's invulnerability, so nothing aggroes the
/// player while the zone loads.
/// </summary>
public static class ExpeditionArrival
{
    /// <summary>The camp-safety rules version; raise it when the rules change so every plan is checked again.</summary>
    public const int CampSafetyRules = 1;

    /// <summary>A safe spot keeps this far beyond every aggressive monster's aggro range (and roaming range).</summary>
    public const int SafetyMargin = 500;

    /// <summary>Roaming monsters count with at most this much of their roaming range.</summary>
    public const int RoamingAllowance = 1000;

    /// <summary>The farthest ring the search tries around the camp (or the dungeon entrance).</summary>
    public const int MaxSearchRadius = 4000;

    public const int ArrivalGraceMs = 10_000;

    /// <summary>The rings the search tries, nearest first.</summary>
    private static readonly int[] RingRadii = [400, 800, 1200, 1600, 2000, 2600, 3200, MaxSearchRadius];

    /// <summary>Monsters and spawns are gathered this far from the search origin: every ring plus the widest hazard.</summary>
    private const int ScanRadius = MaxSearchRadius + StandardMobBrain.MAX_AGGRO_DISTANCE + RoamingAllowance + SafetyMargin;

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>A sphere around an aggressive monster (or its spawn point) that a camp must stay out of. Global coordinates.</summary>
    public readonly record struct Hazard(int X, int Y, int Z, int Radius);

    #region Runtime

    /// <summary>
    /// Moves the plan's camp to a safe spot when it is not one (see the class summary). Returns true when the plan
    /// changed, the camp or only its checked flag, so the caller saves the run. Cheap after the first call for a plan,
    /// and safe to call with no player (the realm is the run's). Needs the act's region loaded; without it nothing
    /// changes and the next call tries again.
    /// </summary>
    public static bool EnsureSafeCamp(ActPlan plan, eRealm realm)
    {
        return EnsureSafeCamp(plan, realm, out _);
    }

    /// <summary><see cref="EnsureSafeCamp(ActPlan, eRealm)"/>; <paramref name="campMoved"/> is true when the camp itself moved.</summary>
    public static bool EnsureSafeCamp(ActPlan plan, eRealm realm, out bool campMoved)
    {
        campMoved = false;

        if (plan == null || plan.CampSafetyVersion >= CampSafetyRules)
        {
            return false;
        }

        try
        {
            Region region = WorldMgr.GetRegion((ushort) plan.RegionId);
            Zone zone = region?.Zones?.FirstOrDefault(z => z.ID == plan.ZoneId);

            if (region == null || zone == null)
            {
                if (Log.IsWarnEnabled)
                {
                    Log.Warn($"[EXPEDITION] The act {plan.Act} camp could not be checked: region {plan.RegionId} or zone {plan.ZoneId} is not loaded.");
                }

                return false;
            }

            bool dungeon = region.IsDungeon || zone.IsDungeon;
            bool InActZone(int x, int y) => region.GetZone(x, y)?.ID == plan.ZoneId;

            (int X, int Y, int Z) origin = (plan.CampX, plan.CampY, plan.CampZ);
            ushort heading = plan.CampHeading;
            DbZonePoint entrance = null;

            if (dungeon)
            {
                IList<DbZonePoint> points = DOLDB<DbZonePoint>.SelectObjects(DB.Column("TargetRegion").IsEqualTo((ushort) plan.RegionId));
                entrance = ChooseEntrance(points, plan.RegionId, realm, InActZone);

                if (entrance != null)
                {
                    origin = (entrance.TargetX, entrance.TargetY, entrance.TargetZ);
                    heading = entrance.TargetHeading;
                }
                else
                {
                    if (Log.IsWarnEnabled)
                    {
                        Log.Warn($"[EXPEDITION] No entrance into region {plan.RegionId} was found for the act {plan.Act} camp in {plan.ZoneName}; its own spot is checked.");
                    }
                }
            }

            List<Hazard> hazards = CollectHazards(region, origin, realm);
            List<(int X, int Y, int Z)> candidates = [origin];
            IPathfindingMgr nav = PathfindingProvider.Instance;

            if (nav != null && nav.IsAvailable && nav.HasNavmesh(zone))
            {
                // Dungeon floors are stacked, so a ring point snaps only to the floor at the origin's height.
                float zRange = dungeon ? 256 : 2048;

                foreach ((int x, int y) in RingCandidates(origin.X, origin.Y))
                {
                    Zone ringZone = region.GetZone(x, y);

                    if (ringZone == null || ringZone.ID != plan.ZoneId)
                    {
                        continue;
                    }

                    Vector3? floor = nav.GetClosestPoint(ringZone, new Vector3(x, y, origin.Z), 128, 128, zRange, nav.DefaultFilters);

                    if (floor.HasValue)
                    {
                        candidates.Add(((int) floor.Value.X, (int) floor.Value.Y, (int) floor.Value.Z));
                    }
                }
            }

            (int X, int Y, int Z)? chosen = ChooseNearestSafe(origin, candidates, (x, y, z) => InActZone(x, y) && IsClear(x, y, z, hazards));
            string reason;

            if (chosen != null)
            {
                reason = chosen.Value == origin ? (entrance != null ? "the dungeon entrance" : "already clear") : "the nearest clear spot";
            }
            else if (entrance != null)
            {
                chosen = origin;
                reason = "the dungeon entrance (no clear spot was found near it)";
                if (Log.IsWarnEnabled)
                {
                    Log.Warn($"[EXPEDITION] No spot clear of aggressive monsters near the entrance of {plan.ZoneName}; the act {plan.Act} camp is the entrance itself.");
                }
            }
            else
            {
                // Monsters may move or die later: the check runs again next time.
                if (Log.IsWarnEnabled)
                {
                    Log.Warn($"[EXPEDITION] No spot clear of aggressive monsters within {MaxSearchRadius} of the act {plan.Act} camp in {plan.ZoneName} ({plan.CampX}, {plan.CampY}, {plan.CampZ}); the camp is left as it is.");
                }

                return false;
            }

            (int X, int Y, int Z) spot = chosen.Value;

            if (spot != (plan.CampX, plan.CampY, plan.CampZ))
            {
                // A moved outdoor camp faces its first objective, as ActGenerator's computed camps do.
                if (entrance == null || spot != origin)
                {
                    ActObjective first = plan.Objectives?.FirstOrDefault(o => o != null);

                    if (first != null)
                    {
                        heading = (ushort) (new Point2D(spot.X, spot.Y).GetHeading(first.X, first.Y) % 4096);
                    }
                }

                if (Log.IsInfoEnabled)
                {
                    Log.Info($"[EXPEDITION] The act {plan.Act} camp in {plan.ZoneName} moved from ({plan.CampX}, {plan.CampY}, {plan.CampZ}) to ({spot.X}, {spot.Y}, {spot.Z}): {reason}.");
                }
                MoveCamp(plan, spot.X, spot.Y, spot.Z, heading);
                campMoved = true;
            }

            plan.CampSafetyVersion = CampSafetyRules;

            return true;
        }
        catch (Exception ex)
        {
            if (Log.IsErrorEnabled)
            {
                Log.Error($"[EXPEDITION] The act {plan.Act} camp in {plan.ZoneName} could not be checked.", ex);
            }

            return false;
        }
    }

    /// <summary>
    /// Teleports a run's player to the act camp: makes the camp safe first (saving the run and moving the camp's
    /// Hastener and camp-side Reforger when it moved), then starts the arrival grace. Returns MoveTo's result.
    /// </summary>
    public static bool MoveToCamp(GamePlayer player, ExpeditionRun run, ActPlan plan)
    {
        if (player == null || plan == null)
        {
            return false;
        }

        if (EnsureSafeCamp(plan, player.Realm, out bool campMoved))
        {
            ExpeditionManager.Save(run);
        }

        // They stand at the old camp; the tracker's next tick spawns them again at the new one.
        if (campMoved)
        {
            ExpeditionExchanger.Despawn(player);
        }

        bool changingRegion = player.CurrentRegionID != plan.RegionId;

        if (!player.MoveTo((ushort) plan.RegionId, plan.CampX, plan.CampY, plan.CampZ, plan.CampHeading))
        {
            return false;
        }

        StartArrivalGrace(player);

        // Loading another region can take most of the grace: it is given again once the player stands there.
        if (changingRegion)
        {
            ExpeditionManager.WhenSettled(player, (ushort) plan.RegionId, () => StartArrivalGrace(player));
        }

        return true;
    }

    /// <summary>
    /// The server's invulnerability timer (as at login and on region change): monsters do not aggro or attack, and the
    /// player cannot attack, for <see cref="ArrivalGraceMs"/>. GamePlayer.StartInvulnerabilityTimer does nothing on a
    /// PvE-type server; false then.
    /// </summary>
    public static bool StartArrivalGrace(GamePlayer player)
    {
        if (player == null || player.CurrentRegion == null)
        {
            return false;
        }

        bool started = player.StartInvulnerabilityTimer(ArrivalGraceMs, p =>
        {
            if (p.ObjectState == GameObject.eObjectState.Active && p.Client?.IsPlaying == true)
            {
                p.Out.SendMessage("Your arrival protection has worn off.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            }
        });

        if (started)
        {
            player.Out.SendMessage($"You are protected for {ArrivalGraceMs / 1000} seconds as you arrive at camp.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
        }

        return started;
    }

    /// <summary>Live aggressive monsters and aggressive spawn points around <paramref name="origin"/>.</summary>
    private static List<Hazard> CollectHazards(Region region, (int X, int Y, int Z) origin, eRealm realm)
    {
        var hazards = new List<Hazard>();
        var npcs = new List<GameNPC>();
        region.GetInRadius(new Point3D(origin.X, origin.Y, origin.Z), eGameObjectType.NPC, (ushort) ScanRadius, npcs);

        foreach (GameNPC npc in npcs)
        {
            // Pets, companions and gamebots are not monsters; PEACE covers the camp's own Hastener and Reforger.
            if (npc == null || !npc.IsAlive || npc is GameBot || npc.Brain is IControlledBrain)
            {
                continue;
            }

            if ((npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) != 0 || (realm != eRealm.None && npc.Realm == realm))
            {
                continue;
            }

            if (npc.Brain is not IOldAggressiveBrain brain || brain.AggroLevel <= 0 || brain.AggroRange <= 0)
            {
                continue;
            }

            int radius = Math.Min(brain.AggroRange, StandardMobBrain.MAX_AGGRO_DISTANCE) + Math.Clamp(npc.RoamingRange, 0, RoamingAllowance) + SafetyMargin;
            hazards.Add(new Hazard(npc.X, npc.Y, npc.Z, radius));
        }

        // Spawn points too: a monster that is dead now respawns where it stood.
        DbMob[] rows = region.RegionData?.Mobs ?? [];
        long scanSquared = (long) ScanRadius * ScanRadius;

        foreach (DbMob row in rows)
        {
            if (row == null || DistanceSquared(row.X, row.Y, origin.X, origin.Y) > scanSquared || AutonomousDarknessFallsGoalScope.IsRaidEventId(row.ObjectId))
            {
                continue;
            }

            int range = SpawnAggroRange(row, realm, NpcTemplateMgr.GetTemplateVariants(row.NPCTemplateID).Select(t => ((int) t.AggroLevel, t.AggroRange)));

            if (range > 0)
            {
                hazards.Add(new Hazard(row.X, row.Y, row.Z, range + Math.Clamp(row.RoamingRange, 0, RoamingAllowance) + SafetyMargin));
            }
        }

        return hazards;
    }

    #endregion

    #region Pure rules

    /// <summary>
    /// The aggro range of a Mob row's monster when it is an aggressive monster to <paramref name="realm"/>, else 0.
    /// Follows GameNPC.LoadFromDatabase: -1 (Constants.USE_AUTOVALUES) aggro level means 30 above level 5 for a
    /// realm-less monster (60 for a realm one), -1 range means 500 (taken as the widest automatic range); the NPC's
    /// template, when it has one, decides instead (any aggressive variant counts, with the widest range).
    /// </summary>
    public static int SpawnAggroRange(DbMob row, eRealm realm, IEnumerable<(int AggroLevel, int AggroRange)> templates)
    {
        if (row == null || (row.Flags & (uint) (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) != 0)
        {
            return 0;
        }

        if (realm != eRealm.None && row.Realm == (byte) realm)
        {
            return 0;
        }

        List<(int AggroLevel, int AggroRange)> variants = templates?.ToList() ?? [];
        int level;
        int range;

        if (variants.Count > 0)
        {
            List<(int AggroLevel, int AggroRange)> aggressive = variants.Where(v => v.AggroLevel > 0).ToList();
            level = aggressive.Count;
            range = aggressive.Count == 0 ? 0 : aggressive.Max(v => v.AggroRange);
        }
        else
        {
            level = row.AggroLevel;
            range = row.AggroRange;

            if (level == Constants.USE_AUTOVALUES)
            {
                level = row.Realm != 0 ? 60 : row.Level > 5 ? 30 : 0;
            }
        }

        if (level <= 0)
        {
            return 0;
        }

        if (range <= 0)
        {
            range = 500;
        }

        return Math.Min(range, StandardMobBrain.MAX_AGGRO_DISTANCE);
    }

    /// <summary>True when the point is outside every hazard (3D distance, as aggro is).</summary>
    public static bool IsClear(int x, int y, int z, IReadOnlyList<Hazard> hazards)
    {
        if (hazards == null)
        {
            return true;
        }

        foreach (Hazard hazard in hazards)
        {
            long dx = x - hazard.X;
            long dy = y - hazard.Y;
            long dz = z - hazard.Z;

            if (dx * dx + dy * dy + dz * dz <= (long) hazard.Radius * hazard.Radius)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Points on rings around the origin (<see cref="RingRadii"/>), nearest ring first, at least 8 per ring and one per
    /// 200 units of radius, each ring turned half a step from the last so the rings do not line up.
    /// </summary>
    public static List<(int X, int Y)> RingCandidates(int originX, int originY)
    {
        var points = new List<(int X, int Y)>();

        for (int ring = 0; ring < RingRadii.Length; ring++)
        {
            int radius = RingRadii[ring];
            int count = Math.Max(8, radius / 200);
            double step = 2 * Math.PI / count;
            double start = ring % 2 == 0 ? 0 : step / 2;

            for (int i = 0; i < count; i++)
            {
                double angle = start + i * step;
                points.Add((originX + (int) Math.Round(radius * Math.Cos(angle)), originY + (int) Math.Round(radius * Math.Sin(angle))));
            }
        }

        return points;
    }

    /// <summary>The candidate nearest the origin (3D; the first listed on a tie) that <paramref name="isSafe"/> accepts, or null.</summary>
    public static (int X, int Y, int Z)? ChooseNearestSafe((int X, int Y, int Z) origin, IEnumerable<(int X, int Y, int Z)> candidates, Func<int, int, int, bool> isSafe)
    {
        if (candidates == null || isSafe == null)
        {
            return null;
        }

        foreach ((int X, int Y, int Z) candidate in candidates.OrderBy(c => DistanceSquared(c.X, c.Y, origin.X, origin.Y) + (long) (c.Z - origin.Z) * (c.Z - origin.Z)))
        {
            if (isSafe(candidate.X, candidate.Y, candidate.Z))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The zone point a player of <paramref name="realm"/> enters region <paramref name="regionId"/> by: one from another
    /// region into it, landing inside the act's zone. Darkness Falls's normal home entrance for the realm comes first
    /// (AutonomousDarknessFallsPolicy; Hibernia's is a realm-neutral row), then the realm's own rows, then realm-neutral
    /// ones, then any other; the lowest Id on a tie. Null when there is none.
    /// </summary>
    public static DbZonePoint ChooseEntrance(IEnumerable<DbZonePoint> points, int regionId, eRealm realm, Func<int, int, bool> insideActZone)
    {
        if (points == null)
        {
            return null;
        }

        ushort homeEntrance = regionId == AutonomousDarknessFallsPolicy.RegionId ? AutonomousDarknessFallsPolicy.HomeEntranceZonePointId(realm) : (ushort) 0;

        return points
            .Where(p => p != null && p.TargetRegion == regionId && p.SourceRegion != regionId)
            .Where(p => (p.TargetX != 0 || p.TargetY != 0) && (insideActZone == null || insideActZone(p.TargetX, p.TargetY)))
            .OrderBy(p =>
            {
                if (homeEntrance != 0 && p.Id == homeEntrance && p.SourceRegion == AutonomousDarknessFallsPolicy.HomeRegion(realm))
                {
                    return 0;
                }

                if (realm != eRealm.None && p.Realm == (ushort) realm)
                {
                    return 1;
                }

                return p.Realm == 0 ? 2 : 3;
            })
            .ThenBy(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Moves the camp and what is described from it: elite pack and shrine directions are worked out again, and a
    /// Reforger placed beside the old camp is placed again (beside the new one, or in town) on its next tick.
    /// Shrines, elite packs and objectives keep their own coordinates.
    /// </summary>
    public static void MoveCamp(ActPlan plan, int x, int y, int z, ushort heading)
    {
        if (plan == null)
        {
            return;
        }

        plan.CampX = x;
        plan.CampY = y;
        plan.CampZ = z;
        plan.CampHeading = heading;

        foreach (EliteCamp elite in plan.Elites ?? [])
        {
            if (elite != null)
            {
                elite.Description = $"A {elite.Affix} pack of {elite.MobName} lairs {ActGenerator.DescribeFromCamp(x, y, elite.X, elite.Y)}";
            }
        }

        foreach (ShrineSpec shrine in plan.Shrines ?? [])
        {
            if (shrine != null)
            {
                shrine.Direction = ActGenerator.DescribeFromCamp(x, y, shrine.X, shrine.Y);
            }
        }

        if (plan.ExchangerPlaced && !plan.ExchangerInTown)
        {
            plan.ExchangerPlaced = false;
        }
    }

    private static long DistanceSquared(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;

        return dx * dx + dy * dy;
    }

    #endregion
}
