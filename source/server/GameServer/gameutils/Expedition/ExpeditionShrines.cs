using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.PacketHandler;

namespace DOL.GS.Expedition;

/// <summary>
/// Wild shrines: three per act, out in the zone at monster camps (one beside an elite pack when the act has one). The
/// journal only gives a shrine's direction until the player comes within <see cref="DiscoverRadius"/>; then it shows
/// its /loc. Walking up to it (<see cref="UseRadius"/>) grants its lesser buff group (<see cref="ExpeditionBuffs"/>) for
/// the rest of the act. The shrine objects exist only while the player's tracker is attached.
/// </summary>
public static class ExpeditionShrines
{
    public const int ShrinesPerAct = 3;
    public const int DiscoverRadius = 2000;
    public const int UseRadius = 300;

    /// <summary>A shrine keeps at least this far from the camp, every objective point and the other shrines.</summary>
    public const int MinDistance = 1500;

    /// <summary>The guarded shrine stands this far from its elite pack's camp point.</summary>
    private const int GuardOffset = 250;

    /// <summary>
    /// objects.csv 4170 "crystal_green_prop" (items.csv 2209, Expansion Only 0, so every client draws it): a green
    /// crystal with a real mesh. Alternatives with a mesh and no expansion: 4171 "crystal_red_prop", 4264 "Dell Crystal".
    /// </summary>
    public const ushort ShrineModel = 4170;

    /// <summary>objects.csv 4171 "crystal_red_prop": the act 5 shrine while it is dormant (owner 2026-10-27).</summary>
    public const ushort DormantModel = 4171;

    /// <summary>Act 5: kills of the camp's monster needed to wake its shrine, half the camp's spawns within these bounds.</summary>
    public const int DormantMinKills = 3;
    public const int DormantMaxKills = 6;

    /// <summary>Act 5: a kill counts for a shrine within this distance (2D) of its camp's centre.</summary>
    public const int DormantCampRadius = 1500;

    /// <summary>Act 5: the dormant shrine stands this far from its camp's centre, outside the monsters' reach.</summary>
    public const int DormantMinDistance = 800;
    public const int DormantMaxDistance = 2000;

    private const string DormantWarnedProperty = "expedition.shrine.dormantWarned";
    private const long DormantWarnEveryMs = 10_000;

    private static readonly ConcurrentDictionary<string, List<GameStaticItem>> Spawned = new(StringComparer.Ordinal);

    /// <summary>The kills a camp of this many spawns asks for: half of them, from 3 to 6.</summary>
    public static int DormantKillsFor(int campSpawns)
    {
        return Math.Clamp(campSpawns / 2, DormantMinKills, DormantMaxKills);
    }

    /// <summary>True while the shrine still waits for kills.</summary>
    public static bool IsDormant(ShrineSpec shrine)
    {
        return shrine != null && shrine.KillsNeeded > 0 && shrine.Kills < shrine.KillsNeeded;
    }

    /// <summary>
    /// True when a monster of this name, killed at this point, counts for the shrine: its camp's monster (an elite's
    /// name carries an affix in front, "Swift essence shredder") within <see cref="DormantCampRadius"/> of the camp.
    /// </summary>
    public static bool CountsFor(ShrineSpec shrine, string name, int regionId, int x, int y)
    {
        if (!IsDormant(shrine) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(shrine.CampMob) || regionId != shrine.RegionId)
        {
            return false;
        }

        bool sameMob = string.Equals(name, shrine.CampMob, StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith(" " + shrine.CampMob, StringComparison.OrdinalIgnoreCase);

        return sameMob && Dist2(x, y, shrine.CampX, shrine.CampY) <= (long) DormantCampRadius * DormantCampRadius;
    }

    #region Generation

    /// <summary>
    /// Three shrines, each a different buff group, at zone camps away from the act camp, the objectives and each other;
    /// the first beside an elite pack when there is one. Fewer when the zone has too few camps.
    /// </summary>
    public static List<ShrineSpec> Choose(Random rng, IReadOnlyList<(int X, int Y, int Z)> campsGlobal, (int X, int Y) campPoint,
        IReadOnlyList<ActObjective> objectives, IReadOnlyList<EliteCamp> elites, int regionId, Func<int, int, string> directionFromCamp)
    {
        List<string> groups = ExpeditionBuffs.Groups.ToList();

        for (int i = groups.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (groups[i], groups[j]) = (groups[j], groups[i]);
        }

        var shrines = new List<ShrineSpec>();
        var taken = new List<(int X, int Y)> { campPoint };
        taken.AddRange(objectives.Select(o => (o.X, o.Y)));
        long minSquared = (long) MinDistance * MinDistance;

        bool FarEnough(int x, int y) => taken.All(t => Dist2(t.X, t.Y, x, y) > minSquared);

        // The guarded shrine: beside the first elite pack.
        EliteCamp guard = elites?.FirstOrDefault(e => e != null && FarEnough(e.X + GuardOffset, e.Y));

        if (guard != null)
        {
            Add(guard.X + GuardOffset, guard.Y, guard.Z, true);
        }

        List<(int X, int Y, int Z)> candidates = campsGlobal.Where(c => FarEnough(c.X, c.Y)).ToList();

        while (shrines.Count < ShrinesPerAct && candidates.Count > 0)
        {
            (int x, int y, int z) = candidates[rng.Next(candidates.Count)];
            Add(x, y, z, false);
            candidates = candidates.Where(c => FarEnough(c.X, c.Y)).ToList();
        }

        return shrines;

        void Add(int x, int y, int z, bool guarded)
        {
            shrines.Add(new ShrineSpec
            {
                Group = groups[shrines.Count % groups.Count],
                RegionId = regionId,
                X = x,
                Y = y,
                Z = z,
                Guarded = guarded,
                Direction = directionFromCamp(x, y)
            });
            taken.Add((x, y));
        }
    }

    private static long Dist2(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;

        return dx * dx + dy * dy;
    }

    #endregion

    #region Tracker tick

    /// <summary>
    /// Called by the objective tracker's tick while the player is alive and in the world: puts the shrine objects in
    /// the world, discovers and uses shrines in range, and keeps the run's lesser buffs up.
    /// </summary>
    public static void Tick(GamePlayer player, ExpeditionRun run, ActPlan plan)
    {
        List<ShrineSpec> shrines = plan?.Shrines;
        bool changed = false;
        bool usedNow = false;

        if (shrines != null && shrines.Count > 0)
        {
            if (PlaceDormant(player, plan))
            {
                changed = true;

                // The objects stand where the shrines were; the next line puts them at their new spots.
                Despawn(player);
            }

            EnsureSpawned(player, shrines);

            foreach (ShrineSpec shrine in shrines)
            {
                if (shrine == null || shrine.Used || player.CurrentRegionID != shrine.RegionId)
                {
                    continue;
                }

                long distance2 = Dist2(player.X, player.Y, shrine.X, shrine.Y);
                string loc = ActGenerator.DescribeLoc(shrine.RegionId, shrine.X, shrine.Y);

                if (!shrine.Discovered && distance2 <= (long) DiscoverRadius * DiscoverRadius)
                {
                    shrine.Discovered = true;
                    changed = true;

                    // On screen as well (owner, 2026-10-24): the journal just gained the shrine's /loc.
                    player.Out.SendMessage($"You discover the {ExpeditionBuffs.ShrineName(shrine.Group)}! Your journal shows where it is.", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage(IsDormant(shrine)
                        ? $"You discover the {ExpeditionBuffs.ShrineName(shrine.Group)}{(loc == null ? string.Empty : $" at {loc}")}. It lies dormant: slay {shrine.KillsNeeded - shrine.Kills} {shrine.CampMob} at the camp beside it to wake it."
                        : $"You discover the {ExpeditionBuffs.ShrineName(shrine.Group)}{(loc == null ? string.Empty : $" at {loc}")}. Walk up to it to receive its blessing.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }

                if (distance2 <= (long) UseRadius * UseRadius && IsDormant(shrine))
                {
                    long now = GameLoop.GameLoopTime;

                    if (now - player.TempProperties.GetProperty<long>(DormantWarnedProperty) >= DormantWarnEveryMs)
                    {
                        player.TempProperties.SetProperty(DormantWarnedProperty, now);
                        player.Out.SendMessage($"The {ExpeditionBuffs.ShrineName(shrine.Group)} is dormant. Slay {shrine.KillsNeeded - shrine.Kills} more {shrine.CampMob} at the camp beside it.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                    }

                    continue;
                }

                if (distance2 <= (long) UseRadius * UseRadius)
                {
                    shrine.Discovered = true;
                    shrine.Used = true;
                    changed = true;
                    usedNow = true;
                    run.AddLog($"Act {plan.Act}: blessed at the {ExpeditionBuffs.ShrineName(shrine.Group)}.");
                    player.Out.SendMessage($"The {ExpeditionBuffs.ShrineName(shrine.Group)} blesses you: lesser {ExpeditionBuffs.NameOf(shrine.Group).ToLowerInvariant()} for the rest of the act.", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                    player.Out.SendMessage($"The {ExpeditionBuffs.ShrineName(shrine.Group)} blesses you: lesser {ExpeditionBuffs.NameOf(shrine.Group).ToLowerInvariant()} for the rest of the act.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                }
            }
        }

        ExpeditionBuffs.Maintain(player, run);

        if (changed)
        {
            ExpeditionManager.Save(run);
        }

        if (usedNow)
        {
            // The shrine objects carry their name from when they were spawned; the next tick puts them back with a
            // used shrine shown as "(spent)".
            Despawn(player);
        }
    }

    private static void EnsureSpawned(GamePlayer player, List<ShrineSpec> shrines)
    {
        if (Spawned.TryGetValue(player.ObjectId, out List<GameStaticItem> existing) && existing.All(o => o.ObjectState == GameObject.eObjectState.Active))
        {
            return;
        }

        Despawn(player);

        var objects = new List<GameStaticItem>();

        foreach (ShrineSpec shrine in shrines)
        {
            if (shrine == null)
            {
                continue;
            }

            Region region = WorldMgr.GetRegion((ushort) shrine.RegionId);

            if (region == null)
            {
                continue;
            }

            GameStaticItem item = new()
            {
                CurrentRegion = region,
                X = shrine.X,
                Y = shrine.Y,
                Z = shrine.Z,
                Heading = 0,
                Model = IsDormant(shrine) ? DormantModel : ShrineModel,
                Name = shrine.Used ? $"{ExpeditionBuffs.ShrineName(shrine.Group)} (spent)"
                    : IsDormant(shrine) ? $"{ExpeditionBuffs.ShrineName(shrine.Group)} (dormant {shrine.Kills}/{shrine.KillsNeeded})"
                    : ExpeditionBuffs.ShrineName(shrine.Group)
            };

            if (item.AddToWorld())
            {
                objects.Add(item);
            }
        }

        Spawned[player.ObjectId] = objects;
    }

    /// <summary>
    /// Act 5: gives each shrine not yet placed the world camp nearest its site (its monster and the kills it asks for)
    /// and moves it just outside that camp, out of the monsters' reach (ExpeditionArrival.FindSpotNear). A shrine
    /// already used stays as it was. True when a shrine changed.
    /// </summary>
    private static bool PlaceDormant(GamePlayer player, ActPlan plan)
    {
        if (plan.Act != ActGenerator.ActCount || plan.Shrines.All(shrine => shrine == null || shrine.Placed || shrine.Used))
        {
            return false;
        }

        Region region = player.CurrentRegion;

        if (region == null || region.ID != plan.RegionId)
        {
            return false;
        }

        IReadOnlyList<AutonomousCapnBryGoalCatalog.Entry> camps = WorldCampSource.Instance.GetZoneCamps(plan.RegionId, plan.ZoneId);

        if (camps.Count == 0)
        {
            return false;
        }

        (int ox, int oy) = ActGenerator.WorldZoneOffset(plan.ZoneId);
        bool changed = false;

        foreach (ShrineSpec shrine in plan.Shrines)
        {
            if (shrine == null || shrine.Placed || shrine.Used)
            {
                continue;
            }

            AutonomousCapnBryGoalCatalog.Entry camp = camps.OrderBy(c => Dist2(ox + c.LocalX, oy + c.LocalY, shrine.X, shrine.Y)).First();
            (int X, int Y, int Z) centre = (ox + camp.LocalX, oy + camp.LocalY, camp.Z);
            shrine.CampMob = camp.Name;
            shrine.CampX = centre.X;
            shrine.CampY = centre.Y;
            shrine.KillsNeeded = DormantKillsFor(camp.Sightings);
            shrine.Kills = 0;
            (int X, int Y, int Z)? spot = ExpeditionArrival.FindSpotNear(region, plan.ZoneId, centre, player.Realm, DormantMinDistance, DormantMaxDistance);

            if (spot != null)
            {
                shrine.X = spot.Value.X;
                shrine.Y = spot.Value.Y;
                shrine.Z = spot.Value.Z;
                shrine.Direction = ActGenerator.DescribeFromCamp(plan.CampX, plan.CampY, shrine.X, shrine.Y);
            }

            shrine.Placed = true;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Act 5: a kill the tracker credited to the player. A kill of a dormant shrine's camp monster near its camp counts
    /// toward waking it; the shrine turns green (respawned by the next tick) once it has its kills.
    /// </summary>
    public static void OnKill(GamePlayer player, ExpeditionRun run, ActPlan plan, GameLiving target)
    {
        if (player == null || run == null || plan?.Shrines == null || target == null || plan.Act != ActGenerator.ActCount)
        {
            return;
        }

        bool changed = false;

        foreach (ShrineSpec shrine in plan.Shrines)
        {
            if (!CountsFor(shrine, target.Name, target.CurrentRegionID, target.X, target.Y))
            {
                continue;
            }

            shrine.Kills++;
            changed = true;
            string name = ExpeditionBuffs.ShrineName(shrine.Group);

            if (IsDormant(shrine))
            {
                player.Out.SendMessage($"The {name} stirs ({shrine.Kills}/{shrine.KillsNeeded}).", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

                continue;
            }

            string loc = ActGenerator.DescribeLoc(shrine.RegionId, shrine.X, shrine.Y);
            run.AddLog($"Act {plan.Act}: the {name} awakened.");
            player.Out.SendMessage($"The {name} awakens!", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
            player.Out.SendMessage($"The {name} awakens{(loc == null ? string.Empty : $" at {loc}")}. Walk up to it to receive its blessing.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        if (changed)
        {
            ExpeditionManager.Save(run);

            // Respawned on the next tick with the new name (and the green crystal once awake).
            Despawn(player);
        }
    }

    /// <summary>Takes the player's shrine objects out of the world (the tracker detached: act change, quit, run end).</summary>
    public static void Despawn(GamePlayer player)
    {
        if (player == null || !Spawned.TryRemove(player.ObjectId, out List<GameStaticItem> objects))
        {
            return;
        }

        foreach (GameStaticItem item in objects)
        {
            try
            {
                item.Delete();
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }

    #endregion

    /// <summary>Journal lines: a discovered shrine's /loc and state; an undiscovered one only its direction.</summary>
    public static IReadOnlyList<string> Describe(ActPlan plan)
    {
        var lines = new List<string>();

        foreach (ShrineSpec shrine in plan?.Shrines ?? [])
        {
            if (shrine == null)
            {
                continue;
            }

            string guarded = shrine.Guarded ? " (guarded by an elite pack)" : string.Empty;

            if (!shrine.Discovered)
            {
                lines.Add($"Shrine: something glimmers {shrine.Direction}{guarded}");

                continue;
            }

            string loc = ActGenerator.DescribeLoc(shrine.RegionId, shrine.X, shrine.Y);
            string state = shrine.Used ? "blessing received"
                : IsDormant(shrine) ? $"dormant: slay {shrine.Kills}/{shrine.KillsNeeded} {shrine.CampMob} at the camp beside it"
                : "not yet used";
            lines.Add($"Shrine: {ExpeditionBuffs.ShrineName(shrine.Group)}{(loc == null ? string.Empty : $" ({loc})")} - {state}{guarded}");
        }

        return lines;
    }
}
